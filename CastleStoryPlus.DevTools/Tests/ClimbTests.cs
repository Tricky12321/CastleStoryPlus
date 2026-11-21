#if UNITY6
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Brix.Engine;
using Brix.Game;
using Brix.Game.AI;
using CastleStoryPlus.DevTools.Pathing;
using CastleStoryPlus.DevTools.Testing;
using CastleStoryPlus.Pathfinding;
using UnityEngine;

namespace CastleStoryPlus.DevTools.Tests;

// Climbing a step without a pause (ClimbPastAllies): bricktrons climb up and step down a one block step where an ally
// stands on the edge, and two bricktrons sent over the same step at once both get there. Before, the in place state
// refused to climb into an ally's voxel and searched the same route again every frame, so the bricktron stood below
// the step until the ally left or its walk gave up after 5 seconds without moving.
internal static class ClimbTests
{
	// The longest a walking bricktron may stand still on its way (game seconds): a turn on the spot and the start of
	// a climb take well under this; the walk's own stuck check fires after 5.
	private const float MaxStill = 2.5f;

	// How far a bricktron must move to count as moving.
	private const float MoveDistance = 0.05f;

	private sealed class Step
	{
		public XYZ Center;

		// The top of the step's edge (where a walk from the west climbs onto it) and the ground below it.
		public XYZ Edge;

		public XYZ Below;
	}

	// Flat ground, and east of the middle a step one block high (6 x 9 blocks); the walk from the west climbs onto it
	// at Edge.
	private static IEnumerator BuildStep(TestContext t, Step step)
	{
		XYZ center = Scenario.Site(t, 17, 13, 4);
		List<XYZ> blocks = new List<XYZ>();
		for (int x = center.x; x <= center.x + 5; x++)
		{
			for (int z = center.z - 4; z <= center.z + 4; z++)
			{
				blocks.Add(new XYZ(x, center.y, z));
			}
		}
		TestWorld.PlaceBlocks(t, blocks);
		yield return TestWorld.WaitForGraph(t);
		if (!PathProbe.Ground(center.x + 2, center.z, out XYZ top) || top.y != center.y + 1)
		{
			t.Skip("The step was not built where it should be (the ground there is at " + top + ")");
		}
		step.Center = center;
		step.Edge = new XYZ(center.x, center.y + 1, center.z);
		step.Below = new XYZ(center.x - 1, center.y, center.z);
		t.Log("Step at " + center + ", edge " + step.Edge);
	}

	private static Labor Worker(TestContext t, XYZ at)
	{
		return Scenario.Bricktron(t, Occupation.Type.Builder, at);
	}

	private static void Send(Labor labor, XYZ goal)
	{
		labor.AssignToIdle();
		Order.Package.OfLocation(Order.Move.RunToward, Location.Of(goal), Labor.DecisionSource.User).Send(labor);
	}

	private static bool Arrived(Labor labor, XYZ goal)
	{
		return labor != null && Units.VoxelOf(labor) == goal && labor.navigation.Locomotion.InPlace;
	}

	// Watches bricktrons on their way: for each, the longest it stood still (game seconds) before it arrived.
	private sealed class StillWatch
	{
		private readonly List<Labor> _labors;

		private readonly List<XYZ> _goals;

		private readonly Vector3[] _anchor;

		private readonly float[] _since;

		public readonly float[] Longest;

		public readonly Vector3[] LongestAt;

		public StillWatch(List<Labor> labors, List<XYZ> goals)
		{
			_labors = labors;
			_goals = goals;
			_anchor = new Vector3[labors.Count];
			_since = new float[labors.Count];
			Longest = new float[labors.Count];
			LongestAt = new Vector3[labors.Count];
			for (int i = 0; i < labors.Count; i++)
			{
				_anchor[i] = labors[i].transform.position;
				_since[i] = Time.time;
			}
		}

		public void Update()
		{
			for (int i = 0; i < _labors.Count; i++)
			{
				Labor labor = _labors[i];
				if (labor == null)
				{
					continue;
				}
				Vector3 position = labor.transform.position;
				if (Arrived(labor, _goals[i]) || (position - _anchor[i]).sqrMagnitude > MoveDistance * MoveDistance)
				{
					_anchor[i] = position;
					_since[i] = Time.time;
					continue;
				}
				float still = Time.time - _since[i];
				if (still > Longest[i])
				{
					Longest[i] = still;
					LongestAt[i] = position;
				}
			}
		}
	}

