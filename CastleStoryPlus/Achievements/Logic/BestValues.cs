using System;
using System.Collections.Generic;

namespace CastleStoryPlus.Achievements;

// What a best value the game reports (waves survived, research done) counts for, as pure logic (no Unity, no game):
// AchievementTracker.LocalMax calls this. Unit tests: CastleStoryPlus.UnitTests/Achievements.
//
// The player's own game (a new one, or a save loaded on this computer as host) is one game across its saves: a save
// loaded at wave 12 goes on at wave 12, so "survive 10 waves in one game" is reached there and wave 13 counts as 13.
// A joined game (a client in someone else's game) started without the player: what it reports while it is set up is
// its baseline, and only what goes beyond it counts.
internal sealed class BestValues
{
	private readonly Dictionary<string, int> _baseline = new Dictionary<string, int>();

	private bool _joined;

	internal bool Joined => _joined;

	// A game became ready: joined (a client) or the player's own (host, new game or loaded save).
	internal void Start(bool joined)
	{
		_joined = joined;
		_baseline.Clear();
	}

	// The game was left.
	internal void Clear()
	{
		_joined = false;
		_baseline.Clear();
	}

	// The value the counter may be raised to, or 0 for nothing. settingUp: the game is still being set up (its first
	// seconds), when a joined game's values are its baseline.
	internal int Report(string counter, int value, bool settingUp)
	{
		if (value <= 0)
		{
			return 0;
		}
		if (!_joined)
		{
			return value;
		}
		_baseline.TryGetValue(counter, out int baseline);
		if (settingUp)
		{
			_baseline[counter] = Math.Max(baseline, value);
			return 0;
		}
		return (value > baseline) ? value : 0;
	}
}
