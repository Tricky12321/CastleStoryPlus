#if UNITY6_PLAYER
using System.Collections.Generic;
using System.Diagnostics;
using Brix.Engine;
using Brix.Game.AI;
using Brix.Pathfinding.Test;
using CastleStoryPlus.DevTools.Api;
using CastleStoryPlus.DevTools.Pathing;
using CastleStoryPlus.Pathfinding;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Networking;
using static CastleStoryPlus.DevTools.Endpoints.Json;

namespace CastleStoryPlus.DevTools.Endpoints;

// The navigation mesh experiment (CastleStoryPlus.Pathfinding.NavMeshExperiment, Unity 6 port): start and stop it,
// its build and update times, and routes over it compared with the game's own A* for the same start and goal.
internal static class NavMeshEndpoints
{
	internal static void Add(List<Route> routes)
	{
		routes.Add(new Route("GET", "/api/navmesh", "The navigation mesh experiment: phase, agent shape, what the build and the updates cost (main thread and on Unity's workers), memory", Get)
		{
			Parameters = "triangles (true: count the mesh's triangles and vertices, slow on a big island)"
		});
		routes.Add(new Route("POST", "/api/navmesh/start", "Builds the navigation mesh from the route graph (again), with the agent shape given", Start)
		{
			Parameters = "radius (0.25), height (0.9), climb (0.6), slope (60), voxel (cell size, 0.125), tile (cells per tile, 128)"
		});
		routes.Add(new Route("POST", "/api/navmesh/stop", "Removes the navigation mesh and stops keeping it up to date", Stop));
		routes.Add(new Route("GET", "/api/navmesh/path", "One route over the navigation mesh next to the game's A* for the same start and goal: time, length, corners, whether both reach the goal", Path)
		{
			Parameters = "to (x,y,z, required), from (x,y,z; default: where the bricktron stands), unit (bricktron id), enemy (true), obstacles (true: buildings' ground counts as walkable), shortest (true: also the whole graph's answer, a Dijkstra), nodes (true: list the A* voxels and the mesh's corners)",
			TimeoutMs = 60000
		});
		routes.Add(new Route("GET", "/api/navmesh/bench", "Routes between random places of the graph near the bricktron, each over the navigation mesh, by the game's A* and by a Dijkstra over the whole graph (the reference): times, lengths and how often they agree on reaching the goal", Bench)
		{
			Parameters = "count (30, at most 200), radius (blocks round the bricktron, 64), seed (1), unit, enemy, obstacles (false; true: buildings' ground counts as walkable), samples (disagreements listed, 10)",
			TimeoutMs = 600000,
			// One pair per frame on the main thread: the game's route search run many times in one frame failed
			// routes it finds when run on its own.
			OffMainThread = true
		});
	}

	private static object Get(ApiRequest request)
	{
		NavMeshExperiment.Stats s = NavMeshExperiment.Measured;
		object triangles = null;
		if (request.Bool("triangles", false) && NavMeshExperiment.Ready)
		{
			Stopwatch watch = Stopwatch.StartNew();
			NavMeshTriangulation t = NavMesh.CalculateTriangulation();
			triangles = Obj("vertices", t.vertices.Length, "triangles", t.indices.Length / 3, "ms", Ms(watch.Elapsed.TotalMilliseconds));
		}
		return Obj(
			"active", NavMeshExperiment.Active,
			"phase", NavMeshExperiment.Phase,
			"problem", NavMeshExperiment.Problem,
			"busy", NavMeshExperiment.Busy,
			"agent", Obj("typeId", NavMeshExperiment.AgentTypeId, "radius", NavMeshExperiment.Radius, "height", NavMeshExperiment.Height, "climb", NavMeshExperiment.Climb, "slope", NavMeshExperiment.Slope, "voxel", NavMeshExperiment.VoxelSize, "tile", NavMeshExperiment.TileSize),
			"graph", Obj("places", s.Nodes, "chunks", s.Chunks, "quads", s.Quads, "links", s.Links),
			"build", Obj(
				"indexMs", Ms(s.IndexMs),
				"meshMs", Ms(s.MeshMs),
				"meshFrames", s.MeshFrames,
				"meshWorstFrameMs", Ms(s.MeshWorstFrameMs),
				"dispatchMs", Ms(s.DispatchMs),
				"asyncBuildMs", Ms(s.BuildWallMs),
				"linksMs", Ms(s.LinkMs),
				"totalMs", Ms(s.TotalWallMs)),
			"updates", Obj(
				"count", s.Updates,
				"chunks", s.UpdatedChunks,
				"mainMsAverage", Ms(Per(s.UpdateMainMs, s.Updates)),
				"mainMsWorst", Ms(s.UpdateMainWorstMs),
				"latencyMsAverage", Ms(Per(s.UpdateLatencyMs, s.Updates)),
				"latencyMsWorst", Ms(s.UpdateLatencyWorstMs),
				"lastLatencyMs", Ms(s.LastUpdateLatencyMs),
				"lastAsyncMs", Ms(s.LastUpdateAsyncMs),
				"markCalls", s.Marks,
				"markMsAverage", Ms(Per(s.MarkMs, s.Marks))),
			"frames", Obj(
				"withWork", s.FramesWithWork,
				"workMsAverage", Ms(Per(s.FrameWorkMs, s.FramesWithWork)),
				"workMsWorst", Ms(s.FrameWorkWorstMs),
				"withAsync", s.FramesWithAsync,
				"frameMsWithAsync", Ms(Per(s.FrameDeltaAsyncMs, s.FramesWithAsync)),
				"idle", s.FramesIdle,
				"frameMsIdle", Ms(Per(s.FrameDeltaIdleMs, s.FramesIdle))),
			"memory", Obj("meshBytes", NavMeshExperiment.MeshBytes(), "navMeshDataBytes", NavMeshExperiment.DataBytes()),
			"triangulation", triangles);
	}

