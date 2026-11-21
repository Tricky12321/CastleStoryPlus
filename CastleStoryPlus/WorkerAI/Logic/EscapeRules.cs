using System;

namespace CastleStoryPlus.WorkerAI;

// The pure rules of a pit escape (StuckEscape) and of putting a load down (LoadRelief), without the game.
internal static class EscapeRules
{
	// An escape's dig sent this many times to its worker without the block going (each walk to it failed, or the task was
	// taken off the worker) counts as failed: the way is planned again from where the worker stands, round that block.
	internal const int MaxDigSends = 3;

	// How often one escape is planned again before it is given up (looked at again at the next check).
	internal const int MaxReplans = 3;

	// How far from the tunnel's mouth (blocks, along the ground) a worker that dug its way out walks before it stops.
	internal const int ExitDistance = 3;

	// Whether a worker standing with its feet at (fx, fy, fz) can dig the block at (bx, by, bz): in its own column the
	// ground under its feet or the block over its head, in a side column anything from one under its feet to its head
	// room (two over its feet). Further off it first has to walk there, and an escape's walk is only the pocket's.
	internal static bool CanDigFrom(int fx, int fy, int fz, int bx, int by, int bz)
	{
		int side = Math.Abs(bx - fx) + Math.Abs(bz - fz);
		int dy = by - fy;
		if (side == 0)
		{
			return dy == -1 || dy == 2;
		}
		return side == 1 && dy >= -1 && dy <= 2;
	}

	// The side a tunnel is made wider to for a step along (dx, dz): to the left of the way.
	internal static void Side(int dx, int dz, out int sx, out int sz)
	{
		sx = -dz;
		sz = dx;
	}

	// Whether two places are at least min apart on the ground (the larger of the x and z distances).
	internal static bool FarFrom(int ax, int az, int bx, int bz, int min)
	{
		return Math.Max(Math.Abs(ax - bx), Math.Abs(az - bz)) >= min;
	}

	// Whether a step that needs no digging may be walked in an escape's plan: within the pocket (the worker's own part
	// of the route graph), onto the way out itself, or on from a place outside the pocket (dug open by the escape, which
	// joins it to the graph). Open ground beside the pocket that the route graph does not join to it is no walk: a plan
	// that went that way sent the worker to dig a block it could never get to.
	internal static bool MayWalk(bool fromInPocket, bool toInPocket, bool toIsExit)
	{
		return toInPocket || toIsExit || !fromInPocket;
	}

	// LoadRelief: how long a storage a worker failed to get to is passed by from about the same place (seconds), and how
	// far (blocks, each way) the worker may have moved for that to hold.
	internal const float UnreachableStoreSeconds = 60f;

	internal const int UnreachableStoreNear = 4;

	// Whether a storage the worker failed to get to (until: when that is forgotten; dx, dy, dz: how far the worker is
	// now from where it failed) is still taken as out of its reach.
	internal static bool StoreStillUnreachable(float until, float now, int dx, int dy, int dz)
	{
		return now < until && Math.Abs(dx) <= UnreachableStoreNear && Math.Abs(dy) <= UnreachableStoreNear && Math.Abs(dz) <= UnreachableStoreNear;
	}

	// LoadRelief: whether a load may be put down where the worker stands: on solid ground, in open air, on the base's
	// routes (a cleanup can get there), not on a stair step, a climb or the floor of a quarry.
	internal static bool CanDropAt(bool groundSolid, bool feetFree, bool onStair, bool inQuarry, bool onRoutes)
	{
		return groundSolid && feetFree && !onStair && !inQuarry && onRoutes;
	}

	// LoadRelief: a "store or drop" in which the worker has not moved for this long (seconds) is broken off; the load is
	// kept (StuckReset leaves the store sequence alone, it would put the load down on the stair it stands on).
	internal const float StoreStillSeconds = 30f;
}
