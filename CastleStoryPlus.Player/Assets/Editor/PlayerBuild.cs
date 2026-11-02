using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Content;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace CastleStoryPlus.Player.Editor
{
	// Builds the player: -executeMethod CastleStoryPlus.Player.Editor.PlayerBuild.Linux (output in Build/Linux) or
	// ...PlayerBuild.Windows (output in Build/Windows).
	// The player holds none of the game: its only scene is an empty bootstrap scene, the game's content and assemblies
	// are made from the player's own install at first start.
	public static class PlayerBuild
	{
		private const string BootstrapScene = "Assets/Scenes/Bootstrap.unity";

		// The project settings the installer copies from the game's install into the player's globalgamemanagers, and a
		// shader of our own, for the layout and program format the installer converts the game's shaders to. The player's
		// files have no type trees, so their Unity 6 layouts are written next to them.
		private static readonly string[] TypesFiles =
		{
			"ProjectSettings/TimeManager.asset",
			"ProjectSettings/AudioManager.asset",
			"ProjectSettings/InputManager.asset",
			"ProjectSettings/Physics2DSettings.asset",
			"ProjectSettings/QualitySettings.asset",
			"ProjectSettings/DynamicsManager.asset",
			"ProjectSettings/TagManager.asset",
			"ProjectSettings/NavMeshAreas.asset",
			"Assets/Types/EngineTypes.shader",
		};
		private const string TypesName = "engine-types.assets";

		public static void Linux()
		{
			Build(BuildTarget.StandaloneLinux64, "Build/Linux/CastleStoryPlus.x86_64");
		}

		public static void Windows()
		{
			Build(BuildTarget.StandaloneWindows64, "Build/Windows/CastleStoryPlus.exe");
		}

		// A Linux player built into the folder named by CSP_PLAYER_OUT (or -playerOut <folder> on the command line), which
		// must be empty or not exist yet: a build to take single files from (CastleStoryPlus.Compute.dll and the Burst
		// library) without touching Build/Linux or an installed player.
		public static void LinuxTo()
		{
			string dir = OutputFolder();
			if (Directory.Exists(dir) && Directory.GetFileSystemEntries(dir).Length > 0)
			{
				Debug.LogError("[PlayerBuild] not empty: " + dir);
				if (Application.isBatchMode)
				{
					EditorApplication.Exit(1);
				}
				return;
			}
			Build(BuildTarget.StandaloneLinux64, Path.Combine(dir, "CastleStoryPlus.x86_64"));
		}

		private static string OutputFolder()
		{
			string[] args = System.Environment.GetCommandLineArgs();
			for (int i = 0; i + 1 < args.Length; i++)
			{
				if (args[i] == "-playerOut")
				{
					return Path.GetFullPath(args[i + 1]);
				}
			}
			string env = System.Environment.GetEnvironmentVariable("CSP_PLAYER_OUT");
			if (string.IsNullOrEmpty(env))
			{
				throw new System.ArgumentException("set CSP_PLAYER_OUT or pass -playerOut <folder>");
			}
			return Path.GetFullPath(env);
		}

		// A release starts from an empty folder (Build/Release/<platform>), never from one an install has run in, which
		// holds the game's code, settings and content. tools/package-port.sh packs it.
		public static void ReleaseLinux()
		{
			Release(BuildTarget.StandaloneLinux64, "Linux", "CastleStoryPlus.x86_64");
		}

		public static void ReleaseWindows()
		{
			Release(BuildTarget.StandaloneWindows64, "Windows", "CastleStoryPlus.exe");
		}

		// A development player (Unity's profiler can attach to it) in Build/Profiling/Linux, for measuring the port
		// without the editor; an install and BepInEx go into it as into Build/Linux.
		public static void ProfilingLinux()
		{
			string dir = Path.Combine("Build", "Profiling", "Linux");
			if (Directory.Exists(dir))
			{
				Directory.Delete(dir, true);
			}
			Build(BuildTarget.StandaloneLinux64, Path.Combine(dir, "CastleStoryPlus.x86_64"), BuildOptions.Development);
		}

		// A Linux player with Vulkan first (the default when no API is forced), in Build/VulkanTest/Linux (an empty folder
		// each time), for testing Vulkan without the mod. The project goes back to its normal list afterwards.
		public static void VulkanTestLinux()
		{
			string dir = Path.Combine("Build", "VulkanTest", "Linux");
			if (Directory.Exists(dir))
			{
				Directory.Delete(dir, true);
			}
			try
			{
				Build(BuildTarget.StandaloneLinux64, Path.Combine(dir, "CastleStoryPlus.x86_64"), BuildOptions.None, new[] { GraphicsDeviceType.Vulkan, GraphicsDeviceType.OpenGLCore });
			}
			finally
			{
				Configure();
				AssetDatabase.SaveAssets();
			}
		}

		private static void Release(BuildTarget target, string platform, string executable)
		{
			string dir = Path.Combine("Build", "Release", platform);
			if (Directory.Exists(dir))
			{
				Directory.Delete(dir, true);
			}
			Build(target, Path.Combine(dir, executable));
		}

		private static void Build(BuildTarget target, string output, BuildOptions extra = BuildOptions.None, GraphicsDeviceType[] linuxApis = null)
		{
			Configure();
			if (linuxApis != null)
			{
				PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneLinux64, linuxApis);
			}
			EnsureBootstrapScene();
			ForgetNoBurstPlugins();
			var options = new BuildPlayerOptions
			{
				scenes = new[] { BootstrapScene },
				locationPathName = output,
				target = target,
				options = extra,
			};
			var report = BuildPipeline.BuildPlayer(options);
			Debug.Log("[PlayerBuild] " + report.summary.result + " " + report.summary.totalSize + " bytes -> " + output);
			if (report.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded)
			{
				WriteTypes(Path.Combine(Path.GetDirectoryName(output), Path.GetFileNameWithoutExtension(output) + "_Data", TypesName), target);
			}
			if (Application.isBatchMode && report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
			{
				EditorApplication.Exit(1);
			}
		}

		private static void Configure()
		{
			NamedBuildTarget standalone = NamedBuildTarget.Standalone;
			PlayerSettings.companyName = "CastleStoryPlus";
			PlayerSettings.productName = "Castle Story Plus";
			PlayerSettings.SetScriptingBackend(standalone, ScriptingImplementation.Mono2x);
			PlayerSettings.SetApiCompatibilityLevel(standalone, ApiCompatibilityLevel.NET_Unity_4_8);
			PlayerSettings.SetManagedStrippingLevel(standalone, ManagedStrippingLevel.Disabled);
			// OpenGL Core first (the default): the game's shaders are GLSL, which this backend takes as they are. Vulkan as
			// well, chosen with -force-vulkan ([Graphics] Renderer in the mod): the installer converts the shaders for both
			// (5.6's SPIR-V, or the GLSL rewritten and compiled), and the player's own shader gives the Vulkan format.
			PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneLinux64, false);
			PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneLinux64, new[] { GraphicsDeviceType.OpenGLCore, GraphicsDeviceType.Vulkan });
			// Direct3D 11: the game's Windows shaders are DXBC for it (the other one, Direct3D 9, Unity 6 dropped).
			PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
			PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { GraphicsDeviceType.Direct3D11 });
			// The game's shaders and image effects are made for linear lighting.
			PlayerSettings.colorSpace = ColorSpace.Linear;
			PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
			PlayerSettings.defaultScreenWidth = 1600;
			PlayerSettings.defaultScreenHeight = 900;
			PlayerSettings.resizableWindow = true;
			PlayerSettings.runInBackground = true;
			PlayerSettings.usePlayerLog = true;
			// The game talks plain HTTP (analytics, update checks); Unity 6 refuses that by default.
			PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed;
			// Engine settings measured against each other (docs/Performance.md): the incremental garbage collector (on
			// unless CSP_GC_INCREMENTAL=0; off, the collector has no write barrier on reference stores but pauses the
			// game for a whole collection) and GPU skinning (off unless CSP_GPU_SKINNING=1; the game had it on).
			PlayerSettings.gcIncremental = System.Environment.GetEnvironmentVariable("CSP_GC_INCREMENTAL") != "0";
			PlayerSettings.gpuSkinning = System.Environment.GetEnvironmentVariable("CSP_GPU_SKINNING") == "1";
		}

		// Unity's default settings and our shader written the way the player serializes them, with type trees.
		private static void WriteTypes(string path, BuildTarget target)
		{
			var objects = new System.Collections.Generic.List<SerializationInfo>();
			foreach (string file in TypesFiles)
			{
				foreach (Object asset in AssetDatabase.LoadAllAssetsAtPath(file))
				{
					if (ObjectIdentifier.TryGetObjectIdentifier(asset, out ObjectIdentifier id))
					{
						objects.Add(new SerializationInfo { serializationObject = id, serializationIndex = objects.Count + 1 });
					}
				}
			}
			string name = Path.GetFileName(path);
			var command = new WriteCommand { fileName = name, internalName = name, serializeObjects = objects };
			var references = new BuildReferenceMap();
			references.AddMappings(name, objects.ToArray());
			var parameters = new WriteParameters
			{
				writeCommand = command,
				settings = new BuildSettings { target = target, group = BuildTargetGroup.Standalone, buildFlags = ContentBuildFlags.None },
				globalUsage = new BuildUsageTagGlobal(),
				usageSet = new BuildUsageTagSet(),
				referenceMap = references,
			};
			string temp = Path.Combine("Temp", "EngineTypes");
			Directory.CreateDirectory(temp);
			ContentBuildInterface.WriteSerializedFile(temp, parameters);
			File.Copy(Path.Combine(temp, name), path, true);
			Debug.Log("[PlayerBuild] " + objects.Count + " engine types -> " + path);
		}

		// The build program plans the copy of Burst's library (CastleStoryPlus.Compute's jobs, lib_burst_generated) from what
		// the Burst step left in Library/Bee the build before. A build from before there were jobs left a note that nothing
		// was made, and with it there the step is never run again: no library, and the jobs run as Mono code.
		private static void ForgetNoBurstPlugins()
		{
			foreach (string program in new[] { "LinuxPlayerBuildProgram", "WinPlayerBuildProgram" })
			{
				string note = Path.Combine("Library", "Bee", "artifacts", program, "AsyncPluginsFromLinker", "no_plugins_were_generated.txt");
				if (File.Exists(note))
				{
					File.Delete(note);
				}
			}
		}

		private static void EnsureBootstrapScene()
		{
			if (File.Exists(BootstrapScene))
			{
				return;
			}
			Directory.CreateDirectory(Path.GetDirectoryName(BootstrapScene));
			Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
			EditorSceneManager.SaveScene(scene, BootstrapScene);
		}
	}
}
