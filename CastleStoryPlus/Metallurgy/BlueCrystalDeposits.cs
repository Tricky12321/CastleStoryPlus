using System.Collections.Generic;
using Brix.Engine;
using Brix.External.Factories;
using Brix.Game.AI;
using CastleStoryPlus.Core;
using Brix.Assets;
using Brix.Game.Semantique;
using Brix.Lua;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.Metallurgy;

// Blue crystal veins in the deep rock of every map. The game has a blue crystal noise, but every preset turns it off
// ("blueCrystalDeeplayer": false), and even turned on it gave next to nothing: it is first in the list of deep
// resources (the last one that matches a block wins, so iron, brimstone and stone painted over it), only in the top
// 10 layers of the deep rock, and only at the very top of its noise (above 0.715). Now its noise is moved to the end
// of the list (after stone, so it wins where it matches), at any depth, above Threshold. The map's noise.json is
// still written with the game's own entry. Every machine generates the deep rock itself from the shared seed, so this
// runs on the host and on clients alike (clients need the mod too). The share of the deep rock each noise takes is
// sampled and logged when a map loads.
[Feature(Features.BlueCrystalDeposits, Features.BlueCrystalDepositsInfo)]
[HarmonyPatch(typeof(GameLoader), "ReadPresetDeepResourcesSettings")]
internal static class BlueCrystalDeposits
{
	internal const int BlueType = (int)VoxelTerrainType.BlueCrystal;

	private const float Threshold = 0.65f;

	private const string DefaultNoise = "Noise.BlueCrystalNoise";

	private const int Samples = 4000;

	// The game's entry (written to noise.json) and where it was, and the entry used instead.
	internal static VoxelNoiseConfigurator.NoiseConfig Original;

	internal static int OriginalIndex = -1;

	internal static VoxelNoiseConfigurator.NoiseConfig Replacement;

	private static void Postfix()
	{
		VoxelNoiseConfigurator.NoiseConfig config = VoxelNoiseConfigurator.GetNoiseConfigFromVoxelTerrainType(VoxelTerrainType.BlueCrystal);
		if (config != null && !config.Enabled)
		{
			config.Enabled = true;
			Plugin.Log.LogInfo("BlueCrystalDeposits: blue crystal veins turned on");
		}
	}

	// Before the noises are made: the game's entry is replaced by one at the end of the list.
	internal static void Replace(VoxelNoiseConfigurator configurator)
	{
		List<VoxelNoiseConfigurator.NoiseConfig> list = configurator.NoiseConfigList;
		if (list == null)
		{
			return;
		}
		if (Replacement != null)
		{
			list.Remove(Replacement);
		}
		Original = null;
		OriginalIndex = list.FindIndex((VoxelNoiseConfigurator.NoiseConfig c) => c.Type == BlueType);
		if (OriginalIndex >= 0)
		{
			Original = list[OriginalIndex];
			list.RemoveAt(OriginalIndex);
		}
		Replacement = new VoxelNoiseConfigurator.NoiseConfig
		{
			Enabled = true,
			Type = BlueType,
			NoiseAssetKey = (Original != null && !string.IsNullOrEmpty(Original.NoiseAssetKey)) ? Original.NoiseAssetKey : DefaultNoise,
			Threshold = Threshold,
			UseRange = false
		};
		list.Add(Replacement);
	}

	// How much of the deep rock each noise would take on its own (random points, depth ranges left out).
	internal static void LogShares(VoxelNoiseConfigurator configurator)
	{
		if (configurator.NoiseConfigList == null)
		{
			return;
		}
		System.Random random = new System.Random(1);
		List<string> parts = new List<string>();
		foreach (VoxelNoiseConfigurator.NoiseConfig config in configurator.NoiseConfigList)
		{
			if (config == null || config.NoiseComponent == null)
			{
				continue;
			}
			int hits = 0;
			for (int i = 0; i < Samples; i++)
			{
				Vector3 point = new Vector3(random.Next(0, 512), random.Next(0, 128), random.Next(0, 512));
				if (config.NoiseComponent.GetValue(point) > config.Threshold)
				{
					hits++;
				}
			}
			parts.Add((VoxelTerrainType)config.Type + " " + (100f * hits / Samples).ToString("0.0") + "%" + (config.Enabled ? string.Empty : " (off)"));
		}
		Plugin.Log.LogInfo("Deep rock noise shares (in list order, the last match wins): " + string.Join(", ", parts.ToArray()));
	}
}

[Feature(Features.BlueCrystalDeposits, Features.BlueCrystalDepositsInfo)]
[HarmonyPatch(typeof(VoxelNoiseConfigurator), nameof(VoxelNoiseConfigurator.Init))]
internal static class BlueCrystalNoisePatch
{
	private static void Prefix(out bool __state)
	{
		VoxelNoiseConfigurator configurator = VoxelNoiseConfigurator.Instance;
		__state = configurator != null && !configurator.initialized;
		if (__state)
		{
			BlueCrystalDeposits.Replace(configurator);
		}
	}

	private static void Postfix(bool __state)
	{
		if (__state && VoxelNoiseConfigurator.Instance != null)
		{
			BlueCrystalDeposits.LogShares(VoxelNoiseConfigurator.Instance);
		}
	}
}

// The map's noise.json is written with the game's own blue crystal entry.
[Feature(Features.BlueCrystalDeposits, Features.BlueCrystalDepositsInfo)]
[HarmonyPatch(typeof(Asset_Map), "Disk_WriteNoiseData")]
internal static class BlueCrystalNoiseSavePatch
{
	private static void Prefix(out int __state)
	{
		__state = -1;
		List<VoxelNoiseConfigurator.NoiseConfig> list = (VoxelNoiseConfigurator.Instance != null) ? VoxelNoiseConfigurator.Instance.NoiseConfigList : null;
		if (list == null || BlueCrystalDeposits.Replacement == null)
		{
			return;
		}
		__state = list.IndexOf(BlueCrystalDeposits.Replacement);
		if (__state < 0)
		{
			return;
		}
		list.RemoveAt(__state);
		if (BlueCrystalDeposits.Original != null)
		{
			list.Insert(Mathf.Clamp(BlueCrystalDeposits.OriginalIndex, 0, list.Count), BlueCrystalDeposits.Original);
		}
	}

	private static System.Exception Finalizer(System.Exception __exception, int __state)
	{
		if (__state >= 0)
		{
			List<VoxelNoiseConfigurator.NoiseConfig> list = VoxelNoiseConfigurator.Instance.NoiseConfigList;
			if (BlueCrystalDeposits.Original != null)
			{
				list.Remove(BlueCrystalDeposits.Original);
			}
			// Always last: it must come after stone.
			list.Add(BlueCrystalDeposits.Replacement);
		}
		return __exception;
	}
}

// Everything that can hold orange crystal can hold as much blue crystal: bricktrons and stockpiles did not take it,
// so it could neither be carried nor cleaned up.
[Feature(Features.BlueCrystalDeposits, Features.BlueCrystalDepositsInfo)]
[HarmonyPatch(typeof(LuaCrafting), nameof(LuaCrafting.Register))]
internal static class BlueCrystalCapacityPatch
{
	private static bool _done;

	private static void Postfix()
	{
		if (_done)
		{
			return;
		}
		_done = true;
		Metallurgy.MirrorCapacities("BlueCrystalDeposits: blue crystal", new Ressource[] { Adjectif.orangeCrystal }, new Ressource[] { Adjectif.blueCrystal });
	}
}
