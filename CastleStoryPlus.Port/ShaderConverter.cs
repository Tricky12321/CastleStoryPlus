using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace CastleStoryPlus.Port;

// Converts the game's Unity 5.6 shaders to Unity 6's shader format, keeping the compiled programs for the player's
// graphics API: OpenGL Core (Linux) or Direct3D 11 (Windows). Both versions keep a GL program as GLSL source (hlslcc
// output, vertex and fragment in one text) and a Direct3D 11 program as DXBC bytecode, so the programs are taken as
// they are; what changes is how they are packed:
//  - each compiled program ("subprogram") in the blob: a new format version, the vertex channels it reads in Unity 6's
//    order (and for Direct3D which input each channel feeds), and a new header on Direct3D's bytecode;
//  - the blob gets parameter entries: where each uniform, texture and constant buffer of a program lives. 5.6's GL
//    programs list no parameters (the 5.6 engine looked the uniforms up in the linked GLSL), so theirs is the empty
//    one; 5.6's Direct3D programs list theirs after the code, which become the program's own entry;
//  - the parsed form: variants are listed per program as player subprograms with indices into the shader's own keyword
//    list, instead of 5.6's subprograms with their bindings.
// The layout and format numbers come from a shader compiled by the player's own Unity (engine-types.assets).
//
// A player built with more than one graphics API (PlayerBuild.VulkanTestLinux: Vulkan and OpenGL Core) gets each
// shader's programs for every one of them, as Unity 6 writes such a shader itself: one blob per platform (the parts of
// `offsets`, `compressedLengths` and `decompressedLengths`, `stageCounts`, each in the player's platform order), and in
// the parsed form each program's player subprograms of all platforms in one list (GL first, then Vulkan; each one's
// blob and parameter indices point into its own platform's blob). Unity takes the programs of the API it runs on.
//
// Vulkan: 5.6's Linux data already holds SPIR-V (platform 18, SMOL-V compressed) for most of the game's shaders; it is
// taken as it is and repacked like a Direct3D 11 program. The programs 5.6 has no SPIR-V for (the Shader Forge shaders,
// several of the game's own, all but the shadow casters of two more) are made from their OpenGL Core GLSL: rewritten
// for Vulkan (GlslToVulkan), compiled with shaderc and compressed as SMOL-V. A shader whose Vulkan programs cannot all
// be made keeps its GL programs only (VulkanFallbacks).
public sealed class ShaderConverter
{
	public const int GlCorePlatform = 15;
	public const int Direct3D11Platform = 4;
	public const int VulkanPlatform = 18;

	// The GPU program type of a Vulkan program (SPIR-V, vertex and fragment in one).
	private const int SpirvProgramType = 25;

	// Unity 6 lists these keywords first in every shader.
	private static readonly string[] BuiltinKeywords = { "STEREO_INSTANCING_ON", "UNITY_SINGLE_PASS_STEREO", "STEREO_MULTIVIEW_ON", "STEREO_CUBEMAP_RENDER_ON" };

	private static readonly string[] Programs = { "progVertex", "progFragment", "progGeometry", "progHull", "progDomain" };

	// 5.6's Direct3D 11 code starts with a 6-byte header (format 1; texture, constant buffer and sampler counts; two
	// zeros), Unity 6's with a 38-byte one (format 2, the same counts, zeros).
	private const int OldCodeHeader = 6;
	private const int CodeHeader = 38;

	// What a Direct3D 11 Shader Model 5 program requires (Unity 6, from a probe shader with "#pragma target 5.0"); the
	// player's own shader gives the Shader Model 4 value.
	private const long ShaderModel5Requirements = 1101803;

	// A Vulkan program's code starts with a header: flags, then offset and length of each stage's SMOL-V stream (five
	// stages). 5.6's header ends there (44 bytes); Unity 6's goes on to 176 bytes, with a 32-byte value per program at
	// 0x50 (a hash we cannot reproduce; a SHA-256 of the streams is written instead).
	private const int OldVulkanHeader = 0x2c;
	private const int VulkanHashOffset = 0x50;
	// ... and at 0x4C the render targets the fragment stage writes, a bit each (1 for one target; a deferred pass's
	// G-buffer pass 15). Unity writes only those targets.
	private const int VulkanTargetsOffset = 0x4c;

	// 5.6 numbers the generic vertex inputs (Vulkan's attribute locations) from 12, Unity 6 from 13.
	private const int OldFirstAttribute = 12;

	private readonly AssetTypeTemplateField template;
	private readonly int tierCount;
	// The player's platforms the converter makes programs for, in the player's order.
	private readonly List<Format> formats = new List<Format>();
	// The platform being converted.
	private Format format;

	// The shader being converted: its blob entries for the platform being converted, the old entries already converted
	// (old index -> program and parameter entry), the parameter entries by content, its keywords (all platforms').
	private List<byte[]> entries;
	private Dictionary<uint, (uint Program, uint Parameters)> converted;
	private Dictionary<string, uint> parameterEntries;
	private List<string> keywords;
	// The shader's 5.6 OpenGL Core programs (Vulkan: the source for those without SPIR-V), and what became of them.
	private List<byte[]> oldGlEntries;
	private int reused;
	private int compiled;
	private List<string> failures;
	private Shaderc shaderc;
	private string shaderName;
	private readonly string glslOnly = Environment.GetEnvironmentVariable("CSP_VULKAN_GLSL");

	// A line per shader: with more than one platform what each got, otherwise only shaders whose programs were
	// compiled from GLSL (how many, and why any failed).
	public List<string> Report { get; } = new List<string>();

	// What Unity 6 needs to know of a platform, from the player's own shader.
	private sealed class Format
	{
		public uint Platform;
		public uint ProgramVersion;
		public byte[] EmptyParameters;
		public long Requirements;
		// The graphics tier the player lists the platform's subprograms in.
		public int Tier;
		public uint StageCount;
		// Vulkan: the start of Unity 6's program header.
		public byte[] VulkanHeader;
	}

