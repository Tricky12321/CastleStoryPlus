using System.Collections;
using System.Collections.Generic;
using Brix.Engine;
using Brix.Engine.Blocks;
using Brix.Game.AI;
using Brix.Utils;
using Brix.Components;
using CastleStoryPlus.DevTools.Pathing;
using CastleStoryPlus.DevTools.Testing;
using UnityEngine;

namespace CastleStoryPlus.DevTools.Tests;

// What the route search must refuse as well as find: a ring of stone round one column shuts it off (even one 3 high:
// stone walls are not climbed; the tests use 12, above TestPathfinding.MaxClimbUpLevelDifference, to be sure); with
// a gap, or once a part of it is taken down, the way is open again. Every test that expects no route first shows
// that the goal could be reached before the wall. Goals in the air or inside the ground are
// not walkable at all. Each search is run for one of the test's own builders, with the reference search
// (a Dijkstra over the same graph) as the second opinion.
[ParallelTests]
internal static class RouteLimitTests
{
	// Higher than a bricktron can climb.
	private const int TallWall = 12;

	[GameTest("Pathfinding", "A spot shut in by a ring of stone 12 high (higher than a bricktron climbs): no route into it", TimeoutSeconds = 60f)]
	private static IEnumerator NoRouteIntoWalledSpot(TestContext t)
	{
		XYZ site = Scenario.Site(t, 9, 9, TallWall + 2);
		Labor builder = Scenario.Bricktron(t, Occupation.Type.Builder, new XYZ(site.x - 4, site.y, site.z));
		yield return Reachable(t, builder, site);
		TestWorld.PlaceBlocks(t, Ring(site, TallWall, false));
		yield return TestWorld.WaitForGraph(t);
		PathProbe.Report report = PathProbe.Run(builder, Units.VoxelOf(builder), site, true);
		Record(t, report);
		NoRoute(t, report, "into the walled spot", site);
	}

	[GameTest("Pathfinding", "From inside a ring of stone 12 high there is no route out", TimeoutSeconds = 60f)]
	private static IEnumerator NoRouteOutOfWalledSpot(TestContext t)
	{
		XYZ site = Scenario.Site(t, 9, 9, TallWall + 2);
		Labor builder = Scenario.Bricktron(t, Occupation.Type.Builder, new XYZ(site.x - 4, site.y, site.z));
		yield return Reachable(t, builder, site);
		TestWorld.PlaceBlocks(t, Ring(site, TallWall, false));
		yield return TestWorld.WaitForGraph(t);
		PathProbe.Report report = PathProbe.Run(builder, site, new XYZ(site.x + 4, site.y, site.z), true);
		Record(t, report);
		NoRoute(t, report, "out of the walled spot", new XYZ(site.x + 4, site.y, site.z));
	}

	[GameTest("Pathfinding", "The same ring 12 high with a gap one column wide: the route goes in through the gap", TimeoutSeconds = 60f)]
	private static IEnumerator RouteThroughGapInTallWall(TestContext t)
	{
		XYZ site = Scenario.Site(t, 9, 9, TallWall + 2);
		Labor builder = Scenario.Bricktron(t, Occupation.Type.Builder, new XYZ(site.x - 4, site.y, site.z));
		yield return Reachable(t, builder, site);
		TestWorld.PlaceBlocks(t, Ring(site, TallWall, true));
		yield return TestWorld.WaitForGraph(t);
		PathProbe.Report report = null;
		Route(t, builder, site, r => report = r);
		XYZ gap = Gap(site);
		t.True(report.AStar.Nodes.Exists(n => n.x == gap.x && n.z == gap.z), "The route does not pass the gap at " + gap);
	}

	[GameTest("Pathfinding", "A walled-in spot opens up when one column of the ring is taken down: no route first, then one", TimeoutSeconds = 90f)]
	private static IEnumerator RouteOpensWhenWallRemoved(TestContext t)
	{
		XYZ site = Scenario.Site(t, 9, 9, TallWall + 2);
		Labor builder = Scenario.Bricktron(t, Occupation.Type.Builder, new XYZ(site.x - 4, site.y, site.z));
		yield return Reachable(t, builder, site);
		TestWorld.PlaceBlocks(t, Ring(site, TallWall, false));
		yield return TestWorld.WaitForGraph(t);
		PathProbe.Report closed = PathProbe.Run(builder, Units.VoxelOf(builder), site, true);
		NoRoute(t, closed, "into the closed ring", site);
		// Top down, so no block is left without support on the way.
		XYZ gap = Gap(site);
		BlockEngine engine = BrixSingleton<BlockEngine>.Instance;
		for (int y = TallWall - 1; y >= 0; y--)
		{
			IBlockData block = engine.GetBlockData(new XYZ(gap.x, site.y + y, gap.z));
			if (block != null)
			{
				block.RootOrSelf.DeBlock();
			}
		}
		yield return t.Until(() => !engine.IsBlock(new XYZ(gap.x, site.y, gap.z)), 5f, "the column to be taken down");
		yield return TestWorld.WaitForGraph(t);
		Route(t, builder, site, _ => { });
	}

