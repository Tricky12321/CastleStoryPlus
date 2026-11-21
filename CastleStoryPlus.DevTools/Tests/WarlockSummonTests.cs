using System.Collections;
using System.Collections.Generic;
using Brix.Engine;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Game.Components.Wards;
using CastleStoryPlus.Core;
using CastleStoryPlus.DevTools.Endpoints;
using CastleStoryPlus.DevTools.Testing;
using CastleStoryPlus.Invasion;
using UnityEngine;

namespace CastleStoryPlus.DevTools.Tests;

// A warlock's summons are part of its wave (#161): the units a warlock summons join its raid, so the wave (the raid)
// is not over while they live, and the scoreboard's tally counts them in what the wave has had.
[ParallelTests("Combat")]
internal static class WarlockSummonTests
{
	// A wave number no real game reaches, so the tally of a real wave is not touched.
	private const int TestWave = 9161;

	// A raid of wave TestWave on open ground (its beacon where it stands, so it does not march off): a warlock and two
	// corruptrons. The warlock summons (its own MagitronRez, as its AI does when outnumbered); its summons must join the
	// raid. Then the warlock and the two corruptrons are killed: the raid must still be there with the summons in it.
	// When the summons are killed too, the raid ends (the wave is over).
	[GameTest("Combat", "A warlock's summons join its wave's raid: with the rest of the wave dead the wave is not over until they are (#161)", TimeoutSeconds = 120f)]
	private static IEnumerator SummonsKeepTheWave(TestContext t)
	{
		if (!Plugin.IsEnabled(Features.WarlockSummons))
		{
			t.Skip("WarlockSummons is off");
		}
		Faction enemy = Scenario.EnemyFaction();
		if (enemy == null)
		{
			t.Skip("No enemy faction in this game");
		}
		XYZ site = Scenario.Site(t, 24, 24);
		Labor warlock = Scenario.Spawn(t, Test.Magitron, enemy, Occupation.Type.Magitron, new XYZ(site.x, site.y, site.z));
		List<Labor> own = new List<Labor> { warlock };
		own.Add(Scenario.Corruptron(t, new XYZ(site.x + 2, site.y, site.z)));
		own.Add(Scenario.Corruptron(t, new XYZ(site.x - 2, site.y, site.z)));
		MagitronRez rez = warlock.GetComponent<MagitronRez>();
		t.NotNull(rez, "The warlock's MagitronRez");
		yield return t.Frames(5);

		RaidProject raid = CheatEndpoints.NewRaid(enemy, site.ToVector3());
		System.Func<bool> gone = () => raid == null || raid.gameObject.IsNullOrReleased() || !raid.gameObject.activeInHierarchy;
		t.Cleanup(() =>
		{
			if (!gone())
			{
				Scenario.Release(raid.gameObject);
			}
		});
		t.NotNull(raid.data, "The raid's data");
		raid.data.NetworkWaveNumber = TestWave;
		foreach (Labor labor in own)
		{
			CheatEndpoints.Hire(raid, labor);
		}
		t.Equal(3, raid.CrewCount(), "Units in the raid before the summon");
		int corruptronsBefore = WarlockSummons.Tally.SummonedOf(TestWave, 1);
		int minitronsBefore = WarlockSummons.Tally.SummonedOf(TestWave, 0);
		// The scoreboard's tally sees the wave's own two corruptrons first.
		WarlockSummons.Tally.Seen(TestWave, 1, 2);

		rez.SpawnCorruptrons();
		yield return t.UntilGame(() => raid.CrewCount() >= 6, 10f, "the warlock's three summons to join its raid", 2f, () => t.Log("Raid crew: " + raid.CrewCount()));
		List<Labor> summons = new List<Labor>();
		foreach (Labor labor in raid.Crew())
		{
			if (!own.Contains(labor))
			{
				summons.Add(labor);
				Scenario.Adopt(t, labor);
			}
		}
		t.Record("summons", summons.Count);
		t.AtLeast(3f, summons.Count, "Summons in the warlock's raid");
		// A summon is a corruptron and two minitrons (more when the warlock's own AI summoned meanwhile too).
		int corruptrons = summons.FindAll(s => WarlockSummons.KindOf(s) == 1).Count;
		int minitrons = summons.FindAll(s => WarlockSummons.KindOf(s) == 0).Count;
		t.AtLeast(1f, corruptrons, "Corruptrons among the summons");
		t.AtLeast(2f, minitrons, "Minitrons among the summons");
		t.Equal(corruptronsBefore + corruptrons, WarlockSummons.Tally.SummonedOf(TestWave, 1), "Corruptrons summoned into the test wave");
		t.Equal(minitronsBefore + minitrons, WarlockSummons.Tally.SummonedOf(TestWave, 0), "Minitrons summoned into the test wave");
		foreach (Labor summon in summons)
		{
			t.True(summon.faction == enemy, "A summon is not of the enemy faction");
			t.True(summon.Autonomy == AutonomyStatus.LockedToOperable, "A summon is not locked to the raid (" + summon.Autonomy + ")");
		}

		// The warlock first, so it summons no more.
		foreach (Labor labor in own)
		{
			Scenario.Health(labor).Kill();
		}
		yield return t.UntilGame(() => own.TrueForAll(l => !Scenario.Alive(l)), 20f, "the warlock and the wave's own corruptrons to die");
		yield return t.Frames(10);
		t.False(gone(), "The wave's raid ended while the warlock's summons were alive");
		t.Equal(summons.Count, raid.CrewCount(), "Units left in the wave's raid (the summons)");
		// What the wave has had: its two corruptrons and the summoned one, although one is left.
		t.Equal(2 + WarlockSummons.Tally.SummonedOf(TestWave, 1), WarlockSummons.Tally.Seen(TestWave, 1, 1), "Corruptrons the wave has had");

		foreach (Labor summon in summons)
		{
			if (Scenario.Alive(summon))
			{
				Scenario.Health(summon).Kill();
			}
		}
		yield return t.UntilGame(() => gone() || raid.CrewCount() == 0, 20f, "the summons to die and leave the raid");
		yield return t.UntilGame(gone, 10f, "the empty raid to end (the wave is over)");
	}
}
