using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace CastleStoryPlus.Port;

// Repacks the game's serialized files (Unity 5.6 player data, no type trees) as asset bundles a newer Unity can load.
// Every type gets its 5.6 type tree, so Unity reads the objects field by field and upgrades them itself; MonoBehaviours
// get theirs from the game's assemblies. References between files become archive paths, streamed data (.resS) moves
// into the bundle, and each bundle gets the AssetBundle object Unity looks for, with preload tables (the player binds a
// MonoBehaviour to its script only when the script was preloaded).
//   levelN + sharedassetsN.assets -> scene bundle BuildPlayer-<scene>: BuildPlayer-<scene>.sharedAssets (the shared
//                                    assets, AssetBundle object in place of the PreloadData, whose list it takes over)
//                                    and BuildPlayer-<scene> (the scene), as Unity lays out its own scene bundles
//   resources.assets, globalgamemanagers.assets -> CAB-<name>
public sealed class BundleConverter : IDisposable
{
	public const string EngineVersion = "5.6.5f1";
	public const string DefaultResources = "Library/unity default resources";
	public const string BuiltinExtra = "Resources/unity_builtin_extra";

	private readonly AssetsManager manager;
	private readonly ClassDatabaseFile classes;
	private readonly string dataDir;
	private readonly Dictionary<int, TypeTreeType> classTrees = new Dictionary<int, TypeTreeType>();
	private readonly Dictionary<string, TypeTreeType> scriptTrees = new Dictionary<string, TypeTreeType>();
	private readonly ShaderConverter shaders;

	// engineTypes: the player's engine-types.assets, for converting the shaders (without it they stay as they are).
	public BundleConverter(string classPackage, string dataDir, string engineTypes = null)
	{
		this.dataDir = dataDir;
		shaders = engineTypes == null ? null : new ShaderConverter(engineTypes);
		manager = new AssetsManager();
		manager.LoadClassPackage(classPackage);
		classes = manager.LoadClassDatabaseFromPackage(EngineVersion);
		manager.MonoTempGenerator = new MonoCecilTempGenerator(Path.Combine(dataDir, "Managed"));
	}

	// An asset in a bundle's container: its name, where it is (file id as seen from the AssetBundle object's file) and
	// what to load first.
	public sealed class ContainerEntry
	{
		public string Key;
		public int FileId;
		public long PathId;
		public List<(int FileId, long PathId)> Preload = new List<(int, long)>();
	}

	// The bundle (without extension) a game file goes into; also the archive folder its files are mounted in.
	public static string MountOf(string fileName, IList<string> scenes)
	{
		int scene = SceneIndexOf(fileName);
		return scene >= 0 ? "BuildPlayer-" + SceneName(scenes[scene]) : "CAB-" + Path.GetFileNameWithoutExtension(fileName);
	}

	// A game file's name inside its bundle.
	public static string InnerNameOf(string fileName, IList<string> scenes)
	{
		string mount = MountOf(fileName, scenes);
		return fileName.StartsWith("sharedassets", StringComparison.Ordinal) ? mount + ".sharedAssets" : mount;
	}

	public static string ArchivePath(string fileName, IList<string> scenes)
	{
		return "archive:/" + MountOf(fileName, scenes) + "/" + InnerNameOf(fileName, scenes);
	}

	public static string SceneName(string scenePath) => Path.GetFileNameWithoutExtension(scenePath);

	// levelN and sharedassetsN.assets belong to scene N; -1 for other files.
	private static int SceneIndexOf(string fileName)
	{
		if (fileName.StartsWith("level", StringComparison.Ordinal) && int.TryParse(fileName.Substring(5), out int level))
		{
			return level;
		}
		if (fileName.StartsWith("sharedassets", StringComparison.Ordinal) && fileName.EndsWith(".assets", StringComparison.Ordinal)
			&& int.TryParse(fileName.Substring(12, fileName.Length - 12 - 7), out int shared))
		{
			return shared;
		}
		return -1;
	}

