using CastleStoryPlus.Pathfinding;
using NUnit.Framework;

namespace CastleStoryPlus.UnitTests.Pathfinding;

[TestFixture]
public class RouteStuckWatchTests
{
	private const float Limit = RouteOverlayRules.StuckSeconds;

	[Test]
	public void AUnitWithoutARouteIsNeverStuck()
	{
		RouteStuckWatch watch = new RouteStuckWatch();
		Assert.That(watch.Update(0f, 1f, 2f, 3f, false), Is.False);
		Assert.That(watch.Update(100f, 1f, 2f, 3f, false), Is.False);
		Assert.That(watch.Stuck, Is.False);
	}

	[Test]
	public void AUnitStandingStillOnItsRouteIsStuckAtTheLimitNotBefore()
	{
		RouteStuckWatch watch = new RouteStuckWatch();
		Assert.That(watch.Update(10f, 1f, 2f, 3f, true), Is.False);
		Assert.That(watch.Update(10f + Limit - 0.01f, 1f, 2f, 3f, true), Is.False);
		Assert.That(watch.Update(10f + Limit, 1f, 2f, 3f, true), Is.True);
		Assert.That(watch.Stuck, Is.True);
	}

	[Test]
	public void MovingStartsTheWaitAgain()
	{
		RouteStuckWatch watch = new RouteStuckWatch();
		watch.Update(0f, 0f, 0f, 0f, true);
		watch.Update(Limit - 0.5f, 0f, 0f, 0f, true);
		// Moved half a block: not stuck, and the wait counts from here.
		Assert.That(watch.Update(Limit - 0.1f, 0.5f, 0f, 0f, true), Is.False);
		Assert.That(watch.Update(Limit + 1f, 0.5f, 0f, 0f, true), Is.False);
		Assert.That(watch.Update(2f * Limit, 0.5f, 0f, 0f, true), Is.True);
	}

	[Test]
	public void ATinyShuffleIsNotMoving()
	{
		RouteStuckWatch watch = new RouteStuckWatch();
		watch.Update(0f, 0f, 0f, 0f, true);
		float shuffle = RouteOverlayRules.MoveEpsilon * 0.5f;
		Assert.That(watch.Update(Limit / 2f, shuffle, 0f, shuffle, true), Is.False);
		Assert.That(watch.Update(Limit, shuffle, 0f, 0f, true), Is.True);
	}

	[Test]
	public void AStuckUnitThatMovesIsNoLongerStuck()
	{
		RouteStuckWatch watch = new RouteStuckWatch();
		watch.Update(0f, 0f, 0f, 0f, true);
		Assert.That(watch.Update(Limit + 1f, 0f, 0f, 0f, true), Is.True);
		Assert.That(watch.Update(Limit + 1.1f, 0f, 1f, 0f, true), Is.False);
		Assert.That(watch.Stuck, Is.False);
	}

	[Test]
	public void LosingTheRouteStartsTheWatchAgain()
	{
		RouteStuckWatch watch = new RouteStuckWatch();
		watch.Update(0f, 0f, 0f, 0f, true);
		Assert.That(watch.Update(Limit + 1f, 0f, 0f, 0f, true), Is.True);
		Assert.That(watch.Update(Limit + 2f, 0f, 0f, 0f, false), Is.False);
		// A new route at the same place: the wait starts now.
		Assert.That(watch.Update(Limit + 3f, 0f, 0f, 0f, true), Is.False);
		Assert.That(watch.Update(2f * Limit + 2.9f, 0f, 0f, 0f, true), Is.False);
		Assert.That(watch.Update(2f * Limit + 3f, 0f, 0f, 0f, true), Is.True);
	}
}
