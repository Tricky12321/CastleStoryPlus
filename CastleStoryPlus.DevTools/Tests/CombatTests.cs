using System.Collections;
using System.Collections.Generic;
using Brix.Engine;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.AI.Damage;
using Brix.Game.Components;
using CastleStoryPlus.DevTools.Endpoints;
using CastleStoryPlus.DevTools.Testing;
using UnityEngine;

namespace CastleStoryPlus.DevTools.Tests;

// Fighting, set up with the test's own units on open ground near the home crystal: soldiers attack enemies in range
// on their own (LaborInstinct) and on an order, corruptrons attack the player's bricktrons and march on the home
// crystal in a raid, and bricktrons of two hostile player factions fight each other.
[ParallelTests("Combat")]
internal static class CombatTests
{
	[GameTest("Combat", "A knight and a corruptron 4 voxels apart: the knight attacks on its own and kills it", TimeoutSeconds = 180f)]
	private static IEnumerator KnightKillsCorruptron(TestContext t)
	{
		XYZ site = Scenario.Site(t, 10, 6);
		Labor knight = Scenario.Bricktron(t, Occupation.Type.Knight, new XYZ(site.x - 2, site.y, site.z));
		Labor corruptron = Scenario.Corruptron(t, new XYZ(site.x + 2, site.y, site.z));
		yield return t.Frames(5);
		float corruptronMax = Scenario.Hp(corruptron);
		t.Record("knightHp", Scenario.Hp(knight));
		t.Record("corruptronHp", corruptronMax);
		float started = Time.time;
		yield return t.UntilGame(() => Scenario.Hp(corruptron) < corruptronMax, 60f, "the knight to hit the corruptron");
		t.Record("firstHitAfter", Mathf.Round(Time.time - started));
		yield return t.UntilGame(() => !Scenario.Alive(corruptron) || !Scenario.Alive(knight), 120f, "one of them to die");
		t.Record("knightHpLeft", Scenario.Hp(knight));
		t.True(Scenario.Alive(knight), "The knight died against a level 1 corruptron");
		t.False(Scenario.Alive(corruptron), "The corruptron still lives");
	}

	[GameTest("Combat", "An archer 9 voxels from a corruptron shoots it", TimeoutSeconds = 180f)]
	private static IEnumerator ArcherShootsCorruptron(TestContext t)
	{
		XYZ site = Scenario.Site(t, 14, 6);
		Labor archer = Scenario.Bricktron(t, Occupation.Type.Archer, new XYZ(site.x - 5, site.y, site.z));
		Labor corruptron = Scenario.Corruptron(t, new XYZ(site.x + 4, site.y, site.z));
		yield return t.Frames(5);
		float before = Scenario.Hp(corruptron);
		yield return t.UntilGame(() => Scenario.Hp(corruptron) < before, 60f, "an arrow to hit the corruptron");
		t.Record("corruptronHp", Scenario.Hp(corruptron) + " of " + before);
		t.True(Scenario.Alive(archer) || Scenario.Hp(corruptron) < before, "The archer neither hit nor survived");
	}

	[GameTest("Combat", "A knight ordered to attack a corruptron 14 voxels away walks to it and hits it", TimeoutSeconds = 180f)]
	private static IEnumerator OrderedAttack(TestContext t)
	{
		XYZ site = Scenario.Site(t, 18, 6);
		Labor knight = Scenario.Bricktron(t, Occupation.Type.Knight, new XYZ(site.x - 7, site.y, site.z));
		Labor corruptron = Scenario.Corruptron(t, new XYZ(site.x + 7, site.y, site.z));
		yield return t.Frames(5);
		float before = Scenario.Hp(corruptron);
		Scenario.ProjectCommands.CmdAttack(knight.gameObject, corruptron.gameObject);
		yield return t.UntilGame(() => Scenario.Hp(corruptron) < before, 90f, "the ordered knight to hit the corruptron");
		t.Record("distanceAtHit", Mathf.Round(Vector3.Distance(knight.transform.position, corruptron.transform.position) * 10f) / 10f);
	}

