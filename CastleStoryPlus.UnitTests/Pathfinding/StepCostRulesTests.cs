using CastleStoryPlus.Pathfinding;
using NUnit.Framework;

namespace CastleStoryPlus.UnitTests.Pathfinding;

[TestFixture]
public class StepCostRulesTests
{
	private const int Up = StepCostRules.DefaultClimbPenalty;

	private const int Down = StepCostRules.DefaultStepDownPenalty;

	// The game's own weights (TerrainPathNode.TryAddRegularNeighborBranch) at its penalties.
	[TestCase(0, 10)]
	[TestCase(1, 50)]
	[TestCase(2, 90)]
	[TestCase(3, 130)]
	[TestCase(4, 170)]
	[TestCase(9, 170)]
	[TestCase(-1, 70)]
	[TestCase(-2, 130)]
	[TestCase(-3, 190)]
	[TestCase(-4, 250)]
	[TestCase(-6, 250)]
	[TestCase(-9, 250)]
	public void GamePenaltiesGiveTheGamesWeights(int denivelation, int weight)
	{
		Assert.That(StepCostRules.Weight(denivelation, StepCostRules.GameClimbPenalty, StepCostRules.GameStepDownPenalty), Is.EqualTo(weight));
	}

	[Test]
	public void FlatStepIsTenWhateverThePenalties()
	{
		Assert.That(StepCostRules.Weight(0, Up, Down), Is.EqualTo(StepCostRules.FlatWeight));
		Assert.That(StepCostRules.Weight(0, 0, 0), Is.EqualTo(StepCostRules.FlatWeight));
		Assert.That(StepCostRules.Weight(0, StepCostRules.MaxPenalty, StepCostRules.MaxPenalty), Is.EqualTo(StepCostRules.FlatWeight));
	}

	// A climb onto a block (both corners a full block up: -6) at the defaults: about 5 blocks of walking in the A*'s
	// rank (length * 10 + weight * 2), against 25 at the game's penalty.
	[Test]
	public void ClimbOntoABlockCostsAboutFiveFlatSteps()
	{
		int climb = StepCostRules.Weight(-6, Up, Down);
		Assert.That(climb, Is.EqualTo(70));
		int flatRank = 10 + StepCostRules.FlatWeight * 2;
		int climbRank = 10 + climb * 2;
		Assert.That(climbRank / flatRank, Is.EqualTo(5));
		int gameClimbRank = 10 + StepCostRules.Weight(-6, StepCostRules.GameClimbPenalty, StepCostRules.GameStepDownPenalty) * 2;
		Assert.That(gameClimbRank / flatRank, Is.EqualTo(17));
	}

	[Test]
	public void DropOffABlockCostsAboutThreeFlatSteps()
	{
		int drop = StepCostRules.Weight(6, Up, Down);
		Assert.That(drop, Is.EqualTo(40));
		Assert.That((10 + drop * 2) / (10 + StepCostRules.FlatWeight * 2), Is.EqualTo(3));
	}

	// A smaller step never costs more than the climb or drop of a whole block, at any penalties.
	[Test]
	public void PartialStepsNeverCostMoreThanTheWholeStep()
	{
		int[] penalties = { 0, 1, 7, 30, 60, 160, 240, 400 };
		foreach (int up in penalties)
		{
			foreach (int down in penalties)
			{
				int previousUp = StepCostRules.FlatWeight;
				int previousDown = StepCostRules.FlatWeight;
				for (int d = 1; d < 10; d++)
				{
					int stepUp = StepCostRules.Weight(-d, up, down);
					int stepDown = StepCostRules.Weight(d, up, down);
					Assert.That(stepUp, Is.GreaterThanOrEqualTo(previousUp), "up " + d + " at " + up);
					Assert.That(stepDown, Is.GreaterThanOrEqualTo(previousDown), "down " + d + " at " + down);
					Assert.That(stepUp, Is.LessThanOrEqualTo(StepCostRules.FlatWeight + up));
					Assert.That(stepDown, Is.LessThanOrEqualTo(StepCostRules.FlatWeight + down));
					previousUp = stepUp;
					previousDown = stepDown;
				}
			}
		}
	}

	// A low wall of player blocks (one high) between two places 6 apart, open at its end 7 to the side: over it is a
	// climb, a drop and 4 flat steps; round it 20 flat steps. The game's weights went round, the defaults go over.
	[Test]
	public void LowWallIsSteppedOverAtTheDefaultsAndWalkedRoundAtTheGamesWeights()
	{
		int round = 20 * StepCostRules.FlatWeight;
		int overGame = 4 * StepCostRules.FlatWeight + StepCostRules.Weight(-6, StepCostRules.GameClimbPenalty, StepCostRules.GameStepDownPenalty) + StepCostRules.Weight(6, StepCostRules.GameClimbPenalty, StepCostRules.GameStepDownPenalty);
		int overDefault = 4 * StepCostRules.FlatWeight + StepCostRules.Weight(-6, Up, Down) + StepCostRules.Weight(6, Up, Down);
		Assert.That(overGame, Is.GreaterThan(round));
		Assert.That(overDefault, Is.LessThan(round));
	}

	[TestCase(-4, true)]
	[TestCase(-6, true)]
	[TestCase(-3, false)]
	[TestCase(0, false)]
	[TestCase(4, false)]
	public void ClimbFromFourThirdsUp(int denivelation, bool climb)
	{
		Assert.That(StepCostRules.IsClimb(denivelation), Is.EqualTo(climb));
	}

	[TestCase(4, true)]
	[TestCase(6, true)]
	[TestCase(3, false)]
	[TestCase(0, false)]
	[TestCase(-4, false)]
	public void StepDownFromFourThirdsDown(int denivelation, bool stepDown)
	{
		Assert.That(StepCostRules.IsStepDown(denivelation), Is.EqualTo(stepDown));
	}

	[TestCase(-5, 0)]
	[TestCase(0, 0)]
	[TestCase(60, 60)]
	[TestCase(400, 400)]
	[TestCase(401, 400)]
	[TestCase(int.MaxValue, 400)]
	[TestCase(int.MinValue, 0)]
	public void PenaltyIsKeptToTheRange(int penalty, int clamped)
	{
		Assert.That(StepCostRules.Clamp(penalty), Is.EqualTo(clamped));
	}

	[Test]
	public void SteepWeightIsTheGamesHundredAtTheGamesPenalty()
	{
		Assert.That(StepCostRules.SteepWeight(StepCostRules.GameClimbPenalty), Is.EqualTo(100));
	}

	// The steps NavMeshRoutes.Ease looks at: the same at the defaults as at the game's penalties (up 2 thirds or more,
	// down 3 or more).
	[Test]
	public void SameStepsAreSteepAtTheDefaultsAsAtTheGames()
	{
		int gameSteep = StepCostRules.SteepWeight(StepCostRules.GameClimbPenalty);
		int steep = StepCostRules.SteepWeight(Up);
		for (int d = -3; d <= 3; d++)
		{
			bool game = StepCostRules.Weight(d, StepCostRules.GameClimbPenalty, StepCostRules.GameStepDownPenalty) >= gameSteep;
			bool now = StepCostRules.Weight(d, Up, Down) >= steep;
			Assert.That(now, Is.EqualTo(game), "denivelation " + d);
		}
	}

	[Test]
	public void FlatStepIsNeverSteep()
	{
		Assert.That(StepCostRules.SteepWeight(0), Is.GreaterThan(StepCostRules.FlatWeight));
		Assert.That(StepCostRules.SteepWeight(1), Is.GreaterThan(StepCostRules.FlatWeight));
	}
}
