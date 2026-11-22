#if UNITY6
using System.Collections;
using Brix.Engine;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.AI.KnowledgeSpace;
using Brix.Game.Components;
using Brix.Game.Semantique;
using Brix.Transactions;
using Brix.Utils;
using CastleStoryPlus.Core;
using CastleStoryPlus.DevTools.Pathing;
using CastleStoryPlus.DevTools.Testing;
using CastleStoryPlus.Diagnostics;
using CastleStoryPlus.WorkerAI;
using UnityEngine;

namespace CastleStoryPlus.DevTools.Tests;

// Workers keep their own work to the base (#181): a worker's own search for material takes a loose item no player owns
// in the base area, but not one far outside it nor one with an enemy standing by it.
[ParallelTests("Economy")]
internal static class BaseAreaTests
{
	// The base area's radius during the test: the stockpile's circle covers the near item and not the far one.
	private const int TestRadius = 10;

	[GameTest("Tasks", "A worker's own search for material takes a loose item no player owns in the base area, not one outside it nor one with an enemy by it; the one outside stays where it lies (#181)", TimeoutSeconds = 150f, Exclusive = true)]
	private static IEnumerator SearchesKeepToTheBase(TestContext t)
	{
		if (!Plugin.IsEnabled(Features.BaseArea) || BaseArea.Radius == null)
		{
			t.Skip("The BaseArea feature is off");
		}
		// West to east: the stockpile (the base here), the near item 4 blocks from it, open ground, the far item 24
		// blocks from the stockpile with the builder right beside it.
		XYZ site = Scenario.Site(t, 31, 9, 4, away: 30);
		XYZ pileAt = new XYZ(site.x - 12, site.y, site.z);
		Recepteur stockpile = null;
		BuildProject build = Scenario.BuildProject(t, pileAt);
		yield return Scenario.Building(t, TaskTests.Pallet, pileAt, build, made => stockpile = made.GetComponent<Recepteur>());
		t.NotNull(stockpile, "The stockpile's storage");
		int radius = BaseArea.Radius.Value;
		int margin = BaseArea.ZoneMargin.Value;
		BaseArea.Radius.Value = TestRadius;
		BaseArea.ZoneMargin.Value = 0;
		t.Cleanup(() =>
		{
			BaseArea.Radius.Value = radius;
			BaseArea.ZoneMargin.Value = margin;
			BaseArea.Refresh();
		});
		Vector3 pile = stockpile.transform.position;
		GameObject near = Neutral(t, BuildResources.IronIngot, new XYZ(Mathf.FloorToInt(pile.x) + 4, site.y, site.z + 2));
		GameObject far = Neutral(t, BuildResources.IronIngot, new XYZ(Mathf.FloorToInt(pile.x) + 24, site.y, site.z + 2));
		Labor builder = Scenario.Bricktron(t, Occupation.Type.Builder, new XYZ(Mathf.FloorToInt(pile.x) + 25, site.y, site.z));
		builder.Autonomy = AutonomyStatus.WaitForOrders;
		GameComponent nearItem = near.GetComponent<GameComponent>();
		GameComponent farItem = far.GetComponent<GameComponent>();
		Ressource iron = Apparence.PrincipaleOf(near);
		t.NotNull(nearItem, "The near iron ingot's game component");
		t.NotNull(farItem, "The far iron ingot's game component");
		t.NotNull(iron, "The iron ingot's resource");
		yield return TestWorld.WaitForGraph(t);
		yield return t.Seconds(2f);
		BaseArea.Refresh();
		Faction team = builder.faction;
		float nearOut = BaseArea.OutsideBy(team, near.transform.position);
		float farOut = BaseArea.OutsideBy(team, far.transform.position);
		t.Log("Stockpile at " + XYZ.FromVector3(pile) + ", near ingot at " + near.transform.position + " (" + nearOut.ToString("F1") + " out), far ingot at " + far.transform.position + " (" + farOut.ToString("F1") + " out), builder at " + Units.VoxelOf(builder));
		t.True(BaseArea.InBase(team, near.transform.position), "The iron ingot 4 blocks from the stockpile is not in the base area");
		t.False(BaseArea.InBase(team, far.transform.position), "The iron ingot 24 blocks from the stockpile is in the base area");
		t.Equal(BaseAreaRules.Verdict.Allowed, BaseArea.Judge(team, nearItem.faction, near.transform.position), "The rule for the near iron ingot");
		t.Equal(BaseAreaRules.Verdict.OutsideBase, BaseArea.Judge(team, farItem.faction, far.transform.position), "The rule for the far iron ingot");
		// The game's test of each thing a worker's search weighs, as the builder's search asks it: the near ingot may
		// be fetched, the far one not.
		t.True(Weighed(team, nearItem, iron), "A worker's search may not fetch the iron ingot in the base area");
		t.False(Weighed(team, farItem, iron), "A worker's search may fetch the iron ingot outside the base area");
		// The builder's own search: the far ingot lies right beside it, yet it is passed over. (What it does choose may
		// also be iron elsewhere, or nothing where the fog hides the near one.)
		IGameComponent found = Knowledge.Instance.ClosestObjectToPickup(builder, iron, team);
		t.Log("The search chose " + Name(found));
		t.Record("chose", Name(found));
		t.True(found == null || found.GameObject != far, "The builder's search chose the iron ingot outside the base area");

		// An enemy by the near ingot: no walk into it for that either.
		Labor enemy = Scenario.Corruptron(t, Scenario.Walkable(new XYZ(Mathf.FloorToInt(near.transform.position.x), site.y, site.z - 2)));
		t.NotNull(enemy, "The corruptron");
		enemy.Autonomy = AutonomyStatus.WaitForOrders;
		yield return t.Seconds(1.5f);
		BaseArea.Refresh();
		t.Log("Corruptron at " + Units.VoxelOf(enemy));
		t.True(BaseArea.NearEnemy(team, near.transform.position), "The corruptron by the near iron ingot does not count as near it");
		t.Equal(BaseAreaRules.Verdict.NearEnemies, BaseArea.Judge(team, nearItem.faction, near.transform.position), "The rule for the near iron ingot with an enemy by it");
		t.False(Weighed(team, nearItem, iron), "A worker's search may fetch the iron ingot the corruptron stands by");
		found = Knowledge.Instance.ClosestObjectToPickup(builder, iron, team);
		t.Log("With the corruptron there the search chose " + Name(found));
		t.True(found == null || found.GameObject != near, "The builder's search chose the iron ingot the corruptron stands by");
		t.True(found == null || found.GameObject != far, "The builder's search chose the iron ingot outside the base area");
		Scenario.Discard(enemy.gameObject);
		yield return t.Seconds(1.5f);
		BaseArea.Refresh();
		t.False(BaseArea.NearEnemy(team, near.transform.position), "The near iron ingot still counts as by an enemy with the corruptron gone");
		t.True(Weighed(team, nearItem, iron), "A worker's search may not fetch the iron ingot in the base area with the corruptron gone");
		t.True(far.activeInHierarchy && PickUpAround.IsLoose(far), "The iron ingot outside the base area was taken");
		// What the base area's watch wrote about the builder, for the record.
		foreach (string line in BaseArea.Recent)
		{
			if (line.StartsWith(builder.name, System.StringComparison.Ordinal))
			{
				t.Log("Base area log: " + line);
			}
		}
	}

	// The game's test of one thing a search of the team weighs, with the base area's rule on it as in a worker's search.
	private static bool Weighed(Faction team, IGameComponent gc, Ressource resource)
	{
		BaseArea.BeginSearch(team);
		try
		{
			return Knowledge._ClosestObjectToPickup_Test(gc, resource);
		}
		finally
		{
			BaseArea.EndSearch();
		}
	}

	// A loose item no player owns, lying at the voxel.
	private static GameObject Neutral(TestContext t, Factory.AssetKey key, XYZ at)
	{
		GameObject item = Transactor.SpawnUNet(key, Faction.DefaultNeutral, Scenario.Walkable(at).ToVector3(), Quaternion.identity);
		t.NotNull(item, "The spawned " + key.Name);
		SpawnSpread.Calm(item);
		t.Cleanup(() =>
		{
			if (item != null && item.activeInHierarchy)
			{
				Scenario.Release(item);
			}
		});
		return item;
	}

	private static string Name(IGameComponent gc)
	{
		return (gc != null && gc.GameObject != null) ? gc.GameObject.name + " at " + gc.GameObject.transform.position : "nothing";
	}
}
#endif
