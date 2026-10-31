#if UNITY6_PLAYER
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

// Routes of the player's units over the navigation mesh (NavMeshRoutes, Unity 6 port): a builder's walks are routed
// by the mesh (not the A*) and arrive, round a wall that was built across the way, and up a ledge it climbs. The
// mesh routing is switched on for the test and back afterwards; the mesh itself must be built (NavMeshExperiment).
internal static class NavMeshRouteTests
{
	private sealed class Site
	{
		public Labor Labor;

		public XYZ Center;
	}

	private static Site Prepare(TestContext t, int width, int depth, int clear)
	{
		if (!NavMeshExperiment.Ready || NavMeshRoutes.Enabled == null)
		{
			t.Skip("The navigation mesh is not built ([Pathfinding] NavMeshExperiment, or POST /api/navmesh/start)");
		}
		bool was = NavMeshRoutes.Enabled.Value;
		NavMeshRoutes.Enabled.Value = true;
		t.Cleanup(() => NavMeshRoutes.Enabled.Value = was);
		Faction local = (User.LocalUser != null) ? User.LocalUser.faction : null;
		foreach (Labor labor in Object.FindObjectsOfType<Labor>())
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
			XYZ start = new XYZ(center.x - width / 2 + 1, center.y, center.z);
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

	// The mesh has taken in every change (its tiles are rebuilt a few frames after the graph).
	private static IEnumerator WaitForMesh(TestContext t)
	{
		yield return TestWorld.WaitForGraph(t);
		yield return t.Until(() => !NavMeshExperiment.Busy, 10f, "the navigation mesh to take in the change");
		yield return t.Frames(2);
	}

	// Orders the builder to a voxel and waits until it stands there; returns through routed how many of its routes the
	// mesh made on the way, and calls eachFrame with its position. Other units' fallbacks are logged too (the list is
	// shared), which only helps when reading a failure.
	private static IEnumerator Walk(TestContext t, Labor labor, XYZ goal, float seconds, int[] routed, System.Action<Vector3> eachFrame = null)
	{
		NavMeshRoutes.Watched = labor.transform;
		t.Cleanup(() => NavMeshRoutes.Watched = null);
		int before = NavMeshRoutes.WatchedRouted;
		int fallbacks = NavMeshRoutes.Fallbacks;
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
				foreach (string line in NavMeshRoutes.Recent)
				{
					t.Log(line);
				}
				t.Fail("Waited " + seconds + " s for " + labor.name + " to reach " + goal + "; it stands at " + labor.transform.position);
			}
			eachFrame?.Invoke(labor.transform.position);
			still = (Units.VoxelOf(labor) == goal && labor.navigation.Locomotion.InPlace) ? still + 1 : 0;
			yield return null;
		}
		if (NavMeshRoutes.Fallbacks > fallbacks)
		{
			int from = Mathf.Max(0, NavMeshRoutes.Recent.Count - (NavMeshRoutes.Fallbacks - fallbacks));
			for (int i = from; i < NavMeshRoutes.Recent.Count; i++)
			{
				t.Log("Left to the A*: " + NavMeshRoutes.Recent[i]);
			}
		}
		routed[0] = NavMeshRoutes.WatchedRouted - before;
	}

	[GameTest("Pathfinding", "A builder's walk across open ground is routed over the navigation mesh and arrives", TimeoutSeconds = 120f)]
	private static IEnumerator WalkRoutedByMesh(TestContext t)
	{
		Site site = Prepare(t, 17, 11, 3);
		XYZ a = new XYZ(site.Center.x - 7, site.Center.y, site.Center.z - 3);
		XYZ b = new XYZ(site.Center.x + 7, site.Center.y, site.Center.z + 3);
		int[] routed = new int[1];
		yield return Walk(t, site.Labor, a, 60f, routed);
		yield return Walk(t, site.Labor, b, 60f, routed);
		t.Record("routedByMesh", routed[0]);
		t.AtLeast(1, routed[0], "Routes the mesh made for the walk");
	}

	[GameTest("Pathfinding", "A wall built across the way before the walk: the mesh's route goes round it and the builder never enters it", TimeoutSeconds = 150f)]
	private static IEnumerator RoutesRoundNewWall(TestContext t)
	{
		Site site = Prepare(t, 17, 11, 4);
		XYZ center = site.Center;
		XYZ a = new XYZ(center.x - 7, center.y, center.z);
		XYZ b = new XYZ(center.x + 7, center.y, center.z);
		int[] routed = new int[1];
		yield return Walk(t, site.Labor, a, 60f, routed);
		List<XYZ> wall = new List<XYZ>();
		for (int dz = -4; dz <= 4; dz++)
		{
			wall.Add(new XYZ(center.x, center.y, center.z + dz));
			wall.Add(new XYZ(center.x, center.y + 1, center.z + dz));
		}
		TestWorld.PlaceBlocks(t, wall);
		yield return WaitForMesh(t);
		int inside = 0;
		yield return Walk(t, site.Labor, b, 90f, routed, (Vector3 p) =>
		{
			XYZ voxel = XYZ.FromVector3(p);
			if (voxel.x == center.x && Mathf.Abs(voxel.z - center.z) <= 4 && Voxel.IsFull(new XYZ(voxel.x, center.y, voxel.z)))
			{
				inside++;
			}
		});
		t.Record("routedByMesh", routed[0]);
		t.Equal(0, inside, "Frames the builder stood inside the wall");
		t.AtLeast(1, routed[0], "Routes the mesh made round the wall");
	}

	[GameTest("Pathfinding", "A ledge one block high: the mesh's route climbs it (the graph's climbing branch) and the builder arrives on top", TimeoutSeconds = 150f)]
	private static IEnumerator ClimbsLedge(TestContext t)
	{
		Site site = Prepare(t, 17, 11, 4);
		XYZ center = site.Center;
		XYZ a = new XYZ(center.x - 7, center.y, center.z);
		// A platform of 5 x 5 blocks, one high, east of the middle; the goal on its top.
		List<XYZ> platform = new List<XYZ>();
		for (int dx = 1; dx <= 5; dx++)
		{
			for (int dz = -2; dz <= 2; dz++)
			{
				platform.Add(new XYZ(center.x + dx, center.y, center.z + dz));
			}
		}
		int[] routed = new int[1];
		yield return Walk(t, site.Labor, a, 60f, routed);
		TestWorld.PlaceBlocks(t, platform);
		yield return WaitForMesh(t);
		int climbs = NavMeshRoutes.Climbs;
		if (!PathProbe.Ground(center.x + 3, center.z, out XYZ top) || top.y != center.y + 1)
		{
			t.Skip("The platform was not built where it should be (the ground there is at " + top + ", block placed: " + Voxel.IsFull(new XYZ(center.x + 3, center.y, center.z)) + ")");
		}
		yield return Walk(t, site.Labor, top, 90f, routed);
		t.Record("routedByMesh", routed[0]);
		t.Record("climbLinks", NavMeshRoutes.Climbs - climbs);
		t.AtLeast(1, routed[0], "Routes the mesh made onto the ledge");
		t.AtLeast(1, NavMeshRoutes.Climbs - climbs, "Climbs the mesh's routes took");
	}
}
#endif