	// The scene paths of the build, in build index order (levelN is scene N).
	public IList<string> ReadScenes()
	{
		AssetsFileInstance ggm = manager.LoadAssetsFile(Path.Combine(dataDir, "globalgamemanagers"), false);
		AssetFileInfo info = ggm.file.GetAssetsOfType(AssetClassID.BuildSettings).Single();
		return manager.GetBaseField(ggm, info)["scenes"]["Array"].Children.Select(c => c.AsString).ToList();
	}

	// Resources.Load paths (lower case, no extension) with the file and path id of their objects.
	public List<(string Path, string File, long PathId)> ReadResources()
	{
		AssetsFileInstance ggm = manager.LoadAssetsFile(Path.Combine(dataDir, "globalgamemanagers"), false);
		AssetFileInfo info = ggm.file.GetAssetsOfType(AssetClassID.ResourceManager).Single();
		var result = new List<(string, string, long)>();
		foreach (AssetTypeValueField pair in manager.GetBaseField(ggm, info)["m_Container"]["Array"].Children)
		{
			int fileId = pair["second"]["m_FileID"].AsInt;
			string file = fileId == 0 ? "globalgamemanagers" : Path.GetFileName(ggm.file.Metadata.Externals[fileId - 1].PathName);
			result.Add((pair["first"].AsString, file, pair["second"]["m_PathID"].AsLong));
		}
		return result;
	}

	public List<long> ObjectsOfType(string fileName, AssetClassID type)
	{
		AssetsFileInstance inst = manager.LoadAssetsFile(Path.Combine(dataDir, fileName), false);
		return inst.file.GetAssetsOfType(type).Select(i => i.PathId).ToList();
	}

	// A file that is no scene's (resources.assets, globalgamemanagers.assets) as a bundle; container lists what can be
	// loaded by name (key, path id).
	public void ConvertShared(string fileName, IList<string> scenes, string bundlePath, IList<(string Key, long PathId)> container)
	{
		string mount = MountOf(fileName, scenes);
		AssetsFileInstance inst = manager.LoadAssetsFile(Path.Combine(dataDir, fileName), true);
		Prepare(inst, scenes, mount, mount);
		var closures = new Dictionary<long, List<(int, long)>>();
		var entries = new List<ContainerEntry>();
		foreach ((string key, long pathId) in container)
		{
			if (!closures.TryGetValue(pathId, out List<(int, long)> preload))
			{
				preload = Dependencies(inst, pathId);
				closures[pathId] = preload;
			}
			entries.Add(new ContainerEntry { Key = key, FileId = 0, PathId = pathId, Preload = preload });
		}

		var files = new List<UnityFsWriter.Entry>();
		AssetFileInfo first = inst.file.GetAssetInfo(1);
		if (first == null || first.GetTypeId(inst.file) == (int)AssetClassID.PreloadData)
		{
			RemovePreloadData(inst.file);
			AddAssetBundle(inst.file, mount, false, entries);
			files.Add(Serialize(inst.file, mount));
		}
		else
		{
			// Unity only takes a bundle whose first file has the AssetBundle object at path id 1. Where an asset has that
			// id (resources.assets), the AssetBundle object goes into a small file of its own in front, which sees the
			// asset file as file 1 and the asset file's own externals from 2 on.
			AssetsFile index = NewFile(inst.file);
			index.Metadata.Externals.Add(External("archive:/" + mount + "/" + mount));
			foreach (AssetsFileExternal external in inst.file.Metadata.Externals)
			{
				index.Metadata.Externals.Add(External(external.PathName, external.Guid, external.Type));
			}
			foreach (ContainerEntry entry in entries)
			{
				entry.FileId = 1;
				entry.Preload = entry.Preload.Select(p => (p.Item1 + 1, p.Item2)).ToList();
			}
			AddAssetBundle(index, mount, false, entries);
			files.Add(Serialize(index, mount + ".index"));
			files.Add(Serialize(inst.file, mount));
		}
		AddResS(files, Path.Combine(dataDir, fileName), mount);
		UnityFsWriter.Write(bundlePath, EngineVersion, files);
	}

