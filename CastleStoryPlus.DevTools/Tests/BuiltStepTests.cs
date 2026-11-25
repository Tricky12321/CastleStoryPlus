#if UNITY6
using System;
using System.Collections;
using System.Collections.Generic;
using Brix.Engine;
using Brix.Game;
using Brix.Game.AI;
using Brix.Pathfinding;
using Brix.Pathfinding.Test;
using CastleStoryPlus.DevTools.Pathing;
using CastleStoryPlus.DevTools.Testing;
using CastleStoryPlus.Pathfinding;
using UnityEngine;

namespace CastleStoryPlus.DevTools.Tests;

// Steps the player builds count as what climbing them takes (#200, point 4; StepCosts): a block placed on the ground
// has a climb onto it and a drop off it at the configured weights, and a low wall of placed blocks is stepped over
// rather than walked round when going round is the longer way, by the route search and by a builder's walk.
internal static class BuiltStepTests
{
	// The wall: 13 blocks long (x -6 to +6 round the site's middle), 1 high; start and goal 3 in front and behind.
	private const int WallHalf = 6;

	private const int Away = 3;

	private sealed class Site
	{
		public Labor Labor;

		public XYZ Center;

		public XYZ From;

		public XYZ To;
	}

	private static IEnumerator BuildWall(TestContext t, Site site)
	{
		XYZ center = Scenario.Site(t, 17, 11, 4);
		List<XYZ> wall = new List<XYZ>();
		for (int dx = -WallHalf; dx <= WallHalf; dx++)
		{
			wall.Add(new XYZ(center.x + dx, center.y, center.z));
		}
		site.Center = center;
		site.From = new XYZ(center.x, center.y, center.z - Away);
		site.To = new XYZ(center.x, center.y, center.z + Away);
		site.Labor = Scenario.Bricktron(t, Occupation.Type.Builder, site.From);
		TestWorld.PlaceBlocks(t, wall);
		yield return TestWorld.WaitForGraph(t);
		// A unit spawned now has its route search set up a few frames later.
		yield return t.Frames(10);
		if (!PathProbe.Ground(center.x, center.z, out XYZ top) || top.y != center.y + 1)
		{
			t.Skip("The wall was not built where it should be (the ground there is at " + top + ")");
		}
		t.Log("Wall across " + center + ", " + site.Labor.name + " at " + site.From + ", goal " + site.To);
	}

	// Over the wall: 2 x (Away - 1) flat steps, a climb and a drop; round its end: (WallHalf + 1) out, 2 x Away across
	// and back, all flat. Skipped when the settings make going over the longer way.
	private static void SkipUnlessOverIsShorter(TestContext t)
	{
		int climb = StepCostRules.Weight(-6, StepCosts.ClimbPenalty(), StepCosts.StepDownPenalty());
		int drop = StepCostRules.Weight(6, StepCosts.ClimbPenalty(), StepCosts.StepDownPenalty());
		int over = 2 * (Away - 1) * StepCostRules.FlatWeight + climb + drop;
		int round = (2 * (WallHalf + 1) + 2 * Away) * StepCostRules.FlatWeight;
		t.Record("overWeight", over);
		t.Record("roundWeight", round);
		if (over >= round)
		{
			t.Skip("With [Pathfinding] ClimbCost " + StepCosts.ClimbPenalty() + " and StepDownCost " + StepCosts.StepDownPenalty() + " going over the wall (" + over + ") is not cheaper than round it (" + round + ")");
		}
	}

	private static bool OnWall(XYZ voxel, XYZ center)
	{
		return voxel.z == center.z && voxel.y == center.y + 1 && Mathf.Abs(voxel.x - center.x) <= WallHalf;
	}

