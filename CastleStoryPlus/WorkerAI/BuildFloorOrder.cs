using System.Collections.Generic;
using Brix.External.Factories;
using Brix.Game.AI;
using Brix.Game.AI.KnowledgeSpace;
using Brix.Game.AI.Nodes;
using Brix.Game.Components;
using Brix.Game.Semantique;
using CastleStoryPlus.Building;
using CastleStoryPlus.Core;
using HarmonyLib;
using Motus.Behavior;
using UnityEngine;

namespace CastleStoryPlus.WorkerAI;

// Floor by floor: a building of several floors (a storage tower with posts, stairs and floors) was built post and
// stairs first, all the way up, and the floors in between were left with gaps. The game's choice weighs a level of
// height as much as 10 blocks of walking, so a builder up on the stairs always found the next post or step closer
// than the floor's gaps, and once the floor above closed over them the gaps could not be reached any more. Now a
// builder does not take a blueprint two or more levels above an unfinished blueprint of the same build task near it
// (within ChainDistance across) that it could do now: one it can get to and has the material for. Posts and stairs
// then rise at most one block above the lowest unfinished floor. Blueprints the player gave top priority
// (BuildPriority) are not held back, and blueprints marked unreachable (UnreachableTasks) hold nothing back.
[Feature(Features.BuildJobs, Features.BuildJobsInfo)]
internal static class BuildFloorOrder
{
	// A blueprint this many levels above an open one waits.
	internal const int Levels = 2;

	// What a worker could do now in one build task, lowest first; found again after this many seconds.
	private const float CacheSeconds = 1f;

	private sealed class Open
	{
		public float At = -1f;

		public readonly List<BuildGoal> Goals = new List<BuildGoal>();

		// Whether the worker has or can fetch the material, per blueprint kind.
		public readonly Dictionary<Factory.AssetKey, bool> Material = new Dictionary<Factory.AssetKey, bool>();
	}

	private static readonly Dictionary<BuildGoalProvider, Dictionary<Labor, Open>> Cache = new Dictionary<BuildGoalProvider, Dictionary<Labor, Open>>();

	private static void Enable()
	{
		GameSession.OnLeave(Cache.Clear);
	}

	private static void Disable()
	{
		Cache.Clear();
	}

	// The blueprint the worker could do now that holds this one back, or null.
	internal static BuildGoal HeldBackBy(BuildGoal goal, Labor labor)
	{
		BuildGoalProvider provider = goal.Provider;
		if (provider == null || labor == null)
		{
			return null;
		}
		Open open = OpenGoals(provider, labor);
		int limit = goal._referenceY - Levels;
		Vector3 at = goal.transform.position;
		foreach (BuildGoal lower in open.Goals)
		{
			if (lower._referenceY > limit)
			{
				break;
			}
			if (lower.IsNullOrReleased() || lower == goal || lower.recepteur == null || lower.recepteur.IsFull())
			{
				continue;
			}
			Vector3 offset = lower.transform.position - at;
			offset.y = 0f;
			if (offset.sqrMagnitude > BuildNeeds.ChainDistance * BuildNeeds.ChainDistance)
			{
				continue;
			}
			if (HasMaterial(open, lower, labor))
			{
				return lower;
			}
		}
		return null;
	}

	private static Open OpenGoals(BuildGoalProvider provider, Labor labor)
	{
		if (!Cache.TryGetValue(provider, out Dictionary<Labor, Open> perLabor))
		{
			if (Cache.Count > 64)
			{
				Prune();
			}
			perLabor = new Dictionary<Labor, Open>();
			Cache[provider] = perLabor;
		}
		if (!perLabor.TryGetValue(labor, out Open open))
		{
			open = new Open();
			perLabor[labor] = open;
		}
		if (open.At >= 0f && Time.time - open.At < CacheSeconds)
		{
			return open;
		}
		open.At = Time.time;
		open.Goals.Clear();
		open.Material.Clear();
		foreach (Blueprint blueprint in provider.TrackedBlueprints)
		{
			BuildGoal other = (blueprint == null) ? null : blueprint.GetComponent<BuildGoal>();
			if (other == null || other.IsNullOrReleased() || other.IsAnti || !other.Commited || !blueprint.State.HasFlag(PlacementFlags.valid) || !blueprint.State.HasFlag(PlacementFlags.buildable) || other.recepteur == null || other.recepteur.IsFull())
			{
				continue;
			}
			if (BuildPriority.Has(other) || UnreachableTasks.IsMarked(other) || !Knowledge.Instance.CanReach(labor, other))
			{
				continue;
			}
			open.Goals.Add(other);
		}
		open.Goals.Sort((BuildGoal a, BuildGoal b) => a._referenceY.CompareTo(b._referenceY));
		return open;
	}

	private static bool HasMaterial(Open open, BuildGoal goal, Labor labor)
	{
		Factory.AssetKey key = goal.AssetKey;
		if (key != null && open.Material.TryGetValue(key, out bool known))
		{
			return known;
		}
		bool has = FreeBlueprints.IsFree(goal) || (!labor.recepteur.IsEmpty() && labor.recepteur.FirstThatFitsIn(goal.recepteur) != null) || labor.Find(goal.recepteur) != null;
		if (key != null)
		{
			open.Material[key] = has;
		}
		return has;
	}

	private static void Prune()
	{
		List<BuildGoalProvider> gone = new List<BuildGoalProvider>();
		foreach (BuildGoalProvider provider in Cache.Keys)
		{
			if (provider == null)
			{
				gone.Add(provider);
			}
		}
		foreach (BuildGoalProvider provider in gone)
		{
			Cache.Remove(provider);
		}
		if (Cache.Count > 64)
		{
			Cache.Clear();
		}
	}
}

[Feature(Features.BuildJobs, Features.BuildJobsInfo)]
[HarmonyPatch(typeof(BuildGoal), nameof(BuildGoal.IsMatchFor))]
internal static class BuildFloorOrderPatch
{
	private static void Postfix(BuildGoal __instance, Labor labor, AIErrorsStore errorDestination, ref bool __result)
	{
		if (!__result || __instance.IsAnti || BuildPriority.Has(__instance))
		{
			return;
		}
		if (BuildFloorOrder.HeldBackBy(__instance, labor) != null)
		{
			errorDestination.AddDecisionError(SharedWorkException<Exceptions.GoalUnavailable>.Get.Problem, labor);
			__result = false;
		}
	}
}
