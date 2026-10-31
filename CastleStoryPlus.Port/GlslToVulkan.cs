using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CastleStoryPlus.Port;

// Rewrites a 5.6 OpenGL Core program (hlslcc's GLSL 150, vertex and fragment in one text) into Vulkan GLSL 450 the way
// Unity's own Vulkan programs are laid out, for the game's shaders that 5.6 has no SPIR-V for:
//  - the loose uniforms of each stage go into a std140 block at explicit offsets: the fragment stage's "PGlobals" at
//    set 1 binding 0, the vertex stage's "VGlobals" after it (as in Unity's programs);
//  - samplers get set 0 bindings, the fragment stage's first, in the order they are declared;
//  - vertex inputs get locations in the order they are declared (Unity feeds generic attribute 13 + location), the
//    varyings locations in the vertex stage's order.
// The parameters (what Unity sets where) are written in 5.6's Vulkan layout, which ShaderConverter turns into Unity 6's.
//
// Clip space: the GL program was compiled for OpenGL's conventions (depth -w..w, near at -w), Unity's Vulkan renders
// Direct3D's with reversed depth (w at near, 0 at far), and gives the shader matrices for that. So the vertex stage gets
// unity_MatrixVP turned back into OpenGL's (z = w - 2z), which keeps everything the GL code derives from clip space
// right (fog, the shadow caster's clamp to the near plane). The position itself comes from a first run of the code with
// Unity's matrix as it is (turning the GL one back loses too much of z), the shadow caster's bias and clamp then done
// the Vulkan way, and its y is flipped (Vulkan's clip space points down; without it the image is upside down and the
// culling inverted, so the inside of the terrain showed). The shadow caster's depth bias, which the engine gives with
// Vulkan's sign and scale, becomes -2 times that for the GL run.
public sealed class GlslToVulkan
{
	public const int UniformSet = 1;
	public const int TextureSet = 0;

	// Unity numbers the generic vertex attributes from 13 (location 0).
	private const int FirstAttribute = 13;

	private const string VertexStageMarker = "#ifdef VERTEX";
	private const string FragmentStageMarker = "#ifdef FRAGMENT";
	private const string Prefix = "csp_vk_";
	private const string MatrixVP = "hlslcc_mtx4x4unity_MatrixVP";
	private const string ShadowBias = "unity_LightShadowBias";
	private const string MatrixPrefix = "hlslcc_mtx4x4";
	// How much nearer the forward passes' depth is (reversed depth: a larger z; 0.01 % of the distance).
	private const string DepthNearer = "1.0001";

	private static readonly Regex UniformLine = new Regex(@"^\s*uniform\s+(\w+)\s+(\w+)\s*(?:\[\s*(\d+)\s*\])?\s*;\s*$");
	private static readonly Regex InOutLine = new Regex(@"^\s*((?:(?:flat|centroid|noperspective|smooth)\s+)*)(in|out)\s+(\w+)\s+(\w+)\s*;\s*$");
	private static readonly Regex AttributeName = new Regex(@"^in_(POSITION|NORMAL|TANGENT|COLOR|TEXCOORD)(\d+)$");

	private sealed class Member
	{
		public string Name;
		public string Type;
		public int Count;
		public int Offset;
	}

	private sealed class Texture
	{
		public string Name;
		public int Binding;
		public int Dimension;
		public int Stages;
	}

	private readonly List<Texture> textures = new List<Texture>();

	// A deferred G-buffer pass (more than one render target): nothing is drawn before it to test against.
	private readonly bool gBuffer;

	// The two stages' Vulkan GLSL.
	public string Vertex { get; private set; }

	public string Fragment { get; private set; }

	// Vertex channels the program reads (Unity 6's order), and the attribute each one feeds.
	public uint Channels { get; private set; }

	public List<(int Channel, int Input)> Inputs { get; } = new List<(int, int)>();

	// The parameters in 5.6's Vulkan layout (as after a 5.6 program's code).
	public byte[] Parameters { get; private set; }

