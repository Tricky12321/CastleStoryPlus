using System.Collections.Generic;
using CastleStoryPlus.Pathfinding;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace CastleStoryPlus.UnitTests.Pathfinding;

[TestFixture]
public class NoGoRulesTests
{
	// ---- the price of a step

	[Test]
	public void AStepOutsideAZoneCostsNothingExtra()
	{
		Assert.That(NoGoRules.StepPenalty(0f, 0f, 1, 60), Is.EqualTo(0));
		Assert.That(NoGoRules.StepPenalty(1f, 0f, 1, 60), Is.EqualTo(0), "leaving a zone is free");
	}

	[Test]
	public void EnteringAZoneCostsStrengthBlocksPlusTheStepInside()
	{
		// 60 blocks to enter, and the step inside costs 3 plain steps instead of 1: 62 blocks of 15 weight.
		Assert.That(NoGoRules.StepPenalty(0f, 1f, 1, 60), Is.EqualTo(62 * NoGoRules.WeightPerBlock));
	}

	[Test]
	public void AStepInsideAZoneCostsTwoBlocksMore()
	{
		Assert.That(NoGoRules.StepPenalty(1f, 1f, 1, 60), Is.EqualTo((NoGoRules.InsideFactor - 1) * NoGoRules.WeightPerBlock));
	}

	[Test]
	public void ACoarsePlaceCostsByItsShareInAZone()
	{
		// An 8 x 8 place half in a zone, from a place outside: half the entry, and half of its 8 blocks at 2 extra.
		int expected = (int)System.Math.Round((0.5 * 60 + 0.5 * 8 * 2) * NoGoRules.WeightPerBlock);
		Assert.That(NoGoRules.StepPenalty(0f, 0.5f, 8, 60), Is.EqualTo(expected));
		// From a place a quarter in the zone only the quarter more is entered.
		int fromQuarter = (int)System.Math.Round((0.25 * 60 + 0.5 * 8 * 2) * NoGoRules.WeightPerBlock);
		Assert.That(NoGoRules.StepPenalty(0.25f, 0.5f, 8, 60), Is.EqualTo(fromQuarter));
	}

	[Test]
	public void TheStrengthIsKeptToItsRange()
	{
		Assert.That(NoGoRules.StepPenalty(0f, 1f, 1, 0), Is.EqualTo(NoGoRules.StepPenalty(0f, 1f, 1, NoGoRules.MinStrength)));
		Assert.That(NoGoRules.StepPenalty(0f, 1f, 1, 100000), Is.EqualTo(NoGoRules.StepPenalty(0f, 1f, 1, NoGoRules.MaxStrength)));
	}

	// The A*'s rank of a route (length * 10 + weight * 2 per step, PathAStarQueue): going round a zone must rank below
	// crossing it when the way round is not much longer, and above when it is very much longer (the zone stays open).
	private static int Rank(int flatSteps, int penalty)
	{
		return flatSteps * 30 + penalty * 2;
	}

	[Test]
	public void ARouteGoesRoundAZoneWhenTheWayRoundIsShort()
	{
		// Straight across: 17 steps, 7 of them in a 7 wide zone; round it: 17 + 10 steps.
		int through = Rank(17, NoGoRules.StepPenalty(0f, 1f, 1, 60) + 6 * NoGoRules.StepPenalty(1f, 1f, 1, 60));
		int round = Rank(27, 0);
		Assert.That(round, Is.LessThan(through));
	}

	[Test]
	public void ARouteCrossesAZoneWhenTheWayRoundIsFarTooLong()
	{
		// A 3 wide zone across a valley: round it is 200 steps more.
		int through = Rank(20, NoGoRules.StepPenalty(0f, 1f, 1, 60) + 2 * NoGoRules.StepPenalty(1f, 1f, 1, 60));
		int round = Rank(220, 0);
		Assert.That(through, Is.LessThan(round));
	}

	[Test]
	public void TheMeshCostIsNeverUnderTheFactor()
	{
		Assert.That(NoGoRules.MeshCost(60, 1f), Is.EqualTo(13f).Within(1e-5f));
		Assert.That(NoGoRules.MeshCost(60, 1.4f), Is.EqualTo(1.4f * 13f).Within(1e-4f));
		Assert.That(NoGoRules.MeshCost(NoGoRules.MinStrength, 0.5f), Is.GreaterThanOrEqualTo(1f));
	}

	// ---- rectangles

