using System;
using System.IO;
using System.Linq;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using CastleStoryPlus.Port;

namespace CastleStoryPlus.Port.Cli;

internal static class Program
{
	private static int Main(string[] args)
	{
		if (args.Length == 0)
		{
			Console.WriteLine("usage: data <classdata.tpk> <Castle Story_Data> <out dir> | scan <out> <assembly>... -- <dir>... | inspect <classdata.tpk> <Castle Story_Data>");
			return 1;
		}
		switch (args[0])
		{
			case "data":
				{
					// data <classdata.tpk> <Castle Story_Data> <out dir> [engine-types.assets]
					var watch = System.Diagnostics.Stopwatch.StartNew();
					DataConverter.ConvertAll(args[1], args[2], args[3], Console.WriteLine, args.Length > 4 ? args[4] : null);
					Console.WriteLine("done in " + watch.Elapsed.TotalSeconds.ToString("0.0") + " s");
					return 0;
				}
			case "manifest":
				{
					// manifest <Castle Story_Data> <out file>: hash list of a clean install
					GameFiles.CreateManifest(args[1], args[2], Console.WriteLine);
					return 0;
				}
			case "verify":
				{
					// verify <Castle Story_Data> <manifest>
					var problems = GameFiles.Verify(args[1], args[2], null);
					problems.ForEach(Console.WriteLine);
					Console.WriteLine(problems.Count == 0 ? "all files match" : problems.Count + " problems");
					return problems.Count == 0 ? 0 : 2;
				}
			case "install":
				{
					// install <classdata.tpk> <manifest> <Castle Story_Data> <player _Data dir> <content dir>
					var watch = System.Diagnostics.Stopwatch.StartNew();
					var installer = new Installer
					{
						ClassPackage = args[1],
						Manifest = args[2],
						GameDataDir = args[3],
						PlayerDataDir = args[4],
						ContentDir = args[5],
						Log = Console.WriteLine,
						Progress = (step, done) => Console.WriteLine("[" + (done * 100).ToString("0") + "%] " + step),
					};
					try
					{
						installer.Run();
					}
					catch (GameFilesException e)
					{
						e.Problems.ForEach(Console.WriteLine);
						Console.WriteLine(e.Message);
						return 2;
					}
					Console.WriteLine("done in " + watch.Elapsed.TotalSeconds.ToString("0.0") + " s");
					return 0;
				}
			case "settings":
				{
					// settings <classdata.tpk> <Castle Story_Data> <player _Data dir>
					GameSettings.Copy(args[1], args[2], args[3], Console.WriteLine);
					return 0;
				}
			case "fields":
				{
					// fields <classdata.tpk> <unity version> <class id>: the class's type tree
					var manager = new AssetsManager();
					manager.LoadClassPackage(args[1]);
					var classes = manager.LoadClassDatabaseFromPackage(args[2]);
					var template = new AssetTypeTemplateField();
					template.FromClassDatabase(classes, classes.FindAssetClassByID(int.Parse(args[3])));
					void Print(AssetTypeTemplateField field, int depth)
					{
						Console.WriteLine(new string(' ', depth * 2) + field.Type + " " + field.Name + (field.IsAligned ? " (aligned)" : "") + " v" + field.Version);
						foreach (var child in field.Children)
						{
							Print(child, depth + 1);
						}
					}
					Print(template, 0);
					return 0;
				}
			case "scripts":
				{
					// scripts <classdata.tpk> <Castle Story_Data> <game object name>: the scripts on game objects of that name
					var manager = new AssetsManager();
					manager.LoadClassPackage(args[1]);
					manager.LoadClassDatabaseFromPackage(BundleConverter.EngineVersion);
					AssetsFileInstance ggm = manager.LoadAssetsFile(Path.Combine(args[2], "globalgamemanagers"), false);
					foreach (string file in Directory.GetFiles(args[2]).OrderBy(f => f, StringComparer.Ordinal).Where(AssetsFile.IsAssetsFile))
					{
						AssetsFileInstance inst = manager.LoadAssetsFile(file, false);
						foreach (var info in inst.file.GetAssetsOfType(AssetClassID.GameObject))
						{
							var go = manager.GetBaseField(inst, info);
							if (!go["m_Name"].AsString.Contains(args[3]))
							{
								continue;
							}
							foreach (var component in go["m_Component"]["Array"].Children)
							{
								var pptr = component["component"];
								var target = manager.GetExtAsset(inst, pptr["m_FileID"].AsInt, pptr["m_PathID"].AsLong);
								if (target.info != null && target.info.TypeId == (int)AssetClassID.RectTransform)
								{
									var rect = manager.GetBaseField(target.file, target.info);
									string V(string f) => rect[f]["x"].AsFloat + "," + rect[f]["y"].AsFloat;
									Console.WriteLine(Path.GetFileName(file) + " " + info.PathId + ": RectTransform anchors " + V("m_AnchorMin") + "-" + V("m_AnchorMax") + " pos " + V("m_AnchoredPosition") + " size " + V("m_SizeDelta") + " pivot " + V("m_Pivot"));
									continue;
								}
								if (target.info != null && target.info.TypeId == (int)AssetClassID.Canvas)
								{
									var canvas = manager.GetBaseField(target.file, target.info);
									Console.WriteLine(Path.GetFileName(file) + " " + info.PathId + ": Canvas mode " + canvas["m_RenderMode"].AsInt + " override " + canvas["m_OverrideSorting"].AsBool + " order " + canvas["m_SortingOrder"].AsInt);
									continue;
								}
								if (target.info == null || target.info.TypeId != (int)AssetClassID.MonoBehaviour)
								{
									continue;
								}
								var behaviour = manager.GetBaseField(target.file, target.info);
								var script = manager.GetExtAsset(target.file, behaviour["m_Script"]["m_FileID"].AsInt, behaviour["m_Script"]["m_PathID"].AsLong);
								string name = script.info == null ? "(script not found: " + behaviour["m_Script"]["m_FileID"].AsInt + ":" + behaviour["m_Script"]["m_PathID"].AsLong + ")" : string.Join(" ", new[] { "m_AssemblyName", "m_Namespace", "m_ClassName" }.Select(f => manager.GetBaseField(script.file, script.info)[f].AsString));
								Console.WriteLine(Path.GetFileName(file) + " " + info.PathId + ": " + name + " in " + Path.GetFileName(script.file?.path ?? "?"));
							}
						}
					}
					return 0;
				}
			case "tree":
				{
					// tree <classdata.tpk> <unity version> <serialized file or bundle> <class id> [name] [max array items]: one
					// object's whole field tree (a bundle's own type trees are used when it has them)
					var manager = new AssetsManager();
					manager.LoadClassPackage(args[1]);
					manager.LoadClassDatabaseFromPackage(args[2]);
					AssetsFileInstance inst;
					if (AssetsFile.IsAssetsFile(args[3]))
					{
						inst = manager.LoadAssetsFile(args[3], false);
					}
					else
					{
						var bundle = manager.LoadBundleFile(args[3]);
						inst = manager.LoadAssetsFileFromBundle(bundle, 0);
					}
					int max = args.Length > 6 ? int.Parse(args[6]) : 4;
					foreach (var info in inst.file.GetAssetsOfType(int.Parse(args[4])))
					{
						var field = manager.GetBaseField(inst, info);
						string name = !field["m_ParsedForm"].IsDummy ? field["m_ParsedForm"]["m_Name"].AsString : !field["m_Name"].IsDummy ? field["m_Name"].AsString : "";
						if (args.Length > 5 && args[5] != "*" && name != args[5])
						{
							continue;
						}
						void Print(AssetTypeValueField f, int depth)
						{
							string value = f.Value == null ? "" : f.Value.ValueType == AssetValueType.ByteArray ? " = [" + f.AsByteArray.Length + " bytes" + (f.AsByteArray.Length <= 32 ? ": " + BitConverter.ToString(f.AsByteArray) : "") + "]" : " = " + f.AsString;
							Console.WriteLine(new string(' ', depth * 2) + f.TypeName + " " + f.FieldName + value + (f.TemplateField.IsArray ? " (" + f.Children.Count + ")" : ""));
							if (f.Value != null && f.Value.ValueType == AssetValueType.ByteArray)
							{
								return;
							}
							int shown = 0;
							foreach (var child in f.Children)
							{
								if (f.TemplateField.IsArray && ++shown > max)
								{
									Console.WriteLine(new string(' ', depth * 2 + 2) + "...");
									break;
								}
								Print(child, depth + 1);
							}
						}
						Print(field, 0);
						break;
					}
					return 0;
				}
			case "bindings":
				{
					// bindings <classdata.tpk> <unity version> <data dir>: how often each (typeID, customType, attribute for a
					// transform) occurs in the animation clips' bindings of every serialized file there
					var manager = new AssetsManager();
					manager.LoadClassPackage(args[1]);
					manager.LoadClassDatabaseFromPackage(args[2]);
					var counts = new System.Collections.Generic.SortedDictionary<string, int>(StringComparer.Ordinal);
					foreach (string file in Directory.GetFiles(args[3]).Where(AssetsFile.IsAssetsFile))
					{
						AssetsFileInstance inst = manager.LoadAssetsFile(file, false);
						foreach (var info in inst.file.GetAssetsOfType(AssetClassID.AnimationClip))
						{
							var clip = manager.GetBaseField(inst, info);
							foreach (var binding in clip["m_ClipBindingConstant"]["genericBindings"]["Array"].Children)
							{
								int type = binding["typeID"].AsInt;
								string key = "typeID " + type + " customType " + binding["customType"].AsByte + (type == 4 ? " attribute " + binding["attribute"].AsUInt : "");
								counts[key] = counts.TryGetValue(key, out int n) ? n + 1 : 1;
							}
						}
						manager.UnloadAssetsFile(inst);
					}
					foreach (var pair in counts)
					{
						Console.WriteLine(pair.Value.ToString().PadLeft(6) + "  " + pair.Key);
					}
					return 0;
				}
			case "names":
				{
					// names <classdata.tpk> <bundle> <class id>: the names of a bundle's objects of a class, with their file
					var manager = new AssetsManager();
					manager.LoadClassPackage(args[1]);
					manager.LoadClassDatabaseFromPackage(BundleConverter.EngineVersion);
					var bundle = manager.LoadBundleFile(args[2]);
					for (int i = 0; i < bundle.file.BlockAndDirInfo.DirectoryInfos.Count; i++)
					{
						if (!bundle.file.IsAssetsFile(i))
						{
							continue;
						}
						AssetsFileInstance inst = manager.LoadAssetsFileFromBundle(bundle, i);
						foreach (var info in inst.file.GetAssetsOfType(int.Parse(args[3])))
						{
							Console.WriteLine(i + " " + info.PathId + " " + manager.GetBaseField(inst, info)["m_Name"].AsString);
						}
					}
					return 0;
				}
			case "objects":
				{
					// objects <classdata.tpk> <bundle> <name>: the game objects whose name holds that and their components (scripts by
					// class, from the bundle's own MonoScripts or those of the bundles next to it)
					var manager = new AssetsManager();
					manager.LoadClassPackage(args[1]);
					manager.LoadClassDatabaseFromPackage(BundleConverter.EngineVersion);
					var bundle = manager.LoadBundleFile(args[2]);
					var files = Enumerable.Range(0, bundle.file.BlockAndDirInfo.DirectoryInfos.Count)
						.Where(i => bundle.file.IsAssetsFile(i))
						.Select(i => manager.LoadAssetsFileFromBundle(bundle, i))
						.ToList();
					string folder = Path.GetDirectoryName(Path.GetFullPath(args[2]));
					foreach (var external in files.SelectMany(f => f.file.Metadata.Externals))
					{
						string candidate = Path.Combine(folder, external.PathName.Replace("archive:/", "").Split('/')[0] + ".bundle");
						if (File.Exists(candidate) && candidate != Path.GetFullPath(args[2]))
						{
							manager.LoadAssetsFileFromBundle(manager.LoadBundleFile(candidate), 0);
						}
					}
					foreach (var inst in files)
					{
						foreach (var info in inst.file.GetAssetsOfType(AssetClassID.GameObject))
						{
							var go = manager.GetBaseField(inst, info);
							if (!go["m_Name"].AsString.Contains(args[3]))
							{
								continue;
							}
							Console.WriteLine(info.PathId + " " + go["m_Name"].AsString);
							foreach (var pair in go["m_Component"]["Array"].Children)
							{
								var component = manager.GetExtAsset(inst, pair["component"]);
								if (component.info == null)
								{
									Console.WriteLine("  (unresolved " + pair["component"]["m_FileID"].AsInt + ":" + pair["component"]["m_PathID"].AsLong + ")");
									continue;
								}
								string kind = ((AssetClassID)component.info.TypeId).ToString();
								if (component.info.TypeId == (int)AssetClassID.MonoBehaviour)
								{
									var behaviour = manager.GetBaseField(component.file, component.info, AssetReadFlags.SkipMonoBehaviourFields);
									var script = manager.GetExtAsset(component.file, behaviour["m_Script"]);
									kind = script.info == null
										? "script ? (" + behaviour["m_Script"]["m_FileID"].AsInt + ":" + behaviour["m_Script"]["m_PathID"].AsLong + ")"
										: "script " + script.baseField["m_AssemblyName"].AsString + " " + script.baseField["m_Namespace"].AsString + "." + script.baseField["m_ClassName"].AsString;
								}
								Console.WriteLine("  " + kind);
							}
						}
					}

					return 0;
				}
			case "glsl-vulkan":
				{
					// glsl-vulkan <file or folder of .glsl> [out dir]: a 5.6 OpenGL Core program (as the blob command's GL
					// entries hold it) rewritten for Vulkan and compiled, as the installer does for a Vulkan player; the
					// Vulkan GLSL goes to the out dir
					var files = Directory.Exists(args[1]) ? Directory.GetFiles(args[1], "*.glsl").OrderBy(f => f, StringComparer.Ordinal).ToArray() : new[] { args[1] };
					int failed = 0;
					using (var shaderc = new Shaderc())
					{
						foreach (string file in files)
						{
							string stage = "rewrite";
							try
							{
								var program = new GlslToVulkan(File.ReadAllText(file));
								if (args.Length > 2)
								{
									Directory.CreateDirectory(args[2]);
									File.WriteAllText(Path.Combine(args[2], Path.GetFileNameWithoutExtension(file) + ".vert"), program.Vertex);
									File.WriteAllText(Path.Combine(args[2], Path.GetFileNameWithoutExtension(file) + ".frag"), program.Fragment);
								}
								stage = "vertex";
								byte[] vertex = shaderc.Compile(program.Vertex, Shaderc.Stage.Vertex, file);
								stage = "fragment";
								byte[] fragment = shaderc.Compile(program.Fragment, Shaderc.Stage.Fragment, file);
								stage = "smol-v";
								foreach (byte[] spirv in new[] { vertex, fragment })
								{
									if (!SmolV.Decode(SmolV.Encode(spirv)).SequenceEqual(spirv))
									{
										throw new InvalidDataException("SMOL-V round trip differs");
									}
								}
							}
							catch (Exception e) when (e is InvalidDataException || e is NotSupportedException)
							{
								failed++;
								Console.WriteLine(Path.GetFileName(file) + ": " + stage + ": " + e.Message.Split('\n')[0]);
							}
						}
					}
					Console.WriteLine(files.Length - failed + " of " + files.Length + " compiled");
					return failed == 0 ? 0 : 1;
				}
			case "smolv-check":
				{
					// smolv-check <folder of Unity 6 platform blobs (blob command, 18-*.bin)>: every Vulkan stage decoded and
					// encoded again must give the same bytes
					int streams = 0;
					int differ = 0;
					foreach (string file in Directory.GetFiles(args[1], "18-*.bin", SearchOption.AllDirectories))
					{
						byte[] blob = File.ReadAllBytes(file);
						int count = BitConverter.ToInt32(blob, 0);
						for (int i = 0; i < count; i++)
						{
							int offset = BitConverter.ToInt32(blob, 4 + i * 12);
							int length = BitConverter.ToInt32(blob, 8 + i * 12);
							if (BitConverter.ToInt32(blob, offset + 4) != 25)
							{
								continue;
							}
							int position = offset + 24;
							int keywords = BitConverter.ToInt32(blob, position);
							position += 4;
							for (int k = 0; k < keywords; k++)
							{
								position = (position + 4 + BitConverter.ToInt32(blob, position) + 3) & ~3;
							}
							int code = position + 4;
							for (int stage = 0; stage < 5; stage++)
							{
								int stageOffset = BitConverter.ToInt32(blob, code + 4 + stage * 8);
								int stageLength = BitConverter.ToInt32(blob, code + 8 + stage * 8);
								if (stageLength == 0)
								{
									continue;
								}
								byte[] smol = new byte[stageLength];
								Buffer.BlockCopy(blob, code + stageOffset, smol, 0, stageLength);
								streams++;
								try
								{
									if (!SmolV.Encode(SmolV.Decode(smol)).SequenceEqual(smol))
									{
										differ++;
										Console.WriteLine(file + " entry " + i + " stage " + stage + ": differs");
									}
								}
								catch (InvalidDataException e)
								{
									differ++;
									Console.WriteLine(file + " entry " + i + " stage " + stage + ": " + e.Message);
								}
							}
						}
					}
					Console.WriteLine(streams + " streams, " + differ + " differ");
					return differ == 0 ? 0 : 1;
				}
			case "blob":
				{
					// blob <classdata.tpk> <unity version> <serialized file or bundle> <shader name> <out dir>: each platform's
					// decompressed program blob of a shader, as <platform>-<part>.bin
					var manager = new AssetsManager();
					manager.LoadClassPackage(args[1]);
					manager.LoadClassDatabaseFromPackage(args[2]);
					AssetsFileInstance inst = AssetsFile.IsAssetsFile(args[3]) ? manager.LoadAssetsFile(args[3], false) : manager.LoadAssetsFileFromBundle(manager.LoadBundleFile(args[3]), 0);
					Directory.CreateDirectory(args[5]);
					foreach (var info in inst.file.GetAssetsOfType(AssetClassID.Shader))
					{
						var shader = manager.GetBaseField(inst, info);
						string shaderName = shader["m_ParsedForm"]["m_Name"].AsString;
						if (args[4] != "*" && shaderName != args[4])
						{
							continue;
						}
						string dir = args[4] == "*" ? Path.Combine(args[5], string.Join("_", shaderName.Split(Path.GetInvalidFileNameChars().Append('/').ToArray()))) : args[5];
						Directory.CreateDirectory(dir);
						byte[] blob = shader["compressedBlob"]["Array"].AsByteArray;
						var platforms = shader["platforms"]["Array"].Children;
						for (int p = 0; p < platforms.Count; p++)
						{
							var offsets = shader["offsets"]["Array"][p];
							var compressed = shader["compressedLengths"]["Array"][p];
							var decompressed = shader["decompressedLengths"]["Array"][p];
							// 5.6 has one part per platform (plain uints), Unity 6 a vector of parts.
							int parts = offsets.Children.Count > 0 && offsets.Value == null ? offsets["Array"].Children.Count : 1;
							for (int part = 0; part < parts; part++)
							{
								uint offset = offsets.Value != null ? offsets.AsUInt : offsets["Array"][part].AsUInt;
								uint clen = compressed.Value != null ? compressed.AsUInt : compressed["Array"][part].AsUInt;
								uint dlen = decompressed.Value != null ? decompressed.AsUInt : decompressed["Array"][part].AsUInt;
								byte[] data = LZ4ps.LZ4Codec.Decode32(blob, (int)offset, (int)clen, (int)dlen);
								string file = Path.Combine(dir, platforms[p].AsUInt + "-" + part + ".bin");
								File.WriteAllBytes(file, data);
								Console.WriteLine(file + " " + data.Length + " bytes");
							}
						}
					}
					return 0;
				}
			case "dump":
				{
					// dump <classdata.tpk> <unity version> <serialized file> <class id> [count]: objects' top-level values
					var manager = new AssetsManager();
					manager.LoadClassPackage(args[1]);
					manager.LoadClassDatabaseFromPackage(args[2]);
					var inst = manager.LoadAssetsFile(args[3], false);
					foreach (var info in inst.file.GetAssetsOfType(int.Parse(args[4])).Take(args.Length > 5 ? int.Parse(args[5]) : 10))
					{
						var field = manager.GetBaseField(inst, info);
						Console.WriteLine(info.PathId + ": " + string.Join(" | ", field.Children
							.Where(c => c.Value != null || c["m_PathID"] is { IsDummy: false })
							.Select(c => c.FieldName + "=" + (c.Value != null ? c.AsString : c["m_FileID"].AsInt + ":" + c["m_PathID"].AsLong))));
					}
					return 0;
				}
			case "scan":
				{
					// scan <out file> <assembly>... -- <search dir>...
					int split = Array.IndexOf(args, "--");
					var scanner = new ReferenceScanner(args.Skip(split + 1));
					using (var output = new StreamWriter(args[1]))
					{
						foreach (string assembly in args.Skip(2).Take(split - 2))
						{
							var missing = scanner.Scan(assembly);
							output.WriteLine("## " + Path.GetFileName(assembly) + " (" + missing.Count + ")");
							foreach (var pair in missing)
							{
								output.WriteLine(pair.Key);
								foreach (string user in pair.Value.Take(3))
								{
									output.WriteLine("    " + user);
								}
								if (pair.Value.Count > 3)
								{
									output.WriteLine("    ... " + (pair.Value.Count - 3) + " more");
								}
							}
							Console.WriteLine(Path.GetFileName(assembly) + ": " + missing.Count + " missing");
						}
					}
					return 0;
				}
			case "inspect":
				{
					// inspect <classdata.tpk> <data dir>: scenes, externals and object counts per file
					var manager = new AssetsManager();
					manager.LoadClassPackage(args[1]);
					manager.LoadClassDatabaseFromPackage(BundleConverter.EngineVersion);
					AssetsFileInstance ggm = manager.LoadAssetsFile(Path.Combine(args[2], "globalgamemanagers"), false);
					foreach (var info in ggm.file.AssetInfos)
					{
						int typeId = info.GetTypeId(ggm.file);
						if (typeId == (int)AssetClassID.BuildSettings)
						{
							var scenes = manager.GetBaseField(ggm, info)["scenes"]["Array"].Children;
							for (int i = 0; i < scenes.Count; i++)
							{
								Console.WriteLine("scene " + i + " " + scenes[i].AsString);
							}
						}
						if (typeId == (int)AssetClassID.ResourceManager)
						{
							var container = manager.GetBaseField(ggm, info)["m_Container"]["Array"].Children;
							Console.WriteLine("resources " + container.Count);
							foreach (var pair in container.Take(5))
							{
								Console.WriteLine("  " + pair["first"].AsString + " -> " + pair["second"]["m_FileID"].AsInt + ":" + pair["second"]["m_PathID"].AsLong);
							}
						}
					}
					foreach (string file in Directory.GetFiles(args[2]).OrderBy(f => f, StringComparer.Ordinal))
					{
						if (!AssetsFile.IsAssetsFile(file))
						{
							continue;
						}
						AssetsFileInstance inst = manager.LoadAssetsFile(file, false);
						var counts = inst.file.AssetInfos.GroupBy(a => a.GetTypeId(inst.file)).OrderByDescending(g => g.Count()).Take(6).Select(g => (AssetClassID)g.Key + ":" + g.Count());
						Console.WriteLine(Path.GetFileName(file) + " v" + inst.file.Header.Version + " objects " + inst.file.AssetInfos.Count + " scripts " + inst.file.Metadata.ScriptTypes.Count + " | " + string.Join(" ", counts));
						Console.WriteLine("    ext: " + string.Join(", ", inst.file.Metadata.Externals.Select(e => e.PathName)));
						manager.UnloadAssetsFile(inst);
					}
					return 0;
				}
			default:
				Console.WriteLine("unknown command " + args[0]);
				return 1;
		}
	}
}
