using Brix.Game.Rules;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.Multiplayer;

// The bricktron limit is a plain field of the game's rules, not one of their network values: each player's game
// mode script sets it from its own settings when the map loads (Rules.bricktronCap = sv_Settings.bricktronCap). A
// client has the default settings, not the host's (a custom preset or changed settings stay on the host), so it
// showed "16/15" while the host allowed 30. The host's limit is appended to the rules' network data, in the
// initial state and in every update, and a client keeps it: its own game mode script setting the limit later
// gets the host's instead. Both ends must run the mod (modded and unmodded peers cannot play together).
[Feature]
[HarmonyPatch(typeof(GeneralRules), nameof(GeneralRules.OnSerialize))]
internal static class BricktronCapSync
{
	// A sync var bit the game does not use (GeneralRules uses 0x01-0x08); setting it makes UNET send an update.
	public const uint DirtyBit = 0x40000000u;

	// The host's limit as last received, for the rules it came with; 0 before any.
	private static int _hostCap;

	private static GeneralRules _hostCapFor;

	// The host's limit on a client, or 0 (the host itself, single player, or nothing received yet).
	internal static int HostCap(GeneralRules rules)
	{
		if (NetworkServer.active || rules == null || _hostCapFor != rules)
		{
			return 0;
		}
		return _hostCap;
	}

	private static void Postfix(GeneralRules __instance, NetworkWriter writer, ref bool __result)
	{
		writer.WritePackedUInt32((uint)Mathf.Max(0, __instance._bricktronCap));
		__result = true;
	}

	internal static void Received(GeneralRules rules, int cap)
	{
		_hostCap = cap;
		_hostCapFor = rules;
		if (!NetworkServer.active && cap > 0)
		{
			rules._bricktronCap = cap;
		}
	}
}

[Feature]
[HarmonyPatch(typeof(GeneralRules), nameof(GeneralRules.OnDeserialize))]
internal static class BricktronCapSyncRead
{
	private static void Postfix(GeneralRules __instance, NetworkReader reader)
	{
		BricktronCapSync.Received(__instance, (int)reader.ReadPackedUInt32());
	}
}

// The host: a new limit is sent to the clients. A client: its own game mode script's limit gives way to the host's.
[Feature]
[HarmonyPatch(typeof(GeneralRules), nameof(GeneralRules.bricktronCap), MethodType.Setter)]
internal static class BricktronCapSyncSet
{
	private static void Prefix(GeneralRules __instance, ref int value)
	{
		int hostCap = BricktronCapSync.HostCap(__instance);
		if (hostCap > 0)
		{
			value = hostCap;
		}
	}

	private static void Postfix(GeneralRules __instance)
	{
		if (NetworkServer.active)
		{
			__instance.SetDirtyBit(BricktronCapSync.DirtyBit);
		}
	}
}
