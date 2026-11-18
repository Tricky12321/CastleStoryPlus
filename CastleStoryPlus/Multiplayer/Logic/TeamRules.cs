using System.Collections.Generic;

namespace CastleStoryPlus.Multiplayer;

// Whether the lobby's teams are acceptable for the game mode, as a pure function (no Unity, no game): SavedTeams reads
// the mode's limits and the players' team numbers and calls this. Unit tests: CastleStoryPlus.UnitTests/Multiplayer.
// The game's own check (GameParam.AreTeamsValid) cannot be used for this: it also wants every one of the game's four team
// slots (the fixed GameParam.CurrentTeamInfos) to have a player (MP_MinPlayersPerTeam 1), so it calls any lobby with fewer players than
// slots invalid and its repair (FixTeams) shuffles the players again at every join.
internal static class TeamRules
{
	// teams: the team number of each player. A limit of -1 means no limit. Teams without a player do not count.
	internal static bool IsValid(IList<int> teams, int teamSlots, int minTeams, int maxTeams, int minPerTeam, int maxPerTeam)
	{
		Dictionary<int, int> sizes = new Dictionary<int, int>();
		foreach (int team in teams)
		{
			if (team < 0 || team >= teamSlots)
			{
				return false;
			}
			sizes[team] = sizes.ContainsKey(team) ? sizes[team] + 1 : 1;
		}
		if (minTeams != -1 && sizes.Count < minTeams)
		{
			return false;
		}
		if (maxTeams != -1 && sizes.Count > maxTeams)
		{
			return false;
		}
		foreach (int size in sizes.Values)
		{
			if (minPerTeam != -1 && size < minPerTeam)
			{
				return false;
			}
			if (maxPerTeam != -1 && size > maxPerTeam)
			{
				return false;
			}
		}
		return true;
	}
}
