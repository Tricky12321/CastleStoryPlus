using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using Brix.Engine;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Legacy;
using Brix.Pathfinding;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.Nature;

// The enemy waves could not get through trees: a place beside a tree is a "forest" step of the route graph, which only
// a unit already standing in a forest may take, and the enemies break blocks but trees are not blocks. Now that trees
// can be planted, a row of them shut the enemies out of a base for good. Now an enemy that breaks its way (corruptron,
// biftron, minitron, magitron) may plan its route through trees, and fells a tree only when it is in its way: while
// it follows a route and has not moved for StuckChecks checks in a row, the tree beside it that lies towards its next
// route step is struck every StrikeSeconds for StrikeDamage, as a bricktron's axe strikes it (a tree has 180), so it
// falls and drops its logs like any felled tree. An enemy walking past trees, fighting, breaking blocks or waiting
// leaves them standing. Host only; the tree's fall is the game's own and reaches every player.
// A forest no longer costs a route what open ground does (#209): the game weighs a step beside a tree as a flat step,
// so the enemies' routes ran straight into a strip of forest in their way and they hacked through it (and stood stuck
// in it) though an open path ran beside it. A forest step of their route search now weighs [Pathfinding]
// EnemyForestCost more (EnemyForestRules), and an enemy that stands still on its route in a forest with no tree in
// its way to fell, or that the strikes did not get on, searches its route again from where it stands.
[Feature(Features.EnemyTreeClearing, Features.EnemyTreeClearingInfo)]
internal class EnemyTreeClearing : MonoBehaviour
{
	private const float CheckSeconds = 0.5f;

	private const float StrikeSeconds = 1.2f;

	private const int StrikeDamage = 60;

	// Moved less than this in a check: not getting on.
	private const float StuckDistance = 0.2f;

	// How nearly towards the next route step a tree must lie (cosine of the angle).
	private const float AheadCosine = 0.5f;

	private sealed class Watch
	{
		internal Vector3 Last;

		internal int Stuck;

		internal float NextStrike;
	}

	private static readonly Dictionary<Labor, Watch> Watched = new Dictionary<Labor, Watch>();

	private static readonly List<Labor> Gone = new List<Labor>();

	private float _nextCheck;

	internal static ConfigEntry<int> ForestCost;

	// For the DevTools: trees struck and routes searched again by an enemy stuck in a forest.
	internal static int Strikes;

	internal static int Repaths;

	private static void Enable()
	{
		ForestCost = Plugin.Cfg.Bind("Pathfinding", "EnemyForestCost", EnemyForestRules.DefaultStepPenalty, new ConfigDescription("Route cost of a step beside a tree for enemies that fell trees in their way, on top of a flat step's 10 (the game weighs it as a flat step, so enemy waves cut straight through a forest though open ground ran beside it). 0: as the game.", new AcceptableValueRange<int>(EnemyForestRules.MinStepPenalty, EnemyForestRules.MaxStepPenalty)));
		GameSession.OnLeave(() => Watched.Clear());
		Plugin.Root.AddComponent<EnemyTreeClearing>();
	}

	internal static int ForestPenalty()
	{
		return (ForestCost != null) ? EnemyForestRules.Clamp(ForestCost.Value) : EnemyForestRules.DefaultStepPenalty;
	}

	// An enemy wave unit that breaks its way.
	internal static bool IsClearer(Labor labor)
	{
		Faction faction = (labor != null) ? labor.faction : null;
#if !ESSENTIALS
		return faction != null && faction.isAI && labor.Profession != null && labor.Profession.CanMakeWay() && Treasure.Beasts.KindOf(labor) == null;
#else
		return faction != null && faction.isAI && labor.Profession != null && labor.Profession.CanMakeWay();
#endif
	}

