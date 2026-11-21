#if UNITY6
using System.Collections;
using System.Collections.Generic;
using Brix.Engine;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.AI;
using CastleStoryPlus.Core;
using CastleStoryPlus.DevTools.Pathing;
using CastleStoryPlus.DevTools.Testing;
using CastleStoryPlus.Pathfinding;
using UnityEngine;

namespace CastleStoryPlus.DevTools.Tests;

// A stair one voxel wide as the only way between two levels (a maintainer's save: workers on their idle walk, stockpile
// consolidation and store-or-drop stood in one stair voxel for 15 s and more, gave their walk up or walked back down
// and up again). Chilling workers now pass each other there (ChillingPassAllies); a search that finds no route because
// an ally's voxel on the stair was closed to it searches again with it open (CrowdPathing); and a unit that may not
// pass allies waits on the stair for the ally instead of turning back (ClimbPastAllies).
internal static class StairCrowdTests
{
	// A walk up or down the stair and across the level takes well under this.
	private const float ArriveSeconds = 60f;

	private sealed class Stair
	{
		public XYZ Center;

		// The steps' tops, from the bottom: Steps[1] and Steps[2] can only be entered over the step before or after.
		public XYZ[] Steps;

		public int Floor;

		public int Top;

		// Floor more than two voxels west of the stair's foot, and the upper level more than two voxels east of its head.
		public bool AwayBelow(XYZ voxel)
		{
			return voxel.y == Floor && voxel.x < Center.x - 4;
		}

		public bool AwayAbove(XYZ voxel)
		{
			return voxel.y == Top && voxel.x > Center.x + 3;
		}
	}

	// Flat ground, and east of the middle a level four blocks high (8 x 9 blocks); the only way up is a stair of three
	// one block steps, one voxel wide, against its west side.
	private static IEnumerator BuildStair(TestContext t, Stair stair)
	{
		XYZ c = Scenario.Site(t, 25, 13, 6);
		List<XYZ> blocks = new List<XYZ>();
		for (int x = c.x + 1; x <= c.x + 8; x++)
		{
			for (int z = c.z - 4; z <= c.z + 4; z++)
			{
				for (int y = c.y; y <= c.y + 3; y++)
				{
					blocks.Add(new XYZ(x, y, z));
				}
			}
		}
		stair.Steps = new XYZ[3];
		for (int k = 1; k <= 3; k++)
		{
			int x = c.x - 3 + k;
			for (int y = c.y; y < c.y + k; y++)
			{
				blocks.Add(new XYZ(x, y, c.z));
			}
			stair.Steps[k - 1] = new XYZ(x, c.y + k, c.z);
		}
		TestWorld.PlaceBlocks(t, blocks);
		yield return TestWorld.WaitForGraph(t);
		if (!PathProbe.Ground(c.x + 4, c.z, out XYZ top) || top.y != c.y + 4 || !PathProbe.Ground(c.x - 1, c.z, out XYZ step) || step.y != c.y + 2)
		{
			t.Skip("The stair was not built where it should be (the ground there is at " + top + ")");
		}
		stair.Center = c;
		stair.Floor = c.y;
		stair.Top = c.y + 4;
		t.Log("Stair at " + c + ", steps " + stair.Steps[0] + " " + stair.Steps[1] + " " + stair.Steps[2]);
	}

	private static bool Arrived(Labor labor, XYZ goal)
	{
		if (labor == null || !labor.navigation.Locomotion.InPlace)
		{
			return false;
		}
		XYZ at = Units.VoxelOf(labor);
		return at.y == goal.y && Mathf.Abs(at.x - goal.x) + Mathf.Abs(at.z - goal.z) <= 1;
	}

	// A walk order as the idle task gives it: the worker is chilling while it walks.
	private static void SendChilling(Labor labor, XYZ goal)
	{
		labor.Autonomy = AutonomyStatus.WaitForOrders;
		labor.AssignToIdle();
		OrderPackage<Labor> package = Order.Package.OfLocation(Order.Move.WalkToward, Location.Of(goal), Labor.DecisionSource.User);
		package.SetPrepare((BaseLabor worker) =>
		{
			((Labor)worker).Activity = Activity.Chilling;
			return true;
		});
		package.Send(labor);
	}

