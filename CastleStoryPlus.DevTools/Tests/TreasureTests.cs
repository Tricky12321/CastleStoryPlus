using System.Collections;
using System.Collections.Generic;
using Brix.Engine;
using Brix.Game;
using Brix.Game.AI;
using CastleStoryPlus.Core;
using CastleStoryPlus.DevTools.Testing;
using CastleStoryPlus.Treasure;
using UnityEngine;

namespace CastleStoryPlus.DevTools.Tests;

// The treasure's switches (#179, #182): with Treasure off a level gets no chests, ruins or guardians (also when the
// switch went off while the game ran), TreasureRuins off leaves the chest without its ruin, TreasureGuardians off
// leaves it without guardians (no corruptrons in the ruins either), and the guardians a saved world brings along are
// taken away while the switches say so. The chests are put on the test's own ground (TreasureChests.Populate), as a
// new world's are on spots of its map; Test Island itself gets none (NoTreasureOnTestIslandPatch).
// They switch features, so they run alone.
internal static class TreasureTests
{
	// How far from a chest its guardians stand (the ruin's radius, 3, and up to 7 more for a big pack) and a little more.
	private const float GuardReach = 20f;

	// The switches for the test, put back as they were by its clean-up.
	private static void Switches(TestContext t, bool treasure, bool ruins, bool guardians)
	{
		bool wasTreasure = Plugin.IsEnabled(Features.Treasure);
		bool wasRuins = Plugin.IsEnabled(Features.TreasureRuins);
		bool wasGuardians = Plugin.IsEnabled(Features.TreasureGuardians);
		t.Cleanup(() =>
		{
			FeatureToggle.Set(Features.Treasure, wasTreasure);
			FeatureToggle.Set(Features.TreasureRuins, wasRuins);
			FeatureToggle.Set(Features.TreasureGuardians, wasGuardians);
		});
		FeatureToggle.Set(Features.Treasure, treasure);
		FeatureToggle.Set(Features.TreasureRuins, ruins);
		FeatureToggle.Set(Features.TreasureGuardians, guardians);
		t.Log("Treasure " + treasure + ", TreasureRuins " + ruins + ", TreasureGuardians " + guardians);
	}

	// The units within reach of the place that were not there before (the guardians a chest got, and anything else).
	private static List<Labor> NewUnits(HashSet<Labor> before, Vector3 at, float reach)
	{
		List<Labor> found = new List<Labor>();
		foreach (Labor labor in Live<Labor>.Active())
		{
			if (labor != null && !before.Contains(labor) && (labor.transform.position - at).sqrMagnitude <= reach * reach)
			{
				found.Add(labor);
			}
		}
		return found;
	}

	// The blocks in the square of the given half side round the place, from its layer up to 6 above it.
	private static int BlocksAround(XYZ at, int half)
	{
		int blocks = 0;
		for (int x = at.x - half; x <= at.x + half; x++)
		{
			for (int z = at.z - half; z <= at.z + half; z++)
			{
				for (int y = at.y - 1; y <= at.y + 6; y++)
				{
					if (Voxel.IsBlock(new XYZ(x, y, z)))
					{
						blocks++;
					}
				}
			}
		}
		return blocks;
	}

	private static HashSet<XYZ> RuinCells()
	{
		List<XYZ> cells = new List<XYZ>();
		TreasureRuins.BuiltCells(cells);
		return new HashSet<XYZ>(cells);
	}

	// A chest put on the site as a new world's are (with its ruin and guardians as the switches say); the clean-up
	// takes the chest, its guardians, their raid and the ruin's blocks away again. Returns where the chest stands.
	private static Vector3 PutChest(TestContext t, XYZ site, HashSet<Labor> unitsBefore)
	{
		HashSet<XYZ> ruinBefore = RuinCells();
		List<Vector3> placed = TreasureChests.Populate(new List<Vector3> { site.ToVector3() }, new System.Random(179));
		t.Equal(1, placed.Count, "Chests placed");
		Vector3 at = placed[0];
		t.Log("A chest at " + at);
		// Last: the ruin's blocks, top down (after the units and the chest are gone).
		t.Cleanup(() =>
		{
			List<XYZ> cells = new List<XYZ>();
			foreach (XYZ cell in RuinCells())
			{
				if (!ruinBefore.Contains(cell))
				{
					cells.Add(cell);
				}
			}
			cells.Sort((a, b) => b.y.CompareTo(a.y));
			foreach (XYZ cell in cells)
			{
				if (!Voxel.IsBlock(cell))
				{
					continue;
				}
				using (IBlockData block = Voxel.GetBlockData(cell))
				{
					if (block != null && !block.IsNull)
					{
						block.RootOrSelf.DeBlock();
					}
				}
			}
		});
		t.Cleanup(() =>
		{
			GameObject chest = TreasureChests.ChestObject(at);
			TreasureChests.Forget(at);
			Scenario.Release(chest);
		});
		// The guardians' raid goes after its guardians (clean-ups run last first).
		List<GameObject> raids = new List<GameObject>();
		t.Cleanup(() =>
		{
			foreach (GameObject raid in raids)
			{
				Scenario.Release(raid);
			}
		});
		foreach (Labor labor in NewUnits(unitsBefore, at, GuardReach))
		{
			Scenario.Adopt(t, labor);
			Project project = labor.Project;
			if (project != null && TreasureChests.IsGuardRaid(project.gameObject) && !raids.Contains(project.gameObject))
			{
				raids.Add(project.gameObject);
			}
		}
		return at;
	}

