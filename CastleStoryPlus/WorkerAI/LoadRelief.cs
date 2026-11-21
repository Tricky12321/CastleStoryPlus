using System.Collections.Generic;
using BepInEx.Configuration;
using Brix.Engine;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.AI.InstructionType;
using Brix.Game.AI.KnowledgeSpace;
using Brix.Game.AI.Nodes;
using Brix.Game.Components;
using Brix.Game.Semantique;
using CastleStoryPlus.Army;
using CastleStoryPlus.Combat;
using CastleStoryPlus.Core;
using CastleStoryPlus.Pathfinding;
using HarmonyLib;
using Motus.Behavior;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.WorkerAI;

// Workers no longer stand frozen with a load they cannot get rid of (issue #49). Every task that needs empty hands
// or bag room (building, digging, harvesting, workshop fetching) turns a loaded worker down, and only the idle task
// made it store the load: "store or drop", which looked for a storage for the first thing in the bag only, so a bag
// of stones, clay and crystal with no room for the stones never had its crystal stored either; then it waited 1, 2,
// 3 and 4 seconds and tried again, and at last put the load down where it stood, which did nothing when there was no
// free spot for that many items. The worker kept its load, took no other work and started over. And a worker whose
// task took it (the storage looked free) and then failed on storage ("no corresponding storage") went from that task
// to the same one again, never reaching the idle task at all.
// Now:
// - "Store or drop" stores whatever in the bag has room somewhere, kind by kind, and puts the rest down at once (one
//   more look after a second, for a stockpile that was full a moment ago) near the base's gather point (if that is
//   within [LoadRelief] DropWalk blocks, else where the worker stands), always: no free spot means the next one
//   free. The load lies there until a stockpile has room; the game's "you need more empty stockpiles" stays.
// - A loaded worker that for [LoadRelief] Seconds (20) has not got rid of any of its load while it stood without a
//   task, idled or had tasks fail is given that "store or drop" itself.
// - A workshop worker whose recipe did not progress for [LoadRelief] RecipeSeconds (90) in "work on recipe" (it could
//   not get to the station, say) has that task cancelled; the workshop gives it its next step afresh. Research
//   progresses with every stroke, so it is not affected.
// A worker under a player's order, or holding what the player gave it (HoldAfterOrder), is left alone. Host only, the
// players' workers.
// In a deep quarry with a stair one block wide (the "Doesnt matter" save) the store went round in circles: every storage
// was a long search and a failed walk from the quarry floor, StuckReset cut the store off after 10 s of standing and put
// the load down on the floor or the stair, the worker dug on, and the bags it left were cleanup jobs nobody could get
// to. And a worker that had just dug its way out of a pit had looked for storage while still in it, found none, walked
// to the gather point and put the load down there although stockpiles had room. Now:
// - A storage a worker failed to get to is passed by from about the same place for a while
//   (EscapeRules.UnreachableStoreSeconds), so the next "store or drop" does not walk the same failed way again.
// - After the walk to the gather point the worker looks for storage again from there before it puts anything down.
// - A load is put down only on open ground on the base's routes: never on a stair, a climb or a quarry's floor. A worker
//   standing on such a place walks to the nearest open ground first, and keeps the load when it cannot get there.
// - StuckReset leaves the store sequence alone; a store in which the worker has not moved for
//   EscapeRules.StoreStillSeconds is broken off here instead, and the load is kept.
[Feature(Features.LoadRelief, Features.LoadReliefInfo)]
internal sealed class LoadRelief : MonoBehaviour
{
	private const float CheckSeconds = 1f;

	// A failed task while loaded counts as this many seconds stalled.
	private const float FailSeconds = 5f;

	// How much of a second stalled one second of work takes off.
	private const float WorkDecay = 0.25f;

	// How many storages one "store or drop" goes to before it puts the rest down.
	private const int MaxStores = 6;

	// How far round the gather point the load is put down.
	private const float DropRadius = 4f;

	private sealed class Watch
	{
		public int Count;