	public GlslToVulkan(string glsl)
	{
		string vertex = Section(glsl, VertexStageMarker);
		string fragment = Section(glsl, FragmentStageMarker);
		uint targets = Targets(glsl);
		gBuffer = (targets & (targets - 1)) != 0;
		var fragmentUniforms = new List<Member>();
		var vertexUniforms = new List<Member>();
		var varyings = new Dictionary<string, int>();
		// Declarations first (bindings depend on both stages), then the texts.
		var fragmentLines = Parse(fragment, fragmentUniforms, 8);
		var vertexLines = Parse(vertex, vertexUniforms, 4);
		int fragmentBinding = 0;
		int vertexBinding = fragmentUniforms.Count > 0 ? 1 : 0;
		string fragmentBlock = Layout(fragmentUniforms, "PGlobals");
		string vertexBlock = Layout(vertexUniforms, "VGlobals");

		Vertex = Write(vertexLines, vertexUniforms, vertexBlock, vertexBinding, true, varyings);
		Fragment = Write(fragmentLines, fragmentUniforms, fragmentBlock, fragmentBinding, false, varyings);

		var parameters = new MemoryStream();
		var writer = new BinaryWriter(parameters);
		var groups = new List<(string Name, List<Member> Members, int Binding, int Stage)>();
		if (fragmentUniforms.Count > 0)
		{
			groups.Add((fragmentBlock, fragmentUniforms, fragmentBinding, 8));
		}
		if (vertexUniforms.Count > 0)
		{
			groups.Add((vertexBlock, vertexUniforms, vertexBinding, 4));
		}
		// Groups: the one outside constant buffers (empty), then one per block.
		writer.Write(1 + groups.Count);
		WriteString(writer, "");
		writer.Write(0);
		writer.Write(0);
		foreach (var group in groups)
		{
			WriteString(writer, group.Name);
			writer.Write(Size(group.Members));
			writer.Write(group.Members.Count);
			// Unity looks parameters up in lists sorted by name.
			foreach (Member member in group.Members.OrderBy(ParameterName, StringComparer.Ordinal))
			{
				bool matrix = member.Name.StartsWith(MatrixPrefix, StringComparison.Ordinal);
				WriteString(writer, ParameterName(member));
				writer.Write(ParameterType(member.Type));
				writer.Write(matrix ? 4 : 1);
				writer.Write(matrix ? 4 : Columns(member.Type));
				writer.Write(matrix ? 1 : 0);
				writer.Write(matrix ? (member.Count / 4 > 1 ? member.Count / 4 : 0) : member.Count);
				writer.Write(member.Offset);
			}
		}
		// Bindings: kind 0 textures (slot, sampler << 8 | dimension), kind 1 constant buffers. A slot is the binding,
		// the set << 16 and the stages using it << 24.
		writer.Write(textures.Count + groups.Count);
		foreach (Texture texture in textures.OrderBy(t => t.Name, StringComparer.Ordinal))
		{
			WriteString(writer, texture.Name);
			writer.Write(0);
			writer.Write(texture.Binding | TextureSet << 16 | texture.Stages << 24);
			writer.Write(texture.Dimension);
		}
		foreach (var group in groups)
		{
			WriteString(writer, group.Name);
			writer.Write(1);
			writer.Write(group.Binding | UniformSet << 16 | group.Stage << 24);
			writer.Write(0);
		}
		Parameters = parameters.ToArray();
	}

	private static string ParameterName(Member member)
	{
		return member.Name.StartsWith(MatrixPrefix, StringComparison.Ordinal) ? member.Name.Substring(MatrixPrefix.Length) : member.Name;
	}

	// The render targets a GL program's fragment stage writes, a bit each.
	public static uint Targets(string glsl)
	{
		int fragment = glsl.IndexOf(FragmentStageMarker, StringComparison.Ordinal);
		uint targets = 0;
		if (fragment >= 0)
		{
			foreach (Match output in Regex.Matches(glsl.Substring(fragment), @"layout\(location\s*=\s*(\d+)\)\s*out\b"))
			{
				targets |= 1u << int.Parse(output.Groups[1].Value);
			}
		}
		return targets;
	}