	// levelN with sharedassetsN.assets as a scene bundle.
	public void ConvertScene(int index, IList<string> scenes, string bundlePath)
	{
		string levelName = "level" + index;
		string sharedName = "sharedassets" + index + ".assets";
		string mount = MountOf(levelName, scenes);
		string sharedInner = InnerNameOf(sharedName, scenes);

		AssetsFileInstance level = manager.LoadAssetsFile(Path.Combine(dataDir, levelName), true);
		AssetsFileInstance shared = manager.LoadAssetsFile(Path.Combine(dataDir, sharedName), true);
		// The scene's preload list: what the 5.6 player loaded before the scene, as seen from sharedassetsN.
		List<(int, long)> preload = new List<(int, long)>();
		AssetFileInfo preloadData = shared.file.GetAssetsOfType(AssetClassID.PreloadData).FirstOrDefault();
		if (preloadData != null)
		{
			foreach (AssetTypeValueField pptr in manager.GetBaseField(shared, preloadData)["m_Assets"]["Array"].Children)
			{
				preload.Add((pptr["m_FileID"].AsInt, pptr["m_PathID"].AsLong));
			}
		}
		RemoveMissingScripts(level);
		Prepare(level, scenes, mount, mount);
		Prepare(shared, scenes, mount, sharedInner);
		preload.AddRange(SceneReferences(level, shared, ArchivePath(sharedName, scenes)).Where(r => !preload.Contains(r)));
		MovePreloadData(shared, preload);
		AddAssetBundle(shared.file, mount, true, new List<ContainerEntry>
		{
			new ContainerEntry { Key = scenes[index], FileId = 0, PathId = 0, Preload = preload },
		});

		var files = new List<UnityFsWriter.Entry>
		{
			Serialize(shared.file, sharedInner),
			Serialize(level.file, mount),
		};
		AddResS(files, Path.Combine(dataDir, sharedName), sharedInner);
		AddResS(files, Path.Combine(dataDir, levelName), mount);
		UnityFsWriter.Write(bundlePath, EngineVersion, files);
	}

	public void Dispose()
	{
		manager.UnloadAll(true);
	}

	private void Prepare(AssetsFileInstance inst, IList<string> scenes, string mount, string innerName)
	{
		// Stream paths are read with the original externals, so before those change.
		RedirectStreams(inst, "archive:/" + mount + "/" + innerName + ".resS");
		FixScriptAssemblyNames(inst);
		FixEulerBindings(inst);
		ConvertShaders(inst);
		AddTypeTrees(inst);
		foreach (AssetsFileExternal external in inst.file.Metadata.Externals)
		{
			external.PathName = RemapExternal(external.PathName, scenes);
		}
	}

	private static string RemapExternal(string pathName, IList<string> scenes)
	{
		string name = Path.GetFileName(pathName);
		if (name == "unity default resources")
		{
			return DefaultResources;
		}
		if (name == "unity_builtin_extra")
		{
			return BuiltinExtra;
		}
		return ArchivePath(name, scenes);
	}

	private static AssetsFileExternal External(string path, GUID128 guid = default, AssetsFileExternalType type = AssetsFileExternalType.Normal)
	{
		return new AssetsFileExternal { VirtualAssetPathName = "", Guid = guid, Type = type, PathName = path, OriginalPathName = path };
	}

	// What the scene references outside itself (scripts above all: the 5.6 player had them all loaded at start, so its
	// PreloadData leaves them out), as (file id, path id) seen from the scene's shared file, which gets the externals it
	// lacks.
	private IEnumerable<(int, long)> SceneReferences(AssetsFileInstance level, AssetsFileInstance shared, string sharedPath)
	{
		var references = new HashSet<(int, long)>();
		foreach (AssetPPtr script in level.file.Metadata.ScriptTypes)
		{
			references.Add((script.FileId, script.PathId));
		}
		foreach (AssetFileInfo info in level.file.AssetInfos)
		{
			foreach ((int fileId, long pathId) in References(manager.GetBaseField(level, info)))
			{
				if (fileId != 0 && pathId != 0)
				{
					references.Add((fileId, pathId));
				}
			}
		}
		List<AssetsFileExternal> externals = shared.file.Metadata.Externals;
		foreach ((int fileId, long pathId) in references.OrderBy(r => r.Item1).ThenBy(r => r.Item2))
		{
			AssetsFileExternal external = level.file.Metadata.Externals[fileId - 1];
			if (external.PathName == sharedPath)
			{
				yield return (0, pathId);
				continue;
			}
			int index = externals.FindIndex(e => e.PathName == external.PathName);
			if (index < 0)
			{
				externals.Add(External(external.PathName, external.Guid, external.Type));
				index = externals.Count - 1;
			}
			yield return (index + 1, pathId);
		}
	}

