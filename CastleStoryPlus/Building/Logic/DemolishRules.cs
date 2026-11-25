using System;
using System.Collections.Generic;

namespace CastleStoryPlus.Building;

// The pure rules of the demolition task (Demolish), without the game.
internal static class DemolishRules
{
	// What the host does with a player's request to mark (or unmark) a building for demolition.
	internal enum Decision
	{
		// Put a demolition blueprint over it.
		Mark,

		// Take its demolition blueprint away again.
		Cancel,

		// Nothing to do: it is already as asked.
		Nothing,

		// Not the player's own placed building (or one that cannot be demolished so).
		Refuse,

		// It is being moved (MoveStructure): its demolition belongs to the move.
		Moving
	}

	internal static Decision Decide(bool mark, bool marked, bool own, bool demolishable, bool moving)
	{
		if (!own || !demolishable)
		{
			return Decision.Refuse;
		}
		if (moving)
		{
			return Decision.Moving;
		}
		if (mark)
		{
			return marked ? Decision.Nothing : Decision.Mark;
		}
		return marked ? Decision.Cancel : Decision.Nothing;
	}

	// Most items of one kind a demolition gives back: a guard against a broken cost, not a rule of the game (the
	// largest building, the warehouse, costs 80 of a kind).
	internal const int MaxPerKind = 200;

	// The items a demolished building gives back: its whole cost, as a move gives it to the new blueprint. Kinds with
	// no count are left out, and each count is capped.
	internal static List<KeyValuePair<string, int>> Refund(IEnumerable<KeyValuePair<string, int>> cost)
	{
		List<KeyValuePair<string, int>> refund = new List<KeyValuePair<string, int>>();
		if (cost == null)
		{
			return refund;
		}
		Dictionary<string, int> index = new Dictionary<string, int>();
		foreach (KeyValuePair<string, int> entry in cost)
		{
			if (string.IsNullOrEmpty(entry.Key) || entry.Value <= 0)
			{
				continue;
			}
			if (index.TryGetValue(entry.Key, out int at))
			{
				refund[at] = new KeyValuePair<string, int>(entry.Key, Math.Min(MaxPerKind, refund[at].Value + entry.Value));
				continue;
			}
			index[entry.Key] = refund.Count;
			refund.Add(new KeyValuePair<string, int>(entry.Key, Math.Min(MaxPerKind, entry.Value)));
		}
		return refund;
	}

	// The demolition blueprint of a building is the building's own blueprint at the building's own place (the remove
	// tool places it so): the same spot within a hundredth of a block.
	internal static bool SameSpot(float dx, float dy, float dz)
	{
		return dx * dx + dy * dy + dz * dz < 0.0001f;
	}

	// A haul claim (a worker going for a leftover item) lapses on its own, in case its task is dropped without its
	// Finally running.
	internal static bool ClaimLapsed(float now, float claimedAt, float seconds)
	{
		return now - claimedAt > seconds;
	}

	// How long a demolition blueprint may be gone while its building still stands before the mark counts as
	// cancelled: the building falls in the same frame as its blueprint goes, but its release can come a moment later.
	internal const float CancelGraceSeconds = 3f;

	internal static bool Cancelled(bool antiAlive, bool buildingAlive, float now, float antiGoneSince)
	{
		return !antiAlive && buildingAlive && antiGoneSince >= 0f && now - antiGoneSince >= CancelGraceSeconds;
	}

	// A hit on a marked building by a unit of another faction (an enemy, or another team) means it may be falling to
	// them, not to the demolition: from then on it gives nothing back, so marking a building under attack cannot turn
	// its loss into a refund. Hits with no known attacker (fire, falling, the demolition's own breaking) and hits by the
	// building's own faction do not count.
	internal static bool HitByOther(bool attackerKnown, bool sameFaction)
	{
		return attackerKnown && !sameFaction;
	}

	// Whether a demolished building gives its cost back.
	internal static bool GivesBack(bool hitByOther)
	{
		return !hitByOther;
	}
}
