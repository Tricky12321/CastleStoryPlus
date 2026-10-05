using System.Collections.Generic;
using Brix.External.Factories;
using Brix.Game.AI;
using Brix.Game.AI.KnowledgeSpace;
using Brix.Game.AI.Nodes;
using Brix.Game.Components;
using Brix.Game.Semantique;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.WorkerAI;

// Server: a worker reserves the resources it sets out to fetch, so no other worker walks to the same ones.
// The game's search for something to pick up only looks at what lies there, not at who is already on the way:
// several workers walked to the same loose item or to the last few items of a stockpile, and all but the first
// found nothing, failed the task and searched again from wherever they had walked to.
// - a loose item is reserved whole by the worker going for it;
// - in a stockpile, the worker reserves as many items of the kind as it will take (its bag room, the task's limit);
//   other workers only choose the stockpile while it still holds unreserved items of what they look for.
// A reservation ends when the worker has picked up, starts another task, is gone, or after 30 s.
[Feature(Features.ResourceReservation, Features.ResourceReservationInfo)]
internal static class ResourceReservation
{
	private class Reservation
	{
		public Labor Labor;

		public Task Task;

		// The loose item, or the stockpile (any recepteur that is not a worker's bag).
		public GameObject Source;

		public Factory.AssetKey Key;

		public int Amount;

		public float Until;
	}

	private const float TimeoutSeconds = 30f;

	private static readonly Dictionary<Labor, Reservation> ByLabor = new Dictionary<Labor, Reservation>();

	private static readonly Dictionary<GameObject, List<Reservation>> BySource = new Dictionary<GameObject, List<Reservation>>();

	private static readonly Dictionary<Factory.AssetKey, int> Counts = new Dictionary<Factory.AssetKey, int>();

	// The worker whose search is running (searches run on the main thread, one at a time).
	internal static Labor Searcher;

	// Set by the worker trace to see what a search would find without the reservations.
	internal static bool Bypass;

	private static void Enable()
	{
		GameSession.OnLeave(() =>
		{
			ByLabor.Clear();
			BySource.Clear();
			Searcher = null;
		});
	}

	public static void Reserve(Labor labor, GameComponent item, int upTo)
	{
		if (labor.IsNullOrReleased() || item.IsNullOrReleased() || labor.recepteur == null || labor.recepteur.Carries(item))
		{
			return;
		}
		Recepteur parent = item.ParentRecepteur;
		if (parent != null && parent.Fonction == FonctionDeContenant.personnage)
		{
			return;
		}
		GameObject source = (parent != null) ? parent.gameObject : item.gameObject;
		Factory.AssetKey key = item.AssetKey;
		int amount = 1;
		if (parent != null)
		{
			amount = labor.recepteur.CanFitUpTo(key);
			if (upTo > 0 && upTo < amount)
			{
				amount = upTo;
			}
			amount = Mathf.Clamp(amount, 1, Mathf.Max(1, CountOf(parent, key)));
		}
		if (ByLabor.TryGetValue(labor, out Reservation current) && current.Source == source && current.Key == key)
		{
			current.Amount = amount;
			current.Task = labor.CurrentTask;
			current.Until = Time.time + TimeoutSeconds;
			return;
		}
		Release(labor);
		Reservation reservation = new Reservation
		{
			Labor = labor,
			Task = labor.CurrentTask,
			Source = source,
			Key = key,
			Amount = amount,
			Until = Time.time + TimeoutSeconds
		};
		ByLabor[labor] = reservation;
		if (!BySource.TryGetValue(source, out List<Reservation> list))
		{
			list = new List<Reservation>();
			BySource[source] = list;
		}
		list.Add(reservation);
	}

	public static void Release(Labor labor)
	{
		if (labor == null || !ByLabor.TryGetValue(labor, out Reservation reservation))
		{
			return;
		}
		ByLabor.Remove(labor);
		if (reservation.Source != null && BySource.TryGetValue(reservation.Source, out List<Reservation> list))
		{
			list.Remove(reservation);
			if (list.Count == 0)
			{
				BySource.Remove(reservation.Source);
			}
		}
	}

	// Whether the search may pick this loose item or stockpile for the resource.
	public static bool IsAvailable(IGameComponent gc, Ressource resource, Labor labor)
	{
		GameObject source = gc.GameObject;
		if (source == null || !BySource.TryGetValue(source, out List<Reservation> list))
		{
			return true;
		}
		Prune(list);
		if (!HasOthers(list, labor))
		{
			return true;
		}
		Recepteur recepteur = gc.recepteur;
		if (gc.ParentRecepteur == null && (recepteur == null || !recepteur.Carries(resource)))
		{
			// A loose item: one worker at a time.
			return false;
		}
		Counts.Clear();
		foreach (IDescriptor stored in recepteur.StoredItems)
		{
			if (stored == null || stored.GameObject == null || !Apparence.PrincipaleIs(stored.GameObject, resource))
			{
				continue;
			}
			Counts.TryGetValue(stored.AssetKey, out int count);
			Counts[stored.AssetKey] = count + 1;
		}
		foreach (Reservation reservation in list)
		{
			if (reservation.Labor != labor && Counts.TryGetValue(reservation.Key, out int count))
			{
				Counts[reservation.Key] = count - reservation.Amount;
			}
		}
		foreach (int left in Counts.Values)
		{
			if (left > 0)
			{
				return true;
			}
		}
		return false;
	}

