using System.Collections.Generic;
using Brix.Game.AI;
using Brix.Game.AI.KnowledgeSpace;
using Brix.Game.Components;
using Brix.Game.Semantique;
using CastleStoryPlus.Core;
using CastleStoryPlus.Market;
using HarmonyLib;
using Smooth.Slinq;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.WorkerAI;

// A worker emptying its bag (after digging, harvesting, a clean-up, before a new task) takes what it carries to the
// best storage (Labor.BestRecepteurToStoreInventory, asked again for every stop until the bag is empty). Now a
// workshop of its own faction that still needs something in the bag for its queue (CraftingStation.StuffToFetch:
// the ingredients still missing, or its fuel) gets it first, when it is not much farther than that storage: at most
// ExtraBlocks more as the crow flies from the worker. Only what the workshop is missing is put in (less what its own
// worker already carries of it); the rest of the bag goes on to storage as before. Markets (their own deliveries),
// workshops the worker cannot reach and bags no storage would take are left as the game has them. A workshop a worker
// has just delivered to is not chosen by that worker again for RetrySeconds, so a delivery that puts nothing in (the
// workshop got it from someone else meanwhile) cannot keep it walking back and forth. Host only: the items move
// through the game's storages, which the game sends to the other players.
[Feature(Features.UnloadToWorkshops, Features.UnloadToWorkshopsInfo)]
[HarmonyPatch(typeof(Labor), nameof(Labor.BestRecepteurToStoreInventory))]
internal static class UnloadToWorkshops
{
	// How much farther (blocks, straight line from the worker) a workshop may be than the storage the game chose.
	internal const float ExtraBlocks = 8f;

	private const float RetrySeconds = 15f;

	// A target is forgotten when the worker has not delivered this long after it was chosen.
	private const float TargetSeconds = 120f;

	private struct Visit
	{
		public CraftingStation Station;

		public float At;
	}

	// The workshop each worker last delivered to, and when.
	private static readonly Dictionary<Labor, Visit> Recent = new Dictionary<Labor, Visit>();

	// Workers on their way to a workshop chosen here, until they deliver.
	private static readonly Dictionary<Labor, Visit> Targets = new Dictionary<Labor, Visit>();

	private static readonly List<IOperable<Labor>> Projects = new List<IOperable<Labor>>();

	private static readonly List<GameObject> Items = new List<GameObject>();

	private static void Enable()
	{
		GameSession.OnLeave(() =>
		{
			Recent.Clear();
			Targets.Clear();
		});
	}

	private static void Postfix(Labor __instance, ref GameObject __result)
	{
		if (!NetworkServer.active || __result == null || __instance == null || __instance.recepteur == null || __instance.recepteur.IsEmpty())
		{
			return;
		}
		CraftingStation station = Choose(__instance, __result);
		if (station == null)
		{
			return;
		}
		Targets[__instance] = new Visit { Station = station, At = Time.time };
		__result = station.gameObject;
	}

	// The nearest workshop that needs something in the worker's bag and is within ExtraBlocks of the storage's
	// distance; null when there is none.
	private static CraftingStation Choose(Labor labor, GameObject storage)
	{
		if (labor.faction == null)
		{
			return null;
		}
		Prune();
		Vector3 position = labor.transform.position;
		float limit = Vector3.Distance(position, storage.transform.position) + ExtraBlocks;
		float best = limit * limit;
		CraftingStation chosen = null;
		Projects.Clear();
		ProjectDatabase.For(labor.faction).AvailableProjects(Projects, labor);
		foreach (IOperable<Labor> project in Projects)
		{
			CraftingStation station = project as CraftingStation;
#if !ESSENTIALS
			if (station == null || station is MarketStation || station.IsNullOrReleased() || station.recepteur == null || station.gameObject == storage)
#else
			if (station == null || station.IsNullOrReleased() || station.recepteur == null || station.gameObject == storage)
#endif
			{
				continue;
			}
			float distance = (station.transform.position - position).sqrMagnitude;
			if (distance > best)
			{
				continue;
			}
			if (Recent.TryGetValue(labor, out Visit visit) && visit.Station == station && Time.time - visit.At < RetrySeconds)
			{
				continue;
			}
			if (!Needs(station, labor) || !Knowledge.Instance.CanReach(labor, station))
			{
				continue;
			}
			best = distance;
			chosen = station;
		}
		Projects.Clear();
		return chosen;
	}

