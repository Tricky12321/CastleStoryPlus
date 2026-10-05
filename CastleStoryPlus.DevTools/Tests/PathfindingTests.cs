using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using Brix.Engine;
using Brix.Game.AI;
using CastleStoryPlus.DevTools.Pathing;
using CastleStoryPlus.DevTools.Testing;
using UnityEngine;

namespace CastleStoryPlus.DevTools.Tests;

// The game's route search, with the mod's patches (HeuristicPatch, KeepImprovedPathPatch, StairHeadroom, ...),
// checked in the game that is running: routes must be found when one exists and be close to the shortest one.
internal static class PathfindingTests
{
	// A route may cost this much more than the shortest one (the A* trades some length for speed).
	private const float MaxCostRatio = 1.3f;

	// On average over many routes, no more than this.
	private const float MaxAverageCostRatio = 1.1f;

	private const int RandomRoutes = 40;

	// How far from the builder the random routes start and end.
	private const int RandomRadius = 30;

	private static Labor Builder(TestContext t)
	{
		Labor labor = Units.Find(0, false);
		if (labor == null)
		{
			t.Skip("The player has no bricktron to search routes for");
		}
		return labor;
	}

	[GameTest("Pathfinding", "Random routes around a builder: found whenever one exists, and close to the shortest route")]
	private static IEnumerator RandomRoutesNearShortest(TestContext t)
	{
		Labor labor = Builder(t);
		XYZ home = Units.VoxelOf(labor);
		System.Random random = new System.Random(20261005);
		int compared = 0;
		int missed = 0;
		float worst = 0f;
		float sum = 0f;
		string worstRoute = null;
		for (int i = 0; i < RandomRoutes; i++)
		{
			if (!RandomGround(random, home, out XYZ from) || !RandomGround(random, home, out XYZ to))
			{
				continue;
			}
			PathProbe.Report report = PathProbe.Run(labor, from, to, true);
			if (!report.Valid || !report.Shortest.Found)
			{
				continue;
			}
			if (!report.AStar.Found)
			{
				missed++;
				t.Log("No route found from " + from + " to " + to + ", though the shortest is " + report.Shortest.Steps + " steps");
				continue;
			}
			compared++;
			sum += report.CostRatio;
			if (report.CostRatio > worst)
			{
				worst = report.CostRatio;
				worstRoute = from + " -> " + to + " (cost " + report.AStar.Cost + " vs " + report.Shortest.Cost + ", " + report.AStar.Steps + " vs " + report.Shortest.Steps + " steps)";
			}
			// One search per frame, so the game keeps running.
			yield return null;
		}
		t.Record("compared", compared);
		t.Record("missed", missed);
		t.Record("worstRatio", Math.Round(worst, 3));
		t.Record("averageRatio", (compared > 0) ? Math.Round(sum / compared, 3) : 0);
		t.Record("worstRoute", worstRoute);
		t.True(compared >= 5, "Too few routes to compare (" + compared + "): no open ground around the builder?");
		t.Equal(0, missed, "Routes the search did not find, though they exist");
		t.AtMost(MaxCostRatio, worst, "Worst route cost / shortest (" + worstRoute + ")");
		t.AtMost(MaxAverageCostRatio, sum / compared, "Average route cost / shortest");
	}

	[GameTest("Pathfinding", "A wall between start and goal: the route goes round its end, close to the shortest way", TimeoutSeconds = 60f)]
	private static IEnumerator DetourAroundWall(TestContext t)
	{
		Labor labor = Builder(t);
		if (!TestWorld.FindFlatArea(Units.VoxelOf(labor), 15, 11, 4, 40, out XYZ center))
		{
			t.Skip("No flat open area of 15 x 11 near the builder");
		}
		t.Log("Flat area at " + center);
		// A wall 9 wide and 3 high across the middle; start and goal 4 in front and behind it.
		List<XYZ> wall = new List<XYZ>();
		for (int dx = -4; dx <= 4; dx++)
		{
			for (int dy = 0; dy < 3; dy++)
			{
				wall.Add(new XYZ(center.x + dx, center.y + dy, center.z));
			}
		}
		TestWorld.PlaceBlocks(t, wall);
		yield return TestWorld.WaitForGraph(t);
		XYZ from = new XYZ(center.x, center.y, center.z - 4);
		XYZ to = new XYZ(center.x, center.y, center.z + 4);
		PathProbe.Report report = PathProbe.Run(labor, from, to, true);
		RecordReport(t, report);
		t.True(report.Valid, "Start or goal not walkable: " + report.Problem);
		t.True(report.Shortest.Found, "The reference search found no way round the wall");
		t.True(report.AStar.Found, "The route search found no way round the wall");
		foreach (XYZ node in report.AStar.Nodes)
		{
			t.True(node.z != center.z || Mathf.Abs(node.x - center.x) > 4, "The route goes through the wall at " + node);
		}
		t.AtMost(MaxCostRatio, report.CostRatio, "Route cost / shortest");
	}

