#if UNITY6
using System;
using System.Collections;
using System.Collections.Generic;
using Brix.Engine;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Network;
using CastleStoryPlus.DevTools.Pathing;
using CastleStoryPlus.DevTools.Testing;
using CastleStoryPlus.Pathfinding;
using UnityEngine;

namespace CastleStoryPlus.DevTools.Tests;

// Straight walks across open ground (StraightPaths): a builder walks a slanted line on flat ground, keeps out of a
// pillar on its line, and still gets round a wall or a hole that appears on its line while it walks.
internal static class StraightPathTests
{
	// How far from the straight line a walk across open ground may stray (a voxel is 1).
	private const float MaxDeviation = 0.5f;

	private sealed class Site
	{
		public Labor Labor;

		public XYZ Center;
	}

	// A flat open area and one of the player's bricktrons with a route to its start (west side): the area nearest
	// each bricktron in turn.
	private static Site FindSite(TestContext t, int width, int depth, int clear)
	{
		Faction local = (User.LocalUser != null) ? User.LocalUser.faction : null;
		foreach (Labor labor in UnityEngine.Object.FindObjectsOfType<Labor>())
		{
			if (labor == null || labor.navigation == null || !labor.gameObject.activeInHierarchy || local == null || !local.IsSame(labor.gameObject))
			{
				continue;
			}
			XYZ from = Units.VoxelOf(labor);
			if (!TestWorld.FindFlatArea(from, width, depth, clear, 40, out XYZ center))
			{
				continue;
			}
			XYZ start = new XYZ(center.x - width / 2 + 1, center.y, center.z - 3);
			PathProbe.Report plan = PathProbe.Run(labor, from, start, false);
			if (plan.Valid && plan.AStar.Found)
			{
				t.Log("Flat area at " + center + ", " + labor.name + " walks from " + from);
				return new Site { Labor = labor, Center = center };
			}
		}
		t.Skip("No flat open area of " + width + " x " + depth + " that one of the player's bricktrons can walk to");
		return null;
	}

	// Orders the builder to a voxel and records where it is every frame until it stands there (or the time is up).
	private static IEnumerator Walk(TestContext t, Labor labor, XYZ goal, List<Vector3> track, float seconds, Action<Vector3> eachFrame = null)
	{
		// What happens to its routes on the way (StraightPaths), logged when the walk fails.
		StraightPaths.WatchLog.Clear();
		StraightPaths.Watched = labor.transform;
		t.Cleanup(() => StraightPaths.Watched = null);
		labor.AssignToIdle();
		Order.Package.OfLocation(Order.Move.RunToward, Location.Of(goal), Labor.DecisionSource.User).Send(labor);
		float end = Time.realtimeSinceStartup + seconds;
		int still = 0;
		while (still < 10)
		{
			if (labor == null)
			{
				t.Fail("The builder is gone");
			}
			if (Time.realtimeSinceStartup > end)
			{
				foreach (string line in StraightPaths.WatchLog)
				{
					t.Log(line);
				}
				t.Fail("Waited " + seconds + " s for " + labor.name + " to reach " + goal + "; it stands at " + labor.transform.position);
			}
			Vector3 position = labor.transform.position;
			track?.Add(position);
			eachFrame?.Invoke(position);
			still = (Units.VoxelOf(labor) == goal && labor.navigation.Locomotion.InPlace) ? still + 1 : 0;
			yield return null;
		}
	}

	private static float Deviation(Vector3 p, Vector3 a, Vector3 b)
	{
		Vector2 d = new Vector2(b.x - a.x, b.z - a.z);
		Vector2 o = new Vector2(p.x - a.x, p.z - a.z);
		return Mathf.Abs(d.x * o.y - d.y * o.x) / d.magnitude;
	}

	private static bool Inside(Vector3 p, XYZ voxel)
	{
		return Mathf.Abs(p.x - voxel.x) < 0.5f && Mathf.Abs(p.z - voxel.z) < 0.5f;
	}

