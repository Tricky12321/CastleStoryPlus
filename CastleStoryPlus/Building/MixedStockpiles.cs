using System;
using System.Collections.Generic;
using Brix.External.Factories;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Game.Semantique;
using Brix.Game.Storage;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.Building;

// A stockpile pallet is 2x2 blocks: four columns. The game only let one resource type on a pallet. Now every
// column holds one resource type, and different columns of the same pallet may hold different types: a type
// takes as many columns as it needs (ceil(count / per column)), and may grow into its last, partly filled column
// and into any free one. Per column = the pallet's capacity for that type / 4, so a pallet of one type holds
// exactly as much as before. The column layout follows from the content alone (types in a fixed order), so
// clients draw the same pallet as the host. Resources the pallet converts (raw stone) and those in
// [MixedStockpiles] NeverMix (iron, brimstone, crystals...) are never mixed.
[Feature(Features.MixedStockpiles, Features.MixedStockpilesInfo)]
internal static class MixedStockpiles
{
	// A 2 x 2 pallet has four columns, a 3 x 3 large stockpile nine.
	public static int Columns(Recepteur recepteur)
	{
		return LargeStockpile.IsLarge(recepteur) ? 9 : 4;
	}

	private static bool _logged;

	private static readonly HashSet<string> NeverMix = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	private static void Enable()
	{
		string list = Plugin.Cfg.Bind("MixedStockpiles", "NeverMix", "Iron,RawIron,PurifiedBrimstone,OrangeCrystal,BlueCrystal,BlueCrystalChunck,Glass,Terracotta,Cog,Bomb,ExplosiveBarrel", "Resources that always get a pallet of their own, comma separated (names as in the log line 'MixedStockpiles: pallet capacity').").Value;
		foreach (string name in list.Split(','))
		{
			if (name.Trim().Length > 0)
			{
				NeverMix.Add(name.Trim());
			}
		}
	}

	public static bool IsPallet(Recepteur recepteur)
	{
		return recepteur != null && LargeStockpile.IsStockpileKey(recepteur.AssetKey) && recepteur.GetComponent<Labor>() == null;
	}

	// Resource types held, in the fixed column order.
	public static List<Ressource> Types(Recepteur recepteur)
	{
		List<Ressource> result = new List<Ressource>();
		foreach (KeyValuePair<Type, Adjectif> pair in recepteur.ContentDescription.DicoAdjectif)
		{
			Ressource resource = pair.Value as Ressource;
			if (resource != null && resource.quantifiable.valeur > 0)
			{
				result.Add(resource);
			}
		}
		result.Sort((Ressource a, Ressource b) => string.CompareOrdinal(a.GetType().Name, b.GetType().Name));
		return result;
	}

	public static bool IsMixed(Recepteur recepteur)
	{
		return IsPallet(recepteur) && Types(recepteur).Count > 1;
	}

	public static int PerColumn(Recepteur recepteur, Ressource resource)
	{
		int capacity = recepteur.BaseCapacity.Value(resource);
		int columns = Columns(recepteur);
		return Mathf.Max(1, (capacity + columns - 1) / columns);
	}

	public static int ColumnsUsed(Recepteur recepteur, Ressource resource)
	{
		int count = recepteur.ContentDescription.Value(resource);
		int perColumn = PerColumn(recepteur, resource);
		return (count + perColumn - 1) / perColumn;
	}

