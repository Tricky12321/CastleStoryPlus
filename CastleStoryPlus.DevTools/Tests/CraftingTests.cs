using System;
using System.Collections;
using Brix.Engine;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Game.Semantique;
using CastleStoryPlus.DevTools.Testing;
using CastleStoryPlus.Economy;
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

	// What the shop and its worker are doing, for a failed run's log.
	private static void Note(TestContext t, Shop shop)
	{
		Labor worker = shop.Worker;
		string doing = (worker == null) ? "no worker" : worker.name + " " + worker.Activity + " at " + CastleStoryPlus.DevTools.Pathing.Units.VoxelOf(worker) + ", task " + ((worker.CurrentTask != null) ? worker.CurrentTask.ToString() : "none") + ", bag " + worker.recepteur.ContentString();
		Faction faction = Faction.GetFaction(shop.Station.gameObject);
		RecipeInfo planks = CookBook.Planks;
#if !ESSENTIALS
		Ressource product = CraftLoopLimit.Product(planks);
#endif
		Recipe current = shop.Station.CurrentRecipe;
		string station = string.Empty;
		if (worker != null)
		{
			GameObject made = null;
			bool pickup = shop.Station.inventory != null && shop.Station.inventory.ItemToPickup(out made);
			GameObject store = (made != null) ? worker.BestRecepteurToStore(made) : null;
			station = ", station " + shop.Station.recepteur.ContentString() + ", decide " + shop.Station.DecideWhatToDo(worker) + ", work for worker " + shop.Station.HasWorkFor(worker) + ", product to pick up " + pickup + " (store " + ((store != null) ? store.name : "none") + "), pick up and store " + shop.Station.CanPickUpProduction(worker, andStore: true) + ", ingredients " + (current != null && current.HasIngredientsIn(shop.Station.recepteur)) + ", room for product " + (current != null && shop.Station.recepteur.HasRoomFor(current.Creates)) + ", needs work " + shop.Station.NeedsWork(current);
		}
#if !ESSENTIALS
		string limit = (product != null) ? ", plank stock " + CraftLoopLimit.Stock(faction, product, cached: false) + " coming " + CraftLoopLimit.Coming(faction, product, planks.Creates, cached: false) : string.Empty;
#else
		string limit = string.Empty;
#endif
		t.Log(doing + "; queue " + shop.Station.QueueCount + ", current " + ((current != null) ? current.Info.id + (current.Finished ? " finished" : string.Empty) : "none") + limit + station + ", operator " + ((shop.Station.Operator != null) ? shop.Station.Operator.name : "none") + ", logs stored " + Scenario.Stored(FactoryBlocks.Wood_DoubleLog_AA) + ", planks " + Planks);
	}

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
		yield return t.UntilGame(() => Planks >= planksBefore + CookBook.Planks.QtyCreated, 240f, CookBook.Planks.QtyCreated + " planks to be made", 30f, () => Note(t, shop));
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
		yield return t.UntilGame(() => Planks >= planksBefore + 2 * CookBook.Planks.QtyCreated, 240f, "6 planks to be made", 30f, () => Note(t, shop));
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
		yield return t.UntilGame(() => Planks >= planksBefore + 2 * CookBook.Planks.QtyCreated, 240f, "6 planks from the looping order", 30f, () => Note(t, shop));
	}

#if !ESSENTIALS
	[GameTest("Crafting", "A looping Planks order kept to a stock limit (two orders above what there is) with logs for more stops exactly at the limit and waits: planks being made, in the workshop or carried count, so no order more starts", TimeoutSeconds = 420f)]
	private static IEnumerator LoopLimitHoldsStock(TestContext t)
	{
		Shop shop = null;
		yield return ChoppingBlock(t, 4, true, made => shop = made);
		// An empty stockpile for the planks: the one with the logs takes no planks, and a workshop full of planks makes
		// no more (the planks then count as stock, as the limit counts them).
		Recepteur planksPile = null;
		yield return Scenario.Building(t, TaskTests.Pallet, new XYZ(shop.Site.x - 1, shop.Site.y, shop.Site.z + 4), Scenario.BuildProject(t, shop.Site), go => planksPile = go.GetComponent<Recepteur>());
		t.NotNull(planksPile, "The stockpile for the planks");
		RecipeInfo planks = CookBook.Planks;
		int batch = CraftLoopLimit.Batch(planks);
		t.Equal(planks.QtyCreated, batch, "Planks one order makes (the step of the limit)");
		t.Equal(2 * batch, CraftLoopLimit.Next(batch, 1, batch), "The limit's step up from one order");
		t.Equal(batch, CraftLoopLimit.Next(batch + 1, -1, batch), "The limit's step down from between orders");
		Faction faction = Faction.GetFaction(shop.Station.gameObject);
		Ressource product = CraftLoopLimit.Product(planks);
		t.NotNull(product, "The Planks recipe's product");
		// A new chopping block starts its default looping Planks order at once: that order counts already (Coming).
		int stockBefore = CraftLoopLimit.Stock(faction, product, cached: false);
		int have = stockBefore + CraftLoopLimit.Coming(faction, product, planks.Creates, cached: false);
		int limit = have + 2 * batch;
		int planksBefore = Planks;
		t.Record("limit", limit);
		CraftingState state = shop.Station.state;
		CraftLoopLimit.Set(state, planks.id, limit);
		t.Cleanup(() => CraftLoopLimit.SetAll(state, null));
		Queue(shop, 1);
		Scenario.ProjectCommands.CmdcraftingLoop(shop.Station.gameObject, true);
		yield return t.UntilGame(() => CraftLoopLimit.Stock(faction, product, cached: false) >= limit, 300f, "the stock to reach the limit", 30f, () => Note(t, shop));
		// Long enough for a third order to be made had it started.
		yield return t.Seconds(90f);
		Note(t, shop);
		t.Record("made", Planks - planksBefore);
		t.Record("stock", CraftLoopLimit.Stock(faction, product, cached: false));
		t.Equal(limit, CraftLoopLimit.Stock(faction, product, cached: false), "Planks in stock once the limit is reached");
		t.Equal(planksBefore + limit - stockBefore, Planks, "Planks made once the limit is reached (no order more)");
		t.True(shop.Station.craftingLabor.LoopRecipe && shop.Station.QueueCount >= 1, "The looping order is still queued, waiting");
	}
