using System.Collections.Generic;
using System.Diagnostics;
using Brix.Engine;
using Brix.Game.AI;
using Brix.Pathfinding;
using Brix.Pathfinding.Test;

namespace CastleStoryPlus.DevTools.Pathing;

// Runs the game's own route search (the A* a bricktron runs, with the mod's patches on it) at once, in one frame,
// for a given bricktron from one voxel to another, and compares the route with the shortest one: a Dijkstra over
// the same navigation graph (the game's level-0 nodes and their links), with the same rules of what the bricktron
// may cross. The cost of a route is the sum of its links' weights, as the game counts it (a flat step 10, a step
// up or down more, climbing 240+).
internal static class PathProbe
{
	internal sealed class Route
	{
		public bool Found;

		public int Cost;

		public int Steps;

		public int Searched;

		public double Milliseconds;

		public List<XYZ> Nodes = new List<XYZ>();
	}

	internal sealed class Report
	{
		public bool Valid;

		public string Problem;

		public XYZ Origin;

		public XYZ Destination;

		public Route AStar;

		public Route Shortest;

		// A* cost / shortest cost (1 = the A* route is a shortest one).
		public float CostRatio;
	}

	// Most nodes the reference search may settle before giving up (a whole map is far less than this per island).
	private const int MaxDijkstraNodes = 400000;

	// Search steps the A* may take in all; the game spreads them over frames, here they run at once.
	private const int MaxAStarSteps = 2000000;

	internal static bool Ready()
	{
		return TestPathfinding.Instance != null && TestPathfinding.Instance.Initialized && TestPathfinding.Instance.terrainLod != null;
	}

	// The highest place a bricktron can stand on in the column x, z (a node of the graph that is not inside a block).
	internal static bool Ground(int x, int z, out XYZ voxel)
	{
		for (int y = 255; y >= 0; y--)
		{
			XYZ p = new XYZ(x, y, z);
			if (TestPathfinding.Instance.terrainLod.GetPathNode(p) is WalkablePathNode node && !node.IsEmpty && !Voxel.IsFull(p))
			{
				voxel = p;
				return true;
			}
		}
		voxel = XYZ.zero;
		return false;
	}

	// Main thread, on the host.
	internal static Report Run(Labor labor, XYZ from, XYZ to, bool withShortest)
	{
		Report report = new Report();
		if (!Ready())
		{
			report.Problem = "The navigation graph is not ready (no game, or still loading)";
			return report;
		}
		Navigation navigation = labor.navigation;
		SearchPathRequest request = SearchPathRequest.Make();
		try
		{
			request.InitNavigation(navigation);
			request.From = from;
			request.Location = new SpaceLocation(to.ToVector3());
			request.Exact = true;
			request.Precise = true;
			request.PresenceTest = navigation.Presence.BuildPresenceTest(navigation.Labor.Profession.CanPassThrough(), false);
			request.CanClimb = navigation.Interaction.CanClimb();
			request.CanWalkInForest = navigation.Labor.Profession.CanWalkInForest();
			request.CanBreakVoxels = navigation.Labor.Profession.CanMakeWay();
			request.SearchAway = false;
			request.LodCount = request.Pathfinding.lodCount;
			request.SearchLimit = -1;
			request.SearchPerUpdate = 100;
			if (!request.InitOrginAndDestination())
			{
				report.Problem = "The start or the goal is not a walkable place";
				return report;
			}
			report.Valid = true;
			report.Origin = request.Origin.RepresentativePosition;
			report.Destination = request.Destination;
			report.AStar = AStar(request);
			if (withShortest)
			{
				report.Shortest = Dijkstra(request.Origin, request.Destination, request.Evaluator);
				if (report.AStar.Found && report.Shortest.Found && report.Shortest.Cost > 0)
				{
					report.CostRatio = (float)report.AStar.Cost / report.Shortest.Cost;
				}
			}
			return report;
		}
		finally
		{
			if (request.Search != null)
			{
				request.Search.Clear();
			}
			request.Dispose();
		}
	}

