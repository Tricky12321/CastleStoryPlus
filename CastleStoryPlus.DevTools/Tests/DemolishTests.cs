using System.Collections;
using System.Collections.Generic;
using Brix.Engine;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Components;
using CastleStoryPlus.Building;
using CastleStoryPlus.DevTools.Building;
using CastleStoryPlus.DevTools.Testing;
using UnityEngine;

namespace CastleStoryPlus.DevTools.Tests;

// The demolition task (Demolish, #200): a building marked for demolition is taken down by a builder of the build
// task, its materials come back as items and are carried to storage; a second click cancels the mark.
[ParallelTests("Economy")]
internal static class DemolishTests
{
	[GameTest("Demolition", "A workbench marked for demolition is taken down by a builder, and the materials it cost reach the stockpiles", TimeoutSeconds = 480f)]
	private static IEnumerator BuilderDemolishesAndStoresMaterials(TestContext t)
	{
		XYZ site = Scenario.Site(t, 14, 6);
		BuildProject build = Scenario.BuildProject(t, site);
		// One stockpile per kind of material the workbench may give back (a pallet holds one kind).
		for (int i = 0; i < 3; i++)
		{
			yield return Scenario.Building(t, TaskTests.Pallet, new XYZ(site.x - 6, site.y, site.z - 2 + i * 2), build, (GameObject made) => { });
		}
		GameObject workbench = null;
		yield return Scenario.Building(t, Blueprints.Workbench, new XYZ(site.x + 2, site.y, site.z), build, (GameObject made) => workbench = made);
		t.NotNull(workbench, "The workbench");
		Labor builder = Scenario.Bricktron(t, Occupation.Type.Builder, new XYZ(site.x - 2, site.y, site.z + 2));

		string refused = Demolish.Apply(User.LocalUser, workbench, true, build);
		t.True(refused == null, "The workbench was not marked: " + refused);
		t.True(Demolish.IsMarked(workbench), "The workbench is not in the demolition task");
		t.NotNull(Demolish.FindAnti(workbench), "The workbench's demolition blueprint");
		List<KeyValuePair<Factory.AssetKey, int>> refund = Demolish.RefundOf(workbench);
		t.True(refund.Count > 0, "The workbench gives nothing back");
		Dictionary<Factory.AssetKey, int> before = new Dictionary<Factory.AssetKey, int>();
		foreach (KeyValuePair<Factory.AssetKey, int> entry in refund)
		{
			before[entry.Key] = Scenario.Stored(entry.Key);
			t.Log("Gives back " + entry.Value + " " + entry.Key.Name + " (" + before[entry.Key] + " stored now)");
		}
		t.Record("refund", refund.Count);

		Scenario.Hire(build, builder);
		yield return t.UntilGame(() => workbench == null || !workbench.activeInHierarchy, 180f, "the builder to take the workbench down");
		t.False(Demolish.IsMarked(workbench), "The demolished workbench is still marked");
		foreach (KeyValuePair<Factory.AssetKey, int> entry in refund)
		{
			Factory.AssetKey key = entry.Key;
			int expected = before[key] + entry.Value;
			yield return t.UntilGame(() => Scenario.Stored(key) >= expected, 240f, entry.Value + " " + key.Name + " to reach the stockpiles");
		}
	}

	[GameTest("Demolition", "Marking a building twice cancels: its demolition blueprint goes and the building stays", TimeoutSeconds = 60f)]
	private static IEnumerator SecondMarkCancels(TestContext t)
	{
		XYZ site = Scenario.Site(t, 6, 6);
		BuildProject build = Scenario.BuildProject(t, site);
		GameObject pallet = null;
		yield return Scenario.Building(t, TaskTests.Pallet, site, build, (GameObject made) => pallet = made);
		t.True(Demolish.Request(pallet, true) == null, "The host refused to mark the stockpile");
		t.True(Demolish.IsMarked(pallet), "The stockpile is not marked");
		t.NotNull(Demolish.FindAnti(pallet), "The stockpile's demolition blueprint");
		t.True(Demolish.Request(pallet, false) == null, "The host refused to cancel the mark");
		yield return t.Until(() => Demolish.FindAnti(pallet) == null, 5f, "the demolition blueprint to go");
		t.False(Demolish.IsMarked(pallet), "The stockpile is still marked after the cancel");
		yield return t.Seconds(2f);
		t.True(pallet != null && pallet.activeInHierarchy, "The stockpile was taken down although its demolition was cancelled");
	}

	[GameTest("Demolition", "A marked stockpile taken down at once gives its whole cost back as items lying on the spot", TimeoutSeconds = 60f)]
	private static IEnumerator DemolishedBuildingGivesItsCostBack(TestContext t)
	{
		XYZ site = Scenario.Site(t, 6, 6);
		BuildProject build = Scenario.BuildProject(t, site);
		GameObject pallet = null;
		yield return Scenario.Building(t, TaskTests.Pallet, site, build, (GameObject made) => pallet = made);
		t.True(Demolish.Apply(User.LocalUser, pallet, true, build) == null, "The stockpile was not marked");
		List<KeyValuePair<Factory.AssetKey, int>> refund = Demolish.RefundOf(pallet);
		Dictionary<Factory.AssetKey, int> before = new Dictionary<Factory.AssetKey, int>();
		foreach (KeyValuePair<Factory.AssetKey, int> entry in refund)
		{
			before[entry.Key] = Scenario.Count(entry.Key);
		}
		int leftovers = DemolishHaul.Count;
		// As if its demolisher had knocked it down: the demolition blueprint finished at once.
		InstantBuild.Complete(new[] { Demolish.FindAnti(pallet) });
		yield return t.Until(() => pallet == null || !pallet.activeInHierarchy, 5f, "the stockpile to be taken down");
		foreach (KeyValuePair<Factory.AssetKey, int> entry in refund)
		{
			t.AtLeast(before[entry.Key] + entry.Value, Scenario.Count(entry.Key), entry.Key.Name + " in the world after the demolition");
		}
		int given = 0;
		foreach (KeyValuePair<Factory.AssetKey, int> entry in refund)
		{
			given += entry.Value;
		}
		t.AtLeast(leftovers + given, DemolishHaul.Count, "Demolition leftovers to carry");
	}
}
