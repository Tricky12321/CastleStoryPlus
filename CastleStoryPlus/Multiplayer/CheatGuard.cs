using System;
using System.Collections.Generic;
using Brix.Engine;
using Brix.Game.Network;
using Brix.Input;
using Brix.Network;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.Multiplayer;

// No cheats in a multiplayer game unless the host allows them (#88). A game counts as multiplayer while it is hosted
// for others and another player is in it (a connection or a second lobby player), and always on a client; single
// player stays as it was. The host picks "Cheats: allowed / off" in the lobby (CheatGuardLobby; off for every new
// lobby), and the choice is sent to the other players (message 206) when it changes and every few seconds.
// While cheats are off:
// - the host refuses the game's spawn commands (CmdSpawn and its kinds: units of any team out of nothing) from the
//   other players; the host's own game still uses them (a magitron raising the dead);
// - every blueprint and demolition placed "at once" (the game's instant build, its editor mode) is placed as a normal
//   one, whoever sent it, so the host also refuses a client that turned the editor mode on by other means;
// - the editor mode is switched off on every player, and the mod's debug menu refuses its cheats (DebugMenu: energy,
//   bricktrons, resources, enemies, waves, time of day, instant build, fog of war, x-ray, critical hits, spawn
//   points), on the host as on the clients;
// - the DevTools API refuses its cheats (spawns, instant build, test worlds, test runs).
// The commands are refused where they are carried out (on the host), not only hidden in the menus.
[Feature(Features.CheatGuard, Features.CheatGuardInfo)]
internal static class CheatGuard
{
	private const short SyncMessage = 206;

	private const float BroadcastSeconds = 3f;

	private const float WarnSeconds = 5f;

	// The host's choice; on a client, what the host sent last.
	private static bool _hostAllows;

	private static bool _changed;

	private static float _nextBroadcast;

	private static NetworkClient _client;

	private static float _nextWarning;

	private sealed class CheatsMessage : MessageBase
	{
		public bool Allowed;

		public override void Serialize(NetworkWriter writer)
		{
			writer.Write(Allowed);
		}

		public override void Deserialize(NetworkReader reader)
		{
			Allowed = reader.ReadBoolean();
		}
	}

	private static void Enable()
	{
		Plugin.Root.AddComponent<CheatGuardDriver>();
	}

	// Whether the host allows cheats in this multiplayer game.
	internal static bool HostAllows => _hostAllows;

	// Whether cheats may be used now: always in single player (and with this feature off), in a multiplayer game only
	// when the host allows them.
	internal static bool Allowed => !Plugin.IsEnabled(Features.CheatGuard) || !InMultiplayerGame || _hostAllows;

	internal static bool Restricted => !Allowed;

	// A client of a multiplayer game, or its host with another player in it.
	internal static bool InMultiplayerGame
	{
		get
		{
			if (!Neo.IsMultiplayer)
			{
				return false;
			}
			if (!NetworkServer.active)
			{
				return true;
			}
			foreach (NetworkConnection connection in NetworkServer.connections)
			{
				// The host's own connection to itself has no transport host (-1).
				if (connection != null && connection.hostId >= 0)
				{
					return true;
				}
			}
			List<LobbyPlayer> players = (UNetManager.instance != null) ? UNetManager.instance.LobbyPlayerList : null;
			return players != null && players.Count > 1;
		}
	}

	// Whether this player may change the choice: the host of a multiplayer game.
	internal static bool CanChoose => NetworkServer.active && Neo.IsMultiplayer;

	internal static string Explanation => "Cheats are off in this multiplayer game: the host did not allow them in the lobby.";

	// Host: allow cheats or not, for every player.
	internal static void SetAllowed(bool allowed)
	{
		if (!NetworkServer.active)
		{
			return;
		}
		if (_hostAllows != allowed)
		{
			Plugin.Log.LogInfo("CheatGuard: cheats " + (allowed ? "allowed" : "off") + " in this multiplayer game");
		}
		_hostAllows = allowed;
		_changed = true;
	}

	// A command refused while cheats are off: logged, at most every few seconds.
	internal static void Refused(string what)
	{
		if (Time.unscaledTime < _nextWarning)
		{
			return;
		}
		_nextWarning = Time.unscaledTime + WarnSeconds;
		Plugin.Log.LogWarning("CheatGuard: refused " + what + ": cheats are off in this multiplayer game");
	}

