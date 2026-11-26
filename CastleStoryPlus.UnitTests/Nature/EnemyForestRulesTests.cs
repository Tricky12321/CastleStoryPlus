using CastleStoryPlus.Nature;
using NUnit.Framework;

namespace CastleStoryPlus.UnitTests.Nature;

[TestFixture]
public class EnemyForestRulesTests
{
	private const int Penalty = EnemyForestRules.DefaultStepPenalty;

	[Test]
	public void OpenGroundKeepsItsWeight()
	{
		Assert.That(EnemyForestRules.StepWeight(130, false, 0, Penalty), Is.EqualTo(130));
		Assert.That(EnemyForestRules.StepWeight(130, false, 3, Penalty), Is.EqualTo(130));
	}

	[Test]
	public void ForestStepPaysThePenalty()
	{
		Assert.That(EnemyForestRules.StepWeight(130, true, 0, Penalty), Is.EqualTo(130 + Penalty));
	}

	[Test]
	public void NoPenaltyIsTheGame()
	{
		Assert.That(EnemyForestRules.StepWeight(130, true, 0, 0), Is.EqualTo(130));
		Assert.That(EnemyForestRules.StepWeight(130, true, 2, -5), Is.EqualTo(130));
	}

	// A coarse place of level n spans 2^n columns, so its step pays for that many voxels (up to level 6).
	[TestCase(1, 2)]
	[TestCase(2, 4)]
	[TestCase(4, 16)]
	[TestCase(6, 64)]
	[TestCase(9, 64)]
	[TestCase(-1, 1)]
	public void CoarseStepsPayForTheirSize(int lod, int voxels)
	{
		Assert.That(EnemyForestRules.StepWeight(0, true, lod, Penalty), Is.EqualTo(Penalty * voxels));
	}

	// In the A*'s rank (length * 10 + weight * 2) a forest step at the default costs about 4 flat steps of open ground.
	[Test]
	public void DefaultForestStepRanksAsSeveralFlatSteps()
	{
		int flat = 10 + 2 * EnemyForestRules.GameForestWeight;
		int forest = 10 + 2 * EnemyForestRules.StepWeight(EnemyForestRules.GameForestWeight, true, 0, Penalty);
		Assert.That(forest, Is.GreaterThanOrEqualTo(4 * flat));
		Assert.That(forest, Is.LessThan(6 * flat));
	}

	// A strip 5 blocks deep crossed straight on a way 13 long, against going 4 blocks out to a lane beside it and 4 back:
	// the way round is cheaper at the default, the forest at the game's weights (#209).
	[Test]
	public void LaneRoundAStripBeatsCuttingThroughIt()
	{
		int length = 13;
		int depth = 5;
		int detour = 8;
		int straight = (length - depth) * EnemyForestRules.GameForestWeight + depth * EnemyForestRules.StepWeight(EnemyForestRules.GameForestWeight, true, 0, Penalty);
		int round = (length + detour) * EnemyForestRules.GameForestWeight;
		Assert.That(round, Is.LessThan(straight));
		int straightAsGame = length * EnemyForestRules.GameForestWeight;
		Assert.That(straightAsGame, Is.LessThan(round));
	}

	[TestCase(-3, 0)]
	[TestCase(0, 0)]
	[TestCase(50, 50)]
	[TestCase(900, 500)]
	public void PenaltyIsClamped(int penalty, int clamped)
	{
		Assert.That(EnemyForestRules.Clamp(penalty), Is.EqualTo(clamped));
	}

	[Test]
	public void OutsideAForestNothingHappens()
	{
		for (int still = 0; still < 40; still++)
		{
			Assert.That(EnemyForestRules.Decide(still, false, true, true), Is.EqualTo(EnemyForestRules.StuckAction.None));
			Assert.That(EnemyForestRules.Decide(still, false, false, true), Is.EqualTo(EnemyForestRules.StuckAction.None));
		}
	}

	[Test]
	public void ATreeInTheWayIsStruckAfterASecond()
	{
		Assert.That(EnemyForestRules.Decide(EnemyForestRules.StrikeChecks - 1, true, true, true), Is.EqualTo(EnemyForestRules.StuckAction.None));
		Assert.That(EnemyForestRules.Decide(EnemyForestRules.StrikeChecks, true, true, true), Is.EqualTo(EnemyForestRules.StuckAction.Strike));
		Assert.That(EnemyForestRules.Decide(EnemyForestRules.StrikeChecks + 1, true, true, false), Is.EqualTo(EnemyForestRules.StuckAction.None));
	}

	[Test]
	public void NoTreeToStrikeSearchesTheRouteAgain()
	{
		Assert.That(EnemyForestRules.Decide(EnemyForestRules.RepathChecks - 1, true, false, true), Is.EqualTo(EnemyForestRules.StuckAction.None));
		Assert.That(EnemyForestRules.Decide(EnemyForestRules.RepathChecks, true, false, true), Is.EqualTo(EnemyForestRules.StuckAction.Repath));
	}

	[Test]
	public void StrikesThatDoNotHelpEndInANewRoute()
	{
		Assert.That(EnemyForestRules.Decide(EnemyForestRules.StrikeRepathChecks - 1, true, true, true), Is.EqualTo(EnemyForestRules.StuckAction.Strike));
		Assert.That(EnemyForestRules.Decide(EnemyForestRules.StrikeRepathChecks, true, true, true), Is.EqualTo(EnemyForestRules.StuckAction.Repath));
		Assert.That(EnemyForestRules.Decide(EnemyForestRules.StrikeRepathChecks, true, true, false), Is.EqualTo(EnemyForestRules.StuckAction.Repath));
	}

	// 180 HP at 60 a strike every 1.2 s: three strikes from the first at StrikeChecks (0.5 s a check). The route is
	// searched again only well after such a tree has fallen.
	[Test]
	public void ATreeFallsBeforeTheRouteIsGivenUp()
	{
		float fallsAt = EnemyForestRules.StrikeChecks * 0.5f + 2 * 1.2f;
		Assert.That(EnemyForestRules.StrikeRepathChecks * 0.5f, Is.GreaterThan(fallsAt + 2f));
	}
}
