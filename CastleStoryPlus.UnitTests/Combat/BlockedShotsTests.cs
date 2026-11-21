using CastleStoryPlus.Combat;
using NUnit.Framework;

namespace CastleStoryPlus.UnitTests.Combat;

[TestFixture]
public class BlockedShotsTests
{
	private sealed class Unit
	{
	}

	private BlockedShots<Unit, Unit> _shots;

	private Unit _archer;

	private Unit _target;

	[SetUp]
	public void SetUp()
	{
		_shots = new BlockedShots<Unit, Unit>();
		_archer = new Unit();
		_target = new Unit();
	}

	[Test]
	public void NothingIsBlockedAtFirst()
	{
		Assert.That(_shots.IsBlocked(_archer, _target, 1, 2, 3, 0f), Is.False);
		Assert.That(_shots.Count, Is.EqualTo(0));
	}

	[Test]
	public void ABlockedTargetIsLeftBeForTheFirstHoldThenTriedAgainWithoutMoving()
	{
		_shots.Block(_archer, _target, 1, 2, 3, 10f);
		Assert.That(_shots.IsBlocked(_archer, _target, 1, 2, 3, 10f), Is.True);
		Assert.That(_shots.IsBlocked(_archer, _target, 1, 2, 3, 10f + BlockedShots<Unit, Unit>.FirstHold - 0.1f), Is.True);
		Assert.That(_shots.IsBlocked(_archer, _target, 1, 2, 3, 10f + BlockedShots<Unit, Unit>.FirstHold), Is.False);
	}

	[Test]
	public void OnlyTheBlockItWasBlockedFromCounts()
	{
		_shots.Block(_archer, _target, 1, 2, 3, 0f);
		Assert.That(_shots.IsBlocked(_archer, _target, 2, 2, 3, 1f), Is.False, "one block over");
		Assert.That(_shots.IsBlocked(_archer, _target, 1, 3, 3, 1f), Is.False, "one block up");
		// A test from another block (RangedReposition trying a place) does not forget it.
		Assert.That(_shots.IsBlocked(_archer, _target, 1, 2, 3, 1f), Is.True);
	}

	[Test]
	public void OnlyThatFighterAndThatTargetAreConcerned()
	{
		Unit other = new Unit();
		_shots.Block(_archer, _target, 0, 0, 0, 0f);
		Assert.That(_shots.IsBlocked(other, _target, 0, 0, 0, 1f), Is.False, "another fighter on the same block");
		Assert.That(_shots.IsBlocked(_archer, other, 0, 0, 0, 1f), Is.False, "another target");
	}

	[Test]
	public void EachBlockAgainOnTheSameCellDoublesTheHoldUpToTheMost()
	{
		Assert.That(BlockedShots<Unit, Unit>.Hold(1), Is.EqualTo(4f));
		Assert.That(BlockedShots<Unit, Unit>.Hold(2), Is.EqualTo(8f));
		Assert.That(BlockedShots<Unit, Unit>.Hold(3), Is.EqualTo(16f));
		Assert.That(BlockedShots<Unit, Unit>.Hold(4), Is.EqualTo(BlockedShots<Unit, Unit>.MaxHold));
		Assert.That(BlockedShots<Unit, Unit>.Hold(50), Is.EqualTo(BlockedShots<Unit, Unit>.MaxHold));

		_shots.Block(_archer, _target, 0, 0, 0, 0f);
		_shots.Block(_archer, _target, 0, 0, 0, 5f);
		Assert.That(_shots.IsBlocked(_archer, _target, 0, 0, 0, 12.9f), Is.True, "second block: 8 s");
		Assert.That(_shots.IsBlocked(_archer, _target, 0, 0, 0, 13f), Is.False);
	}

	[Test]
	public void ABlockFromAnotherCellStartsAtTheFirstHoldAgain()
	{
		_shots.Block(_archer, _target, 0, 0, 0, 0f);
		_shots.Block(_archer, _target, 0, 0, 0, 5f);
		_shots.Block(_archer, _target, 4, 0, 0, 20f);
		Assert.That(_shots.IsBlocked(_archer, _target, 4, 0, 0, 23.9f), Is.True);
		Assert.That(_shots.IsBlocked(_archer, _target, 4, 0, 0, 24f), Is.False);
		Assert.That(_shots.IsBlocked(_archer, _target, 0, 0, 0, 21f), Is.False, "the old cell is no longer blocked");
	}

	[Test]
	public void AShotThatGetsOffForgetsTheTarget()
	{
		_shots.Block(_archer, _target, 0, 0, 0, 0f);
		_shots.Block(_archer, _target, 0, 0, 0, 5f);
		_shots.Forget(_archer, _target);
		Assert.That(_shots.IsBlocked(_archer, _target, 0, 0, 0, 6f), Is.False);
		Assert.That(_shots.Count, Is.EqualTo(0));
		// Blocked again after the shot: the first hold, not a doubled one.
		_shots.Block(_archer, _target, 0, 0, 0, 10f);
		Assert.That(_shots.IsBlocked(_archer, _target, 0, 0, 0, 14f), Is.False);
	}

	[Test]
	public void OldEntriesAreDropped()
	{
		_shots.Block(_archer, _target, 0, 0, 0, 0f);
		Unit fresh = new Unit();
		_shots.Block(_archer, fresh, 0, 0, 0, 40f);
		_shots.Prune(40f);
		Assert.That(_shots.Count, Is.EqualTo(1));
		Assert.That(_shots.IsBlocked(_archer, fresh, 0, 0, 0, 41f), Is.True);
	}

	[Test]
	public void NullsAreIgnored()
	{
		_shots.Block(null, _target, 0, 0, 0, 0f);
		_shots.Block(_archer, null, 0, 0, 0, 0f);
		Assert.That(_shots.Count, Is.EqualTo(0));
		Assert.That(_shots.IsBlocked(null, _target, 0, 0, 0, 0f), Is.False);
	}
}
