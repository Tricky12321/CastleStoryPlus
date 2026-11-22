using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using CastleStoryPlus.Building;
using CastleStoryPlus.Core;
using HarmonyLib;
using NUnit.Framework;

namespace CastleStoryPlus.Tests;

// Audit of what a save needs from the mod (#185): the mod's buildings, blocks and items must be added to the game's
// factories whatever features are switched on, or a save holding them loses them (and a feature that uses them breaks).
// Reads the patch and feature attributes without patching anything (local only, like the Harmony patch audit).
//   - every Factory.Awake patch belongs to an always-on class, unless it only changes templates (a reviewed list),
//   - every mod building (a class with a CustomBuilding) is added by SavedContent,
//   - the blueprint mapping guard and the other patches saves depend on are always on.
[TestFixture]
public class SavedContentAuditTests
{
	// Factory.Awake postfixes of a named feature that add no factory keys, only change the game's (or the mod's)
	// templates. Key: patch class full name, value: why it may follow its feature's switch.
	private static readonly Dictionary<string, string> TemplateOnlyPatches = new Dictionary<string, string>
	{
		{ "CastleStoryPlus.Metallurgy.MetallurgyFactoryPatch", "ingot looks and furnace/forge room; the coal and steel items come from SavedContent" },
		{ "CastleStoryPlus.Building.WorkSpotReserve", "blueprints claim their work spot; adds no keys" },
	};

	// Patch classes a saved world needs whatever is switched on: they must be always on ([Feature] without a name).
	private static readonly string[] SaveCriticalPatches = new string[]
	{
		"CastleStoryPlus.Core.SavedContent",
		"CastleStoryPlus.Core.BlueprintMappingGuard",
		"CastleStoryPlus.Army.KitBundleFactoryPatch",
		"CastleStoryPlus.Building.CustomBlocksMaterialPatch",
		"CastleStoryPlus.Building.CustomBlocksPlacedSupportPatch",
		"CastleStoryPlus.Nature.SaplingsSave",
		"CastleStoryPlus.Upgrades.TeamUpgradesSave",
		"CastleStoryPlus.Saving.UnknownBlocks",
	};

	private static Assembly ModAssembly => typeof(Plugin).Assembly;

	private static List<Type> _types;

	[OneTimeSetUp]
	public void Collect()
	{
		AppDomain.CurrentDomain.AssemblyResolve += ResolveFromGameFolders;
		try
		{
			_types = ModAssembly.GetTypes().ToList();
		}
		catch (ReflectionTypeLoadException ex)
		{
			Assert.Fail("Types of the mod could not be loaded (a missing game assembly?):\n" + string.Join("\n", ex.LoaderExceptions.Select(e => e.Message).Distinct().ToArray()));
		}
	}

	[OneTimeTearDown]
	public void Release()
	{
		AppDomain.CurrentDomain.AssemblyResolve -= ResolveFromGameFolders;
	}

	// Assemblies of the game that are not next to the tests (written by the build to gamefolders.txt).
	private static Assembly ResolveFromGameFolders(object sender, ResolveEventArgs args)
	{
		string file = Path.Combine(AppContext.BaseDirectory, "gamefolders.txt");
		if (!File.Exists(file))
		{
			return null;
		}
		string name = new AssemblyName(args.Name).Name + ".dll";
		foreach (string folder in File.ReadAllLines(file))
		{
			string path = Path.Combine(folder.Trim(), name);
			if (folder.Trim().Length > 0 && File.Exists(path))
			{
				return Assembly.LoadFrom(path);
			}
		}
		return null;
	}

	[Test]
	public void EveryFactoryAwakePatchThatCanAddKeysIsAlwaysOn()
	{
		List<Type> patches = PatchesOf("Factory", "Awake");
		Assert.That(patches.Select(p => p.FullName), Does.Contain("CastleStoryPlus.Core.SavedContent"), "SavedContent no longer patches Factory.Awake");
		List<string> failures = new List<string>();
		foreach (Type patch in patches)
		{
			string feature = FeatureOf(patch);
			if (feature != null && !TemplateOnlyPatches.ContainsKey(patch.FullName))
			{
				failures.Add(patch.FullName + " (feature " + feature + ")");
			}
		}
		Assert.That(failures, Is.Empty, "Factory.Awake patches that follow a feature's switch: a save with what they add does not load with the feature off. Add it to SavedContent (always on), or, if it adds no factory keys, to TemplateOnlyPatches with the reason:\n" + string.Join("\n", failures.ToArray()));
	}

	[Test]
	public void TheTemplateOnlyListHasNoStaleEntries()
	{
		HashSet<string> patches = new HashSet<string>(PatchesOf("Factory", "Awake").Select(p => p.FullName));
		List<string> stale = TemplateOnlyPatches.Keys.Where(k => !patches.Contains(k)).ToList();
		Assert.That(stale, Is.Empty, "TemplateOnlyPatches lists classes that no longer patch Factory.Awake:\n" + string.Join("\n", stale.ToArray()));
	}