	[GameTest("Pathfinding", "A builder walks a slanted line across flat open ground instead of a staircase", TimeoutSeconds = 120f)]
	private static IEnumerator WalksStraightAcrossOpenGround(TestContext t)
	{
		Site site = FindSite(t, 17, 11, 3);
		Labor labor = site.Labor;
		XYZ center = site.Center;
		XYZ a = new XYZ(center.x - 7, center.y, center.z - 3);
		XYZ b = new XYZ(center.x + 7, center.y, center.z + 3);
		yield return Walk(t, labor, a, null, 60f);
		// Loose items, other units or a door on the line keep the staircase; then there is nothing to measure.
		string clear = StraightPaths.Explain(a, b, labor.presence.BuildPresenceTest(), labor.gameObject);
		if (clear != "clear")
		{
			t.Skip("The line is not clear: " + clear);
		}
		List<Vector3> track = new List<Vector3>();
		yield return Walk(t, labor, b, track, 60f);
		foreach (string line in StraightPaths.WatchLog)
		{
			t.Log(line);
		}
		float worst = 0f;
		float length = 0f;
		for (int i = 0; i < track.Count; i++)
		{
			if (i > 0)
			{
				length += new Vector2(track[i].x - track[i - 1].x, track[i].z - track[i - 1].z).magnitude;
			}
			worst = Mathf.Max(worst, Deviation(track[i], a.ToVector3(), b.ToVector3()));
		}
		float straight = new Vector2(b.x - a.x, b.z - a.z).magnitude;
		t.Record("straightPaths", StraightPaths.Enabled != null && StraightPaths.Enabled.Value);
		t.Record("maxDeviation", Math.Round(worst, 2));
		t.Record("walked", Math.Round(length, 2));
		t.Record("straightLine", Math.Round(straight, 2));
		if (StraightPaths.Enabled == null || !StraightPaths.Enabled.Value)
		{
			t.Skip("StraightPaths is off (max deviation " + worst.ToString("0.00") + ")");
		}
		t.AtMost(MaxDeviation, worst, "Furthest the walk strayed from the straight line");
	}

	[GameTest("Pathfinding", "A pillar on the line: the builder walks round it, never into it", TimeoutSeconds = 120f)]
	private static IEnumerator KeepsOutOfPillarOnLine(TestContext t)
	{
		Site site = FindSite(t, 17, 11, 4);
		Labor labor = site.Labor;
		XYZ center = site.Center;
		XYZ a = new XYZ(center.x - 7, center.y, center.z - 3);
		XYZ b = new XYZ(center.x + 7, center.y, center.z + 3);
		yield return Walk(t, labor, a, null, 60f);
		// Pillars where the line passes and on the corners next to it.
		List<XYZ> pillars = new List<XYZ> { center, new XYZ(center.x + 2, center.y, center.z + 1), new XYZ(center.x - 3, center.y, center.z - 2) };
		List<XYZ> blocks = new List<XYZ>();
		foreach (XYZ p in pillars)
		{
			blocks.Add(p);
			blocks.Add(p + XYZ.up);
		}
		TestWorld.PlaceBlocks(t, blocks);
		yield return TestWorld.WaitForGraph(t);
		List<Vector3> track = new List<Vector3>();
		yield return Walk(t, labor, b, track, 60f);
		int inside = 0;
		foreach (Vector3 p in track)
		{
			foreach (XYZ pillar in pillars)
			{
				if (Inside(p, pillar))
				{
					inside++;
				}
			}
		}
		t.Record("frames", track.Count);
		t.Equal(0, inside, "Frames the builder stood inside a pillar");
	}