	// One program's player subprograms and their parameter entries, for one platform.
	private sealed class ProgramResult
	{
		public readonly List<AssetTypeValueField> SubPrograms = new List<AssetTypeValueField>();
		public readonly List<uint> Parameters = new List<uint>();
	}

	public ShaderConverter(string engineTypesPath)
	{
		var manager = new AssetsManager();
		AssetsFileInstance types = manager.LoadAssetsFile(engineTypesPath, false);
		TypeTreeType tree = types.file.Metadata.TypeTreeTypes.First(t => t.TypeId == (int)AssetClassID.Shader);
		template = new AssetTypeTemplateField();
		template.FromTypeTree(tree);
		TypeTree = TypeTrees.FromTemplate(template, (int)AssetClassID.Shader);
		TypeHash = tree.TypeHash;

		AssetFileInfo info = types.file.GetAssetsOfType(AssetClassID.Shader).First();
		AssetTypeValueField shader = template.MakeValue(types.file.Reader, info.GetAbsoluteByteOffset(types.file));
		// Every platform the player was built with that the converter knows (a player built with more than one API lists
		// each one's programs).
		List<AssetTypeValueField> platforms = shader["platforms"]["Array"].Children;
		AssetTypeValueField vertex = shader["m_ParsedForm"]["m_SubShaders"]["Array"][0]["m_Passes"]["Array"][0]["progVertex"];
		List<AssetTypeValueField> tiers = vertex["m_PlayerSubPrograms"]["Array"].Children;
		tierCount = tiers.Count;
		for (int platform = 0; platform < platforms.Count; platform++)
		{
			uint id = platforms[platform].AsUInt;
			if (id != GlCorePlatform && id != VulkanPlatform && id != Direct3D11Platform)
			{
				continue;
			}
			// A subprogram of this platform's kind.
			bool OfPlatform(AssetTypeValueField sub)
			{
				return ProgramPlatform(sub["m_GpuProgramType"].AsInt) == id;
			}
			int tier = tiers.FindIndex(t => t["Array"].Children.Any(OfPlatform));
			if (tier < 0)
			{
				continue;
			}
			List<byte[]> typeEntries = Entries(PlatformBlob(shader, platform, true), true);
			int sampleIndex = tiers[tier]["Array"].Children.FindIndex(OfPlatform);
			AssetTypeValueField sample = tiers[tier]["Array"][sampleIndex];
			byte[] sampleProgram = typeEntries[(int)sample["m_BlobIndex"].AsUInt];
			formats.Add(new Format
			{
				Platform = id,
				ProgramVersion = BitConverter.ToUInt32(sampleProgram, 0),
				EmptyParameters = typeEntries[(int)vertex["m_ParameterBlobIndices"]["Array"][tier]["Array"][sampleIndex].AsUInt],
				Requirements = sample["m_ShaderRequirements"].AsLong,
				Tier = tier,
				StageCount = shader["stageCounts"]["Array"][platform].AsUInt,
				VulkanHeader = id == VulkanPlatform ? VulkanHeader(sampleProgram) : null,
			});
		}
		types.file.Close();
		if (formats.Count == 0)
		{
			throw new InvalidOperationException("the player's shaders are for no graphics API the converter knows (" + string.Join(", ", platforms.Select(p => p.AsUInt)) + ")");
		}
	}

	// Unity 6's Shader layout, for the files the converted shaders are written to.
	public TypeTreeType TypeTree { get; }

	public Hash128 TypeHash { get; }

	// The player's shader platforms the shaders get programs for (GlCorePlatform, VulkanPlatform, Direct3D11Platform).
	public IEnumerable<uint> Platforms => formats.Select(f => f.Platform);

	public int Converted { get; private set; }

	public int WithoutPrograms { get; private set; }

	// The shaders left without Vulkan programs (their GL ones only) because some could not be made.
	public SortedSet<string> VulkanFallbacks { get; } = new SortedSet<string>(StringComparer.Ordinal);

	// The graphics APIs every converted shader has programs for, as Unity names them (GraphicsDeviceType).
	public IEnumerable<string> GraphicsApis => formats
		.Where(f => f.Platform != VulkanPlatform || VulkanFallbacks.Count == 0)
		.Select(f => f.Platform == VulkanPlatform ? "Vulkan" : f.Platform == Direct3D11Platform ? "Direct3D11" : "OpenGLCore");

	private uint Platform => format.Platform;

	private bool Direct3D => Platform == Direct3D11Platform;

	private bool Vulkan => Platform == VulkanPlatform;

	// The platform of a GPU program type: GLCore32, GLCore41, GLCore43; DX11VertexSM40 to DX11DomainSM50; SPIR-V.
	private static uint ProgramPlatform(int type)
	{
		if (type >= 6 && type <= 8)
		{
			return GlCorePlatform;
		}
		if (type >= 15 && type <= 22)
		{
			return Direct3D11Platform;
		}
		return type == SpirvProgramType ? VulkanPlatform : uint.MaxValue;
	}

