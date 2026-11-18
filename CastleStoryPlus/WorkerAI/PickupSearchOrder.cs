using System;
using System.Collections.Generic;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.AI.KnowledgeSpace;
using Brix.Game.Components;
using Brix.Game.Semantique;
using Brix.Utils;
using CastleStoryPlus.Core;
using HarmonyLib;

namespace CastleStoryPlus.WorkerAI;

// A worker that needs a resource (a builder's material, a workshop's ingredient) looks for the nearest thing holding
// it (Knowledge.ClosestObjectToPickup): every loose block, item and stockpile it can reach, of every resource. The game
// tested each one first for whether the worker's faction can see it and only then whether it holds the resource. The
// sight test is the costly one: a good on a pallet asks its pallet's sight in turn, with several component look-ups.
// So a search went through the sight of some 400 things to find the few holding the resource, close to 2 ms at once.
// Now the cheap tests come first (holds the resource, same or neutral faction) and only those that pass are tested for
// sight; the nearest is chosen with the game's own distance (a stockpile's pick-up bonus included), so the answer is
// the same.
[Feature(Features.FasterTaskSearch, Features.FasterTaskSearchInfo)]
[HarmonyPatch(typeof(Knowledge), nameof(Knowledge.ClosestObjectToPickup))]
// Low: the prefixes that note who searches (reservations, research) must run first, the loop below reads their state.
[HarmonyPriority(Priority.Low)]
internal static class PickupSearchOrder
{
	private static readonly Func<Labor, IGameComponent, float> Distance = KnowledgeUtility.GetPickupDistance();

	private static bool Prefix(Knowledge __instance, Labor source, Ressource resource, Faction faction, ref IGameComponent __result)
	{
		HashSet<IGameComponent> known = CollectionPool.RequestHashSet<IGameComponent>();
		try
		{
			__instance.ReacheableObjectsFromWithKey(source, Knowledge.InterestingResourceObject, known);
			float best = float.MaxValue;
			IGameComponent closest = null;
			foreach (IGameComponent gc in known)
			{
				if (gc == null || !Knowledge._ClosestObjectToPickup_Test(gc, resource) || !Affiliation.IsSameOrNeutral(gc.faction, faction) || !LocalFactionVision.ObjectIsVisibleForFaction(gc, faction))
				{
					continue;
				}
				float distance = Distance(source, gc);
				if (distance < best)
				{
					best = distance;
					closest = gc;
				}
			}
			__result = closest;
		}
		finally
		{
			known.Dispose();
		}
		return false;
	}
}

// The same for the nearest stockpile with room for what a worker carries (Knowledge.ClosestStorageWithRoomFor, asked
// for every block of a quarry a worker weighs and every load it brings back): room and the faction are tested before
// the stockpile's sight.
[Feature(Features.FasterTaskSearch, Features.FasterTaskSearchInfo)]
[HarmonyPatch(typeof(Knowledge), nameof(Knowledge.ClosestStorageWithRoomFor))]
internal static class StorageSearchOrder
{
	private static readonly Func<Labor, IGameComponent, IGameComponent, float> Distance = KnowledgeUtility.GetStorageDistance();

	private static bool Prefix(Knowledge __instance, Labor source, IGameComponent itemToStore, Faction faction, ref IGameComponent __result)
	{
		HashSet<IGameComponent> known = CollectionPool.RequestHashSet<IGameComponent>();
		try
		{
			__instance.ReacheableObjectsFromWithKey(source, Knowledge.storageObjects, known);
			float best = float.MaxValue;
			IGameComponent closest = null;
			foreach (IGameComponent gc in known)
			{
				if (gc == null || !Knowledge._ClosestStorageWithRoomFor_Test(gc, itemToStore) || !Affiliation.IsSameOrNeutral(gc.faction, faction) || !LocalFactionVision.ObjectIsVisibleForFaction(gc, faction))
				{
					continue;
				}
				float distance = Distance(source, gc, itemToStore);
				if (distance < best)
				{
					best = distance;
					closest = gc;
				}
			}
			__result = closest;
		}
		finally
		{
			known.Dispose();
		}
		return false;
	}
}
