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
// While players join one by one the game's own lobby check (GameParam.FixTeams) may move players to make the teams
// valid for the mode (Conquest Versus needs two teams): when someone joins, every player who has not picked a team
// himself is put back, and after the check has moved players they are put back again, if the saved teams are valid.
// A player who picks a team in the lobby keeps it (until the host picks another save).
// A saved faction nobody takes over is given its saved relations too (SavedTeamsRelationsPatch).
[Feature(Features.SavedTeams, Features.SavedTeamsInfo)]
internal static class SavedTeams
{
	private const string SaveFile = "castlestoryplus_teams.json";

	// Teams are being put back (KeepSaved does not run again from the changes it makes).
	internal static bool Applying { get; private set; }

	// Players who picked their team in the lobby: they keep it.
	internal static readonly HashSet<LobbyPlayer> Chosen = new HashSet<LobbyPlayer>();

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
		bool outer = Applying;
		Applying = true;
		try
		{
			if (Chosen.Contains(player))
			{
				return;
			}
			int team = TeamOf(map, player.factionNumber);
			if (team < 0 || team >= GameParam.MaxTeamCount || team == player.TeamNumber)
			{
				return;
			}
			int was = player.TeamNumber;
			player.NetworkTeamNumber = team;
			Plugin.Log.LogInfo("SavedTeams: faction " + player.factionNumber + " is in team " + (team + 1) + " as in the save (was " + (was + 1) + ")");
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning("SavedTeams: could not read the save's teams: " + ex.Message);
		}
		finally
		{
			Applying = outer;
		}
	}

	internal static void ApplyAll()
	{
		if (!NetworkServer.active || UNetManager.instance == null || UNetManager.instance.LobbyPlayerList == null)
		{
			return;
		}
		bool outer = Applying;
		Applying = true;
		try
		{
			foreach (LobbyPlayer player in new List<LobbyPlayer>(UNetManager.instance.LobbyPlayerList))
			{
				Apply(player);
			}
		}
		finally
		{
			Applying = outer;
		}
	}

	// After the game's own lobby check (GameParam.FixTeams) has moved players to make the teams valid: the saved teams
	// are put back if they are valid too (all the players of the save are there), else the game's repair stays.
	internal static void KeepSaved()
	{
		if (!NetworkServer.active || Applying || UNetManager.instance == null || UNetManager.instance.LobbyPlayerList == null)
		{
			return;
		}
		List<LobbyPlayer> players = new List<LobbyPlayer>(UNetManager.instance.LobbyPlayerList);
		Chosen.RemoveWhere(p => p == null);
		int[] repaired = new int[players.Count];
		for (int i = 0; i < repaired.Length; i++)
		{
			repaired[i] = players[i].TeamNumber;
		}
		ApplyAll();
		if (GameParam.AreTeamsValid())
		{
			return;
		}
		Applying = true;
		try
		{
			for (int i = 0; i < repaired.Length; i++)
			{
				players[i].NetworkTeamNumber = repaired[i];
			}
		}
		finally
		{
			Applying = false;
		}
	}

	// The save's teams by faction name, or null (an older save, a new game).
	internal static Dictionary<string, int> TeamsByName(Asset_Map map)
	{
		if (map == null || map.IsNewGame() || string.IsNullOrEmpty(map.path))
		{
			return null;
		}
		string file = Path.Combine(map.path, SaveFile);
		if (!File.Exists(file))
		{
			return null;
		}
		JObject teams = JObject.Parse(File.ReadAllText(file))["teams"] as JObject;
		if (teams == null)
		{
			return null;
		}
		Dictionary<string, int> result = new Dictionary<string, int>();
		foreach (KeyValuePair<string, JToken> pair in teams)
		{
			if (pair.Value.Type == JTokenType.Integer)
			{
				result[pair.Key] = (int)pair.Value;
			}
		}
		return result;
	}

	// Host, when the game starts: a saved faction no player took over has no relations from the game (it only sets
	// them for factions with a player: enemies in Conquest), so its turrets and units would fight its former allies.
	// It gets the saved ones: allied to the factions of its team, enemy to the others.
	internal static void SetRelationsOfFreeFactions()
	{
		if (!NetworkServer.active)
		{
			return;
		}
		try
		{
			Dictionary<string, int> teams = TeamsByName(GameParam.Map);
			if (teams == null)
			{
				return;
			}
			List<Faction> saved = new List<Faction>();
			foreach (Faction faction in Faction.GetAllFactions())
			{
				if (faction != null && teams.ContainsKey(faction.name))
				{
					saved.Add(faction);
				}
			}
			foreach (Faction free in saved)
			{
				if (free.AllOwners.Count > 0)
				{
					continue;
				}
				foreach (Faction other in saved)
				{
					if (other == free)
					{
						continue;
					}
					Affiliation.Relation relation = teams[free.name] == teams[other.name] ? Affiliation.Relation.Allied : Affiliation.Relation.Enemy;
					free.SetFactionRelation(other, relation);
					other.SetFactionRelation(free, relation);
				}
				free.SetFactionRelation(Faction.DefaultEnemy, Affiliation.Relation.Enemy);
				free.SetFactionRelation(Faction.DefaultNeutral, Affiliation.Relation.Neutral);
				Faction.DefaultEnemy.SetFactionRelation(free, Affiliation.Relation.Enemy);
				Faction.DefaultNeutral.SetFactionRelation(free, Affiliation.Relation.Neutral);
				Plugin.Log.LogInfo("SavedTeams: " + free.name + " has no player; its relations are as in the save");
			}
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning("SavedTeams: could not set the relations of free factions: " + ex.Message);
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

// The game's lobby check has moved players: the saved teams come back if they are valid.
[Feature(Features.SavedTeams, Features.SavedTeamsInfo)]
[HarmonyPatch(typeof(GameParam), "FixTeams")]
internal static class SavedTeamsFixPatch
{
	private static void Postfix()
	{
		SavedTeams.KeepSaved();
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

// A player joining the lobby: the game has just put him in the first free team. Every player already there is put
// back in his saved team too: while the first players join, the lobby is not valid for the mode yet (Conquest
// Versus needs two teams, so two players who shared a team were split by the game's own lobby check, RefreshTeams),
// and the team that check moved would otherwise stay where it was moved to when the last player of the save joins.
[Feature(Features.SavedTeams, Features.SavedTeamsInfo)]
[HarmonyPatch(typeof(LobbyPlayer), nameof(LobbyPlayer.Start))]
internal static class SavedTeamsJoinPatch
{
	private static void Postfix(LobbyPlayer __instance)
	{
		SavedTeams.Apply(__instance);
		SavedTeams.ApplyAll();
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
		SavedTeams.Chosen.Clear();
		SavedTeams.ApplyAll();
	}
}

// A player picking his team in the lobby: it is his, the saved team no longer overrides it.
[Feature(Features.SavedTeams, Features.SavedTeamsInfo)]
[HarmonyPatch(typeof(LobbyPlayer), nameof(LobbyPlayer.CmdSetTeam))]
internal static class SavedTeamsChosenPatch
{
	private static void Prefix(LobbyPlayer __instance)
	{
		SavedTeams.Chosen.Add(__instance);
	}
}

// The game has made its factions' relations from the lobby's teams.
[Feature(Features.SavedTeams, Features.SavedTeamsInfo)]
[HarmonyPatch(typeof(SimpleHostScript), nameof(SimpleHostScript.Run))]
internal static class SavedTeamsRelationsPatch
{
	private static void Postfix()
	{
		SavedTeams.SetRelationsOfFreeFactions();
	}
}