	// The converted shader, from the 5.6 shader's values.
	public AssetTypeValueField Convert(AssetTypeValueField old)
	{
		AssetTypeValueField shader = ValueBuilder.DefaultValueFieldFromTemplate(template);
		FieldCopy.Copy(old, shader);
		AssetTypeValueField form = shader["m_ParsedForm"];
		FixStates(form);
		shaderName = old["m_ParsedForm"]["m_Name"].AsString;
		keywords = new List<string>(BuiltinKeywords);

		// Each program of the shader (sub shaders, passes, Programs), as pairs of old and new.
		var programs = new List<(AssetTypeValueField Old, AssetTypeValueField New, bool Vertex)>();
		List<AssetTypeValueField> oldSubShaders = old["m_ParsedForm"]["m_SubShaders"]["Array"].Children;
		List<AssetTypeValueField> subShaders = form["m_SubShaders"]["Array"].Children;
		for (int s = 0; s < subShaders.Count; s++)
		{
			List<AssetTypeValueField> oldPasses = oldSubShaders[s]["m_Passes"]["Array"].Children;
			List<AssetTypeValueField> passes = subShaders[s]["m_Passes"]["Array"].Children;
			for (int p = 0; p < passes.Count; p++)
			{
				foreach (string name in Programs)
				{
					programs.Add((oldPasses[p][name], passes[p][name], name == "progVertex"));
				}
			}
		}

		// Debug: CSP_SHADER_SKIP=<regex> leaves the programs out of the shaders it matches.
		string skip = Environment.GetEnvironmentVariable("CSP_SHADER_SKIP");
		bool skipped = !string.IsNullOrEmpty(skip) && System.Text.RegularExpressions.Regex.IsMatch(shaderName, skip);
		var done = new List<(Format Format, byte[] Blob, List<ProgramResult> Programs)>();
		var notes = new List<string>();
		bool anyPrograms = false;
		// Vulkan after GL: its programs are checked against the GL ones.
		foreach (Format next in formats.OrderBy(f => f.Platform == VulkanPlatform))
		{
			format = next;
			int platform = old["platforms"]["Array"].Children.FindIndex(p => p.AsUInt == Platform);
			List<byte[]> oldEntries = platform < 0 || skipped ? new List<byte[]>() : Entries(PlatformBlob(old, platform, false), false);
			oldGlEntries = new List<byte[]>();
			if (Vulkan && !skipped)
			{
				int gl = old["platforms"]["Array"].Children.FindIndex(p => p.AsUInt == GlCorePlatform);
				if (gl >= 0)
				{
					oldGlEntries = Entries(PlatformBlob(old, gl, false), false);
				}
			}
			reused = 0;
			compiled = 0;
			failures = new List<string>();
			entries = new List<byte[]> { format.EmptyParameters };
			converted = new Dictionary<uint, (uint, uint)>();
			parameterEntries = new Dictionary<string, uint> { { System.Convert.ToBase64String(format.EmptyParameters), 0 } };
			int keywordCount = keywords.Count;
			var results = new List<ProgramResult>();
			try
			{
				foreach (var program in programs)
				{
					results.Add(ConvertProgram(program.Old, program.New, program.Vertex, oldEntries));
				}
			}
			catch (Exception e) when (Vulkan)
			{
				// Any other trouble with the Vulkan programs (shaderc not loading, a program we cannot read) costs this
				// shader its Vulkan programs, not the install.
				failures.Add(e.GetType().Name + ": " + e.Message.Split('\n')[0].Trim());
			}
			// Vulkan needs a program for every variant the GL programs have, or the shader would draw some of them
			// under GL and none under Vulkan.
			if (Vulkan && failures.Count == 0)
			{
				int gl = done.FindIndex(d => d.Format.Platform == GlCorePlatform);
				for (int i = 0; gl >= 0 && i < programs.Count; i++)
				{
					IEnumerable<string> missing = KeywordSets(done[gl].Programs[i]).Except(KeywordSets(results[i]));
					if (missing.Any())
					{
						failures.Add("no Vulkan program for " + (missing.First().Length > 0 ? missing.First() : "no keywords"));
						break;
					}
				}
			}
			string name = PlatformName(Platform);
			if (Vulkan && failures.Count > 0)
			{
				keywords.RemoveRange(keywordCount, keywords.Count - keywordCount);
				VulkanFallbacks.Add(shaderName);
				notes.Add(name + " failed, GL programs only (" + failures.Count + " failed: " + failures[0] + ")");
				continue;
			}
			int count = results.Sum(r => r.SubPrograms.Count);
			anyPrograms |= count > 0;
			notes.Add(name + " " + (Vulkan && (reused > 0 || compiled > 0) ? reused + " SPIR-V programs reused, " + compiled + " compiled from GLSL" : count + " programs"));
			done.Add((format, Pack(entries), results));
		}
		done = done.OrderBy(d => formats.IndexOf(d.Format)).ToList();

		// The programs: each tier's subprograms of all platforms, in the player's platform order.
		for (int i = 0; i < programs.Count; i++)
		{
			AssetTypeValueField program = programs[i].New;
			SetArray(program["m_SubPrograms"]["Array"], Enumerable.Empty<AssetTypeValueField>());
			AssetTypeValueField tiers = program["m_PlayerSubPrograms"]["Array"];
			AssetTypeValueField blobIndices = program["m_ParameterBlobIndices"]["Array"];
			if (done.All(d => d.Programs[i].SubPrograms.Count == 0))
			{
				SetArray(tiers, Enumerable.Empty<AssetTypeValueField>());
				SetArray(blobIndices, Enumerable.Empty<AssetTypeValueField>());
				continue;
			}
			var tierLists = new List<AssetTypeValueField>();
			var indexLists = new List<AssetTypeValueField>();
			for (int t = 0; t < tierCount; t++)
			{
				AssetTypeValueField list = ValueBuilder.DefaultValueFieldFromArrayTemplate(tiers);
				AssetTypeValueField indices = ValueBuilder.DefaultValueFieldFromArrayTemplate(blobIndices);
				var ofTier = done.Where(d => d.Format.Tier == t).Select(d => d.Programs[i]).ToList();
				SetArray(list["Array"], ofTier.SelectMany(r => r.SubPrograms));
				SetArray(indices["Array"], ofTier.SelectMany(r => r.Parameters).Select(index =>
				{
					AssetTypeValueField item = ValueBuilder.DefaultValueFieldFromArrayTemplate(indices["Array"]);
					item.AsUInt = index;
					return item;
				}));
				tierLists.Add(list);
				indexLists.Add(indices);
			}
			SetArray(tiers, tierLists);
			SetArray(blobIndices, indexLists);
		}

		AssetTypeValueField keywordNames = form["m_KeywordNames"]["Array"];
		SetArray(keywordNames, keywords.Select(k =>
		{
			AssetTypeValueField item = ValueBuilder.DefaultValueFieldFromArrayTemplate(keywordNames);
			item.AsString = k;
			return item;
		}));
		form["m_KeywordFlags"]["Array"].AsByteArray = new byte[keywords.Count];
		foreach (AssetTypeValueField subShader in subShaders)
		{
			foreach (AssetTypeValueField pass in subShader["m_Passes"]["Array"].Children)
			{
				foreach (string name in Programs.Take(2))
				{
					SetUShorts(pass[name]["m_SerializedKeywordStateMask"]["Array"], Enumerable.Range(0, keywords.Count).Select(i => (ushort)i));
				}
			}
		}

		WriteBlobs(shader, done.Select(d => (d.Format, d.Blob)).ToList());
		if (formats.Count > 1 || notes.Any(n => n.Contains("GLSL") || n.Contains("failed")))
		{
			Report.Add(shaderName + ": " + string.Join("; ", notes));
		}
		if (anyPrograms)
		{
			Converted++;
		}
		else
		{
			WithoutPrograms++;
		}
		return shader;
	}

