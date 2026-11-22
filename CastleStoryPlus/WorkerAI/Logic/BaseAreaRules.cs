using System;
using System.Collections.Generic;

namespace CastleStoryPlus.WorkerAI;

// The base area's rules (BaseArea) as plain functions of numbers, with no call into the game or Unity, so they can be
// tested without the game (CastleStoryPlus.UnitTests). BaseArea holds the state and the game side.
internal static class BaseAreaRules
{
	// What a worker's own search may do with a thing it found.
	internal enum Verdict
	{
		// It may go for it.
		Allowed,

		// Something no player owns (a loose item, a neutral chest) outside the base area: no walk out for it.
		OutsideBase,

		// Enemies stand near it: no walk into them for it.
		NearEnemies
	}

	// How far above or below a place an enemy still counts as near it (a wall top or a cliff over it is not "near").
	internal const float EnemyHeight = 12f;

	// Whether a place (x, z) lies in the base area: within an anchor's radius of it (anchors: x, z, radius three by
	// three; the distance across, height left out). A team with no anchor at all (no crystal, nothing built) has no
	// base to keep to, so every place counts as in it.
	internal static bool Inside(float x, float z, List<float> anchors)
	{
		return OutsideBy(x, z, anchors) <= 0f;
	}

	// How many blocks a place lies outside the base area (0 or less: in it): the shortest way across to the edge of
	// the nearest anchor's circle. Without anchors 0.
	internal static float OutsideBy(float x, float z, List<float> anchors)
	{
		if (anchors == null || anchors.Count < 3)
		{
			return 0f;
		}
		float best = float.MaxValue;
		for (int i = 0; i + 2 < anchors.Count; i += 3)
		{
			float dx = x - anchors[i];
			float dz = z - anchors[i + 1];
			float edge = (float)Math.Sqrt(dx * dx + dz * dz) - anchors[i + 2];
			if (edge < best)
			{
				best = edge;
			}
		}
		return best;
	}

	// Whether a place (x, y, z) lies within radius (across) and EnemyHeight (up or down) of the point (ex, ey, ez).
	internal static bool Near(float x, float y, float z, float ex, float ey, float ez, float radius)
	{
		float dx = x - ex;
		float dz = z - ez;
		float dy = y - ey;
		return dx * dx + dz * dz <= radius * radius && dy <= EnemyHeight && dy >= -EnemyHeight;
	}

	// Whether any of the points (x, y, z three by three) is near the place.
	internal static bool NearAny(float x, float y, float z, List<float> points, float radius)
	{
		if (points == null)
		{
			return false;
		}
		for (int i = 0; i + 2 < points.Count; i += 3)
		{
			if (Near(x, y, z, points[i], points[i + 1], points[i + 2], radius))
			{
				return true;
			}
		}
		return false;
	}

	// The rule for a thing a worker's own search found (a loose item or a storage to fetch from or store in):
	// - the team's own things in the base area are always fine (a siege does not stop the builders' fetching);
	// - the team's own things outside it (logs at a far woodcutting task) are fine unless enemies stand near them;
	// - things no player owns (loot, a neutral chest, stones in a ruin) only in the base area and with no enemy near.
	internal static Verdict Judge(bool own, bool insideBase, bool nearEnemy)
	{
		if (own)
		{
			return (insideBase || !nearEnemy) ? Verdict.Allowed : Verdict.NearEnemies;
		}
		if (nearEnemy)
		{
			return Verdict.NearEnemies;
		}
		return insideBase ? Verdict.Allowed : Verdict.OutsideBase;
	}

	// Whether a worker out of the base area is worth a line in the log: no player's order sent it there (recently,
	// or the task it is on), and it is further out than the slack (a worker on the edge going in and out of it).
	internal static bool Report(float outsideBy, bool playerOrder, float slack)
	{
		return !playerOrder && outsideBy > slack;
	}
}
