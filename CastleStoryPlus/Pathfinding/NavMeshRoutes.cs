#if UNITY6_PLAYER
using System.Collections.Generic;
using System.Diagnostics;
using BepInEx.Configuration;
using Brix.Engine;
using Brix.Game.AI;
using Brix.Game.Components.Volumes;
using Brix.Pathfinding;
using Brix.Pathfinding.Test;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;

namespace CastleStoryPlus.Pathfinding;

// Routes of the player's own units over Unity's navigation mesh (Unity 6 port only, [Pathfinding] NavMeshRoutes,
// off by default; needs the mesh of NavMeshExperiment). The game's A* searches the voxel graph on the main thread a
// slice per frame (9 ms per route on a big island); the mesh answers the same route in about 0.2 ms, in one go.
//   Which searches: a route search of a unit of a faction that is not the AI's, that does not break its way through
//   blocks and does not search away from something. Enemies and every route the mesh cannot give keep the A*.
//   Goal: the places of the graph round the target where the game's own search would stop (the target's
//   TargetReached: an access voxel of a building, the voxel itself, a voxel next to a unit), nearest first; a route
//   is asked to up to three of them.
//   Route: the mesh's corners are turned back into the voxels the walk needs. Between two corners, the voxels a
//   straight line passes through (4-connected), each one a branch of the graph from the one before (at the height
//   the mesh has there), and one the unit's own search would take (the same branch evaluator: branch types it may
//   use, doors it may pass, units in the way). A pair of corners that is one of the mesh's links (climbing, stepping
//   down, a ladder) becomes that branch of the graph, or the run of ladder branches. The voxels end at the first one
//   that reaches the target.
//   Result: the voxels are handed over as the search's result (a chain of the graph's own links), so the rest is the
//   game's: the route is unrolled and checked (PathUnroll), pulled straight (StraightPaths) and walked by Locomotion4
//   as before. Anything that does not fit (no route on the mesh, a voxel with no branch, a door or unit in the way,
//   the target not reached) leaves the request to the A* as if nothing had happened.
[Feature(Features.Pathfinding, Features.PathfindingInfo)]
internal static class NavMeshRoutes
{
	// Goals a route is asked to, nearest first.
	private const int MostGoals = 5;

	// The most voxels looked at round the target for goals, and the longest route made.
	private const int MostGoalCells = 20000;

	private const int MostRouteVoxels = 6000;

	// How far from a voxel's top a mesh point may be.
	private const float SampleDistance = 1f;

	internal static ConfigEntry<bool> Enabled;

	// Requests the mesh may route (false) and those it routed (true), from InitSearch to ConcludeSearch.
	private static readonly Dictionary<SearchPathRequest, bool> Requests = new Dictionary<SearchPathRequest, bool>();

	// Measurements for DevTools (/api/navmesh).
	internal static int Attempts;

	internal static int Routed;

	internal static int Fallbacks;

	internal static double Milliseconds;

	internal static double WorstMilliseconds;

	internal static long RouteVoxels;

	// Stretches where the line was blocked and the route went round over the graph.
	internal static int Mended;

	// The mesh's links routes took: climbs, steps down, ladders.
	internal static int Climbs;

	internal static int StepDowns;

	internal static int LadderRuns;

	// DevTools tests: one unit whose routes are counted on their own.
	internal static Transform Watched;

	internal static int WatchedRouted;

	internal static int WatchedFallbacks;

	internal static readonly Dictionary<string, int> Reasons = new Dictionary<string, int>();

	// The last fallbacks, with where they were, for DevTools.
	internal static readonly List<string> Recent = new List<string>();

	private static readonly NavMeshPath MeshPath = new NavMeshPath();

	private static Vector3[] _corners = new Vector3[64];

	private static readonly List<PathBranch> Route = new List<PathBranch>();

	private static readonly List<XYZ> Cells = new List<XYZ>();

	private static readonly List<TerrainPathNode> Goals = new List<TerrainPathNode>();

	private static readonly Queue<TerrainPathNode> LadderOpen = new Queue<TerrainPathNode>();

	// Per place the ladder search reached: the place before it and the branch from there.
	private static readonly Dictionary<TerrainPathNode, KeyValuePair<TerrainPathNode, PathBranch>> LadderCame = new Dictionary<TerrainPathNode, KeyValuePair<TerrainPathNode, PathBranch>>();

	private static readonly List<PathBranch> LadderRun = new List<PathBranch>();