	private static object Start(ApiRequest request)
	{
		NavMeshExperiment.Radius = request.Float("radius", 0.25f);
		NavMeshExperiment.Height = request.Float("height", 0.9f);
		NavMeshExperiment.Climb = request.Float("climb", 0.6f);
		NavMeshExperiment.Slope = request.Float("slope", 60f);
		NavMeshExperiment.VoxelSize = request.Float("voxel", 0.125f);
		NavMeshExperiment.TileSize = request.Int("tile", 128);
		NavMeshExperiment.Start();
		return Obj("started", true);
	}

	private static object Stop(ApiRequest request)
	{
		NavMeshExperiment.Stop();
		return Obj("stopped", true);
	}

	private sealed class Comparison
	{
		public PathProbe.Report Report;

		public bool MeshFound;

		public NavMeshPathStatus Status;

		public double SampleMs;

		public double PathMs;

		public float MeshLength;

		public float AStarLength;

		public Vector3[] Corners;

		public string Problem;
	}

	private static void NeedMesh()
	{
		if (!NetworkServer.active)
		{
			throw new ApiException(409, "Route searches only run on the host (single player or hosting)");
		}
		if (!PathProbe.Ready())
		{
			throw new ApiException(409, "The navigation graph is not ready (no game, or still loading)");
		}
		if (!NavMeshExperiment.Ready)
		{
			throw new ApiException(409, "The navigation mesh is not built (POST /api/navmesh/start; phase " + NavMeshExperiment.Phase + ")");
		}
	}

	private static Labor Unit(ApiRequest request)
	{
		Labor labor = Units.Find(request.Int("unit", 0), request.Bool("enemy", false));
		if (labor == null)
		{
			throw new ApiException(404, "No bricktron to search for (unit id not found, or no unit of that side)");
		}
		return labor;
	}

	private static NavMeshQueryFilter FilterFor(Labor labor, bool obstacles)
	{
		Navigation navigation = labor.navigation;
		bool canClimb = navigation.Interaction.CanClimb();
		bool forest = labor.Profession.CanWalkInForest();
		bool doors = navigation.faction == null || !navigation.faction.isAI;
		return NavMeshExperiment.Filter(canClimb, forest, obstacles, doors);
	}

