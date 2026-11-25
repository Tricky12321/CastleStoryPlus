using System.Collections.Generic;
using CastleStoryPlus.Pathfinding;
using NUnit.Framework;

namespace CastleStoryPlus.UnitTests.Pathfinding;

[TestFixture]
public class RouteOverlayRulesTests
{
	private static List<RouteStepState> Steps(params int[] states)
	{
		List<RouteStepState> steps = new List<RouteStepState>();
		foreach (int state in states)
		{
			steps.Add((RouteStepState)state);
		}
		return steps;
	}

	private const int Walk = (int)RouteStepState.Walking;

	private const int Climb = (int)RouteStepState.Climbing;

	private const int Stuck = (int)RouteStepState.Stuck;

	[Test]
	public void APlainStepIsWalking()
	{
		Assert.That(RouteOverlayRules.StepState(false, false, false, false), Is.EqualTo(RouteStepState.Walking));
	}

	[Test]
	public void ClimbingUpSteppingDownAndLaddersAreClimbing()
	{
		Assert.That(RouteOverlayRules.StepState(true, false, false, false), Is.EqualTo(RouteStepState.Climbing));
		Assert.That(RouteOverlayRules.StepState(false, true, false, false), Is.EqualTo(RouteStepState.Climbing));
		Assert.That(RouteOverlayRules.StepState(false, false, true, false), Is.EqualTo(RouteStepState.Climbing));
	}

	[Test]
	public void AStuckUnitsStepsAreStuckWhateverTheirKind()
	{
		Assert.That(RouteOverlayRules.StepState(false, false, false, true), Is.EqualTo(RouteStepState.Stuck));
		Assert.That(RouteOverlayRules.StepState(true, true, true, true), Is.EqualTo(RouteStepState.Stuck));
	}

	[Test]
	public void NoStepsGiveNoRuns()
	{
		Assert.That(RouteOverlayRules.Runs(Steps()), Is.Empty);
		Assert.That(RouteOverlayRules.Runs(null), Is.Empty);
	}

	[Test]
	public void StepsOfOneStateAreOneRunOverAllPoints()
	{
		List<RouteRun> runs = RouteOverlayRules.Runs(Steps(Walk, Walk, Walk));
		Assert.That(runs.Count, Is.EqualTo(1));
		Assert.That(runs[0].First, Is.EqualTo(0));
		Assert.That(runs[0].Last, Is.EqualTo(3));
		Assert.That(runs[0].State, Is.EqualTo(RouteStepState.Walking));
	}

	[Test]
	public void RunsSplitWhereTheStateChangesAndShareTheirBoundaryPoint()
	{
		// Walk, walk, climb, walk: points 0-2 walking, 2-3 climbing, 3-4 walking.
		List<RouteRun> runs = RouteOverlayRules.Runs(Steps(Walk, Walk, Climb, Walk));
		Assert.That(runs.Count, Is.EqualTo(3));
		Assert.That(runs[0].First, Is.EqualTo(0));
		Assert.That(runs[0].Last, Is.EqualTo(2));
		Assert.That(runs[1].First, Is.EqualTo(2));
		Assert.That(runs[1].Last, Is.EqualTo(3));
		Assert.That(runs[1].State, Is.EqualTo(RouteStepState.Climbing));
		Assert.That(runs[2].First, Is.EqualTo(3));
		Assert.That(runs[2].Last, Is.EqualTo(4));
		Assert.That(runs[2].State, Is.EqualTo(RouteStepState.Walking));
	}

	[Test]
	public void ASingleStepIsARunOfTwoPoints()
	{
		List<RouteRun> runs = RouteOverlayRules.Runs(Steps(Climb));
		Assert.That(runs.Count, Is.EqualTo(1));
		Assert.That(runs[0].First, Is.EqualTo(0));
		Assert.That(runs[0].Last, Is.EqualTo(1));
	}

	[Test]
	public void RunsIntoAListReplaceWhatItHad()
	{
		List<RouteRun> runs = RouteOverlayRules.Runs(Steps(Walk, Climb, Walk));
		RouteOverlayRules.Runs(Steps(Stuck, Stuck), runs);
		Assert.That(runs.Count, Is.EqualTo(1));
		Assert.That(runs[0].State, Is.EqualTo(RouteStepState.Stuck));
		Assert.That(runs[0].Last, Is.EqualTo(2));
	}

	[Test]
	public void EveryStateHasItsOwnColour()
	{
		RouteOverlayRules.Colour(RouteStepState.Walking, out float wr, out float wg, out float wb);
		RouteOverlayRules.Colour(RouteStepState.Climbing, out float cr, out float cg, out float cb);
		RouteOverlayRules.Colour(RouteStepState.Stuck, out float sr, out float sg, out float sb);
		Assert.That(new[] { wr, wg, wb }, Is.Not.EqualTo(new[] { cr, cg, cb }));
		Assert.That(new[] { wr, wg, wb }, Is.Not.EqualTo(new[] { sr, sg, sb }));
		Assert.That(new[] { cr, cg, cb }, Is.Not.EqualTo(new[] { sr, sg, sb }));
		// Stuck is red: far more red than green or blue.
		Assert.That(sr, Is.GreaterThan(sg + 0.5f));
		Assert.That(sr, Is.GreaterThan(sb + 0.5f));
		// Walking is blue.
		Assert.That(wb, Is.GreaterThan(wr));
		foreach (float channel in new[] { wr, wg, wb, cr, cg, cb, sr, sg, sb })
		{
			Assert.That(channel, Is.InRange(0f, 1f));
		}
	}

	[Test]
	public void TheVoxelLevelCountsTheFloorAndTheCentreToTheSameVoxel()
	{
		// Voxel 10: its floor is at 9.5, its centre at 10.
		Assert.That(RouteOverlayRules.VoxelLevel(9.5f), Is.EqualTo(10f));
		Assert.That(RouteOverlayRules.VoxelLevel(10f), Is.EqualTo(10f));
		Assert.That(RouteOverlayRules.VoxelLevel(10.3f), Is.EqualTo(10f));
		Assert.That(RouteOverlayRules.VoxelLevel(9.45f), Is.EqualTo(10f));
		Assert.That(RouteOverlayRules.VoxelLevel(9.35f), Is.EqualTo(9f));
		Assert.That(RouteOverlayRules.VoxelLevel(-0.5f), Is.EqualTo(0f));
	}
}
