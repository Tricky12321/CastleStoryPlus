using System.Collections;
using Brix.Engine;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Semantique;
using Brix.Legacy;
using CastleStoryPlus.Core;
using CastleStoryPlus.DevTools.Testing;
using UnityEngine;
using FactoryBlocks = Brix.External.Factories.Blocks;

namespace CastleStoryPlus.DevTools.Tests;

// The stockpile wheel's "accept only coal" button (#176): the stockpile it is used on takes coal, and only coal.
[ParallelTests("Economy")]
internal static class CoalStockpileTests
{
	[GameTest("Stockpiles", "The stockpile wheel has an 'accept only coal' button; a stockpile set to it gets a builder's coal and has no room for stone, logs or raw iron (#176)", TimeoutSeconds = 120f)]
	private static IEnumerator StockpileAcceptsOnlyCoal(TestContext t)
	{
		if (!Plugin.IsEnabled(Features.Metallurgy))
		{
			t.Skip("Metallurgy is off");
		}
		t.True(PieContenu.boutons.ContainsKey(Metallurgy.CoalStockpileWheel.Button), "The wheel files have no " + Metallurgy.CoalStockpileWheel.Button + " button");
		t.True(Instructions.commendes.ContainsKey(Metallurgy.CoalStockpileWheel.Event), "No command for the coal button");
		XYZ site = Scenario.Site(t, 6, 6);
		BuildProject build = Scenario.BuildProject(t, site);
		Recepteur stockpile = null;
		yield return Scenario.Building(t, TaskTests.Pallet, site, build, made => stockpile = made.GetComponent<Recepteur>());
		t.NotNull(stockpile, "The stockpile's storage");
		MonoBehaviour[] subjects = Instructions.sujets;
		object target = Instructions.objet;
		try
		{
			Instructions.sujets = new MonoBehaviour[0];
			Instructions.objet = stockpile.gameObject;
			Instructions.Execute(Metallurgy.CoalStockpileWheel.Event);
		}
		finally
		{
			Instructions.sujets = subjects;
			Instructions.objet = target;
		}
		yield return t.Until(() => stockpile.ExclusiveRessource != null, 5f, "the stockpile to take only one resource");
		t.Equal(Metallurgy.Metallurgy.Coal.GetType(), stockpile.ExclusiveRessource.GetType(), "The resource the stockpile takes");
		t.True(stockpile.HasRoomFor(Metallurgy.Metallurgy.CoalKey), "The coal-only stockpile has no room for coal");
		t.False(stockpile.HasRoomFor(RawResources.RawStone), "The coal-only stockpile has room for stone");
		t.False(stockpile.HasRoomFor(RawResources.RawIron), "The coal-only stockpile has room for raw iron");
		t.False(stockpile.HasRoomFor(FactoryBlocks.Wood_DoubleLog_AA), "The coal-only stockpile has room for logs");

		// A builder next to it with coal in its bag stores it there; stone and a log lying by it are never sent there.
		Labor worker = Scenario.Bricktron(t, Occupation.Type.Builder, new XYZ(site.x, site.y, site.z - 3));
		yield return t.Frames(5);
		GameObject coal = Scenario.Items(t, Metallurgy.Metallurgy.CoalKey, 1, worker.transform.position + Vector3.up * 2f)[0];
		GameObject stone = Scenario.Items(t, RawResources.RawStone, 1, stockpile.transform.position + Vector3.up * 2f)[0];
		GameObject log = Scenario.Items(t, FactoryBlocks.Wood_DoubleLog_AA, 1, stockpile.transform.position + Vector3.up * 2f)[0];
		worker.recepteur.AddObject(coal);
		yield return t.Frames(2);
		t.Equal(1, worker.recepteur.ContentDescription.Value(Metallurgy.Metallurgy.Coal), "Coal in the builder's bag");
		// The new stockpile is known to the workers' storage search a moment after it is built.
		yield return t.UntilGame(() => worker.BestRecepteurToStore(coal) == stockpile.gameObject, 20f, "the builder to see the stockpile as the storage for its coal");
		t.True(worker.BestRecepteurToStore(stone) != stockpile.gameObject, "Stone would be stored on the coal-only stockpile");
		t.True(worker.BestRecepteurToStore(log) != stockpile.gameObject, "A log would be stored on the coal-only stockpile");
		Order.Transfer.GoStore.Send(worker, Labor.DecisionSource.Project);
		yield return t.UntilGame(() => stockpile.ContentDescription.Value(Metallurgy.Metallurgy.Coal) >= 1, 60f, "the builder to store its coal on the stockpile", 15f, () => t.Log(worker.name + " " + worker.Activity + ", bag " + worker.recepteur.ContentString() + ", stockpile " + stockpile.ContentString()));
		t.Equal(0, stockpile.ContentDescription.Value(Adjectif.stoneBlock), "Stone on the coal-only stockpile");
		t.Equal(0, stockpile.ContentDescription.Value(Adjectif.woodBlock), "Logs on the coal-only stockpile");
	}
}