		public float Stalled;

		// While storing: where the worker stood, and since when.
		public Vector3 StoreAnchor;

		public float StoreSince = -1f;
	}

	// A storage a worker failed to get to: from where, and until when it is passed by.
	private sealed class Unreachable
	{
		public GameObject Storage;

		public XYZ From;

		public float Until;
	}

	private static readonly Dictionary<Labor, List<Unreachable>> UnreachableStores = new Dictionary<Labor, List<Unreachable>>();

	// How far a worker that cannot put its load down where it stands looks for open ground on the routes (places).
	private const int MaxDropSearch = 1500;

	private sealed class RecipeWatch
	{
		public Recipe Recipe;

		public int Work;

		public float Since;
	}

	private static ConfigEntry<float> _seconds;

	private static ConfigEntry<float> _recipeSeconds;

	private static ConfigEntry<float> _dropWalk;

	private static readonly Dictionary<Labor, Watch> Watches = new Dictionary<Labor, Watch>();

	private static readonly Dictionary<Labor, RecipeWatch> Recipes = new Dictionary<Labor, RecipeWatch>();

	private readonly List<Labor> _labors = new List<Labor>();

	private float _next;

	private static void Enable()
	{
		_seconds = Plugin.Cfg.Bind("LoadRelief", "Seconds", 20f, new ConfigDescription("A loaded worker that stood without work, idled or failed tasks this long without getting rid of any of its load is sent to store it or put it down (0: off).", new AcceptableValueRange<float>(0f, 300f)));
		_recipeSeconds = Plugin.Cfg.Bind("LoadRelief", "RecipeSeconds", 90f, new ConfigDescription("A workshop worker whose recipe did not progress this long has its task cancelled (0: off).", new AcceptableValueRange<float>(0f, 600f)));
		_dropWalk = Plugin.Cfg.Bind("LoadRelief", "DropWalk", 48f, new ConfigDescription("A load nothing can store is put down near the gather point when that is at most this many blocks away, else where the worker stands.", new AcceptableValueRange<float>(0f, 500f)));
		GameSession.OnLeave(() =>
		{
			Watches.Clear();
			Recipes.Clear();
			UnreachableStores.Clear();
		});
		Plugin.Root.AddComponent<LoadRelief>();
	}

	private void Update()
	{
		if (!NetworkServer.active || Time.time < _next)
		{
			return;
		}
		_next = Time.time + CheckSeconds;
		Live<Labor>.Active(_labors);
		foreach (Labor labor in _labors)
		{
			try
			{
				CheckLoad(labor);
				CheckRecipe(labor);
			}
			catch (System.Exception ex)
			{
				Plugin.Log.LogWarning("LoadRelief: " + ex.Message);
				Watches.Remove(labor);
				Recipes.Remove(labor);
			}
		}
	}

	internal static bool IsPlayers(Labor labor)
	{
		return labor != null && labor.faction != null && !labor.faction.isAI;
	}

	private static bool LeftAlone(Labor labor)
	{
		return labor.IsPhasedOut || labor.Activity == Activity.Fighting || PlayerOrdersFirst.HasPlayerOrder(labor) || HoldAfterOrder.IsHeld(labor);
	}

	internal static bool IsStoring(Task task)
	{
		string name = (task != null) ? task.Description : null;
		return name == "GoStoreOrDropAt" || name == "GoStoreOrDrop" || name == "GoStore";
	}

