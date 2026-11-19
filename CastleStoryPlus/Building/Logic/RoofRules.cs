using System;

namespace CastleStoryPlus.Building;

// The pure rules of loose items on a building's roof (RoofItems), without the game.
internal static class RoofRules
{
	// Whether a point lies on or over the roof of a building: inside its footprint (grown by margin on every side) and
	// from depth under its top up to above over it.
	internal static bool OnRoof(float minX, float maxX, float minZ, float maxZ, float top, float x, float y, float z, float margin, float depth, float above)
	{
		return x >= minX - margin && x <= maxX + margin && z >= minZ - margin && z <= maxZ + margin && y >= top - depth && y <= top + above;
	}

	// How many whole blocks the block column (x, z) lies outside the footprint's block range: 0 inside or on its
	// edge column, 1 for the ring of columns round it, and so on.
	internal static int Ring(int minX, int maxX, int minZ, int maxZ, int x, int z)
	{
		int dx = Math.Max(Math.Max(minX - x, x - maxX), 0);
		int dz = Math.Max(Math.Max(minZ - z, z - maxZ), 0);
		return Math.Max(dx, dz);
	}
}