	private static string PlatformName(uint platform)
	{
		return platform == VulkanPlatform ? "Vulkan" : platform == Direct3D11Platform ? "Direct3D 11" : "GL";
	}

	// The keyword sets of a program's subprograms.
	private IEnumerable<string> KeywordSets(ProgramResult result)
	{
		return result.SubPrograms.Select(sub => string.Join(" ", sub["m_KeywordIndices"]["Array"].Children.Select(k => keywords[k.AsUShort]).OrderBy(k => k, StringComparer.Ordinal))).Distinct();
	}

	// One program's subprograms for the platform being converted (program: the new program, for the field layout).
	private ProgramResult ConvertProgram(AssetTypeValueField old, AssetTypeValueField program, bool vertex, List<byte[]> oldEntries)
	{
		var result = new ProgramResult();
		AssetTypeValueField tiers = program["m_PlayerSubPrograms"]["Array"];
		if (!vertex && !Direct3D)
		{
			// Unity 6 keeps a GL (and Vulkan) shader's whole program with the vertex stage.
			return result;
		}
		List<AssetTypeValueField> subPrograms = result.SubPrograms;
		List<uint> parameterIndices = result.Parameters;
		// Vulkan: the keyword sets 5.6 has SPIR-V for; the GL programs of the others are compiled.
		var withSpirv = new HashSet<string>();
		// Vulkan: the render targets each keyword set's program writes, from the GL program of the same variant (5.6's
		// SPIR-V header does not say).
		var targets = new Dictionary<string, uint>();
		if (Vulkan)
		{
			foreach (AssetTypeValueField sub in old["m_SubPrograms"]["Array"].Children)
			{
				int glType = sub["m_GpuProgramType"].AsInt;
				uint glIndex = sub["m_BlobIndex"].AsUInt;
				if (glType >= 6 && glType <= 8 && glIndex < oldGlEntries.Count)
				{
					byte[] gl = oldGlEntries[(int)glIndex];
					int glPosition = ReadSubProgramKeywords(gl, out _);
					string text = Encoding.UTF8.GetString(gl, glPosition + 4, BitConverter.ToInt32(gl, glPosition));
					targets[KeywordSet(gl)] = GlslToVulkan.Targets(text);
				}
			}
		}
		// Debug: CSP_VULKAN_GLSL=<regex> compiles the GL programs of the shaders it matches even where 5.6 has SPIR-V.
		bool allFromGlsl = Vulkan && !string.IsNullOrEmpty(glslOnly) && System.Text.RegularExpressions.Regex.IsMatch(shaderName, glslOnly);
		if (Vulkan && !allFromGlsl)
		{
			foreach (AssetTypeValueField sub in old["m_SubPrograms"]["Array"].Children)
			{
				uint index = sub["m_BlobIndex"].AsUInt;
				if (sub["m_GpuProgramType"].AsInt == SpirvProgramType && index < oldEntries.Count && !IsEmptyVulkanProgram(oldEntries[(int)index]))
				{
					withSpirv.Add(KeywordSet(oldEntries[(int)index]));
				}
			}
		}
		foreach (AssetTypeValueField sub in old["m_SubPrograms"]["Array"].Children)
		{
			int type = sub["m_GpuProgramType"].AsInt;
			uint index = sub["m_BlobIndex"].AsUInt;
			List<byte[]> source = oldEntries;
			// GLCore32, GLCore41, GLCore43; DX11VertexSM40 to DX11DomainSM50; SPIR-V.
			bool wanted = Vulkan ? type == SpirvProgramType && !allFromGlsl : Direct3D ? type >= 15 && type <= 22 : type >= 6 && type <= 8;
			bool fromGlsl = Vulkan && type >= 6 && type <= 8 && index < oldGlEntries.Count && !withSpirv.Contains(KeywordSet(oldGlEntries[(int)index]));
			if (fromGlsl)
			{
				source = oldGlEntries;
			}
			else if (!wanted || index >= oldEntries.Count)
			{
				continue;
			}
			// GL programs are another blob's entries.
			uint key = fromGlsl ? index | 0x80000000 : index;
			if (!converted.TryGetValue(key, out (uint Program, uint Parameters) blob))
			{
				if (fromGlsl)
				{
					byte[] code = CompileGlSubProgram(oldGlEntries[(int)index], out byte[] parameters);
					if (code == null)
					{
						continue;
					}
					blob = (Add(code), AddParameters(parameters));
					compiled++;
				}
				else if (Vulkan)
				{
					uint written = targets.TryGetValue(KeywordSet(oldEntries[(int)index]), out uint mask) ? mask : 1;
					byte[] code = ConvertVulkanSubProgram(oldEntries[(int)index], written, out byte[] parameters);
					if (code == null)
					{
						// 5.6 lists empty Vulkan programs too (without stages).
						continue;
					}
					blob = (Add(code), AddParameters(parameters));
					reused++;
				}
				else if (Direct3D)
				{
					byte[] code = ConvertDirect3DSubProgram(oldEntries[(int)index], out byte[] parameters);
					blob = (Add(code), AddParameters(parameters));
				}
				else
				{
					blob = (Add(ConvertSubProgram(oldEntries[(int)index])), 0);
				}
				converted[key] = blob;
			}
			ReadSubProgramKeywords(source[(int)index], out List<string> names);
			AssetTypeValueField player = ValueBuilder.DefaultValueFieldFromArrayTemplate(TierTemplate(tiers));
			player["m_BlobIndex"].AsUInt = blob.Program;
			SetUShorts(player["m_KeywordIndices"]["Array"], names.Select(n => (ushort)KeywordIndex(n)));
			// DX11VertexSM50, DX11PixelSM50 and the geometry, hull and domain programs.
			bool shaderModel5 = Direct3D && (type == 16 || type >= 18);
			player["m_ShaderRequirements"].AsLong = shaderModel5 ? ShaderModel5Requirements : format.Requirements;
			player["m_GpuProgramType"].AsInt = fromGlsl ? SpirvProgramType : type;
			subPrograms.Add(player);
			parameterIndices.Add(blob.Parameters);
		}
		return result;
	}

