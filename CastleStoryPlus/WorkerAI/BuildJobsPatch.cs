using System.Collections.Generic;
using Brix.External.Factories;
using Brix.Game.AI;
using Brix.Game.AI.KnowledgeSpace;
using Brix.Game.AI.Nodes;
using Brix.Game.Components;
using Brix.Game.Semantique;
using CastleStoryPlus.Core;
using HarmonyLib;
using Motus.Behavior;
using UnityEngine;

namespace CastleStoryPlus.WorkerAI;

// What workers are bringing to which blueprint, counted in items. A worker that sets out with an armful for one
// blueprint and the ones near it registers how many it brings to each; the entries go when it has delivered there,
// when its task ends, or after a while.
internal static class BuildNeeds
{
	private sealed class Incoming
	{
		public Labor Labor;

		public int Amount;

		public float Until;
	}

	// An entry left behind (a task dropped without its clean-up) does not hold a blueprint forever.
	private const float IncomingSeconds = 180f;

	// Other blueprints of the same project a worker also brings material to, at most this far from the first.
	internal const float ChainDistance = 12f;

	private static readonly Dictionary<BuildGoal, List<Incoming>> Bringing = new Dictionary<BuildGoal, List<Incoming>>();

	internal static void Clear()
	{
		Bringing.Clear();
	}

	internal static void Register(BuildGoal goal, Labor labor, int amount)
	{
		if (!Bringing.TryGetValue(goal, out List<Incoming> list))
		{
			list = new List<Incoming>();
			Bringing[goal] = list;
		}
		list.RemoveAll((Incoming i) => i.Labor == labor);
		list.Add(new Incoming { Labor = labor, Amount = amount, Until = Time.time + IncomingSeconds });
	}

	internal static void Delivered(BuildGoal goal, Labor labor)
	{
		if (goal != null && Bringing.TryGetValue(goal, out List<Incoming> list))
		{
			list.RemoveAll((Incoming i) => i.Labor == labor);
			if (list.Count == 0)
			{
				Bringing.Remove(goal);
			}
		}
	}

	internal static void ReleaseAll(Labor labor)
	{
		List<BuildGoal> empty = new List<BuildGoal>();
		foreach (KeyValuePair<BuildGoal, List<Incoming>> pair in Bringing)
		{
			pair.Value.RemoveAll((Incoming i) => i.Labor == labor);
			if (pair.Value.Count == 0)
			{
				empty.Add(pair.Key);
			}
		}
		foreach (BuildGoal goal in empty)
		{
			Bringing.Remove(goal);
		}
	}

	private static bool IsBringing(BuildGoal goal, Labor labor)
	{
		return Bringing.TryGetValue(goal, out List<Incoming> list) && list.Exists((Incoming i) => i.Labor == labor && i.Until > Time.time);
	}

	// Items the other workers are bringing to the goal: what they registered, 1 for each one working on it without
	// registering (delivering what it already carried), and 1 for each one that chose it but has not started.
	public static int OtherWorkersBring(BuildGoal goal, Labor labor)
	{
		int bring = 0;
		if (Bringing.TryGetValue(goal, out List<Incoming> list))
		{
			foreach (Incoming incoming in list)
			{
				if (incoming.Labor != labor && !incoming.Labor.IsNullOrReleased() && incoming.Until > Time.time)
				{
					bring += incoming.Amount;
				}
			}
		}
		foreach (Labor worker in goal.workers)
		{
			if (worker != labor && !worker.IsNullOrReleased() && !IsBringing(goal, worker))
			{
				bring++;
			}
		}
		return bring + TaskReservation.OtherReservations(goal, labor);
	}

	public static int RemainingNeedFor(BuildGoal goal, Ressource ressource)
	{
		return (ressource == null) ? -1 : goal.recepteur.CurrentCapacity.Value(ressource);
	}

	public static int RemainingNeedFor(BuildGoal goal, GameObject item)
	{
		return RemainingNeedFor(goal, (item == null) ? null : Apparence.PrincipaleOf(item));
	}
}

// A build goal counts as covered once the other workers already bring what it still needs (counted in items), so
// several workers no longer take the same small job.
[Feature(Features.BuildJobs, Features.BuildJobsInfo)]
[HarmonyPatch(typeof(BuildGoal), nameof(BuildGoal.IsMatchFor))]
internal static class BuildGoalCoveredPatch
{
	private static void Enable()
	{
		GameSession.OnLeave(BuildNeeds.Clear);
	}

	private static void Postfix(BuildGoal __instance, Labor labor, AIErrorsStore errorDestination, ref bool __result)
	{
		if (!__result || __instance.IsAnti)
		{
			return;
		}
		int others = BuildNeeds.OtherWorkersBring(__instance, labor);
		if (others == 0)
		{
			return;
		}
		int remaining = BuildNeeds.RemainingNeedFor(__instance, labor.Find(__instance.recepteur));
		if (remaining >= 0 && remaining <= others)
		{
			errorDestination.AddDecisionError(SharedWorkException<Exceptions.GoalTooCrowded>.Get.Problem, labor);
			__result = false;
		}
	}
}