	private static string _why;

	private static void Enable()
	{
		Enabled = Plugin.Cfg.Bind("Pathfinding", "NavMeshRoutes", false, "Unity 6 port only, needs NavMeshExperiment: routes of the player's own units are found on Unity's navigation mesh (well under a millisecond, in one go) instead of the game's search over the voxel graph, and turned back into the voxels the walk needs. Enemies, and any route the mesh cannot give, keep the game's search.");
		GameSession.OnLeave(Requests.Clear);
	}

	private static void Disable()
	{
		Requests.Clear();
	}

	internal static void ResetStats()
	{
		Attempts = 0;
		Routed = 0;
		Fallbacks = 0;
		Milliseconds = 0;
		WorstMilliseconds = 0;
		RouteVoxels = 0;
		Mended = 0;
		Climbs = 0;
		StepDowns = 0;
		LadderRuns = 0;
		Reasons.Clear();
		Recent.Clear();
	}

	internal static bool IsRouted(SearchPathRequest request)
	{
		return request != null && Requests.TryGetValue(request, out bool routed) && routed;
	}

	// A new search of a request (requests are pooled and reused): whether the mesh may route it.
	internal static void Started(SearchPathRequest request)
	{
		Requests.Remove(request);
		if (Enabled == null || !Enabled.Value || !NavMeshExperiment.Ready || request.SearchAway || request.CanBreakVoxels)
		{
			return;
		}
		Navigation navigation = request.Navigation;
		if (navigation == null || navigation.faction == null || navigation.faction.isAI || request.Location == null)
		{
			return;
		}
		if (Requests.Count > 512)
		{
			Requests.Clear();
		}
		Requests[request] = false;
	}

	internal static void Concluded(SearchPathRequest request)
	{
		Requests.Remove(request);
	}

	// The first slice of the search: route it over the mesh; true when it did (the search is then done).
	internal static bool TryRoute(SearchPathRequest request)
	{
		if (!Requests.TryGetValue(request, out bool routed) || routed)
		{
			return false;
		}
		long start = Stopwatch.GetTimestamp();
		Attempts++;
		_why = null;
		PathLink link = null;
		try
		{
			link = Find(request);
		}
		catch (System.Exception ex)
		{
			_why = "error: " + ex.Message;
		}
		double ms = StraightPaths.Since(start);
		Milliseconds += ms;
		if (ms > WorstMilliseconds)
		{
			WorstMilliseconds = ms;
		}
		if (link == null)
		{
			Requests.Remove(request);
			Fallbacks++;
			if (Watched != null && request.Navigation.transform == Watched)
			{
				WatchedFallbacks++;
			}
			string why = _why ?? "no route";
			int colon = why.IndexOf(':');
			string reason = (colon > 0) ? why.Substring(0, colon) : why;
			Reasons.TryGetValue(reason, out int count);
			Reasons[reason] = count + 1;
			Recent.Add(Time.frameCount + " " + request.Navigation.name + " from " + request.Origin.RepresentativePosition + " to " + request.Destination + " (" + request.Location.GetType().Name + "): " + why);
			if (Recent.Count > 40)
			{
				Recent.RemoveAt(0);
			}
			return false;
		}
		((SearchPathRequest.ResultAccepter)request.Accepter).Result = link;
		request.Done = true;
		Requests[request] = true;
		Routed++;
		if (Watched != null && request.Navigation.transform == Watched)
		{
			WatchedRouted++;
		}
		return true;
	}

	private static PathLink Fail(string why)
	{
		_why = why;
		return null;
	}