	[GameTest("Combat", "A corruptron next to a builder attacks it", TimeoutSeconds = 120f)]
	private static IEnumerator CorruptronAttacksBricktron(TestContext t)
	{
		XYZ site = Scenario.Site(t, 8, 6);
		Labor builder = Scenario.Bricktron(t, Occupation.Type.Builder, new XYZ(site.x - 1, site.y, site.z));
		yield return t.Frames(5);
		float before = Scenario.Hp(builder);
		Labor corruptron = Scenario.Corruptron(t, new XYZ(site.x + 2, site.y, site.z));
		yield return t.UntilGame(() => Scenario.Hp(builder) < before, 60f, "the corruptron to hit the builder");
		t.Record("builderHp", Scenario.Hp(builder) + " of " + before);
		t.True(Scenario.Alive(corruptron), "The corruptron died attacking a builder");
	}

	[GameTest("Combat", "A corruptron dies: it is gone, the game counts the death (UnitDied) and a firefly with its energy is made", TimeoutSeconds = 60f)]
	private static IEnumerator CorruptronDeath(TestContext t)
	{
		XYZ site = Scenario.Site(t, 6, 6);
		Labor corruptron = Scenario.Corruptron(t, new XYZ(site.x, site.y, site.z));
		yield return t.Frames(5);
		GameObject victim = corruptron.gameObject;
		bool counted = false;
		System.Action<GameObject, GameObject> onDied = (GameObject died, GameObject killer) =>
		{
			if (died == victim)
			{
				counted = true;
			}
		};
		var connection = GameSignals.Connect(GameSignals.UnitDied, onDied);
		t.Cleanup(() => connection.Disconnect());
		int firefliesBefore = Object.FindObjectsOfType<Firefly>().Length;
		IDamageReceiver health = Scenario.Health(corruptron);
		t.NotNull(health, "The corruptron's damage receiver");
		health.Kill();
		yield return t.UntilGame(() => !Scenario.Alive(corruptron), 20f, "the corruptron to die");
		yield return t.UntilGame(() => counted, 5f, "the game's UnitDied signal");
		yield return t.UntilGame(() => Object.FindObjectsOfType<Firefly>().Length > firefliesBefore, 15f, "the corruptron's firefly (its energy back to the crystal)");
	}

	[GameTest("Combat", "Three corruptrons in a raid march on the home crystal and damage it (reach it, when the game mode makes it invincible)", TimeoutSeconds = 300f, Exclusive = true)]
	private static IEnumerator RaidDamagesHomeCrystal(TestContext t)
	{
		FireflyNest home = WorldEndpoints.HomeNest(Scenario.Player);
		t.NotNull(home, "The home crystal");
		DamageReceiver crystal = home.GetComponent<DamageReceiver>();
		t.NotNull(crystal, "The home crystal's damage receiver");
		float before = crystal.CurrentHP;
		// The crystal's own hit points back afterwards: the test must not cost the world its crystal.
		t.Cleanup(() =>
		{
			if (crystal != null && crystal.CurrentHP > 0f)
			{
				crystal.CurrentHP = before;
			}
		});
		XYZ homeVoxel = Scenario.Home();
		XYZ start = Scenario.Site(t, 6, 6, 4, 16);
		t.Log("Raid from " + start + " to the crystal at " + homeVoxel + " (" + before + " hp)");
		List<Labor> raiders = new List<Labor>();
		for (int i = 0; i < 3; i++)
		{
			raiders.Add(Scenario.Corruptron(t, new XYZ(start.x + i, start.y, start.z)));
		}
		yield return t.Frames(5);
		RaidProject raid = CheatEndpoints.NewRaid(Scenario.EnemyFaction(), home.transform.position);
		t.Cleanup(() =>
		{
			if (raid != null && raid.gameObject.activeInHierarchy)
			{
				Scenario.Release(raid.gameObject);
			}
		});
		foreach (Labor raider in raiders)
		{
			CheatEndpoints.Hire(raid, raider);
		}
		// The player's own bricktrons near the crystal may fight them first; any damage to the crystal counts.
		t.Record("crystalInvincible", crystal.IsInvincible);
		float end = Time.time + 180f;
		float nextLog = Time.time;
		while (crystal.CurrentHP >= before && !raiders.TrueForAll(r => !Scenario.Alive(r)) && Time.time < end && !(crystal.IsInvincible && raiders.Exists(r => Scenario.Alive(r) && Vector3.Distance(r.transform.position, home.transform.position) <= 6f)))
		{
			if (Time.time >= nextLog)
			{
				nextLog = Time.time + 20f;
				List<string> state = new List<string>();
				foreach (Labor raider in raiders)
				{
					state.Add(Scenario.Alive(raider) ? (Mathf.RoundToInt(Vector3.Distance(raider.transform.position, home.transform.position)) + " from the crystal, " + raider.Activity + ", " + Scenario.Hp(raider) + " hp") : "dead");
				}
				t.Log("Raiders: " + string.Join("; ", state.ToArray()));
			}
			yield return null;
		}
		t.Record("crystalHp", crystal.CurrentHP + " of " + before);
		float nearest = float.MaxValue;
		foreach (Labor raider in raiders)
		{
			if (Scenario.Alive(raider))
			{
				nearest = Mathf.Min(nearest, Vector3.Distance(raider.transform.position, home.transform.position));
			}
		}
		t.Record("nearestRaider", (nearest < float.MaxValue) ? (object)Mathf.RoundToInt(nearest) : "all dead");
		if (crystal.IsInvincible)
		{
			// The game mode keeps the home crystal from harm (sandbox): the raid only has to get there.
			t.AtMost(6f, nearest, "Distance of the nearest raider to the invincible home crystal");
		}
		else
		{
			t.True(crystal.CurrentHP < before, "The raid did not damage the crystal (nearest raider " + nearest + " away)");
		}
	}

