#if UNITY6
using System;
using System.Runtime.CompilerServices;
using Brix.Engine;
using Brix.Game;
using Brix.Game.AI;
using Brix.Pathfinding;
using CastleStoryPlus.Core;
using HarmonyLib;

namespace CastleStoryPlus.Pathfinding;

// The game's route search pays for the team's no-go zones (NoGoZones). SearchPathRequest searches from coarse places
// of the graph (a place of level n covers 2^n x 2^n columns) down to voxels, and its builders (LodBuilder) let a
// weight function add to every step's weight (GetWeightFunc, which the game leaves empty). For a unit of a team with
// zones the function adds NoGoRules.StepPenalty: the share of the place stepped into that lies in a zone, against
// the share of the place stepped from (entering costs, leaving does not), and the place's size. Set on the request's
// direct search and on its finer search (LoopImprovePath makes that one); a search away from something (fleeing)
// and the AI's searches are left as they are. One function per pooled request, made once.
[Feature(Features.NoGoZones, Features.NoGoZonesInfo)]
[HarmonyPatch(typeof(SearchPathRequest), "InitSearch")]
internal static class NoGoZonesSearchPatch
{
	internal sealed class Cost
	{
		public NoGoMap Map;

		public int Strength;

		public readonly Func<PathBranch, PathLink, int, int> Weight;

		public Cost()
		{
			Weight = Add;
		}

		private int Add(PathBranch head, PathLink tail, int weight)
		{
			NoGoMap map = Map;
			PathNode to = head.Node;
			if (map == null || tail == null || to == null || !NoGoZones.Active)
			{
				return weight;
			}
			float into = Coverage(map, to);
			if (into <= 0f)
			{
				return weight;
			}
			PathNode from = tail.head.Node;
			float was = (from != null) ? Coverage(map, from) : 0f;
			return weight + NoGoRules.StepPenalty(was, into, 1 << to.Lod, Strength);
		}
	}

	private static readonly ConditionalWeakTable<SearchPathRequest, Cost> Costs = new ConditionalWeakTable<SearchPathRequest, Cost>();

	// For the DevTools: searches that paid for zones.
	internal static int Searches;

	// The share of the place's columns in a zone (a voxel: 0 or 1).
	internal static float Coverage(NoGoMap map, PathNode node)
	{
		int lod = node.Lod;
		if (lod <= 0)
		{
			XYZ p = node.RepresentativePosition;
			return map.Contains(p.x, p.z) ? 1f : 0f;
		}
		XYZ low = node.LowResPosition;
		return map.Coverage(low.x << lod, low.z << lod, 1 << lod);
	}

	private static void Postfix(SearchPathRequest __instance)
	{
		if (__instance.SearchAway || __instance.DirectBuilder == null)
		{
			return;
		}
		Navigation navigation = __instance.Navigation;
		Faction faction = (navigation != null) ? navigation.faction : null;
		NoGoMap map = NoGoZones.MapFor(faction);
		if (map == null)
		{
			if (Costs.TryGetValue(__instance, out Cost unused))
			{
				unused.Map = null;
			}
			__instance.DirectBuilder.GetWeightFunc = null;
			return;
		}
		Cost cost = Costs.GetValue(__instance, (SearchPathRequest r) => new Cost());
		cost.Map = map;
		cost.Strength = NoGoZones.CurrentStrength;
		__instance.DirectBuilder.GetWeightFunc = cost.Weight;
		Searches++;
	}

	// The finer search's builder (made by LoopImprovePath) gets the same function.
	internal static void Improve(SearchPathRequest request)
	{
		if (request.ImproveBuilder == null)
		{
			return;
		}
		request.ImproveBuilder.GetWeightFunc = (request.DirectBuilder != null && !request.SearchAway) ? request.DirectBuilder.GetWeightFunc : null;
	}
}

[Feature(Features.NoGoZones, Features.NoGoZonesInfo)]
[HarmonyPatch(typeof(SearchPathRequest), "LoopImprovePath")]
internal static class NoGoZonesImprovePatch
{
	private static void Postfix(SearchPathRequest __instance)
	{
		NoGoZonesSearchPatch.Improve(__instance);
	}
}
#endif
