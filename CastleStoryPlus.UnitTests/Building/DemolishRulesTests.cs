using System.Collections.Generic;
using CastleStoryPlus.Building;
using NUnit.Framework;

namespace CastleStoryPlus.UnitTests.Building;

[TestFixture]
public class DemolishRulesTests
{
	[Test]
	public void AnOwnBuildingIsMarked()
	{
		Assert.That(DemolishRules.Decide(true, false, true, true, false), Is.EqualTo(DemolishRules.Decision.Mark));
	}

	[Test]
	public void AMarkedBuildingIsUnmarked()
	{
		Assert.That(DemolishRules.Decide(false, true, true, true, false), Is.EqualTo(DemolishRules.Decision.Cancel));
	}

	[Test]
	public void MarkingTwiceOrCancellingNothingDoesNothing()
	{
		Assert.That(DemolishRules.Decide(true, true, true, true, false), Is.EqualTo(DemolishRules.Decision.Nothing));
		Assert.That(DemolishRules.Decide(false, false, true, true, false), Is.EqualTo(DemolishRules.Decision.Nothing));
	}

	[Test]
	public void AnotherPlayersBuildingIsRefused()
	{
		Assert.That(DemolishRules.Decide(true, false, false, true, false), Is.EqualTo(DemolishRules.Decision.Refuse));
		Assert.That(DemolishRules.Decide(false, true, false, true, false), Is.EqualTo(DemolishRules.Decision.Refuse));
	}

	[Test]
	public void WhatCannotBeDemolishedIsRefused()
	{
		Assert.That(DemolishRules.Decide(true, false, true, false, false), Is.EqualTo(DemolishRules.Decision.Refuse));
	}

	[Test]
	public void ABuildingBeingMovedIsLeftToTheMove()
	{
		Assert.That(DemolishRules.Decide(true, false, true, true, true), Is.EqualTo(DemolishRules.Decision.Moving));
		Assert.That(DemolishRules.Decide(false, true, true, true, true), Is.EqualTo(DemolishRules.Decision.Moving));
	}

	[Test]
	public void TheWholeCostComesBack()
	{
		List<KeyValuePair<string, int>> refund = DemolishRules.Refund(new[]
		{
			new KeyValuePair<string, int>("Plank", 12),
			new KeyValuePair<string, int>("Rope", 4)
		});
		Assert.That(refund, Is.EqualTo(new[]
		{
			new KeyValuePair<string, int>("Plank", 12),
			new KeyValuePair<string, int>("Rope", 4)
		}));
	}

	[Test]
	public void EmptyAndNegativeCountsAreLeftOutAndKindsAdded()
	{
		List<KeyValuePair<string, int>> refund = DemolishRules.Refund(new[]
		{
			new KeyValuePair<string, int>("Plank", 5),
			new KeyValuePair<string, int>("Stone", 0),
			new KeyValuePair<string, int>("Iron", -3),
			new KeyValuePair<string, int>(null, 7),
			new KeyValuePair<string, int>("Plank", 2)
		});
		Assert.That(refund, Is.EqualTo(new[] { new KeyValuePair<string, int>("Plank", 7) }));
	}

	[Test]
	public void ACountIsCapped()
	{
		List<KeyValuePair<string, int>> refund = DemolishRules.Refund(new[] { new KeyValuePair<string, int>("Brick", 5000) });
		Assert.That(refund[0].Value, Is.EqualTo(DemolishRules.MaxPerKind));
	}

	[Test]
	public void NoCostGivesNothing()
	{
		Assert.That(DemolishRules.Refund(null), Is.Empty);
	}

	[Test]
	public void TheSameSpotIsWithinAHundredthOfABlock()
	{
		Assert.That(DemolishRules.SameSpot(0.005f, 0f, -0.005f), Is.True);
		Assert.That(DemolishRules.SameSpot(0.5f, 0f, 0f), Is.False);
	}

	[Test]
	public void AClaimLapses()
	{
		Assert.That(DemolishRules.ClaimLapsed(100f, 50f, 90f), Is.False);
		Assert.That(DemolishRules.ClaimLapsed(200f, 50f, 90f), Is.True);
	}

	[Test]
	public void AMarkIsCancelledOnlyAfterTheGraceWithTheBuildingStanding()
	{
		Assert.That(DemolishRules.Cancelled(true, true, 10f, -1f), Is.False);
		Assert.That(DemolishRules.Cancelled(false, true, 10f, 9f), Is.False);
		Assert.That(DemolishRules.Cancelled(false, true, 10f, 10f - DemolishRules.CancelGraceSeconds), Is.True);
		Assert.That(DemolishRules.Cancelled(false, false, 10f, 0f), Is.False);
	}
}