	private static Comparison Compare(Labor labor, XYZ from, XYZ to, NavMeshQueryFilter filter, bool withShortest)
	{
		Comparison c = new Comparison();
		c.Report = PathProbe.Run(labor, from, to, withShortest);
		if (!c.Report.Valid)
		{
			c.Problem = c.Report.Problem;
			return c;
		}
		Dictionary<XYZ, TerrainPathNode> nodes = TestPathfinding.Instance.TerrainNodes;
		if (!nodes.TryGetValue(c.Report.Origin, out TerrainPathNode origin) || !nodes.TryGetValue(c.Report.Destination, out TerrainPathNode goal))
		{
			c.Problem = "start or goal has no place in the graph";
			return c;
		}
		if (c.Report.AStar != null && c.Report.AStar.Found)
		{
			Vector3? last = null;
			foreach (XYZ p in c.Report.AStar.Nodes)
			{
				Vector3 here = nodes.TryGetValue(p, out TerrainPathNode n) ? NavMeshExperiment.TopCentre(n) : p.ToVector3();
				if (last.HasValue)
				{
					c.AStarLength += Vector3.Distance(last.Value, here);
				}
				last = here;
			}
		}
		Stopwatch watch = Stopwatch.StartNew();
		bool a = NavMesh.SamplePosition(NavMeshExperiment.TopCentre(origin), out NavMeshHit start, 1f, filter);
		bool b = NavMesh.SamplePosition(NavMeshExperiment.TopCentre(goal), out NavMeshHit end, 1f, filter);
		c.SampleMs = watch.Elapsed.TotalMilliseconds;
		if (!a || !b)
		{
			c.Problem = !a ? "start is not on the mesh" : "goal is not on the mesh";
			return c;
		}
		NavMeshPath path = new NavMeshPath();
		watch.Reset();
		watch.Start();
		NavMesh.CalculatePath(start.position, end.position, filter, path);
		c.PathMs = watch.Elapsed.TotalMilliseconds;
		c.Status = path.status;
		c.Corners = path.corners;
		for (int i = 1; i < c.Corners.Length; i++)
		{
			c.MeshLength += Vector3.Distance(c.Corners[i - 1], c.Corners[i]);
		}
		c.MeshFound = path.status == NavMeshPathStatus.PathComplete && c.Corners.Length > 0 && Vector3.Distance(c.Corners[c.Corners.Length - 1], end.position) < 0.5f;
		return c;
	}

	private static object Path(ApiRequest request)
	{
		NeedMesh();
		Labor labor = Unit(request);
		XYZ to = XYZ.FromVector3(request.Vector("to"));
		XYZ from = (request.Get("from") != null) ? XYZ.FromVector3(request.Vector("from")) : Units.VoxelOf(labor);
		Comparison c = Compare(labor, from, to, FilterFor(labor, request.Bool("obstacles", false)), request.Bool("shortest", false));
		bool withNodes = request.Bool("nodes", false);
		List<float[]> corners = null;
		if (withNodes && c.Corners != null)
		{
			corners = new List<float[]>();
			foreach (Vector3 corner in c.Corners)
			{
				corners.Add(Vec(corner));
			}
		}
		List<int[]> voxels = null;
		if (withNodes && c.Report.AStar != null)
		{
			voxels = new List<int[]>();
			foreach (XYZ p in c.Report.AStar.Nodes)
			{
				voxels.Add(PathEndpoints.Voxel(p));
			}
		}
		PathProbe.Route astar = c.Report.AStar;
		return Obj(
			"unit", labor.name,
			"problem", c.Problem,
			"origin", PathEndpoints.Voxel(c.Report.Origin),
			"destination", PathEndpoints.Voxel(c.Report.Destination),
			"graph", (c.Report.Shortest == null) ? null : Obj("found", c.Report.Shortest.Found, "ms", Ms(c.Report.Shortest.Milliseconds), "searched", c.Report.Shortest.Searched),
			"astar", (astar == null) ? null : Obj("found", astar.Found, "ms", Ms(astar.Milliseconds), "searched", astar.Searched, "steps", astar.Steps, "cost", astar.Cost, "length", Round(c.AStarLength), "voxels", voxels),
			"navmesh", Obj("found", c.MeshFound, "status", c.Status.ToString(), "ms", Ms(c.PathMs), "sampleMs", Ms(c.SampleMs), "corners", (c.Corners != null) ? c.Corners.Length : 0, "length", Round(c.MeshLength), "points", corners),
			"agree", c.Problem == null && astar != null && astar.Found == c.MeshFound);
	}

