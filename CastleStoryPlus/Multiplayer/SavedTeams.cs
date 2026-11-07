using System;
using System.Collections.Generic;
using System.IO;
using Brix.Assets;
using Brix.Game;
using Brix.Network;
using CastleStoryPlus.Core;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine.Networking;

namespace CastleStoryPlus.Multiplayer;

// A multiplayer save keeps its teams. The game does not save which factions are allied: the alliances are made at the
// start of every game from the lobby's team numbers (SimpleHostScript.Run), and a player joining the lobby of a loaded
// save is put in the first free team (LobbyPlayer.Start, in every preset where players are enemies). Two players who
// were one team before saving were two teams after loading, unless both picked the same team again, and their
// turrets and bricktrons fought each other; the other faction's bricktrons also lost their task icons, which the game
// only shows for allied or neutral units.
// The host now writes the teams of the save's factions beside the save, and in the lobby of a loaded save each player
// is put in the team his faction had, when he joins, when his faction changes and when the host picks another save.
// The team can still be changed in the lobby afterwards.
[Feature(Features.SavedTeams, Features.SavedTeamsInfo)]
internal static class SavedTeams
{
	private const string SaveFile = "castlestoryplus_teams.json";

	// Host: called when the game has written a save into its folder.
	internal static void Write(string folder)
	{
		List<Faction> factions = Faction.GetAllFactions();
		if (factions == null || factions.Count == 0)
		{
			return;
		}
		// The factions a player had (the same list the lobby of the loaded save offers).
		Faction.FactionMeta[] metas = Faction.GetAllFactionsMeta();
		List<Faction> players = new List<Faction>();
		for (int i = 0; i < factions.Count && i < metas.Length; i++)
		{
			Faction faction = factions[i];
			if (metas[i].assigned && faction != null && !faction.isAI && !faction.IsSpectator && faction != Faction.DefaultNeutral)
			{
				players.Add(faction);
			}
		}
		if (players.Count == 0)
		{
			return;
		}
		// Allied factions share a team number; the numbers themselves are only labels.
		int[] teams = new int[players.Count];
		for (int i = 0; i < teams.Length; i++)
		{
			teams[i] = -1;
		}
		int next = 0;
		for (int i = 0; i < players.Count; i++)
		{
			if (teams[i] >= 0)
			{
				continue;
			}
			teams[i] = next++;
			for (int j = i + 1; j < players.Count; j++)
			{
				if (teams[j] < 0 && Affiliation.IsAllied(players[i], players[j]))
				{
					teams[j] = teams[i];
				}
			}
		}
		JObject saved = new JObject();
		for (int i = 0; i < players.Count; i++)
		{
			saved[players[i].name] = teams[i];
		}
		try
		{
			File.WriteAllText(Path.Combine(folder, SaveFile), new JObject { ["teams"] = saved }.ToString(Newtonsoft.Json.Formatting.None));
		}
		catch (Exception ex)
		{
			Plugin.Log.LogError("SavedTeams: could not save the teams: " + ex.Message);
		}
	}

	// Host, in the lobby: puts the player in the team his faction had in the loaded save.
	internal static void Apply(LobbyPlayer player)
	{
		if (!NetworkServer.active || player == null)
		{
			return;
		}
		Asset_Map map = GameParam.Map;
		if (map == null || map.IsNewGame() || string.IsNullOrEmpty(map.path))
		{
			return;
		}
		try
		{
			int team = TeamOf(map, player.factionNumber);
			if (team < 0 || team >= GameParam.MaxTeamCount || team == player.TeamNumber)
			{
				return;
			}
			player.NetworkTeamNumber = team;
			Plugin.Log.LogInfo("SavedTeams: " + player.playerName + " is in team " + (team + 1) + " as in the save");
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning("SavedTeams: could not read the save's teams: " + ex.Message);
		}
	}

	internal static void ApplyAll()
	{
		if (!NetworkServer.active || UNetManager.instance == null || UNetManager.instance.LobbyPlayerList == null)
		{
			return;
		}
		foreach (LobbyPlayer player in UNetManager.instance.LobbyPlayerList)
		{
			Apply(player);
		}
	}

	// The saved team of the lobby's faction number, or -1 when the save has none (an older save, a new game).
	private static int TeamOf(Asset_Map map, int factionNumber)
	{
		string file = Path.Combine(map.path, SaveFile);
		if (factionNumber < 0 || !File.Exists(file))
		{
			return -1;
		}
		// The lobby numbers the save's factions that had a player, in the save's order (GameParam.RefreshFactions).
		Faction.FactionMeta[] metas = map.Disk_ReadFactionMeta();
		if (metas == null)
		{
			return -1;
		}
		string name = null;
		int index = 0;
		foreach (Faction.FactionMeta meta in metas)
		{
			if (!meta.assigned)
			{
				continue;
			}
			if (index == factionNumber)
			{
				name = meta.name;
				break;
			}
			index++;
		}
		if (name == null)
		{
			return -1;
		}
		JObject teams = JObject.Parse(File.ReadAllText(file))["teams"] as JObject;
		JToken team = teams?[name];
		if (team == null || team.Type != JTokenType.Integer)
		{
			return -1;
		}
		return (int)team;
	}
}

// Saves the teams with the game.
[Feature(Features.SavedTeams, Features.SavedTeamsInfo)]
[HarmonyPatch(typeof(Asset_Map), "Disk_Save")]
internal static class SavedTeamsSavePatch
{
	private static void Postfix(Asset_Map __instance)
	{
		if (NetworkServer.active && __instance.mapType == Asset_Map.MapType.Save && !string.IsNullOrEmpty(__instance.path))
		{
			SavedTeams.Write(__instance.path);
		}
	}
}

// A player joining the lobby: the game has just put him in the first free team.
[Feature(Features.SavedTeams, Features.SavedTeamsInfo)]
[HarmonyPatch(typeof(LobbyPlayer), nameof(LobbyPlayer.Start))]
internal static class SavedTeamsJoinPatch
{
	private static void Postfix(LobbyPlayer __instance)
	{
		SavedTeams.Apply(__instance);
	}
}

// A player taking another of the save's factions (picked, or moved by the host's checks) takes its team with it.
[Feature(Features.SavedTeams, Features.SavedTeamsInfo)]
[HarmonyPatch(typeof(LobbyPlayer), nameof(LobbyPlayer.NetworkfactionNumber), MethodType.Setter)]
internal static class SavedTeamsFactionPatch
{
	private static void Prefix(LobbyPlayer __instance, out int __state)
	{
		__state = __instance.factionNumber;
	}

	private static void Postfix(LobbyPlayer __instance, int __state)
	{
		if (__instance.factionNumber != __state)
		{
			SavedTeams.Apply(__instance);
		}
	}
}

// The host picking another save while players wait in the lobby.
[Feature(Features.SavedTeams, Features.SavedTeamsInfo)]
[HarmonyPatch(typeof(GameParam), nameof(GameParam.Map), MethodType.Setter)]
internal static class SavedTeamsMapPatch
{
	private static void Postfix()
	{
		SavedTeams.ApplyAll();
	}
}
