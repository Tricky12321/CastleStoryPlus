using System;
using System.Collections.Generic;
using Brix.Components;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Game.Semantique;
using Brix.Game.Utils;
using Brix.Legacy;
using Brix.Lifecycle.Pooling;
using Brix.Lua;
using Brix.Transactions;
using Brix.UI.Icons;
using Brix.Utils;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.Economy;

// Dark crystals, a resource only enemies give: a corruptron, minitron or magitron drops 5 when it dies (one in three),
// a biftron 15 (one in two). The crystal tier of the upgrades costs dark crystals instead of blue crystal, and the 3x
// bricktron upgrade costs 50 dark crystals instead of energy. Dark crystal is the game's unused resource type
// PurifiedBlueCrystal (the network and saves know it), with its own item: a clone of the raw blue crystal turned deep
// violet, which never merges back into the terrain. Carried in the bag like blue crystal; everything that can hold
// orange crystal can hold as much dark crystal. Drops happen on the host.
[Feature(Features.DarkCrystals, Features.DarkCrystalsInfo)]
internal static class DarkCrystals
{
	internal const string Name = "DarkCrystal";

	internal static readonly Factory.AssetKey Key = new Factory.AssetKey("RawResources", Name);

	internal const LuaCrafting.LuaResource LuaDarkCrystal = (LuaCrafting.LuaResource)102;

	internal const int SmallDrop = 5;

	internal const float SmallChance = 1f / 3f;

	internal const int BigDrop = 15;

	internal const float BigChance = 0.5f;

	internal static Ressource Resource => Adjectif.purifiedBlueCrystal;

	internal static IconKey Icon;

	internal static IconKey StockpiledIcon;

	// Multiplies the blue crystal's material colours (the base colour is then set to Purple).
	private static readonly Color Tint = new Color(0.65f, 0.15f, 0.7f);

	private static readonly Color Purple = new Color(0.5f, 0.1f, 0.6f, 1f);

	// The violet glow: the crystal's emission and a small light.
	private static readonly Color Glow = new Color(0.6f, 0.2f, 1f, 1f);

	private const float GlowStrength = 1.6f;

	private const float LightRange = 2.5f;

	private const float LightIntensity = 1.4f;

	private static void Enable()
	{
		EnsureIcons();
		// The icon function comes with Metallurgy.
		if (!Metallurgy.Metallurgy.IsOn())
		{
			LuaInjection.AddFunction("ResourceIcon", (MoonSharp.Interpreter.ScriptExecutionContext context, MoonSharp.Interpreter.CallbackArguments args) =>
			{
				Sprite sprite = Metallurgy.Metallurgy.SpriteFor(args.Count > 0 ? args[0].CastToString() : null);
				return (sprite != null) ? MoonSharp.Interpreter.DynValue.FromObject(context.OwnerScript, sprite) : MoonSharp.Interpreter.DynValue.Nil;
			});
		}
		LuaInjection.AddPatch(Features.DarkCrystals, "LUI/Meta/Meta_Resource.lua", "return _t", LuaInjection.Mode.InsertBefore,
			"_t:Add(Resource.DarkCrystal,\t{Name = ||\"Dark crystal\",\tInfo = ||\"Dropped by slain enemies. Used by the crystal tier of the upgrades and to make giant bricktrons.\",\tIcon = ||CastleStoryPlus.ResourceIcon(\"dark_crystal\"),\tStockpiledIcon = ||CastleStoryPlus.ResourceIcon(\"dark_crystal_stockpiled\")})\n\n");
	}

	// Whether the feature is switched on, also before its Enable ran (the upgrades ask while they set up their costs).
	internal static bool IsOn()
	{
		return Plugin.Cfg.Bind("Features", Features.DarkCrystals, true, Features.DarkCrystalsInfo).Value;
	}

	internal static void EnsureIcons()
	{
		if (Icon != null)
		{
			return;
		}
		Metallurgy.Metallurgy.EnsureIcons();
		if (!IconDB._databases.TryGetValue(Metallurgy.Metallurgy.IconDatabase, out IconDB database) || database == null)
		{
			return;
		}
		Icon = Metallurgy.Metallurgy.AddIcon(database, "dark_crystal");
		StockpiledIcon = Metallurgy.Metallurgy.AddIcon(database, "dark_crystal_stockpiled");
	}

