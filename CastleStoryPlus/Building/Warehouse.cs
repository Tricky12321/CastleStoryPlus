using System;
using System.Collections.Generic;
using Brix.Components;
using Brix.External.Factories;
using Brix.Game.AI;
using Brix.Game.AI.KnowledgeSpace;
using Brix.Game.Components;
using Brix.Game.Components.Volumes;
using Brix.Game.Semantique;
using Brix.Game.Utils;
using Brix.Lifecycle.Pooling;
using Brix.NewUI.Tooltips;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;
using Object = UnityEngine.Object;

namespace CastleStoryPlus.Building;

// The warehouse: a 9 x 6 storage hall, cloned from the stockpile (so it has the stockpile's menu, resource
// filters and counts) with its own model (WarehouseModel). It holds 4500 resources of any kind (in the stockpile's
// bulk units: 4500 stones, 225 bricks, 675 planks, ...), workers walk in through the cart doors and store and fetch
// inside, and its brick works turns stored stone into bricks: 20 stones become 1 brick, while the stockpile's
// "convert stone to bricks" switch is on. The game's own conversion (fill a full stockpile with bricks) is off for
// it. Expensive to build.
[Feature(Features.Warehouse, Features.WarehouseInfo)]
[HarmonyPatch(typeof(Factory), nameof(Factory.Awake))]
internal static class Warehouse
{
	internal const string Name = "Warehouse";

	internal const int Capacity = 4500;

	internal const int StonesPerBrick = 20;

	internal static readonly CustomBuilding Building = new CustomBuilding(Name, 9, 6, 6)
	{
		BlueprintSource = "Pallet",
		BuildingSource = "Palette",
		KeepChildren = new string[2] { "Content", "OverheadTarget" },
		ValidatorFrom = "Workbench",
		BuildModel = WarehouseModel.Build,
		BuildCost = (Description cost) =>
		{
			cost.Add(Adjectif.New(Adjectif.plankBlock.GetType(), 60));
			cost.Add(Adjectif.New(Adjectif.stoneBlock.GetType(), 80));
			cost.Add(Adjectif.New(Adjectif.iron.GetType(), 20));
			cost.Add(Adjectif.New(Adjectif.rope.GetType(), 10));
		},
		SetupBuilding = Setup,
		// Rows from the back: the hall (walls round 5 x 4 blocks of floor, cart doors in the middle of the front)
		// and, on the right, the open brick works with the stone heap, the press, a water barrel and the bricks.
		Layout = new string[6] { "#########", "#AAAAA#..", "#AAAAA##.", "#AAAAA#.#", "#AAAAA##.", "##...#..." }
	};

	private static void Enable()
	{
		UI.BuildIcons.Register();
		LuaInjection.AddPatch(Features.Warehouse, "LUI/Meta/Meta_Structure.lua", "Hotkey = \"project_Stockpile\",\t\tgroupId = 1 })\n", LuaInjection.Mode.InsertAfter,
			"_t.Add(AssetKey.New(\"Blueprints\", \"" + Name + "\"),\t\t{ Name = ||\"" + Name + "\",\t\tIcon = " + UI.BuildIcons.Lua("warehouse", "_Stockpiled_Brick") + ",\tHotkey = \"\",\t\tgroupId = 1 })\n");
		// The workers' storage search and pick-up index only look at these keys.
		HashSet<Factory.AssetKey> storage = (HashSet<Factory.AssetKey>)AccessTools.Field(typeof(Knowledge), "storageObjects").GetValue(null);
		storage.Add(Building.BuildingKey);
		Knowledge.InterestingResourceObject.Add(Building.BuildingKey);
		CursorTooltip.titles[Building.BlueprintKey] = "##cursortip_stockpile";
	}

	private static void Postfix()
	{
		Building.AddToFactories();
	}

	internal static bool IsWarehouse(Component component)
	{
		FactoryImprint imprint = (component != null) ? component.GetComponent<FactoryImprint>() : null;
		return imprint != null && imprint.AssetKey == Building.BuildingKey;
	}

