using System;
using System.Collections;
using Brix.Engine;
using Brix.External.Factories;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Game.Semantique;
using CastleStoryPlus.DevTools.Testing;
using UnityEngine;
using FactoryBlocks = Brix.External.Factories.Blocks;
using Object = UnityEngine.Object;

namespace CastleStoryPlus.DevTools.Tests;

// Workshops: a worker fetches the ingredients from a stockpile, works the recipe and the product is made (and
// stored again), through the game's own crafting commands as the workshop's menu sends them. Without ingredients or
// without a worker nothing is made.
[ParallelTests("Economy")]
internal static class CraftingTests
{
	private sealed class Shop
	{
		public XYZ Site;

		public Recepteur Stockpile;

		public CraftingStation Station;

		public Labor Worker;
	}

	private static int Planks => Scenario.Count(FactoryBlocks.Wood_DoublePlank_AA);

	[GameTest("Crafting", "A chopping block with a worker and logs in a stockpile: one Planks order makes 3 planks", TimeoutSeconds = 300f)]
	private static IEnumerator ChoppingBlockMakesPlanks(TestContext t)
	{
		Shop shop = null;
		yield return ChoppingBlock(t, 2, true, made => shop = made);
		int planksBefore = Planks;
		int logsBefore = Scenario.Count(FactoryBlocks.Wood_DoubleLog_AA);
		Queue(shop, 1);
		t.Record("queued", shop.Station.QueueCount);
		t.True(shop.Station.QueueCount >= 1, "The Planks order is not in the chopping block's queue");
		float started = Time.time;
		yield return t.UntilGame(() => Planks >= planksBefore + CookBook.Planks.QtyCreated, 240f, CookBook.Planks.QtyCreated + " planks to be made");
		t.Record("gameSeconds", Mathf.Round(Time.time - started));
		t.Record("planks", Planks - planksBefore);
		t.Equal(logsBefore - 1, Scenario.Count(FactoryBlocks.Wood_DoubleLog_AA), "Logs left (one used)");
	}

	[GameTest("Crafting", "Two Planks orders with two logs make 6 planks and use both logs", TimeoutSeconds = 300f)]
	private static IEnumerator TwoOrdersMakeSixPlanks(TestContext t)
	{
		Shop shop = null;
		yield return ChoppingBlock(t, 2, true, made => shop = made);
		int planksBefore = Planks;
		int logsBefore = Scenario.Count(FactoryBlocks.Wood_DoubleLog_AA);
		Queue(shop, 2);
		yield return t.UntilGame(() => Planks >= planksBefore + 2 * CookBook.Planks.QtyCreated, 240f, "6 planks to be made");
		t.Equal(logsBefore - 2, Scenario.Count(FactoryBlocks.Wood_DoubleLog_AA), "Logs left (both used)");
	}

	[GameTest("Crafting", "A Planks order with no logs anywhere makes nothing (and stays queued)", TimeoutSeconds = 120f)]
	private static IEnumerator NoLogsNoPlanks(TestContext t)
	{
		// Logs earlier tests left (felled trees, stockpiles) would be fetched: taken away first. No other economy test
		// runs at the same time.
		int removed = Scenario.RemoveAll(FactoryBlocks.Wood_DoubleLog_AA);
		t.Record("leftoverLogsRemoved", removed);
		yield return t.Frames(3);
		t.Equal(0, Scenario.Count(FactoryBlocks.Wood_DoubleLog_AA), "Logs in the world");
		Shop shop = null;
		yield return ChoppingBlock(t, 0, true, made => shop = made);
		int planksBefore = Planks;
		Queue(shop, 1);
		yield return t.Seconds(45f);
		t.Equal(planksBefore, Planks, "Planks after 45 game seconds without logs");
		t.True(shop.Station.QueueCount >= 1, "The order left the queue though nothing was made");
	}

	[GameTest("Crafting", "A chopping block without a worker makes nothing from the logs", TimeoutSeconds = 120f)]
	private static IEnumerator NoWorkerNoPlanks(TestContext t)
	{
		Shop shop = null;
		// No idle builder anywhere either: one would take the order by itself (IdleBuilderTakesWorkshopOrder).
		Scenario.SidelineOthers(t);
		yield return ChoppingBlock(t, 2, false, made => shop = made);
		int planksBefore = Planks;
		Queue(shop, 1);
		yield return t.Seconds(45f);
		System.Collections.Generic.List<string> stations = new System.Collections.Generic.List<string>();
		foreach (CraftingStation station in Object.FindObjectsOfType<CraftingStation>())
		{
			stations.Add(station.name + " at " + XYZ.FromVector3(station.transform.position) + (station == shop.Station ? " (this test's)" : string.Empty) + ", operator " + ((station.Operator != null) ? station.Operator.name : "none") + ", queue " + station.QueueCount);
		}
		t.Record("stations", stations);
		t.Record("logsInStockpile", shop.Stockpile.ContentDescription.Value(Adjectif.woodBlock));
		t.True(shop.Station.Operator == null, "The chopping block got a worker on its own");
		t.Equal(planksBefore, Planks, "Planks after 45 game seconds without a worker");
	}

	[GameTest("Crafting", "A looping Planks order goes on while there are logs: 2 logs make 6 planks", TimeoutSeconds = 300f)]
	private static IEnumerator LoopUsesAllLogs(TestContext t)
	{
		Shop shop = null;
		yield return ChoppingBlock(t, 2, true, made => shop = made);
		int planksBefore = Planks;
		Queue(shop, 1);
		Scenario.ProjectCommands.CmdcraftingLoop(shop.Station.gameObject, true);
		t.True(shop.Station.craftingLabor.LoopRecipe, "The chopping block does not loop");
		yield return t.UntilGame(() => Planks >= planksBefore + 2 * CookBook.Planks.QtyCreated, 240f, "6 planks from the looping order");
	}