	// The ruin's blocks still waiting for the blocks under them are made (Treasure's component, every frame).
	private static IEnumerator RuinBuilt(TestContext t)
	{
		yield return t.Until(() => !TreasureRuins.Busy, 30f, "the ruin's blocks to be laid");
		yield return t.Frames(2);
	}

	[GameTest("Treasure", "With Treasure switched off while the game runs, a level that starts no longer sets about placing chests, ruins or guardians (#179, #182)", TimeoutSeconds = 30f)]
	private static IEnumerator TreasureOffLevelPlacesNothing(TestContext t)
	{
		// The level's own placing is run again here: only on Test Island, whose chests the tests keep off.
		if (!NoTreasureOnTestIslandPatch.IsTestIsland(GameParam.Map))
		{
			t.Skip("Not on Test Island: a level's placing would put chests into this world");
		}
		if (!Plugin.IsEnabled(Features.Treasure))
		{
			t.Skip("Treasure is off at the start (its level hook was never made)");
		}
		bool wasActive = TreasureChests.Active;
		Switches(t, true, true, true);
		// The level's placing as it was, with Treasure on as it was (this clean-up runs before the switches are put back).
		t.Cleanup(() =>
		{
			FeatureToggle.Set(Features.Treasure, true);
			TreasureChests.LevelReady();
			if (TreasureChests.Active != wasActive)
			{
				DevToolsPlugin.Log.LogWarning("Tests: the treasure's chests are " + (TreasureChests.Active ? "watched" : "not watched") + " after the treasure test, " + (wasActive ? "watched" : "not watched") + " before");
			}
		});
		// On: the level sets about placing them (Test Island then gets none).
		int calls = NoTreasureOnTestIslandPatch.Calls;
		TreasureChests.LevelReady();
		if (NoTreasureOnTestIslandPatch.Calls == calls)
		{
			t.Skip("This game's mode gets no treasure chests");
		}
		t.True(TreasureChests.Active, "The chests are not watched with Treasure on");
		// Switched off while the game runs (its hook stays): the level does not set about it, and nothing is watched.
		FeatureToggle.Set(Features.Treasure, false);
		calls = NoTreasureOnTestIslandPatch.Calls;
		TreasureChests.LevelReady();
		t.Equal(calls, NoTreasureOnTestIslandPatch.Calls, "Times the level set about placing chests with Treasure off");
		t.False(TreasureChests.Active, "The chests are watched with Treasure off");
		int chests = 0;
		foreach (object[] chest in TreasureChests.Describe())
		{
			chests++;
		}
		t.Equal(0, chests, "Chests with Treasure off");
		t.True(GuardianSweep.Wanted, "Guardians from a save are kept with Treasure off");
		yield return null;
	}

	[GameTest("Treasure", "With Treasure off, a new world's chest spots get no chest, no ruin and no guardians or corruptrons (#179, #182)", TimeoutSeconds = 60f)]
	private static IEnumerator TreasureOffPutsNothing(TestContext t)
	{
		XYZ site = Scenario.Site(t, 25, 25);
		Switches(t, false, true, true);
		HashSet<Labor> before = new HashSet<Labor>(Live<Labor>.Active());
		HashSet<XYZ> ruinBefore = RuinCells();
		int blocksBefore = BlocksAround(site, TreasureRuins.Radius + 4);
		List<Vector3> placed = TreasureChests.Populate(new List<Vector3> { site.ToVector3() }, new System.Random(179));
		yield return t.Seconds(2f);
		t.Equal(0, placed.Count, "Chests placed with Treasure off");
		foreach (Vector3 at in placed)
		{
			TreasureChests.Forget(at);
		}
		t.Equal(ruinBefore.Count, RuinCells().Count, "Ruin cells built");
		t.False(TreasureRuins.Busy, "Ruin blocks are waiting to be laid");
		t.Equal(blocksBefore, BlocksAround(site, TreasureRuins.Radius + 4), "Blocks round the spot");
		List<Labor> units = NewUnits(before, site.ToVector3(), GuardReach);
		foreach (Labor labor in units)
		{
			Scenario.Adopt(t, labor);
			t.Log("New unit by the spot: " + labor.name + " (" + ((labor.faction != null) ? labor.faction.name : "no faction") + ")");
		}
		t.Equal(0, units.Count, "New units by the spot");
	}