	private uint Add(byte[] entry)
	{
		entries.Add(entry);
		return (uint)(entries.Count - 1);
	}

	// Programs with the same parameters share their entry.
	private uint AddParameters(byte[] parameters)
	{
		string key = System.Convert.ToBase64String(parameters);
		if (!parameterEntries.TryGetValue(key, out uint index))
		{
			index = Add(parameters);
			parameterEntries[key] = index;
		}
		return index;
	}

	private static AssetTypeValueField TierTemplate(AssetTypeValueField tiers)
	{
		return ValueBuilder.DefaultValueFieldFromArrayTemplate(tiers)["Array"];
	}

	private int KeywordIndex(string name)
	{
		int index = keywords.IndexOf(name);
		if (index < 0)
		{
			keywords.Add(name);
			index = keywords.Count - 1;
		}
		return index;
	}

	// 5.6 and Unity 6 lay a subprogram out the same way up to its code: format version, program type, four statistics,
	// keywords, code. After the code 5.6 has the vertex channels the program reads and six more values; Unity 6 has the
	// channels (in its own order) and the inputs they feed, none for GL.
	private byte[] ConvertSubProgram(byte[] old)
	{
		int position = ReadSubProgramKeywords(old, out _);
		int codeLength = BitConverter.ToInt32(old, position);
		int codeEnd = Align(position + 4 + codeLength);
		uint channels = BitConverter.ToUInt32(old, codeEnd);

		var result = new MemoryStream();
		var writer = new BinaryWriter(result);
		writer.Write(format.ProgramVersion);
		writer.Write(old, 4, codeEnd - 4);
		writer.Write(VertexChannels(channels));
		writer.Write(0);
		return result.ToArray();
	}

	// A Direct3D 11 subprogram: after the code 5.6 has the vertex channels, the inputs they feed, and the parameters
	// (groups of uniforms: one without a constant buffer, then one per constant buffer; then the textures, buffers and
	// constant buffers with their slots). The parameters go to their own entry.
	private byte[] ConvertDirect3DSubProgram(byte[] old, out byte[] parameters)
	{
		int position = ReadSubProgramKeywords(old, out _);
		int codeLength = BitConverter.ToInt32(old, position);
		int codeStart = position + 4;
		var reader = new BinaryReader(new MemoryStream(old));
		reader.BaseStream.Position = Align(codeStart + codeLength);

		var result = new MemoryStream();
		var writer = new BinaryWriter(result);
		writer.Write(format.ProgramVersion);
		writer.Write(old, 4, position - 4);
		var code = new byte[codeLength - OldCodeHeader + CodeHeader];
		code[0] = 2;
		Buffer.BlockCopy(old, codeStart + 1, code, 1, 3);
		Buffer.BlockCopy(old, codeStart + OldCodeHeader, code, CodeHeader, codeLength - OldCodeHeader);
		writer.Write(code.Length);
		writer.Write(code);
		Pad(writer);

		uint channels = reader.ReadUInt32();
		int bindCount = reader.ReadInt32();
		writer.Write(VertexChannels(channels));
		writer.Write(bindCount);
		for (int i = 0; i < bindCount; i++)
		{
			int channel = VertexChannel(reader.ReadInt32());
			reader.ReadInt32();
			writer.Write(channel);
			writer.Write(VertexInput(channel));
		}
		parameters = ConvertParameters(reader);
		return result.ToArray();
	}