	[GameTest("Combat", "Knights of two hostile bricktron factions (Blue and Red made enemies) fight until one dies", TimeoutSeconds = 240f)]
	private static IEnumerator RivalFactionsFight(TestContext t)
	{
		Faction blue = Scenario.Player;
		// The game mode's other colours (Red, Green, Orange) exist without players: any faction that is not the
		// player's, the corruptrons', the spectators' or the neutral one.
		Faction red = null;
		List<string> seen = new List<string>();
		foreach (Faction faction in Faction.GetAllFactions())
		{
			if (faction == null)
			{
				continue;
			}
			seen.Add(faction.name + (faction.isAI ? " (AI)" : string.Empty) + (faction.IsSpectator ? " (spectator)" : string.Empty));
			if (red == null && faction != blue && !faction.isAI && !faction.IsSpectator && faction != Faction.DefaultNeutral && faction != Scenario.EnemyFaction())
			{
				red = faction;
			}
		}
		t.Record("factions", seen);
		if (red == null)
		{
			t.Skip("No second bricktron faction in this game (" + string.Join(", ", seen.ToArray()) + ")");
		}
		MakeEnemies(t, blue, red);
		XYZ site = Scenario.Site(t, 10, 6);
		Labor ours = Scenario.Bricktron(t, Occupation.Type.Knight, new XYZ(site.x - 2, site.y, site.z));
		Labor theirs = Scenario.Spawn(t, Test.Bricktron, red, Occupation.Type.Knight, new XYZ(site.x + 2, site.y, site.z));
		yield return t.Frames(5);
		t.True(Affiliation.IsEnemy(ours.gameObject, theirs.gameObject), "The two knights are not enemies");
		float oursMax = Scenario.Hp(ours);
		float theirsMax = Scenario.Hp(theirs);
		t.Record("enemyListsBuilt", blue.EnemyFactions.Contains(red) && red.EnemyFactions.Contains(blue));
		// Units of a faction without a player have no fog of war of their own to see enemies in, so they may not
		// start a fight by instinct: given 15 game seconds, else both are ordered to attack, as the right click does.
		float instinctEnd = Time.time + 15f;
		while (Time.time < instinctEnd && Scenario.Hp(ours) >= oursMax && Scenario.Hp(theirs) >= theirsMax)
		{
			yield return null;
		}
		bool byInstinct = Scenario.Hp(ours) < oursMax || Scenario.Hp(theirs) < theirsMax;
		t.Record("startedByInstinct", byInstinct);
		if (!byInstinct)
		{
			Order.Combat.GoAttackDirectly.Send(ours, theirs.gameObject, Labor.DecisionSource.User);
			Order.Combat.GoAttackDirectly.Send(theirs, ours.gameObject, Labor.DecisionSource.User);
		}
		yield return t.UntilGame(() => Scenario.Hp(ours) < oursMax || Scenario.Hp(theirs) < theirsMax, 60f, "a first blow between the factions");
		yield return t.UntilGame(() => !Scenario.Alive(ours) || !Scenario.Alive(theirs), 180f, "one knight to die");
		t.Record("winner", Scenario.Alive(ours) ? blue.name : red.name);
		t.Record("winnerHp", Mathf.Max(Scenario.Hp(ours), Scenario.Hp(theirs)));
	}