	private static Route AStar(SearchPathRequest request)
	{
		Route route = new Route();
		Stopwatch watch = Stopwatch.StartNew();
		request.InitSearch();
		int steps = 0;
		while (!request.Work(request.SearchPerUpdate))
		{
			steps += request.SearchPerUpdate;
			if (steps > MaxAStarSteps)
			{
				break;
			}
		}
		route.Searched = request.Searched;
		if (request.SuccessLink != null)
		{
			request.InitImprove();
			while (!request.LoopImprovePath(request.SearchPerUpdate))
			{
			}
		}
		request.ConcludeSearch();
		route.Found = request.Succeeded;
		if (route.Found && request.PathUnroller != null)
		{
			List<PathNode> nodes = new List<PathNode>();
			request.PathUnroller.GetResultNodes(nodes);
			// From the start to the goal, whichever way round the unroller gives them.
			if (nodes.Count > 1 && nodes[0].RepresentativePosition == request.Destination)
			{
				nodes.Reverse();
			}
			if (nodes.Count > 0 && nodes[0] != request.Origin)
			{
				nodes.Insert(0, request.Origin);
			}
			for (int i = 0; i < nodes.Count; i++)
			{
				route.Nodes.Add(nodes[i].RepresentativePosition);
				if (i > 0)
				{
					route.Cost += LinkWeight(nodes[i - 1], nodes[i]);
				}
			}
			route.Steps = route.Nodes.Count - 1;
		}
		watch.Stop();
		route.Milliseconds = watch.Elapsed.TotalMilliseconds;
		return route;
	}

	// The weight of the link from one node to the next, as the graph has it (-1: not linked; counted as 0).
	private static int LinkWeight(PathNode from, PathNode to)
	{
		if (from is TerrainPathNode node && node.siblings != null)
		{
			foreach (PathBranch branch in node.siblings)
			{
				if (branch.Node == to)
				{
					return branch.Weight;
				}
			}
		}
		return 0;
	}

	private static Route Dijkstra(PathNode origin, XYZ destination, PathBranchEvaluator evaluator)
	{
		Route route = new Route();
		Stopwatch watch = Stopwatch.StartNew();
		Dictionary<PathNode, int> best = new Dictionary<PathNode, int>();
		Dictionary<PathNode, PathNode> previous = new Dictionary<PathNode, PathNode>();
		HashSet<PathNode> settled = new HashSet<PathNode>();
		MinHeap<PathNode> open = new MinHeap<PathNode>();
		best[origin] = 0;
		open.Push(origin, 0);
		PathNode goal = null;
		while (open.Count > 0 && settled.Count < MaxDijkstraNodes)
		{
			PathNode node = open.Pop(out int cost);
			if (!settled.Add(node))
			{
				continue;
			}
			if (node.RepresentativePosition == destination)
			{
				goal = node;
				break;
			}
			if (!(node is TerrainPathNode terrain) || terrain.siblings == null)
			{
				continue;
			}
			foreach (PathBranch branch in terrain.siblings)
			{
				PathNode next = branch.Node;
				if (next.IsEmpty || next.Lod != 0 || settled.Contains(next) || (evaluator != null && !evaluator.CanCross(branch)))
				{
					continue;
				}
				int nextCost = cost + branch.Weight;
				if (!best.TryGetValue(next, out int known) || nextCost < known)
				{
					best[next] = nextCost;
					previous[next] = node;
					open.Push(next, nextCost);
				}
			}
		}
		route.Searched = settled.Count;
		if (goal != null)
		{
			route.Found = true;
			route.Cost = best[goal];
			for (PathNode node = goal; node != null; node = previous.TryGetValue(node, out PathNode before) ? before : null)
			{
				route.Nodes.Add(node.RepresentativePosition);
			}
			route.Nodes.Reverse();
			route.Steps = route.Nodes.Count - 1;
		}
		watch.Stop();
		route.Milliseconds = watch.Elapsed.TotalMilliseconds;
		return route;
	}
}