#endif

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

	[GameTest("Crafting", "A worker emptying its bag brings its log to a chopping block whose Planks order needs it, though the stockpile is nearer (#96)", TimeoutSeconds = 180f)]
	private static IEnumerator UnloadBringsWorkshopItsLog(TestContext t)
	{
		Unload unload = null;
		yield return UnloadNextTo(t, -4, made => unload = made);
		t.Record("stockpileBlocks", unload.StockpileDistance);
		t.Record("workshopBlocks", unload.WorkshopDistance);
		t.Equal(1, unload.InWorkshop, "Logs the chopping block got");
		t.Equal(0, unload.InStockpile, "Logs in the stockpile");
	}

	[GameTest("Crafting", "A worker emptying its bag next to a stockpile stores its log there when the chopping block needing it is far away (#96)", TimeoutSeconds = 180f)]
	private static IEnumerator UnloadSkipsFarWorkshop(TestContext t)
	{
		Unload unload = null;
		yield return UnloadNextTo(t, 12, made => unload = made);
		t.Record("stockpileBlocks", unload.StockpileDistance);
		t.Record("workshopBlocks", unload.WorkshopDistance);
		t.Equal(0, unload.InWorkshop, "Logs the chopping block got");
		t.Equal(1, unload.InStockpile, "Logs in the stockpile");
	}

	private sealed class Unload
	{
		public float StockpileDistance;

		public float WorkshopDistance;

		public int InWorkshop;

		public int InStockpile;
	}

	// A stockpile with a builder next to it carrying the only log in the world, and a chopping block workshopX
	// voxels along from the site's centre (the stockpile is at -10) with a Planks order waiting for a log. The builder
	// is told to store its bag; where the log is when the bag is empty.
	private static IEnumerator UnloadNextTo(TestContext t, int workshopX, Action<Unload> made)
	{
		int removed = Scenario.RemoveAll(FactoryBlocks.Wood_DoubleLog_AA);
		t.Record("leftoverLogsRemoved", removed);
		Scenario.SidelineOthers(t);
		yield return t.Frames(3);
		XYZ site = Scenario.Site(t, 28, 6);
		BuildProject build = Scenario.BuildProject(t, site);
		Recepteur stockpile = null;
		CraftingStation station = null;
		yield return Scenario.Building(t, TaskTests.Pallet, new XYZ(site.x - 10, site.y, site.z), build, go => stockpile = go.GetComponent<Recepteur>());
		yield return Scenario.Building(t, Blueprints.ChoppingBlock, new XYZ(site.x + workshopX, site.y, site.z), build, go => station = go.GetComponent<CraftingStation>());
		t.NotNull(stockpile, "The stockpile's storage");
		t.NotNull(station, "The chopping block's crafting station");
		Labor worker = Scenario.Bricktron(t, Occupation.Type.Builder, new XYZ(site.x - 10, site.y, site.z - 2));
		yield return t.Frames(5);
		GameObject log = Scenario.Items(t, FactoryBlocks.Wood_DoubleLog_AA, 1, worker.transform.position + Vector3.up * 2f)[0];
		worker.recepteur.AddObject(log);
		yield return t.Frames(2);
		Unload unload = new Unload
		{
			StockpileDistance = Mathf.Round(Vector3.Distance(worker.transform.position, stockpile.transform.position)),
			WorkshopDistance = Mathf.Round(Vector3.Distance(worker.transform.position, station.transform.position))
		};
		// The new stockpile is known to the workers' storage search a moment after it is built.
		yield return t.UntilGame(() => worker.BestRecepteurToStore(log) == stockpile.gameObject, 20f, "the builder to see the stockpile as the storage for its log");
		Scenario.ProjectCommands.CmdcraftingQueueRecipe(station.gameObject, CookBook.Planks.id, 1);
		yield return t.Frames(2);
		t.True(station.StuffToFetch() != null, "The chopping block's Planks order waits for a log");
		t.Equal(1, worker.recepteur.ContentDescription.Value(Adjectif.woodBlock), "Logs in the builder's bag when it is sent to store it");
		Order.Transfer.GoStore.Send(worker, Labor.DecisionSource.Project);
		// Read in the frame the bag is emptied: the builder, idle then, soon takes the chopping block's order by itself
		// and fetches the log from the stockpile.
		yield return t.UntilGame(() =>
		{
			if (!worker.recepteur.IsEmpty())
			{
				return false;
			}
			unload.InWorkshop = station.recepteur.ContentDescription.Value(Adjectif.woodBlock);
			unload.InStockpile = stockpile.ContentDescription.Value(Adjectif.woodBlock);
			return true;
		}, 90f, "the builder to empty its bag", 15f, () => t.Log(worker.name + " " + worker.Activity + ", task " + ((worker.CurrentTask != null) ? worker.CurrentTask.ToString() : "none") + ", bag " + worker.recepteur.ContentString() + ", station " + station.recepteur.ContentString() + ", stockpile " + stockpile.ContentString()));
		made(unload);
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