	// An object and everything it references in its own file, with the objects in other files it references; all as
	// (file id, path id) seen from its file, the object first.
	private List<(int, long)> Dependencies(AssetsFileInstance inst, long pathId)
	{
		var result = new List<(int, long)>();
		var seen = new HashSet<(int, long)>();
		var pending = new Stack<long>();
		pending.Push(pathId);
		seen.Add((0, pathId));
		while (pending.Count > 0)
		{
			long current = pending.Pop();
			result.Add((0, current));
			AssetFileInfo info = inst.file.GetAssetInfo(current);
			if (info == null)
			{
				continue;
			}
			foreach ((int fileId, long target) in References(manager.GetBaseField(inst, info)))
			{
				if (target == 0 || !seen.Add((fileId, target)))
				{
					continue;
				}
				if (fileId == 0)
				{
					pending.Push(target);
				}
				else
				{
					result.Add((fileId, target));
				}
			}
		}
		return result;
	}

	private static IEnumerable<(int, long)> References(AssetTypeValueField field)
	{
		if (field.TypeName.StartsWith("PPtr<", StringComparison.Ordinal))
		{
			yield return (field["m_FileID"].AsInt, field["m_PathID"].AsLong);
			yield break;
		}
		if (field.Children == null)
		{
			yield break;
		}
		foreach (AssetTypeValueField child in field.Children)
		{
			foreach ((int, long) reference in References(child))
			{
				yield return reference;
			}
		}
	}

	// Keeps the scene's PreloadData (Unity loads it with the scene) with the full list, moved off path id 1, which the
	// AssetBundle object takes.
	private void MovePreloadData(AssetsFileInstance inst, List<(int FileId, long PathId)> assets)
	{
		AssetsFile file = inst.file;
		AssetFileInfo info = file.GetAssetsOfType(AssetClassID.PreloadData).FirstOrDefault();
		if (info == null)
		{
			return;
		}
		AssetTypeValueField field = manager.GetBaseField(inst, info);
		AssetTypeValueField array = field["m_Assets"]["Array"];
		array.Children.Clear();
		foreach ((int fileId, long pathId) in assets)
		{
			AssetTypeValueField pptr = ValueBuilder.DefaultValueFieldFromArrayTemplate(array);
			pptr["m_FileID"].AsInt = fileId;
			pptr["m_PathID"].AsLong = pathId;
			array.Children.Add(pptr);
		}
		long pathIdNew = file.AssetInfos.Max(a => a.PathId) + 1;
		file.Metadata.RemoveAssetInfo(info);
		file.Metadata.AddAssetInfo(new AssetFileInfo
		{
			PathId = pathIdNew,
			TypeIdOrIndex = info.TypeIdOrIndex,
			TypeId = info.TypeId,
			ScriptTypeIndex = info.ScriptTypeIndex,
			Replacer = new ContentReplacerFromBuffer(field.WriteToByteArray()),
		});
	}