	[GameTest("Pathfinding", "A builder ordered to a spot 10 voxels away walks there", TimeoutSeconds = 90f)]
	private static IEnumerator BuilderWalksToSpot(TestContext t)
	{
		Labor labor = Builder(t);
		XYZ start = Units.VoxelOf(labor);
		if (!TestWorld.FindFlatArea(start + new XYZ(10, 0, 0), 3, 3, 2, 6, out XYZ goal))
		{
			t.Skip("No open ground about 10 voxels from the builder");
		}
		PathProbe.Report plan = PathProbe.Run(labor, start, goal, false);
		if (!plan.Valid || !plan.AStar.Found)
		{
			t.Skip("No route from the builder to " + goal + " (" + plan.Problem + ")");
		}
		t.Log("Walking " + labor.name + " from " + start + " to " + goal + " (" + plan.AStar.Steps + " steps)");
		labor.AssignToIdle();
		Order.Package.OfLocation(Order.Move.RunToward, Location.Of(goal), Labor.DecisionSource.User).Send(labor);
		float started = Time.time;
		yield return t.Until(() => labor == null || XYZ.ManhattanMagnitude(Units.VoxelOf(labor) - goal) <= 1, 60f, labor.name + " to reach " + goal);
		t.NotNull(labor, "The builder");
		t.Record("seconds", Math.Round(Time.time - started, 1));
		t.Record("steps", plan.AStar.Steps);
	}

	[GameTest("Pathfinding", "Random routes for an enemy unit (their own weight and budget): found and close to the shortest")]
	private static IEnumerator EnemyRoutesNearShortest(TestContext t)
	{
		Labor enemy = Units.Find(0, true);
		Labor builder = Builder(t);
		if (enemy == null)
		{
			t.Skip("No enemy unit in the game");
		}
		XYZ home = Units.VoxelOf(builder);
		System.Random random = new System.Random(5102026);
		int compared = 0;
		float worst = 0f;
		for (int i = 0; i < RandomRoutes / 2; i++)
		{
			if (!RandomGround(random, home, out XYZ from) || !RandomGround(random, home, out XYZ to))
			{
				continue;
			}
			PathProbe.Report report = PathProbe.Run(enemy, from, to, true);
			if (!report.Valid || !report.Shortest.Found)
			{
				continue;
			}
			t.True(report.AStar.Found, "An enemy found no route from " + from + " to " + to + ", though one exists");
			compared++;
			worst = Mathf.Max(worst, report.CostRatio);
			yield return null;
		}
		t.Record("compared", compared);
		t.Record("worstRatio", Math.Round(worst, 3));
		if (compared == 0)
		{
			t.Skip("No routes to compare");
		}
		t.AtMost(MaxCostRatio, worst, "Worst enemy route cost / shortest");
	}

	private static bool RandomGround(System.Random random, XYZ around, out XYZ voxel)
	{
		for (int attempt = 0; attempt < 10; attempt++)
		{
			int x = around.x + random.Next(-RandomRadius, RandomRadius + 1);
			int z = around.z + random.Next(-RandomRadius, RandomRadius + 1);
			if (PathProbe.Ground(x, z, out voxel))
			{
				return true;
			}
		}
		voxel = XYZ.zero;
		return false;
	}

	private static void RecordReport(TestContext t, PathProbe.Report report)
	{
		if (report.AStar != null)
		{
			t.Record("astarCost", report.AStar.Cost);
			t.Record("astarSteps", report.AStar.Steps);
			t.Record("astarSearched", report.AStar.Searched);
		}
		if (report.Shortest != null)
		{
			t.Record("shortestCost", report.Shortest.Cost);
			t.Record("shortestSteps", report.Shortest.Steps);
		}
		t.Record("costRatio", Math.Round(report.CostRatio, 3).ToString(CultureInfo.InvariantCulture));
	}
}