	// The stockpile's room scaled to Capacity (its bulk, and every resource in proportion), any mix of resources,
	// and no passing through it when it is empty (the stockpile lets workers walk over an empty pallet; the
	// warehouse has walls).
	private static void Setup(GameObject go)
	{
		Recepteur recepteur = go.GetComponent<Recepteur>();
		if (recepteur != null)
		{
			Description capacity = recepteur._baseCapacity;
			int bulk = capacity.Value(Adjectif.encombrement);
			float factor = (bulk > 0) ? Capacity / (float)bulk : 1f;
			List<string> parts = new List<string>();
			foreach (KeyValuePair<Type, Adjectif> entry in capacity.DicoAdjectif)
			{
				Adjectif adjectif = entry.Value;
				if (adjectif.quantifiable == null || adjectif.GetType() == Adjectif.outil.GetType())
				{
					continue;
				}
				if (adjectif is Ressource || adjectif.GetType() == Adjectif.encombrement.GetType())
				{
					adjectif.quantifiable.valeur = Mathf.RoundToInt(adjectif.quantifiable.valeur * factor);
					parts.Add(entry.Key.Name + "=" + adjectif.quantifiable.valeur);
				}
			}
			recepteur._acceptMultipleResouceType = true;
			Plugin.Log.LogInfo(Name + ": capacity " + string.Join(", ", parts.ToArray()));
		}
		PassThroughStockpile passThrough = go.GetComponent<PassThroughStockpile>();
		if (passThrough != null)
		{
			Object.DestroyImmediate(passThrough);
		}
		go.AddComponent<WarehouseBrickWorks>();
	}
}

// Server: every few seconds a warehouse with the stockpile's "convert" switch on turns 20 stones into 1 brick, as
// long as it holds the stones and has room for the brick (a brick takes the room of 20 stones).
internal class WarehouseBrickWorks : MonoBehaviour
{
	private const float Interval = 3f;

	// Bricks made per round at most, so a full warehouse of stone is worked off over time.
	private const int BricksPerRound = 5;

	private float _next;

	private void Update()
	{
		if (!NetworkServer.active || Time.time < _next)
		{
			return;
		}
		_next = Time.time + Interval;
		ResourceConverter converter = GetComponent<ResourceConverter>();
		Recepteur recepteur = GetComponent<Recepteur>();
		if (converter == null || recepteur == null || !converter._autoConvert)
		{
			return;
		}
		try
		{
			for (int i = 0; i < BricksPerRound; i++)
			{
				if (!MakeBrick(recepteur))
				{
					break;
				}
			}
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning("Warehouse: brick works: " + ex.Message);
		}
	}

	private static bool MakeBrick(Recepteur recepteur)
	{
		if (recepteur.ContentDescription.Value(Adjectif.stoneBlock) >= recepteur.BaseCapacity.Value(Adjectif.stoneBlock))
		{
			return false;
		}
		List<GameObject> stones = new List<GameObject>();
		int found = 0;
		foreach (IDescriptor item in recepteur.StoredItems)
		{
			int each = (item != null && item.Description != null && item.GameObject != null) ? item.Description.Value(Adjectif.stones) : 0;
			if (each <= 0)
			{
				continue;
			}
			stones.Add(item.GameObject);
			found += each;
			if (found >= Warehouse.StonesPerBrick)
			{
				break;
			}
		}
		if (found < Warehouse.StonesPerBrick)
		{
			return false;
		}
		foreach (GameObject stone in stones)
		{
			ObjectPoolSingleton.Release(stone);
		}
		Factory.AssetKey key = Description.RepresentativeOf(Adjectif.stoneBlock);
		GameObject brick = ObjectPoolSingleton.Request_Unsafe(key, active: true);
		recepteur.AddObject(brick);
		NetworkServer.Spawn(brick, key.AssetId);
		return true;
	}
}

