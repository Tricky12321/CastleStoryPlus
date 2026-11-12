#if UNITY6
using Brix.Network;
using Brix.UI.Builder.Menu;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Networking.NetworkSystem;

namespace CastleStoryPlus.Multiplayer;

// The host's start button shows the loading screen on every player (GameParam.BeginLoadingScreen); when the host's
// loading animation has played, the host says it is ready, and the lobby manager loads the map once every player is
// ready. A client that pressed "not ready" in that second (or a lobby event that sets everyone not ready, such as a
// client's name change, which the game counts as a player joining) left a player not ready: the map never loaded,
// and every player sat on the loading screen at 0 % for good. Once the host has pressed start:
// - a client's "not ready" is turned back into ready (and sent so to every player), and the map loads;
// - the lobby's "everyone not ready" is skipped, and a player connecting is turned away (it would count as not
//   ready);
// - a player leaving lets the others go on: the check whether all are ready runs again;
// - a client's own ready button no longer sends "not ready" after its loading screen began.
// Should the map still not load within StartTimeoutSeconds (nothing left to wait for), the lobby's normal rules
// apply again.
[Feature(Features.LobbyStart, Features.LobbyStartInfo)]
[HarmonyPatch(typeof(GameParam), nameof(GameParam.BeginLoadingScreen))]
internal static class LobbyStart
{
	private const float StartTimeoutSeconds = 60f;

	// Host: start was pressed and the map has not been loaded yet.
	internal static bool Starting;

	// Client: this player's loading screen began.
	internal static bool LoadingShown;

	private static float _startedAt;

	private static float _recheckAt = -1f;

	private static void Enable()
	{
		Plugin.Root.AddComponent<LobbyStartDriver>();
		GameSession.OnLeave(Reset);
	}

	internal static void Reset()
	{
		Starting = false;
		LoadingShown = false;
		_recheckAt = -1f;
	}

	private static void Prefix()
	{
		if (NetworkServer.active)
		{
			Starting = true;
			_startedAt = Time.unscaledTime;
			Plugin.Log.LogInfo("LobbyStart: the host started the game; ready changes are held until the map loads");
		}
	}

	internal static void RecheckSoon()
	{
		_recheckAt = Time.unscaledTime + 0.5f;
	}

	internal static void Tick()
	{
		if (!Starting)
		{
			return;
		}
		if (!NetworkServer.active)
		{
			Reset();
			return;
		}
		if (_recheckAt >= 0f && Time.unscaledTime >= _recheckAt)
		{
			_recheckAt = -1f;
			UNetManager manager = UNetManager.instance;
			if (manager != null)
			{
				Plugin.Log.LogInfo("LobbyStart: a player left while starting; checking whether the rest are ready");
				manager.CheckReadyToBegin();
			}
		}
		if (Starting && Time.unscaledTime - _startedAt > StartTimeoutSeconds)
		{
			Plugin.Log.LogWarning("LobbyStart: the map did not load within " + StartTimeoutSeconds + " s of the start; the lobby's ready rules apply again");
			Starting = false;
		}
	}

	// The lobby manager loaded the map (it returns early when someone is still not ready).
	[Feature(Features.LobbyStart, Features.LobbyStartInfo)]
	[HarmonyPatch(typeof(UNetManager), nameof(UNetManager.OnLobbyServerPlayersReady))]
	internal static class PlayersReadyPatch
	{
		private static void Postfix(UNetManager __instance)
		{
			if (Starting && __instance.LobbyPlayerList.TrueForAll((LobbyPlayer player) => player.readyToBegin))
			{
				Starting = false;
				_recheckAt = -1f;
			}
		}
	}

