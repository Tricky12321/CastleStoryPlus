using System.Collections.Generic;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Game.Semantique;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.Core;

// The live instances of a component type, kept as they are made and destroyed. Object.FindObjectsOfType walks the
// whole scene and takes 50 ms or more in a built-up world, so features that look at every worker, storage or tool
// bag now and then ask here instead. Active answers what FindObjectsOfType would: those whose game object is active
// (pooled ones sitting in the pool are not).
internal static class Live<T> where T : Component
{
	private static readonly HashSet<T> Items = new HashSet<T>();

	private static readonly System.Predicate<T> IsGone = (T item) => item == null;

	internal static void Add(T item)
	{
		if (item != null)
		{
			Items.Add(item);
		}
	}

	internal static void Remove(T item)
	{
		Items.Remove(item);
	}

	// A new list, so callers may change the world while they go through it.
	internal static List<T> Active()
	{
		List<T> result = new List<T>(Items.Count);
		Active(result);
		return result;
	}

	// Fills the list (cleared first) with the active ones; destroyed ones are dropped on the way.
	internal static void Active(List<T> into)
	{
		into.Clear();
		bool gone = false;
		foreach (T item in Items)
		{
			if (item == null)
			{
				gone = true;
				continue;
			}
			if (item.gameObject.activeInHierarchy)
			{
				into.Add(item);
			}
		}
		if (gone)
		{
			Items.RemoveWhere(IsGone);
		}
	}
}

[Feature]
[HarmonyPatch(typeof(Labor), "Awake")]
internal static class LiveLaborAddPatch
{
	private static void Postfix(Labor __instance)
	{
		Live<Labor>.Add(__instance);
	}
}

[Feature]
[HarmonyPatch(typeof(Labor), "OnDestroy")]
internal static class LiveLaborRemovePatch
{
	private static void Postfix(Labor __instance)
	{
		Live<Labor>.Remove(__instance);
	}
}

[Feature]
[HarmonyPatch(typeof(Recepteur), "Start")]
internal static class LiveRecepteurAddPatch
{
	private static void Postfix(Recepteur __instance)
	{
		Live<Recepteur>.Add(__instance);
	}
}

[Feature]
[HarmonyPatch(typeof(Recepteur), "OnDestroy")]
internal static class LiveRecepteurRemovePatch
{
	private static void Postfix(Recepteur __instance)
	{
		Live<Recepteur>.Remove(__instance);
	}
}

// A tool bag has no OnDestroy of its own; destroyed ones are dropped when asked for.
[Feature]
[HarmonyPatch(typeof(Toolbag), nameof(Toolbag.Start))]
internal static class LiveToolbagAddPatch
{
	private static void Postfix(Toolbag __instance)
	{
		Live<Toolbag>.Add(__instance);
	}
}

[Feature]
[HarmonyPatch(typeof(FireflyNest), "Awake")]
internal static class LiveNestAddPatch
{
	private static void Postfix(FireflyNest __instance)
	{
		Live<FireflyNest>.Add(__instance);
	}
}

[Feature]
[HarmonyPatch(typeof(FireflyNest), "OnDestroy")]
internal static class LiveNestRemovePatch
{
	private static void Postfix(FireflyNest __instance)
	{
		Live<FireflyNest>.Remove(__instance);
	}
}