	// Whether the workshop is missing something the worker carries and has room for it.
	private static bool Needs(CraftingStation station, Labor labor)
	{
		Description needed = station.StuffToFetch();
		if (needed == null)
		{
			return false;
		}
		foreach (Ressource resource in needed.GetRessources().ToList())
		{
			if (Missing(station, labor, resource) > 0 && FirstCarried(labor, resource, station) != null)
			{
				return true;
			}
		}
		return false;
	}

	// How many of the resource the workshop still needs, less what its own worker (another one) carries of it.
	private static int Missing(CraftingStation station, Labor labor, Ressource resource)
	{
		int missing = resource.quantifiable.valeur;
		Labor worker = station.Operator;
		if (worker != null && worker != labor && worker.recepteur != null)
		{
			missing -= worker.recepteur.ContentDescription.Value(resource.GetType());
		}
		return missing;
	}

	private static GameObject FirstCarried(Labor labor, Ressource resource, CraftingStation station)
	{
		foreach (IDescriptor item in labor.recepteur.StoredItems)
		{
			GameObject go = item?.GameObject;
			if (go != null && Apparence.PrincipaleIs(go, resource) && station.recepteur.HasRoomFor(go))
			{
				return go;
			}
		}
		return null;
	}

	// The storing step at a workshop chosen here (Transaction.StoreEverything from the worker): puts in only what the
	// workshop is missing. False when it is not such a delivery.
	internal static bool Deliver(GameObject from, GameObject into)
	{
		if (!NetworkServer.active || from == null || into == null)
		{
			return false;
		}
		Labor labor = from.GetComponent<Labor>();
		if (labor == null || !Targets.TryGetValue(labor, out Visit visit))
		{
			return false;
		}
		CraftingStation station = visit.Station;
		if (station.IsNullOrReleased() || station.gameObject != into || Time.time - visit.At > TargetSeconds)
		{
			Targets.Remove(labor);
			return false;
		}
		Targets.Remove(labor);
		Recent[labor] = new Visit { Station = station, At = Time.time };
		Description needed = station.StuffToFetch();
		if (needed == null || labor.recepteur == null)
		{
			return true;
		}
		int stored = 0;
		foreach (Ressource resource in needed.GetRessources().ToList())
		{
			int missing = Missing(station, labor, resource);
			if (missing <= 0)
			{
				continue;
			}
			Items.Clear();
			foreach (IDescriptor item in labor.recepteur.StoredItems)
			{
				GameObject go = item?.GameObject;
				if (go != null && Apparence.PrincipaleIs(go, resource))
				{
					Items.Add(go);
				}
			}
			foreach (GameObject go in Items)
			{
				if (missing <= 0 || !station.recepteur.HasRoomFor(go))
				{
					break;
				}
				station.recepteur.AddObject(go);
				missing--;
				stored++;
			}
			Items.Clear();
		}
		Plugin.Log.LogInfo("UnloadToWorkshops: " + labor.name + " put " + stored + " in " + station.name + ", bag left " + labor.recepteur.ContentString());
		return true;
	}

	private static void Prune()
	{
		if (Targets.Count < 64 && Recent.Count < 256)
		{
			return;
		}
		float now = Time.time;
		List<Labor> old = new List<Labor>();
		foreach (KeyValuePair<Labor, Visit> pair in Targets)
		{
			if (pair.Key == null || now - pair.Value.At > TargetSeconds)
			{
				old.Add(pair.Key);
			}
		}
		foreach (Labor labor in old)
		{
			Targets.Remove(labor);
		}
		old.Clear();
		foreach (KeyValuePair<Labor, Visit> pair in Recent)
		{
			if (pair.Key == null || now - pair.Value.At > RetrySeconds)
			{
				old.Add(pair.Key);
			}
		}
		foreach (Labor labor in old)
		{
			Recent.Remove(labor);
		}
	}
}

// The storing step itself: at a workshop chosen by UnloadToWorkshops only what it is missing goes in, not the whole
// bag (the game puts in everything the workshop has room for).
[Feature(Features.UnloadToWorkshops, Features.UnloadToWorkshopsInfo)]
[HarmonyPatch(typeof(Transaction), nameof(Transaction.StoreEverything))]
internal static class UnloadToWorkshopsStorePatch
{
	private static bool Prefix(GameObject from, GameObject into)
	{
		return !UnloadToWorkshops.Deliver(from, into);
	}
}