	private static void CheckLoad(Labor labor)
	{
		if (labor == null || !IsPlayers(labor) || labor.recepteur == null || labor.recepteur.IsEmpty() || LeftAlone(labor) || _seconds.Value <= 0f)
		{
			Watches.Remove(labor);
			return;
		}
		int count = labor.recepteur.StoredItems.Count;
		if (!Watches.TryGetValue(labor, out Watch watch) || watch.Count != count)
		{
			Watches[labor] = new Watch { Count = count };
			return;
		}
		Task task = labor.CurrentTask;
		if (IsStoring(task))
		{
			watch.Stalled = 0f;
			CheckStore(labor, watch);
			return;
		}
		watch.StoreSince = -1f;
		// No task, or the idle task's walk or sit: nothing will take the load off it.
		if (task == null || labor.Activity == Activity.Chilling)
		{
			watch.Stalled += CheckSeconds;
		}
		else
		{
			// Working with the load on its back (felling trees, say): a failure now and then does not add up.
			watch.Stalled = Mathf.Max(0f, watch.Stalled - CheckSeconds * WorkDecay);
		}
		if (watch.Stalled < _seconds.Value)
		{
			return;
		}
		Plugin.Log.LogInfo("LoadRelief: " + labor.name + " carried " + labor.recepteur.ContentString() + " for " + Mathf.RoundToInt(watch.Stalled) + " s without work ('" + ((task != null) ? task.Description : "-") + "'): sent to store or put it down");
		watch.Stalled = 0f;
		Relieve(labor);
	}

	// A store or drop in which the worker has not moved for EscapeRules.StoreStillSeconds is broken off; the load is
	// kept (StuckReset, which would put it down where the worker stands, leaves the store sequence alone).
	private static void CheckStore(Labor labor, Watch watch)
	{
		Vector3 at = labor.transform.position;
		if (watch.StoreSince < 0f || (at - watch.StoreAnchor).sqrMagnitude > 0.25f)
		{
			watch.StoreAnchor = at;
			watch.StoreSince = Time.time;
			return;
		}
		if (Time.time - watch.StoreSince < EscapeRules.StoreStillSeconds)
		{
			return;
		}
		Plugin.Log.LogInfo("LoadRelief: " + labor.name + " stood still " + Mathf.RoundToInt(Time.time - watch.StoreSince) + " s storing " + labor.recepteur.ContentString() + ": store broken off, load kept");
		watch.StoreSince = -1f;
		labor.AbortWorkSignal.Invoke(TaskResult.ProjectCancelledTask);
	}

	// A loaded worker's task failed: counts towards sending it to store or drop.
	internal static void TaskFailed(Labor labor)
	{
		if (_seconds == null || _seconds.Value <= 0f || labor == null || !IsPlayers(labor) || labor.recepteur == null || labor.recepteur.IsEmpty())
		{
			return;
		}
		int count = labor.recepteur.StoredItems.Count;
		if (!Watches.TryGetValue(labor, out Watch watch) || watch.Count != count)
		{
			watch = new Watch { Count = count };
			Watches[labor] = watch;
		}
		watch.Stalled += FailSeconds;
	}

	internal static void Relieve(Labor labor)
	{
		Location around = DropPlace(labor);
		OrderPackage<Labor> package = Order.Package.OfLocation(Order.Transfer.GoStoreOrDropAt, around, Labor.DecisionSource.Project);
		package.Send(labor);
	}

	private static void CheckRecipe(Labor labor)
	{
		Task task = (labor != null) ? labor.CurrentTask : null;
		CraftingStation station = (labor != null) ? labor.Operable as CraftingStation : null;
		Recipe recipe = (station != null) ? station.CurrentRecipe : null;
		if (task == null || recipe == null || task.Description != "WorkOnRecipe" || !IsPlayers(labor) || _recipeSeconds.Value <= 0f || labor.Activity == Activity.Fighting)
		{
			Recipes.Remove(labor);
			return;
		}
		if (!Recipes.TryGetValue(labor, out RecipeWatch watch) || watch.Recipe != recipe || watch.Work != recipe.work)
		{
			Recipes[labor] = new RecipeWatch { Recipe = recipe, Work = recipe.work, Since = Time.time };
			return;
		}
		if (Time.time - watch.Since < _recipeSeconds.Value)
		{
			return;
		}
		Plugin.Log.LogInfo("LoadRelief: " + labor.name + "'s recipe at " + station.name + " made no progress for " + Mathf.RoundToInt(Time.time - watch.Since) + " s in 'WorkOnRecipe'" + (labor.recepteur.IsEmpty() ? string.Empty : (" (carrying " + labor.recepteur.ContentString() + ")")) + ": task cancelled");
		Recipes.Remove(labor);
		labor.AbortWorkSignal.Invoke(TaskResult.ProjectCancelledTask);
	}