	internal static void SetUpFactories()
	{
		Factory raw = Metallurgy.Metallurgy.Ready("RawResources", "RawBlueCrystal");
		if (raw != null && !raw.cachedTemplates.ContainsKey(Name))
		{
			Metallurgy.Metallurgy.AddItem(raw, "RawBlueCrystal", Name, typeof(BlueCrystal), Resource, Tint, (GameObject item) =>
			{
				// Left on the ground, raw blue crystal merges back into the terrain as blue crystal.
				foreach (Garnotte garnotte in item.GetComponentsInChildren<Garnotte>(true))
				{
					garnotte.CanMergeback = false;
				}
				MakeGlow(item);
			});
		}
		Factory adjectives = Metallurgy.Metallurgy.Ready("Adjectives", "RawIron");
		if (adjectives != null)
		{
			EnsureIcons();
			Metallurgy.Metallurgy.SetUpInfo(adjectives, "PurifiedBlueCrystal", "BlueCrystal", "Dark crystal", Icon, StockpiledIcon);
		}
	}

	// Purple, glowing violet: the item's own (already copied) materials get a purple base colour and a violet
	// emission, and a small violet light without shadows.
	private static void MakeGlow(GameObject item)
	{
		foreach (Renderer renderer in item.GetComponentsInChildren<Renderer>(true))
		{
			foreach (Material material in renderer.sharedMaterials)
			{
				if (material == null)
				{
					continue;
				}
				if (material.HasProperty("_Color"))
				{
					material.SetColor("_Color", new Color(Purple.r, Purple.g, Purple.b, material.GetColor("_Color").a));
				}
				if (material.HasProperty("_EmissionColor"))
				{
					material.SetColor("_EmissionColor", Glow * GlowStrength);
					material.EnableKeyword("_EMISSION");
					material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
				}
			}
		}
		GameObject glow = new GameObject("DarkCrystalGlow");
		glow.transform.SetParent(item.transform, false);
		glow.transform.localPosition = Vector3.up * 0.2f;
		Light light = glow.AddComponent<Light>();
		light.type = LightType.Point;
		light.color = Glow;
		light.range = LightRange;
		light.intensity = LightIntensity;
		light.shadows = LightShadows.None;
		light.renderMode = LightRenderMode.Auto;
	}

	internal static void RegisterLua()
	{
		LuaCrafting._adjectiveToLuaResource[Resource.GetKey()] = LuaDarkCrystal;
		LuaCrafting._luaResourceToResource[LuaDarkCrystal] = Resource.GetKey();
		Metallurgy.Metallurgy.AddEnumValue(LuaCrafting.ResourceEnum.Table, "Iron", "DarkCrystal", LuaDarkCrystal);
		// Carried in the bag, like blue crystal; loose dark crystal is then also picked up by cleanup areas (their
		// workers only take in passing what goes in the bag).
		Recepteur.BagStuff.Add(Adjectif.purifiedBlueCrystal);
		Metallurgy.Metallurgy.GoesInBag(Adjectif.purifiedBlueCrystal);
		Metallurgy.Metallurgy.MirrorCapacities("DarkCrystals: dark crystal", new Ressource[] { Adjectif.orangeCrystal }, new Ressource[] { Resource });
	}

