using System.Collections.Generic;
using Brix.Engine;
using Brix.Game;
using Brix.Game.Rules;
using Brix.Game.Network;
using Brix.Network;
using Brix.UI.Builder.Menu;
using CastleStoryPlus.DevTools.Api;
using UnityEngine.Networking;
using static CastleStoryPlus.DevTools.Endpoints.Json;

namespace CastleStoryPlus.DevTools.Endpoints;

// Multiplayer from the title screen, the way its menus do it, for testing the network transport with two players on
// one machine (CSP_API_PORT gives the second its own API port): host sends the signal the host button sends (the
// waiting screen starts the server on the hosting port, then the lobby opens and picks an official map), join fills
// in the custom server's address and port and sends the join button's signal, ready and start are the lobby's.
internal static class NetEndpoints
{
	internal static void Add(List<Route> routes)
	{
		routes.Add(new Route("GET", "/api/net", "Multiplayer state: server and client running, the transport's connections, the lobby's players (name, host, ready, faction, ping), the map", State));
		routes.Add(new Route("POST", "/api/net/host", "Hosts a multiplayer game from the title screen, as the host button does; GET /api/net shows when the lobby is up", Host)
		{
			Parameters = "port (default the game's hosting port), name (player name, default Host)"
		});
		routes.Add(new Route("POST", "/api/net/join", "Joins a multiplayer game by address, as the custom server's join button does", Join)
		{
			Parameters = "address (required), port (required), name (player name, default Client)"
		});
		routes.Add(new Route("POST", "/api/net/ready", "Sets this player ready (or not) in the lobby, as its ready button does; the host's ready also sends the map to clients that asked for it", Ready)
		{
			Parameters = "value (default true)"
		});
		routes.Add(new Route("POST", "/api/net/start", "The host starts the game from the lobby (every player must be ready)", Start));
		routes.Add(new Route("POST", "/api/net/lan", "Looks for games hosted on the LAN, as the multiplayer menu's LAN list does (a host on port 3000 broadcasts on port 3100); GET /api/net lists what was heard (lanGames)", Lan));
		routes.Add(new Route("POST", "/api/net/online", "Asks Steam for the online list's games (as the multiplayer menu does every second); GET /api/net lists them (onlineGames)", Online));
		routes.Add(new Route("POST", "/api/net/joinlobby", "Joins a game of the online list by its Steam lobby id, as a click on it and join do", JoinLobby)
		{
			Parameters = "lobby (Steam lobby id from onlineGames, required), name (player name, default Client)"
		});
		routes.Add(new Route("POST", "/api/net/leave", "Leaves the multiplayer game or lobby (as the lobby's leave does)", Leave));
		routes.Add(new Route("POST", "/api/net/relations", "Host: how the players' factions stand to each other (the game mode's rule: allied in co-op modes, enemies in versus modes such as Grab the Gems), for trying what allies see", Relations)
		{
			Parameters = "value (allied, neutral or enemy; required)"
		});
	}

	private static object Relations(ApiRequest request)
	{
		if (!NetworkServer.active || GeneralRules.Main == null)
		{
			throw new ApiException(409, "Only the host of a game in progress");
		}
		string value = request.Required("value");
		Affiliation.Relation relation;
		try
		{
			relation = (Affiliation.Relation)System.Enum.Parse(typeof(Affiliation.Relation), value, true);
		}
		catch (System.ArgumentException)
		{
			throw new ApiException(400, "'value' takes allied, neutral or enemy, was " + value);
		}
		GeneralRules.Main.PlayerRelations = relation;
		// Who is sent what (allies see each other's things) is only worked out again when an object's vision changes:
		// at once for everything.
		int rebuilt = 0;
		foreach (NetworkIdentity identity in new List<NetworkIdentity>(NetworkServer.objects.Values))
		{
			if (identity != null)
			{
				identity.RebuildObservers(false);
				rebuilt++;
			}
		}
		return Obj("playerRelations", GeneralRules.Main.PlayerRelations.ToString(), "observersRebuilt", rebuilt);
	}

	private static object State(ApiRequest request)
	{
		List<object> players = new List<object>();
		UNetManager manager = UNetManager.instance;
		if (manager != null && manager.LobbyPlayerList != null)
		{
			foreach (LobbyPlayer player in manager.LobbyPlayerList)
			{
				if (player == null)
				{
					continue;
				}
				players.Add(Obj("name", player.NetworkplayerName, "host", player.NetworkisHost, "local", player.isLocalPlayer, "ready", LobbyMenu.GetIsReady(player), "faction", player.NetworkfactionNumber, "team", player.NetworkTeamNumber, "ping", player.Networkping));
			}
		}
		int connections = 0;
		foreach (NetworkConnection connection in NetworkServer.connections)
		{
			if (connection != null)
			{
				connections++;
			}
		}
		NetworkClient client = (manager != null) ? manager.LocalNetworkClient : null;
		return Obj(
			"server", NetworkServer.active,
			"client", NetworkClient.active,
			"clientConnected", client != null && client.isConnected,
			"serverConnections", connections,
			"multiplayer", Neo.multiplayer,
			"hostingPort", UNetManager.HostingPort,
			"map", (GameParam.Map != null) ? GameParam.Map.displayName : null,
			"players", players,
			"steam", Steam(),
			"lanSearch", manager != null && manager.networkDiscovery != null && manager.networkDiscovery.running && manager.networkDiscovery.isClient,
			"lanGames", LanGames,
			"onlineGames", OnlineGames());
	}