	// Components whose script the game's own build had already lost: a MonoBehaviour with a null m_Script and no fields
	// (the factory templates StorageLocator, Construction, Lumber, Mine, Repair, ... and a few objects in the menus). The
	// 5.6 player warned about them too ("The referenced script on this Behaviour ... is missing!"); each load and each
	// Instantiate of their object logs it again, with a stack trace. They do nothing (no class, so nothing can get them;
	// the game skips null components), so the scene leaves them out and their game objects no longer list them. Only in
	// the scene file: nothing outside a scene can point at its objects, so no other file or preload list is affected.
	private void RemoveMissingScripts(AssetsFileInstance inst)
	{
		AssetsFile file = inst.file;
		var removed = new HashSet<long>();
		foreach (AssetFileInfo info in file.GetAssetsOfType(AssetClassID.MonoBehaviour).ToList())
		{
			AssetTypeValueField script = manager.GetBaseField(inst, info, AssetReadFlags.SkipMonoBehaviourFields)["m_Script"];
			if (script["m_FileID"].AsInt == 0 && script["m_PathID"].AsLong == 0)
			{
				removed.Add(info.PathId);
				file.Metadata.RemoveAssetInfo(info);
			}
		}
		if (removed.Count == 0)
		{
			return;
		}
		foreach (AssetFileInfo info in file.GetAssetsOfType(AssetClassID.GameObject))
		{
			AssetTypeValueField field = manager.GetBaseField(inst, info);
			List<AssetTypeValueField> components = field["m_Component"]["Array"].Children;
			int count = components.Count;
			components.RemoveAll(pair => pair["component"]["m_FileID"].AsInt == 0 && removed.Contains(pair["component"]["m_PathID"].AsLong));
			if (components.Count != count)
			{
				info.SetNewData(field);
			}
		}
	}

	private static void RemovePreloadData(AssetsFile file)
	{
		AssetFileInfo first = file.GetAssetInfo(1);
		if (first != null && first.GetTypeId(file) == (int)AssetClassID.PreloadData)
		{
			file.Metadata.RemoveAssetInfo(first);
		}
	}