	// Server: what a dying enemy drops.
	internal static void Drop(Locomotion4 dying)
	{
		Occupation occupation = dying.Occupation;
		Profession profession = (occupation != null) ? occupation.Profession : null;
		if (profession == null || !profession.IsCorruptron())
		{
			return;
		}
		bool big = occupation.CurrentOccupation == Occupation.Type.Biftron;
		if (UnityEngine.Random.value >= (big ? BigChance : SmallChance))
		{
			return;
		}
		int amount = big ? BigDrop : SmallDrop;
		Vector3 center = dying.transform.position;
		List<GameObject> dropped = new List<GameObject>();
		int total = 0;
		while (total < amount)
		{
			GameObject go = Transactor.SpawnUNet(Key, null, center + Vector3.up * 0.5f, Quaternion.identity);
			if (go == null)
			{
				break;
			}
			dropped.Add(go);
			IDescriptor descriptor = go.GetComponent<IDescriptor>();
			int each = (descriptor != null && descriptor.Description != null) ? descriptor.Description.Value(Resource) : 0;
			// An item without a count counts as one, so this always ends.
			total += Mathf.Max(1, each);
		}
		if (dropped.Count > 0)
		{
			Recepteur.ScatterObjects(dropped, center);
		}
	}

	// Dark crystals in the team's stockpiles and tool racks.
	internal static int Stock(Faction faction)
	{
		return CraftLoopLimit.Stock(faction, Resource, cached: false);
	}

	// Server: takes that many dark crystals out of the team's stockpiles. Whole items are taken; false (and nothing
	// taken) when there are not enough.
	internal static bool Consume(Faction faction, int amount)
	{
		List<GameObject> items = new List<GameObject>();
		int found = 0;
		AutoList list = BrixSingleton<AutoList>.Instance;
		if (list == null)
		{
			return false;
		}
		HashSet<GameObject> seen = new HashSet<GameObject>();
		foreach (Factory.AssetKey storage in new[] { ObjetsDynamiques.Palette, ObjetsDynamiques.Toolrack, ObjetsDynamiques.SingleToolrack })
		{
			HashSet<GameObject> instances = list.GetInstances(storage);
			if (instances == null)
			{
				continue;
			}
			foreach (GameObject go in instances)
			{
				if (found >= amount)
				{
					break;
				}
				if (go == null || !go.activeInHierarchy || !seen.Add(go) || Faction.GetFaction(go) != faction)
				{
					continue;
				}
				Recepteur recepteur = go.GetComponent<Recepteur>();
				if (recepteur == null)
				{
					continue;
				}
				foreach (IDescriptor item in recepteur.StoredItems)
				{
					if (found >= amount)
					{
						break;
					}
					int each = (item != null && item.Description != null && item.GameObject != null) ? item.Description.Value(Resource) : 0;
					if (each <= 0)
					{
						continue;
					}
					items.Add(item.GameObject);
					found += each;
				}
			}
		}
		if (found < amount)
		{
			return false;
		}
		foreach (GameObject item in items)
		{
			ObjectPoolSingleton.Release(item);
		}
		return true;
	}
}

[Feature(Features.DarkCrystals, Features.DarkCrystalsInfo)]
[HarmonyPatch(typeof(Factory), nameof(Factory.Awake))]
internal static class DarkCrystalsFactoryPatch
{
	private static void Postfix()
	{
		DarkCrystals.SetUpFactories();
	}
}

// The Lua value and the capacities, once the game has set up its own (the first Lua script set-up).
[Feature(Features.DarkCrystals, Features.DarkCrystalsInfo)]
[HarmonyPatch(typeof(LuaCrafting), nameof(LuaCrafting.Register))]
internal static class DarkCrystalsLuaPatch
{
	private static bool _done;

	private static void Postfix()
	{
		if (_done)
		{
			return;
		}
		_done = true;
		try
		{
			DarkCrystals.RegisterLua();
		}
		catch (Exception ex)
		{
			Plugin.Log.LogError("DarkCrystals: could not register: " + ex);
		}
	}
}

// Server: slain enemies may drop dark crystals.
[Feature(Features.DarkCrystals, Features.DarkCrystalsInfo)]
[HarmonyPatch(typeof(Locomotion4), "Die")]
internal static class DarkCrystalsDropPatch
{
	private static void Prefix(Locomotion4 __instance)
	{
		if (!NetworkServer.active || __instance.IsCorpse)
		{
			return;
		}
		try
		{
			DarkCrystals.Drop(__instance);
		}
		catch (Exception ex)
		{
			Plugin.Log.LogError("DarkCrystals: drop failed: " + ex);
		}
	}
}
