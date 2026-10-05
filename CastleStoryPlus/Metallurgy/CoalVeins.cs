using System;
using System.Collections.Generic;
using Brix.Assets;
using Brix.Engine;
using Brix.Engine.TerrainAtlas;
using Brix.External.Factories;
using Brix.Game.AI;
using Brix.Noise;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CastleStoryPlus.Metallurgy;

// Coal veins in the deep rock, like iron. The deep rock is not saved: every machine works it out from the map's seed
// and the deep resource noises each time a map is opened, so every map gets coal veins, also old maps and saves,
// wherever nobody has dug yet. Coal is a terrain type of its own (CoalType):
// - its block info is a copy of iron ore's (same digging time) that drops coal;
// - its look is iron ore's with dark coal specks: the free cell next to the iron specks in the terrain's overlay
//   texture is filled with the iron specks turned black;
// - its noise is iron's with another seed, before stone in the list like iron, so veins are about as common as
//   iron and never in the same place; it is left out of the map's noise.json, so a save stays as the game wrote it.
// Mines and tunnels have a coal button of their own (QuarryResources); coal is carried to storage like iron.
[Feature(Features.Metallurgy, Features.MetallurgyInfo)]
internal static class CoalVeins
{
	// Free in the block table (60 is a brick block) and in the terrain atlas.
	internal const int CoalType = 61;

	private const int IronType = (int)VoxelTerrainType.Iron;

	private const int StoneType = (int)VoxelTerrainType.Stone;

	private const float Threshold = 0.3f;

	// Added to every seed of the copied iron noise.
	private const int SeedOffset = 7919;

	// Terrain textures with the coal cell painted in, by original.
	private static readonly Dictionary<Texture, Texture2D> Painted = new Dictionary<Texture, Texture2D>();

	internal static bool IsCoalConfig(VoxelNoiseConfigurator.NoiseConfig config)
	{
		return config != null && config.Type == CoalType;
	}

	// Block info: iron ore's, with its own id and coal as what it drops.
	internal static void AddBlockInfo()
	{
		if (!BlockInfoGenerator._idToBlockInfos.TryGetValue(IronType, out BlockInfo iron) || BlockInfoGenerator._idToBlockInfos.ContainsKey(CoalType))
		{
			return;
		}
		BlockInfo coal = (BlockInfo)AccessTools.Method(typeof(object), "MemberwiseClone").Invoke(iron, null);
		coal.ID = CoalType;
		coal.ResourceKey = Metallurgy.CoalKey;
		BlockInfoGenerator._idToBlockInfos[CoalType] = coal;
		Plugin.Log.LogInfo("Metallurgy: coal terrain type " + CoalType + " added");
	}

	// Atlas entry: iron ore's base, with the coal specks cell as its special overlay.
	internal static void AddAtlasEntry(MondeAtlas atlas)
	{
		if (atlas.mapping == null || atlas.terrainType2entry != null)
		{
			return;
		}
		MondeAtlasEntry iron = null;
		foreach (MondeAtlasEntry entry in atlas.mapping)
		{
			if (entry == null)
			{
				continue;
			}
			if (entry.terrainType == CoalType)
			{
				return;
			}
			if (entry.terrainType == IronType)
			{
				iron = entry;
			}
		}
		if (iron == null || iron.visuals == null)
		{
			return;
		}
		TerrainAtlasVisualEntry visuals = ScriptableObject.CreateInstance<TerrainAtlasVisualEntry>();
		visuals.name = "Coal";
		visuals.uv = iron.visuals.uv;
		visuals.useTransitions = iron.visuals.useTransitions;
		visuals.uvTransition = iron.visuals.uvTransition;
		visuals.layer = iron.visuals.layer;
		visuals.useSpecial = true;
		visuals.uvSpecial = new Vector2(3f, iron.visuals.uvSpecial.y);
		visuals.useWeightedNoise = iron.visuals.useWeightedNoise;
		visuals.weightedNoise = iron.visuals.weightedNoise;
		Object.DontDestroyOnLoad(visuals);
		MondeAtlasEntry[] mapping = new MondeAtlasEntry[atlas.mapping.Length + 1];
		atlas.mapping.CopyTo(mapping, 0);
		mapping[mapping.Length - 1] = new MondeAtlasEntry
		{
			terrainType = CoalType,
			densityLevel = iron.densityLevel,
			visuals = visuals
		};
		atlas.mapping = mapping;
	}

	// Noise: iron's, before stone (the last match wins, and stone covers most of the deep rock).
	internal static void AddNoiseConfig(VoxelNoiseConfigurator configurator)
	{
		List<VoxelNoiseConfigurator.NoiseConfig> list = configurator.NoiseConfigList;
		if (list == null)
		{
			return;
		}
		list.RemoveAll(IsCoalConfig);
		VoxelNoiseConfigurator.NoiseConfig iron = list.Find((VoxelNoiseConfigurator.NoiseConfig c) => c.Type == IronType);
		if (iron == null)
		{
			return;
		}
		int index = list.FindIndex((VoxelNoiseConfigurator.NoiseConfig c) => c.Type == StoneType);
		list.Insert((index < 0) ? list.Count : index, new VoxelNoiseConfigurator.NoiseConfig
		{
			Enabled = true,
			Type = CoalType,
			NoiseAssetKey = iron.NoiseAssetKey,
			Threshold = Threshold,
			UseRange = iron.UseRange,
			Start = iron.Start,
			End = iron.End
		});
	}

