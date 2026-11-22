using System.Collections.Generic;
using CastleStoryPlus.WorkerAI;
using NUnit.Framework;

namespace CastleStoryPlus.UnitTests.WorkerAI;

[TestFixture]
public class BaseAreaRulesTests
{
	// A crystal at (0, 0) with 40 blocks round it, a stockpile at (100, 0) with 40, a task zone at (0, 200) of 10.
	private static List<float> Base()
	{
		return new List<float> { 0f, 0f, 40f, 100f, 0f, 40f, 0f, 200f, 10f };
	}

	[Test]
	public void APlaceNearAnyAnchorIsInTheBase()
	{
		Assert.That(BaseAreaRules.Inside(0f, 0f, Base()), Is.True);
		Assert.That(BaseAreaRules.Inside(30f, 20f, Base()), Is.True);
		Assert.That(BaseAreaRules.Inside(130f, 0f, Base()), Is.True);
		Assert.That(BaseAreaRules.Inside(5f, 205f, Base()), Is.True);
	}

	[Test]
	public void APlaceFarFromEveryAnchorIsOutside()
	{
		// Half way between the crystal and the task zone, and far out past the stockpile.
		Assert.That(BaseAreaRules.Inside(0f, 100f, Base()), Is.False);
		Assert.That(BaseAreaRules.Inside(300f, 300f, Base()), Is.False);
	}

	[Test]
	public void TheEdgeOfACircleIsStillIn()
	{
		Assert.That(BaseAreaRules.Inside(40f, 0f, new List<float> { 0f, 0f, 40f }), Is.True);
		Assert.That(BaseAreaRules.Inside(40.1f, 0f, new List<float> { 0f, 0f, 40f }), Is.False);
	}

	[Test]
	public void HowFarOutIsMeasuredToTheNearestEdge()
	{
		// 60 from the crystal (20 past its edge), 40 from the task zone's middle (30 past its edge).
		Assert.That(BaseAreaRules.OutsideBy(0f, 60f, new List<float> { 0f, 0f, 40f, 0f, 100f, 10f }), Is.EqualTo(20f).Within(0.001f));
		// Inside: zero or less.
		Assert.That(BaseAreaRules.OutsideBy(10f, 0f, Base()), Is.LessThanOrEqualTo(0f));
	}

	[Test]
	public void ADistanceIsMeasuredAcrossNotAlongTheAxes()
	{
		// 30 and 30 across is 42.4 away: out, although each axis alone is within 40.
		Assert.That(BaseAreaRules.Inside(30f, 30f, new List<float> { 0f, 0f, 40f }), Is.False);
		Assert.That(BaseAreaRules.Inside(28f, 28f, new List<float> { 0f, 0f, 40f }), Is.True);
	}

	[Test]
	public void ATeamWithNoAnchorsHasNoBaseToKeepTo()
	{
		Assert.That(BaseAreaRules.Inside(5000f, -5000f, new List<float>()), Is.True);
		Assert.That(BaseAreaRules.Inside(5000f, -5000f, null), Is.True);
		Assert.That(BaseAreaRules.OutsideBy(5000f, -5000f, new List<float>()), Is.EqualTo(0f));
	}

	[Test]
	public void AnEnemyIsNearWithinItsRadiusAndHeight()
	{
		Assert.That(BaseAreaRules.Near(10f, 5f, 10f, 20f, 5f, 10f, 20f), Is.True);
		Assert.That(BaseAreaRules.Near(10f, 5f, 10f, 30f, 5f, 10f, 20f), Is.True);
		Assert.That(BaseAreaRules.Near(10f, 5f, 10f, 30.5f, 5f, 10f, 20f), Is.False);
		// Far over or under the place (a wall top, a cliff) is not near.
		Assert.That(BaseAreaRules.Near(10f, 5f, 10f, 10f, 5f + BaseAreaRules.EnemyHeight, 10f, 20f), Is.True);
		Assert.That(BaseAreaRules.Near(10f, 5f, 10f, 10f, 5f + BaseAreaRules.EnemyHeight + 1f, 10f, 20f), Is.False);
		Assert.That(BaseAreaRules.Near(10f, 5f, 10f, 10f, 5f - BaseAreaRules.EnemyHeight - 1f, 10f, 20f), Is.False);
	}

	[Test]
	public void AnyEnemyCounts()
	{
		List<float> enemies = new List<float> { 500f, 0f, 500f, 50f, 0f, 50f };
		Assert.That(BaseAreaRules.NearAny(45f, 0f, 45f, enemies, 20f), Is.True);
		Assert.That(BaseAreaRules.NearAny(0f, 0f, 0f, enemies, 20f), Is.False);
		Assert.That(BaseAreaRules.NearAny(0f, 0f, 0f, new List<float>(), 20f), Is.False);
		Assert.That(BaseAreaRules.NearAny(0f, 0f, 0f, null, 20f), Is.False);
	}

	[Test]
	public void WhatNoPlayerOwnsIsOnlyTakenInTheBaseAndAwayFromEnemies()
	{
		Assert.That(BaseAreaRules.Judge(own: false, insideBase: true, nearEnemy: false), Is.EqualTo(BaseAreaRules.Verdict.Allowed));
		Assert.That(BaseAreaRules.Judge(own: false, insideBase: false, nearEnemy: false), Is.EqualTo(BaseAreaRules.Verdict.OutsideBase));
		Assert.That(BaseAreaRules.Judge(own: false, insideBase: true, nearEnemy: true), Is.EqualTo(BaseAreaRules.Verdict.NearEnemies));
		Assert.That(BaseAreaRules.Judge(own: false, insideBase: false, nearEnemy: true), Is.EqualTo(BaseAreaRules.Verdict.NearEnemies));
	}

	[Test]
	public void TheTeamsOwnThingsAreTakenAnywhereButByEnemiesOutsideTheBase()
	{
		Assert.That(BaseAreaRules.Judge(own: true, insideBase: true, nearEnemy: false), Is.EqualTo(BaseAreaRules.Verdict.Allowed));
		Assert.That(BaseAreaRules.Judge(own: true, insideBase: false, nearEnemy: false), Is.EqualTo(BaseAreaRules.Verdict.Allowed));
		// A siege does not stop the builders fetching from the base's own stockpiles.
		Assert.That(BaseAreaRules.Judge(own: true, insideBase: true, nearEnemy: true), Is.EqualTo(BaseAreaRules.Verdict.Allowed));
		Assert.That(BaseAreaRules.Judge(own: true, insideBase: false, nearEnemy: true), Is.EqualTo(BaseAreaRules.Verdict.NearEnemies));
	}

	[Test]
	public void OnlyAWorkerWellOutWithNoPlayersOrderIsReported()
	{
		Assert.That(BaseAreaRules.Report(30f, playerOrder: false, slack: 4f), Is.True);
		Assert.That(BaseAreaRules.Report(30f, playerOrder: true, slack: 4f), Is.False);
		Assert.That(BaseAreaRules.Report(3f, playerOrder: false, slack: 4f), Is.False);
		Assert.That(BaseAreaRules.Report(-10f, playerOrder: false, slack: 4f), Is.False);
	}
}