	[GameTest("Combat", "A level 5 corruptron has more hit points than a level 1 one", TimeoutSeconds = 30f)]
	private static IEnumerator HigherLevelHasMoreHp(TestContext t)
	{
		XYZ site = Scenario.Site(t, 8, 6);
		Labor weak = Scenario.Corruptron(t, new XYZ(site.x - 2, site.y, site.z), 1);
		Labor strong = Scenario.Corruptron(t, new XYZ(site.x + 2, site.y, site.z), 5);
		yield return t.Frames(5);
		t.Record("level1", Scenario.Health(weak).MaxHP);
		t.Record("level5", Scenario.Health(strong).MaxHP);
		t.True(Scenario.Health(strong).MaxHP > Scenario.Health(weak).MaxHP, "A level 5 corruptron has no more hit points than a level 1 one");
	}

	[GameTest("Combat", "A knight has more hit points than a builder", TimeoutSeconds = 30f)]
	private static IEnumerator KnightHasMoreHpThanBuilder(TestContext t)
	{
		XYZ site = Scenario.Site(t, 8, 6);
		Labor knight = Scenario.Bricktron(t, Occupation.Type.Knight, new XYZ(site.x - 2, site.y, site.z));
		Labor builder = Scenario.Bricktron(t, Occupation.Type.Builder, new XYZ(site.x + 2, site.y, site.z));
		yield return t.Frames(5);
		t.Record("knight", Scenario.Health(knight).MaxHP);
		t.Record("builder", Scenario.Health(builder).MaxHP);
		t.True(Scenario.Health(knight).MaxHP > Scenario.Health(builder).MaxHP, "A knight has no more hit points than a builder");
	}

	[GameTest("Combat", "Three archers 8 voxels away shoot a corruptron dead", TimeoutSeconds = 180f)]
	private static IEnumerator ArchersKillCorruptron(TestContext t)
	{
		XYZ site = Scenario.Site(t, 14, 8);
		List<Labor> archers = new List<Labor>();
		for (int i = -1; i <= 1; i++)
		{
			archers.Add(Scenario.Bricktron(t, Occupation.Type.Archer, new XYZ(site.x - 4, site.y, site.z + i * 2)));
		}
		Labor corruptron = Scenario.Corruptron(t, new XYZ(site.x + 4, site.y, site.z));
		yield return t.UntilGame(() => !Scenario.Alive(corruptron), 120f, "the corruptron to die");
		t.Record("archersAlive", archers.FindAll(a => Scenario.Alive(a)).Count);
	}

	[GameTest("Combat", "Two knights against two corruptrons: both corruptrons die, a knight survives", TimeoutSeconds = 240f)]
	private static IEnumerator KnightsBeatCorruptrons(TestContext t)
	{
		XYZ site = Scenario.Site(t, 12, 8);
		List<Labor> knights = new List<Labor>
		{
			Scenario.Bricktron(t, Occupation.Type.Knight, new XYZ(site.x - 3, site.y, site.z - 1)),
			Scenario.Bricktron(t, Occupation.Type.Knight, new XYZ(site.x - 3, site.y, site.z + 1))
		};
		List<Labor> corruptrons = new List<Labor>
		{
			Scenario.Corruptron(t, new XYZ(site.x + 3, site.y, site.z - 1)),
			Scenario.Corruptron(t, new XYZ(site.x + 3, site.y, site.z + 1))
		};
		yield return t.UntilGame(() => corruptrons.TrueForAll(c => !Scenario.Alive(c)) || knights.TrueForAll(k => !Scenario.Alive(k)), 180f, "one side to be dead");
		t.Record("knightsAlive", knights.FindAll(k => Scenario.Alive(k)).Count);
		t.True(corruptrons.TrueForAll(c => !Scenario.Alive(c)), "The corruptrons won");
	}

	[GameTest("Combat", "Two knights of the same faction side by side do not hurt each other", TimeoutSeconds = 60f)]
	private static IEnumerator SameFactionDoesNotFight(TestContext t)
	{
		XYZ site = Scenario.Site(t, 8, 6);
		Labor a = Scenario.Bricktron(t, Occupation.Type.Knight, new XYZ(site.x - 1, site.y, site.z));
		Labor b = Scenario.Bricktron(t, Occupation.Type.Knight, new XYZ(site.x + 1, site.y, site.z));
		yield return t.Frames(5);
		float aMax = Scenario.Hp(a);
		float bMax = Scenario.Hp(b);
		yield return t.Seconds(20f);
		t.Equal(aMax, Scenario.Hp(a), a.name + "'s hit points");
		t.Equal(bMax, Scenario.Hp(b), b.name + "'s hit points");
	}