	// Replaces the game's "only the resource already on it" rule with the column rule.
	public static void ApplyColumnCapacity(Recepteur recepteur)
	{
		LogCapacityOnce(recepteur);
		Description capacity = recepteur.CurrentCapacity;
		Description content = recepteur.ContentDescription;
		capacity.CopyQuantifiablesFrom(recepteur.BaseCapacityWithExclusions);
		capacity.Subtract(content);
		int free = Columns(recepteur);
		foreach (Ressource type in Types(recepteur))
		{
			free -= ColumnsUsed(recepteur, type);
		}
		free = Mathf.Max(0, free);
		List<Ressource> resources = new List<Ressource>();
		foreach (KeyValuePair<Type, Adjectif> pair in capacity.DicoAdjectif)
		{
			Ressource resource = pair.Value as Ressource;
			if (resource != null)
			{
				resources.Add(resource);
			}
		}
		List<Ressource> held = Types(recepteur);
		bool holdsSolo = false;
		foreach (Ressource resource in held)
		{
			if (IsSolo(recepteur, resource))
			{
				holdsSolo = true;
			}
		}
		bool anyRoom = false;
		foreach (Ressource resource in resources)
		{
			int perColumn = PerColumn(recepteur, resource);
			int count = content.Value(resource);
			int allowed = (ColumnsUsed(recepteur, resource) + free) * perColumn - count;
			// A resource the pallet converts (raw stone into bricks) or one set to never mix (iron, brimstone...) only
			// goes on a pallet that holds nothing else, and nothing else goes on a pallet that holds it.
			bool solo = IsSolo(recepteur, resource);
			if ((solo && held.Count > 0 && !(held.Count == 1 && held[0].GetType() == resource.GetType())) || (!solo && holdsSolo))
			{
				allowed = 0;
			}
			int room = Mathf.Max(0, Mathf.Min(capacity.Value(resource), allowed));
			capacity.Set(resource, room);
			if (room > 0)
			{
				anyRoom = true;
			}
		}
		// The game counts a pallet as full when nothing is left; leftover bulk alone must not keep it open.
		if (!anyRoom && capacity.Has(Adjectif.encombrement))
		{
			capacity.Set(Adjectif.encombrement, 0);
		}
	}

	public static bool IsSolo(Recepteur recepteur, Ressource resource)
	{
		return NeverMix.Contains(resource.GetType().Name) || IsConverted(recepteur, resource);
	}

	// Input of one of the pallet's own conversion rules (ResourceConverter: raw stone into bricks).
	public static bool IsConverted(Recepteur recepteur, Ressource resource)
	{
		ResourceConverter converter = recepteur.GetComponent<ResourceConverter>();
		if (converter == null)
		{
			return false;
		}
		foreach (ResourceConverter.ConversionRule rule in converter._conversionRules)
		{
			if (rule.InputResourceType == resource.GetType())
			{
				return true;
			}
		}
		return false;
	}

	private static void LogCapacityOnce(Recepteur recepteur)
	{
		if (_logged)
		{
			return;
		}
		_logged = true;
		string text = string.Empty;
		foreach (KeyValuePair<Type, Adjectif> pair in recepteur.BaseCapacity.DicoAdjectif)
		{
			text += pair.Key.Name + "=" + pair.Value.quantifiable.valeur + " ";
		}
		Plugin.Log.LogInfo("MixedStockpiles: pallet capacity " + text);
	}
}

// Every add, remove and exclusion change ends here (DealSingleResourceRecepteur, ResetDescriptions).
[Feature(Features.MixedStockpiles, Features.MixedStockpilesInfo)]
[HarmonyPatch(typeof(Recepteur), "RegenerateDescriptionsWithExclusions")]
internal static class MixedStockpileCapacityPatch
{
	private static void Postfix(Recepteur __instance)
	{
		if (MixedStockpiles.IsPallet(__instance))
		{
			MixedStockpiles.ApplyColumnCapacity(__instance);
		}
	}
}

// The fill shown over a pallet was the share of its first resource; now it is the share of all four columns.
[Feature(Features.MixedStockpiles, Features.MixedStockpilesInfo)]
[HarmonyPatch(typeof(Recepteur), nameof(Recepteur.GetProportionUsed), new Type[0])]
internal static class MixedStockpileProportionPatch
{
	private static void Postfix(Recepteur __instance, ref float __result)
	{
		if (!MixedStockpiles.IsMixed(__instance))
		{
			return;
		}
		float used = 0f;
		foreach (Ressource resource in MixedStockpiles.Types(__instance))
		{
			used += (float)__instance.ContentDescription.Value(resource) / MixedStockpiles.PerColumn(__instance, resource);
		}
		__result = Mathf.Clamp01(used / MixedStockpiles.Columns(__instance));
	}
}

