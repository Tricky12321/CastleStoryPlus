using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Brix.Engine;
using Brix.External.Factories;
using Brix.Game.Utils;
using Brix.IO.Serialization;
using CastleStoryPlus.Core;
using CastleStoryPlus.DevTools.Testing;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace CastleStoryPlus.DevTools.Tests;

// A content feature switched off in the settings (#185): its buildings, blocks and items stay in the game's factories,
// so a world that has them still loads. Each test switches features off as a player does and puts them back as they
// were. Runs alone: switching a feature off changes the game for every other test.
internal static class SavedContentTests
{
	// The content features and the factory keys a save can hold of each.
	private static readonly KeyValuePair<string, Factory.AssetKey[]>[] Content = new KeyValuePair<string, Factory.AssetKey[]>[]
	{
		Of(Features.Market, "ObjetsDynamiques/Market", "Blueprints/Market"),
		Of(Features.Warehouse, "ObjetsDynamiques/Warehouse", "Blueprints/Warehouse"),
		Of(Features.LargeStockpile, "ObjetsDynamiques/LargePalette", "Blueprints/LargePalette"),
		Of(Features.Upgrades, "ObjetsDynamiques/Smithy", "ObjetsDynamiques/Armoury", "ObjetsDynamiques/ResearchStation", "Blueprints/Smithy"),
		Of(Features.Saplings, "ObjetsDynamiques/Sapling", "Blueprints/Sapling", "ObjetsDynamiques/SaplingArea"),
		Of(Features.CustomBlocks, "Blocks/Stone_Brick2x2", "Blueprints/Stone_Brick2x2", "Blocks/Wood_Slab1x1", "Blueprints/Wood_Slab1x1"),
		Of(Features.Metallurgy, "RawResources/Coal", "BuildResources/SteelIngot"),
		Of(Features.DarkCrystals, "RawResources/DarkCrystal"),
	};

	[GameTest("Saving", "Each content feature switched off: its buildings, blocks and items are still in the factories, and the patches that add them and guard the blueprint mapping stay on (#185)", Exclusive = true, TimeoutSeconds = 60f)]
	private static IEnumerator ContentStaysWithFeatureOff(TestContext t)
	{
		foreach (KeyValuePair<string, Factory.AssetKey[]> content in Content)
		{
			SwitchOff(t, content.Key);
		}
		yield return null;
		foreach (KeyValuePair<string, Factory.AssetKey[]> content in Content)
		{
			foreach (Factory.AssetKey key in content.Value)
			{
				t.True(Factory.Exist(key), key + " is not in the factories with " + content.Key + " switched off");
			}
		}
		t.True(HasPatch(AccessTools.Method(typeof(Factory), nameof(Factory.Awake)), typeof(SavedContent)), "SavedContent no longer patches Factory.Awake with the content features off");
		t.True(HasPatch(AccessTools.Method(typeof(BlueprintMapper), nameof(BlueprintMapper.AddMappings)), typeof(BlueprintMappingGuard)), "The blueprint mapping guard is off with the content features off");
	}

	[GameTest("Saving", "A market, warehouse, large stockpile, smithy and sapling saved and loaded again (as a save is) with their features switched off: each comes back as itself (#185)", Exclusive = true, TimeoutSeconds = 180f)]
	private static IEnumerator BuildingsLoadWithFeatureOff(TestContext t)
	{
		Factory.AssetKey[] blueprints = new Factory.AssetKey[]
		{
			new Factory.AssetKey("Blueprints", "Market"),
			new Factory.AssetKey("Blueprints", "Warehouse"),
			new Factory.AssetKey("Blueprints", "LargePalette"),
			new Factory.AssetKey("Blueprints", "Smithy"),
			new Factory.AssetKey("Blueprints", "Sapling"),
		};
		string[] features = new string[] { Features.Market, Features.Warehouse, Features.LargeStockpile, Features.Upgrades, Features.Saplings };
		XYZ site = Scenario.Site(t, 14, 10);
		for (int i = 0; i < blueprints.Length; i++)
		{
			// Built with the feature on, as in the game the player saved.
			GameObject made = null;
			yield return Scenario.Building(t, blueprints[i], site, Scenario.BuildProject(t, site), (GameObject go) => made = go);
			Factory.AssetKey key = KeyOf(made);
			t.NotNull(key, blueprints[i].Name + "'s factory key");
			string json = made.GetComponent<SerializationComponent>().GetSerializedGameObject();
			t.True(!string.IsNullOrEmpty(json), blueprints[i].Name + " was not saved");
			Scenario.Discard(made);
			yield return t.Frames(3);
			// Loaded with the feature switched off, one object as the game's loader does it (LoadGameObjectsPatch).
			SwitchOff(t, features[i]);
			GameObject loaded = null;
			try
			{
				JsonSerializer serializer = JsonSerializer.Create(Io.GetLoadSerializerSettings("save"));
				loaded = serializer.Deserialize<GameObject>(JToken.Parse(json).CreateReader());
			}
			catch (Exception ex)
			{
				t.Fail(key + " did not load with " + features[i] + " switched off: " + ex);
			}
			GameObject toDiscard = loaded;
			t.Cleanup(() => Scenario.Discard(toDiscard));
			t.NotNull(loaded, key + " loaded with " + features[i] + " switched off");
			t.Equal(key.ToString(), KeyOf(loaded)?.ToString(), "The loaded object's key");
			t.Log(key + " saved and loaded with " + features[i] + " off");
			Scenario.Discard(loaded);
			yield return t.Frames(3);
		}
	}

	// Off now, back to how it was after the test.
	private static void SwitchOff(TestContext t, string feature)
	{
		bool was = Plugin.IsEnabled(feature);
		t.Cleanup(() => FeatureToggle.Set(feature, was));
		FeatureToggle.Set(feature, false);
	}

	private static bool HasPatch(MethodBase original, Type patchClass)
	{
		Patches info = (original != null) ? Harmony.GetPatchInfo(original) : null;
		if (info == null)
		{
			return false;
		}
		foreach (IList<Patch> list in new IList<Patch>[] { info.Prefixes, info.Postfixes })
		{
			foreach (Patch patch in list)
			{
				if (patch.PatchMethod.DeclaringType == patchClass)
				{
					return true;
				}
			}
		}
		return false;
	}

	private static Factory.AssetKey KeyOf(GameObject go)
	{
		FactoryImprint imprint = (go != null) ? go.GetComponent<FactoryImprint>() : null;
		return (imprint != null) ? imprint.AssetKey : null;
	}

	private static KeyValuePair<string, Factory.AssetKey[]> Of(string feature, params string[] keys)
	{
		Factory.AssetKey[] assetKeys = new Factory.AssetKey[keys.Length];
		for (int i = 0; i < keys.Length; i++)
		{
			string[] parts = keys[i].Split('/');
			assetKeys[i] = new Factory.AssetKey(parts[0], parts[1]);
		}
		return new KeyValuePair<string, Factory.AssetKey[]>(feature, assetKeys);
	}
}