	[GameTest("Pathfinding", "A ring of stone only 3 high already shuts a spot off: stone walls are not climbed", TimeoutSeconds = 60f)]
	private static IEnumerator LowWallIsNotClimbed(TestContext t)
	{
		XYZ site = Scenario.Site(t, 9, 9, 6);
		Labor builder = Scenario.Bricktron(t, Occupation.Type.Builder, new XYZ(site.x - 4, site.y, site.z));
		yield return Reachable(t, builder, site);
		TestWorld.PlaceBlocks(t, Ring(site, 3, false));
		yield return TestWorld.WaitForGraph(t);
		PathProbe.Report report = PathProbe.Run(builder, Units.VoxelOf(builder), site, true);
		Record(t, report);
		NoRoute(t, report, "over a wall 3 high", site);
	}

	[GameTest("Pathfinding", "A step of one block: the route goes up onto it", TimeoutSeconds = 60f)]
	private static IEnumerator RouteUpOneStep(TestContext t)
	{
		XYZ site = Scenario.Site(t, 9, 5, 5);
		Labor builder = Scenario.Bricktron(t, Occupation.Type.Builder, new XYZ(site.x - 4, site.y, site.z));
		List<XYZ> platform = new List<XYZ>();
		for (int dx = 0; dx <= 2; dx++)
		{
			for (int dz = -1; dz <= 1; dz++)
			{
				platform.Add(new XYZ(site.x + dx, site.y, site.z + dz));
			}
		}
		TestWorld.PlaceBlocks(t, platform);
		yield return TestWorld.WaitForGraph(t);
		XYZ top = new XYZ(site.x + 1, site.y + 1, site.z);
		Route(t, builder, top, _ => { });
	}

	[GameTest("Pathfinding", "A goal in the air (3 voxels above the ground): the route ends on the ground under it, never in the air", TimeoutSeconds = 30f)]
	private static IEnumerator GoalInTheAirEndsOnGround(TestContext t)
	{
		XYZ site = Scenario.Site(t, 5, 5, 6);
		Labor builder = Scenario.Bricktron(t, Occupation.Type.Builder, new XYZ(site.x - 2, site.y, site.z));
		yield return t.Frames(5);
		XYZ air = new XYZ(site.x, site.y + 3, site.z);
		PathProbe.Report report = PathProbe.Run(builder, Units.VoxelOf(builder), air, true);
		Record(t, report);
		EndsOnOpenGround(t, report, air);
	}

	[GameTest("Pathfinding", "A goal inside the ground (a solid voxel): the route ends on open ground, never inside it", TimeoutSeconds = 30f)]
	private static IEnumerator GoalInsideGroundEndsOnSurface(TestContext t)
	{
		XYZ site = Scenario.Site(t, 5, 5, 3);
		Labor builder = Scenario.Bricktron(t, Occupation.Type.Builder, new XYZ(site.x - 2, site.y, site.z));
		yield return t.Frames(5);
		XYZ solid = new XYZ(site.x, site.y - 2, site.z);
		t.True(Voxel.IsFull(solid), "The goal voxel " + solid + " is not solid ground");
		PathProbe.Report report = PathProbe.Run(builder, Units.VoxelOf(builder), solid, true);
		Record(t, report);
		EndsOnOpenGround(t, report, solid);
	}

	[GameTest("Pathfinding", "A builder ordered into a spot walled in 12 high never gets there", TimeoutSeconds = 90f)]
	private static IEnumerator OrderIntoWalledSpotNeverArrives(TestContext t)
	{
		XYZ site = Scenario.Site(t, 9, 9, TallWall + 2);
		Labor builder = Scenario.Bricktron(t, Occupation.Type.Builder, new XYZ(site.x - 4, site.y, site.z));
		yield return Reachable(t, builder, site);
		TestWorld.PlaceBlocks(t, Ring(site, TallWall, false));
		yield return TestWorld.WaitForGraph(t);
		Order.Package.OfLocation(Order.Move.RunToward, Location.Of(site), Labor.DecisionSource.User).Send(builder);
		yield return t.Seconds(40f);
		XYZ at = Units.VoxelOf(builder);
		t.Record("endedAt", at.ToString());
		t.True(Mathf.Abs(at.x - site.x) > 0 || Mathf.Abs(at.z - site.z) > 0, "The builder got into the walled spot");
		t.True(at.y < site.y + 3, "The builder is up on the wall at " + at);
	}

