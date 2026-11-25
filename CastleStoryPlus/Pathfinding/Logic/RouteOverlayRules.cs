using System;
using System.Collections.Generic;

namespace CastleStoryPlus.Pathfinding;

// How a step of a unit's route is drawn by the route overlay (RouteOverlay).
internal enum RouteStepState
{
	Walking,
	Climbing,
	Stuck
}

// The pure rules of the route overlay (RouteOverlay), without the game: the state of each step, the runs of one
// colour, the colours, and when a unit counts as stuck.
internal static class RouteOverlayRules
{
	// The most route points drawn per unit (the unit itself and the next nodes); a longer route is cut there.
	internal const int MaxPoints = 400;

	// The most selected units whose routes are drawn.
	internal const int MaxUnits = 32;

	// A unit with a route that has not moved MoveEpsilon blocks in StuckSeconds counts as stuck (the game's own walker
	// gives a route up after 5 s without moving).
	internal const float StuckSeconds = 3f;

	internal const float MoveEpsilon = 0.1f;

	// Height of the line over the voxel a route point stands in (the floor is half a block under its centre).
	internal const float Lift = -0.35f;

	// The state of the step onto a route node: a climb up a ledge, a step down one or a ladder is climbing, the rest is
	// walking. A stuck unit's whole route is stuck.
	internal static RouteStepState StepState(bool climbUp, bool stepDown, bool ladder, bool stuck)
	{
		if (stuck)
		{
			return RouteStepState.Stuck;
		}
		return (climbUp || stepDown || ladder) ? RouteStepState.Climbing : RouteStepState.Walking;
	}

	// The runs of one state along a route of steps.Count + 1 points (step i goes from point i to point i + 1). Two runs
	// next to each other share their boundary point, so the line has no gaps. An empty list for no steps.
	internal static List<RouteRun> Runs(IList<RouteStepState> steps)
	{
		List<RouteRun> runs = new List<RouteRun>();
		Runs(steps, runs);
		return runs;
	}

	internal static void Runs(IList<RouteStepState> steps, List<RouteRun> runs)
	{
		runs.Clear();
		if (steps == null)
		{
			return;
		}
		for (int i = 0; i < steps.Count; i++)
		{
			RouteRun last = (runs.Count > 0) ? runs[runs.Count - 1] : null;
			if (last != null && last.State == steps[i])
			{
				last.Last = i + 1;
				continue;
			}
			runs.Add(new RouteRun { First = i, Last = i + 1, State = steps[i] });
		}
	}

	// The colour of a state, as red, green and blue from 0 to 1: walking light blue, climbing amber, stuck red.
	internal static void Colour(RouteStepState state, out float r, out float g, out float b)
	{
		switch (state)
		{
			case RouteStepState.Climbing:
				r = 1f;
				g = 0.7f;
				b = 0.15f;
				return;
			case RouteStepState.Stuck:
				r = 1f;
				g = 0.2f;
				b = 0.15f;
				return;
			default:
				r = 0.3f;
				g = 0.8f;
				b = 1f;
				return;
		}
	}

	// The voxel level a unit's feet are in: its position is anywhere from the floor (half a block under the voxel's
	// centre) to a little over the centre, so the level is that rounded with the floor counted to the voxel above it.
	internal static float VoxelLevel(float y)
	{
		return (float)Math.Floor(y + 0.6f);
	}
}