	[GameTest("Treasure", "With TreasureRuins off, a chest stands on open ground: no ruin's block is laid, and it still gets its guardians (#179)", TimeoutSeconds = 90f)]
	private static IEnumerator RuinsOffBuildsNoRuin(TestContext t)
	{
		if (Scenario.EnemyFaction() == null)
		{
			t.Skip("No enemy faction in this game");
		}
		XYZ site = Scenario.Site(t, 25, 25);
		Switches(t, true, false, true);
		HashSet<Labor> before = new HashSet<Labor>(Live<Labor>.Active());
		HashSet<XYZ> ruinBefore = RuinCells();
		int blocksBefore = BlocksAround(site, TreasureRuins.Radius + 4);
		Vector3 at = PutChest(t, site, before);
		yield return RuinBuilt(t);
		t.Equal(ruinBefore.Count, RuinCells().Count, "Ruin cells built with TreasureRuins off");
		t.Equal(blocksBefore, BlocksAround(site, TreasureRuins.Radius + 4), "Blocks round the chest");
		int guardians = 0;
		foreach (Labor labor in NewUnits(before, at, GuardReach))
		{
			if (Beasts.KindOf(labor) != null)
			{
				guardians++;
			}
		}
		t.Record("guardians", guardians);
		t.AtLeast(2f, guardians, "Guardians by the chest");
		if (TreasureChests.Active)
		{
			yield return t.Until(() => TreasureChests.ChestObject(at) != null, 10f, "the chest to be made");
		}
	}

	[GameTest("Treasure", "With TreasureGuardians off, a chest gets its ruin but no guardians and no corruptrons in it (#182)", TimeoutSeconds = 90f)]
	private static IEnumerator GuardiansOffSpawnsNoGuards(TestContext t)
	{
		XYZ site = Scenario.Site(t, 25, 25);
		Switches(t, true, true, false);
		HashSet<Labor> before = new HashSet<Labor>(Live<Labor>.Active());
		HashSet<XYZ> ruinBefore = RuinCells();
		Vector3 at = PutChest(t, site, before);
		yield return RuinBuilt(t);
		int ruin = RuinCells().Count - ruinBefore.Count;
		t.Record("ruinCells", ruin);
		t.True(ruin > 0, "The chest got no ruin with TreasureRuins on");
		List<Labor> units = NewUnits(before, at, GuardReach);
		foreach (Labor labor in units)
		{
			t.Log("New unit by the chest: " + labor.name + " (" + ((labor.faction != null) ? labor.faction.name : "no faction") + ")");
		}
		t.Equal(0, units.Count, "New units by the chest with TreasureGuardians off");
		if (TreasureChests.Active)
		{
			yield return t.Until(() => TreasureChests.ChestObject(at) != null, 10f, "the chest to be made");
		}
	}

	[GameTest("Treasure", "With TreasureGuardians or Treasure off, the guardians a saved world has are taken away and its other enemies stay (#181, #182)", TimeoutSeconds = 60f)]
	private static IEnumerator SavedGuardiansTakenAway(TestContext t)
	{
		Faction enemy = Scenario.EnemyFaction();
		if (enemy == null)
		{
			t.Skip("No enemy faction in this game");
		}
		XYZ site = Scenario.Site(t, 9, 9);
		Switches(t, true, true, true);
		t.False(GuardianSweep.Wanted, "Guardians are taken away with every switch on");
		// A guardian as one loads from a save (a unit with a beast's variant), and an ordinary corruptron.
		Labor guardian = Beasts.Spawn(BeastKind.Boulderback, enemy, Scenario.Walkable(new XYZ(site.x - 2, site.y, site.z)).ToVector3(), 1);
		t.NotNull(guardian, "The guardian");
		Scenario.Adopt(t, guardian);
		Labor corruptron = Scenario.Corruptron(t, new XYZ(site.x + 2, site.y, site.z));
		corruptron.Autonomy = AutonomyStatus.HoldingPosition;
		guardian.Autonomy = AutonomyStatus.HoldingPosition;
		yield return t.Frames(5);
		FeatureToggle.Set(Features.TreasureGuardians, false);
		t.True(GuardianSweep.Wanted, "Guardians are kept with TreasureGuardians off");
		FeatureToggle.Set(Features.TreasureGuardians, true);
		FeatureToggle.Set(Features.Treasure, false);
		t.True(GuardianSweep.Wanted, "Guardians are kept with Treasure off");
		int removed = GuardianSweep.Sweep(new List<Labor> { guardian, corruptron });
		yield return t.Frames(3);
		t.Equal(1, removed, "Units taken away");
		t.False(Scenario.Alive(guardian), "The guardian is still there");
		t.True(Scenario.Alive(corruptron), "The ordinary corruptron was taken away");
	}
}
