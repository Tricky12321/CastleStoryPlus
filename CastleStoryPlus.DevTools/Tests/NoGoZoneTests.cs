using System.Collections;
using System.Collections.Generic;
using Brix.Engine;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Semantique;
using CastleStoryPlus.DevTools.Pathing;
using CastleStoryPlus.DevTools.Testing;
using CastleStoryPlus.Pathfinding;
using UnityEngine;

namespace CastleStoryPlus.DevTools.Tests;

// No-go zones (NoGoZones): a route goes round a zone of the unit's team on open ground (the game's search, and the
// walk the unit is given, over the navigation mesh in the port), a goal inside a zone is still reached, a unit
// standing in a zone walks out of it, and an enemy's route ignores the zone.
[ParallelTests]
internal static class NoGoZoneTests
{
	// The zone in the middle of a 21 x 13 site: 7 x 7 columns across the straight line between the two ends, which
	// stand 8 columns either side of the middle. Round it is 8 or so steps longer than through it.
	private const int Half = 3;

	private const int Reach = 8;

	private static NoGoRect ZoneAt(XYZ center)
	{
		return new NoGoRect(center.x - Half, center.z - Half, center.x + Half, center.z + Half);
	}

	// Draws the zone for the player's faction (the host's own, as the tool does) and takes it away afterwards; waits
	// until the navigation mesh has it.
	private static IEnumerator Draw(TestContext t, NoGoRect zone)
	{
		if (!NoGoZones.Active)
		{
			t.Skip("The NoGoZones feature is off");
		}
		Faction faction = Scenario.Player;
		t.True(NoGoZones.Apply(faction, NoGoZones.OpAdd, zone), "The zone " + zone + " was not taken (" + NoGoZones.LastRefusal + ")");
		t.Cleanup(() => NoGoZones.Apply(faction, NoGoZones.OpErase, zone));
		yield return t.Frames(2);
#if UNITY6_PLAYER
		yield return t.Until(() => !NavMeshExperiment.Busy, 10f, "the navigation mesh to take in the zone");
		yield return t.Frames(2);
#endif
	}

	private static int Inside(List<XYZ> cells, NoGoRect zone)
	{
		int inside = 0;
		foreach (XYZ cell in cells)
		{
			if (zone.Contains(cell.x, cell.z))
			{
				inside++;
			}
		}
		return inside;
	}

	// Orders the unit to a voxel and waits until it stands there; the voxels it went through are added to track.
	private static IEnumerator Walk(TestContext t, Labor labor, XYZ goal, List<XYZ> track)
	{
		labor.AssignToIdle();
		Order.Package.OfLocation(Order.Move.RunToward, Location.Of(goal), Labor.DecisionSource.User).Send(labor);
		float end = Time.realtimeSinceStartup + 60f;
		int still = 0;
		while (still < 5)
		{
			if (labor == null)
			{
				t.Fail("The unit is gone");
			}
			if (Time.realtimeSinceStartup > end)
			{
				t.Fail("Waited 60 s for " + labor.name + " to reach " + goal + "; it stands at " + labor.transform.position);
			}
			XYZ at = Units.VoxelOf(labor);
			if (track != null && (track.Count == 0 || track[track.Count - 1] != at))
			{
				track.Add(at);
			}
			still = (at == goal && labor.navigation.Locomotion.InPlace) ? still + 1 : 0;
			yield return null;
		}
	}

	[GameTest("NoGoZones", "The game's route search goes round a no-go zone of the unit's team on open ground")]
	private static IEnumerator SearchGoesRound(TestContext t)
	{
		XYZ center = Scenario.Site(t, 2 * Reach + 5, 2 * Half + 7, 3);
		XYZ a = new XYZ(center.x - Reach, center.y, center.z);
		XYZ b = new XYZ(center.x + Reach, center.y, center.z);
		Labor labor = Scenario.Bricktron(t, Occupation.Type.Builder, a);
		yield return t.Frames(10);
		yield return TestWorld.WaitForGraph(t);
		NoGoRect zone = ZoneAt(center);
		PathProbe.Report before = PathProbe.Run(labor, a, b, false);
		t.True(before.Valid && before.AStar.Found, "No route from " + a + " to " + b + " without the zone: " + before.Problem);
		t.Record("throughBefore", Inside(before.AStar.Nodes, zone));
		yield return Draw(t, zone);
		PathProbe.Report after = PathProbe.Run(labor, a, b, false);
		t.True(after.Valid && after.AStar.Found, "No route from " + a + " to " + b + " with the zone (it must stay passable): " + after.Problem);
		int inside = Inside(after.AStar.Nodes, zone);
		t.Record("steps", after.AStar.Steps);
		t.Record("throughAfter", inside);
		t.Equal(0, inside, "Route voxels inside the zone " + zone);
	}