	// Sends each bricktron to its goal at once and waits until all stand there; gives the longest each stood still.
	private static IEnumerator WalkAll(TestContext t, List<Labor> labors, List<XYZ> goals, float seconds, float[] longest)
	{
		for (int i = 0; i < labors.Count; i++)
		{
			Send(labors[i], goals[i]);
		}
		StillWatch watch = new StillWatch(labors, goals);
		int passed = ClimbPastAllies.Passed;
		int gaveUp = ClimbPastAllies.GaveUp;
		yield return t.UntilGame(() =>
		{
			watch.Update();
			for (int i = 0; i < labors.Count; i++)
			{
				if (!Arrived(labors[i], goals[i]))
				{
					return false;
				}
			}
			return true;
		}, seconds, "every bricktron to reach its goal", 5f, () =>
		{
			List<string> state = new List<string>();
			for (int i = 0; i < labors.Count; i++)
			{
				state.Add(labors[i].name + " at " + Units.VoxelOf(labors[i]) + " (goal " + goals[i] + ") " + labors[i].navigation.Locomotion.state);
			}
			t.Log(string.Join("; ", state.ToArray()));
		});
		for (int i = 0; i < labors.Count; i++)
		{
			longest[i] = Mathf.Max(longest[i], watch.Longest[i]);
			if (watch.Longest[i] > MaxStill)
			{
				t.Log(labors[i].name + " stood still " + watch.Longest[i].ToString("0.0") + " s at " + watch.LongestAt[i]);
			}
		}
		t.Record("passedAllies", ClimbPastAllies.Passed - passed);
		t.Record("waitsGivenUp", ClimbPastAllies.GaveUp - gaveUp);
	}

	[GameTest("Pathfinding", "A builder climbs up and steps down a one block step where an ally stands on the edge, without standing still on the way", TimeoutSeconds = 240f)]
	private static IEnumerator ClimbsStepPastAllyWithoutPause(TestContext t)
	{
		Step step = new Step();
		yield return BuildStep(t, step);
		XYZ center = step.Center;
		XYZ bottom = new XYZ(center.x - 5, center.y, center.z);
		XYZ top = new XYZ(center.x + 3, center.y + 1, center.z);
		Labor ally = Worker(t, step.Edge);
		Labor walker = Worker(t, bottom);
		yield return t.Frames(5);
		ally.Autonomy = AutonomyStatus.WaitForOrders;
		ally.AssignToIdle();
		walker.Autonomy = AutonomyStatus.WaitForOrders;
		float[] longest = new float[1];
		List<Labor> walkers = new List<Labor> { walker };
		// Three times up over the ally's voxel and down again.
		for (int round = 0; round < 3; round++)
		{
			yield return WalkAll(t, walkers, new List<XYZ> { top }, 40f, longest);
			yield return WalkAll(t, walkers, new List<XYZ> { bottom }, 40f, longest);
		}
		t.Record("longestStill", System.Math.Round(longest[0], 2));
		t.True(Units.VoxelOf(ally) == step.Edge, "The ally left its place on the edge (it stands at " + Units.VoxelOf(ally) + ")");
		t.AtMost(MaxStill, longest[0], "Longest the builder stood still on its way (game seconds)");
	}

	[GameTest("Pathfinding", "Two builders sent up the same one block step at once both get there, without standing still on the way", TimeoutSeconds = 150f)]
	private static IEnumerator TwoClimbSameStepAtOnce(TestContext t)
	{
		Step step = new Step();
		yield return BuildStep(t, step);
		XYZ center = step.Center;
		// One behind the other below the edge; the first stops on the edge, the second climbs past it further on.
		Labor first = Worker(t, new XYZ(center.x - 3, center.y, center.z));
		Labor second = Worker(t, new XYZ(center.x - 4, center.y, center.z));
		yield return t.Frames(5);
		first.Autonomy = AutonomyStatus.WaitForOrders;
		second.Autonomy = AutonomyStatus.WaitForOrders;
		float[] longest = new float[2];
		yield return WalkAll(t, new List<Labor> { first, second }, new List<XYZ> { step.Edge, new XYZ(center.x + 3, center.y + 1, center.z) }, 20f, longest);
		t.Record("longestStill", System.Math.Round(longest.Max(), 2));
		t.AtMost(MaxStill, longest.Max(), "Longest a builder stood still on its way (game seconds)");
	}

	[GameTest("Pathfinding", "Two builders cross on the same one block step, one climbing up while the other steps down, and both get there", TimeoutSeconds = 150f)]
	private static IEnumerator CrossOnSameStep(TestContext t)
	{
		Step step = new Step();
		yield return BuildStep(t, step);
		XYZ center = step.Center;
		XYZ bottom = new XYZ(center.x - 4, center.y, center.z);
		XYZ top = new XYZ(center.x + 3, center.y + 1, center.z);
		// Each starts where the other goes, on the line over the edge.
		Labor up = Worker(t, bottom);
		Labor down = Worker(t, top);
		yield return t.Frames(5);
		up.Autonomy = AutonomyStatus.WaitForOrders;
		down.Autonomy = AutonomyStatus.WaitForOrders;
		float[] longest = new float[2];
		yield return WalkAll(t, new List<Labor> { up, down }, new List<XYZ> { top, bottom }, 20f, longest);
		t.Record("longestStill", System.Math.Round(longest.Max(), 2));
		t.AtMost(MaxStill, longest.Max(), "Longest a builder stood still on its way (game seconds)");
	}
}
#endif
