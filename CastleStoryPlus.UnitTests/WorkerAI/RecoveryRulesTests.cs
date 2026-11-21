using System.Collections.Generic;
using CastleStoryPlus.WorkerAI;
using NUnit.Framework;

namespace CastleStoryPlus.UnitTests.WorkerAI;

[TestFixture]
public class RecoveryRulesTests
{
	[Test]
	public void FailuresInARowMakeALoop()
	{
		List<float> failures = new List<float>();
		Assert.That(RecoveryRules.RecordOutcome(failures, 0f, true), Is.False);
		Assert.That(RecoveryRules.RecordOutcome(failures, 5f, true), Is.False);
		Assert.That(RecoveryRules.RecordOutcome(failures, 10f, true), Is.False);
		Assert.That(RecoveryRules.RecordOutcome(failures, 15f, true), Is.True);
		Assert.That(failures, Is.Empty);
	}

	[Test]
	public void ATaskDoneWellEndsTheRun()
	{
		List<float> failures = new List<float>();
		RecoveryRules.RecordOutcome(failures, 0f, true);
		RecoveryRules.RecordOutcome(failures, 5f, true);
		RecoveryRules.RecordOutcome(failures, 10f, true);
		Assert.That(RecoveryRules.RecordOutcome(failures, 12f, false), Is.False);
		Assert.That(RecoveryRules.RecordOutcome(failures, 15f, true), Is.False);
	}

	[Test]
	public void SlowFailuresAreNoLoop()
	{
		List<float> failures = new List<float>();
		for (int i = 0; i < 20; i++)
		{
			Assert.That(RecoveryRules.RecordOutcome(failures, i * 30f, true), Is.False);
		}
	}

	[TestCase(-1f, 50f, false)]
	[TestCase(0f, 100f, true)]
	[TestCase(0f, 121f, false)]
	public void ARepeatedLoopEscalates(float last, float now, bool expected)
	{
		Assert.That(RecoveryRules.Escalates(last, now), Is.EqualTo(expected));
	}

	[TestCase(5f, false)]
	[TestCase(-19f, false)]
	[TestCase(-21f, true)]
	[TestCase(float.NaN, true)]
	public void BelowTheMapIsTheVoid(float y, bool expected)
	{
		Assert.That(RecoveryRules.InVoid(y), Is.EqualTo(expected));
	}

	[TestCase(true, -1f, 10f, true)]
	[TestCase(false, -1f, 10f, false)]
	[TestCase(true, 9f, 10f, false)]
	[TestCase(true, 7f, 10f, true)]
	public void AResetCommandNeedsATeamAndIsRateLimited(bool hasTeam, float last, float now, bool expected)
	{
		Assert.That(RecoveryRules.AcceptResetCommand(hasTeam, last, now), Is.EqualTo(expected));
	}

	[TestCase(0, 0, 0)]
	[TestCase(5, 5, 0)]
	[TestCase(8, 5, 3)]
	[TestCase(3, 5, 0)]
	public void ForeignPiecesAreTheOnesNotDug(int total, int dug, int expected)
	{
		Assert.That(RecoveryRules.ForeignCount(total, dug), Is.EqualTo(expected));
	}

	[TestCase(10f, 12f, false)]
	[TestCase(10f, 15f, false)]
	[TestCase(10f, 15.5f, true)]
	public void ALongGapWithoutATaskEndsTheStuckWatch(float lastSeen, float now, bool expected)
	{
		Assert.That(RecoveryRules.GapEndsWatch(lastSeen, now), Is.EqualTo(expected));
	}

	[TestCase(true, false, 10f)]
	[TestCase(false, true, 10f)]
	[TestCase(true, true, 10f)]
	[TestCase(false, false, 60f)]
	public void ARetryLoopGetsTheShortStuckLimit(bool walk, bool severalTasks, float expected)
	{
		Assert.That(RecoveryRules.StuckLimit(walk, severalTasks, 10f, 60f), Is.EqualTo(expected));
	}

	[TestCase(true, true, true)]
	[TestCase(true, false, true)]
	[TestCase(false, false, true)]
	[TestCase(false, true, false)]
	public void ABlueprintIsOnlyMarkedUnreachableAfterAFailedWalk(bool walkFailed, bool blueprint, bool expected)
	{
		Assert.That(RecoveryRules.MarksStoodStillGoal(walkFailed, blueprint), Is.EqualTo(expected));
	}

	[TestCase(0f, false)]
	[TestCase(1f, true)]
	[TestCase(0.25f, true)]
	[TestCase(20f, true)]
	public void TheResetWaitsWhileThePauseLasts(float timeScale, bool expected)
	{
		Assert.That(RecoveryRules.CanResetNow(timeScale), Is.EqualTo(expected));
	}

	[Test]
	public void APlayersOrderWaitsLessForTheLegsThanAnyOtherTask()
	{
		Assert.That(RecoveryRules.LegsWaitLimit(true), Is.EqualTo(RecoveryRules.OrderWaitSeconds));
		Assert.That(RecoveryRules.LegsWaitLimit(false), Is.EqualTo(RecoveryRules.LongWaitSeconds));
		Assert.That(RecoveryRules.LegsWaitLimit(true), Is.LessThan(RecoveryRules.LegsWaitLimit(false)));
		Assert.That(RecoveryRules.LegsWaitLimit(true), Is.GreaterThan(0f));
	}

	[TestCase(false, false, false, false, false, 0f, true)]
	[TestCase(true, false, false, false, false, 0f, false)]
	[TestCase(false, true, false, false, false, 0f, false)]
	[TestCase(false, false, true, false, false, 0f, false)]
	[TestCase(false, false, false, true, false, 0f, false)]
	[TestCase(false, false, false, false, true, 0.1f, true)]
	[TestCase(false, false, false, false, true, 0.5f, false)]
	[TestCase(false, false, false, false, true, 3f, false)]
	[TestCase(false, false, false, false, false, 3f, true)]
	public void LegsAreResetOnlyWhenNothingElseHoldsThem(bool dead, bool grabbed, bool phasedOut, bool climbing, bool flailing, float speed, bool expected)
	{
		Assert.That(RecoveryRules.LegsMayBeReset(dead, grabbed, phasedOut, climbing, flailing, speed), Is.EqualTo(expected));
	}

	[TestCase(false, true, false, true)]
	[TestCase(true, true, false, false)]
	[TestCase(false, false, false, false)]
	[TestCase(false, true, true, false)]
	public void LegsWaitingWhileTheAnimationStandsAreOutOfStep(bool canMove, bool stands, bool inTransition, bool expected)
	{
		Assert.That(RecoveryRules.LegsOutOfStep(canMove, stands, inTransition), Is.EqualTo(expected));
	}
}