// The game's conversion fills a full stockpile with bricks, whatever else it holds; the warehouse has its own.
[Feature(Features.Warehouse, Features.WarehouseInfo)]
[HarmonyPatch(typeof(ResourceConverter), nameof(ResourceConverter.CurrentAutoConvertRule), MethodType.Getter)]
internal static class WarehouseAutoConvertPatch
{
	private static bool Prefix(ResourceConverter __instance, ref ResourceConverter.ConversionRule __result)
	{
		if (!Warehouse.IsWarehouse(__instance))
		{
			return true;
		}
		__result = null;
		return false;
	}
}

[Feature(Features.Warehouse, Features.WarehouseInfo)]
[HarmonyPatch(typeof(ResourceConverter), nameof(ResourceConverter.CurrentConvertRule), MethodType.Getter)]
internal static class WarehouseConvertPatch
{
	private static bool Prefix(ResourceConverter __instance, ref ResourceConverter.ConversionRule __result)
	{
		if (!Warehouse.IsWarehouse(__instance))
		{
			return true;
		}
		__result = null;
		return false;
	}
}

// Warehouses are also listed as stockpiles, so every count and search of the team's stockpiles finds them.
[Feature(Features.Warehouse, Features.WarehouseInfo)]
[HarmonyPatch(typeof(AutoList), nameof(AutoList.AddToAutoList))]
internal static class WarehouseListAddPatch
{
	private static void Postfix(AutoList __instance, Factory.AssetKey key, GameObject go)
	{
		if (key == Warehouse.Building.BuildingKey && go != null)
		{
			__instance.GetInstances(ObjetsDynamiques.Palette).Add(go);
		}
	}
}

[Feature(Features.Warehouse, Features.WarehouseInfo)]
[HarmonyPatch(typeof(AutoList), nameof(AutoList.RemoveFromAutoList), new Type[] { typeof(Factory.AssetKey), typeof(GameObject) })]
internal static class WarehouseListRemovePatch
{
	private static void Postfix(AutoList __instance, Factory.AssetKey key, GameObject go)
	{
		if (key == Warehouse.Building.BuildingKey)
		{
			__instance.GetInstances(ObjetsDynamiques.Palette).Remove(go);
		}
	}
}

// The game counts only empty storages as room for a new kind of resource ("You need more empty stockpiles!" and
// the projects' stockpile counts). A warehouse takes any mix of resources, so one with room left counts too.
[Feature(Features.Warehouse, Features.WarehouseInfo)]
[HarmonyPatch(typeof(Brix.Game.Storage.CompoundRecepteur), nameof(Brix.Game.Storage.CompoundRecepteur.RecepteursEmptyCount))]
internal static class WarehouseEmptyCountPatch
{
	private static void Postfix(Brix.Game.Storage.CompoundRecepteur __instance, ref int __result)
	{
		foreach (Recepteur recepteur in __instance.TrackedRecepteurs)
		{
			if (recepteur != null && !recepteur.IsEmpty() && Warehouse.IsWarehouse(recepteur)
				&& recepteur.ContentDescription.Value(Adjectif.encombrement) < recepteur.BaseCapacity.Value(Adjectif.encombrement))
			{
				__result++;
			}
		}
	}
}

// The observer feeding the faction storage count tracks own pallets; warehouses too.
[Feature(Features.Warehouse, Features.WarehouseInfo)]
[HarmonyPatch(typeof(UIGameObserver), nameof(UIGameObserver.ResetAll))]
internal static class WarehouseObserverPatch
{
	private static void Postfix()
	{
		if (UIGameObserver.storage == null)
		{
			return;
		}
		Predicate<Transform> pallets = UIGameObserver.storage.condition;
		UIGameObserver.storage.condition = (Transform t) => (pallets != null && pallets(t)) || (Warehouse.IsWarehouse(t) && UIGameObserver.IsMine(t));
	}
}