	private static PathLink Find(SearchPathRequest request)
	{
		TestPathfinding graph = NavMeshExperiment.Graph;
		TerrainPathNode origin = request.Origin as TerrainPathNode;
		if (graph == null || !NavMeshExperiment.Ready || graph != request.TestPathfinding)
		{
			return Fail("mesh not ready");
		}
		if (origin == null || origin.IsEmpty || origin.siblings == null)
		{
			return Fail("origin not a place of the graph");
		}
		if (!(request.Accepter is SearchPathRequest.ResultAccepter) || request.Evaluator == null || request.Builder == null)
		{
			return Fail("search not set up");
		}
		GameObject source = request.Navigation.gameObject;
		Location location = request.Location;
		if (Reached(location, source, origin))
		{
			return new PathLink(origin);
		}
		if (!FindGoals(request, graph, origin, source))
		{
			return null;
		}
		NavMeshQueryFilter filter = NavMeshExperiment.Filter(request.CanClimb, request.CanWalkInForest, request.Navigation.faction);
		if (!NavMesh.SamplePosition(NavMeshExperiment.TopCentre(origin), out NavMeshHit from, SampleDistance, filter))
		{
			return Fail("origin not on the mesh");
		}
		string why = "goal not on the mesh";
		for (int g = 0; g < Goals.Count; g++)
		{
			TerrainPathNode goal = Goals[g];
			if (!NavMesh.SamplePosition(NavMeshExperiment.TopCentre(goal), out NavMeshHit to, SampleDistance, filter))
			{
				continue;
			}
			if (!NavMesh.CalculatePath(from.position, to.position, filter, MeshPath) || MeshPath.status != NavMeshPathStatus.PathComplete)
			{
				why = "no route on the mesh";
				continue;
			}
			int count = MeshPath.GetCornersNonAlloc(_corners);
			while (count == _corners.Length)
			{
				_corners = new Vector3[_corners.Length * 2];
				count = MeshPath.GetCornersNonAlloc(_corners);
			}
			if (count < 1)
			{
				why = "no route on the mesh";
				continue;
			}
			_why = null;
			if (!Voxels(request, origin, goal, count))
			{
				why = _why ?? "route not walkable";
				continue;
			}
			// Up to the first voxel that reaches the target, as the game's search stops there.
			int end = -1;
			for (int i = 1; i < Route.Count; i++)
			{
				if (Reached(location, source, (TerrainPathNode)Route[i].Node))
				{
					end = i;
					break;
				}
			}
			if (end < 0)
			{
				why = "target not reached";
				continue;
			}
			PathBuilder builder = request.Builder;
			PathLink link = new PathLink(origin);
			for (int i = 1; i <= end; i++)
			{
				link = new PathLink(builder, Route[i], link);
			}
			RouteVoxels += end;
			return link;
		}
		return Fail(why);
	}

	private static bool Reached(Location location, GameObject source, TerrainPathNode node)
	{
		return node != null && location.TargetReached(source, node.RepresentativePosition.ToVector3());
	}

	// The places round the target where the search would stop, nearest to the origin first (at most MostGoals).
	private static bool FindGoals(SearchPathRequest request, TestPathfinding graph, TerrainPathNode origin, GameObject source)
	{
		Goals.Clear();
		Location location = request.Location;
		XYZ destination = request.Destination;
		XYZ min;
		XYZ max;
		if (location is CharacterLocation)
		{
			min = destination - XYZ.one * 2;
			max = destination + XYZ.one * 2;
		}
		else if (location is ObjectLocation)
		{
			IVolume volume = (location.Object != null) ? location.Object.GetComponent<IVolume>() : null;
			if (volume != null)
			{
				VoxelBounds bounds = volume.ComputeBounds();
				min = bounds.Min - XYZ.one;
				max = bounds.Max + XYZ.one;
			}
			else
			{
				min = destination - XYZ.one * 2;
				max = destination + XYZ.one * 2;
			}
		}
		else if (location is ReachSpaceLocation)
		{
			min = destination - XYZ.one;
			max = destination + XYZ.one;
		}
		else if (location is SpaceLocation)
		{
			min = destination;
			max = destination;
		}
		else
		{
			Fail("target kind " + location.GetType().Name);
			return false;
		}
		long cells = (long)(max.x - min.x + 1) * (max.y - min.y + 1) * (max.z - min.z + 1);
		if (cells > MostGoalCells)
		{
			Fail("target too big: " + cells + " voxels");
			return false;
		}
		XYZ o = origin.position;
		for (int x = min.x; x <= max.x; x++)
		{
			for (int y = min.y; y <= max.y; y++)
			{
				for (int z = min.z; z <= max.z; z++)
				{
					XYZ p = new XYZ(x, y, z);
					if (!graph.TerrainNodes.TryGetValue(p, out TerrainPathNode node) || !NavMeshExperiment.IsStandable(node) || !Reached(location, source, node))
					{
						continue;
					}
					// Keep the nearest few, sorted.
					int d = Distance(p, o);
					int at = Goals.Count;
					while (at > 0 && Distance(Goals[at - 1].position, o) > d)
					{
						at--;
					}
					if (at < MostGoals)
					{
						Goals.Insert(at, node);
						if (Goals.Count > MostGoals)
						{
							Goals.RemoveAt(MostGoals);
						}
					}
				}
			}
		}
		if (Goals.Count == 0)
		{
			Fail("no place reaches the target");
			return false;
		}
		return true;
	}