	// The copied iron noise gets other seeds, and its modules are rebuilt from the leaves up (a combiner's refresh
	// builds itself before its parts).
	internal static void Reseed(NoiseBaseComponent component)
	{
		if (component == null)
		{
			return;
		}
		foreach (Brix.Noise.NoiseGenerator generator in component.GetComponentsInChildren<Brix.Noise.NoiseGenerator>(true))
		{
			generator.seed += SeedOffset;
		}
		Rebuild(component);
	}

	// Leaves first: a combiner, selector or modifier builds its module from its parts' modules as they are then (the
	// game's own Refresh builds the parent first, so it would keep the parts' old modules, with the old seeds).
	private static void Rebuild(NoiseBaseComponent component)
	{
		if (component == null)
		{
			return;
		}
		if (component is NoiseCombiner combiner)
		{
			Rebuild(combiner.NoiseComponentA);
			Rebuild(combiner.NoiseComponentB);
		}
		else if (component is NoiseSelector selector)
		{
			Rebuild(selector.NoiseComponentA);
			Rebuild(selector.NoiseComponentB);
			Rebuild(selector.NoiseComponentControl);
		}
		else if (component is NoiseModifier modifier)
		{
			Rebuild(modifier.NoiseComponent);
		}
		component.InitNoiseModule();
	}

	// Fills the free overlay cell next to the iron specks with the iron specks turned black, in every loaded terrain
	// material. The textures are compressed and not readable, so they are copied through a render texture.
	internal static void PaintTerrainTextures()
	{
		foreach (Material material in Resources.FindObjectsOfTypeAll<Material>())
		{
			if (material == null || !material.HasProperty("_TransTex"))
			{
				continue;
			}
			Replace(material, "_TransTex", coal: true);
			Replace(material, "_TransNormalTex", coal: false);
		}
	}

	private static void Replace(Material material, string property, bool coal)
	{
		if (!material.HasProperty(property))
		{
			return;
		}
		Texture original = material.GetTexture(property);
		if (original == null || original is Texture2D && Painted.ContainsValue((Texture2D)original))
		{
			return;
		}
		if (!Painted.TryGetValue(original, out Texture2D painted))
		{
			try
			{
				painted = Paint(original, coal);
			}
			catch (Exception ex)
			{
				Plugin.Log.LogError("Metallurgy: could not paint coal into " + original.name + ": " + ex);
				painted = null;
			}
			Painted[original] = painted;
		}
		if (painted != null)
		{
			material.SetTexture(property, painted);
		}
	}

	private static Texture2D Paint(Texture original, bool coal)
	{
		int width = original.width;
		int height = original.height;
		RenderTexture target = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, coal ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
		RenderTexture previous = RenderTexture.active;
		Texture2D copy = new Texture2D(width, height, TextureFormat.RGBA32, mipmap: true, linear: !coal);
		try
		{
			Graphics.Blit(original, target);
			RenderTexture.active = target;
			copy.ReadPixels(new Rect(0f, 0f, width, height), 0, 0, recalculateMipMaps: false);
		}
		finally
		{
			RenderTexture.active = previous;
			RenderTexture.ReleaseTemporary(target);
		}
		Color32[] pixels = copy.GetPixels32();
		int cell = width / 4;
		int row = OverlayRow(pixels, width, cell);
		if (row < 0)
		{
			Plugin.Log.LogWarning("Metallurgy: no free cell for coal next to the ore overlays in " + original.name + ", coal looks like iron");
			Object.Destroy(copy);
			return null;
		}
		for (int y = 0; y < cell; y++)
		{
			int line = (row * cell + y) * width;
			for (int x = 0; x < cell; x++)
			{
				Color32 source = pixels[line + 2 * cell + x];
				if (coal)
				{
					// Black specks with a faint grey sheen.
					float light = (0.299f * source.r + 0.587f * source.g + 0.114f * source.b) / 255f;
					byte value = (byte)Mathf.RoundToInt(255f * (0.04f + 0.3f * light));
					source = new Color32(value, value, (byte)Mathf.Min(255, value + 3), source.a);
				}
				pixels[line + 3 * cell + x] = source;
			}
		}
		copy.SetPixels32(pixels);
		copy.Apply(updateMipmaps: true);
		copy.Compress(highQuality: true);
		copy.name = original.name + " (coal)";
		copy.wrapMode = original.wrapMode;
		copy.filterMode = original.filterMode;
		copy.anisoLevel = original.anisoLevel;
		copy.Apply(updateMipmaps: false, makeNoLongerReadable: true);
		Object.DontDestroyOnLoad(copy);
		Plugin.Log.LogInfo("Metallurgy: coal painted into " + original.name + " (cell row " + row + ")");
		return copy;
	}