	private static object Bench(ApiRequest request)
	{
		Labor labor = null;
		NavMeshQueryFilter filter = default;
		XYZ centre = XYZ.zero;
		List<XYZ> places = new List<XYZ>();
		int radius = request.Int("radius", 64);
		bool obstacles = request.Bool("obstacles", false);
		MainThread.Run(() =>
		{
			NeedMesh();
			labor = Unit(request);
			filter = FilterFor(labor, obstacles);
			centre = Units.VoxelOf(labor);
			foreach (KeyValuePair<XYZ, TerrainPathNode> entry in TestPathfinding.Instance.TerrainNodes)
			{
				XYZ p = entry.Key;
				if (System.Math.Abs(p.x - centre.x) <= radius && System.Math.Abs(p.z - centre.z) <= radius && NavMeshExperiment.IsStandable(entry.Value))
				{
					places.Add(p);
				}
			}
			return null;
		}, 30000);
		int count = Mathf.Clamp(request.Int("count", 30), 1, 200);
		int samples = request.Int("samples", 10);
		System.Random random = new System.Random(request.Int("seed", 1));
		if (places.Count < 2)
		{
			throw new ApiException(409, "Too few places round the bricktron");
		}
		int valid = 0, both = 0, neither = 0, astarOnly = 0, meshOnly = 0, graphFound = 0, meshMatchesGraph = 0, astarMatchesGraph = 0;
		List<double> graphMs = new List<double>();
		List<double> astarMs = new List<double>();
		List<double> meshMs = new List<double>();
		List<double> astarFoundMs = new List<double>();
		List<double> meshFoundMs = new List<double>();
		List<double> lengthRatio = new List<double>();
		List<double> corners = new List<double>();
		List<double> steps = new List<double>();
		List<object> disagreements = new List<object>();
		for (int i = 0; i < count; i++)
		{
			XYZ from = places[random.Next(places.Count)];
			XYZ to = places[random.Next(places.Count)];
			Comparison c = (Comparison)MainThread.Run(() => Compare(labor, from, to, filter, true), 60000);
			if (c.Problem != null || c.Report.AStar == null)
			{
				if (disagreements.Count < samples)
				{
					disagreements.Add(Obj("from", PathEndpoints.Voxel(from), "to", PathEndpoints.Voxel(to), "problem", c.Problem));
				}
				continue;
			}
			valid++;
			bool astar = c.Report.AStar.Found;
			// The whole graph's answer (a Dijkstra with the same rules): what both should agree with.
			bool graph = c.Report.Shortest != null && c.Report.Shortest.Found;
			if (c.Report.Shortest != null)
			{
				graphMs.Add(c.Report.Shortest.Milliseconds);
			}
			graphFound += graph ? 1 : 0;
			meshMatchesGraph += (graph == c.MeshFound) ? 1 : 0;
			astarMatchesGraph += (graph == astar) ? 1 : 0;
			astarMs.Add(c.Report.AStar.Milliseconds);
			meshMs.Add(c.PathMs + c.SampleMs);
			if (astar)
			{
				astarFoundMs.Add(c.Report.AStar.Milliseconds);
				steps.Add(c.Report.AStar.Steps);
			}
			if (c.MeshFound)
			{
				meshFoundMs.Add(c.PathMs + c.SampleMs);
				corners.Add(c.Corners.Length);
			}
			if (astar && c.MeshFound)
			{
				both++;
				if (c.AStarLength > 0)
				{
					lengthRatio.Add(c.MeshLength / c.AStarLength);
				}
			}
			else if (!astar && !c.MeshFound)
			{
				neither++;
			}
			else
			{
				if (astar)
				{
					astarOnly++;
				}
				else
				{
					meshOnly++;
				}
				if (disagreements.Count < samples)
				{
					disagreements.Add(Obj("from", PathEndpoints.Voxel(c.Report.Origin), "to", PathEndpoints.Voxel(c.Report.Destination), "graph", graph, "astar", astar, "astarSearched", c.Report.AStar.Searched, "navmesh", c.Status.ToString(), "meshLength", Round(c.MeshLength)));
				}
			}
		}
		return Obj(
			"unit", labor.name,
			"places", places.Count,
			"pairs", count,
			"valid", valid,
			"bothFound", both,
			"neitherFound", neither,
			"astarOnly", astarOnly,
			"navmeshOnly", meshOnly,
			"agreement", (valid > 0) ? Round((float)(both + neither) / valid) : 0f,
			"graphFound", graphFound,
			"navmeshAgreesWithGraph", (valid > 0) ? Round((float)meshMatchesGraph / valid) : 0f,
			"astarAgreesWithGraph", (valid > 0) ? Round((float)astarMatchesGraph / valid) : 0f,
			"graphMs", Summary(graphMs),
			"astarMs", Summary(astarMs),
			"astarMsFound", Summary(astarFoundMs),
			"navmeshMs", Summary(meshMs),
			"navmeshMsFound", Summary(meshFoundMs),
			"astarSteps", Summary(steps),
			"navmeshCorners", Summary(corners),
			"lengthNavmeshPerAstar", Summary(lengthRatio),
			"disagreements", disagreements);
	}

	private static object Summary(List<double> values)
	{
		if (values.Count == 0)
		{
			return null;
		}
		values.Sort();
		double sum = 0;
		foreach (double v in values)
		{
			sum += v;
		}
		return Obj("n", values.Count, "mean", Ms(sum / values.Count), "median", Ms(values[values.Count / 2]), "p90", Ms(values[(int)(values.Count * 0.9)]), "max", Ms(values[values.Count - 1]));
	}

	private static double Per(double total, long count)
	{
		return (count > 0) ? total / count : 0;
	}

	private static double Ms(double value)
	{
		return System.Math.Round(value, 3);
	}
}
#endif