	private static void SendRun(Labor labor, XYZ goal)
	{
		labor.Autonomy = AutonomyStatus.WaitForOrders;
		labor.AssignToIdle();
		Order.Package.OfLocation(Order.Move.RunToward, Location.Of(goal), Labor.DecisionSource.User).Send(labor);
	}

	// Waits until every unit stands at its goal; counts the units that turned back: one going up that was on the stair
	// and is later on the floor away from it, one going down that was on the stair and is later up on the level away
	// from it.
	private static IEnumerator WalkAll(TestContext t, Stair stair, List<Labor> labors, List<XYZ> goals, List<bool> up, int[] turnedBack)
	{
		bool[] onStair = new bool[labors.Count];
		bool[] turned = new bool[labors.Count];
		int closed = CrowdPathing.Closed;
		int keptOpen = CrowdPathing.KeptOpen;
		int reopened = CrowdPathing.Reopened;
		int gaveUp = ClimbPastAllies.GaveUp;
		float started = Time.time;
		yield return t.UntilGame(() =>
		{
			bool all = true;
			for (int i = 0; i < labors.Count; i++)
			{
				XYZ at = Units.VoxelOf(labors[i]);
				if (at.y > stair.Floor && at.y < stair.Top)
				{
					onStair[i] = true;
				}
				else if (onStair[i] && !turned[i] && (up[i] ? stair.AwayBelow(at) : stair.AwayAbove(at)))
				{
					turned[i] = true;
					t.Log(labors[i].name + " turned back to " + at + " (goal " + goals[i] + ") after " + (Time.time - started).ToString("0.0") + " s");
				}
				if (!Arrived(labors[i], goals[i]))
				{
					all = false;
				}
			}
			return all;
		}, ArriveSeconds, "every unit to get up or down the stair", 5f, () =>
		{
			List<string> state = new List<string>();
			for (int i = 0; i < labors.Count; i++)
			{
				Task task = labors[i].CurrentTask;
				state.Add(labors[i].name + " at " + Units.VoxelOf(labors[i]) + " (goal " + goals[i] + ") " + labors[i].Activity + " " + ((task != null) ? task.Description : "-") + " " + labors[i].navigation.Locomotion.state);
			}
			t.Log(string.Join("; ", state.ToArray()));
		});
		for (int i = 0; i < labors.Count; i++)
		{
			if (turned[i])
			{
				turnedBack[0]++;
			}
		}
		t.Record("seconds", System.Math.Round(Time.time - started, 1));
		t.Record("voxelsClosed", CrowdPathing.Closed - closed);
		t.Record("keptOpen", CrowdPathing.KeptOpen - keptOpen);
		t.Record("searchedAgain", CrowdPathing.Reopened - reopened);
		t.Record("waitsGivenUp", ClimbPastAllies.GaveUp - gaveUp);
		t.Record("turnedBack", turnedBack[0]);
	}

	[GameTest("Pathfinding", "Chilling workers (idle walk) going up and down a one voxel wide stair at once all get there, none turns back", TimeoutSeconds = 240f)]
	private static IEnumerator ChillingWorkersCrossOnStair(TestContext t)
	{
		if (!Plugin.IsEnabled(Features.PassThroughAllies))
		{
			t.Skip("PassThroughAllies is off");
		}
		Stair stair = new Stair();
		yield return BuildStair(t, stair);
		XYZ c = stair.Center;
		List<Labor> labors = new List<Labor>();
		List<XYZ> goals = new List<XYZ>();
		List<bool> up = new List<bool>();
		for (int i = 0; i < 3; i++)
		{
			labors.Add(Scenario.Bricktron(t, Occupation.Type.Builder, new XYZ(c.x - 6 - i, stair.Floor, c.z)));
			goals.Add(new XYZ(c.x + 7, stair.Top, c.z - 2 + 2 * i));
			up.Add(true);
		}
		for (int i = 0; i < 3; i++)
		{
			labors.Add(Scenario.Bricktron(t, Occupation.Type.Builder, new XYZ(c.x + 2 + i, stair.Top, c.z)));
			goals.Add(new XYZ(c.x - 9, stair.Floor, c.z - 2 + 2 * i));
			up.Add(false);
		}
		yield return t.Frames(5);
		for (int i = 0; i < labors.Count; i++)
		{
			SendChilling(labors[i], goals[i]);
		}
		int[] turnedBack = new int[1];
		yield return WalkAll(t, stair, labors, goals, up, turnedBack);
		t.Equal(0, turnedBack[0], "Workers that turned back on the stair");
	}

