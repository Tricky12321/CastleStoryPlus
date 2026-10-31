using System.Collections.Generic;
using Brix.Engine;
using Brix.Engine.Nature;
using Brix.Game.AI;
using Brix.Game.AI.Pathfinding;
using Brix.Game.Components.Volumes;
using Brix.Pathfinding;
using Brix.Pathfinding.Test;
using CastleStoryPlus.Core;
using CastleStoryPlus.DevTools.Api;
using CastleStoryPlus.DevTools.Pathing;
using UnityEngine.Networking;
using static CastleStoryPlus.DevTools.Endpoints.Json;

namespace CastleStoryPlus.DevTools.Endpoints;

// The game's pathfinding: run a route search and compare it with the shortest route; look at the graph's nodes.
internal static class PathEndpoints
{
	internal static void Add(List<Route> routes)
	{
		routes.Add(new Route("GET", "/api/path", "Runs the game's route search (A*, with the mod's patches) for a bricktron and compares it with the shortest route over the same graph", Find)
		{
			Parameters = "to (x,y,z, required), from (x,y,z; default: where the bricktron stands), unit (bricktron id; default: the player's first builder), enemy (true: an enemy unit searches), nodes (true: list the routes' voxels), shortest (false: skip the comparison)",
			TimeoutMs = 60000
		});
		routes.Add(new Route("GET", "/api/path/straight", "Whether a bricktron may walk straight from one voxel to another (the mod's StraightPaths line test: flat route nodes, plain steps, no obstacle or unit on the line, flat ground beside it), and where and why not; with the route search's own route between them and the kinds of its steps", Straight)
		{
			Parameters = "from, to (x,y,z, required), unit (bricktron id; default: the player's first builder; its presence test is used)"
		});
		routes.Add(new Route("GET", "/api/path/node", "The navigation node at a voxel: kind, obstacles and its links (to, kind, weight)", Node)
		{
			Parameters = "at (x,y,z, required)"
		});
		routes.Add(new Route("GET", "/api/path/ground", "The highest voxel a bricktron can stand on in a column", Ground)
		{
			Parameters = "x, z (required)"
		});
		routes.Add(new Route("GET", "/api/path/occupied", "Why a unit would be pushed out of a voxel (the game's PopOuterOfObstacle): whether the voxel is open, nature blockers in it and under it, and the volumes there that are obstacles, characters, occupied or access cells, with whether the unit may pass through each", Occupied)
		{
			Parameters = "at (x,y,z, required), unit (bricktron id; default: the player's first builder)"
		});
	}

	private static void NeedGraph()
	{
		if (!NetworkServer.active)
		{
			throw new ApiException(409, "Route searches only run on the host (single player or hosting)");
		}
		if (!PathProbe.Ready())
		{
			throw new ApiException(409, "The navigation graph is not ready (no game, or still loading)");
		}
	}

	private static object Find(ApiRequest request)
	{
		NeedGraph();
		Labor labor = Units.Find(request.Int("unit", 0), request.Bool("enemy", false));
		if (labor == null)
		{
			throw new ApiException(404, "No bricktron to search for (unit id not found, or no unit of that side)");
		}
		XYZ to = XYZ.FromVector3(request.Vector("to"));
		XYZ from = (request.Get("from") != null) ? XYZ.FromVector3(request.Vector("from")) : Units.VoxelOf(labor);
		PathProbe.Report report = PathProbe.Run(labor, from, to, request.Bool("shortest", true));
		bool withNodes = request.Bool("nodes", false);
		return Obj(
			"unit", labor.name,
			"valid", report.Valid,
			"problem", report.Problem,
			"origin", Voxel(report.Origin),
			"destination", Voxel(report.Destination),
			"astar", RouteJson(report.AStar, withNodes),
			"shortest", RouteJson(report.Shortest, withNodes),
			"costRatio", Round(report.CostRatio));
	}

	private static object Straight(ApiRequest request)
	{
		NeedGraph();
		Labor labor = Units.Find(request.Int("unit", 0), false);
		XYZ from = XYZ.FromVector3(request.Vector("from"));
		XYZ to = XYZ.FromVector3(request.Vector("to"));
		string line = CastleStoryPlus.Pathfinding.StraightPaths.Explain(from, to, (labor != null && labor.presence != null) ? labor.presence.BuildPresenceTest() : null, (labor != null) ? labor.gameObject : null);
		Dictionary<string, int> kinds = new Dictionary<string, int>();
		string firstOther = null;
		if (labor != null)
		{
			PathProbe.Report report = PathProbe.Run(labor, from, to, false);
			if (report.AStar != null && report.AStar.Found)
			{
				for (int i = 1; i < report.AStar.Nodes.Count; i++)
				{
					string kind = PathNodeRegistry.PermissionVers(report.AStar.Nodes[i - 1], report.AStar.Nodes[i]).ToString();
					kinds[kind] = kinds.TryGetValue(kind, out int n) ? n + 1 : 1;
					if (firstOther == null && kind != "Full")
					{
						firstOther = report.AStar.Nodes[i] + " (" + kind + ")";
					}
				}
			}
		}
		return Obj("line", line, "routeSteps", kinds, "firstStepNotPlain", firstOther);
	}

