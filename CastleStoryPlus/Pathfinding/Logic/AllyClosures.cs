namespace CastleStoryPlus.Pathfinding;

// One unit's ally voxels closed to its own route searches (CrowdPathing), as pure bookkeeping (no Unity, no game):
// voxels are packed into a key (Key), times are game seconds the caller gives. A voxel is closed for a few seconds
// after an ally in it blocked the unit's walk, so its next route goes round the ally. When a search with a closed
// voxel finds no route, the closed voxels were the only way (a stair or a passage one voxel wide): they are opened
// again and remembered as chokes for a while, and a choke is not closed again; the unit waits for the ally there
// instead of turning back. Unit tests: CastleStoryPlus.UnitTests/Pathfinding.
internal sealed class AllyClosures
{
	// The most voxels closed (and chokes remembered) at a time; the one that ends first gives way to a new one.
	internal const int MaxClosed = 4;

	internal const int MaxChokes = 4;

	private readonly long[] _closed = new long[MaxClosed];

	private readonly float[] _closedUntil = new float[MaxClosed];

	private readonly bool[] _closedUsed = new bool[MaxClosed];

	private readonly long[] _chokes = new long[MaxChokes];

	private readonly float[] _chokeUntil = new float[MaxChokes];

	private readonly bool[] _chokeUsed = new bool[MaxChokes];

	// Closed voxels held (some may have run out and are let go when next looked at): 0 lets a search skip the test.
	internal int Count { get; private set; }

	// Times a search was refused a closed voxel since the last ResetRefused (the search's start).
	internal int Refused { get; private set; }

	// A voxel as one key (21 bits per axis, enough for any map).
	internal static long Key(int x, int y, int z)
	{
		return ((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0x1FFFFF) << 21) | (long)(z & 0x1FFFFF);
	}

	// Closes a voxel until now + seconds; a choke stays open (false).
	internal bool Close(long key, float now, float seconds)
	{
		if (IsChoke(key, now))
		{
			return false;
		}
		int slot = Slot(_closed, _closedUntil, _closedUsed, key);
		if (!_closedUsed[slot])
		{
			_closedUsed[slot] = true;
			Count++;
		}
		_closed[slot] = key;
		_closedUntil[slot] = now + seconds;
		return true;
	}

	// Whether a search must not cross the voxel now; a refusal counts in Refused.
	internal bool Refuses(long key, float now)
	{
		for (int i = 0; i < MaxClosed; i++)
		{
			if (!_closedUsed[i] || _closed[i] != key)
			{
				continue;
			}
			if (now < _closedUntil[i])
			{
				Refused++;
				return true;
			}
			_closedUsed[i] = false;
			Count--;
		}
		return false;
	}

	internal void ResetRefused()
	{
		Refused = 0;
	}

	// The search with the closed voxels found no route: they are opened and remembered as chokes until now + seconds.
	// Gives how many were open again (0 when none was still closed).
	internal int ReopenAsChokes(float now, float seconds)
	{
		int reopened = 0;
		for (int i = 0; i < MaxClosed; i++)
		{
			if (!_closedUsed[i])
			{
				continue;
			}
			if (now < _closedUntil[i])
			{
				int slot = Slot(_chokes, _chokeUntil, _chokeUsed, _closed[i]);
				_chokes[slot] = _closed[i];
				_chokeUntil[slot] = now + seconds;
				_chokeUsed[slot] = true;
				reopened++;
			}
			_closedUsed[i] = false;
		}
		Count = 0;
		Refused = 0;
		return reopened;
	}

	// Whether the voxel was found to be the only way a moment ago.
	internal bool IsChoke(long key, float now)
	{
		for (int i = 0; i < MaxChokes; i++)
		{
			if (!_chokeUsed[i] || _chokes[i] != key)
			{
				continue;
			}
			if (now < _chokeUntil[i])
			{
				return true;
			}
			_chokeUsed[i] = false;
		}
		return false;
	}

	// The slot for a key: its own, else a free one, else the one that ends first.
	private static int Slot(long[] keys, float[] until, bool[] used, long key)
	{
		int free = -1;
		int first = 0;
		for (int i = 0; i < keys.Length; i++)
		{
			if (used[i] && keys[i] == key)
			{
				return i;
			}
			if (!used[i])
			{
				if (free < 0)
				{
					free = i;
				}
			}
			else if (until[i] < until[first])
			{
				first = i;
			}
		}
		return (free >= 0) ? free : first;
	}
}