	// Unity 5.6 names a script's assembly "Assembly-CSharp.dll", Unity 6 "Assembly-CSharp"; with the old name no script
	// binds to its class.
	private void FixScriptAssemblyNames(AssetsFileInstance inst)
	{
		foreach (AssetFileInfo info in inst.file.GetAssetsOfType(AssetClassID.MonoScript))
		{
			AssetTypeValueField field = manager.GetBaseField(inst, info);
			string assembly = field["m_AssemblyName"].AsString;
			if (assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
			{
				field["m_AssemblyName"].AsString = assembly.Substring(0, assembly.Length - 4);
				info.SetNewData(field);
			}
		}
	}

	// An animation clip's Euler rotation curve binds to its transform with custom type 14 in Unity 5.6 and 4 in Unity 6
	// (the other custom types, light, material, particles, muscles, kept their numbers). With 14 Unity 6 binds nothing:
	// the sky's orbit clips left the sun lying flat at the horizon, and the ground lit only from the side.
	private const byte OldEulerBinding = 14;

	private const byte EulerBinding = 4;

	private void FixEulerBindings(AssetsFileInstance inst)
	{
		foreach (AssetFileInfo info in inst.file.GetAssetsOfType(AssetClassID.AnimationClip))
		{
			AssetTypeValueField field = manager.GetBaseField(inst, info);
			bool changed = false;
			foreach (AssetTypeValueField binding in field["m_ClipBindingConstant"]["genericBindings"]["Array"].Children)
			{
				if (binding["typeID"].AsInt == (int)AssetClassID.Transform && binding["attribute"].AsUInt == 4 && binding["customType"].AsByte == OldEulerBinding)
				{
					binding["customType"].AsByte = EulerBinding;
					changed = true;
				}
			}
			if (changed)
			{
				info.SetNewData(field);
			}
		}
	}

	private void RedirectStreams(AssetsFileInstance inst, string resS)
	{
		AssetsFile file = inst.file;
		foreach (AssetFileInfo info in file.AssetInfos)
		{
			int typeId = info.GetTypeId(file);
			if (typeId != (int)AssetClassID.Texture2D && typeId != (int)AssetClassID.Mesh && typeId != (int)AssetClassID.AudioClip
				&& typeId != (int)AssetClassID.Cubemap && typeId != (int)AssetClassID.Texture3D && typeId != (int)AssetClassID.Texture2DArray)
			{
				continue;
			}
			AssetTypeValueField field = manager.GetBaseField(inst, info);
			AssetTypeValueField holder = typeId == (int)AssetClassID.AudioClip ? field["m_Resource"] : field["m_StreamData"];
			AssetTypeValueField stream = holder.IsDummy ? holder : holder[typeId == (int)AssetClassID.AudioClip ? "m_Source" : "path"];
			if (stream.IsDummy || string.IsNullOrEmpty(stream.AsString))
			{
				continue;
			}
			stream.AsString = resS;
			info.SetNewData(field);
		}
	}

	public ShaderConverter Shaders => shaders;

	private void ConvertShaders(AssetsFileInstance inst)
	{
		if (shaders == null)
		{
			return;
		}
		foreach (AssetFileInfo info in inst.file.GetAssetsOfType(AssetClassID.Shader))
		{
			info.SetNewData(shaders.Convert(manager.GetBaseField(inst, info)));
		}
	}

	private void AddTypeTrees(AssetsFileInstance inst)
	{
		AssetsFile file = inst.file;
		List<TypeTreeType> types = file.Metadata.TypeTreeTypes;
		for (int i = 0; i < types.Count; i++)
		{
			TypeTreeType old = types[i];
			if (old.TypeId == (int)AssetClassID.Shader && shaders != null)
			{
				types[i] = WithBlob(old.TypeId, old.ScriptTypeIndex, old.ScriptIdHash, shaders.TypeHash, shaders.TypeTree);
				continue;
			}
			TypeTreeType tree = old.TypeId == (int)AssetClassID.MonoBehaviour && old.ScriptTypeIndex != ushort.MaxValue
				? ScriptTree(inst, old.ScriptTypeIndex)
				: ClassTree(old.TypeId);
			types[i] = WithBlob(old.TypeId, old.ScriptTypeIndex, old.ScriptIdHash, old.TypeHash, tree);
			types[i].IsStrippedType = old.IsStrippedType;
		}
		file.Metadata.TypeTreeEnabled = true;
	}

	private static TypeTreeType WithBlob(int typeId, ushort scriptIndex, Hash128 scriptHash, Hash128 typeHash, TypeTreeType tree)
	{
		return new TypeTreeType
		{
			TypeId = typeId,
			ScriptTypeIndex = scriptIndex,
			ScriptIdHash = scriptHash,
			TypeHash = typeHash,
			ExtTypeHash = Hash128.NewBlankHash(),
			TypeBlobIsDefinition = true,
			TypeDependencies = new int[0],
			TypeBlob = tree.TypeBlob,
		};
	}

	private TypeTreeType ClassTree(int classId)
	{
		if (!classTrees.TryGetValue(classId, out TypeTreeType tree))
		{
			tree = TypeTrees.FromClass(classes, classId) ?? throw new InvalidDataException("No 5.6 type tree for class " + classId);
			classTrees[classId] = tree;
		}
		return tree;
	}

	private TypeTreeType ScriptTree(AssetsFileInstance inst, ushort scriptIndex)
	{
		AssetPPtr script = inst.file.Metadata.ScriptTypes[scriptIndex];
		AssetExternal monoScript = manager.GetExtAsset(inst, script.FileId, script.PathId);
		if (monoScript.baseField == null)
		{
			throw new InvalidDataException("MonoScript " + script.FileId + ":" + script.PathId + " of " + inst.name + " not found");
		}
		string assembly = monoScript.baseField["m_AssemblyName"].AsString;
		if (!assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
		{
			assembly += ".dll";
		}
		string nameSpace = monoScript.baseField["m_Namespace"].AsString;
		string className = monoScript.baseField["m_ClassName"].AsString;
		string key = assembly + "|" + nameSpace + "|" + className;
		if (!scriptTrees.TryGetValue(key, out TypeTreeType tree))
		{
			var template = new AssetTypeTemplateField();
			template.FromClassDatabase(classes, classes.FindAssetClassByID((int)AssetClassID.MonoBehaviour));
			template = manager.MonoTempGenerator.GetTemplateField(template, assembly, nameSpace, className, new UnityVersion(EngineVersion));
			tree = TypeTrees.FromTemplate(template, (int)AssetClassID.MonoBehaviour);
			scriptTrees[key] = tree;
		}
		return tree;
	}

	// An empty serialized file of the same format as another, for an AssetBundle object of its own.
	private static AssetsFile NewFile(AssetsFile like)
	{
		return new AssetsFile
		{
			Header = new AssetsFileHeader { Version = like.Header.Version },
			Metadata = new AssetsFileMetadata
			{
				UnityVersion = EngineVersion,
				TargetPlatform = like.Metadata.TargetPlatform,
				TypeTreeEnabled = true,
				TypeTreeTypes = new List<TypeTreeType>(),
				AssetInfos = new List<AssetFileInfo>(),
				ScriptTypes = new List<AssetPPtr>(),
				Externals = new List<AssetsFileExternal>(),
				RefTypes = new List<TypeTreeType>(),
				UserInformation = "",
			},
		};
	}

	private void AddAssetBundle(AssetsFile file, string name, bool scene, IList<ContainerEntry> entries)
	{
		List<TypeTreeType> types = file.Metadata.TypeTreeTypes;
		int typeIndex = types.FindIndex(t => t.TypeId == (int)AssetClassID.AssetBundle);
		if (typeIndex < 0)
		{
			types.Add(WithBlob((int)AssetClassID.AssetBundle, ushort.MaxValue, Hash128.NewBlankHash(), Hash128.NewBlankHash(), ClassTree((int)AssetClassID.AssetBundle)));
			typeIndex = types.Count - 1;
		}

		var template = new AssetTypeTemplateField();
		template.FromClassDatabase(classes, classes.FindAssetClassByID((int)AssetClassID.AssetBundle));
		AssetTypeValueField bundle = ValueBuilder.DefaultValueFieldFromTemplate(template);
		bundle["m_Name"].AsString = name;
		bundle["m_AssetBundleName"].AsString = name;
		bundle["m_RuntimeCompatibility"].AsUInt = 1;
		bundle["m_IsStreamedSceneAssetBundle"].AsBool = scene;

		AssetTypeValueField preload = bundle["m_PreloadTable"]["Array"];
		AssetTypeValueField container = bundle["m_Container"]["Array"];
		foreach (ContainerEntry entry in entries)
		{
			int start = preload.Children.Count;
			foreach ((int fileId, long pathId) in entry.Preload)
			{
				AssetTypeValueField pptr = ValueBuilder.DefaultValueFieldFromArrayTemplate(preload);
				pptr["m_FileID"].AsInt = fileId;
				pptr["m_PathID"].AsLong = pathId;
				preload.Children.Add(pptr);
			}
			AssetTypeValueField pair = ValueBuilder.DefaultValueFieldFromArrayTemplate(container);
			pair["first"].AsString = entry.Key;
			pair["second"]["preloadIndex"].AsInt = start;
			pair["second"]["preloadSize"].AsInt = entry.Preload.Count;
			pair["second"]["asset"]["m_FileID"].AsInt = entry.PathId == 0 ? 0 : entry.FileId;
			pair["second"]["asset"]["m_PathID"].AsLong = entry.PathId;
			container.Children.Add(pair);
		}

		if (file.GetAssetInfo(1) != null)
		{
			throw new InvalidDataException("Path id 1 in " + name + " is taken");
		}
		file.Metadata.AddAssetInfo(new AssetFileInfo
		{
			PathId = 1,
			TypeIdOrIndex = typeIndex,
			TypeId = (int)AssetClassID.AssetBundle,
			ScriptTypeIndex = ushort.MaxValue,
			Replacer = new ContentReplacerFromBuffer(bundle.WriteToByteArray()),
		});
	}

	private static UnityFsWriter.Entry Serialize(AssetsFile file, string name)
	{
		var data = new MemoryStream();
		using (var writer = new AssetsFileWriter(data))
		{
			file.Write(writer);
		}
		return new UnityFsWriter.Entry { Name = name, Data = data.ToArray(), Serialized = true };
	}

	private static void AddResS(List<UnityFsWriter.Entry> entries, string sourcePath, string innerName)
	{
		if (File.Exists(sourcePath + ".resS"))
		{
			entries.Add(new UnityFsWriter.Entry { Name = innerName + ".resS", SourcePath = sourcePath + ".resS" });
		}
	}
}