	// One stage's text, without the #ifdef around it.
	private static string Section(string glsl, string marker)
	{
		int start = glsl.IndexOf(marker, StringComparison.Ordinal);
		if (start < 0)
		{
			throw new InvalidDataException("no " + marker);
		}
		start = glsl.IndexOf('\n', start) + 1;
		int depth = 0;
		int position = start;
		while (position < glsl.Length)
		{
			int end = glsl.IndexOf('\n', position);
			if (end < 0)
			{
				end = glsl.Length;
			}
			string line = glsl.Substring(position, end - position).Trim();
			if (line.StartsWith("#if", StringComparison.Ordinal))
			{
				depth++;
			}
			else if (line.StartsWith("#endif", StringComparison.Ordinal))
			{
				if (depth == 0)
				{
					return glsl.Substring(start, position - start);
				}
				depth--;
			}
			position = end + 1;
		}
		throw new InvalidDataException("no #endif for " + marker);
	}

	// The stage's lines, its uniforms and samplers taken out.
	private List<string> Parse(string text, List<Member> uniforms, int stage)
	{
		var lines = new List<string>();
		foreach (string line in text.Split('\n'))
		{
			Match uniform = UniformLine.Match(line);
			if (uniform.Success)
			{
				string type = uniform.Groups[1].Value;
				string name = uniform.Groups[2].Value;
				int dimension = SamplerDimension(type);
				if (dimension > 0)
				{
					Texture texture = textures.FirstOrDefault(t => t.Name == name);
					if (texture == null)
					{
						texture = new Texture { Name = name, Binding = textures.Count, Dimension = dimension };
						textures.Add(texture);
					}
					texture.Stages |= stage;
					lines.Add("layout(set = " + TextureSet + ", binding = " + texture.Binding + ") uniform " + type + " " + name + ";");
				}
				else
				{
					if (Columns(type) == 0)
					{
						throw new NotSupportedException("uniform type " + type);
					}
					uniforms.Add(new Member { Type = type, Name = name, Count = uniform.Groups[3].Success ? int.Parse(uniform.Groups[3].Value) : 0 });
				}
				continue;
			}
			if (line.TrimStart().StartsWith("uniform", StringComparison.Ordinal))
			{
				throw new NotSupportedException("uniform declaration: " + line.Trim());
			}
			lines.Add(line);
		}
		return lines;
	}

	// std140 offsets (written out explicitly), and the block's name with a hash of its layout, as Unity names its own.
	private static string Layout(List<Member> members, string name)
	{
		int offset = 0;
		uint hash = 2166136261;
		foreach (Member member in members)
		{
			int columns = Columns(member.Type);
			int size = columns * 4;
			int alignment = columns == 1 ? 4 : columns == 2 ? 8 : 16;
			if (member.Count > 0)
			{
				alignment = 16;
				size = 16 * member.Count;
			}
			offset = (offset + alignment - 1) / alignment * alignment;
			member.Offset = offset;
			offset += size;
			foreach (char c in member.Type + " " + member.Name + " " + member.Count + " " + member.Offset + ";")
			{
				hash = (hash ^ c) * 16777619;
			}
		}
		return name + hash;
	}

	private static int Size(List<Member> members)
	{
		Member last = members.Last();
		return last.Offset + (last.Count > 0 ? 16 * last.Count : Columns(last.Type) * 4);
	}