	private static object RouteJson(PathProbe.Route route, bool withNodes)
	{
		if (route == null)
		{
			return null;
		}
		List<int[]> nodes = null;
		if (withNodes)
		{
			nodes = new List<int[]>();
			foreach (XYZ node in route.Nodes)
			{
				nodes.Add(Voxel(node));
			}
		}
		return Obj("found", route.Found, "cost", route.Cost, "steps", route.Steps, "searched", route.Searched, "ms", System.Math.Round(route.Milliseconds, 1), "nodes", nodes);
	}

	private static object Node(ApiRequest request)
	{
		NeedGraph();
		XYZ at = XYZ.FromVector3(request.Vector("at"));
		PathNode node = TestPathfinding.Instance.terrainLod.GetPathNode(at);
		List<object> links = new List<object>();
		if (node is TerrainPathNode terrain && terrain.siblings != null)
		{
			foreach (PathBranch branch in terrain.siblings)
			{
				links.Add(Obj("to", Voxel(branch.Node.RepresentativePosition), "kind", branch.Type.ToString(), "weight", branch.Weight, "toKind", branch.Node.Type.ToString()));
			}
		}
		return Obj(
			"at", Voxel(at),
			"kind", node.Type.ToString(),
			"lod", node.Lod,
			"obstacles", node.ObstacleCount,
			"blockFull", Brix.Engine.Voxel.IsFull(at),
			"links", links);
	}

	private static object Ground(ApiRequest request)
	{
		NeedGraph();
		int x = request.Int("x", int.MinValue);
		int z = request.Int("z", int.MinValue);
		if (x == int.MinValue || z == int.MinValue)
		{
			throw new ApiException(400, "'x' and 'z' are required");
		}
		bool found = PathProbe.Ground(x, z, out XYZ voxel);
		return Obj("found", found, "voxel", found ? Voxel(voxel) : null);
	}

	private static object Occupied(ApiRequest request)
	{
		NeedGraph();
		XYZ at = XYZ.FromVector3(request.Vector("at"));
		Labor labor = Units.Find(request.Int("unit", 0), false);
		UnityEngine.GameObject unit = (labor != null) ? labor.gameObject : null;
		List<object> volumes = new List<object>();
		foreach (VProperty property in new[] { VProperty.obstacle, VProperty.character, VProperty.occupy, VProperty.access })
		{
			foreach (IVolume volume in VolumeRegistry.VolumesWithPropertyAt(at, property))
			{
				if (volume == null)
				{
					continue;
				}
				UnityEngine.GameObject go = volume.GameObject;
				bool? passes = (unit != null && volume.PassThrough != null) ? volume.PassThrough.CanPassThrough(unit) : (bool?)null;
				volumes.Add(Obj("property", property.ToString(), "object", (go != null) ? go.name : null, "id", (go != null) ? go.InstanceId() : 0, "position", (go != null) ? Vector(go.transform.position) : null, "self", go == unit, "passThrough", passes));
			}
		}
		UnityEngine.Vector3 centre = at.ToVector3();
		return Obj(
			"at", Voxel(at),
			"unit", (labor != null) ? labor.name : null,
			"open", Brix.Engine.Voxel.IsOpen(at),
			"nonEmpty", Brix.Engine.Voxel.IsNonEmpty(at),
			"natureBlocker", NatureRegistry.HasNatureBlockerAt(at),
			"natureBlockerBelow", NatureRegistry.HasNatureBlockerAt(at - XYZ.up),
			"occupiedOrInsideGround", (unit != null) ? FloorPicker.IsOccupiedOrInsideGround(unit, centre, avoidCharacterCollisions: false) : (bool?)null,
			"volumes", volumes);
	}

	private static double[] Vector(UnityEngine.Vector3 v)
	{
		return new double[3] { System.Math.Round(v.x, 2), System.Math.Round(v.y, 2), System.Math.Round(v.z, 2) };
	}

	internal static int[] Voxel(XYZ p)
	{
		return new int[3] { p.x, p.y, p.z };
	}
}
