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
	}

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
			return;
		}
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
			StaticNode store = Node.Catcher.Do((StaticNode)labor.GoStoreIn(storage)).Catch((Exceptions.WorkFailedException e) => Node.Empty);
			return Node.Sequence.Do(store).Do((Labor l, int n, bool again) => Step(l, n, again), labor, stores + 1, lookedAgain);
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
			if (storage != null)
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

	private static StaticNode DropNode(Labor labor)
	{
		StaticNode drop = Node.Sequence.Do((Labor l) =>
		{
			PutDown(l);
		}, labor);
		Location place = WalkTo(labor);
		if (place == null)
		{
			return drop;
		}
		StaticNode walk = Node.Catcher.Do(labor.RunToward(place, precise: false, aggressive: false)).Catch((FailException e) => Node.Empty);
		return Node.Sequence.Do(walk).Do(drop);
	}

	private static void PutDown(Labor labor)
	{
		if (labor.IsNullOrReleased() || labor.recepteur.IsEmpty())
		{
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