	// A client's "not ready" after the start is turned back into ready.
	[Feature(Features.LobbyStart, Features.LobbyStartInfo)]
	[HarmonyPatch(typeof(NetworkLobbyManager), "OnServerReadyToBeginMessage")]
	internal static class ReadyMessagePatch
	{
		private static void Postfix(NetworkLobbyManager __instance, NetworkMessage netMsg)
		{
			LobbyReadyToBeginMessage message = NetworkLobbyManager.s_ReadyToBeginMessage;
			if (!Starting || message == null || message.readyState || netMsg == null || netMsg.conn == null)
			{
				return;
			}
			if (!netMsg.conn.GetPlayerController(message.slotId, out PlayerController controller) || controller.gameObject == null)
			{
				return;
			}
			NetworkLobbyPlayer player = controller.gameObject.GetComponent<NetworkLobbyPlayer>();
			if (player == null)
			{
				return;
			}
			Plugin.Log.LogInfo("LobbyStart: a player pressed not ready after the start; kept ready");
			player.readyToBegin = true;
			LobbyReadyToBeginMessage ready = new LobbyReadyToBeginMessage();
			ready.slotId = player.slot;
			ready.readyState = true;
			NetworkServer.SendToReady(null, MsgType.LobbyReadyToBegin, ready);
			__instance.CheckReadyToBegin();
		}
	}

	// The lobby's "everyone not ready" (a player joined or renamed, the map, mode or preset changed) waits.
	[Feature(Features.LobbyStart, Features.LobbyStartInfo)]
	[HarmonyPatch(typeof(LobbyMenu), "SetEveryoneNotReady")]
	internal static class EveryoneNotReadyPatch
	{
		private static bool Prefix()
		{
			return !Starting;
		}
	}

	// A player connecting after the start would count as not ready: turned away.
	[Feature(Features.LobbyStart, Features.LobbyStartInfo)]
	[HarmonyPatch(typeof(UNetManager), nameof(UNetManager.OnLobbyServerConnect))]
	internal static class ConnectPatch
	{
		private static bool Prefix(UNetManager __instance, NetworkConnection conn)
		{
			if (!Starting)
			{
				return true;
			}
			Plugin.Log.LogInfo("LobbyStart: a player tried to join while the game was starting; turned away");
			__instance.SafeDisconnectConnection(conn);
			return false;
		}
	}

	[Feature(Features.LobbyStart, Features.LobbyStartInfo)]
	[HarmonyPatch(typeof(UNetManager), nameof(UNetManager.OnLobbyServerDisconnect))]
	internal static class DisconnectPatch
	{
		private static void Postfix()
		{
			if (Starting)
			{
				RecheckSoon();
			}
		}
	}

	// Client: the loading screen began (the host pressed start).
	[Feature(Features.LobbyStart, Features.LobbyStartInfo)]
	[HarmonyPatch(typeof(GameParam), "OnPlayLoadingAnimation")]
	internal static class LoadingShownPatch
	{
		private static void Postfix()
		{
			if (!NetworkServer.active)
			{
				LoadingShown = true;
			}
		}
	}

	// Client: "not ready" is not sent once the loading screen began.
	[Feature(Features.LobbyStart, Features.LobbyStartInfo)]
	[HarmonyPatch(typeof(LobbyMenu), nameof(LobbyMenu.SetIsReady))]
	internal static class SetIsReadyPatch
	{
		private static bool Prefix(bool value)
		{
			if (!value && LoadingShown && !NetworkServer.active)
			{
				Plugin.Log.LogInfo("LobbyStart: not ready ignored, the game is loading");
				return false;
			}
			return true;
		}
	}

	// Back in a lobby (a new one, or after leaving): the start of the last one is over.
	[Feature(Features.LobbyStart, Features.LobbyStartInfo)]
	[HarmonyPatch(typeof(LobbyMenu), "OpenSelf")]
	internal static class LobbyOpenPatch
	{
		private static void Postfix()
		{
			Reset();
		}
	}

	[Feature(Features.LobbyStart, Features.LobbyStartInfo)]
	[HarmonyPatch(typeof(UNetManager), nameof(UNetManager.OnStopClient))]
	internal static class StopClientPatch
	{
		private static void Postfix()
		{
			Reset();
		}
	}
}

internal class LobbyStartDriver : MonoBehaviour
{
	private void Update()
	{
		LobbyStart.Tick();
	}
}
#endif