	private static int Distance(XYZ a, XYZ b)
	{
		int dx = a.x - b.x;
		int dy = a.y - b.y;
		int dz = a.z - b.z;
		return dx * dx + dy * dy + dz * dz;
	}

	// The mesh's corners as the graph's voxels, into Route (the origin first).
	private static bool Voxels(SearchPathRequest request, TerrainPathNode origin, TerrainPathNode goal, int count)
	{
		PathBranchEvaluator evaluator = request.Evaluator;
		Route.Clear();
		Route.Add(new PathBranch(origin));
		TerrainPathNode current = origin;
		// Walk from the origin's own top, not the point the mesh moved it to; to the goal's top at the end.
		Vector3 previous = NavMeshExperiment.TopCentre(origin);
		for (int i = 1; i < count; i++)
		{
			Vector3 corner = (i == count - 1) ? NavMeshExperiment.TopCentre(goal) : Nudged(i);
			NavMeshExperiment.LinkInfo link = LinkAt(current, _corners[i - 1], _corners[i]);
			if (link != null)
			{
				if (!Cross(current, link, evaluator, out current))
				{
					return false;
				}
				previous = corner;
				continue;
			}
			if (!Walk(ref current, previous, corner, evaluator, i == count - 1))
			{
				return false;
			}
			previous = corner;
			if (Route.Count > MostRouteVoxels)
			{
				_why = "route too long";
				return false;
			}
		}
		if (current != goal)
		{
			_why = "route ends at " + current.position + ", not at the goal " + goal.position;
			return false;
		}
		return true;
	}

	// A corner where the route bends round something, moved a little away from it. Where two areas meet (forest and
	// plain ground, a door no friend of the unit's), Unity keeps no distance from the border: the corner lies on it,
	// on the border between two voxels, and the line along it would as often run through the voxels on the far side.
	private const float Nudge = 0.15f;

	private static Vector3 Nudged(int i)
	{
		Vector3 corner = _corners[i];
		Vector3 inward = (_corners[i - 1] - corner).normalized + (_corners[i + 1] - corner).normalized;
		inward.y = 0f;
		if (inward.sqrMagnitude < 1e-4f)
		{
			return corner;
		}
		return corner - inward.normalized * Nudge;
	}

	// The mesh's link from this place between these two corners, if they are one.
	private static NavMeshExperiment.LinkInfo LinkAt(TerrainPathNode current, Vector3 a, Vector3 b)
	{
		if (!NavMeshExperiment.LinksFrom.TryGetValue(current.position, out List<NavMeshExperiment.LinkInfo> links))
		{
			return null;
		}
		foreach (NavMeshExperiment.LinkInfo link in links)
		{
			if ((link.Start - a).sqrMagnitude < 0.25f && (link.End - b).sqrMagnitude < 0.25f)
			{
				return link;
			}
		}
		return null;
	}

