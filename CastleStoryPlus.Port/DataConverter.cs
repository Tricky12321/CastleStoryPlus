using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CastleStoryPlus.Port;

// Converts all of the game's data files (Castle Story_Data) into bundles plus a catalog the player reads at start:
//   bundle <file name>                 every bundle, to load before anything else
//   scene <build index> <scene name>   the scenes, in build order
//   resource <path> <bundle>           Resources.Load paths and the bundle that has them under that name
public static class DataConverter
{
	public const string CatalogName = "catalog.txt";
	// The graphics APIs the converted shaders have programs for, a line each (Unity's GraphicsDeviceType names:
	// OpenGLCore, Vulkan, Direct3D11). Vulkan only when every shader has its Vulkan programs.
	public const string GraphicsName = "graphics.txt";

	// engineTypes: the player's engine-types.assets, for converting the shaders to the player's format.
	public static void ConvertAll(string classPackage, string dataDir, string outDir, Action<string> log, string engineTypes = null)
	{
		Directory.CreateDirectory(outDir);
		// A fresh conversion: bundles of an earlier one (perhaps of another layout) must not linger.
		foreach (string old in Directory.GetFiles(outDir, "*.bundle").Concat(Directory.GetFiles(outDir, CatalogName)).Concat(Directory.GetFiles(outDir, GraphicsName)))
		{
			File.Delete(old);
		}
		using (var converter = new BundleConverter(classPackage, dataDir, engineTypes))
		{
			IList<string> scenes = converter.ReadScenes();
			var resources = converter.ReadResources();
			var catalog = new List<string>();

			var shared = Directory.GetFiles(dataDir)
				.Select(Path.GetFileName)
				.Where(n => n == "resources.assets" || n == "globalgamemanagers.assets")
				.OrderBy(n => n, StringComparer.Ordinal);
			foreach (string file in shared)
			{
				string bundle = BundleConverter.MountOf(file, scenes) + ".bundle";
				var container = resources.Where(r => r.File == file).Select(r => (r.Path, r.PathId)).ToList();
				if (file == "globalgamemanagers.assets")
				{
					// The MonoScripts, which the player loads at start ("monoscript/<path id>").
					container.AddRange(converter.ObjectsOfType(file, AssetsTools.NET.Extra.AssetClassID.MonoScript).Select(id => ("monoscript/" + id, id)));
				}
				log(file + " -> " + bundle + (container.Count > 0 ? " (" + container.Count + " resources)" : ""));
				converter.ConvertShared(file, scenes, Path.Combine(outDir, bundle), container);
				catalog.Add("bundle " + bundle);
				foreach ((string path, long _) in container.Where(c => !c.Item1.StartsWith("monoscript/", StringComparison.Ordinal)))
				{
					catalog.Add("resource " + path + " " + bundle);
				}
			}
			for (int i = 0; i < scenes.Count; i++)
			{
				string file = "level" + i;
				string bundle = BundleConverter.MountOf(file, scenes) + ".bundle";
				log(file + " + sharedassets" + i + ".assets -> " + bundle);
				converter.ConvertScene(i, scenes, Path.Combine(outDir, bundle));
				catalog.Add("bundle " + bundle);
				catalog.Add("scene " + i + " " + BundleConverter.SceneName(scenes[i]));
			}
			foreach (var other in resources.Where(r => r.File != "resources.assets" && r.File != "globalgamemanagers.assets"))
			{
				log("resource " + other.Path + " is in " + other.File + ", not loadable");
			}
			File.WriteAllLines(Path.Combine(outDir, CatalogName), catalog);
			if (converter.Shaders != null)
			{
				ShaderConverter shaders = converter.Shaders;
				log("shaders: " + shaders.Converted + " converted, " + shaders.WithoutPrograms + " without programs for the player's graphics API");
				foreach (string line in shaders.Report.Distinct())
				{
					log("  " + line);
				}
				List<string> apis = shaders.GraphicsApis.ToList();
				if (shaders.Platforms.Contains((uint)ShaderConverter.VulkanPlatform) && !apis.Contains("Vulkan"))
				{
					log("graphics: Vulkan left out, " + shaders.VulkanFallbacks.Count + " shaders have no Vulkan programs (" + string.Join(", ", shaders.VulkanFallbacks) + ")");
				}
				log("graphics: " + string.Join(", ", apis));
				File.WriteAllLines(Path.Combine(outDir, GraphicsName), apis);
			}
		}
	}
}