	[GameTest("Pathfinding", "A builder ordered into the 12 high ring through its gap walks in", TimeoutSeconds = 120f)]
	private static IEnumerator BuilderWalksInThroughGap(TestContext t)
	{
		XYZ site = Scenario.Site(t, 9, 9, TallWall + 2);
		Labor builder = Scenario.Bricktron(t, Occupation.Type.Builder, new XYZ(site.x - 4, site.y, site.z));
		yield return Reachable(t, builder, site);
		TestWorld.PlaceBlocks(t, Ring(site, TallWall, true));
		yield return TestWorld.WaitForGraph(t);
		Order.Package.OfLocation(Order.Move.RunToward, Location.Of(site), Labor.DecisionSource.User).Send(builder);
		yield return t.UntilGame(() => Units.VoxelOf(builder) == site, 60f, builder.name + " to walk in through the gap", 15f, () => t.Log(builder.name + " at " + Units.VoxelOf(builder) + ", " + builder.Activity));
	}

	// ---- helpers

	// The game's route search for a route it must find, once the graph has taken in the change.
	private static void Route(TestContext t, Labor builder, XYZ goal, System.Action<PathProbe.Report> found)
	{
		PathProbe.Report report = PathProbe.Run(builder, Units.VoxelOf(builder), goal, true);
		Record(t, report);
		t.True(report.Valid && report.AStar.Found, "The route search found no route to " + goal + " (the reference search " + ((report.Shortest != null && report.Shortest.Found) ? "found one" : "found none too") + ") " + report.Problem);
		found(report);
	}

	// The positive control of a test that expects no route: before anything is built the goal can be reached
	// (by the reference search over the graph), so a missing route afterwards is the wall's doing.
	private static IEnumerator Reachable(TestContext t, Labor builder, XYZ goal)
	{
		yield return t.Frames(5);
		PathProbe.Report open = PathProbe.Run(builder, Units.VoxelOf(builder), goal, true);
		t.True(open.Valid && open.Shortest != null && open.Shortest.Found, "The goal " + goal + " cannot be reached even before the wall (" + open.Problem + ")");
		t.Record("openCost", open.Shortest.Cost);
	}

	// The 8 columns round the spot, height high; with a gap: the one west of it left out.
	private static List<XYZ> Ring(XYZ center, int height, bool withGap)
	{
		XYZ gap = Gap(center);
		List<XYZ> blocks = new List<XYZ>();
		for (int dx = -1; dx <= 1; dx++)
		{
			for (int dz = -1; dz <= 1; dz++)
			{
				if ((dx == 0 && dz == 0) || (withGap && center.x + dx == gap.x && center.z + dz == gap.z))
				{
					continue;
				}
				for (int y = 0; y < height; y++)
				{
					blocks.Add(new XYZ(center.x + dx, center.y + y, center.z + dz));
				}
			}
		}
		return blocks;
	}

	private static XYZ Gap(XYZ center)
	{
		return new XYZ(center.x - 1, center.y, center.z);
	}

	// No route: either start or goal is no walkable place, or neither search finds one.
	private static void NoRoute(TestContext t, PathProbe.Report report, string what, XYZ goal)
	{
		if (!report.Valid)
		{
			t.Log("Refused: " + report.Problem);
			return;
		}
		t.False(report.AStar.Found, "The route search found a route " + what + " to " + goal + " (" + report.AStar.Steps + " steps)");
		t.False(report.Shortest != null && report.Shortest.Found, "The reference search found a route " + what);
	}

	// The search took a goal that is no place to stand to the nearest one: the route, if any, ends on an open voxel
	// with solid ground under it, not at the goal itself.
	private static void EndsOnOpenGround(TestContext t, PathProbe.Report report, XYZ goal)
	{
		if (!report.Valid || !report.AStar.Found || report.AStar.Nodes.Count == 0)
		{
			t.Log("No route at all: " + report.Problem);
			return;
		}
		XYZ last = report.AStar.Nodes[report.AStar.Nodes.Count - 1];
		t.Record("endsAt", last.ToString());
		t.True(last != goal, "The route ends in the goal voxel " + goal + " itself");
		t.True(Voxel.IsOpen(last), "The route ends in a voxel that is not open: " + last);
		t.True(Voxel.IsFull(last + new XYZ(0, -1, 0)), "The route ends in the air at " + last);
	}

	private static void Record(TestContext t, PathProbe.Report report)
	{
		t.Record("valid", report.Valid);
		t.Record("problem", report.Problem);
		if (report.AStar != null)
		{
			t.Record("found", report.AStar.Found);
			t.Record("cost", report.AStar.Cost);
			t.Record("steps", report.AStar.Steps);
			t.Record("searched", report.AStar.Searched);
		}
		if (report.Shortest != null)
		{
			t.Record("shortestFound", report.Shortest.Found);
		}
	}
}