	// ---- store or drop

	internal static StaticNode StoreOrDrop(Labor labor)
	{
		return Node.Sequence.Do((Labor l) => Step(l, 0, false), labor);
	}

	private static Node Step(Labor labor, int stores, bool lookedAgain)
	{
		if (labor.IsNullOrReleased() || labor.recepteur.IsEmpty())
		{
			return Node.Empty;
		}
		GameComponent storage = (stores < MaxStores) ? StorageForLoad(labor) : null;
		if (storage != null)
		{
			return Node.Sequence.Do(Store(labor, storage)).Do((Labor l, int n, bool again) => Step(l, n, again), labor, stores + 1, lookedAgain);
		}
		if (!lookedAgain && stores < MaxStores)
		{
			// A stockpile full a moment ago (or out of reach for a moment) may have room now.
			return Node.Sequence.Do((Labor l) =>
			{
				Knowledge.Instance.ResetExclusionsFor(l.gameObject, 0.5f);
			}, labor).Do(Node.WaitForMilliseconds(1000)).Do((Labor l, int n) => Step(l, n, true), labor, stores);
		}
		return DropNode(labor);
	}

	// After the walk to where the load is put down: storage is looked for again from there (the worker may have looked
	// from a pit it has since got out of), with two more storages to try, then the load is put down without walking on.
	private static Node StepAfterWalk(Labor labor, int stores)
	{
		if (labor.IsNullOrReleased() || labor.recepteur.IsEmpty())
		{
			return Node.Empty;
		}
		GameComponent storage = (stores < MaxStores) ? StorageForLoad(labor) : null;
		if (storage != null)
		{
			return Node.Sequence.Do(Store(labor, storage)).Do((Labor l, int n) => StepAfterWalk(l, n), labor, stores + 1);
		}
		return PutDownNode(labor);
	}

	// The walk to a storage and the store; a failed walk is remembered (the storage is passed by from here for a while).
	private static StaticNode Store(Labor labor, GameComponent storage)
	{
		return Node.Catcher.Do((StaticNode)labor.GoStoreIn(storage)).Catch((Exceptions.WorkFailedException e, Labor l, GameComponent s) =>
		{
			Failed(l, s, e);
		}, labor, storage);
	}

	private static void Failed(Labor labor, GameComponent storage, Exceptions.WorkFailedException e)
	{
		// A storage gone, full or a load gone is no question of reach.
		if (labor.IsNullOrReleased() || storage.IsNullOrReleased() || e is Exceptions.StorageBecameFull || e is Exceptions.InventoryBecameEmpty || e is Exceptions.NullStorage)
		{
			return;
		}
		if (!UnreachableStores.TryGetValue(labor, out List<Unreachable> list))
		{
			list = new List<Unreachable>();
			UnreachableStores[labor] = list;
		}
		list.RemoveAll((Unreachable u) => u.Storage == storage.gameObject || u.Until < Time.time);
		list.Add(new Unreachable { Storage = storage.gameObject, From = XYZ.FromVector3(labor.transform.position), Until = Time.time + EscapeRules.UnreachableStoreSeconds });
		Plugin.Log.LogInfo("LoadRelief: " + labor.name + " could not get to " + storage.name + " (" + e.GetType().Name + "): passed by from here for " + Mathf.RoundToInt(EscapeRules.UnreachableStoreSeconds) + " s");
	}

	private static bool StoreUnreachable(Labor labor, GameObject storage)
	{
		if (!UnreachableStores.TryGetValue(labor, out List<Unreachable> list))
		{
			return false;
		}
		XYZ at = XYZ.FromVector3(labor.transform.position);
		foreach (Unreachable entry in list)
		{
			if (entry.Storage == storage && EscapeRules.StoreStillUnreachable(entry.Until, Time.time, at.x - entry.From.x, at.y - entry.From.y, at.z - entry.From.z))
			{
				return true;
			}
		}
		return false;
	}