	private static object Host(ApiRequest request)
	{
		RequireTitleScreen();
		int port = request.Int("port", UNetManager.HostingPort);
		UNetManager.SetHostingPort(port);
		Neo.multiplayerName = request.String("name", "Host");
		UNetManager.instance.LocalPlayerName = Neo.multiplayerName;
		NetworkWaitingModalMenu.OpenAsHostSignal.Invoke();
		return Obj("hosting", true, "port", port);
	}

	private static object Join(ApiRequest request)
	{
		RequireTitleScreen();
		string address = request.String("address", null);
		int port = request.Int("port", 0);
		if (string.IsNullOrEmpty(address) || port <= 0)
		{
			throw new ApiException(400, "address and port are required");
		}
		Neo.matchData = default;
		// As typed into the custom server window (what the join button checks), then kept.
		NetworkCustomServerModalMenu.TmpAddress = address;
		NetworkCustomServerModalMenu.TmpPort = port.ToString();
		NetworkCustomServerModalMenu.Address = address;
		NetworkCustomServerModalMenu.Port = port.ToString();
		if (!NetworkCustomServerActionsMenu.CanJoin)
		{
			throw new ApiException(400, "The game does not take " + address + ":" + port + " as a server address");
		}
		Neo.multiplayerName = request.String("name", "Client");
		UNetManager.instance.LocalPlayerName = Neo.multiplayerName;
		NetworkWaitingModalMenu.OpenAsClientSignal.Invoke();
		return Obj("joining", address + ":" + port);
	}

	private static object Ready(ApiRequest request)
	{
		RequireLobby();
		bool value = request.Bool("value", true);
		LobbyMenu.SetIsReady(value);
		return Obj("ready", value);
	}

	private static object Start(ApiRequest request)
	{
		RequireLobby();
		if (!LobbyMenu.Player.isHost)
		{
			throw new ApiException(409, "Only the host starts the game");
		}
		LobbyMenu.StartGame();
		return Obj("starting", true);
	}

	// Steam as the game sees it: whether its SteamManager got the Steam API, who is signed in, the lobby it is in.
	private static object Steam()
	{
		if (!SteamManager.Initialized)
		{
			return Obj("initialized", false);
		}
		return Obj("initialized", true, "user", Steamworks.SteamFriends.GetPersonaName(), "lobby", SteamManager.Instance.LobbyId.m_SteamID.ToString());
	}

	// Address and broadcast text of each game heard on the LAN since the search started.
	private static readonly Dictionary<string, string> LanGames = new Dictionary<string, string>();

	private static bool _listening;

	private static object Lan(ApiRequest request)
	{
		UNetNetworkDiscovery discovery = UNetManager.instance.networkDiscovery;
		if (!_listening)
		{
			discovery.ReceivedBroadcastSignal.Connect((address, data) =>
			{
				LanGames[address] = data;
			});
			_listening = true;
		}
		LanGames.Clear();
		discovery.FindLanGames();
		return Obj("searching", discovery.running);
	}

	private static readonly List<Network.MatchList.MatchData> OnlineList = new List<Network.MatchList.MatchData>();

	private static bool _onlineListening;

	private static object Online(ApiRequest request)
	{
		if (!SteamManager.Initialized)
		{
			throw new ApiException(409, "Steam is not running for the game");
		}
		if (!_onlineListening)
		{
			SteamManager.Instance.LobbyMatchListReceivedSignal.Connect(list =>
			{
				OnlineList.Clear();
				OnlineList.AddRange(list);
			});
			_onlineListening = true;
		}
		Steamworks.SteamMatchmaking.RequestLobbyList();
		return Obj("asked", true);
	}

	private static List<object> OnlineGames()
	{
		List<object> games = new List<object>();
		foreach (Network.MatchList.MatchData match in OnlineList)
		{
			games.Add(Obj("lobby", match.LobbySteamID.m_SteamID.ToString(), "name", match.MatchName, "version", match.Version, "players", match.PlayerCount, "relay", Steamworks.SteamMatchmaking.GetLobbyData(match.LobbySteamID, "cspRelay")));
		}
		return games;
	}

	private static object JoinLobby(ApiRequest request)
	{
		RequireTitleScreen();
		string lobby = request.String("lobby", null);
		int index = OnlineList.FindIndex(m => m.LobbySteamID.m_SteamID.ToString() == lobby);
		if (index < 0)
		{
			throw new ApiException(404, "No game with lobby " + lobby + " in the online list (POST /api/net/online first)");
		}
		Neo.multiplayerName = request.String("name", "Client");
		UNetManager.instance.LocalPlayerName = Neo.multiplayerName;
		Neo.matchData = OnlineList[index];
		NetworkWaitingModalMenu.OpenAsClientSignal.Invoke();
		return Obj("joining", lobby);
	}

	private static object Leave(ApiRequest request)
	{
		UNetManager.instance.Disconnect();
		return Obj("left", true);
	}

	private static void RequireTitleScreen()
	{
		if (UNetManager.instance == null || NetworkServer.active || NetworkClient.active)
		{
			throw new ApiException(409, "Only from the title screen, outside a game or lobby");
		}
	}

	private static void RequireLobby()
	{
		if (LobbyMenu.Player == null)
		{
			throw new ApiException(409, "Not in a multiplayer lobby");
		}
	}
}
