#if UNITY6
using System.Collections;
using System.Collections.Generic;
using Brix.Engine;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.AI;
using CastleStoryPlus.Core;
using CastleStoryPlus.DevTools.Endpoints;
using CastleStoryPlus.DevTools.Pathing;
using CastleStoryPlus.DevTools.Testing;
using CastleStoryPlus.Pathfinding;
using UnityEngine;

namespace CastleStoryPlus.DevTools.Tests;

// Enemies behind a biftron (#102, CrowdPathing): corruptrons whose way climbs onto a ledge where a biftron stands
// still climb the free ledge beside it.
[ParallelTests("Combat")]
internal static class EnemyCrowdTests
{
	// Flat ground rising one voxel onto a plateau 9 wide (a ledge); a biftron stands still on the ledge's edge, and
	// corruptrons in a row behind it are sent to the plateau's far end (a move order, or a raid on that place). Passes
	// when every corruptron is on the ledge past the biftron. Without CrowdPathing a lone corruptron on a move order
	// stood below the biftron for good (its every route climbed into the biftron).
	private static IEnumerator Ledge(TestContext t, int count, bool raid)
	{
		if (!Plugin.IsEnabled(Features.CrowdPathing))
		{
			t.Skip("CrowdPathing is off");
		}
		XYZ site = Scenario.Site(t, 41, 13, 5);
		List<XYZ> blocks = new List<XYZ>();
		for (int x = site.x; x <= site.x + 19; x++)
		{
			for (int z = site.z - 4; z <= site.z + 4; z++)
			{
				blocks.Add(new XYZ(x, site.y, z));
			}
		}
		TestWorld.PlaceBlocks(t, blocks);
		yield return TestWorld.WaitForGraph(t);
		Faction enemy = Scenario.EnemyFaction();
		Labor biftron = Scenario.Spawn(t, Test.Biftron, enemy, Occupation.Type.Biftron, new XYZ(site.x, site.y + 1, site.z));
		List<Labor> crowd = new List<Labor>();
		for (int i = 0; i < count; i++)
		{
			crowd.Add(Scenario.Corruptron(t, new XYZ(site.x - 7 - i, site.y, site.z)));
		}
		yield return t.Frames(5);
		biftron.Autonomy = AutonomyStatus.WaitForOrders;
		XYZ goal = new XYZ(site.x + 18, site.y + 1, site.z);
		if (raid)
		{
			RaidProject project = CheatEndpoints.NewRaid(enemy, goal.ToVector3());
			t.Cleanup(() =>
			{
				if (project != null && project.gameObject.activeInHierarchy)
				{
					Scenario.Release(project.gameObject);
				}
			});
			foreach (Labor labor in crowd)
			{
				CheatEndpoints.Hire(project, labor);
			}
		}
		else
		{
			foreach (Labor labor in crowd)
			{
				labor.Autonomy = AutonomyStatus.WaitForOrders;
				labor.AssignToIdle();
				Order.Package.OfLocation(Order.Move.RunToward, Location.Of(goal), Labor.DecisionSource.User).Send(labor);
			}
		}
		StraightPaths.WatchLog.Clear();
		StraightPaths.Watched = crowd[0].transform;
		t.Cleanup(() => StraightPaths.Watched = null);
		int closedBefore = CrowdPathing.Closed;
		float started = Time.time;
		yield return t.UntilGame(() => crowd.TrueForAll(c => !Scenario.Alive(c) || (Units.VoxelOf(c).x > site.x + 1 && Units.VoxelOf(c).y > site.y)), 60f, "the corruptrons to get onto the ledge past the biftron", 5f, () =>
		{
			List<string> state = new List<string>();
			foreach (Labor c in crowd)
			{
				state.Add(Units.VoxelOf(c) + " " + c.navigation.Locomotion.state);
			}
			foreach (string line in StraightPaths.WatchLog)
			{
				t.Log(line);
			}
			StraightPaths.WatchLog.Clear();
			t.Log("Biftron at " + Units.VoxelOf(biftron) + "; corruptrons: " + string.Join("; ", state.ToArray()) + "; voxels closed: " + (CrowdPathing.Closed - closedBefore));
		});
		t.Record("passedAfter", Mathf.Round(Time.time - started));
		t.Record("voxelsClosed", CrowdPathing.Closed - closedBefore);
		t.True(Units.VoxelOf(biftron).x == site.x, "The biftron left its place on the ledge's edge");
	}

	[GameTest("Combat", "A corruptron sent onto a ledge where a biftron stands on the edge climbs up beside it (#102)", TimeoutSeconds = 150f)]
	private static IEnumerator CorruptronClimbsPastBiftron(TestContext t)
	{
		return Ledge(t, 1, false);
	}

	[GameTest("Combat", "Six corruptrons in a raid onto a ledge where a biftron stands on the edge all get onto it (#102)", TimeoutSeconds = 150f)]
	private static IEnumerator RaidClimbsPastBiftron(TestContext t)
	{
		return Ledge(t, 6, true);
	}
}
#endif