	// A link's branches: the climb or step down itself, or the run of ladder branches up or down to its exit.
	private static bool Cross(TerrainPathNode current, NavMeshExperiment.LinkInfo link, PathBranchEvaluator evaluator, out TerrainPathNode reached)
	{
		reached = current;
		if (link.Area != NavMeshExperiment.AreaLadder)
		{
			PathBranch.BranchType type = (link.Area == NavMeshExperiment.AreaClimb) ? PathBranch.BranchType.Climbing : PathBranch.BranchType.StepDown;
			foreach (PathBranch branch in current.siblings)
			{
				if (branch.Type == type && branch.Node is TerrainPathNode node && node.position == link.To && !node.IsEmpty)
				{
					if (!evaluator.CanCross(branch))
					{
						_why = "refused: " + type + " to " + node.position;
						return false;
					}
					Route.Add(branch);
					reached = node;
					if (link.Area == NavMeshExperiment.AreaClimb)
					{
						Climbs++;
					}
					else
					{
						StepDowns++;
					}
					return true;
				}
			}
			_why = "stale link: no " + type + " branch from " + current.position + " to " + link.To;
			return false;
		}
		// The ladder: a search over ladder branches from the place to the exit.
		LadderOpen.Clear();
		LadderCame.Clear();
		LadderOpen.Enqueue(current);
		LadderCame[current] = default;
		TerrainPathNode exit = null;
		while (LadderOpen.Count > 0 && LadderCame.Count < 128)
		{
			TerrainPathNode here = LadderOpen.Dequeue();
			if (here.position == link.To && here != current)
			{
				exit = here;
				break;
			}
			if (here != current && NavMeshExperiment.IsStandable(here))
			{
				continue;
			}
			if (here.siblings == null)
			{
				continue;
			}
			foreach (PathBranch branch in here.siblings)
			{
				if (branch.Type != PathBranch.BranchType.Ladder || !(branch.Node is TerrainPathNode next) || next.IsEmpty || LadderCame.ContainsKey(next) || !evaluator.CanCross(branch))
				{
					continue;
				}
				LadderCame[next] = new KeyValuePair<TerrainPathNode, PathBranch>(here, branch);
				LadderOpen.Enqueue(next);
			}
		}
		if (exit == null)
		{
			_why = "stale link: no ladder from " + current.position + " to " + link.To;
			return false;
		}
		LadderRun.Clear();
		for (TerrainPathNode node = exit; node != current && node != null;)
		{
			KeyValuePair<TerrainPathNode, PathBranch> came = LadderCame[node];
			LadderRun.Add(came.Value);
			node = came.Key;
		}
		for (int k = LadderRun.Count - 1; k >= 0; k--)
		{
			Route.Add(LadderRun[k]);
		}
		LadderRuns++;
		reached = exit;
		return true;
	}

	// A straight walk between two points on the mesh: the voxels the line passes, each a branch from the one before.
	private static bool Walk(ref TerrainPathNode current, Vector3 a, Vector3 b, PathBranchEvaluator evaluator, bool last)
	{
		Line(a, b, Cells);
		XYZ at = current.position;
		for (int k = 0; k < Cells.Count; k++)
		{
			XYZ cell = Cells[k];
			if (cell.x == at.x && cell.z == at.z)
			{
				continue;
			}
			// The height the mesh has over this column, along the line.
			float t = Along(a, b, cell);
			float y = Mathf.Lerp(a.y, b.y, t);
			TerrainPathNode next = Step(current, cell.x, cell.z, y, evaluator, out PathBranch branch);
			if (next == null && k + 1 < Cells.Count)
			{
				// The line cuts a corner closely (the mesh keeps a quarter of a block from it): round the corner
				// through the other voxel beside it, when the voxel after is diagonal to this one.
				XYZ after = Cells[k + 1];
				if (Mathf.Abs(after.x - at.x) == 1 && Mathf.Abs(after.z - at.z) == 1)
				{
					int ox = (cell.x != at.x) ? at.x : after.x;
					int oz = (cell.z != at.z) ? at.z : after.z;
					string why = _why;
					TerrainPathNode other = Step(current, ox, oz, y, evaluator, out PathBranch otherBranch);
					PathBranch thenBranch = default;
					TerrainPathNode then = null;
					if (other != null)
					{
						then = Step(other, after.x, after.z, y, evaluator, out thenBranch);
					}
					if (then != null)
					{
						Route.Add(otherBranch);
						Route.Add(thenBranch);
						current = then;
						at = then.position;
						k++;
						continue;
					}
					_why = why;
				}
			}
			if (next == null)
			{
				if (k == Cells.Count - 1 && !last)
				{
					// The corner's own voxel: the next stretch starts from here and mends its way round it.
					return true;
				}
				int reached = Mend(ref current, k, a, b, evaluator);
				if (reached < 0)
				{
					return false;
				}
				k = reached;
				at = current.position;
				continue;
			}
			Route.Add(branch);
			current = next;
			at = next.position;
		}
		return true;
	}

	// How far a mended stretch may go round what blocks the line: places looked at, and blocks from where it starts.
	private const int MendPlaces = 400;

	private const int MendReach = 10;

	// Voxels of the line ahead a mended stretch may come back to.
	private const int MendAhead = 16;

	private static readonly Queue<TerrainPathNode> MendOpen = new Queue<TerrainPathNode>();

	private static readonly Dictionary<TerrainPathNode, KeyValuePair<TerrainPathNode, PathBranch>> MendCame = new Dictionary<TerrainPathNode, KeyValuePair<TerrainPathNode, PathBranch>>();

	private static readonly Dictionary<long, int> MendTargets = new Dictionary<long, int>();

	private static readonly List<PathBranch> MendRun = new List<PathBranch>();

