using System.Collections.Generic;
using Brix.Assets;
using Brix.Engine;
using Brix.Game;
using Brix.Game.Rules;
using Brix.Game.Network;
using Brix.Network;
using Brix.UI.Builder.Menu;
using Brix.UI.Scripts;
using CastleStoryPlus.DevTools.Api;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
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
		routes.Add(new Route("POST", "/api/net/start", "The host starts the game from the lobby (every player must be ready)", Start)
		{
			Parameters = "button (true: as the start button does, the loading screen first on every player and then the host's ready; default: the host's ready at once)"
		});
		routes.Add(new Route("POST", "/api/net/name", "Renames this player in the lobby, as Enter in the lobby's name box does", Name)
		{
			Parameters = "value (required)"
		});
		routes.Add(new Route("POST", "/api/net/mode", "Host: picks the lobby's game mode and preset (and a map that has the mode), as the lobby's map and mode lists do", Mode)
		{
			Parameters = "gamemode (codename, required unless save is given, e.g. invasion), preset (codename or name; default the mode's default), map (codename or name; default the lobby's map if it has the mode, else the first official one that has it), save (instead of the others: a save's exact name or path, as POST /api/net/save answers it; the save is the lobby's map with the mode and preset it was last played in)"
		});
		routes.Add(new Route("POST", "/api/net/team", "Host, in the lobby: puts a player in a team, as the lobby's team choice does (GET /api/net shows each player's team)", Team)
		{
			Parameters = "value (the team, counting from 0; required), name (the player's lobby name; default the local player)"
		});
		routes.Add(new Route("POST", "/api/net/save", "Host, in a game: saves it as the pause menu's save does (a loaded manual save is overwritten, else a new save is made) and stays in the game; answers the save's name", SaveGame));
		routes.Add(new Route("GET", "/api/net/alliances", "The game's factions that have a player: their owners and how each stands to the others (allied, enemy), to see what a loaded save made of the teams", Alliances));
		routes.Add(new Route("GET", "/api/net/rules", "The game's rules as this player has them: the bricktron limit (with the research's Housing; baseCap without it)", Rules));
		routes.Add(new Route("POST", "/api/net/rules", "Sets the game's bricktron limit as a game mode's settings do when the map loads (the host: for every player; a client: as its own game mode script does, which the host's limit overrides)", SetRules)
		{
			Parameters = "bricktronCap (required)"
		});
		routes.Add(new Route("GET", "/api/net/nameedit", "The lobby's name box: shown, focused, its text", NameEditState));
		routes.Add(new Route("POST", "/api/net/nameedit", "Opens the lobby's name box, focuses it and puts text in it as typing does (nothing is sent until Enter)", NameEdit)
		{
			Parameters = "text (required)"
		});
		routes.Add(new Route("GET", "/api/net/chat", "The chat box of a game: its text, whether it is open for typing and whether it shows", ChatState));
		routes.Add(new Route("POST", "/api/net/chat", "Sends a chat message in a multiplayer game, as typing it in the chat box and Enter do", Chat)
		{
			Parameters = "text (required)"
		});
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
		routes.Add(new Route("POST", "/api/net/cheats", "Host: allows cheats for every player of this multiplayer game or not, as the lobby's \"Cheats\" does (off for every new lobby). While they are off, the API's cheats answer 403", Cheats)
		{
			Parameters = "value (true or false; required)"
		});
		routes.Add(new Route("GET", "/api/net/taskcrew", "Bricktrons per task (TaskCrewSize) as this player has it: the value, whether this player chooses it, and the tasks' crew sizes in the world", TaskCrew));
		routes.Add(new Route("POST", "/api/net/taskcrew", "Single player or host: sets the bricktrons per task, as the lobby's \"Bricktrons per task\" and the mod's settings do (in the lobby or a game; the tasks in the world take it at once)", SetTaskCrew)
		{
			Parameters = "value (1 to 31; required)"
		});
		routes.Add(new Route("POST", "/api/net/cheatprobe", "Test of the cheat guard: sends the game's spawn command (a builder by the home crystal) or a blueprint placed at once to the host, as a changed game could; the host refuses them while cheats are off", CheatProbe)
		{
			Parameters = "kind (spawn or instant; required), key (instant: Factory/Name of the blueprint), at (instant: x,y,z)"
		});
	}

	// Sends a cheat to the host as a player with a changed game could: the game's spawn command, or a blueprint placed
	// at once. For testing that the host refuses them (CheatGuard); the host's answer shows in its world, not here.
	private static object CheatProbe(ApiRequest request)
	{
		if (User.LocalUser == null || User.LocalUser.faction == null)
		{
			throw new ApiException(409, "No game in progress");
		}
		string kind = request.Required("kind");
		if (kind == "spawn")
		{
			Vector3 at = WorldEndpoints.HomeVoxel().ToVector3() + new Vector3(3f, 1f, 3f);
			User.LocalUser.GetComponent<UNetProjectCmd>().CallCmdSpawn(Brix.External.Factories.Test.Bricktron.Factory, Brix.External.Factories.Test.Bricktron.Name, at, Quaternion.identity, User.LocalUser.faction.gameObject, Brix.Game.AI.Occupation.Type.Builder, true);
			return Obj("sent", "CmdSpawn", "at", at.ToString());
		}
		if (kind == "instant")
		{
			string[] key = request.Required("key").Split('/');
			if (key.Length != 2)
			{
				throw new ApiException(400, "'key' takes Factory/Name");
			}
			Vector3 at = request.Vector("at");
			User.LocalUser.GetComponent<UNetBlueprint>().CallCmdSpawnPlacedBlueprint(key[0], key[1], at, Quaternion.identity, true, null, false);
			return Obj("sent", "CmdSpawnPlacedBlueprint instant", "at", at.ToString());
		}
		throw new ApiException(400, "'kind' takes spawn or instant");
	}

	private static object TaskCrew(ApiRequest request)
	{
		return TaskCrewState();
	}

	private static object SetTaskCrew(ApiRequest request)
	{
		if (!CastleStoryPlus.WorkerAI.TaskCrewSize.CanChoose)
		{
			throw new ApiException(409, "Only single player or the host of a multiplayer game (with TaskCrewSize on)");
		}
		int value = request.Int("value", 0);
		if (value < 1 || value > CastleStoryPlus.WorkerAI.TaskCrewSize.Most)
		{
			throw new ApiException(400, "'value' must be from 1 to " + CastleStoryPlus.WorkerAI.TaskCrewSize.Most);
		}
		CastleStoryPlus.WorkerAI.TaskCrewSize.Set(value);
		return TaskCrewState();
	}

	private static object TaskCrewState()
	{
		Dictionary<string, int> sizes = new Dictionary<string, int>();
		foreach (Brix.Game.AI.Project project in Object.FindObjectsOfType<Brix.Game.AI.Project>())
		{
			if (CastleStoryPlus.WorkerAI.TaskCrewSize.Applies(project))
			{
				string key = project.GetType().Name + " " + project.MaximumCrewSize;
				sizes.TryGetValue(key, out int n);
				sizes[key] = n + 1;
			}
		}
		List<object> tasks = new List<object>();
		foreach (KeyValuePair<string, int> pair in sizes)
		{
			tasks.Add(pair.Key + " x" + pair.Value);
		}
		return Obj("value", CastleStoryPlus.WorkerAI.TaskCrewSize.Value, "canChoose", CastleStoryPlus.WorkerAI.TaskCrewSize.CanChoose, "tasks", tasks);
	}

	private static object Cheats(ApiRequest request)
	{
		if (!CastleStoryPlus.Multiplayer.CheatGuard.CanChoose)
		{
			throw new ApiException(409, "Only the host of a multiplayer game or lobby");
		}
		CastleStoryPlus.Multiplayer.CheatGuard.SetAllowed(request.Bool("value", false));
		return CheatsState();
	}

	private static object CheatsState()
	{
		return Obj("hostAllows", CastleStoryPlus.Multiplayer.CheatGuard.HostAllows, "multiplayerGame", CastleStoryPlus.Multiplayer.CheatGuard.InMultiplayerGame, "allowed", CastleStoryPlus.Multiplayer.CheatGuard.Allowed);
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
			"maxFactions", GameParam.MaxFactionCount,
			"multiplayer", Neo.multiplayer,
			"hostingPort", UNetManager.HostingPort,
			"map", (GameParam.Map != null) ? GameParam.Map.displayName : null,
			"players", players,
			"steam", Steam(),
			"lanSearch", manager != null && manager.networkDiscovery != null && manager.networkDiscovery.running && manager.networkDiscovery.isClient,
			"lanGames", LanGames,
			"onlineGames", OnlineGames(),
			"cheats", CheatsState());
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
		if (request.Bool("button", false))
		{
			GameParam.instance.BeginLoadingScreen();
			return Obj("starting", true, "button", true);
		}
		LobbyMenu.StartGame();
		return Obj("starting", true);
	}

	private static object Mode(ApiRequest request)
	{
		RequireLobby();
		if (!NetworkServer.active)
		{
			throw new ApiException(409, "Only the host picks the mode");
		}
		string saveName = request.String("save", null);
		if (saveName != null)
		{
			return PickSave(saveName);
		}
		string modeName = request.Required("gamemode");
		string mapName = request.String("map", null);
		Asset_Map map = null;
		List<Asset_Map> maps = new List<Asset_Map>(AssetDispensary.maps.SendQuery(AssetQueryParameters.AllSources | AssetQueryParameters.RefreshCache, AssetPlayableContext.Multiplayer, loadSubassets: true));
		if (mapName == null && GameParam.Map != null && ModeOf(GameParam.Map, modeName) != null)
		{
			map = GameParam.Map;
		}
		foreach (Asset_Map candidate in maps)
		{
			if (map != null)
			{
				break;
			}
			bool matches = (mapName == null)
				? candidate.authorType == AssetAuthorType.Official && ModeOf(candidate, modeName) != null
				: candidate.codename.Equals(mapName, System.StringComparison.OrdinalIgnoreCase) || candidate.displayName.Equals(mapName, System.StringComparison.OrdinalIgnoreCase);
			if (matches)
			{
				map = candidate;
			}
		}
		if (map == null)
		{
			throw new ApiException(404, (mapName == null) ? "No official multiplayer map has the mode '" + modeName + "'" : "No multiplayer map '" + mapName + "'");
		}
		Asset_Gamemode mode = ModeOf(map, modeName);
		if (mode == null)
		{
			throw new ApiException(404, "The map " + map.displayName + " has no multiplayer mode '" + modeName + "'");
		}
		string presetName = request.String("preset", null);
		Asset_GamemodePreset preset = null;
		foreach (Asset_GamemodePreset candidate in mode.GetPresets(AssetQueryParameters.AllSources, AssetPlayableContext.Multiplayer))
		{
			if (presetName == null ? candidate.isDefault : (candidate.codename.Equals(presetName, System.StringComparison.OrdinalIgnoreCase) || candidate.displayName.Equals(presetName, System.StringComparison.OrdinalIgnoreCase)))
			{
				preset = candidate;
				break;
			}
		}
		if (preset == null)
		{
			preset = (presetName == null) ? mode.GetDefaultPreset(AssetQueryParameters.AllSources, AssetPlayableContext.Multiplayer) : null;
			if (preset == null)
			{
				throw new ApiException(404, "The mode " + mode.codename + " has no multiplayer preset '" + presetName + "'");
			}
		}
		GameParam.Map = map;
		GameParam.Gamemode = mode;
		GameParam.Preset = preset;
		return Obj("map", map.displayName, "mapCodename", map.codename, "gamemode", mode.codename, "preset", preset.codename);
	}

	// The lobby's map list showing a save: the save as the map, with the mode and preset it was last played in.
	private static object PickSave(string saveName)
	{
		Asset_Map save = null;
		foreach (Asset_Map candidate in AssetDispensary.saves.SendQuery(AssetQueryParameters.AllSources | AssetQueryParameters.RefreshCache, AssetPlayableContext.Multiplayer))
		{
			if (candidate != null && (string.Equals(candidate.customName, saveName, System.StringComparison.OrdinalIgnoreCase) || string.Equals(candidate.path, saveName, System.StringComparison.Ordinal)))
			{
				save = candidate;
				break;
			}
		}
		if (save == null)
		{
			throw new ApiException(404, "No multiplayer save '" + saveName + "'");
		}
		Asset_Gamemode mode = save.GetLastPlayedGamemode();
		Asset_GamemodePreset preset = save.GetLastPlayedGamemodePreset();
		if (mode == null || preset == null)
		{
			throw new ApiException(409, "The save " + save.customName + " has no last played mode and preset");
		}
		GameParam.Map = save;
		GameParam.Gamemode = mode;
		GameParam.Preset = preset;
		return Obj("save", save.customName, "gamemode", mode.codename, "preset", preset.codename, "path", save.path);
	}

	private static object Team(ApiRequest request)
	{
		RequireLobby();
		if (!NetworkServer.active)
		{
			throw new ApiException(409, "Only the host puts players in teams");
		}
		int team = request.Int("value", -1);
		if (team < 0)
		{
			throw new ApiException(400, "Give the team: value=0 is the first");
		}
		string name = request.String("name", null);
		foreach (LobbyPlayer player in UNetManager.instance.LobbyPlayerList)
		{
			if (player != null && (name == null ? player.isLocalPlayer : player.NetworkplayerName == name))
			{
				player.NetworkTeamNumber = team;
				return Obj("name", player.NetworkplayerName, "team", player.NetworkTeamNumber);
			}
		}
		throw new ApiException(404, "No such player in the lobby");
	}

	private static object SaveGame(ApiRequest request)
	{
		if (!NetworkServer.active || Architecte.instance == null || GameParam.Map == null)
		{
			throw new ApiException(409, "Only the host saves, in a game");
		}
		if (!CastleStoryPlus.Menus.QuitDialogPatch.SaveBeforeLeaving(out Asset_Map written) || written == null)
		{
			throw new ApiException(500, "The game could not be saved (see /api/log?problems=true)");
		}
		return Obj("saved", true, "name", written.customName, "path", written.path);
	}

	private static object Alliances(ApiRequest request)
	{
		List<Faction> played = new List<Faction>();
		foreach (Faction faction in Faction.GetAllFactions())
		{
			if (faction != null && !faction.isAI && !faction.IsSpectator && faction != Faction.DefaultNeutral && faction.AllOwners.Count > 0)
			{
				played.Add(faction);
			}
		}
		List<object> factions = new List<object>();
		foreach (Faction faction in played)
		{
			List<string> owners = new List<string>();
			foreach (User owner in faction.AllOwners)
			{
				owners.Add(owner.ScreenName);
			}
			List<object> relations = new List<object>();
			foreach (Faction other in played)
			{
				if (other != faction)
				{
					relations.Add(Obj("faction", other.name, "allied", Affiliation.IsAllied(faction, other), "enemy", Affiliation.IsEnemy(faction, other)));
				}
			}
			factions.Add(Obj("faction", faction.name, "owners", owners, "relations", relations));
		}
		return Obj("factions", factions);
	}

	private static Asset_Gamemode ModeOf(Asset_Map map, string modeName)
	{
		foreach (Asset_Gamemode mode in map.GetGamemodes(AssetPlayableContext.Multiplayer))
		{
			if (mode != null && (mode.codename.Equals(modeName, System.StringComparison.OrdinalIgnoreCase) || mode.displayName.Equals(modeName, System.StringComparison.OrdinalIgnoreCase)))
			{
				return mode;
			}
		}
		return null;
	}

	private static object Rules(ApiRequest request)
	{
		GeneralRules rules = GeneralRules.Main;
		if (rules == null)
		{
			throw new ApiException(409, "No game rules (not in a game)");
		}
		return Obj("server", NetworkServer.active, "bricktronCap", rules.bricktronCap, "baseCap", rules._bricktronCap);
	}

	private static object SetRules(ApiRequest request)
	{
		if (GeneralRules.Main == null)
		{
			throw new ApiException(409, "No game rules (not in a game)");
		}
		// Through the property by reflection, as the game mode's Lua (Rules.bricktronCap = ...) sets it.
		typeof(GeneralRules).GetProperty(nameof(GeneralRules.bricktronCap)).SetValue(GeneralRules.Main, request.Int("bricktronCap", GeneralRules.Main._bricktronCap), null);
		return Rules(request);
	}

	private static object Name(ApiRequest request)
	{
		RequireLobby();
		string value = request.Required("value");
		LegacyLobbyLeftMenu.PlayerName = value;
		return Obj("name", LobbyMenu.Player.NetworkplayerName, "asked", value);
	}

	private static InputField NameField(bool open)
	{
		RequireLobby();
		foreach (LegacyLobbyLeftMenu menu in Object.FindObjectsOfType<LegacyLobbyLeftMenu>())
		{
			if (menu.TargetContainer == null)
			{
				continue;
			}
			if (open && !LegacyLobbyLeftMenu.IsExpansionState(LegacyLobbyLeftMenu.ExpansionState.PlayerName))
			{
				LegacyLobbyLeftMenu.ToggleExpansionState(LegacyLobbyLeftMenu.ExpansionState.PlayerName);
			}
			foreach (Container.Item item in menu.TargetContainer.items)
			{
				if (item.containerPopulatorOperation == LegacyBaseMenu.Operation.PlayerNameEdit && item.instance != null && item.instance.actualInputField != null)
				{
					return item.instance.actualInputField;
				}
			}
		}
		throw new ApiException(409, "No lobby name box");
	}

	private static object NameEditState(ApiRequest request)
	{
		InputField field = NameField(false);
		return Obj("shown", field.gameObject.activeInHierarchy, "focused", field.isFocused, "text", field.text, "name", LobbyMenu.Player.NetworkplayerName);
	}

	private static object NameEdit(ApiRequest request)
	{
		string text = request.Required("text");
		InputField field = NameField(true);
		field.Select();
		field.ActivateInputField();
		field.text = text;
		field.caretPosition = text.Length;
		return Obj("shown", field.gameObject.activeInHierarchy, "focused", field.isFocused, "text", field.text);
	}

	private static object ChatState(ApiRequest request)
	{
		ChatBoxDriver driver = ChatBoxDriver.Instance;
		if (driver == null || !driver.initialized)
		{
			throw new ApiException(409, "No chat box (not in a game)");
		}
		Text output = driver.chatPanel.GetPlugComponent<Text>("Output Text");
		CanvasGroup group = driver.chatPanel.GetComponent<CanvasGroup>();
		return Obj("text", (output != null) ? output.text : null, "open", driver._isActive, "alpha", (group != null) ? group.alpha : 1f, "panelActive", driver.chatPanel.gameObject.activeInHierarchy);
	}

	private static object Chat(ApiRequest request)
	{
		string text = request.Required("text");
		if (!Neo.multiplayer || User.LocalUser == null)
		{
			throw new ApiException(409, "Not in a multiplayer game");
		}
		User.LocalUser.CallCmdSendChatMessage(text);
		return Obj("sent", text);
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