	// Somewhere for some of what the worker carries, kind by kind; null when nothing in the bag has room anywhere.
	private static GameComponent StorageForLoad(Labor labor)
	{
		HashSet<string> tried = new HashSet<string>();
		foreach (IDescriptor item in labor.recepteur.StoredItems)
		{
			if (item == null || item.GameObject == null)
			{
				continue;
			}
			Ressource kind = Apparence.PrincipaleOf(item.GameObject);
			if (!tried.Add((kind != null) ? kind.ToString() : item.GameObject.name))
			{
				continue;
			}
			GameObject storage = labor.BestRecepteurToStore(item.GameComponent);
			if (storage != null && !StoreUnreachable(labor, storage))
			{
				GameComponent component = storage.GetComponent<GameComponent>();
				if (component != null)
				{
					return component;
				}
			}
		}
		return null;
	}

	// Walks to where the load is put down (the gather point, or the nearest open ground when the worker stands where
	// nothing may be put down), looks for storage again there, and puts down the rest.
	private static StaticNode DropNode(Labor labor)
	{
		Location place = WalkTo(labor);
		if (place == null && !CanDropHere(labor))
		{
			XYZ? ground = NearestDropPlace(labor);
			if (ground.HasValue)
			{
				place = Location.Of(ground.Value);
			}
		}
		if (place == null)
		{
			return PutDownNode(labor);
		}
		StaticNode walk = Node.Catcher.Do(labor.RunToward(place, precise: false, aggressive: false)).Catch((FailException e) => Node.Empty);
		return Node.Sequence.Do(walk).Do((Labor l) => StepAfterWalk(l, MaxStores - 2), labor);
	}

	private static StaticNode PutDownNode(Labor labor)
	{
		return Node.Sequence.Do((Labor l) =>
		{
			PutDown(l);
		}, labor);
	}

	// Whether the worker may put a load down where it stands (EscapeRules.CanDropAt): on open ground of the base's
	// routes, not on a stair step, a climb or a quarry's floor.
	internal static bool CanDropHere(Labor labor)
	{
		if (labor.IsNullOrReleased())
		{
			return false;
		}
		return CanDropAt(labor.faction, XYZ.FromVector3(labor.transform.position + Vector3.up * 0.1f));
	}

	private static bool CanDropAt(Faction faction, XYZ feet)
	{
		XYZ ground = feet - XYZ.up;
		bool onStair = QuarryGoalProvider2.IsStair(feet) || QuarryGoalProvider2.IsStair(ground) || QuarryStairs.Keeps(ground);
		bool onRoutes = faction == null || !ReachMap.Has(faction) || ReachMap.Near(faction, feet, 0, 1, 0);
		return EscapeRules.CanDropAt(!Voxel.IsEmpty(ground), Voxel.IsEmpty(feet), onStair, QuarryStairs.InQuarry(feet), onRoutes);
	}

	// The nearest place on the base's routes, walking from the worker, where a load may be put down; null when there is
	// none near (or the worker is not on the routes at all: a pit).
	private static XYZ? NearestDropPlace(Labor labor)
	{
		Faction faction = labor.faction;
		if (faction == null || !ReachMap.Has(faction))
		{
			return null;
		}
		XYZ start = XYZ.FromVector3(labor.transform.position + Vector3.up * 0.1f);
		HashSet<XYZ> seen = new HashSet<XYZ> { start };
		Queue<XYZ> open = new Queue<XYZ>();
		open.Enqueue(start);
		XYZ[] sides = { new XYZ(1, 0, 0), new XYZ(-1, 0, 0), new XYZ(0, 0, 1), new XYZ(0, 0, -1) };
		while (open.Count > 0 && seen.Count < MaxDropSearch)
		{
			XYZ cell = open.Dequeue();
			if (cell != start && CanDropAt(faction, cell))
			{
				return cell;
			}
			foreach (XYZ side in sides)
			{
				for (int dy = -1; dy <= 1; dy++)
				{
					XYZ next = cell + side + new XYZ(0, dy, 0);
					if (seen.Contains(next) || !ReachMap.Contains(faction, next))
					{
						continue;
					}
					seen.Add(next);
					open.Enqueue(next);
				}
			}
		}
		return null;
	}

