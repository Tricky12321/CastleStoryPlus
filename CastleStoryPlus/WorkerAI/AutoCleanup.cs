using System.Collections.Generic;
using BepInEx.Configuration;
using Brix.Components;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.AI.InstructionType;
using Brix.Game.AI.KnowledgeSpace;
using Brix.Game.AI.Nodes;
using Brix.Game.Components;
using Brix.Game.Semantique;
using Brix.Input;
using Brix.Lifecycle.Pooling;
using Brix.UI.Icons;
using Brix.Utils;
using CastleStoryPlus.Core;
using CastleStoryPlus.Giant;
using HarmonyLib;
using Motus.Behavior;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.WorkerAI;

// Idle chore: pick up loose items lying on the ground near the base (home crystal and stockpiles) or near the
// worker, and store them, without a cleanup zone. Only offered by IdleProject, so any real work comes first.
[Feature(Features.AutoCleanup, Features.AutoCleanupInfo)]
internal static class AutoCleanup
{
	internal static ConfigEntry<int> Radius;

	// Same layers as the game's cleanup zones: loose stones, free blocks and accessories.
	private const int Layers = 1073152;

	private const float RefreshSeconds = 5f;

	// A claim expires on its own in case a task is dropped without running its Finally.
	private const float ClaimSeconds = 60f;

	private static readonly Collider[] Hits = new Collider[512];

	private static readonly Dictionary<Faction, List<GameObject>> Candidates = new Dictionary<Faction, List<GameObject>>();

	private static readonly Dictionary<Faction, float> RefreshedAt = new Dictionary<Faction, float>();

	private static readonly Dictionary<GameObject, float> Claims = new Dictionary<GameObject, float>();

	private static readonly Dictionary<Labor, GameObject> Plans = new Dictionary<Labor, GameObject>();

	// Items a worker set out for and did not get stored, left alone for a while (until the time given).
	private static readonly Dictionary<GameObject, float> Failed = new Dictionary<GameObject, float>();

	private const float FailedSeconds = 120f;

	public static readonly LaborInstruction<GameObject> Cleanup = new LaborInstruction<GameObject>("AutoCleanup", IconKeys.PickUp, (Labor labor, GameObject item) => CleanupNode(labor, item));

	private static void Enable()
	{
		Radius = Plugin.Cfg.Bind("AutoCleanup", "Radius", 15, "Idle workers pick up loose items within this many blocks of the home crystal, a stockpile or themselves.");
		GameSession.OnLeave(() =>
		{
			Candidates.Clear();
			RefreshedAt.Clear();
			Claims.Clear();
			Plans.Clear();
			Failed.Clear();
		});
	}

	// Finds the nearest loose item this worker can pick up and store, and claims it. Returns null if none.
	public static GameObject TryPlan(Labor labor)
	{
		if (!NetworkServer.active || labor == null || !labor.recepteur.IsEmpty() || InputModeController.DefaultMode == InputMode.worldEditor)
		{
			return null;
		}
		ExpireClaims();
		List<GameObject> items = new List<GameObject>(BaseCandidates(labor.faction));
		Collect(labor.transform.position, items, null);
		Vector3 position = labor.transform.position;
		items.Sort((GameObject a, GameObject b) => (a.transform.position - position).sqrMagnitude.CompareTo((b.transform.position - position).sqrMagnitude));
		foreach (GameObject item in items)
		{
			if (!IsLoose(item) || Claims.ContainsKey(item) || Knowledge.Instance.IsReserved(item) || ResourceReservation.IsReservedByOther(item, labor))
			{
				continue;
			}
			GameComponent component = item.GetComponent<GameComponent>();
			if (component == null || !labor.recepteur.HasRoomFor(item) || labor.BestRecepteurToStore(item) == null || !CanReach(labor, item, component))
			{
				continue;
			}
			Claims[item] = Time.time + ClaimSeconds;
			Plans[labor] = item;
			return item;
		}
		return null;
	}