	private string Write(List<string> lines, List<Member> uniforms, string block, int binding, bool vertex, Dictionary<string, int> varyings)
	{
		bool usesMatrixVP = vertex && uniforms.Any(u => u.Name == MatrixVP);
		bool usesShadowBias = vertex && uniforms.Any(u => u.Name == ShadowBias);
		var output = new StringBuilder();
		output.Append("#version 450\n");
		if (vertex)
		{
			// Each pass of a shader is its own pipeline, and the driver may compile the same position code a little
			// differently in each. A light's additive pass (ForwardAdd, depth test equal or less) then lands just behind
			// the base pass in some pixels, and those flicker as the camera moves (seen on the crystals, whose vertex
			// stage is long). Invariant makes every pass compute the same position bit for bit, as GL drivers did.
			output.Append("invariant gl_Position;\n");
		}
		if (uniforms.Count > 0)
		{
			output.Append("layout(std140, set = " + UniformSet + ", binding = " + binding + ") uniform " + block + "\n{\n");
			foreach (Member member in uniforms)
			{
				string name = (member.Name == MatrixVP && usesMatrixVP) || (member.Name == ShadowBias && usesShadowBias) ? Prefix + member.Name : member.Name;
				output.Append("\tlayout(offset = " + member.Offset + ") " + member.Type + " " + name + (member.Count > 0 ? "[" + member.Count + "]" : "") + ";\n");
			}
			output.Append("};\n");
		}
		if (usesMatrixVP)
		{
			output.Append("vec4 " + MatrixVP + "[4];\n");
		}
		if (usesShadowBias)
		{
			output.Append("vec4 " + ShadowBias + ";\n");
		}
		int attribute = 0;
		bool sawMain = false;
		foreach (string line in lines)
		{
			string trimmed = line.Trim();
			if (trimmed.StartsWith("#version", StringComparison.Ordinal) || trimmed.StartsWith("#extension", StringComparison.Ordinal))
			{
				continue;
			}
			Match inOut = InOutLine.Match(line);
			if (inOut.Success)
			{
				string qualifiers = inOut.Groups[1].Value;
				bool input = inOut.Groups[2].Value == "in";
				string type = inOut.Groups[3].Value;
				string name = inOut.Groups[4].Value;
				int location;
				if (vertex && input)
				{
					Match semantic = AttributeName.Match(name);
					if (!semantic.Success)
					{
						throw new NotSupportedException("vertex input " + name);
					}
					location = attribute++;
					int channel = Channel(semantic.Groups[1].Value, int.Parse(semantic.Groups[2].Value));
					Channels |= 1u << channel;
					Inputs.Add((channel, FirstAttribute + location));
				}
				else
				{
					if (!varyings.TryGetValue(name, out location))
					{
						location = varyings.Count == 0 ? 0 : varyings.Values.Max() + 1;
						varyings[name] = location;
					}
				}
				output.Append("layout(location = " + location + ") " + qualifiers + (input ? "in " : "out ") + type + " " + name + ";\n");
				continue;
			}
			if (vertex && trimmed == "void main()")
			{
				output.Append(line.Replace("main", "csp_main") + "\n");
				sawMain = true;
				continue;
			}
			output.Append(line + "\n");
		}
		if (vertex)
		{
			if (!sawMain)
			{
				throw new NotSupportedException("no void main() in the vertex stage");
			}
			output.Append("void main()\n{\n");
			if (usesMatrixVP)
			{
				// The position: the program run with Unity's own matrix, which gives Vulkan's clip space directly.
				// Turning the GL position back ((w - z) / 2 of z = w - 2z') loses z to the cancellation (the w row's
				// terms are the size of the world coordinates), and in the deferred path the forward-only objects (the
				// crystals) are tested against a depth Unity draws first with a shadow caster (equal or less): whole
				// triangles came out behind it and were dropped, so the shards looked moved and broken.
				output.Append("\t" + MatrixVP + " = " + Prefix + MatrixVP + ";\n");
				if (usesShadowBias)
				{
					// The shadow caster's linear bias and clamp to the near plane are GL's in its code: left out here
					// (the normal offset, in world space, stays) and done after, as Unity's Vulkan programs do.
					output.Append("\t" + ShadowBias + " = vec4(0.0, 0.0, " + Prefix + ShadowBias + ".zw);\n");
				}
				output.Append("\tcsp_main();\n");
				output.Append("\tvec4 " + Prefix + "position = gl_Position;\n");
				// Everything else (fog, the varyings) from the GL clip space the code was compiled for.
				output.Append("\tfor (int i = 0; i < 4; i++)\n\t{\n");
				output.Append("\t\t" + MatrixVP + "[i] = " + Prefix + MatrixVP + "[i];\n");
				output.Append("\t\t" + MatrixVP + "[i].z = " + Prefix + MatrixVP + "[i].w - 2.0 * " + Prefix + MatrixVP + "[i].z;\n");
				output.Append("\t}\n");
			}
			if (usesShadowBias)
			{
				output.Append("\t" + ShadowBias + " = " + Prefix + ShadowBias + ";\n");
				output.Append("\t" + ShadowBias + ".x = -2.0 * " + Prefix + ShadowBias + ".x;\n");
			}
			output.Append("\tcsp_main();\n");
			if (usesMatrixVP)
			{
				output.Append("\tgl_Position = " + Prefix + "position;\n");
				if (usesShadowBias)
				{
					// UnityApplyLinearShadowBias with reversed depth (near at w).
					output.Append("\tgl_Position.z += clamp(" + Prefix + ShadowBias + ".x / gl_Position.w, -1.0, 0.0);\n");
					output.Append("\tgl_Position.z = mix(gl_Position.z, min(gl_Position.z, gl_Position.w), " + Prefix + ShadowBias + ".y);\n");
				}
				else if (!gBuffer)
				{
					// Even so the depth Unity drew first is not quite ours (other code, other rounding; 0.001 % was not
					// enough) and hid whole triangles: the forward passes come a hair nearer.
					output.Append("\tgl_Position.z *= " + DepthNearer + ";\n");
				}
			}
			// Vulkan's clip space has y pointing down; Unity's own Vulkan programs flip it at the end too.
			output.Append("\tgl_Position.y = -gl_Position.y;\n");
			output.Append("}\n");
		}
		return output.ToString();
	}