	private static long Column(int x, int z)
	{
		return ((long)x << 32) ^ (uint)z;
	}

	// The line is blocked at its voxel k (a full pallet, a unit, a door, a voxel the mesh has and the graph does not
	// link): the shortest way over the graph from where the walk is to a voxel of the line a little further on, by
	// the unit's own rules. Returns the index of the voxel of the line reached, or -1.
	private static int Mend(ref TerrainPathNode current, int k, Vector3 a, Vector3 b, PathBranchEvaluator evaluator)
	{
		string why = _why;
		MendTargets.Clear();
		int lastTarget = Mathf.Min(Cells.Count - 1, k + MendAhead);
		for (int j = k; j <= lastTarget; j++)
		{
			MendTargets[Column(Cells[j].x, Cells[j].z)] = j;
		}
		MendOpen.Clear();
		MendCame.Clear();
		MendOpen.Enqueue(current);
		MendCame[current] = default;
		XYZ start = current.position;
		TerrainPathNode found = null;
		int foundAt = -1;
		while (MendOpen.Count > 0 && MendCame.Count < MendPlaces)
		{
			TerrainPathNode here = MendOpen.Dequeue();
			if (here != current && MendTargets.TryGetValue(Column(here.position.x, here.position.z), out int j))
			{
				float y = Mathf.Lerp(a.y, b.y, Along(a, b, Cells[j]));
				if (Mathf.Abs(NavMeshExperiment.TopCentre(here).y - y) < 1f)
				{
					found = here;
					foundAt = j;
					break;
				}
			}
			if (here.siblings == null || Mathf.Abs(here.position.x - start.x) + Mathf.Abs(here.position.z - start.z) >= MendReach)
			{
				continue;
			}
			foreach (PathBranch branch in here.siblings)
			{
				if (!(branch.Node is TerrainPathNode next) || next.IsEmpty || MendCame.ContainsKey(next) || !IsWalk(branch.Type) || !evaluator.CanCross(branch))
				{
					continue;
				}
				MendCame[next] = new KeyValuePair<TerrainPathNode, PathBranch>(here, branch);
				MendOpen.Enqueue(next);
			}
		}
		if (found == null)
		{
			_why = why + "; no way round within " + MendReach + " blocks";
			return -1;
		}
		MendRun.Clear();
		for (TerrainPathNode node = found; node != current && node != null;)
		{
			KeyValuePair<TerrainPathNode, PathBranch> came = MendCame[node];
			MendRun.Add(came.Value);
			node = came.Key;
		}
		for (int r = MendRun.Count - 1; r >= 0; r--)
		{
			Route.Add(MendRun[r]);
		}
		Mended++;
		current = found;
		return foundAt;
	}

	// A branch a walk on the mesh's ground may take: not a climb, a ladder or breaking in.
	private static bool IsWalk(PathBranch.BranchType type)
	{
		return type != PathBranch.BranchType.Ladder && type != PathBranch.BranchType.Breaking && type != PathBranch.BranchType.Climbing && type != PathBranch.BranchType.Empty;
	}

	private static float Along(Vector3 a, Vector3 b, XYZ cell)
	{
		float dx = b.x - a.x;
		float dz = b.z - a.z;
		float length = dx * dx + dz * dz;
		if (length < 1e-6f)
		{
			return 1f;
		}
		return Mathf.Clamp01(((cell.x - a.x) * dx + (cell.z - a.z) * dz) / length);
	}

	// The branch from a place to the column next to it, at the height nearest the mesh's: a walk (not a climb, a
	// ladder or breaking in), one the unit's search would take.
	private static TerrainPathNode Step(TerrainPathNode current, int x, int z, float y, PathBranchEvaluator evaluator, out PathBranch chosen)
	{
		chosen = default;
		TerrainPathNode best = null;
		float bestDiff = float.MaxValue;
		bool refused = false;
		if (Mathf.Abs(x - current.position.x) + Mathf.Abs(z - current.position.z) != 1 || current.siblings == null)
		{
			_why = "line leaves the graph at " + x + "," + z + " from " + current.position;
			return null;
		}
		foreach (PathBranch branch in current.siblings)
		{
			if (!(branch.Node is TerrainPathNode node) || node.IsEmpty || node.position.x != x || node.position.z != z)
			{
				continue;
			}
			if (!IsWalk(branch.Type))
			{
				continue;
			}
			if (!evaluator.CanCross(branch))
			{
				refused = true;
				continue;
			}
			float diff = Mathf.Abs(NavMeshExperiment.TopCentre(node).y - y);
			if (diff < bestDiff)
			{
				bestDiff = diff;
				best = node;
				chosen = branch;
			}
		}
		if (best == null)
		{
			_why = refused ? ("refused: the step to " + x + "," + z + " from " + current.position + " (a door, building or unit)") : ("no step: from " + current.position + " to " + x + "," + z);
			return null;
		}
		if (bestDiff > 1f)
		{
			_why = "height: the step to " + best.position + " is " + bestDiff.ToString("0.0") + " off the mesh";
			return null;
		}
		return best;
	}

