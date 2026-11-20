using System;
using System.Collections.Generic;

namespace CastleStoryPlus.Building;

// The pure support rules of the mod's bricks and slabs (CustomBlocks, SlabOnPlanks, WoodOverhang), without the game.
internal static class BlockSupportRules
{
	// How many of a block's support cells must hold it: half its footprint, rounded up, as the game's 1 x 2 brick needs
	// one of its two. A 1 x 4 brick needs two (both ends of a gap, or half its length on a wall), a 2 x 4 four.
	internal static int RequiredSupport(int width, int depth)
	{
		int footprint = Math.Max(1, width) * Math.Max(1, depth);
		return (footprint + 1) / 2;
	}

	// The game's MaxMissingSupport for a block with this many support cells: all of them but the required ones may be
	// missing. Never below 0 (a block with fewer support cells than required needs all of them).
	internal static int MaxMissingSupport(int supportCells, int width, int depth)
	{
		return Math.Max(0, supportCells - RequiredSupport(width, depth));
	}

	// The MaxMissingSupport a placed (built) block of the mod is held to: one supported cell is enough, the rule every
	// block of the mod was placed under before RequiredSupport. Only blueprints (placing and building) need
	// RequiredSupport; a built block keeps the old rule, so the castles of existing saves, which the game validates
	// again when a save is loaded, do not collapse. A built block with none of its support cells holding still falls.
	internal static int PlacedMaxMissingSupport(int supportCells)
	{
		return Math.Max(0, supportCells - 1);
	}

	// The game's own test: enough of the support cells hold the block.
	internal static bool EnoughSupport(int supported, int supportCells, int maxMissingSupport)
	{
		return supported >= supportCells - maxMissingSupport;
	}

	// Whether every cell of a hanging block (self) lies within max sideways steps of standing wood: a path of at most
	// max steps from the cell to a standing cell, through wood and the block's own cells. So a slab hangs at most max
	// blocks out from wood that stands on something, counted in blocks (a 1 x 5 slab in line with a walkway reaches
	// five blocks out by itself). neighbours gives a cell's sideways neighbours, isWood whether a cell (not one of
	// self) holds wood, isStanding whether that wood stands on its own (asked only of wood cells).
	internal static bool HangsWithin<T>(ICollection<T> self, Func<T, IEnumerable<T>> neighbours, Func<T, bool> isWood, Func<T, bool> isStanding, int max)
	{
		if (self == null || self.Count == 0 || max < 1)
		{
			return false;
		}
		HashSet<T> own = new HashSet<T>(self);
		// The wood within max steps of the block, and the standing wood among it. A standing cell that could reach a cell
		// of the block within max steps is within max steps of the block, and so is every cell on that path.
		HashSet<T> explored = new HashSet<T>(own);
		List<T> standing = new List<T>();
		List<T> frontier = new List<T>(own);
		for (int depth = 1; depth <= max && frontier.Count > 0; depth++)
		{
			List<T> next = new List<T>();
			foreach (T cell in frontier)
			{
				foreach (T side in neighbours(cell))
				{
					if (explored.Contains(side) || !isWood(side))
					{
						continue;
					}
					explored.Add(side);
					next.Add(side);
					if (isStanding(side))
					{
						standing.Add(side);
					}
				}
			}
			frontier = next;
		}
		if (standing.Count == 0)
		{
			return false;
		}
		// From the standing wood back through what was found: every cell of the block must be reached within max steps.
		HashSet<T> reached = new HashSet<T>(standing);
		int left = own.Count;
		frontier = standing;
		for (int depth = 1; depth <= max && frontier.Count > 0; depth++)
		{
			List<T> next = new List<T>();
			foreach (T cell in frontier)
			{
				foreach (T side in neighbours(cell))
				{
					if (!explored.Contains(side) || !reached.Add(side))
					{
						continue;
					}
					next.Add(side);
					if (own.Contains(side))
					{
						left--;
						if (left == 0)
						{
							return true;
						}
					}
				}
			}
			frontier = next;
		}
		return false;
	}
}