	[GameTest("Combat", "Knights of two player factions that are not enemies (Blue and Green, as the game sets them) do not fight", TimeoutSeconds = 60f)]
	private static IEnumerator FactionsNotEnemiesDoNotFight(TestContext t)
	{
		Faction blue = Scenario.Player;
		Faction green = Faction.FindByName("Green");
		if (green == null || green == blue)
		{
			t.Skip("No Green faction in this game");
		}
		XYZ site = Scenario.Site(t, 8, 6);
		Labor ours = Scenario.Bricktron(t, Occupation.Type.Knight, new XYZ(site.x - 1, site.y, site.z));
		Labor theirs = Scenario.Spawn(t, Test.Bricktron, green, Occupation.Type.Knight, new XYZ(site.x + 1, site.y, site.z));
		yield return t.Frames(5);
		t.Record("relation", Affiliation.GetRelation(blue, green).ToString());
		t.False(Affiliation.IsEnemy(ours.gameObject, theirs.gameObject), "Blue and Green are enemies in this game");
		float oursMax = Scenario.Hp(ours);
		float theirsMax = Scenario.Hp(theirs);
		yield return t.Seconds(20f);
		t.Equal(oursMax, Scenario.Hp(ours), "The Blue knight's hit points");
		t.Equal(theirsMax, Scenario.Hp(theirs), "The Green knight's hit points");
	}

	[GameTest("Combat", "A corruptron killed by a knight leaves a corpse", TimeoutSeconds = 120f)]
	private static IEnumerator KilledCorruptronLeavesCorpse(TestContext t)
	{
		XYZ site = Scenario.Site(t, 10, 6);
		int corpsesBefore = CorpsesNear(site);
		Scenario.Bricktron(t, Occupation.Type.Knight, new XYZ(site.x - 2, site.y, site.z));
		Labor corruptron = Scenario.Corruptron(t, new XYZ(site.x + 2, site.y, site.z));
		yield return t.UntilGame(() => !Scenario.Alive(corruptron), 90f, "the knight to kill the corruptron");
		yield return t.UntilGame(() => CorpsesNear(site) > corpsesBefore, 10f, "the corpse");
	}

	[GameTest("Combat", "A knight kills a minitron (the small corruptron)", TimeoutSeconds = 120f)]
	private static IEnumerator KnightKillsMinitron(TestContext t)
	{
		Faction enemy = Scenario.EnemyFaction();
		if (enemy == null)
		{
			t.Skip("No enemy faction in this game");
		}
		XYZ site = Scenario.Site(t, 10, 6);
		Labor knight = Scenario.Bricktron(t, Occupation.Type.Knight, new XYZ(site.x - 2, site.y, site.z));
		Labor minitron = Scenario.Spawn(t, Test.Minitron, enemy, Occupation.Type.Minitron, new XYZ(site.x + 2, site.y, site.z));
		yield return t.UntilGame(() => !Scenario.Alive(minitron), 90f, "the minitron to die");
		t.True(Scenario.Alive(knight), "The knight died against a minitron");
	}

	private static int CorpsesNear(XYZ site)
	{
		int count = 0;
		foreach (Corpse corpse in Object.FindObjectsOfType<Corpse>())
		{
			if (corpse != null && corpse.gameObject.activeInHierarchy && Vector3.Distance(corpse.transform.position, site.ToVector3()) < 15f)
			{
				count++;
			}
		}
		return count;
	}

	// Both ways, as the server keeps each faction's enemy list (Faction.EnemyFactions) from its own side; put back
	// as they were by the clean-up.
	private static void MakeEnemies(TestContext t, Faction a, Faction b)
	{
		bool aKnew = a.AffiliationMap.TryGetValue(b, out Affiliation.Relation aBefore);
		bool bKnew = b.AffiliationMap.TryGetValue(a, out Affiliation.Relation bBefore);
		a.SetFactionRelation(b, Affiliation.Relation.Enemy);
		b.SetFactionRelation(a, Affiliation.Relation.Enemy);
		t.Cleanup(() =>
		{
			Restore(a, b, aKnew, aBefore);
			Restore(b, a, bKnew, bBefore);
		});
		t.Log(a.name + " and " + b.name + " made enemies");
	}

	private static void Restore(Faction faction, Faction other, bool known, Affiliation.Relation before)
	{
		faction.SetFactionRelation(other, known ? before : Affiliation.Relation.Undefined);
		if (!known)
		{
			faction.AffiliationMap.Remove(other);
		}
	}
}
