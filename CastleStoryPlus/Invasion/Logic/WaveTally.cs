using System;
using System.Collections.Generic;

namespace CastleStoryPlus.Invasion;

// How many enemies of each kind a wave has had ("seen" in the scoreboard's wave rows), as pure logic (no Unity, no
// game): the host's scoreboard asks it with what is left of each kind in the wave's raid. A wave's own units only
// get fewer, so the most of them ever left at once is how many came; the units a warlock summoned into the wave's
// raid (#161) come on top of that. Unit tests: CastleStoryPlus.UnitTests/Invasion.
internal sealed class WaveTally
{
	// The scoreboard's kinds: minitrons, corruptrons, healtrons, biftrons, magitrons.
	internal const int KindCount = 5;

	private readonly Dictionary<int, int[]> _summoned = new Dictionary<int, int[]>();

	private readonly Dictionary<int, int[]> _ownMost = new Dictionary<int, int[]>();

	// A unit of the kind was summoned into the wave's raid.
	internal void Summoned(int wave, int kind)
	{
		if (wave <= 0 || kind < 0 || kind >= KindCount)
		{
			return;
		}
		Row(_summoned, wave)[kind]++;
	}

	internal int SummonedOf(int wave, int kind)
	{
		if (kind < 0 || kind >= KindCount || !_summoned.TryGetValue(wave, out int[] row))
		{
			return 0;
		}
		return row[kind];
	}

	// left: the units of the kind in the wave's raid now, its own and the summoned ones alive. Returns how many of the
	// kind the wave has had: the most of its own ever left at once and every unit summoned into it. The summoned ones
	// that died are among the summoned, so what is left of the wave's own is at least left minus all summoned.
	internal int Seen(int wave, int kind, int left)
	{
		if (kind < 0 || kind >= KindCount)
		{
			return Math.Max(0, left);
		}
		int summoned = SummonedOf(wave, kind);
		int own = Math.Max(0, left - summoned);
		int[] most = Row(_ownMost, wave);
		if (own > most[kind])
		{
			most[kind] = own;
		}
		return Math.Max(left, most[kind] + summoned);
	}

	// A new game: nothing seen.
	internal void Clear()
	{
		_summoned.Clear();
		_ownMost.Clear();
	}

	private static int[] Row(Dictionary<int, int[]> rows, int wave)
	{
		if (!rows.TryGetValue(wave, out int[] row))
		{
			row = new int[KindCount];
			rows[wave] = row;
		}
		return row;
	}
}