	// For the developer tools: every loose item the worker would consider, nearest first, with why it would not
	// pick it up (null: it would).
	internal static List<KeyValuePair<GameObject, string>> Explain(Labor labor)
	{
		List<KeyValuePair<GameObject, string>> result = new List<KeyValuePair<GameObject, string>>();
		List<GameObject> items = new List<GameObject>(BaseCandidates(labor.faction));
		Collect(labor.transform.position, items, null);
		Vector3 position = labor.transform.position;
		items.Sort((GameObject a, GameObject b) => (a.transform.position - position).sqrMagnitude.CompareTo((b.transform.position - position).sqrMagnitude));
		foreach (GameObject item in items)
		{
			string why = null;
			GameComponent component = item.GetComponent<GameComponent>();
			if (!IsLoose(item))
			{
				why = "not loose";
			}
			else if (Claims.ContainsKey(item))
			{
				why = "claimed by another worker's cleanup";
			}
			else if (Knowledge.Instance.IsReserved(item))
			{
				why = "reserved by a task";
			}
			else if (ResourceReservation.IsReservedByOther(item, labor))
			{
				why = "reserved by another worker";
			}
			else if (component == null)
			{
				why = "no game component";
			}
			else if (!labor.recepteur.IsEmpty() || !labor.recepteur.HasRoomFor(item))
			{
				why = "the worker's hands are not free";
			}
			else if (labor.BestRecepteurToStore(item) == null)
			{
				why = "no storage with room for it";
			}
			else if (!CanReach(labor, item, component))
			{
				why = "the worker cannot reach it";
			}
			result.Add(new KeyValuePair<GameObject, string>(item, why));
		}
		return result;
	}

	// The workers' knowledge only tracks some kinds of items (logs, planks, bricks, ...); for those its regions
	// tell whether the worker can get there. Weapons, armour and the like it does not track, and asking it about
	// them always says no, so for those the worker tries, and an item it did not get stored is left alone for a
	// while.
	private static bool CanReach(Labor labor, GameObject item, GameComponent component)
	{
		if (Knowledge.Instance.KnowsAbout(item))
		{
			return Knowledge.Instance.CanReach(labor, component);
		}
		return !Failed.TryGetValue(item, out float until) || Time.time > until;
	}

	// The worker is carrying a loose item to storage.
	public static bool IsCarrying(Labor labor)
	{
		return labor != null && labor.CurrentTask != null && Plans.ContainsKey(labor);
	}

	public static void Release(Labor labor)
	{
		if (labor != null && Plans.TryGetValue(labor, out GameObject item))
		{
			Plans.Remove(labor);
			if (item != null)
			{
				Claims.Remove(item);
			}
		}
	}

	private static Node CleanupNode(Labor labor, GameObject item)
	{
		if (item == null || !IsLoose(item))
		{
			Release(labor);
			return Node.Empty;
		}
		GameComponent itemComponent = item.GetComponent<GameComponent>();
		return Node.Sequence.Label("Auto cleanup")
			.Do(labor.GoPickUp(itemComponent, -1))
			.Label("Pick up loose item")
			.Do((Labor l) =>
			{
				// Carrying now: stop being interruptible so the load is not dropped again.
				l.Activity = Activity.Idle;
			}, labor)
			.Do(labor.GoStoreInBestStorage())
			.Label("Store loose item")
			.Finally((Labor l) =>
			{
				// Still lying where it was: the worker could not get to it or was called away.
				if (IsLoose(item))
				{
					Failed[item] = Time.time + FailedSeconds;
				}
				Release(l);
			}, labor);
	}