	[GameTest("Pathfinding", "A block placed on flat ground: a climb onto it and a drop off it, weighed by [Pathfinding] ClimbCost and StepDownCost (#200)", TimeoutSeconds = 60f)]
	private static IEnumerator PlacedBlockHasClimbAndDrop(TestContext t)
	{
		Site site = new Site();
		yield return BuildWall(t, site);
		TestTerrainLod lod = TestPathfinding.Instance.terrainLod;
		XYZ below = new XYZ(site.Center.x, site.Center.y, site.Center.z - 1);
		XYZ top = new XYZ(site.Center.x, site.Center.y + 1, site.Center.z);
		TerrainPathNode lower = lod.GetPathNode(below) as TerrainPathNode;
		TerrainPathNode upper = lod.GetPathNode(top) as TerrainPathNode;
		t.True(lower != null && !lower.IsEmpty, "No place of the graph in front of the wall at " + below);
		t.True(upper != null && !upper.IsEmpty, "No place of the graph on top of the wall at " + top);
		PathBranch up = Branch(lower, upper);
		PathBranch down = Branch(upper, lower);
		t.Record("climbWeight", up.Weight);
		t.Record("dropWeight", down.Weight);
		t.True(up.Type == PathBranch.BranchType.Climbing, "The step onto the block is a " + up.Type + ", not a climb");
		t.True(down.Type == PathBranch.BranchType.StepDown, "The step off the block is a " + down.Type + ", not a drop");
		t.Equal(StepCostRules.FlatWeight + StepCosts.ClimbPenalty(), up.Weight, "Weight of the climb onto the block");
		t.Equal(StepCostRules.FlatWeight + StepCosts.StepDownPenalty(), down.Weight, "Weight of the drop off the block");
	}

	private static PathBranch Branch(TerrainPathNode from, TerrainPathNode to)
	{
		if (from != null && from.siblings != null)
		{
			foreach (PathBranch branch in from.siblings)
			{
				if (branch.Node == to)
				{
					return branch;
				}
			}
		}
		return PathBranch.Empty;
	}

	[GameTest("Pathfinding", "A low wall of placed blocks between start and goal: the route search steps over it instead of walking round its end (#200)", TimeoutSeconds = 60f)]
	private static IEnumerator RouteStepsOverLowWall(TestContext t)
	{
		Site site = new Site();
		yield return BuildWall(t, site);
		SkipUnlessOverIsShorter(t);
		PathProbe.Report report = PathProbe.Run(site.Labor, site.From, site.To, true);
		t.True(report.Valid, "Start or goal not walkable: " + report.Problem);
		t.True(report.Shortest.Found, "The reference search found no route");
		t.True(report.AStar.Found, "The route search found no route");
		t.Record("astarCost", report.AStar.Cost);
		t.Record("astarSteps", report.AStar.Steps);
		t.Record("shortestCost", report.Shortest.Cost);
		t.True(report.Shortest.Nodes.Exists((XYZ node) => OnWall(node, site.Center)), "The shortest route goes round the wall (" + report.Shortest.Steps + " steps), not over it");
		t.True(report.AStar.Nodes.Exists((XYZ node) => OnWall(node, site.Center)), "The route goes round the wall (" + report.AStar.Steps + " steps), not over it");
		t.AtMost(2 * Away + 2, report.AStar.Steps, "Steps of the route over the wall");
	}

	[GameTest("Pathfinding", "A builder sent across a low wall of placed blocks climbs over it instead of walking round its end (#200)", TimeoutSeconds = 120f)]
	private static IEnumerator BuilderStepsOverLowWall(TestContext t)
	{
		Site site = new Site();
		yield return BuildWall(t, site);
		SkipUnlessOverIsShorter(t);
#if UNITY6_PLAYER
		// The navigation mesh (NavMeshRoutes) routes the player's units: it must have taken the wall in.
		yield return t.Until(() => !NavMeshExperiment.Busy, 10f, "the navigation mesh to take in the wall");
#endif
		Labor labor = site.Labor;
		labor.Autonomy = AutonomyStatus.WaitForOrders;
		labor.AssignToIdle();
		Order.Package.OfLocation(Order.Move.RunToward, Location.Of(site.To), Labor.DecisionSource.User).Send(labor);
		bool onWall = false;
		int furthest = 0;
		float started = Time.time;
		yield return t.UntilGame(() =>
		{
			if (labor == null)
			{
				return true;
			}
			XYZ voxel = Units.VoxelOf(labor);
			onWall |= OnWall(voxel, site.Center);
			furthest = Math.Max(furthest, Mathf.Abs(voxel.x - site.Center.x));
			return voxel == site.To && labor.navigation.Locomotion.InPlace;
		}, 40f, labor.name + " to reach " + site.To);
		t.NotNull(labor, "The builder");
		t.Record("seconds", Math.Round(Time.time - started, 1));
		t.Record("furthestSideways", furthest);
		t.True(onWall, labor.name + " did not step over the wall (it went " + furthest + " blocks sideways)");
		t.AtMost(WallHalf, furthest, "Blocks the builder went sideways (round the wall's end is " + (WallHalf + 1) + ")");
	}
}
#endif
