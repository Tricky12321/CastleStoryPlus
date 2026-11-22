using System.Collections.Generic;
using Brix.Game.AI;
using CastleStoryPlus.Core;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.Treasure;

// The switches for the parts of the treasure (#179, #182): the ruin round each chest and its guardians. Each is
// asked by the host when it places the chests of a new world (TreasureChests.Populate); they have nothing else to
// switch, so these classes only give them their place in the settings.
[Feature(Features.TreasureRuins, Features.TreasureRuinsInfo)]
internal static class TreasureRuinsSwitch
{
}

[Feature(Features.TreasureGuardians, Features.TreasureGuardiansInfo)]
internal static class TreasureGuardiansSwitch
{
}

// A saved world keeps the guardians it had: they are ordinary enemy units the game saves, marked as beasts by their
// variant (Beasts). With Treasure off they lost everything that made them guardians (their model, leash, roaming
// round the chest and the patches that keep their raid out of the invasion's count) and were plain corruptrons and
// biftrons standing in the ruins, which went after any bricktron that came near (#181, #182). So with Treasure or
// TreasureGuardians off, the host takes them away when a level is ready, as the test suite takes its units away: off
// the network and destroyed. Only units with a beast's variant are touched, which only the treasure gives; the
// map's own and the waves' enemies stay. The ruins' blocks stay too: they are the world's blocks now, a player may
// have built with or onto them, and the players can dig them away.
// Always on (it has to work while Treasure is off), host only (the clients see the units go).
[Feature]
internal static class GuardianSweep
{
	private static void Enable()
	{
		GameSession.OnLevelReady(LevelReady);
	}

	// Whether the guardians a world has are to go: the host's switches.
	internal static bool Wanted => !Plugin.IsEnabled(Features.Treasure) || !Plugin.IsEnabled(Features.TreasureGuardians);

	private static void LevelReady()
	{
		if (!NetworkServer.active || !Wanted)
		{
			return;
		}
		int removed = Sweep(Live<Labor>.Active());
		if (removed > 0)
		{
			Plugin.Log.LogInfo("Treasure: took away " + removed + " guardians from the save (" + (Plugin.IsEnabled(Features.Treasure) ? "TreasureGuardians" : "Treasure") + " is off)");
		}
	}

	// Takes the treasure's guardians among the units away; how many. Internal for the DevTools' tests.
	internal static int Sweep(IEnumerable<Labor> units)
	{
		List<GameObject> guardians = new List<GameObject>();
		foreach (Labor labor in units)
		{
			if (labor != null && labor.gameObject.activeInHierarchy && Beasts.KindOf(labor) != null)
			{
				guardians.Add(labor.gameObject);
			}
		}
		foreach (GameObject go in guardians)
		{
			Labor labor = go.GetComponent<Labor>();
			// Off its raid first, so the raid does not keep a crew member that is gone.
			labor.AssignToIdle();
			if (go.GetComponent<NetworkIdentity>() != null)
			{
				NetworkServer.UnSpawn(go);
			}
			// Destroyed, not put back into its pool: a unit taken away alive keeps what the game remembers of it (its
			// targets, its raid) on the pooled object, and the next corruptron taken from the pool would be that object.
			go.SetActive(false);
			Object.Destroy(go);
		}
		return guardians.Count;
	}
}