	// Loose items around the home crystal and the stockpiles, cached per faction for a few seconds.
	private static List<GameObject> BaseCandidates(Faction faction)
	{
		if (Candidates.TryGetValue(faction, out List<GameObject> cached) && RefreshedAt.TryGetValue(faction, out float at) && Time.time - at < RefreshSeconds)
		{
			return cached;
		}
		List<GameObject> result = new List<GameObject>();
		HashSet<GameObject> seen = new HashSet<GameObject>();
		FireflyNest nest = GiantBricktron.HomeNest(faction);
		if (nest != null)
		{
			Collect(nest.transform.position, result, seen);
		}
		HashSet<GameObject> stockpiles = BrixSingleton<AutoList>.Instance?.GetInstances(ObjetsDynamiques.Palette);
		if (stockpiles != null)
		{
			foreach (GameObject pile in stockpiles)
			{
				if (pile == null || !pile.activeInHierarchy)
				{
					continue;
				}
				GameComponent component = pile.GetComponent<GameComponent>();
				if (component != null && component.faction == faction)
				{
					Collect(pile.transform.position, result, seen);
				}
			}
		}
		Candidates[faction] = result;
		RefreshedAt[faction] = Time.time;
		return result;
	}

	private static void Collect(Vector3 center, List<GameObject> result, HashSet<GameObject> seen)
	{
		int count = Physics.OverlapSphereNonAlloc(center, Radius.Value, Hits, Layers);
		for (int i = 0; i < count; i++)
		{
			GameObject go = SignificantParent.Of(Hits[i].gameObject);
			if (go.IsNullOrReleased() || !IsLoose(go) || !Storable(go))
			{
				continue;
			}
			if (seen == null ? !result.Contains(go) : seen.Add(go))
			{
				result.Add(go);
			}
		}
		System.Array.Clear(Hits, 0, count);
	}

	// Not inside a stockpile, rack or crystal, not carried or worn by a unit.
	private static bool IsLoose(GameObject go)
	{
		if (go.IsNullOrReleased() || !go.activeInHierarchy)
		{
			return false;
		}
		Transform parent = go.transform.parent;
		return parent == null || (parent.GetComponentInParent<Recepteur>() == null && parent.GetComponentInParent<Labor>() == null);
	}

	// Same rules as the game's cleanup zones.
	private static bool Storable(GameObject go)
	{
		if (Apparence.Is<Fireflies>(go) || Apparence.Is<Granulaire>(go))
		{
			return false;
		}
		return !Apparence.Is<ExplosiveBarrel>(go) || Brix.Game.Rules.GeneralRules.AllowStoringBarrels;
	}

	private static void ExpireClaims()
	{
		List<GameObject> expired = null;
		foreach (KeyValuePair<GameObject, float> pair in Claims)
		{
			if (pair.Key == null || Time.time > pair.Value)
			{
				if (expired == null)
				{
					expired = new List<GameObject>();
				}
				expired.Add(pair.Key);
			}
		}
		if (expired == null)
		{
			return;
		}
		foreach (GameObject item in expired)
		{
			Claims.Remove(item);
		}
		List<Labor> stale = new List<Labor>();
		foreach (KeyValuePair<Labor, GameObject> pair in Plans)
		{
			if (pair.Key == null || pair.Value == null || !Claims.ContainsKey(pair.Value))
			{
				stale.Add(pair.Key);
			}
		}
		foreach (Labor labor in stale)
		{
			Plans.Remove(labor);
		}
	}
}

// Before tidying stockpiles or going to the gather point, an idle worker picks up loose items nearby.
// Runs before StockpileConsolidationPatch, which skips when this one has given an order.
[Feature(Features.AutoCleanup, Features.AutoCleanupInfo)]
[HarmonyPatch(typeof(IdleProject), nameof(IdleProject._GetInstruction))]
[HarmonyPriority(Priority.High)]
internal static class AutoCleanupPatch
{
	private static bool Prefix(IdleProject __instance, Labor labor, Var<OrderPackage<Labor>> result)
	{
		if (!labor.recepteur.IsEmpty() || !__instance.GatherAtIdlePoint(labor))
		{
			return true;
		}
		GameObject item = AutoCleanup.TryPlan(labor);
		if (item == null)
		{
			return true;
		}
		OrderPackage<Labor> package = Order.Package.OfGameObject(AutoCleanup.Cleanup, item, Labor.DecisionSource.Project);
		// Interruptible until the worker picks something up, so real work always wins.
		package.SetPrepare((BaseLabor worker) => __instance.Prepare(worker as Labor, false, Activity.Chilling));
		result.Set(package);
		return false;
	}
}
