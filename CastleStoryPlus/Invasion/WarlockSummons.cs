using Brix.Game.AI;
using Brix.Game.Components.Wards;
using Brix.Game.Network;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.Invasion;

// A warlock's summons belong to its wave (#161). A warlock (magitron) that is outnumbered summons a corruptron and
// two minitrons (MagitronRez.Spawn, through the host's spawn command), but the game left them out of any raid: an
// invasion wave is its raid's crew, and a raid ends (the wave counts as survived, the scoreboard drops its row) when
// its crew is empty, so with only summons left the wave was over and "Enemies left" did not count them. The host now
// hires each summon into the warlock's raid as it is spawned (as the invasion script hires a wave's units): the raid
// stays until the summons are dead too, the scoreboard counts them in the wave's row (the clients get the host's rows
// through WaveCountsSync, unchanged) and in what the wave has had. A summon is never rolled into a Healtron, so a
// warlock summons what it always did. Summons of a wave whose timer ran out stay in their own wave's raid; the next
// wave is a raid of its own. A warlock in no raid (spawned by hand, a treasure chest's guards) summons as before.
[Feature(Features.WarlockSummons, Features.WarlockSummonsInfo)]
[HarmonyPatch]
internal static class WarlockSummons
{
	// What each wave has had, summons included (asked by the scoreboard on the host).
	internal static readonly WaveTally Tally = new WaveTally();

	// The warlock whose MagitronRez.Spawn is running: the unit the spawn command makes meanwhile is its summon.
	private static MagitronRez _summoner;

	private static void Enable()
	{
		GameSession.OnLeave(Forget);
	}

	internal static void Forget()
	{
		Tally.Clear();
		_summoner = null;
	}

	[HarmonyPatch(typeof(MagitronRez), "Spawn")]
	[HarmonyPrefix]
	private static void SummonStarts(MagitronRez __instance)
	{
		_summoner = __instance;
	}

	[HarmonyPatch(typeof(MagitronRez), "Spawn")]
	[HarmonyFinalizer]
	private static void SummonEnds()
	{
		_summoner = null;
	}

	// The host's spawn command makes the unit at once (the warlock's AI runs on the host only); a client's command
	// would reach the host later, outside the warlock's Spawn, and is left alone.
	[HarmonyPatch(typeof(UNetProjectCmd), "SpawnUnit")]
	[HarmonyPostfix]
	private static void Spawned(GameObject __result)
	{
		if (_summoner == null || __result == null || !NetworkServer.active)
		{
			return;
		}
		Join(_summoner.GetComponent<Labor>(), __result.GetComponent<Labor>());
	}

	// The summon into its warlock's raid, as the game's change-project command hires a unit the invasion script assigns.
	internal static bool Join(Labor warlock, Labor summon)
	{
		if (warlock == null || summon == null)
		{
			return false;
		}
		RaidProject raid = warlock.Project as RaidProject;
		if (raid == null || !raid.IsStillAlive || raid.faction != warlock.faction || Treasure.TreasureChests.IsGuardRaid(raid.gameObject))
		{
			return false;
		}
		Combat.Healtron.MarkRolled(summon);
		if (raid.IsFull())
		{
			// One more place (the game's own MaximumCrewSize clamps a raid to 8, which would fire a unit of a bigger one).
			raid.workforceProvider.MaxPopulation = raid.CrewCount() + 1;
		}
		if (!raid.Hire(summon))
		{
			Plugin.Log.LogWarning("WarlockSummons: raid " + raid.name + " did not take " + summon.name);
			return false;
		}
		summon.Autonomy = AutonomyStatus.LockedToOperable;
		int wave = (raid.data != null) ? raid.data.WaveNumber : 0;
		Tally.Summoned(wave, KindOf(summon));
		Plugin.Log.LogInfo("WarlockSummons: " + summon.name + " joined raid " + raid.name + ((wave > 0) ? (" (wave " + wave + ")") : string.Empty));
		return true;
	}

	// The scoreboard's kind of a unit (WaveTally's order), -1 for another.
	internal static int KindOf(Labor labor)
	{
		if (labor == null || labor.Occupation == null)
		{
			return -1;
		}
		switch (labor.Occupation.CurrentOccupation)
		{
			case Occupation.Type.Minitron:
				return 0;
			case Occupation.Type.Corruptron:
				return Combat.Healtron.IsHealtron(labor) ? 2 : 1;
			case Occupation.Type.Biftron:
				return 3;
			case Occupation.Type.Magitron:
				return 4;
			default:
				return -1;
		}
	}
}
