using Brix.Game;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine.Networking;

namespace CastleStoryPlus.Upgrades;

// Syncs the tiers by appending them to Faction's own network data, in both the initial state and every update.
// Both ends must run the mod (modded and unmodded peers cannot play together).
[Feature(Features.Upgrades, Features.UpgradesInfo)]
[HarmonyPatch(typeof(Faction), nameof(Faction.OnSerialize))]
internal static class TeamUpgradesNetwork
{
	// A sync var bit the game does not use (Faction uses 0x01-0x1000); setting it makes UNET send an update.
	public const uint DirtyBit = 0x40000000u;

	private static void Postfix(Faction __instance, NetworkWriter writer, ref bool __result)
	{
		TeamUpgrades.Write(__instance, writer);
		__result = true;
	}
}

[Feature(Features.Upgrades, Features.UpgradesInfo)]
[HarmonyPatch(typeof(Faction), nameof(Faction.OnDeserialize))]
internal static class TeamUpgradesNetworkRead
{
	private static void Postfix(Faction __instance, NetworkReader reader)
	{
		TeamUpgrades.Read(__instance, reader);
	}
}