	[Test]
	public void ARectangleIsMadeFromCornersInAnyOrder()
	{
		NoGoRect rect = new NoGoRect(5, 9, -2, 3);
		Assert.That(rect.MinX, Is.EqualTo(-2));
		Assert.That(rect.MaxX, Is.EqualTo(5));
		Assert.That(rect.MinZ, Is.EqualTo(3));
		Assert.That(rect.MaxZ, Is.EqualTo(9));
		Assert.That(rect.Cells, Is.EqualTo(8 * 7));
	}

	[Test]
	public void ADrawnRectangleIsKeptToTheLongestSide()
	{
		NoGoRect rect = NoGoRules.Drawn(0, 0, 1000, -1000);
		Assert.That(rect.Width, Is.EqualTo(NoGoRules.MaxSide));
		Assert.That(rect.Depth, Is.EqualTo(NoGoRules.MaxSide));
		Assert.That(rect.MinX, Is.EqualTo(0));
		Assert.That(rect.MaxZ, Is.EqualTo(0));
	}

	private static long Cells(List<NoGoRect> zones)
	{
		long cells = 0;
		foreach (NoGoRect zone in zones)
		{
			cells += zone.Cells;
		}
		return cells;
	}

	private static bool Covers(List<NoGoRect> zones, int x, int z)
	{
		int count = 0;
		foreach (NoGoRect zone in zones)
		{
			if (zone.Contains(x, z))
			{
				count++;
			}
		}
		Assert.That(count, Is.LessThanOrEqualTo(1), "column " + x + "," + z + " is in two rectangles");
		return count == 1;
	}

	[Test]
	public void CuttingTheMiddleLeavesFourStrips()
	{
		List<NoGoRect> parts = new List<NoGoRect>();
		NoGoRules.Subtract(new NoGoRect(0, 0, 9, 9), new NoGoRect(3, 3, 5, 5), parts);
		Assert.That(parts.Count, Is.EqualTo(4));
		Assert.That(Cells(parts), Is.EqualTo(100 - 9));
		for (int x = 0; x <= 9; x++)
		{
			for (int z = 0; z <= 9; z++)
			{
				bool cut = x >= 3 && x <= 5 && z >= 3 && z <= 5;
				Assert.That(Covers(parts, x, z), Is.EqualTo(!cut), x + "," + z);
			}
		}
	}

	[Test]
	public void CuttingAllOfItLeavesNothing()
	{
		List<NoGoRect> parts = new List<NoGoRect>();
		NoGoRules.Subtract(new NoGoRect(2, 2, 4, 4), new NoGoRect(0, 0, 9, 9), parts);
		Assert.That(parts, Is.Empty);
	}

	[Test]
	public void CuttingBesideItLeavesItWhole()
	{
		List<NoGoRect> parts = new List<NoGoRect>();
		NoGoRules.Subtract(new NoGoRect(0, 0, 4, 4), new NoGoRect(5, 0, 9, 4), parts);
		Assert.That(parts, Is.EqualTo(new[] { new NoGoRect(0, 0, 4, 4) }));
	}

	[Test]
	public void OverlappingZonesShareNoColumn()
	{
		List<NoGoRect> zones = new List<NoGoRect>();
		Assert.That(NoGoRules.Add(zones, new NoGoRect(0, 0, 9, 9)), Is.True);
		Assert.That(NoGoRules.Add(zones, new NoGoRect(5, 5, 14, 14)), Is.True);
		Assert.That(Cells(zones), Is.EqualTo(100 + 100 - 25));
		for (int x = -1; x <= 15; x++)
		{
			for (int z = -1; z <= 15; z++)
			{
				bool inside = (x >= 0 && x <= 9 && z >= 0 && z <= 9) || (x >= 5 && x <= 14 && z >= 5 && z <= 14);
				Assert.That(Covers(zones, x, z), Is.EqualTo(inside), x + "," + z);
			}
		}
	}

	[Test]
	public void ErasingTakesOnlyTheRectangleOut()
	{
		List<NoGoRect> zones = new List<NoGoRect>();
		NoGoRules.Add(zones, new NoGoRect(0, 0, 9, 9));
		Assert.That(NoGoRules.Erase(zones, new NoGoRect(0, 0, 9, 4)), Is.True);
		Assert.That(zones, Is.EqualTo(new[] { new NoGoRect(0, 5, 9, 9) }));
		Assert.That(NoGoRules.Erase(zones, new NoGoRect(20, 20, 30, 30)), Is.False, "nothing there to erase");
		Assert.That(NoGoRules.Erase(zones, new NoGoRect(-5, -5, 50, 50)), Is.True);
		Assert.That(zones, Is.Empty);
	}

