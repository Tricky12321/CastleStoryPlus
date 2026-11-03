#if UNITY6_PLAYER
using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Brix.Engine;
using Brix.Game.Network;
using Brix.Network;
using CastleStoryPlus.Core;
using HarmonyLib;
using Network;
using Steamworks;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.Multiplayer;

// Unity 6 port: multiplayer over the internet through Steam, without opening a port. The game hosted a Steam lobby
// but filled in its name, address and version only once Castle Story's NAT facilitator answered, and joined over
// the internet by NAT punch-through through that facilitator; the facilitator is gone, so the online list stayed
// empty and only LAN and typed-in addresses worked. Now the host's lobby gets its name, version and the host's Steam
// id as soon as Steam made it, the online list asks Steam for such lobbies (worldwide), and joining one (from the
// list or a friend's invitation) connects over Steam's peer-to-peer networking, which goes through Valve's relays
// when the two computers cannot reach each other. The transport's relay (Compat's LlapiRelay) carries UNet's
// connections over it; LAN games and typed-in addresses still connect directly over UDP.
[Feature(Features.SteamRelay, Features.SteamRelayInfo)]
internal static class SteamRelay
{
	// Our lobbies carry this key: games of the original game or of another transport version are not listed.
	private const string NetKey = "cspNet";

	private const string NetVersion = "1";

	// The Steam peer-to-peer channels the relay's packets use (the game uses none): to the hosting side and to the
	// connecting side, so two copies of the game on one Steam account each read only their own.
	private const int ServerChannel = 3;

	private const int ClientChannel = 4;

	internal static void Enable()
	{
		Plugin.Root.AddComponent<SteamRelayPump>();
	}

	// Waits for the game's SteamManager to have Steam, then lends Steam's peer-to-peer networking to the transport.
	internal sealed class SteamRelayPump : MonoBehaviour
	{
		private static Callback<P2PSessionRequest_t> _sessionRequest;

		private void Update()
		{
			if (!SteamManager.Initialized)
			{
				return;
			}
			_sessionRequest = Callback<P2PSessionRequest_t>.Create(request =>
			{
				// Anyone may ask; the transport only lets a peer in that connects to a game hosted here.
				SteamNetworking.AcceptP2PSessionWithUser(request.m_steamIDRemote);
			});
			SteamNetworking.AllowP2PPacketRelay(true);
			LlapiRelay.Send = Send;
			LlapiRelay.Receive = Receive;
			LlapiRelay.Release = peer => SteamNetworking.CloseP2PSessionWithUser(new CSteamID(peer));
			Plugin.Log.LogInfo("SteamRelay: multiplayer over Steam ready (" + SteamUser.GetSteamID().m_SteamID + ")");
			enabled = false;
		}

		private static bool Send(ulong peer, byte[] data, int size, bool reliable, bool toServer)
		{
			return SteamNetworking.SendP2PPacket(new CSteamID(peer), data, (uint)size, reliable ? EP2PSend.k_EP2PSendReliable : EP2PSend.k_EP2PSendUnreliable, toServer ? ServerChannel : ClientChannel);
		}

		private static bool Receive(byte[] buffer, out int size, out ulong peer, bool toServer)
		{
			size = 0;
			peer = 0;
			int channel = toServer ? ServerChannel : ClientChannel;
			if (!SteamNetworking.IsP2PPacketAvailable(out uint available, channel) || available > buffer.Length)
			{
				return false;
			}
			if (!SteamNetworking.ReadP2PPacket(buffer, (uint)buffer.Length, out uint read, out CSteamID remote, channel))
			{
				return false;
			}
			size = (int)read;
			peer = remote.m_SteamID;
			return true;
		}
	}

	// The host's lobby, as soon as Steam made it: what the online list shows and how to reach the host.
	[Feature(Features.SteamRelay, Features.SteamRelayInfo)]
	[HarmonyPatch(typeof(SteamManager), "LobbyCreated")]
	internal static class LobbyCreatedPatch
	{
		private static void Postfix(LobbyCreated_t param)
		{
			if (param.m_eResult != EResult.k_EResultOK || !NetworkServer.active)
			{
				return;
			}
			CSteamID lobby = new CSteamID(param.m_ulSteamIDLobby);
			SteamMatchmaking.SetLobbyData(lobby, "name", UNetManager.instance.LocalPlayerName ?? string.Empty);
			SteamMatchmaking.SetLobbyData(lobby, "version", Neo.SemanticVersion);
			SteamMatchmaking.SetLobbyData(lobby, "hostInternalIP", LocalAddress());
			SteamMatchmaking.SetLobbyData(lobby, "playercount", "1");
			SteamMatchmaking.SetLobbyData(lobby, NetKey, NetVersion);
			SteamMatchmaking.SetLobbyData(lobby, "cspRelay", SteamUser.GetSteamID().m_SteamID.ToString(CultureInfo.InvariantCulture));
			SteamMatchmaking.SetLobbyData(lobby, "cspPort", UNetManager.HostingPort.ToString(CultureInfo.InvariantCulture));
			Plugin.Log.LogInfo("SteamRelay: lobby " + lobby.m_SteamID + " is listed");
		}
	}

	// The online list: our lobbies only, from anywhere in the world (Steam's default is the player's region).
	[Feature(Features.SteamRelay, Features.SteamRelayInfo)]
	[HarmonyPatch(typeof(SteamMatchmaking), nameof(SteamMatchmaking.RequestLobbyList))]
	internal static class LobbyListPatch
	{
		private static void Prefix()
		{
			SteamMatchmaking.AddRequestLobbyListStringFilter(NetKey, NetVersion, ELobbyComparison.k_ELobbyComparisonEqual);
			SteamMatchmaking.AddRequestLobbyListDistanceFilter(ELobbyDistanceFilter.k_ELobbyDistanceFilterWorldwide);
		}
	}

	// Joining a Steam lobby's game (from the list or an invitation) goes over the relay to the host's Steam id.
	[Feature(Features.SteamRelay, Features.SteamRelayInfo)]
	[HarmonyPatch(typeof(UNetManager), nameof(UNetManager.ConnectToServerWithMatchData))]
	internal static class JoinPatch
	{
		private static bool Prefix(UNetManager __instance, MatchList.MatchData matchData)
		{
			if (matchData.IsLanGame || !LlapiRelay.Available || !SteamManager.Initialized || !matchData.LobbySteamID.IsValid())
			{
				return true;
			}
			string relay = SteamMatchmaking.GetLobbyData(matchData.LobbySteamID, "cspRelay");
			if (!ulong.TryParse(relay, NumberStyles.None, CultureInfo.InvariantCulture, out ulong host) || host == 0)
			{
				return true;
			}
			if (!int.TryParse(SteamMatchmaking.GetLobbyData(matchData.LobbySteamID, "cspPort"), NumberStyles.None, CultureInfo.InvariantCulture, out int port) || port <= 0)
			{
				port = 3000;
			}
			Plugin.Log.LogInfo("SteamRelay: joining " + SteamMatchmaking.GetLobbyData(matchData.LobbySteamID, "name") + " over Steam (" + host + ")");
			__instance.ConnectToServer(LlapiRelay.RelayAddress(host), port);
			return false;
		}
	}

	// This computer's address on its local network (shown in the list as the original game did).
	private static string LocalAddress()
	{
		try
		{
			foreach (IPAddress address in Dns.GetHostAddresses(Dns.GetHostName()))
			{
				if (address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
				{
					return address.ToString();
				}
			}
		}
		catch (SocketException)
		{
		}
		return "127.0.0.1";
	}
}
#endif
