using CastleStoryPlus.Core;
using System.Collections.Generic;
using Brix.External.Factories;
using Brix.Game.AI;
using Brix.Game.AI.KnowledgeSpace;
using Brix.Game.Components;
using Brix.Pathfinding;
using CastleStoryPlus.DevTools.Api;
using CastleStoryPlus.DevTools.Pathing;
using CastleStoryPlus.WorkerAI;
using UnityEngine;
using UnityEngine.Networking;
using static CastleStoryPlus.DevTools.Endpoints.Json;

namespace CastleStoryPlus.DevTools.Endpoints;

// Why a builder finds no blueprint to work on: runs the build project's own search for a worker at once (the
// resources it believes it can fetch, the flood over the route graph from where it stands to the blueprints' access
// cells) and tells for each blueprint of the project whether the search found it, whether the workers' reachability
// map says it can be reached and whether it takes one of the fetchable resources.
internal static class BuildSearchEndpoints
{
	// The flood stops on its own when the graph around the worker is used up; this only guards against a runaway.
	private const int MaxSteps = 100000;

	internal static void Add(List<Route> routes)
	{
		routes.Add(new Route("GET", "/api/buildsearch", "Runs a build project's search for blueprints for a worker at once: the resources it believes it can fetch, where the search starts, how many route nodes it floods, which blueprints it finds, and per blueprint of the project why it is (not) found", Search)
		{
			Parameters = "unit (bricktron id; default: the player's first builder), project (build project id; default: the worker's own project, else the first build project)",
			TimeoutMs = 60000
		});
	}

	private static object Search(ApiRequest request)
	{
		if (!NetworkServer.active)
		{
			throw new ApiException(409, "Only on the host");
		}
		Labor labor = Units.Find(request.Int("unit", 0), false);
		if (labor == null)
		{
			throw new ApiException(404, "No bricktron");
		}
		BuildProject project = FindProject(request.Int("project", 0), labor);
		if (project == null)
		{
			throw new ApiException(404, "No build project");
		}
		BuildGoalProvider provider = project.GetComponent<BuildGoalProvider>();
		if (provider == null)
		{
			throw new ApiException(409, "The build project has no blueprint list");
		}
		return Explain(labor, project);
	}

	// The build project's search for the worker, run at once, and why each of its blueprints is (not) found.
	internal static object Explain(Labor labor, BuildProject project)
	{
		BuildGoalProvider provider = project.GetComponent<BuildGoalProvider>();
		if (provider == null)
		{
			return Obj("error", "The build project has no blueprint list");
		}

		HashSet<Factory.AssetKey> fetchables = new HashSet<Factory.AssetKey>();
		bool carrying = !labor.recepteur.IsEmpty();
		if (carrying)
		{
			labor.recepteur.UniqueStoredItemsKeys(fetchables);
		}
		else
		{
			Knowledge.Instance.UniqueResourcesReacheableFrom(labor.gameObject, fetchables);
		}
		List<string> fetchableNames = new List<string>();
		foreach (Factory.AssetKey key in fetchables)
		{
			fetchableNames.Add((key != null) ? key.Name : "null");
		}
		int regions = 0;
		foreach (KnowledgeRegion region in Knowledge.Instance.RegionsOf(labor.gameObject))
		{
			if (region != null)
			{
				regions++;
			}
		}

		PathNode start = labor.presence.CurrentPathNode;
		BuildGoalFinder finder = new BuildGoalFinder();
		finder.PrepareStep(labor, fetchables, project.goalParent.transform);
		int steps = 0;
		bool finished = false;
		while (steps < MaxSteps)
		{
			steps++;
			if (finder.SearchStep())
			{
				finished = true;
				break;
			}
		}
		int searched = finder.FindAllSearched;
		int flooded = finder.FindAllDictionary.Count;
		int volumesSeen = finder.VisitedVolumes.Count;
		HashSet<BuildGoal> found = new HashSet<BuildGoal>(finder.Results);
		finder.ResultStep();
		finder.Destroy();

		List<object> blueprints = new List<object>();
		Dictionary<string, int> summary = new Dictionary<string, int>();
		foreach (Blueprint blueprint in provider.TrackedBlueprints)
		{
			if (blueprint == null || blueprint.Released)
			{
				continue;
			}
			BuildGoal goal = blueprint.GetComponent<BuildGoal>();
			if (goal == null)
			{
				continue;
			}
			bool buildable = blueprint.State.HasFlag(PlacementFlags.valid) && blueprint.State.HasFlag(PlacementFlags.buildable) && goal.Commited;
			bool reach = Knowledge.Instance.CanReach(labor, goal);
			bool takes = goal.IsAnti || (goal.recepteur != null && goal.recepteur.HasBaseCapacityForOneOf(fetchables));
			string why = !buildable ? "not buildable (waiting for support, not valid or not committed)"
				: found.Contains(goal) ? "found"
				: !takes ? "takes none of the fetchable resources"
				: !reach ? "out of reach (reachability map)"
				: "not reached by the search's flood";
			summary.TryGetValue(why, out int n);
			summary[why] = n + 1;
			blueprints.Add(Obj(
				"id", blueprint.gameObject.InstanceId(),
				"type", blueprint.name,
				"position", Vec(blueprint.transform.position),
				"demolish", goal.IsAnti,
				"why", why,
				"reachableByMap", reach,
				"takesFetchable", takes,
				"unreachableMark", UnreachableTasks.IsMarked(goal)));
		}

		return Obj(
			"unit", labor.name,
			"at", Vec(labor.transform.position),
			"project", project.name + " " + project.gameObject.InstanceId(),
			"carrying", carrying,
			"fetchables", fetchableNames,
			"reachRegions", regions,
			"start", (start == null || start.IsEmpty) ? null : Obj(
				"at", Cell(start.RepresentativePosition),
				"lod", start.Lod,
				"obstacles", start.ObstacleCount,
				"full", start.IsFull,
				"accesses", start.AccessCount),
			"flood", Obj(
				"finished", finished,
				"steps", steps,
				"searched", searched,
				"nodes", flooded,
				"volumesSeen", volumesSeen),
			"found", found.Count,
			"summary", summary,
			"blueprints", blueprints);
	}

	private static BuildProject FindProject(int id, Labor labor)
	{
		if (id == 0 && labor.Project is BuildProject own)
		{
			return own;
		}
		foreach (BuildProject project in Object.FindObjectsOfType<BuildProject>())
		{
			if (project == null || !project.IsStillAlive)
			{
				continue;
			}
			if (id == 0 || project.gameObject.InstanceId() == id)
			{
				return project;
			}
		}
		return null;
	}

	private static int[] Cell(Brix.Engine.XYZ cell)
	{
		return new int[3] { cell.x, cell.y, cell.z };
	}
}