	public static bool IsReservedByOther(GameObject item, Labor labor)
	{
		if (item == null || !BySource.TryGetValue(item, out List<Reservation> list))
		{
			return false;
		}
		Prune(list);
		return HasOthers(list, labor);
	}

	private static bool HasOthers(List<Reservation> list, Labor labor)
	{
		foreach (Reservation reservation in list)
		{
			if (reservation.Labor != labor)
			{
				return true;
			}
		}
		return false;
	}

	private static void Prune(List<Reservation> list)
	{
		for (int i = list.Count - 1; i >= 0; i--)
		{
			if (!IsValid(list[i]))
			{
				Release(list[i].Labor);
			}
		}
	}

	// A reservation lasts while its worker is on the task it was made for.
	private static bool IsValid(Reservation reservation)
	{
		if (Time.time > reservation.Until || reservation.Labor.IsNullOrReleased() || reservation.Source.IsNullOrReleased())
		{
			return false;
		}
		return reservation.Labor.CurrentTask == reservation.Task;
	}

	private static int CountOf(Recepteur recepteur, Factory.AssetKey key)
	{
		int count = 0;
		foreach (IDescriptor stored in recepteur.StoredItems)
		{
			if (stored != null && stored.AssetKey == key)
			{
				count++;
			}
		}
		return count;
	}
}

// The search for something to pick up: remember who is searching.
[Feature(Features.ResourceReservation, Features.ResourceReservationInfo)]
[HarmonyPatch(typeof(Knowledge), nameof(Knowledge.ClosestObjectToPickup))]
internal static class ResourceReservationSearchPatch
{
	private static void Prefix(Labor source)
	{
		ResourceReservation.Searcher = source;
	}

	private static void Finalizer()
	{
		ResourceReservation.Searcher = null;
	}
}

// ... and skip loose items and stockpiles whose resources other workers have reserved.
[Feature(Features.ResourceReservation, Features.ResourceReservationInfo)]
[HarmonyPatch(typeof(Knowledge), "_ClosestObjectToPickup_Test")]
internal static class ResourceReservationTestPatch
{
	private static void Postfix(IGameComponent gc, Ressource ressource, ref bool __result)
	{
		if (__result && !ResourceReservation.Bypass && ResourceReservation.Searcher != null && !ResourceReservation.IsAvailable(gc, ressource, ResourceReservation.Searcher))
		{
			__result = false;
		}
	}
}

// The search has picked an item: reserve it at once, before the next worker searches.
[Feature(Features.ResourceReservation, Features.ResourceReservationInfo)]
[HarmonyPatch(typeof(PickUpNode), "NodeFindAndPickUp")]
internal static class ResourceReservationFoundPatch
{
	private static void Postfix(Labor labor, int upTo, GameObject item)
	{
		if (!item.IsNullOrReleased())
		{
			ResourceReservation.Reserve(labor, item.GetComponent<GameComponent>(), upTo);
		}
	}
}

// Every walk to pick something up (also cleanup and stockpile consolidation) reserves what it goes for.
[Feature(Features.ResourceReservation, Features.ResourceReservationInfo)]
[HarmonyPatch(typeof(PickUpNode), "_GoPickUp")]
internal static class ResourceReservationGoPatch
{
	private static void Prefix(Labor labor, GameComponent itemToPickUp, int upTo)
	{
		ResourceReservation.Reserve(labor, itemToPickUp, upTo);
	}
}

// Picked up: the reservation has done its job.
[Feature(Features.ResourceReservation, Features.ResourceReservationInfo)]
[HarmonyPatch(typeof(PickUpNode), nameof(PickUpNode.TakeSome))]
internal static class ResourceReservationTakePatch
{
	private static void Finalizer(Labor labor)
	{
		ResourceReservation.Release(labor);
	}
}

// A pooled worker that is reused has no reservation.
[Feature(Features.ResourceReservation, Features.ResourceReservationInfo)]
[HarmonyPatch(typeof(Labor), nameof(Labor.OnRequested))]
internal static class ResourceReservationResetPatch
{
	private static void Prefix(Labor __instance)
	{
		ResourceReservation.Release(__instance);
	}
}