	private static void PutDown(Labor labor)
	{
		if (labor.IsNullOrReleased() || labor.recepteur.IsEmpty())
		{
			return;
		}
		if (!CanDropHere(labor))
		{
			Vector3 here = labor.transform.position;
			Plugin.Log.LogInfo("LoadRelief: " + labor.name + " found no storage for " + labor.recepteur.ContentString() + " and keeps it: it stands at " + Mathf.FloorToInt(here.x) + "," + Mathf.FloorToInt(here.y) + "," + Mathf.FloorToInt(here.z) + ", where nothing is put down (a stair, a climb, a quarry's floor, off the routes)");
			return;
		}
		string load = labor.recepteur.ContentString();
		labor.recepteur.DropAll();
		if (!labor.recepteur.IsEmpty())
		{
			RackEquipDropFirstPatch.ForceDrop(labor);
		}
		Vector3 at = labor.transform.position;
		Plugin.Log.LogInfo("LoadRelief: " + labor.name + " found no storage for " + load + " and put it down at " + Mathf.FloorToInt(at.x) + "," + Mathf.FloorToInt(at.y) + "," + Mathf.FloorToInt(at.z) + (labor.recepteur.IsEmpty() ? string.Empty : " (some is still in its bag)"));
	}

	// Near the gather point when the worker is not there already and it is not too far; null: put down here.
	private static Location WalkTo(Labor labor)
	{
		Faction faction = labor.faction;
		if (faction == null || faction.Gatherpoint == Vector3.zero)
		{
			return null;
		}
		float distance = Vector3.Distance(labor.transform.position, faction.Gatherpoint);
		if (distance <= DropRadius + 2f || distance > _dropWalk.Value)
		{
			return null;
		}
		return Location.RandomAround(XYZ.FromVector3(faction.Gatherpoint), DropRadius, labor);
	}

	private static Location DropPlace(Labor labor)
	{
		Faction faction = labor.faction;
		Vector3 center = (faction != null && faction.Gatherpoint != Vector3.zero) ? faction.Gatherpoint : labor.transform.position;
		return Location.RandomAround(XYZ.FromVector3(center), DropRadius, labor);
	}
}

// The idle task's "store or drop" (Order.Transfer.GoStoreOrDropAt, the only caller): stores what has room kind by
// kind and puts the rest down at once.
[Feature(Features.LoadRelief, Features.LoadReliefInfo)]
[HarmonyPatch(typeof(StoreNode), nameof(StoreNode.NestedGoStoreInBestStorageOrDrop))]
internal static class LoadReliefStorePatch
{
	private static bool Prefix(Labor labor, int count, ref StaticNode __result)
	{
		if (count != 0 || labor == null || !NetworkServer.active)
		{
			return true;
		}
		__result = LoadRelief.StoreOrDrop(labor);
		return false;
	}
}

// A loaded worker's failed tasks count towards sending it to store or drop. Before OnFinally, which clears the worker.
[Feature(Features.LoadRelief, Features.LoadReliefInfo)]
[HarmonyPatch(typeof(Task), "OnFinally")]
internal static class LoadReliefFailPatch
{
	private static void Prefix(Task __instance)
	{
		switch (__instance.result)
		{
			case TaskResult.WorkerFailed:
			case TaskResult.PathFailed:
			case TaskResult.CouldNotReach:
			case TaskResult.InvalidSearch:
				break;
			default:
				return;
		}
		if (!NetworkServer.active || LoadRelief.IsStoring(__instance))
		{
			return;
		}
		LoadRelief.TaskFailed(__instance.Worker as Labor);
	}
}
