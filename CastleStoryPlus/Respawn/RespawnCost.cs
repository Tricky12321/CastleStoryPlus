using Brix.Game.Components;
using CastleStoryPlus.Core;
using HarmonyLib;

namespace CastleStoryPlus.Respawn;

// Server: a dead bricktron's firefly respawns for a flat amount of energy, not the price of the next new bricktron
// (which grows with the number of bricktrons). New bricktrons keep the game's growing price.
[Feature(Features.RespawnCost, Features.RespawnCostInfo)]
[HarmonyPatch(typeof(FireflyNest), "FeedQueuedFireflies")]
internal static class RespawnCost
{
	internal const int Energy = 100;

	internal static bool Enabled;

	private static void Enable()
	{
		Enabled = true;
	}

	// The game's loop, with the flat price.
	private static bool Prefix(FireflyNest __instance)
	{
		int available = __instance.GetAvailablePurifiedEnergy();
		while (available >= Energy)
		{
			Firefly firefly = __instance.GetFireflyOfCondition((Firefly ff) => ff.queued && !ff.IsSpawning && ff.captureTimer.Check());
			if (firefly == null)
			{
				break;
			}
			__instance.ConsumeUnnamedFirefliesUpTo(Energy);
			__instance.SpawnBricktronFromFirefly(firefly);
			available -= Energy;
		}
		return false;
	}
}

// A player's dead bricktron carries no refund home (by default the game refunds the full price of a new bricktron,
// which would pay for the respawn and more), so the respawn really costs the flat price. Fireflies of AI factions
// keep their refund: they go to the killer as plain energy.
[Feature(Features.RespawnCost, Features.RespawnCostInfo)]
[HarmonyPatch(typeof(Corpse), "GetRefundEnergy")]
internal static class RespawnCostRefundPatch
{
	private static void Postfix(Corpse __instance, ref int __result)
	{
		if (__instance.originalFaction != null && !__instance.originalFaction.isAI)
		{
			__result = 0;
		}
	}
}