	[GameTest("Pathfinding", "A worker whose only way up (a stair step) is closed to its search still finds the way up through it", TimeoutSeconds = 150f)]
	private static IEnumerator ClosedStairStepIsSearchedAgainOpen(TestContext t)
	{
		if (!Plugin.IsEnabled(Features.CrowdPathing))
		{
			t.Skip("CrowdPathing is off");
		}
		Stair stair = new Stair();
		yield return BuildStair(t, stair);
		XYZ c = stair.Center;
		Labor worker = Scenario.Bricktron(t, Occupation.Type.Builder, new XYZ(c.x - 6, stair.Floor, c.z));
		yield return t.Frames(5);
		// As if an ally standing there had blocked it a moment ago: the step is closed to its searches for a while.
		XYZ step = stair.Steps[1];
		CrowdPathing.Closures.GetOrCreateValue(worker.presence).Voxels.Close(CrowdPathing.Key(step), Time.time, 60f);
		int reopened = CrowdPathing.Reopened;
		XYZ goal = new XYZ(c.x + 6, stair.Top, c.z);
		SendRun(worker, goal);
		int[] turnedBack = new int[1];
		yield return WalkAll(t, stair, new List<Labor> { worker }, new List<XYZ> { goal }, new List<bool> { true }, turnedBack);
		t.True(CrowdPathing.Reopened > reopened, "The search did not search again with the closed step open (it went another way?)");
		t.True(CrowdPathing.IsChoke(worker.presence, step), "The step is not remembered as the only way");
	}

	[GameTest("Combat", "A corruptron going up a one voxel wide stair where a biftron stands waits on the stair and climbs on when it moves off, without turning back", TimeoutSeconds = 150f)]
	private static IEnumerator CorruptronWaitsOnStairForBiftron(TestContext t)
	{
		if (!Plugin.IsEnabled(Features.CrowdPathing) || !Plugin.IsEnabled(Features.PassThroughAllies))
		{
			t.Skip("CrowdPathing or PassThroughAllies is off");
		}
		Stair stair = new Stair();
		yield return BuildStair(t, stair);
		XYZ c = stair.Center;
		Faction enemy = Scenario.EnemyFaction();
		Labor biftron = Scenario.Spawn(t, Test.Biftron, enemy, Occupation.Type.Biftron, stair.Steps[1]);
		Labor corruptron = Scenario.Corruptron(t, new XYZ(c.x - 6, stair.Floor, c.z));
		yield return t.Frames(5);
		biftron.Autonomy = AutonomyStatus.WaitForOrders;
		XYZ goal = new XYZ(c.x + 6, stair.Top, c.z);
		SendRun(corruptron, goal);
		// The biftron stands on the stair for a few seconds (longer than ClimbPastAllies' first wait), then goes up.
		yield return t.Seconds(4f);
		SendRun(biftron, new XYZ(c.x + 6, stair.Top, c.z + 3));
		int[] turnedBack = new int[1];
		yield return WalkAll(t, stair, new List<Labor> { corruptron }, new List<XYZ> { goal }, new List<bool> { true }, turnedBack);
		t.Equal(0, turnedBack[0], "Times the corruptron turned back on the stair");
	}
}
#endif