	[Test]
	public void EveryModBuildingIsAddedBySavedContent()
	{
		HashSet<string> added = new HashSet<string>(DelegatesMadeIn(typeof(SavedContent), "Postfix").Select(m => m.DeclaringType.FullName + "." + m.Name));
		List<string> owners = new List<string>();
		foreach (Type type in _types)
		{
			if (type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Any(f => f.FieldType == typeof(CustomBuilding)))
			{
				owners.Add(type.FullName);
			}
		}
		Assert.That(owners.Count, Is.GreaterThanOrEqualTo(5), "Classes with a CustomBuilding (market, warehouse, upgrades, saplings, ...): " + string.Join(", ", owners.ToArray()));
		owners.Add("CastleStoryPlus.Building.LargeStockpile");
		owners.Add("CastleStoryPlus.Building.CustomBlocks");
		List<string> missing = owners.Where(o => !added.Contains(o + ".AddToFactories")).ToList();
		Assert.That(missing, Is.Empty, "Mod buildings and blocks not added to the factories by SavedContent (always on):\n" + string.Join("\n", missing.ToArray()) + "\nSavedContent adds: " + string.Join(", ", added.ToArray()));
	}

	[Test]
	public void TheBlueprintMappingGuardIsAlwaysOn()
	{
		List<Type> patches = PatchesOf("BlueprintMapper", "AddMappings");
		Assert.That(patches, Is.Not.Empty, "Nothing guards BlueprintMapper.AddMappings against the mod's buildings mapped twice");
		List<string> gated = patches.Where(p => FeatureOf(p) != null).Select(p => p.FullName + " (feature " + FeatureOf(p) + ")").ToList();
		Assert.That(gated, Is.Empty, "BlueprintMapper.AddMappings patches that follow a feature's switch:\n" + string.Join("\n", gated.ToArray()));
	}

	[Test]
	public void ThePatchesSavesDependOnAreAlwaysOn()
	{
		List<string> failures = new List<string>();
		foreach (string name in SaveCriticalPatches)
		{
			Type type = ModAssembly.GetType(name);
			if (type == null)
			{
				failures.Add(name + ": not found");
				continue;
			}
			FeatureAttribute feature = (FeatureAttribute)Attribute.GetCustomAttribute(type, typeof(FeatureAttribute));
			if (feature == null)
			{
				failures.Add(name + ": no [Feature] (never patched)");
			}
			else if (feature.Name != null)
			{
				failures.Add(name + ": follows the switch of " + feature.Name);
			}
		}
		Assert.That(failures, Is.Empty, "Patches a saved world needs, whatever is switched on:\n" + string.Join("\n", failures.ToArray()));
	}

	// The mod's patch classes on the target (by the game type's name and the method's), with a [Feature] (so patched).
	private static List<Type> PatchesOf(string typeName, string methodName)
	{
		List<Type> found = new List<Type>();
		foreach (Type type in _types)
		{
			if (Attribute.GetCustomAttribute(type, typeof(FeatureAttribute)) == null)
			{
				continue;
			}
			Type declaring = null;
			string method = null;
			foreach (HarmonyPatch attribute in type.GetCustomAttributes(typeof(HarmonyPatch), false).Cast<HarmonyPatch>())
			{
				declaring = attribute.info.declaringType ?? declaring;
				method = attribute.info.methodName ?? method;
			}
			if (declaring != null && declaring.Name == typeName && method == methodName)
			{
				found.Add(type);
			}
		}
		return found;
	}

	private static string FeatureOf(Type type)
	{
		FeatureAttribute feature = (FeatureAttribute)Attribute.GetCustomAttribute(type, typeof(FeatureAttribute));
		return feature?.Name;
	}

	// The methods the method makes delegates of (ldftn), read from its IL.
	private static List<MethodBase> DelegatesMadeIn(Type type, string methodName)
	{
		MethodInfo method = type.GetMethod(methodName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
		Assert.That(method, Is.Not.Null, type.FullName + "." + methodName + " not found");
		byte[] il = method.GetMethodBody().GetILAsByteArray();
		List<MethodBase> found = new List<MethodBase>();
		for (int i = 0; i + 5 < il.Length; i++)
		{
			// ldftn is 0xFE 0x06 followed by a method token (table 0x06 MethodDef or 0x0A MemberRef).
			if (il[i] != 0xFE || il[i + 1] != 0x06 || (il[i + 5] != 0x06 && il[i + 5] != 0x0A))
			{
				continue;
			}
			int token = BitConverter.ToInt32(il, i + 2);
			try
			{
				found.Add(type.Module.ResolveMethod(token));
			}
			catch (ArgumentException)
			{
				// Not a token after all (the bytes of another instruction's operand).
			}
		}
		return found;
	}
}