	private void Update()
	{
		float now = Time.time;
		if (!NetworkServer.active || now < _nextCheck)
		{
			return;
		}
		_nextCheck = now + CheckSeconds;
		TreeRegistry registry = TreeRegistry.Instance;
		if (registry == null)
		{
			return;
		}
		foreach (Labor labor in Live<Labor>.Active())
		{
			if (labor.IsNullOrReleased() || !IsClearer(labor))
			{
				continue;
			}
			Vector3 position = labor.transform.position;
			if (!Watched.TryGetValue(labor, out Watch watch))
			{
				watch = new Watch { Last = position };
				Watched[labor] = watch;
			}
			bool still = (position - watch.Last).sqrMagnitude < StuckDistance * StuckDistance;
			watch.Last = position;
			// Only on its way somewhere: not while it fights, breaks a block or waits.
			if (!still || labor.Activity == Activity.Fighting || !NextStep(labor, position, out Vector3 heading, out Vector3 next))
			{
				watch.Stuck = 0;
				continue;
			}
			watch.Stuck++;
			XYZ cell = XYZ.FromVector3(position);
			bool inForest = registry.IsNearATree(cell);
			XYZ tree = XYZ.zero;
			bool treeAhead = inForest && watch.Stuck >= EnemyForestRules.StrikeChecks && FindTree(registry, cell, position, heading, out tree);
			switch (EnemyForestRules.Decide(watch.Stuck, inForest, treeAhead, now >= watch.NextStrike))
			{
				case EnemyForestRules.StuckAction.Strike:
					watch.NextStrike = now + StrikeSeconds;
					Strike(labor, tree);
					Strikes++;
					break;
				case EnemyForestRules.StuckAction.Repath:
					watch.Stuck = 0;
					Repath(labor, next);
					break;
			}
		}
		foreach (KeyValuePair<Labor, Watch> entry in Watched)
		{
			if (entry.Key == null)
			{
				Gone.Add(entry.Key);
			}
		}
		foreach (Labor labor in Gone)
		{
			Watched.Remove(labor);
		}
		Gone.Clear();
	}

	// The direction to the next step of the route the unit follows and that step; false when it follows none, or when
	// its next steps break a block (it is breaking its way, not stuck).
	private static bool NextStep(Labor labor, Vector3 position, out Vector3 heading, out Vector3 next)
	{
		heading = Vector3.zero;
		next = position;
		DestinationIterator path = (labor.navigation != null && labor.navigation.Locomotion != null) ? labor.navigation.Locomotion.PathIterator : null;
		if (path == null || !path.HasCurrent)
		{
			return false;
		}
		for (int i = 0; i < 3; i++)
		{
			WalkNode node = path.GetNth(path.CurrentIndex + i);
			if (node == null)
			{
				break;
			}
			if (node.IsBlockBreaking())
			{
				return false;
			}
			Vector3 to = node.Position - position;
			to.y = 0f;
			if (to.sqrMagnitude > 0.25f)
			{
				heading = to;
				next = node.Position;
				return true;
			}
		}
		return false;
	}

	// The unit's walk is blocked: it halts and searches its route again from where it stands (the game's own repath on
	// a blocked walk), and with the forest's cost that route takes the shortest way out of the forest.
	private static void Repath(Labor labor, Vector3 next)
	{
		Locomotion4 locomotion = (labor.navigation != null) ? labor.navigation.Locomotion : null;
		if (locomotion == null)
		{
			return;
		}
		locomotion.PathBlockedAt(next);
		Repaths++;
	}

	// The tree beside the unit most nearly towards its next route step.
	private static bool FindTree(TreeRegistry registry, XYZ cell, Vector3 position, Vector3 heading, out XYZ tree)
	{
		tree = XYZ.zero;
		float best = AheadCosine;
		bool found = false;
		Vector3 flat = new Vector3(heading.x, 0f, heading.z);
		if (flat.sqrMagnitude > 0.0001f)
		{
			flat.Normalize();
		}
		foreach (XYZ offset in TreeRegistry.TreeNeighborhood)
		{
			XYZ at = cell + offset;
			if (!registry.HasTree(at))
			{
				continue;
			}
			Vector3 to = at.ToVector3() - position;
			to.y = 0f;
			float ahead = (to.sqrMagnitude > 0.0001f) ? Vector3.Dot(flat, to.normalized) : 1f;
			if (ahead > best)
			{
				best = ahead;
				tree = at;
				found = true;
			}
		}
		return found;
	}

	private static void Strike(Labor labor, XYZ position)
	{
		DamagedTree tree = DamagedTree.GetAtOrCreate(position);
		if (tree.IsNullOrReleased() || tree.IsDying)
		{
			return;
		}
		Faction faction = labor.affiliation.faction;
		tree.GetOrAddComponent<AffiliationComponent>().faction = faction;
		labor.transform.rotation = Quaternion.LookRotation(Flat(position.ToVector3() - labor.transform.position, labor.transform.forward));
		tree.Strike(position.ToScalaire(), StrikeDamage);
	}