	// A Vulkan subprogram: the same as a Direct3D 11 one after the code (vertex channels, the inputs they feed,
	// parameters); the code is 5.6's SMOL-V streams under Unity 6's longer header. Null for an empty program.
	private byte[] ConvertVulkanSubProgram(byte[] old, uint written, out byte[] parameters)
	{
		parameters = null;
		int position = ReadSubProgramKeywords(old, out _);
		int codeLength = BitConverter.ToInt32(old, position);
		int codeStart = position + 4;
		if (codeLength <= OldVulkanHeader)
		{
			return null;
		}
		var reader = new BinaryReader(new MemoryStream(old));
		reader.BaseStream.Position = Align(codeStart + codeLength);

		// Unity 6's header (flags and the values after the stages from the player's own program), the stages moved by
		// the longer header, the streams as they are.
		int shift = format.VulkanHeader.Length - OldVulkanHeader;
		var code = new byte[codeLength + shift];
		Buffer.BlockCopy(format.VulkanHeader, 0, code, 0, format.VulkanHeader.Length);
		for (int stage = 0; stage < 5; stage++)
		{
			uint offset = BitConverter.ToUInt32(old, codeStart + 4 + stage * 8);
			uint length = BitConverter.ToUInt32(old, codeStart + 8 + stage * 8);
			WriteUInt(code, 4 + stage * 8, length == 0 ? 0 : offset + (uint)shift);
			WriteUInt(code, 8 + stage * 8, length);
		}
		Buffer.BlockCopy(old, codeStart + OldVulkanHeader, code, format.VulkanHeader.Length, codeLength - OldVulkanHeader);
		WriteUInt(code, VulkanTargetsOffset, written);
		using (var sha = System.Security.Cryptography.SHA256.Create())
		{
			byte[] hash = sha.ComputeHash(old, codeStart + OldVulkanHeader, codeLength - OldVulkanHeader);
			Buffer.BlockCopy(hash, 0, code, VulkanHashOffset, hash.Length);
		}

		var result = new MemoryStream();
		var writer = new BinaryWriter(result);
		writer.Write(format.ProgramVersion);
		writer.Write(old, 4, position - 4);
		writer.Write(code.Length);
		writer.Write(code);
		Pad(writer);

		uint channels = reader.ReadUInt32();
		int bindCount = reader.ReadInt32();
		writer.Write(VertexChannels(channels));
		writer.Write(bindCount);
		for (int i = 0; i < bindCount; i++)
		{
			int channel = VertexChannel(reader.ReadInt32());
			int input = reader.ReadInt32();
			writer.Write(channel);
			writer.Write(input >= OldFirstAttribute ? input + 1 : input);
		}
		parameters = ConvertParameters(reader);
		return result.ToArray();
	}

	private static bool IsEmptyVulkanProgram(byte[] program)
	{
		int position = ReadSubProgramKeywords(program, out _);
		return BitConverter.ToInt32(program, position) <= OldVulkanHeader;
	}

	private static string KeywordSet(byte[] program)
	{
		ReadSubProgramKeywords(program, out List<string> names);
		return string.Join(" ", names.OrderBy(n => n, StringComparer.Ordinal));
	}

	// A Vulkan subprogram made from a 5.6 OpenGL Core one: its GLSL rewritten (GlslToVulkan), each stage compiled and
	// SMOL-V compressed, under Unity 6's header; the vertex inputs and parameters from the rewrite. Null (and the reason
	// noted) when it fails.
	private byte[] CompileGlSubProgram(byte[] old, out byte[] parameters)
	{
		parameters = null;
		int position = ReadSubProgramKeywords(old, out List<string> names);
		int codeLength = BitConverter.ToInt32(old, position);
		string glsl = Encoding.UTF8.GetString(old, position + 4, codeLength).TrimEnd('\0');
		byte[] vertex;
		byte[] fragment;
		GlslToVulkan program;
		try
		{
			program = new GlslToVulkan(glsl);
			if (shaderc == null)
			{
				shaderc = new Shaderc();
			}
			string name = string.Join(" ", names);
			vertex = SmolV.Encode(shaderc.Compile(program.Vertex, Shaderc.Stage.Vertex, "vertex " + name));
			fragment = SmolV.Encode(shaderc.Compile(program.Fragment, Shaderc.Stage.Fragment, "fragment " + name));
		}
		catch (Exception e) when (e is InvalidDataException || e is NotSupportedException)
		{
			failures.Add((names.Count > 0 ? string.Join(" ", names) : "no keywords") + ": " + e.Message.Split('\n')[0].Trim());
			return null;
		}

		// The header's first two stages are the vertex and fragment streams; the fragment one is stored first.
		var code = new byte[format.VulkanHeader.Length + fragment.Length + vertex.Length];
		Buffer.BlockCopy(format.VulkanHeader, 0, code, 0, format.VulkanHeader.Length);
		for (int i = 4; i < OldVulkanHeader; i++)
		{
			code[i] = 0;
		}
		WriteUInt(code, 4, (uint)(format.VulkanHeader.Length + fragment.Length));
		WriteUInt(code, 8, (uint)vertex.Length);
		WriteUInt(code, 12, (uint)format.VulkanHeader.Length);
		WriteUInt(code, 16, (uint)fragment.Length);
		WriteUInt(code, VulkanTargetsOffset, GlslToVulkan.Targets(glsl));
		Buffer.BlockCopy(fragment, 0, code, format.VulkanHeader.Length, fragment.Length);
		Buffer.BlockCopy(vertex, 0, code, format.VulkanHeader.Length + fragment.Length, vertex.Length);
		using (var sha = System.Security.Cryptography.SHA256.Create())
		{
			byte[] hash = sha.ComputeHash(code, format.VulkanHeader.Length, code.Length - format.VulkanHeader.Length);
			Buffer.BlockCopy(hash, 0, code, VulkanHashOffset, hash.Length);
		}

		var result = new MemoryStream();
		var writer = new BinaryWriter(result);
		writer.Write(format.ProgramVersion);
		writer.Write(SpirvProgramType);
		writer.Write(old, 8, position - 8);
		writer.Write(code.Length);
		writer.Write(code);
		Pad(writer);
		writer.Write(program.Channels);
		writer.Write(program.Inputs.Count);
		foreach ((int channel, int input) in program.Inputs)
		{
			writer.Write(channel);
			writer.Write(input);
		}
		parameters = ConvertParameters(new BinaryReader(new MemoryStream(program.Parameters)));
		return result.ToArray();
	}

	// Unity 6's Vulkan program header, from the player's own program: up to its first stage.
	private static byte[] VulkanHeader(byte[] program)
	{
		int position = ReadSubProgramKeywords(program, out _);
		int codeStart = position + 4;
		uint first = uint.MaxValue;
		for (int stage = 0; stage < 5; stage++)
		{
			if (BitConverter.ToUInt32(program, codeStart + 8 + stage * 8) != 0)
			{
				first = Math.Min(first, BitConverter.ToUInt32(program, codeStart + 4 + stage * 8));
			}
		}
		var header = new byte[first];
		Buffer.BlockCopy(program, codeStart, header, 0, header.Length);
		return header;
	}