// A worker fetching material for a blueprint takes as much as it can carry, up to what that blueprint and the
// nearest other blueprints of the same project still need (minus what other workers bring), and delivers to them
// one after the other on the same trip: a row of stockpiles needing a log each gets three from one worker. It was
// one item per trip (only what the first blueprint needed); the game itself took up to 20 for one blueprint.
[Feature(Features.BuildJobs, Features.BuildJobsInfo)]
[HarmonyPatch(typeof(BuildGoal), nameof(BuildGoal.GetWorkNode))]
internal static class BuildPickUpAmountPatch
{
	private static bool Prefix(BuildGoal __instance, Labor labor, ref Node __result)
	{
		if (__instance.IsNullOrReleased() || labor.IsNullOrReleased() || __instance.IsAnti || labor.recepteur.FirstThatFitsIn(__instance.recepteur) != null)
		{
			return true;
		}
		GameObject item = labor.Find(__instance.recepteur);
		Ressource ressource = (item == null) ? null : Apparence.PrincipaleOf(item);
		FactoryImprint imprint = (item == null) ? null : item.GetComponent<FactoryImprint>();
		if (ressource == null || imprint == null)
		{
			return true;
		}
		int carry = Mathf.Max(1, labor.recepteur.CanFitUpTo(imprint.AssetKey));
		int first = Mathf.Clamp(BuildNeeds.RemainingNeedFor(__instance, ressource) - BuildNeeds.OtherWorkersBring(__instance, labor), 1, carry);
		List<KeyValuePair<BuildGoal, int>> plan = new List<KeyValuePair<BuildGoal, int>> { new KeyValuePair<BuildGoal, int>(__instance, first) };
		int upTo = first;
		foreach (BuildGoal other in NearbyGoals(__instance, labor, ressource))
		{
			if (upTo >= carry)
			{
				break;
			}
			int need = BuildNeeds.RemainingNeedFor(other, ressource) - BuildNeeds.OtherWorkersBring(other, labor);
			if (need <= 0)
			{
				continue;
			}
			int take = Mathf.Min(need, carry - upTo);
			plan.Add(new KeyValuePair<BuildGoal, int>(other, take));
			upTo += take;
		}
		foreach (KeyValuePair<BuildGoal, int> step in plan)
		{
			BuildNeeds.Register(step.Key, labor, step.Value);
		}
		Sequencer.Builder sequence = Node.Sequence.Label("BuildGoal.GetWorkNode Sequence")
			.Do((BuildGoal aBuildGoal, Labor aLabor, int amount) => aLabor.FindAndPickUp(aBuildGoal.recepteur, amount), __instance, labor, upTo).Label("FindAndPickUp", labor)
			.Do((BuildGoal aBuildGoal, Labor aLabor) => aLabor.GoStoreIn(aBuildGoal), __instance, labor)
			.Do((BuildGoal aBuildGoal, Labor aLabor) => BuildNeeds.Delivered(aBuildGoal, aLabor), __instance, labor);
		for (int i = 1; i < plan.Count; i++)
		{
			sequence = sequence.Do((BuildGoal aBuildGoal, Labor aLabor) => DeliverAlso(aBuildGoal, aLabor), plan[i].Key, labor);
		}
		__result = (StaticNode)sequence.Finally((Labor aLabor) => BuildNeeds.ReleaseAll(aLabor), labor);
		return false;
	}

	// Blueprints of the same project near the first one that still need this resource, nearest first.
	private static List<BuildGoal> NearbyGoals(BuildGoal goal, Labor labor, Ressource ressource)
	{
		List<BuildGoal> result = new List<BuildGoal>();
		if (goal.Provider == null)
		{
			return result;
		}
		Vector3 at = goal.transform.position;
		foreach (Blueprint blueprint in goal.Provider.TrackedBlueprints)
		{
			BuildGoal other = (blueprint == null) ? null : blueprint.GetComponent<BuildGoal>();
			if (other == null || other == goal || other.IsNullOrReleased() || other.IsAnti || !other.Commited || !blueprint.State.HasFlag(PlacementFlags.buildable) || other.recepteur == null || other.recepteur.IsFull())
			{
				continue;
			}
			if ((other.transform.position - at).sqrMagnitude > BuildNeeds.ChainDistance * BuildNeeds.ChainDistance || BuildNeeds.RemainingNeedFor(other, ressource) <= 0)
			{
				continue;
			}
			if (!Knowledge.Instance.CanReach(labor, other))
			{
				continue;
			}
			result.Add(other);
		}
		result.Sort((BuildGoal a, BuildGoal b) => (a.transform.position - at).sqrMagnitude.CompareTo((b.transform.position - at).sqrMagnitude));
		return result;
	}

	// On to the next blueprint with what is left in the arms; a blueprint that went away or got filled in the
	// meantime, or one the worker cannot get to, is passed over without failing the trip.
	private static Node DeliverAlso(BuildGoal goal, Labor labor)
	{
		if (goal.IsNullOrReleased() || labor.recepteur.IsEmpty() || labor.recepteur.FirstThatFitsIn(goal.recepteur) == null)
		{
			BuildNeeds.Delivered(goal, labor);
			return Node.Empty;
		}
		return Node.Catcher.Do(Node.Sequence
				.Do((BuildGoal aBuildGoal, Labor aLabor) => aLabor.GoStoreIn(aBuildGoal), goal, labor)
				.Do((BuildGoal aBuildGoal, Labor aLabor) => BuildNeeds.Delivered(aBuildGoal, aLabor), goal, labor))
			.Catch((Exceptions.WorkFailedException e, BuildGoal aBuildGoal, Labor aLabor) =>
			{
				BuildNeeds.Delivered(aBuildGoal, aLabor);
				return Node.Empty;
			}, goal, labor);
	}
}