// Stones turn into bricks by wiping the whole pallet; never on a pallet that also holds other resources.
[Feature(Features.MixedStockpiles, Features.MixedStockpilesInfo)]
[HarmonyPatch(typeof(ResourceConverter), nameof(ResourceConverter.CurrentAutoConvertRule), MethodType.Getter)]
internal static class MixedStockpileAutoConvertPatch
{
	private static void Postfix(ResourceConverter __instance, ref ResourceConverter.ConversionRule __result)
	{
		if (__result != null && MixedStockpiles.IsMixed(__instance.recepteur))
		{
			__result = null;
		}
	}
}

[Feature(Features.MixedStockpiles, Features.MixedStockpilesInfo)]
[HarmonyPatch(typeof(ResourceConverter), nameof(ResourceConverter.CurrentConvertRule), MethodType.Getter)]
internal static class MixedStockpileConvertPatch
{
	private static void Postfix(ResourceConverter __instance, ref ResourceConverter.ConversionRule __result)
	{
		if (__result != null && MixedStockpiles.IsMixed(__instance.recepteur))
		{
			__result = null;
		}
	}
}

// The pallet's built-in displays fill the whole pallet with one resource. On a mixed pallet they are hidden and
// MixedStockpileVisual draws the columns instead.
[Feature(Features.MixedStockpiles, Features.MixedStockpilesInfo)]
[HarmonyPatch(typeof(RecepteurDisplay), "UpdateElements")]
internal static class MixedStockpileDisplayPatch
{
	private static bool Prefix(RecepteurDisplay __instance)
	{
		Recepteur recepteur = __instance.targetRecepteur;
		// Large stockpiles, mixed or not, are drawn by LargeStockpileVisual.
		if (!MixedStockpiles.IsPallet(recepteur) || LargeStockpile.IsLarge(recepteur))
		{
			return true;
		}
		bool mixed = MixedStockpiles.IsMixed(recepteur);
		MixedStockpileVisual.Refresh(recepteur, mixed);
		if (!mixed)
		{
			return true;
		}
		foreach (RecepteurDisplay.DisplayElement element in __instance.displayElements)
		{
			if (ShowsResource(element))
			{
				element.ToggleDisplay(false);
			}
			else
			{
				element.UpdateDisplay(recepteur);
			}
		}
		return false;
	}

	// An element that appears when the pallet holds some resource (the stacked bricks, planks, barrels...).
	internal static bool ShowsResource(RecepteurDisplay.DisplayElement element)
	{
		foreach (RecepteurDisplay.DisplayElement.ConditionElement condition in element.conditions)
		{
			AdjectiveKey key = condition.relatedAdjective;
			if (key == null || !(key.GetAdjectif() is Ressource))
			{
				continue;
			}
			switch (condition.displayCondition)
			{
				case RecepteurDisplay.ElementCondition.WhenHas:
				case RecepteurDisplay.ElementCondition.WhenGreater:
				case RecepteurDisplay.ElementCondition.WhenGreaterOrEqual:
				case RecepteurDisplay.ElementCondition.WhenEqual:
					return true;
			}
		}
		return false;
	}
}

// Draws a mixed pallet: each column is a stack of the resource's own mesh, as high as the column is full.
internal class MixedStockpileVisual : MonoBehaviour
{
	private const float ColumnHeight = 1.8f;

	private const float ColumnWidth = 0.85f;

	private const float BaseHeight = 0.3f;

	// Column centres on the 2x2 pallet, in the order the columns are handed out.
	private static readonly Vector2[] Centres = new Vector2[4]
	{
		new Vector2(-0.5f, -0.5f),
		new Vector2(0.5f, -0.5f),
		new Vector2(-0.5f, 0.5f),
		new Vector2(0.5f, 0.5f)
	};