	private static Vector3 Flat(Vector3 v, Vector3 fallback)
	{
		v.y = 0f;
		return (v.sqrMagnitude > 0.0001f) ? v : fallback;
	}
}

// Enemies that break their way may plan routes through trees (which they fell on the way).
[Feature(Features.EnemyTreeClearing, Features.EnemyTreeClearingInfo)]
[HarmonyPatch(typeof(Profession), nameof(Profession.CanWalkInForest))]
internal static class EnemyTreeClearingRoutePatch
{
	private static void Postfix(Profession __instance, ref bool __result)
	{
		if (!__result && EnemyTreeClearing.IsClearer(__instance.GetComponent<Labor>()))
		{
			__result = true;
		}
	}
}

#if UNITY6
// The forest's cost in a clearer's route search (#209). The game's route search lets a weight function add to every
// step's weight (SearchPathRequest.LodBuilder.GetWeightFunc; NoGoZonesSearch sets one for the player's teams): for a
// clearer it is wrapped in one that adds EnemyForestRules.StepWeight to each forest step. Set after the other
// patches on the request's direct search and its finer search (LoopImprovePath). One function per pooled request.
[Feature(Features.EnemyTreeClearing, Features.EnemyTreeClearingInfo)]
[HarmonyPatch]
internal static class EnemyForestCostPatch
{
	internal sealed class Cost
	{
		public Func<PathBranch, PathLink, int, int> Inner;

		public int Penalty;

		public readonly Func<PathBranch, PathLink, int, int> Weight;

		public Cost()
		{
			Weight = Add;
		}

		private int Add(PathBranch head, PathLink tail, int weight)
		{
			if (Inner != null)
			{
				weight = Inner(head, tail, weight);
			}
			if (head.type != PathBranch.BranchType.Forest)
			{
				return weight;
			}
			PathNode node = head.Node;
			return EnemyForestRules.StepWeight(weight, true, (node != null) ? node.Lod : 0, Penalty);
		}
	}

	private static readonly ConditionalWeakTable<SearchPathRequest, Cost> Costs = new ConditionalWeakTable<SearchPathRequest, Cost>();

	// For the DevTools: clearers' searches that paid for forest steps.
	internal static int Searches;

	[HarmonyPatch(typeof(SearchPathRequest), "InitSearch")]
	[HarmonyPostfix]
	[HarmonyPriority(Priority.Last)]
	private static void InitSearch(SearchPathRequest __instance)
	{
		if (__instance.SearchAway || __instance.DirectBuilder == null)
		{
			return;
		}
		SearchPathRequest.LodBuilder builder = __instance.DirectBuilder;
		Costs.TryGetValue(__instance, out Cost cost);
		Func<PathBranch, PathLink, int, int> current = builder.GetWeightFunc;
		// Its own function from the request's last search, which nothing has replaced since: nothing else is set.
		if (cost != null && current == cost.Weight)
		{
			current = null;
		}
		int penalty = EnemyTreeClearing.ForestPenalty();
		Navigation navigation = __instance.Navigation;
		if (penalty <= 0 || navigation == null || !EnemyTreeClearing.IsClearer(navigation.Labor))
		{
			builder.GetWeightFunc = current;
		}
		else
		{
			cost = cost ?? Costs.GetValue(__instance, (SearchPathRequest r) => new Cost());
			cost.Inner = current;
			cost.Penalty = penalty;
			builder.GetWeightFunc = cost.Weight;
			Searches++;
		}
		if (__instance.ImproveBuilder != null)
		{
			__instance.ImproveBuilder.GetWeightFunc = builder.GetWeightFunc;
		}
	}

	// The finer search's builder (made by LoopImprovePath) gets the same function.
	[HarmonyPatch(typeof(SearchPathRequest), "LoopImprovePath")]
	[HarmonyPostfix]
	[HarmonyPriority(Priority.Last)]
	private static void Improve(SearchPathRequest __instance)
	{
		if (__instance.ImproveBuilder == null || __instance.SearchAway || __instance.DirectBuilder == null)
		{
			return;
		}
		if (Costs.TryGetValue(__instance, out Cost cost) && __instance.DirectBuilder.GetWeightFunc == cost.Weight)
		{
			__instance.ImproveBuilder.GetWeightFunc = cost.Weight;
		}
	}
}
#endif
