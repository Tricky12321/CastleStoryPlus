using CastleStoryPlus.Upgrades;
using NUnit.Framework;

namespace CastleStoryPlus.Tests;

[TestFixture]
public class SprintRulesTests
{
	[TestCase(0, 0f)]
	[TestCase(1, 5f)]
	[TestCase(2, 6.5f)]
	[TestCase(3, 8f)]
	public void DurationGrowsWithTheTier(int tier, float expected)
	{
		Assert.That(SprintRules.Duration(tier, 5f), Is.EqualTo(expected).Within(0.001f));
	}

	[TestCase(0, 0f)]
	[TestCase(1, 60f)]
	[TestCase(2, 45f)]
	[TestCase(3, 30f)]
	public void CooldownShrinksWithTheTier(int tier, float expected)
	{
		Assert.That(SprintRules.Cooldown(tier, 60f), Is.EqualTo(expected).Within(0.001f));
	}

	[Test]
	public void TierOutOfRangeIsClamped()
	{
		Assert.That(SprintRules.Duration(-4, 5f), Is.EqualTo(0f));
		Assert.That(SprintRules.Duration(9, 5f), Is.EqualTo(SprintRules.Duration(3, 5f)));
	}

	[TestCase(50f, 1.5f)]
	[TestCase(0f, 1f)]
	[TestCase(-20f, 1f)]
	public void FactorIsOnePlusTheBonus(float percent, float expected)
	{
		Assert.That(SprintRules.Factor(percent), Is.EqualTo(expected).Within(0.0001f));
	}

	[Test]
	public void BonusesMultiplyInAnyOrder()
	{
		float boots = 0.1f;
		float road = 1.4f;
		float sprint = 1.5f;
		float composed = SprintRules.Compose(3f, boots, road, sprint);
		Assert.That(composed, Is.EqualTo(3f * 1.1f * 1.4f * 1.5f).Within(0.0001f));
		// What the game's getter gives when the three postfixes run in another order.
		float other = 3f * sprint * road * (1f + boots);
		Assert.That(composed, Is.EqualTo(other).Within(0.0001f));
	}

	[Test]
	public void NoBonusesLeavesTheSpeed()
	{
		Assert.That(SprintRules.Compose(6f, 0f, 1f, 1f), Is.EqualTo(6f));
	}

	[Test]
	public void CannotStartWithoutTheResearch()
	{
		Assert.That(SprintRules.CanStart(0, false, false, 100f, 0f), Is.False);
	}

	[Test]
	public void EnemiesNeverSprint()
	{
		Assert.That(SprintRules.CanStart(3, true, false, 100f, 0f), Is.False);
	}

	[Test]
	public void CannotStartInsideASprint()
	{
		Assert.That(SprintRules.CanStart(1, false, true, 100f, 0f), Is.False);
	}

	[Test]
	public void CannotStartBeforeTheCooldownIsOver()
	{
		float ready = SprintRules.ReadyAt(10f, 1, 5f, 60f);
		Assert.That(ready, Is.EqualTo(75f).Within(0.001f));
		Assert.That(SprintRules.CanStart(1, false, false, 74.9f, ready), Is.False);
		Assert.That(SprintRules.CanStart(1, false, false, 75f, ready), Is.True);
	}

	[Test]
	public void HigherTierIsReadyEarlier()
	{
		Assert.That(SprintRules.ReadyAt(0f, 3, 5f, 60f), Is.EqualTo(8f + 30f).Within(0.001f));
		Assert.That(SprintRules.ReadyAt(0f, 3, 5f, 60f), Is.LessThan(SprintRules.ReadyAt(0f, 1, 5f, 60f)));
	}

	[Test]
	public void KeyAndFleeAlwaysStart()
	{
		Assert.That(SprintRules.ShouldAutoStart(SprintTrigger.Key, false, 0f, 10f), Is.True);
		Assert.That(SprintRules.ShouldAutoStart(SprintTrigger.Flee, false, 0f, 10f), Is.True);
	}

	[TestCase(true, 12f, true)]
	[TestCase(true, 10f, true)]
	[TestCase(true, 9.9f, false)]
	[TestCase(false, 50f, false)]
	public void ChargeNeedsAMeleeFighterWithSomeWayToGo(bool melee, float distance, bool expected)
	{
		Assert.That(SprintRules.ShouldAutoStart(SprintTrigger.Charge, melee, distance, 10f), Is.EqualTo(expected));
	}

	[TestCase(true, 1, true)]
	[TestCase(true, 500, true)]
	[TestCase(true, 501, false)]
	[TestCase(true, 0, false)]
	[TestCase(false, 3, false)]
	public void CommandNeedsAKnownPlayerAndAReasonableCount(bool known, int units, bool expected)
	{
		Assert.That(SprintRules.AcceptCommand(known, units), Is.EqualTo(expected));
	}

	[TestCase(true, true, true)]
	[TestCase(true, false, false)]
	[TestCase(false, true, false)]
	public void CommandUnitsMustBeBricktronsOfTheSendersTeam(bool bricktron, bool sameTeam, bool expected)
	{
		Assert.That(SprintRules.AcceptUnit(bricktron, sameTeam), Is.EqualTo(expected));
	}
}