	// The voxel columns a straight line between two points passes through, in order, each a side neighbour of the
	// one before; the start's column first.
	private static void Line(Vector3 a, Vector3 b, List<XYZ> cells)
	{
		cells.Clear();
		int x = Mathf.RoundToInt(a.x);
		int z = Mathf.RoundToInt(a.z);
		int endX = Mathf.RoundToInt(b.x);
		int endZ = Mathf.RoundToInt(b.z);
		cells.Add(new XYZ(x, 0, z));
		float dx = b.x - a.x;
		float dz = b.z - a.z;
		int stepX = (dx > 0f) ? 1 : -1;
		int stepZ = (dz > 0f) ? 1 : -1;
		float deltaX = (Mathf.Abs(dx) > 1e-6f) ? Mathf.Abs(1f / dx) : float.MaxValue;
		float deltaZ = (Mathf.Abs(dz) > 1e-6f) ? Mathf.Abs(1f / dz) : float.MaxValue;
		float nextX = (Mathf.Abs(dx) > 1e-6f) ? (x + 0.5f * stepX - a.x) / dx : float.MaxValue;
		float nextZ = (Mathf.Abs(dz) > 1e-6f) ? (z + 0.5f * stepZ - a.z) / dz : float.MaxValue;
		int guard = Mathf.Abs(endX - x) + Mathf.Abs(endZ - z) + 4;
		while ((x != endX || z != endZ) && guard-- > 0)
		{
			if (nextX < nextZ)
			{
				x += stepX;
				nextX += deltaX;
			}
			else
			{
				z += stepZ;
				nextZ += deltaZ;
			}
			cells.Add(new XYZ(x, 0, z));
		}
	}
}

// A new search: whether the mesh may route it.
[Feature(Features.Pathfinding, Features.PathfindingInfo)]
[HarmonyPatch(typeof(SearchPathRequest), "InitSearch")]
internal static class NavMeshRoutesStartPatch
{
	private static void Postfix(SearchPathRequest __instance)
	{
		NavMeshRoutes.Started(__instance);
	}
}

// The search's first slice routes it over the mesh; done then, with the route as its result. After the frame budget
// (SearchFrameBudget) and the DevTools measurements, which count this as the search's time.
[Feature(Features.Pathfinding, Features.PathfindingInfo)]
[HarmonyPatch(typeof(SearchPathRequest), "Work")]
internal static class NavMeshRoutesWorkPatch
{
	[HarmonyPriority(Priority.Low)]
	private static bool Prefix(SearchPathRequest __instance, ref bool __result, bool __runOriginal)
	{
		if (!__runOriginal || !NavMeshRoutes.TryRoute(__instance))
		{
			return __runOriginal;
		}
		__result = true;
		return false;
	}
}

// A routed search needs no finer search afterwards: the route is already one of the finest level's voxels.
[Feature(Features.Pathfinding, Features.PathfindingInfo)]
[HarmonyPatch(typeof(SearchPathRequest), "LoopImprovePath")]
internal static class NavMeshRoutesImprovePatch
{
	[HarmonyPriority(Priority.Low)]
	private static bool Prefix(SearchPathRequest __instance, ref bool __result, bool __runOriginal)
	{
		if (!__runOriginal || !NavMeshRoutes.IsRouted(__instance))
		{
			return __runOriginal;
		}
		__result = true;
		return false;
	}
}

[Feature(Features.Pathfinding, Features.PathfindingInfo)]
[HarmonyPatch(typeof(SearchPathRequest), "ConcludeSearch")]
internal static class NavMeshRoutesConcludePatch
{
	[HarmonyPriority(Priority.Last)]
	private static void Postfix(SearchPathRequest __instance)
	{
		NavMeshRoutes.Concluded(__instance);
	}
}
#endif
