using CastleStoryPlus.WorkerAI;
using NUnit.Framework;

namespace CastleStoryPlus.UnitTests.WorkerAI;

[TestFixture]
public class EscapeRulesTests
{
	// A worker with its feet at (10, 5, 10).
	[TestCase(10, 4, 10)]
	[TestCase(10, 7, 10)]
	[TestCase(11, 4, 10)]
	[TestCase(11, 5, 10)]
	[TestCase(9, 6, 10)]
	[TestCase(10, 7, 11)]
	public void ABlockBesideOrOverTheWorkerCanBeDug(int x, int y, int z)
	{
		Assert.That(EscapeRules.CanDigFrom(10, 5, 10, x, y, z), Is.True);
	}

	// The save's case: a block five over the worker and further off (444, 128, 436 from 449, 123, 441).
	[TestCase(444, 128, 436)]
	[TestCase(450, 126, 441)]
	[TestCase(450, 121, 441)]
	[TestCase(450, 123, 442)]
	[TestCase(451, 123, 441)]
	[TestCase(449, 124, 441)]
	public void ABlockOutOfReachCannotBeDug(int x, int y, int z)
	{
		Assert.That(EscapeRules.CanDigFrom(449, 123, 441, x, y, z), Is.False);
	}

	[Test]
	public void TheSideIsToTheLeftOfTheWay()
	{
		EscapeRules.Side(1, 0, out int sx, out int sz);
		Assert.That((sx, sz), Is.EqualTo((0, 1)));
		EscapeRules.Side(0, 1, out sx, out sz);
		Assert.That((sx, sz), Is.EqualTo((-1, 0)));
		EscapeRules.Side(-1, 0, out sx, out sz);
		Assert.That((sx, sz), Is.EqualTo((0, -1)));
	}

	[TestCase(0, 0, 3, 0, 3, true)]
	[TestCase(0, 0, 2, 2, 3, false)]
	[TestCase(0, 0, -1, 4, 3, true)]
	[TestCase(5, 5, 5, 5, 1, false)]
	public void FarFromTakesTheLargerDistance(int ax, int az, int bx, int bz, int min, bool far)
	{
		Assert.That(EscapeRules.FarFrom(ax, az, bx, bz, min), Is.EqualTo(far));
	}

	[TestCase(true, true, false, true)]
	[TestCase(true, false, true, true)]
	[TestCase(true, false, false, false)]
	[TestCase(false, false, false, true)]
	[TestCase(false, true, false, true)]
	public void AnOpenStepLeavesThePocketOnlyOntoTheWayOut(bool fromInPocket, bool toInPocket, bool toIsExit, bool walk)
	{
		Assert.That(EscapeRules.MayWalk(fromInPocket, toInPocket, toIsExit), Is.EqualTo(walk));
	}

	[TestCase(100f, 50f, 0, 0, 0, true)]
	[TestCase(100f, 50f, 4, -4, 4, true)]
	[TestCase(100f, 50f, 5, 0, 0, false)]
	[TestCase(100f, 50f, 0, -5, 0, false)]
	[TestCase(100f, 100f, 0, 0, 0, false)]
	[TestCase(100f, 120f, 0, 0, 0, false)]
	public void AStorageOutOfReachIsPassedByFromNearbyForAWhile(float until, float now, int dx, int dy, int dz, bool unreachable)
	{
		Assert.That(EscapeRules.StoreStillUnreachable(until, now, dx, dy, dz), Is.EqualTo(unreachable));
	}

	[Test]
	public void ALoadIsPutDownOnlyOnOpenGroundOfTheRoutes()
	{
		Assert.That(EscapeRules.CanDropAt(true, true, false, false, true), Is.True);
		Assert.That(EscapeRules.CanDropAt(false, true, false, false, true), Is.False, "a climb (no ground under the feet)");
		Assert.That(EscapeRules.CanDropAt(true, false, false, false, true), Is.False, "in a block (a stair step)");
		Assert.That(EscapeRules.CanDropAt(true, true, true, false, true), Is.False, "on a stair");
		Assert.That(EscapeRules.CanDropAt(true, true, false, true, true), Is.False, "on a quarry's floor");
		Assert.That(EscapeRules.CanDropAt(true, true, false, false, false), Is.False, "off the routes (a pit)");
	}
}
