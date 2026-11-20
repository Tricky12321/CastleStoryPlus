using System.Collections.Generic;
using CastleStoryPlus.Treasure;
using NUnit.Framework;

namespace CastleStoryPlus.UnitTests.Treasure;

[TestFixture]
public class TreasureRulesTests
{
	[Test]
	public void TheRuinAndTheGuardiansGroundAreKeptOut()
	{
		// The chest itself, the ruin's wall (4 out), its rubble (up to 8) and where the guardians roam (up to 9).
		Assert.That(TreasureRules.InKeepOut(10f, 5f, 10f, 10f, 5f, 10f), Is.True);
		Assert.That(TreasureRules.InKeepOut(14f, 6f, 10f, 10f, 5f, 10f), Is.True);
		Assert.That(TreasureRules.InKeepOut(18f, 5f, 10f, 10f, 5f, 10f), Is.True);
		Assert.That(TreasureRules.InKeepOut(10f, 5f, 19f, 10f, 5f, 10f), Is.True);
	}

	[Test]
	public void TheEdgeOfTheClearingIsStillIn()
	{
		Assert.That(TreasureRules.InKeepOut(10f + TreasureRules.KeepOutRadius, 5f, 10f, 10f, 5f, 10f), Is.True);
		Assert.That(TreasureRules.InKeepOut(10f + TreasureRules.KeepOutRadius + 0.1f, 5f, 10f, 10f, 5f, 10f), Is.False);
	}

	[Test]
	public void TheDistanceIsMeasuredAcrossNotAlongTheAxes()
	{
		// 9 and 9 across is 12.7 away: out, although each axis alone is within 12.
		Assert.That(TreasureRules.InKeepOut(19f, 5f, 19f, 10f, 5f, 10f), Is.False);
		Assert.That(TreasureRules.InKeepOut(18f, 5f, 18f, 10f, 5f, 10f), Is.True);
	}

	[Test]
	public void FarAboveOrBelowIsNotInTheRuin()
	{
		Assert.That(TreasureRules.InKeepOut(10f, 5f + TreasureRules.KeepOutHeight, 10f, 10f, 5f, 10f), Is.True);
		Assert.That(TreasureRules.InKeepOut(10f, 5f + TreasureRules.KeepOutHeight + 1f, 10f, 10f, 5f, 10f), Is.False);
		Assert.That(TreasureRules.InKeepOut(10f, 5f - TreasureRules.KeepOutHeight - 1f, 10f, 10f, 5f, 10f), Is.False);
	}

	[Test]
	public void AnyGuardedChestCounts()
	{
		List<float> guarded = new List<float> { 0f, 0f, 0f, 100f, 20f, 100f };
		Assert.That(TreasureRules.InAnyKeepOut(3f, 0f, 3f, guarded), Is.True);
		Assert.That(TreasureRules.InAnyKeepOut(105f, 21f, 95f, guarded), Is.True);
		Assert.That(TreasureRules.InAnyKeepOut(50f, 10f, 50f, guarded), Is.False);
	}

	[Test]
	public void NoGuardedChestKeepsNothingOut()
	{
		Assert.That(TreasureRules.InAnyKeepOut(0f, 0f, 0f, new List<float>()), Is.False);
		// A list cut short (not three by three) reads only its whole chests.
		Assert.That(TreasureRules.InAnyKeepOut(0f, 0f, 0f, new List<float> { 0f, 0f }), Is.False);
	}

	[Test]
	public void OnlyAnUnopenedGuardedChestKeepsOut()
	{
		Assert.That(TreasureRules.KeepsOut(opened: false, guarded: true), Is.True);
		Assert.That(TreasureRules.KeepsOut(opened: false, guarded: false), Is.False);
		Assert.That(TreasureRules.KeepsOut(opened: true, guarded: true), Is.False);
		Assert.That(TreasureRules.KeepsOut(opened: true, guarded: false), Is.False);
	}
}