	private string _signature = string.Empty;

	public static void Refresh(Recepteur recepteur, bool mixed)
	{
		MixedStockpileVisual visual = recepteur.GetComponentInChildren<MixedStockpileVisual>(true);
		if (!mixed)
		{
			if (visual != null)
			{
				visual.Clear();
			}
			return;
		}
		if (visual == null)
		{
			GameObject go = new GameObject("CSP_MixedColumns");
			go.transform.SetParent(recepteur.transform, false);
			visual = go.AddComponent<MixedStockpileVisual>();
		}
		visual.Build(recepteur);
	}

	private void Clear()
	{
		_signature = string.Empty;
		for (int i = transform.childCount - 1; i >= 0; i--)
		{
			Destroy(transform.GetChild(i).gameObject);
		}
	}

	private void Build(Recepteur recepteur)
	{
		List<Ressource> types = MixedStockpiles.Types(recepteur);
		string signature = string.Empty;
		foreach (Ressource resource in types)
		{
			signature += resource.GetType().Name + ":" + recepteur.ContentDescription.Value(resource) + ";";
		}
		// Every display on the pallet asks for a refresh; build once per content change.
		if (signature == _signature)
		{
			return;
		}
		Clear();
		_signature = signature;
		Vector2[] centres = Centres;
		int column = 0;
		foreach (Ressource resource in types)
		{
			int perColumn = MixedStockpiles.PerColumn(recepteur, resource);
			int left = recepteur.ContentDescription.Value(resource);
			while (left > 0 && column < centres.Length)
			{
				int inColumn = Mathf.Min(left, perColumn);
				BuildColumn(resource, centres[column], (float)inColumn / perColumn);
				left -= inColumn;
				column++;
			}
		}
	}

	private void BuildColumn(Ressource resource, Vector2 centre, float fill)
	{
		Factory.AssetKey key = Description.RepresentativeOf(resource);
		GameObject prefab = (key != null) ? Factory.Peek(key) : null;
		MeshFilter sourceFilter = (prefab != null) ? prefab.GetComponentInChildren<MeshFilter>(true) : null;
		MeshRenderer sourceRenderer = (sourceFilter != null) ? sourceFilter.GetComponent<MeshRenderer>() : null;
		if (sourceFilter == null || sourceFilter.sharedMesh == null || sourceRenderer == null)
		{
			return;
		}
		Bounds bounds = sourceFilter.sharedMesh.bounds;
		float scale = Mathf.Min(ColumnWidth / Mathf.Max(0.01f, bounds.size.x), ColumnWidth / Mathf.Max(0.01f, bounds.size.z));
		scale = Mathf.Min(scale, ColumnHeight / Mathf.Max(0.01f, bounds.size.y));
		float unitHeight = bounds.size.y * scale;
		int units = Mathf.Max(1, Mathf.RoundToInt(fill * ColumnHeight / Mathf.Max(0.01f, unitHeight)));
		for (int i = 0; i < units; i++)
		{
			GameObject piece = new GameObject(resource.GetType().Name);
			piece.transform.SetParent(transform, false);
			piece.transform.localScale = Vector3.one * scale;
			// Alternate the direction of each layer, like the game's own stacks.
			piece.transform.localRotation = Quaternion.Euler(0f, (i % 2 == 0) ? 0f : 90f, 0f);
			Vector3 offset = piece.transform.localRotation * (bounds.center * scale);
			piece.transform.localPosition = new Vector3(centre.x, BaseHeight + unitHeight * (i + 0.5f), centre.y) - offset;
			piece.AddComponent<MeshFilter>().sharedMesh = sourceFilter.sharedMesh;
			piece.AddComponent<MeshRenderer>().sharedMaterials = sourceRenderer.sharedMaterials;
		}
	}
}
