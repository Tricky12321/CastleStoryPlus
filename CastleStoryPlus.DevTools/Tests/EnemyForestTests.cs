#if UNITY6
using System.Collections;
using System.Collections.Generic;
using Brix.Engine;
using Brix.Game;
using Brix.Game.AI;
using Brix.Legacy;
using CastleStoryPlus.Core;
using CastleStoryPlus.DevTools.Endpoints;
using CastleStoryPlus.DevTools.Pathing;
using CastleStoryPlus.DevTools.Testing;
using CastleStoryPlus.Nature;
using UnityEngine;

namespace CastleStoryPlus.DevTools.Tests;

// Enemies and forests (#209, EnemyTreeClearing): a wave whose way runs along a strip of forest takes the open ground
// beside it instead of cutting through the trees, and an enemy that stands in a forest gets out of it to its goal.
[ParallelTests("Combat")]
internal static class EnemyForestTests
{
	// The strip: trees every second column, 13 long (x) and 3 rows (z -2, 0, +2) round the site's centre line, so the
	// forest's places (the voxels beside a tree) span z -3 to +3, and open grass lanes from z +-4 to +-7 run beside it.
	private const int HalfLength = 6;

	private static List<XYZ> PlantStrip(TestContext t, XYZ site)
	{
		List<XYZ> columns = new List<XYZ>();
		for (int x = site.x - HalfLength; x <= site.x + HalfLength; x += 2)
		{
			for (int dz = -2; dz <= 2; dz += 2)
			{
				columns.Add(new XYZ(x, site.y, site.z + dz));
			}
		}
		return Scenario.PlantTrees(t, columns.ToArray());
	}

	private static int StandingTrees(List<XYZ> trees)
	{
		TreeRegistry registry = TreeRegistry.Instance;
		int standing = 0;
		foreach (XYZ tree in trees)
		{
			if (registry != null && registry.HasTree(tree) && DamagedTree.GetAt(tree) == null)
			{
				standing++;
			}
		}
		return standing;
	}

	private static bool Reached(Labor labor, XYZ goal)
	{
		XYZ at = Units.VoxelOf(labor);
		return Mathf.Abs(at.x - goal.x) <= 2 && Mathf.Abs(at.z - goal.z) <= 2;
	}

	private static string State(List<Labor> units)
	{
		List<string> state = new List<string>();
		foreach (Labor labor in units)
		{
			state.Add(Units.VoxelOf(labor) + " " + labor.navigation.Locomotion.state);
		}
		return string.Join("; ", state.ToArray());
	}

	// Four corruptrons in a raid on the far end of the strip, from in front of its near end (on its centre line): the
	// straight way runs through the whole strip, the way round along a lane is a few blocks longer. Passes when every
	// corruptron reaches the goal, no tree of the strip was struck, and none of them stood in the strip's middle row.
	[GameTest("Combat", "A raid whose way runs along a strip of forest walks the open lane beside it instead of cutting through (#209)", TimeoutSeconds = 150f)]
	private static IEnumerator RaidTakesLaneRoundForest(TestContext t)
	{
		if (!Plugin.IsEnabled(Features.EnemyTreeClearing))
		{
			t.Skip("EnemyTreeClearing is off");
		}
		XYZ site = Scenario.Site(t, 33, 15, 5);
		List<XYZ> trees = PlantStrip(t, site);
		yield return TestWorld.WaitForGraph(t);
		Faction enemy = Scenario.EnemyFaction();
		List<Labor> crowd = new List<Labor>();
		for (int i = 0; i < 4; i++)
		{
			crowd.Add(Scenario.Corruptron(t, new XYZ(site.x - HalfLength - 8 - (i % 2), site.y, site.z - 1 + (i / 2) * 2)));
		}
		yield return t.Frames(5);
		XYZ goal = new XYZ(site.x + HalfLength + 8, site.y, site.z);
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
		int searchesBefore = EnemyForestCostPatch.Searches;
		int strikesBefore = EnemyTreeClearing.Strikes;
		int inStrip = 0;
		float started = Time.time;
		yield return t.UntilGame(() =>
		{
			foreach (Labor c in crowd)
			{
				XYZ at = Units.VoxelOf(c);
				if (Mathf.Abs(at.z - site.z) <= 1 && at.x > site.x - HalfLength && at.x < site.x + HalfLength)
				{
					inStrip++;
				}
			}
			return crowd.TrueForAll(c => !Scenario.Alive(c) || Reached(c, goal));
		}, 75f, "the corruptrons to reach the far end of the strip", 5f, () =>
		{
			t.Log("Corruptrons: " + State(crowd) + "; trees standing " + StandingTrees(trees) + "/" + trees.Count + "; strikes " + (EnemyTreeClearing.Strikes - strikesBefore) + "; frames in the strip " + inStrip);
		});
		t.Record("reachedAfter", Mathf.Round(Time.time - started));
		t.Record("forestSearches", EnemyForestCostPatch.Searches - searchesBefore);
		t.Record("strikes", EnemyTreeClearing.Strikes - strikesBefore);
		t.Record("framesInStrip", inStrip);
		t.True(EnemyForestCostPatch.Searches > searchesBefore, "No corruptron route search paid for forest steps");
		t.True(StandingTrees(trees) == trees.Count, "The corruptrons felled " + (trees.Count - StandingTrees(trees)) + " trees of the strip instead of walking round it");
		t.True(inStrip == 0, "The corruptrons walked through the middle of the strip (" + inStrip + " unit frames)");
	}

	// A corruptron standing in the middle of the strip, sent on a move order to the open ground beyond the strip's far
	// end: it gets out of the forest and to its goal, cutting at most a few trees, instead of standing stuck in it.
	[GameTest("Combat", "A corruptron standing in a forest gets out of it to its goal instead of standing stuck (#209)", TimeoutSeconds = 120f)]
	private static IEnumerator CorruptronLeavesForest(TestContext t)
	{
		if (!Plugin.IsEnabled(Features.EnemyTreeClearing))
		{
			t.Skip("EnemyTreeClearing is off");
		}
		XYZ site = Scenario.Site(t, 33, 15, 5);
		List<XYZ> trees = PlantStrip(t, site);
		yield return TestWorld.WaitForGraph(t);
		Labor corruptron = Scenario.Corruptron(t, new XYZ(site.x + 1, site.y, site.z + 1));
		yield return t.Frames(5);
		XYZ goal = new XYZ(site.x + HalfLength + 8, site.y, site.z);
		corruptron.Autonomy = AutonomyStatus.WaitForOrders;
		corruptron.AssignToIdle();
		Order.Package.OfLocation(Order.Move.RunToward, Location.Of(goal), Labor.DecisionSource.User).Send(corruptron);
		int repathsBefore = EnemyTreeClearing.Repaths;
		float started = Time.time;
		List<Labor> one = new List<Labor> { corruptron };
		yield return t.UntilGame(() => !Scenario.Alive(corruptron) || Reached(corruptron, goal), 45f, "the corruptron to get out of the forest to its goal", 5f, () =>
		{
			t.Log("Corruptron: " + State(one) + "; trees standing " + StandingTrees(trees) + "/" + trees.Count + "; repaths " + (EnemyTreeClearing.Repaths - repathsBefore));
		});
		t.Record("reachedAfter", Mathf.Round(Time.time - started));
		t.Record("repaths", EnemyTreeClearing.Repaths - repathsBefore);
		t.Record("treesFelled", trees.Count - StandingTrees(trees));
		t.True(Scenario.Alive(corruptron), "The corruptron died");
	}
}
#endif
