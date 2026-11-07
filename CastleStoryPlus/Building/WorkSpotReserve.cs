using System;
using System.Collections.Generic;
using Brix.Engine;
using Brix.External.Factories;
using Brix.External.Factories.Templates;
using Brix.Game.Components;
using Brix.Game.Components.Volumes;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.Building;

// A finished workshop takes more room than its blueprint: the forge, the workbench and the lab (and the mod's smithy,
// armoury, market and research station, cloned from them) claim the spot their worker stands on (occupy) only once
// built. The blueprint left that spot free, so another blueprint of the same task could be placed on it; when the
// workshop was finished its work spot was in the other blueprint's way, which was then blocked for good ("something
// is in the way"). The blueprints now claim the same cells as the finished building (occupy, as the building has
// them), so a blueprint placed there is in the way at once, while it can still be placed elsewhere. Done on the
// templates once both factories are loaded.
[Feature(Features.WorkSpotReserve, Features.WorkSpotReserveInfo)]
[HarmonyPatch(typeof(Factory), nameof(Factory.Awake))]
internal static class WorkSpotReserve
{
	// The factory of buildings (workshops, stockpiles, wards...); blocks have no cells like these.
	private const string BuildingFactory = "ObjetsDynamiques";

	// Blueprint templates already looked at (the templates themselves: a factory loaded again has new ones).
	private static readonly HashSet<GameObject> Done = new HashSet<GameObject>();

	// After the other Factory.Awake postfixes, which add the mod's own buildings.
	[HarmonyPriority(Priority.Last)]
	private static void Postfix()
	{
		try
		{
			foreach (Factory factory in Factory.GetFactories())
			{
				if (factory == null)
				{
					continue;
				}
				// The templates as the factory finds them itself, without filling its cache early.
				foreach (FactoryTemplate template in factory.GetComponentsInChildren<FactoryTemplate>(true))
				{
					Reserve(template);
				}
			}
		}
		catch (Exception ex)
		{
			Plugin.Log.LogError("WorkSpotReserve: " + ex);
		}
	}

	private static void Reserve(FactoryTemplate template)
	{
		Blueprint blueprint = (template != null) ? template.GetComponent<Blueprint>() : null;
		Factory.AssetKey result = (blueprint != null) ? blueprint.BlueprintResult : null;
		if (result == null || result.Factory != BuildingFactory)
		{
			return;
		}
		if (Done.Contains(template.gameObject))
		{
			return;
		}
		FactoryTemplate building = Find(result);
		if (building == null)
		{
			// Its factory is not loaded yet: tried again after the next one.
			return;
		}
		Done.Add(template.gameObject);
		string name = template.AssetKey.ToString();
		VolumeDataComponent planned = blueprint.GetComponent<VolumeDataComponent>();
		VolumeDataComponent built = building.GetComponent<VolumeDataComponent>();
		if (planned == null || built == null || planned._indexedVolumetricProperties == null || built._indexedVolumetricProperties == null)
		{
			return;
		}
		const long occupy = (long)VPropertyMask.occupy;
		const long taken = (long)(VPropertyMask.occupy | VPropertyMask.fantome);
		IndexedVolumetricProperties cells = planned._indexedVolumetricProperties;
		List<XYZ> missing = new List<XYZ>();
		foreach (KeyValuePair<XYZ, long> cell in built._indexedVolumetricProperties)
		{
			if ((cell.Value & occupy) != 0 && (!cells.TryGetValue(cell.Key, out long mask) || (mask & taken) == 0))
			{
				missing.Add(cell.Key);
			}
		}
		if (missing.Count == 0)
		{
			return;
		}
		// A new set of cells rather than changing the old one in place, as LargeStockpile does.
		IndexedVolumetricProperties reserved = new IndexedVolumetricProperties();
		foreach (KeyValuePair<XYZ, long> cell in cells)
		{
			reserved[cell.Key] = cell.Value;
		}
		List<string> added = new List<string>();
		foreach (XYZ cell in missing)
		{
			reserved[cell] = (reserved.TryGetValue(cell, out long mask) ? mask : 0L) | occupy;
			added.Add(cell.x + "," + cell.y + "," + cell.z);
		}
		planned._indexedVolumetricProperties = reserved;
		Plugin.Log.LogInfo("WorkSpotReserve: " + name + " now claims " + string.Join(" ", added.ToArray()) + " as its building " + result + " does");
	}

	private static FactoryTemplate Find(Factory.AssetKey key)
	{
		foreach (Factory factory in Factory.GetFactories())
		{
			if (factory == null || factory.name != key.Factory)
			{
				continue;
			}
			foreach (FactoryTemplate template in factory.GetComponentsInChildren<FactoryTemplate>(true))
			{
				if (template != null && template.gameObject.name == key.Name)
				{
					return template;
				}
			}
		}
		return null;
	}
}
