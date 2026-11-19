using System.Collections;
using Brix.Engine;
using Brix.External.Factories;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Game.Semantique;
using CastleStoryPlus.Core;
using CastleStoryPlus.DevTools.Testing;
using UnityEngine;
using FactoryBlocks = Brix.External.Factories.Blocks;

namespace CastleStoryPlus.DevTools.Tests;

// Stockpile consolidation (#80): an idle builder empties the stockpile holding less of a resource into the fuller one,
// over as many trips as it takes, and leaves two stockpiles of about the same fill alone. Runs alone (no
// [ParallelTests]): consolidation looks at every stockpile of the team, and the test takes every stone brick out of
// the world first so no other stockpile joins in.
internal static class ConsolidationTests
{
	private static readonly Factory.AssetKey Brick = FactoryBlocks.Stone_DoubleBrick;

	private sealed class Piles
	{
		public Recepteur Small;

		public Recepteur Large;

		public Labor Worker;

		public int Hand;

		public int Room;
	}

	private static int Held(Recepteur pile)
	{
		int count = 0;
		if (pile == null)
		{
			return count;
		}
		foreach (IDescriptor item in pile.StoredItems)
		{
			if (item != null && item.GameObject != null && Scenario.Is(item.GameObject, Brick))
			{
				count++;
			}
		}
		return count;
	}

	// Two empty stockpiles six blocks apart and an idle builder beside them; no other worker of the player and no
	// other stone brick in the world.
	private static IEnumerator Setup(TestContext t, System.Action<Piles> made)
	{
		if (!Plugin.IsEnabled(Features.StockpileConsolidation))
		{
			t.Skip("StockpileConsolidation is switched off");
		}
		Scenario.SidelineOthers(t);
		t.Record("bricksRemoved", Scenario.RemoveAll(Brick));
		XYZ site = Scenario.Site(t, 12, 6);
		BuildProject build = Scenario.BuildProject(t, site);
		Piles piles = new Piles();
		yield return Scenario.Building(t, TaskTests.Pallet, new XYZ(site.x - 3, site.y, site.z), build, go => piles.Small = go.GetComponent<Recepteur>());
		yield return Scenario.Building(t, TaskTests.Pallet, new XYZ(site.x + 3, site.y, site.z), build, go => piles.Large = go.GetComponent<Recepteur>());
		t.NotNull(piles.Small, "The first stockpile's storage");
		t.NotNull(piles.Large, "The second stockpile's storage");
		piles.Worker = Scenario.Bricktron(t, Occupation.Type.Builder, new XYZ(site.x, site.y, site.z - 2));
		piles.Hand = piles.Worker.recepteur.CanFitUpTo(Brick);
		piles.Room = piles.Large.CanFitUpTo(Brick);
		t.Record("hand", piles.Hand);
		t.Record("room", piles.Room);
		made(piles);
	}

	private static void Fill(TestContext t, Recepteur pile, int count)
	{
		foreach (GameObject block in Scenario.Items(t, Brick, count, pile.transform.position + Vector3.up))
		{
			pile.AddObject(block);
		}
	}

	private static void Note(TestContext t, Piles piles)
	{
		Labor worker = piles.Worker;
		string doing = (worker == null) ? "no worker" : worker.name + " " + worker.Activity + ", task " + ((worker.CurrentTask != null) ? worker.CurrentTask.ToString() : "none") + ", bag " + worker.recepteur.ContentString();
		t.Log(doing + "; small " + Held(piles.Small) + ", large " + Held(piles.Large));
	}

	[GameTest("Stockpiles", "An idle builder empties a stockpile holding more stone bricks than one load into a clearly fuller one, over several trips (#80)", TimeoutSeconds = 300f)]
	private static IEnumerator SmallStockpileIsEmptiedOverSeveralTrips(TestContext t)
	{
		Piles piles = null;
		yield return Setup(t, made => piles = made);
		// More than two loads in the small one, the large one filled so the small one's bricks just fit in it.
		int small = Mathf.Min(2 * piles.Hand + 1, (piles.Room - 2) / 2);
		if (piles.Hand <= 0 || small <= piles.Hand)
		{
			t.Skip("A stockpile holds too few stone bricks (" + piles.Room + ") for more than one load (" + piles.Hand + ")");
		}
		int large = piles.Room - small;
		Fill(t, piles.Small, small);
		Fill(t, piles.Large, large);
		yield return t.Frames(3);
		t.Equal(small, Held(piles.Small), "Bricks in the small stockpile");
		t.Equal(large, Held(piles.Large), "Bricks in the large stockpile");
		yield return t.UntilGame(() => Held(piles.Small) == 0 && Held(piles.Large) == small + large, 240f, "the small stockpile to be emptied into the large one", 30f, () => Note(t, piles));
		t.Record("small", Held(piles.Small));
		t.Record("large", Held(piles.Large));
	}

	[GameTest("Stockpiles", "An idle builder leaves two stockpiles holding the same number of stone bricks alone (#80)", TimeoutSeconds = 120f)]
	private static IEnumerator EquallyFullStockpilesStayPut(TestContext t)
	{
		Piles piles = null;
		yield return Setup(t, made => piles = made);
		int each = Mathf.Max(1, Mathf.Min(piles.Hand + 1, piles.Room / 2));
		Fill(t, piles.Small, each);
		Fill(t, piles.Large, each);
		yield return t.Frames(3);
		yield return t.Seconds(40f);
		Note(t, piles);
		t.Equal(each, Held(piles.Small), "Bricks in the first stockpile after 40 game seconds");
		t.Equal(each, Held(piles.Large), "Bricks in the second stockpile after 40 game seconds");
	}
}