	// Unity 6's vertex channels: position, normal, tangent, color, uv0-uv7.
	private static int Channel(string semantic, int index)
	{
		switch (semantic)
		{
			case "POSITION":
				return 0;
			case "NORMAL":
				return 1;
			case "TANGENT":
				return 2;
			case "COLOR":
				return 3;
			default:
				if (index > 7)
				{
					throw new NotSupportedException("TEXCOORD" + index);
				}
				return 4 + index;
		}
	}

	// Unity's texture dimensions: 2D 2, 3D 3, cube 4, 2D array 5, cube array 6.
	private static int SamplerDimension(string type)
	{
		switch (type)
		{
			case "sampler2D":
			case "sampler2DShadow":
				return 2;
			case "sampler3D":
				return 3;
			case "samplerCube":
			case "samplerCubeShadow":
				return 4;
			case "sampler2DArray":
			case "sampler2DArrayShadow":
				return 5;
			case "samplerCubeArray":
				return 6;
			default:
				return 0;
		}
	}

	private static int Columns(string type)
	{
		switch (type)
		{
			case "float":
			case "int":
			case "uint":
			case "bool":
				return 1;
			default:
				Match vector = Regex.Match(type, @"^[biu]?vec([234])$");
				return vector.Success ? int.Parse(vector.Groups[1].Value) : 0;
		}
	}

	// Unity's parameter types: float 0, int 1, bool 2, uint 5.
	private static int ParameterType(string type)
	{
		if (type.StartsWith("i", StringComparison.Ordinal))
		{
			return 1;
		}
		if (type.StartsWith("b", StringComparison.Ordinal))
		{
			return 2;
		}
		if (type.StartsWith("u", StringComparison.Ordinal))
		{
			return 5;
		}
		return 0;
	}

	private static void WriteString(BinaryWriter writer, string value)
	{
		byte[] bytes = Encoding.UTF8.GetBytes(value);
		writer.Write(bytes.Length);
		writer.Write(bytes);
		while (writer.BaseStream.Position % 4 != 0)
		{
			writer.Write((byte)0);
		}
	}
}