	// Every frame: the client's handler, the host's broadcast, and the editor mode switched off while cheats are off.
	internal static void Update()
	{
		NetworkClient client = (UNetManager.instance != null) ? UNetManager.instance.LocalNetworkClient : null;
		if (client == null && !NetworkServer.active)
		{
			// Not in a multiplayer game or lobby (any more): the next lobby starts with cheats off.
			_hostAllows = false;
			_client = null;
		}
		if (client != null && client != _client)
		{
			_client = client;
			if (client.handlers.ContainsKey(SyncMessage))
			{
				client.UnregisterHandler(SyncMessage);
			}
			client.RegisterHandler(SyncMessage, Received);
		}
		if (NetworkServer.active && Neo.IsMultiplayer && (_changed || Time.unscaledTime >= _nextBroadcast))
		{
			_changed = false;
			_nextBroadcast = Time.unscaledTime + BroadcastSeconds;
			NetworkServer.SendToAll(SyncMessage, new CheatsMessage { Allowed = _hostAllows });
		}
		if (Architecte.editorMode && InputModeController.DefaultMode != InputMode.worldEditor && Restricted)
		{
			Architecte.editorMode = false;
			GameSignals.Invoke(GameSignals.OnEditorModeChanged, false);
			Plugin.Log.LogInfo("CheatGuard: instant build switched off (cheats are off in this multiplayer game)");
		}
	}

	private static void Received(NetworkMessage netMsg)
	{
		CheatsMessage message = netMsg.ReadMessage<CheatsMessage>();
		if (NetworkServer.active)
		{
			return;
		}
		if (_hostAllows != message.Allowed)
		{
			Plugin.Log.LogInfo("CheatGuard: the host " + (message.Allowed ? "allows" : "does not allow") + " cheats");
		}
		_hostAllows = message.Allowed;
	}
}

internal class CheatGuardDriver : MonoBehaviour
{
	private void Update()
	{
		try
		{
			CheatGuard.Update();
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning("CheatGuard: " + ex.Message);
		}
	}
}

// The host carries out the game's spawn commands only for its own game (the creative sidebar, a magitron raising the
// dead) while cheats are off; another player's spawn is refused.
[Feature(Features.CheatGuard, Features.CheatGuardInfo)]
[HarmonyPatch]
internal static class CheatGuardSpawnPatch
{
	private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
	{
		foreach (string name in new[] { "CmdSpawn", "CmdSpawnWithLevel", "CmdSpawnCrossbowTron", "CmdSpawnAlchemist", "CmdSpawnGeomancer", "CmdSpawnHalbardier" })
		{
			System.Reflection.MethodInfo method = AccessTools.Method(typeof(UNetProjectCmd), name);
			if (method != null)
			{
				yield return method;
			}
		}
	}

	private static bool Prefix(UNetProjectCmd __instance, System.Reflection.MethodBase __originalMethod)
	{
		if (!NetworkServer.active || __instance == null || __instance.isLocalPlayer || CheatGuard.Allowed)
		{
			return true;
		}
		CheatGuard.Refused(__originalMethod.Name + " from " + __instance.name);
		return false;
	}
}

// A blueprint placed "at once" (instant build) is placed as a normal one while cheats are off, whoever sent it. Runs
// before the other patches of the method, which look at instant.
[Feature(Features.CheatGuard, Features.CheatGuardInfo)]
[HarmonyPatch(typeof(UNetBlueprint), "_FinishSpawnBlueprint")]
internal static class CheatGuardInstantBlueprintPatch
{
	[HarmonyPriority(Priority.First)]
	private static void Prefix(ref bool instant)
	{
		if (instant && CheatGuard.Restricted)
		{
			instant = false;
			CheatGuard.Refused("an instant blueprint");
		}
	}
}

// The same for a demolition placed at once; one without a build task to go to (only an instant one has none) is
// dropped.
[Feature(Features.CheatGuard, Features.CheatGuardInfo)]
[HarmonyPatch(typeof(UNetBlueprint), nameof(UNetBlueprint.CmdPlaceAnti))]
internal static class CheatGuardInstantDemolitionPatch
{
	[HarmonyPriority(Priority.First)]
	private static void Prefix(ref bool instant)
	{
		if (instant && CheatGuard.Restricted)
		{
			instant = false;
			CheatGuard.Refused("an instant demolition");
		}
	}
}

[Feature(Features.CheatGuard, Features.CheatGuardInfo)]
[HarmonyPatch(typeof(UNetBlueprint), nameof(UNetBlueprint.CmdPlaceAnti))]
internal static class CheatGuardDemolitionProjectPatch
{
	[HarmonyPriority(Priority.Last)]
	private static bool Prefix(bool instant, GameObject BuildProject)
	{
		return instant || BuildProject != null;
	}
}