	private static void WriteUInt(byte[] data, int position, uint value)
	{
		byte[] bytes = BitConverter.GetBytes(value);
		Buffer.BlockCopy(bytes, 0, data, position, 4);
	}

	// 5.6: groups (name, size, uniforms: name, type, rows, columns, is matrix, array size, offset), then bindings (name,
	// kind, slot, extra: for a texture its sampler slot << 8 | dimension). Unity 6: format version, the same groups with
	// vectors (name, columns | type << 16, array size, offset), matrices (name, rows | type << 16, array size, offset)
	// and structs apart, then five lists: textures (name, slot, sampler slot, dimension << 1), one the game does not
	// use, constant buffers (name, slot), buffers (name, slot, array size, resource type), one more the game does not
	// use; each slot with its encoding (0 here).
	private byte[] ConvertParameters(BinaryReader reader)
	{
		var result = new MemoryStream();
		var writer = new BinaryWriter(result);
		writer.Write(format.ProgramVersion);
		int groups = reader.ReadInt32();
		writer.Write(groups);
		for (int g = 0; g < groups; g++)
		{
			WriteString(writer, ReadString(reader));
			writer.Write(reader.ReadInt32());
			int count = reader.ReadInt32();
			var vectors = new List<(string Name, int Size, int ArraySize, int Offset)>();
			var matrices = new List<(string Name, int Size, int ArraySize, int Offset)>();
			for (int i = 0; i < count; i++)
			{
				string name = ReadString(reader);
				int type = reader.ReadInt32();
				int rows = reader.ReadInt32();
				int columns = reader.ReadInt32();
				bool matrix = reader.ReadInt32() != 0;
				int arraySize = reader.ReadInt32();
				int offset = reader.ReadInt32();
				if (matrix)
				{
					matrices.Add((name, rows | type << 16, arraySize, offset));
				}
				else
				{
					vectors.Add((name, columns | type << 16, arraySize, offset));
				}
			}
			foreach (var list in new[] { vectors, matrices })
			{
				writer.Write(list.Count);
				foreach (var parameter in list)
				{
					WriteString(writer, parameter.Name);
					writer.Write(parameter.Size);
					writer.Write(parameter.ArraySize);
					writer.Write(parameter.Offset);
				}
			}
			// Structs.
			writer.Write(0);
		}

		var textures = new List<(string Name, int Slot, int Sampler, int Dimension)>();
		var buffers = new List<(string Name, int Slot)>();
		var constantBuffers = new List<(string Name, int Slot)>();
		int bindings = reader.ReadInt32();
		for (int i = 0; i < bindings; i++)
		{
			string name = ReadString(reader);
			int kind = reader.ReadInt32();
			int slot = reader.ReadInt32();
			int extra = reader.ReadInt32();
			switch (kind)
			{
				case 0:
					textures.Add((name, slot, extra >> 8, extra & 0xff));
					break;
				case 1:
					constantBuffers.Add((name, slot));
					break;
				case 2:
					buffers.Add((name, slot));
					break;
			}
		}
		writer.Write(textures.Count);
		foreach (var texture in textures)
		{
			WriteString(writer, texture.Name);
			writer.Write(0);
			writer.Write(texture.Slot);
			writer.Write(0);
			// Vulkan: the texture and its sampler are one binding, and Unity 6 lists no sampler slot (-1).
			writer.Write(Vulkan ? -1 : texture.Sampler);
			writer.Write(texture.Dimension << 1);
		}
		// (Not used by the game's programs.)
		writer.Write(0);
		writer.Write(constantBuffers.Count);
		foreach (var binding in constantBuffers)
		{
			WriteString(writer, binding.Name);
			writer.Write(0);
			writer.Write(binding.Slot);
		}
		// Buffers: also their array size and resource type (1, a structured buffer).
		writer.Write(buffers.Count);
		foreach (var binding in buffers)
		{
			WriteString(writer, binding.Name);
			writer.Write(0);
			writer.Write(binding.Slot);
			writer.Write(1);
			writer.Write(1);
		}
		// (Not used by the game's programs.)
		writer.Write(0);
		return result.ToArray();
	}

	private static string ReadString(BinaryReader reader)
	{
		int length = reader.ReadInt32();
		string value = Encoding.UTF8.GetString(reader.ReadBytes(length));
		reader.BaseStream.Position = Align((int)reader.BaseStream.Position);
		return value;
	}

	private static void WriteString(BinaryWriter writer, string value)
	{
		byte[] bytes = Encoding.UTF8.GetBytes(value);
		writer.Write(bytes.Length);
		writer.Write(bytes);
		Pad(writer);
	}

	private static void Pad(BinaryWriter writer)
	{
		while (writer.BaseStream.Position % 4 != 0)
		{
			writer.Write((byte)0);
		}
	}

	// The subprogram's keywords; returns where its code length is.
	private static int ReadSubProgramKeywords(byte[] data, out List<string> keywords)
	{
		int position = 24;
		int count = BitConverter.ToInt32(data, position);
		position += 4;
		keywords = new List<string>(count);
		for (int i = 0; i < count; i++)
		{
			int length = BitConverter.ToInt32(data, position);
			keywords.Add(Encoding.UTF8.GetString(data, position + 4, length));
			position = Align(position + 4 + length);
		}
		return position;
	}

	// 5.6's vertex channels: position, normal, color, uv0-uv3, tangent. Unity 6's: position, normal, tangent, color,
	// uv0-uv7.
	private static readonly int[] ChannelMap = { 0, 1, 3, 4, 5, 6, 7, 2 };

	private static uint VertexChannels(uint old)
	{
		uint result = 0;
		for (int bit = 0; bit < ChannelMap.Length; bit++)
		{
			if ((old & (1u << bit)) != 0)
			{
				result |= 1u << ChannelMap[bit];
			}
		}
		return result;
	}

