using System.Collections.Generic;

namespace CastleStoryPlus.Treasure;

// The treasure's keep-out rule as plain functions of numbers, with no call into the game or Unity, so it can be tested
// without the game (CastleStoryPlus.UnitTests). TreasureChests holds the state and the game side.
internal static class TreasureRules
{
	// How far round a guarded chest (across, in blocks) the players' workers take no work of their own: the ruin, its
	// rubble and the ground its guardians roam (up to 9 from the chest), up to the edge of its clearing.
	public const float KeepOutRadius = 12f;

	// How far above or below the chest the keep-out reaches (a ruin is low; a cliff's foot or top nearby is not in it).
	public const float KeepOutHeight = 8f;

	// Whether a place (x, y, z) lies in the keep-out of a chest at (cx, cy, cz).
	public static bool InKeepOut(float x, float y, float z, float cx, float cy, float cz)
	{
		float dx = x - cx;
		float dz = z - cz;
		float dy = y - cy;
		return dx * dx + dz * dz <= KeepOutRadius * KeepOutRadius && dy <= KeepOutHeight && dy >= -KeepOutHeight;
	}

	// Whether a place lies in the keep-out of any of the guarded chests (x, y, z three by three).
	public static bool InAnyKeepOut(float x, float y, float z, List<float> guarded)
	{
		for (int i = 0; i + 2 < guarded.Count; i += 3)
		{
			if (InKeepOut(x, y, z, guarded[i], guarded[i + 1], guarded[i + 2]))
			{
				return true;
			}
		}
		return false;
	}

	// Whether a chest keeps the players' workers out: one not opened yet while a living guardian is near it.
	public static bool KeepsOut(bool opened, bool guarded)
	{
		return !opened && guarded;
	}

	// Whether a walk of a player's bricktron is turned away (#197): one into the keep-out of a guarded chest, from
	// outside it, of the bricktron's own accord. A player's order still sends it there, a bricktron already inside
	// (sent there by the player) may move about, and fighting is no errand: a soldier, a member of the player's raid
	// (sent to kill the guardians) and a walk after a unit are never held up.
	public static bool TurnsWalkAway(bool destinationKeptOut, bool walkerKeptOut, bool playerOrder, bool fighting)
	{
		return destinationKeptOut && !walkerKeptOut && !playerOrder && !fighting;
	}
}
