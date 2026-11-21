using System.Collections.Generic;
using CastleStoryPlus.WorkerAI;
using NUnit.Framework;

namespace CastleStoryPlus.UnitTests.WorkerAI;

[TestFixture]
public class QuarryStairRulesTests
{
	// Two steps of a stair going down along x: (5, 10, 3) and (6, 9, 3).
	private static readonly HashSet<(int, int, int)> Steps = new HashSet<(int, int, int)> { (5, 10, 3), (6, 9, 3) };

	private static bool IsStep(int x, int y, int z)
	{
		return Steps.Contains((x, y, z));
	}

	[TestCase(5, 10, 3)]
	[TestCase(6, 9, 3)]
	public void TheStepIsKept(int x, int y, int z)
	{
		Assert.That(QuarryStairRules.Keeps(x, y, z, IsStep), Is.True);
	}

	[TestCase(5, 9, 3)]
	[TestCase(6, 8, 3)]
	public void TheGroundUnderAStepIsKept(int x, int y, int z)
	{
		Assert.That(QuarryStairRules.Keeps(x, y, z, IsStep), Is.True);
	}

	[TestCase(5, 8, 3)]
	[TestCase(5, 11, 3)]
	[TestCase(6, 10, 3)]
	[TestCase(4, 10, 3)]
	[TestCase(5, 10, 4)]
	[TestCase(6, 7, 3)]
	public void OtherGroundIsNotKept(int x, int y, int z)
	{
		Assert.That(QuarryStairRules.Keeps(x, y, z, IsStep), Is.False);
	}

	[TestCase(4, 10, 4)]
	[TestCase(2, 5, 1)]
	[TestCase(2, 4, 1)]
	public void InTheBoxOrRightUnderItIsNear(int x, int y, int z)
	{
		Assert.That(QuarryStairRules.Near(x, y, z, 0, 5, 0, 4, 10, 4), Is.True);
	}

	[TestCase(5, 7, 2)]
	[TestCase(-1, 7, 2)]
	[TestCase(2, 11, 2)]
	[TestCase(2, 3, 2)]
	[TestCase(2, 7, 5)]
	public void OutsideTheBoxIsNotNear(int x, int y, int z)
	{
		Assert.That(QuarryStairRules.Near(x, y, z, 0, 5, 0, 4, 10, 4), Is.False);
	}

	[Test]
	public void AWholeStairGoesDownToItsLastStep()
	{
		Assert.That(QuarryStairRules.LowestStep(new[] { 130, 129, 128, 127, 126, 125 }, new bool[6]), Is.EqualTo(125));
	}

	[Test]
	public void AStairEndsAboveAStepDugAway()
	{
		// The save: step (445, 125, 434) dug away, the steps under it cannot be walked down to.
		bool[] gone = { false, false, false, false, true, false };
		Assert.That(QuarryStairRules.LowestStep(new[] { 130, 129, 128, 127, 126, 125 }, gone), Is.EqualTo(127));
	}

	[Test]
	public void NoStepsHoldNoFloorBack()
	{
		Assert.That(QuarryStairRules.LowestStep(new int[0], new bool[0]), Is.EqualTo(QuarryStairRules.NoStep));
		Assert.That(QuarryStairRules.TooDeep(-500, QuarryStairRules.NoStep), Is.False);
	}

	[TestCase(127, false)]
	[TestCase(126, false)]
	[TestCase(125, true)]
	[TestCase(123, true)]
	public void TheFloorGoesOneUnderTheLowestStepAtMost(int y, bool tooDeep)
	{
		Assert.That(QuarryStairRules.TooDeep(y, 127), Is.EqualTo(tooDeep));
	}
}