	[GameTest("Pathfinding", "A wall built across the line while the builder walks it: it walks round, never through", TimeoutSeconds = 120f)]
	private static IEnumerator WallAppearsOnLine(TestContext t)
	{
		Site site = FindSite(t, 17, 11, 4);
		Labor labor = site.Labor;
		XYZ center = site.Center;
		XYZ a = new XYZ(center.x - 7, center.y, center.z - 3);
		XYZ b = new XYZ(center.x + 7, center.y, center.z + 3);
		yield return Walk(t, labor, a, null, 60f);
		// Across the line, two thirds of the way, once the builder is a third of the way.
		XYZ middle = new XYZ(center.x + 3, center.y, center.z + 1);
		List<XYZ> wall = new List<XYZ>();
		for (int dz = -3; dz <= 3; dz++)
		{
			wall.Add(new XYZ(middle.x, middle.y, middle.z + dz));
			wall.Add(new XYZ(middle.x, middle.y + 1, middle.z + dz));
		}
		bool placed = false;
		int placedFrame = 0;
		int inside = 0;
		List<Vector3> track = new List<Vector3>();
		yield return Walk(t, labor, b, track, 90f, (Vector3 p) =>
		{
			if (!placed && p.x > a.x + 4)
			{
				placed = true;
				placedFrame = Time.frameCount;
				TestWorld.PlaceBlocks(t, wall);
				t.Log("Wall placed with the builder at " + p);
			}
			XYZ voxel = XYZ.FromVector3(p);
			// Only the wall's blocks that were placed (one where something stands is refused).
			if (placed && voxel.x == middle.x && Mathf.Abs(voxel.z - middle.z) <= 3 && Voxel.IsFull(new XYZ(voxel.x, middle.y, voxel.z)))
			{
				if (inside == 0)
				{
					t.Log("Inside the wall at " + p + ", " + (Time.frameCount - placedFrame) + " frames after it was placed");
				}
				inside++;
			}
		});
		t.True(placed, "The wall was never placed");
		t.Record("frames", track.Count);
		t.Equal(0, inside, "Frames the builder stood inside the wall");
	}

	[GameTest("Pathfinding", "A pit dug in the ground on the line while the builder walks it: it never walks over the pit, and still arrives", TimeoutSeconds = 150f)]
	private static IEnumerator PitDugOnLine(TestContext t)
	{
		Site site = FindSite(t, 17, 11, 3);
		Labor labor = site.Labor;
		XYZ center = site.Center;
		XYZ a = new XYZ(center.x - 7, center.y, center.z - 3);
		XYZ b = new XYZ(center.x + 7, center.y, center.z + 3);
		yield return Walk(t, labor, a, null, 60f);
		// Two columns on the line ahead are lowered by 2 (deeper than a step down) once the builder is on its way.
		List<XYZ> pit = new List<XYZ> { new XYZ(center.x + 2, center.y, center.z + 1), new XYZ(center.x + 3, center.y, center.z + 1) };
		bool dug = false;
		int over = 0;
		List<Vector3> track = new List<Vector3>();
		yield return Walk(t, labor, b, track, 90f, (Vector3 p) =>
		{
			if (!dug && p.x > a.x + 3)
			{
				dug = true;
				Dig(pit, -2);
				t.Cleanup(() => Dig(pit, 2));
				t.Log("Pit dug with the builder at " + p);
			}
			XYZ voxel = XYZ.FromVector3(p);
			if (dug && voxel.y >= center.y && pit.Exists((XYZ c) => c.x == voxel.x && c.z == voxel.z))
			{
				over++;
			}
		});
		t.True(dug, "The pit was never dug");
		t.Record("frames", track.Count);
		t.Equal(0, over, "Frames the builder stood over the pit at the old ground's height");
	}

	// Moves the top of the given columns up or down, as the world editor's brushes do.
	private static void Dig(List<XYZ> columns, int by)
	{
		VoxelEngine.BeginChanges();
		try
		{
			foreach (XYZ c in columns)
			{
				VoxelEngine.SetColumnTopVoxel(c.x, c.z, VoxelEngine.GetColumnTop(c.x, c.z) + by);
			}
		}
		finally
		{
			VoxelEngine.ApplyChanges();
		}
		GameSignals.Invoke(GameSignals.OnTerrainEdited);
	}
}
#endif