	private static int VertexChannel(int old)
	{
		return old >= 0 && old < ChannelMap.Length ? ChannelMap[old] : old;
	}

	// The vertex input a Unity 6 channel feeds: position, normal, tangent and color their own, uvN TEXCOORDN (input 4 is
	// unused).
	private static int VertexInput(int channel)
	{
		return channel < 4 ? channel : channel + 1;
	}

	private static int Align(int position)
	{
		return (position + 3) & ~3;
	}

	// Render state values Unity 6 added: depth clip on (5.6 always clipped); a value not driven by a property is named
	// "<noninit>".
	private static void FixStates(AssetTypeValueField form)
	{
		foreach (AssetTypeValueField subShader in form["m_SubShaders"]["Array"].Children)
		{
			foreach (AssetTypeValueField pass in subShader["m_Passes"]["Array"].Children)
			{
				AssetTypeValueField state = pass["m_State"];
				FixValueNames(state);
				if (!state["zClip"].IsDummy)
				{
					state["zClip"]["val"].AsFloat = 1;
				}
			}
		}
	}

	private static void FixValueNames(AssetTypeValueField field)
	{
		if ((field.TypeName == "SerializedShaderFloatValue" || field.TypeName == "SerializedShaderVectorValue") && field["name"].AsString.Length == 0)
		{
			field["name"].AsString = "<noninit>";
		}
		if (field.Value != null)
		{
			return;
		}
		foreach (AssetTypeValueField child in field.Children)
		{
			FixValueNames(child);
		}
	}

	// A platform's blob: 5.6 has one part per platform, Unity 6 a list of parts (one here).
	private static byte[] PlatformBlob(AssetTypeValueField shader, int platform, bool parts)
	{
		AssetTypeValueField offset = shader["offsets"]["Array"][platform];
		AssetTypeValueField compressed = shader["compressedLengths"]["Array"][platform];
		AssetTypeValueField decompressed = shader["decompressedLengths"]["Array"][platform];
		if (parts)
		{
			offset = offset["Array"][0];
			compressed = compressed["Array"][0];
			decompressed = decompressed["Array"][0];
		}
		return LZ4ps.LZ4Codec.Decode32(shader["compressedBlob"]["Array"].AsByteArray, (int)offset.AsUInt, (int)compressed.AsUInt, (int)decompressed.AsUInt);
	}

	// The entries of a platform's blob: 5.6 lists offset and length of each, Unity 6 also a segment.
	private static List<byte[]> Entries(byte[] blob, bool segments)
	{
		int count = BitConverter.ToInt32(blob, 0);
		int stride = segments ? 12 : 8;
		var result = new List<byte[]>(count);
		for (int i = 0; i < count; i++)
		{
			int offset = BitConverter.ToInt32(blob, 4 + i * stride);
			int length = BitConverter.ToInt32(blob, 8 + i * stride);
			var entry = new byte[length];
			Buffer.BlockCopy(blob, offset, entry, 0, length);
			result.Add(entry);
		}
		return result;
	}

	private static byte[] Pack(List<byte[]> entries)
	{
		var result = new MemoryStream();
		var writer = new BinaryWriter(result);
		writer.Write(entries.Count);
		int offset = 4 + entries.Count * 12;
		foreach (byte[] entry in entries)
		{
			writer.Write(offset);
			writer.Write(entry.Length);
			writer.Write(0);
			offset += entry.Length;
		}
		foreach (byte[] entry in entries)
		{
			writer.Write(entry);
		}
		return result.ToArray();
	}

	// The platforms' blobs, compressed one after the other, each one part.
	private static void WriteBlobs(AssetTypeValueField shader, List<(Format Format, byte[] Blob)> blobs)
	{
		var all = new MemoryStream();
		var offsets = new List<uint>();
		var compressedLengths = new List<uint>();
		foreach ((Format _, byte[] blob) in blobs)
		{
			byte[] compressed = LZ4ps.LZ4Codec.Encode32HC(blob, 0, blob.Length);
			offsets.Add((uint)all.Length);
			compressedLengths.Add((uint)compressed.Length);
			all.Write(compressed, 0, compressed.Length);
		}
		SetUInts(shader["platforms"]["Array"], blobs.Select(b => b.Format.Platform));
		SetParts(shader["offsets"]["Array"], offsets);
		SetParts(shader["compressedLengths"]["Array"], compressedLengths);
		SetParts(shader["decompressedLengths"]["Array"], blobs.Select(b => (uint)b.Blob.Length));
		shader["compressedBlob"]["Array"].AsByteArray = all.ToArray();
		SetUInts(shader["stageCounts"]["Array"], blobs.Select(b => b.Format.StageCount));
		shader["m_ShaderIsBaked"].AsBool = true;
	}

	// A list of one-part values, one per platform.
	private static void SetParts(AssetTypeValueField array, IEnumerable<uint> values)
	{
		SetArray(array, values.Select(value =>
		{
			AssetTypeValueField parts = ValueBuilder.DefaultValueFieldFromArrayTemplate(array);
			SetUInts(parts["Array"], new[] { value });
			return parts;
		}).ToList());
	}

	private static void SetUInts(AssetTypeValueField array, IEnumerable<uint> values)
	{
		SetArray(array, values.Select(v =>
		{
			AssetTypeValueField item = ValueBuilder.DefaultValueFieldFromArrayTemplate(array);
			item.AsUInt = v;
			return item;
		}));
	}

	private static void SetUShorts(AssetTypeValueField array, IEnumerable<ushort> values)
	{
		SetArray(array, values.Select(v =>
		{
			AssetTypeValueField item = ValueBuilder.DefaultValueFieldFromArrayTemplate(array);
			item.AsUShort = v;
			return item;
		}));
	}

	private static void SetArray(AssetTypeValueField array, IEnumerable<AssetTypeValueField> items)
	{
		array.Children = items.ToList();
		array.AsArray = new AssetTypeArrayInfo(array.Children.Count);
	}
}