	[Test]
	public void TooManyRectanglesAreRefusedAndNothingChanges()
	{
		List<NoGoRect> zones = new List<NoGoRect>();
		for (int i = 0; i < NoGoRules.MaxRects; i++)
		{
			Assert.That(NoGoRules.Add(zones, new NoGoRect(i * 3, 0, i * 3, 0)), Is.True);
		}
		List<NoGoRect> before = new List<NoGoRect>(zones);
		Assert.That(NoGoRules.Add(zones, new NoGoRect(1000, 0, 1000, 0)), Is.False);
		Assert.That(zones, Is.EqualTo(before));
		// One that only covers others already there makes no more.
		Assert.That(NoGoRules.Add(zones, new NoGoRect(-1, 0, 10, 0)), Is.True);
	}

	// ---- the map the routes read

	[Test]
	public void TheMapKnowsEveryColumnOfItsRectanglesAlsoBelowZero()
	{
		NoGoMap map = new NoGoMap();
		map.Add(new NoGoRect(-20, -3, -14, 2));
		map.Add(new NoGoRect(15, 15, 17, 17));
		for (int x = -25; x <= 20; x++)
		{
			for (int z = -8; z <= 20; z++)
			{
				bool inside = (x >= -20 && x <= -14 && z >= -3 && z <= 2) || (x >= 15 && x <= 17 && z >= 15 && z <= 17);
				Assert.That(map.Contains(x, z), Is.EqualTo(inside), x + "," + z);
			}
		}
	}

	[Test]
	public void CoverageIsTheShareOfTheSquareInTheZones()
	{
		NoGoMap map = new NoGoMap();
		Assert.That(map.IsEmpty, Is.True);
		map.Add(new NoGoRect(0, 0, 7, 3));
		Assert.That(map.Coverage(0, 0, 8), Is.EqualTo(0.5f).Within(1e-6f));
		Assert.That(map.Coverage(0, 0, 4), Is.EqualTo(1f).Within(1e-6f));
		Assert.That(map.Coverage(2, 3, 2), Is.EqualTo(0.5f).Within(1e-6f));
		Assert.That(map.Coverage(6, 2, 4), Is.EqualTo(0.25f).Within(1e-6f));
		Assert.That(map.Coverage(100, 100, 16), Is.EqualTo(0f));
		Assert.That(map.Coverage(3, 3, 1), Is.EqualTo(1f));
		Assert.That(map.Coverage(3, 4, 1), Is.EqualTo(0f));
	}

	[Test]
	public void CoverageOfAlliesOverlappingZonesIsNeverMoreThanAll()
	{
		NoGoMap map = new NoGoMap();
		map.Add(new NoGoRect(0, 0, 15, 15));
		map.Add(new NoGoRect(0, 0, 15, 15));
		Assert.That(map.Coverage(0, 0, 16), Is.EqualTo(1f));
	}

	// ---- saved

	[Test]
	public void ZonesAreSavedAndReadBack()
	{
		Dictionary<string, List<NoGoRect>> zones = new Dictionary<string, List<NoGoRect>>
		{
			["Player1"] = new List<NoGoRect> { new NoGoRect(1, 2, 3, 4), new NoGoRect(-10, -10, -5, -6) },
			["Player2"] = new List<NoGoRect> { new NoGoRect(100, 100, 120, 101) },
			["Nobody"] = new List<NoGoRect>()
		};
		JObject json = NoGoRules.ToJson(zones);
		Assert.That(json.ContainsKey("Nobody"), Is.False, "a faction without zones is not saved");
		Dictionary<string, List<NoGoRect>> read = NoGoRules.FromJson(JObject.Parse(json.ToString()));
		Assert.That(read.Keys, Is.EquivalentTo(new[] { "Player1", "Player2" }));
		Assert.That(read["Player1"], Is.EquivalentTo(zones["Player1"]));
		Assert.That(read["Player2"], Is.EquivalentTo(zones["Player2"]));
	}

	[Test]
	public void AMalformedSaveLosesOnlyWhatIsMalformed()
	{
		JObject json = JObject.Parse("{\"Player1\": [[1, 2, 3, 4], [1, 2, 3], \"x\", [0.5, 1, 2, 3], [5, 5, 6, 6]], \"Player2\": 7, \"Player3\": []}");
		Dictionary<string, List<NoGoRect>> read = NoGoRules.FromJson(json);
		Assert.That(read.Keys, Is.EquivalentTo(new[] { "Player1" }));
		Assert.That(read["Player1"], Is.EquivalentTo(new[] { new NoGoRect(1, 2, 3, 4), new NoGoRect(5, 5, 6, 6) }));
		Assert.That(NoGoRules.FromJson(null), Is.Empty);
	}
}