	[GameTest("NoGoZones", "A bricktron walks round a no-go zone of its team, close beside it", TimeoutSeconds = 120f)]
	private static IEnumerator WalkGoesRound(TestContext t)
	{
		XYZ center = Scenario.Site(t, 2 * Reach + 5, 2 * Half + 7, 3);
		XYZ a = new XYZ(center.x - Reach, center.y, center.z);
		XYZ b = new XYZ(center.x + Reach, center.y, center.z);
		Labor labor = Scenario.Bricktron(t, Occupation.Type.Builder, a);
		yield return t.Frames(10);
		yield return TestWorld.WaitForGraph(t);
		yield return Walk(t, labor, a, null);
		NoGoRect zone = ZoneAt(center);
		yield return Draw(t, zone);
		List<XYZ> track = new List<XYZ>();
		yield return Walk(t, labor, b, track);
		int inside = Inside(track, zone);
		t.Record("voxels", track.Count);
		t.Record("inside", inside);
		t.Equal(0, inside, "Voxels walked inside the zone " + zone);
		// Round it, not far out: the walk stays within a few columns of the zone's edge.
		int widest = 0;
		foreach (XYZ cell in track)
		{
			widest = Mathf.Max(widest, Mathf.Abs(cell.z - center.z));
		}
		t.Record("widest", widest);
		t.AtMost(Half + 3, widest, "How far from the straight line the walk went");
	}

	[GameTest("NoGoZones", "A goal inside a no-go zone is still reached", TimeoutSeconds = 120f)]
	private static IEnumerator GoalInsideIsReached(TestContext t)
	{
		XYZ center = Scenario.Site(t, 2 * Reach + 5, 2 * Half + 7, 3);
		XYZ a = new XYZ(center.x - Reach, center.y, center.z);
		Labor labor = Scenario.Bricktron(t, Occupation.Type.Builder, a);
		yield return t.Frames(10);
		yield return TestWorld.WaitForGraph(t);
		yield return Walk(t, labor, a, null);
		NoGoRect zone = ZoneAt(center);
		yield return Draw(t, zone);
		PathProbe.Report report = PathProbe.Run(labor, a, center, false);
		t.True(report.Valid && report.AStar.Found, "No route to " + center + " in the middle of the zone: " + report.Problem);
		List<XYZ> track = new List<XYZ>();
		yield return Walk(t, labor, center, track);
		// It goes in by the shortest way: about the zone's half width of voxels inside.
		int inside = Inside(track, zone);
		t.Record("inside", inside);
		t.AtMost(Half + 3, inside, "Voxels walked inside the zone to its middle");
	}

	[GameTest("NoGoZones", "A bricktron standing in a no-go zone walks out of it", TimeoutSeconds = 120f)]
	private static IEnumerator StartInsideLeaves(TestContext t)
	{
		XYZ center = Scenario.Site(t, 2 * Reach + 5, 2 * Half + 7, 3);
		XYZ b = new XYZ(center.x + Reach, center.y, center.z);
		Labor labor = Scenario.Bricktron(t, Occupation.Type.Builder, center);
		yield return t.Frames(10);
		yield return TestWorld.WaitForGraph(t);
		yield return Walk(t, labor, center, null);
		NoGoRect zone = ZoneAt(center);
		yield return Draw(t, zone);
		List<XYZ> track = new List<XYZ>();
		yield return Walk(t, labor, b, track);
		int inside = Inside(track, zone);
		t.Record("inside", inside);
		t.AtMost(Half + 3, inside, "Voxels walked inside the zone on the way out");
	}

	[GameTest("NoGoZones", "An enemy's route ignores the player's no-go zone")]
	private static IEnumerator EnemyIgnoresZone(TestContext t)
	{
		XYZ center = Scenario.Site(t, 2 * Reach + 5, 2 * Half + 7, 3);
		XYZ a = new XYZ(center.x - Reach, center.y, center.z);
		XYZ b = new XYZ(center.x + Reach, center.y, center.z);
		Labor enemy = Scenario.Corruptron(t, a);
		yield return t.Frames(10);
		yield return TestWorld.WaitForGraph(t);
		NoGoRect zone = ZoneAt(center);
		yield return Draw(t, zone);
		t.True(NoGoZones.MapFor(enemy.faction) == null, "The enemy's faction heeds the player's zones");
		PathProbe.Report report = PathProbe.Run(enemy, a, b, false);
		t.True(report.Valid && report.AStar.Found, "No route for the enemy: " + report.Problem);
		t.Record("inside", Inside(report.AStar.Nodes, zone));
		t.AtLeast(1, Inside(report.AStar.Nodes, zone), "Route voxels inside the zone (the straight way)");
	}
}