	// The row of 4 x 4 cells holding the ore overlays: the first three cells used, the fourth empty. Which row it is
	// depends on whether the texture's rows run up or down, so both candidates are checked.
	private static int OverlayRow(Color32[] pixels, int width, int cell)
	{
		foreach (int row in new[] { 2, 1 })
		{
			if (Alpha(pixels, width, cell, 0, row) > 0.02f && Alpha(pixels, width, cell, 1, row) > 0.02f && Alpha(pixels, width, cell, 2, row) > 0.02f && Alpha(pixels, width, cell, 3, row) < 0.005f)
			{
				return row;
			}
		}
		return -1;
	}

	private static float Alpha(Color32[] pixels, int width, int cell, int column, int row)
	{
		long sum = 0;
		int count = 0;
		for (int y = 0; y < cell; y += 8)
		{
			int line = (row * cell + y) * width + column * cell;
			for (int x = 0; x < cell; x += 8)
			{
				sum += pixels[line + x].a;
				count++;
			}
		}
		return (count == 0) ? 0f : sum / (255f * count);
	}
}

[Feature(Features.Metallurgy, Features.MetallurgyInfo)]
[HarmonyPatch(typeof(BlockInfoGenerator), nameof(BlockInfoGenerator.CreateMetaBlockInfos))]
internal static class CoalBlockInfoPatch
{
	private static void Postfix()
	{
		CoalVeins.AddBlockInfo();
	}
}

[Feature(Features.Metallurgy, Features.MetallurgyInfo)]
[HarmonyPatch(typeof(MondeAtlas), nameof(MondeAtlas.Init))]
internal static class CoalAtlasPatch
{
	private static void Prefix(MondeAtlas __instance)
	{
		CoalVeins.AddAtlasEntry(__instance);
	}
}

// Every machine, when the game's deep resources are set up (the host after reading the save's noise list).
[Feature(Features.Metallurgy, Features.MetallurgyInfo)]
[HarmonyPatch(typeof(VoxelNoiseConfigurator), nameof(VoxelNoiseConfigurator.Init))]
internal static class CoalNoisePatch
{
	private static void Prefix()
	{
		VoxelNoiseConfigurator configurator = VoxelNoiseConfigurator.Instance;
		if (configurator == null || configurator.initialized)
		{
			return;
		}
		CoalVeins.AddNoiseConfig(configurator);
		CoalVeins.PaintTerrainTextures();
	}
}

[Feature(Features.Metallurgy, Features.MetallurgyInfo)]
[HarmonyPatch(typeof(VoxelNoiseConfigurator), "InstantiateNoiseComponent")]
internal static class CoalNoiseSeedPatch
{
	private static void Postfix(VoxelNoiseConfigurator.NoiseConfig noiseConfig)
	{
		if (CoalVeins.IsCoalConfig(noiseConfig))
		{
			CoalVeins.Reseed(noiseConfig.NoiseComponent);
		}
	}
}

// The map's noise.json is written without the coal noise (it only holds the asset key, not the other seed).
[Feature(Features.Metallurgy, Features.MetallurgyInfo)]
[HarmonyPatch(typeof(Asset_Map), "Disk_WriteNoiseData")]
internal static class CoalNoiseSavePatch
{
	private static void Prefix(out KeyValuePair<int, VoxelNoiseConfigurator.NoiseConfig> __state)
	{
		__state = new KeyValuePair<int, VoxelNoiseConfigurator.NoiseConfig>(-1, null);
		List<VoxelNoiseConfigurator.NoiseConfig> list = (VoxelNoiseConfigurator.Instance != null) ? VoxelNoiseConfigurator.Instance.NoiseConfigList : null;
		int index = (list != null) ? list.FindIndex(CoalVeins.IsCoalConfig) : -1;
		if (index >= 0)
		{
			__state = new KeyValuePair<int, VoxelNoiseConfigurator.NoiseConfig>(index, list[index]);
			list.RemoveAt(index);
		}
	}

	private static Exception Finalizer(Exception __exception, KeyValuePair<int, VoxelNoiseConfigurator.NoiseConfig> __state)
	{
		if (__state.Key >= 0)
		{
			List<VoxelNoiseConfigurator.NoiseConfig> list = VoxelNoiseConfigurator.Instance.NoiseConfigList;
			list.Insert(Mathf.Min(__state.Key, list.Count), __state.Value);
		}
		return __exception;
	}
}

// Mines and tunnels have their own coal setting (QuarryResources) ...
// ... and coal counts towards the 20 pieces a digger carries before going to storage.
[Feature(Features.Metallurgy, Features.MetallurgyInfo)]
[HarmonyPatch(typeof(DigGoal), "AmountOfGarnotteCarried")]
internal static class CoalCarriedPatch
{
	private static void Postfix(Labor labor, ref int __result)
	{
		if (labor != null && labor.recepteur != null)
		{
			__result += labor.recepteur.ContentDescription.Value<Clay>();
		}
	}
}
