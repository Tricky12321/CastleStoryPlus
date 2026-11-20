using System.Collections.Generic;
using CastleStoryPlus.Building;
using NUnit.Framework;

namespace CastleStoryPlus.UnitTests.Building;

[TestFixture]
public class BlockSupportRulesTests
{
	[Test]
	public void HalfTheFootprintRoundedUpMustHold()
	{
		Assert.That(BlockSupportRules.RequiredSupport(1, 1), Is.EqualTo(1));
		Assert.That(BlockSupportRules.RequiredSupport(1, 2), Is.EqualTo(1));
		Assert.That(BlockSupportRules.RequiredSupport(1, 3), Is.EqualTo(2));
		Assert.That(BlockSupportRules.RequiredSupport(1, 4), Is.EqualTo(2));
		Assert.That(BlockSupportRules.RequiredSupport(1, 5), Is.EqualTo(3));
		Assert.That(BlockSupportRules.RequiredSupport(2, 2), Is.EqualTo(2));
		Assert.That(BlockSupportRules.RequiredSupport(2, 3), Is.EqualTo(3));
		Assert.That(BlockSupportRules.RequiredSupport(2, 4), Is.EqualTo(4));
	}

	// #145: a 1 x 4 brick has 4 cells below and 2 beyond its ends.
	[Test]
	public void ALongBrickTouchingStoneWithOneEndDoesNotHold()
	{
		int maxMissing = BlockSupportRules.MaxMissingSupport(6, 1, 4);
		Assert.That(BlockSupportRules.EnoughSupport(1, 6, maxMissing), Is.False);
	}

	[Test]
	public void ALongBrickBridgingAGapWithBothEndsHolds()
	{
		Assert.That(BlockSupportRules.EnoughSupport(2, 6, BlockSupportRules.MaxMissingSupport(6, 1, 4)), Is.True);
		// A 1 x 3 brick: 3 below and 2 ends.
		Assert.That(BlockSupportRules.EnoughSupport(2, 5, BlockSupportRules.MaxMissingSupport(5, 1, 3)), Is.True);
	}

	[Test]
	public void ALongBrickHalfOnAWallHolds()
	{
		Assert.That(BlockSupportRules.EnoughSupport(2, 6, BlockSupportRules.MaxMissingSupport(6, 1, 4)), Is.True);
	}

	// #141: a 1 x 5 slab has 5 cells below and 12 round it at its own height.
	[Test]
	public void ALongSlabTouchingStoneWithOneEndDoesNotHold()
	{
		int maxMissing = BlockSupportRules.MaxMissingSupport(17, 1, 5);
		Assert.That(BlockSupportRules.EnoughSupport(1, 17, maxMissing), Is.False);
		Assert.That(BlockSupportRules.EnoughSupport(3, 17, maxMissing), Is.True);
	}

	[Test]
	public void ASmallSlabStillHoldsOnOneCell()
	{
		// A 1 x 1 slab: 1 below and 4 round it; a 2 x 1 slab: 2 below and 6 round it.
		Assert.That(BlockSupportRules.EnoughSupport(1, 5, BlockSupportRules.MaxMissingSupport(5, 1, 1)), Is.True);
		Assert.That(BlockSupportRules.EnoughSupport(1, 8, BlockSupportRules.MaxMissingSupport(8, 1, 2)), Is.True);
	}

	[Test]
	public void FewerSupportCellsThanRequiredNeedsAllOfThem()
	{
		Assert.That(BlockSupportRules.MaxMissingSupport(1, 2, 4), Is.EqualTo(0));
	}

	// A placed block keeps the old rule (one supported cell), so castles of existing saves stand when loaded; a placed
	// block with no supported cell still falls.
	[Test]
	public void APlacedBlockHoldsOnOneSupportedCell()
	{
		Assert.That(BlockSupportRules.EnoughSupport(1, 6, BlockSupportRules.PlacedMaxMissingSupport(6)), Is.True);
		Assert.That(BlockSupportRules.EnoughSupport(0, 6, BlockSupportRules.PlacedMaxMissingSupport(6)), Is.False);
		Assert.That(BlockSupportRules.PlacedMaxMissingSupport(0), Is.EqualTo(0));
	}

	// The blueprint's need is never weaker than the placed block's rule: a block placed now still stands when built.
	[Test]
	public void ABlueprintNeedsAtLeastWhatAPlacedBlockNeeds()
	{
		for (int width = 1; width <= 2; width++)
		{
			for (int depth = 1; depth <= 5; depth++)
			{
				for (int cells = 1; cells <= 20; cells++)
				{
					Assert.That(BlockSupportRules.MaxMissingSupport(cells, width, depth), Is.LessThanOrEqualTo(BlockSupportRules.PlacedMaxMissingSupport(cells)));
				}
			}
		}
	}

	// ---- hanging wood: cells on a line along x (z = 0), coded as x.

	private static IEnumerable<int> Line(int x)
	{
		yield return x - 1;
		yield return x + 1;
	}

	private static bool Hangs(int[] self, int[] wood, int[] standing, int max)
	{
		HashSet<int> woodSet = new HashSet<int>(wood);
		HashSet<int> standingSet = new HashSet<int>(standing);
		return BlockSupportRules.HangsWithin(self, Line, woodSet.Contains, standingSet.Contains, max);
	}

	[Test]
	public void ALogNextToAStandingLogHangs()
	{
		Assert.That(Hangs(new[] { 1 }, new[] { 0 }, new[] { 0 }, 5), Is.True);
	}

	[Test]
	public void ALogFiveOutHangsAndSixOutDoesNot()
	{
		Assert.That(Hangs(new[] { 5 }, new[] { 0, 1, 2, 3, 4 }, new[] { 0 }, 5), Is.True);
		Assert.That(Hangs(new[] { 6 }, new[] { 0, 1, 2, 3, 4, 5 }, new[] { 0 }, 5), Is.False);
	}

	[Test]
	public void ALongSlabCountsItsOwnLength()
	{
		// A 1 x 5 slab in line with a standing slab end at 0: its far end is 5 out.
		Assert.That(Hangs(new[] { 1, 2, 3, 4, 5 }, new[] { 0 }, new[] { 0 }, 5), Is.True);
		// A second 1 x 5 slab beyond the first: its far end is 10 out (#141).
		Assert.That(Hangs(new[] { 6, 7, 8, 9, 10 }, new[] { 0, 1, 2, 3, 4, 5 }, new[] { 0 }, 5), Is.False);
	}

	[Test]
	public void WoodThatDoesNotStandHoldsNothing()
	{
		Assert.That(Hangs(new[] { 2 }, new[] { 0, 1 }, new int[0], 5), Is.False);
	}

	[Test]
	public void AGapInTheWoodBreaksTheChain()
	{
		Assert.That(Hangs(new[] { 3 }, new[] { 0, 1 }, new[] { 0 }, 5), Is.False);
	}

	[Test]
	public void ASlabBetweenTwoStandingWoodsMayBeLongerThanTheOverhang()
	{
		// A 1 x 5 slab between standing wood at 0 and 6: no cell is more than 3 from either end.
		Assert.That(Hangs(new[] { 1, 2, 3, 4, 5 }, new[] { 0, 6 }, new[] { 0, 6 }, 3), Is.True);
		Assert.That(Hangs(new[] { 1, 2, 3, 4, 5 }, new[] { 0 }, new[] { 0 }, 3), Is.False);
	}
}