	[GameTest("Crafting", "An idle builder takes a chopping block's Planks order by itself (no worker assigned) and makes the planks", TimeoutSeconds = 300f)]
	private static IEnumerator IdleBuilderTakesWorkshopOrder(TestContext t)
	{
		Scenario.SidelineOthers(t);
		Shop shop = null;
		yield return ChoppingBlock(t, 1, false, made => shop = made);
		Labor idle = Scenario.Bricktron(t, Occupation.Type.Builder, new XYZ(shop.Site.x, shop.Site.y, shop.Site.z - 2));
		yield return t.Frames(5);
		int planksBefore = Planks;
		Queue(shop, 1);
		yield return t.UntilGame(() => Planks >= planksBefore + CookBook.Planks.QtyCreated, 240f, "the idle builder to make the planks", 30f, () => t.Log(idle.name + ": " + idle.Activity + ", operator " + ((shop.Station.Operator != null) ? shop.Station.Operator.name : "none")));
	}

	[GameTest("Crafting", "A workshop queues up to 20 orders (the game: 8); the orders after the 8th survive a removal before them and the network copy of the queue", TimeoutSeconds = 120f)]
	private static IEnumerator QueueHoldsTwentyOrders(TestContext t)
	{
		Scenario.SidelineOthers(t);
		Shop shop = null;
		yield return ChoppingBlock(t, 0, false, made => shop = made);
		// A new workshop sets its default order (a looping Planks) when it starts, the frame after it is built.
		yield return t.Frames(3);
		Scenario.ProjectCommands.CmdcraftingDoNothing(shop.Station.gameObject);
		yield return t.Frames(2);
		Queue(shop, 25);
		yield return t.Frames(2);
		t.Equal(20, shop.Station.QueueCount, "Orders in the queue after queuing 25");
		Scenario.ProjectCommands.CmdcraftingRemoveAt(shop.Station.gameObject, 3);
		yield return t.Frames(2);
		t.Equal(19, shop.Station.QueueCount, "Orders after taking out the 4th");
		// What a joining player gets: the state written in full and read into a list again.
		UnityEngine.Networking.NetworkWriter writer = new UnityEngine.Networking.NetworkWriter();
		shop.Station.state.OnSerialize(writer, true);
		System.Collections.Generic.List<RecipeInfo> before = new System.Collections.Generic.List<RecipeInfo>();
		shop.Station.state.GetRecipe(before);
		shop.Station.state.OnDeserialize(new UnityEngine.Networking.NetworkReader(writer.ToArray()), true);
		System.Collections.Generic.List<RecipeInfo> after = new System.Collections.Generic.List<RecipeInfo>();
		shop.Station.state.GetRecipe(after);
		t.Record("queued", before.Count + " before, " + after.Count + " after");
		t.Equal(19, after.Count, "Orders read back from the network copy");
		Scenario.ProjectCommands.CmdcraftingDoNothing(shop.Station.gameObject);
		yield return t.Frames(2);
		t.Equal(0, shop.Station.QueueCount, "Orders after clearing the queue");
	}

	// ---- helpers

	// A stockpile with logs logs in it and a chopping block next to it, with a worker when withWorker. The planks
	// made near it are taken away by the clean-up.
	private static IEnumerator ChoppingBlock(TestContext t, int logs, bool withWorker, Action<Shop> made)
	{
		Shop shop = new Shop { Site = Scenario.Site(t, 12, 6) };
		BuildProject build = Scenario.BuildProject(t, shop.Site);
		yield return Scenario.Building(t, TaskTests.Pallet, new XYZ(shop.Site.x - 4, shop.Site.y, shop.Site.z), build, go => shop.Stockpile = go.GetComponent<Recepteur>());
		yield return Scenario.Building(t, Blueprints.ChoppingBlock, new XYZ(shop.Site.x + 2, shop.Site.y, shop.Site.z), build, go => shop.Station = go.GetComponent<CraftingStation>());
		t.NotNull(shop.Stockpile, "The stockpile's storage");
		t.NotNull(shop.Station, "The chopping block's crafting station");
		if (logs > 0)
		{
			foreach (GameObject log in Scenario.Items(t, FactoryBlocks.Wood_DoubleLog_AA, logs, shop.Stockpile.transform.position + Vector3.up))
			{
				shop.Stockpile.AddObject(log);
			}
		}
		if (withWorker)
		{
			shop.Worker = Scenario.Bricktron(t, Occupation.Type.Builder, new XYZ(shop.Site.x, shop.Site.y, shop.Site.z - 2));
			yield return t.Frames(5);
			Scenario.ProjectCommands.CmdCraftingAssignWorker(shop.Station.gameObject, shop.Worker.gameObject);
			yield return t.Frames(2);
			t.True(shop.Station.Operator == shop.Worker, "The worker is not the chopping block's operator");
		}
		XYZ site = shop.Site;
		t.Cleanup(() =>
		{
			foreach (FactoryImprint imprint in Object.FindObjectsOfType<FactoryImprint>())
			{
				if (imprint != null && Scenario.Is(imprint.gameObject, FactoryBlocks.Wood_DoublePlank_AA) && Vector3.Distance(imprint.transform.position, site.ToVector3()) < 15f)
				{
					Scenario.Release(imprint.gameObject);
				}
			}
		});
		made(shop);
	}

	private static void Queue(Shop shop, int count)
	{
		for (int i = 0; i < count; i++)
		{
			Scenario.ProjectCommands.CmdcraftingQueueRecipe(shop.Station.gameObject, CookBook.Planks.id, 1);
		}
	}
}
