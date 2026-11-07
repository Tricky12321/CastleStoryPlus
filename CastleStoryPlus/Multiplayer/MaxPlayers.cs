#if UNITY6_PLAYER
using System;
using BepInEx.Configuration;
using Brix.Game.Network;
using Brix.Network;
using CastleStoryPlus.Core;
using HarmonyLib;
using Steamworks;
using UnityEngine.Networking;

namespace CastleStoryPlus.Multiplayer;

// Unity 6 port: more than 4 players in a multiplayer game. The game fixed 4 in three places: UNetManager.Initialize
// sets maxConnections to 4 (a fifth player is turned away, and at 4 the host's Steam lobby is made private, so the
// game drops out of the online list), StartHostMultiplayer makes the Steam lobby with room for 4, and the lobby
// manager's player slots (NetworkLobbyManager.maxPlayers, lobbySlots) are made for 4. [Multiplayer] MaxPlayers sets
// how many players the host lets in (up to 8: the network transport's default room). The lobby screen shows up to 4
// teams of 4, and how many factions a map has is still the map's own; more players share factions.
// Every player gets 8 lobby slots whatever the setting, because a client's slot list must hold every slot number the
// host hands out; only the host's setting limits who gets in.
[Feature(Features.MaxPlayers, Features.MaxPlayersInfo)]
[HarmonyPatch(typeof(UNetManager), nameof(UNetManager.Initialize))]
internal static class MaxPlayers
{
	// The room of the transport's default host topology (HLAPI NetworkServerSimple), and of the lobby slots.
	internal const int Limit = 8;

	private static ConfigEntry<int> _maxPlayers;

	internal static int Value => (_maxPlayers != null) ? Math.Max(2, Math.Min(Limit, _maxPlayers.Value)) : 4;

	private static void Enable()
	{
		_maxPlayers = Plugin.Cfg.Bind("Multiplayer", "MaxPlayers", Limit, new ConfigDescription("Players a hosted multiplayer game lets in, the host included (the game: 4). The host's setting applies.", new AcceptableValueRange<int>(2, Limit)));
	}

	// Initialize runs before hosting and before joining (it is where the game sets its 4).
	private static void Postfix(UNetManager __instance)
	{
		__instance.maxConnections = Value;
		if (__instance.maxPlayers < Limit)
		{
			__instance.maxPlayers = Limit;
		}
		// The lobby manager makes its slots once, when they are still empty; a list from an earlier game is kept.
		if (__instance.lobbySlots == null || __instance.lobbySlots.Length < __instance.maxPlayers)
		{
			NetworkLobbyPlayer[] slots = new NetworkLobbyPlayer[__instance.maxPlayers];
			if (__instance.lobbySlots != null)
			{
				Array.Copy(__instance.lobbySlots, slots, __instance.lobbySlots.Length);
			}
			__instance.lobbySlots = slots;
		}
		Plugin.Log.LogInfo("MaxPlayers: up to " + __instance.maxConnections + " players (" + __instance.lobbySlots.Length + " lobby slots)");
	}

	// The game makes the Steam lobby with room for 4: give it the same room as the game.
	[Feature(Features.MaxPlayers, Features.MaxPlayersInfo)]
	[HarmonyPatch(typeof(SteamManager), "LobbyCreated")]
	internal static class LobbyLimitPatch
	{
		private static void Postfix(LobbyCreated_t param)
		{
			if (param.m_eResult != EResult.k_EResultOK || !NetworkServer.active || UNetManager.instance == null)
			{
				return;
			}
			int limit = UNetManager.instance.maxConnections;
			if (!SteamMatchmaking.SetLobbyMemberLimit(new CSteamID(param.m_ulSteamIDLobby), limit))
			{
				Plugin.Log.LogWarning("MaxPlayers: Steam did not take the lobby's room of " + limit);
			}
		}
	}
}
#endif
