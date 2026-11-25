using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace CastleStoryPlus.Pathfinding;

// A no-go zone's rectangle of columns (NoGoZones): every voxel column from (MinX, MinZ) to (MaxX, MaxZ), both ends in.
internal struct NoGoRect : IEquatable<NoGoRect>
{
	public int MinX;

	public int MinZ;

	public int MaxX;

	public int MaxZ;

	// The rectangle between two corners, in any order.
	public NoGoRect(int x0, int z0, int x1, int z1)
	{
		MinX = Math.Min(x0, x1);
		MinZ = Math.Min(z0, z1);
		MaxX = Math.Max(x0, x1);
		MaxZ = Math.Max(z0, z1);
	}

	public int Width => MaxX - MinX + 1;

	public int Depth => MaxZ - MinZ + 1;

	public long Cells => (long)Width * Depth;

	public bool Contains(int x, int z)
	{
		return x >= MinX && x <= MaxX && z >= MinZ && z <= MaxZ;
	}

	public bool Intersects(NoGoRect other)
	{
		return other.MinX <= MaxX && other.MaxX >= MinX && other.MinZ <= MaxZ && other.MaxZ >= MinZ;
	}

	// How many columns of the square of size columns from (x, z) lie in the rectangle.
	public long Overlap(int x, int z, int size)
	{
		long w = Math.Min(MaxX, x + size - 1) - Math.Max(MinX, x) + 1;
		long d = Math.Min(MaxZ, z + size - 1) - Math.Max(MinZ, z) + 1;
		return (w > 0 && d > 0) ? w * d : 0;
	}

	public bool Equals(NoGoRect other)
	{
		return MinX == other.MinX && MinZ == other.MinZ && MaxX == other.MaxX && MaxZ == other.MaxZ;
	}

	public override bool Equals(object obj)
	{
		return obj is NoGoRect other && Equals(other);
	}

	public override int GetHashCode()
	{
		return ((MinX * 397 ^ MinZ) * 397 ^ MaxX) * 397 ^ MaxZ;
	}

	public override string ToString()
	{
		return MinX + "," + MinZ + " - " + MaxX + "," + MaxZ;
	}
}

// The rules of the no-go zones, as pure functions (no Unity, no game): NoGoZones and the route patches call these.
// Unit tests: CastleStoryPlus.UnitTests/Pathfinding/NoGoRulesTests.cs.
internal static class NoGoRules
{
	// The setting [NoGoZones] Strength: about how many blocks longer a route may get to keep out of a zone.
	internal const int DefaultStrength = 60;

	internal const int MinStrength = 5;

	internal const int MaxStrength = 500;

	// A step inside a zone costs this many plain steps (on top of the price of entering it).
	internal const int InsideFactor = 3;

	// The most rectangles one faction keeps, and the longest side of one.
	internal const int MaxRects = 64;

	internal const int MaxSide = 256;

	// The game's graph: the A* ranks a step by length * 10 + weight * 2 and a flat step weighs 10, so a flat step
	// ranks 30 and one block of route is 15 weight (RoadRules.FlatWeight).
	internal const int WeightPerBlock = 15;

	internal static int ClampStrength(int strength)
	{
		return Math.Min(MaxStrength, Math.Max(MinStrength, strength));
	}

	// The extra weight of the route graph's step from a place to the next, given how much of each place's columns lie
	// in a zone (0 to 1; a coarse place of the graph covers size x size columns, a voxel 1). Entering a zone costs
	// strength blocks of route, and every block walked inside it InsideFactor plain steps instead of 1; leaving it is
	// free. So a route goes round a zone when that is at most about strength blocks longer (plus twice the zone's width
	// it would cross), and one that has to go in (its goal is inside, or it has no other way) still finds its way: the
	// zone is dear, never closed.
	internal static int StepPenalty(float fromCoverage, float toCoverage, int size, int strength)
	{
		if (toCoverage <= 0f)
		{
			return 0;
		}
		float entering = Math.Max(0f, toCoverage - Math.Max(0f, fromCoverage));
		float inside = toCoverage * Math.Max(1, size);
		float blocks = entering * ClampStrength(strength) + inside * (InsideFactor - 1);
		return (int)Math.Round(blocks * WeightPerBlock, MidpointRounding.AwayFromZero);
	}

	// The navigation mesh's cost of a zone's area for a unit of the zone's team: Detour has no price for entering an area,
	// only one per metre, so the area costs as much more as crossing a zone of 5 blocks costs strength blocks more (times
	// the speed factor every walking area has, RoadRules.AreaCost). Never under the factor (Detour's heuristic).
	internal static float MeshCost(int strength, float factor)
	{
		return Math.Max(1f, factor) * (1f + ClampStrength(strength) / 5f);
	}

	// A rectangle a player drew, kept to MaxSide on each side (from its first corner).
	internal static NoGoRect Drawn(int x0, int z0, int x1, int z1)
	{
		int limit = MaxSide - 1;
		x1 = (x1 >= x0) ? Math.Min(x1, x0 + limit) : Math.Max(x1, x0 - limit);
		z1 = (z1 >= z0) ? Math.Min(z1, z0 + limit) : Math.Max(z1, z0 - limit);
		return new NoGoRect(x0, z0, x1, z1);
	}

	// The parts of a that cut leaves (none, a whole, or up to four strips).
	internal static void Subtract(NoGoRect a, NoGoRect cut, List<NoGoRect> into)
	{
		if (!a.Intersects(cut))
		{
			into.Add(a);
			return;
		}
		// Strips below and above the cut (whole width), then left and right of it (the cut's rows only).
		if (cut.MinZ > a.MinZ)
		{
			into.Add(new NoGoRect(a.MinX, a.MinZ, a.MaxX, cut.MinZ - 1));
		}
		if (cut.MaxZ < a.MaxZ)
		{
			into.Add(new NoGoRect(a.MinX, cut.MaxZ + 1, a.MaxX, a.MaxZ));
		}
		int minZ = Math.Max(a.MinZ, cut.MinZ);
		int maxZ = Math.Min(a.MaxZ, cut.MaxZ);
		if (cut.MinX > a.MinX)
		{
			into.Add(new NoGoRect(a.MinX, minZ, cut.MinX - 1, maxZ));
		}
		if (cut.MaxX < a.MaxX)
		{
			into.Add(new NoGoRect(cut.MaxX + 1, minZ, a.MaxX, maxZ));
		}
	}

	// Takes the rectangle out of the zones (they stay apart from each other). True when anything changed.
	internal static bool Erase(List<NoGoRect> zones, NoGoRect cut)
	{
		List<NoGoRect> result = new List<NoGoRect>(zones.Count + 4);
		bool changed = false;
		foreach (NoGoRect zone in zones)
		{
			if (zone.Intersects(cut))
			{
				changed = true;
			}
			Subtract(zone, cut, result);
		}
		if (changed)
		{
			zones.Clear();
			zones.AddRange(result);
		}
		return changed;
	}

	// Adds the rectangle to the zones: what it overlaps of them is taken out of them first, so no column is in two
	// rectangles. False (nothing changed) when that would make more than MaxRects rectangles.
	internal static bool Add(List<NoGoRect> zones, NoGoRect rect)
	{
		List<NoGoRect> result = new List<NoGoRect>(zones.Count + 4);
		foreach (NoGoRect zone in zones)
		{
			Subtract(zone, rect, result);
		}
		result.Add(rect);
		if (result.Count > MaxRects)
		{
			return false;
		}
		zones.Clear();
		zones.AddRange(result);
		return true;
	}

	// ---- saved and sent: {"faction name": [[minX, minZ, maxX, maxZ], ...], ...}

	internal static JObject ToJson(IEnumerable<KeyValuePair<string, List<NoGoRect>>> zones)
	{
		JObject json = new JObject();
		foreach (KeyValuePair<string, List<NoGoRect>> entry in zones)
		{
			if (string.IsNullOrEmpty(entry.Key) || entry.Value == null || entry.Value.Count == 0)
			{
				continue;
			}
			JArray rects = new JArray();
			foreach (NoGoRect rect in entry.Value)
			{
				rects.Add(new JArray(rect.MinX, rect.MinZ, rect.MaxX, rect.MaxZ));
			}
			json[entry.Key] = rects;
		}
		return json;
	}

	// The zones of each faction name; a malformed rectangle is skipped, and each faction keeps at most MaxRects.
	internal static Dictionary<string, List<NoGoRect>> FromJson(JObject json)
	{
		Dictionary<string, List<NoGoRect>> zones = new Dictionary<string, List<NoGoRect>>();
		if (json == null)
		{
			return zones;
		}
		foreach (KeyValuePair<string, JToken> entry in json)
		{
			if (!(entry.Value is JArray rects))
			{
				continue;
			}
			List<NoGoRect> list = new List<NoGoRect>();
			foreach (JToken token in rects)
			{
				if (!(token is JArray r) || r.Count != 4 || r[0].Type != JTokenType.Integer || r[1].Type != JTokenType.Integer || r[2].Type != JTokenType.Integer || r[3].Type != JTokenType.Integer)
				{
					continue;
				}
				NoGoRect rect = Drawn((int)r[0], (int)r[1], (int)r[2], (int)r[3]);
				if (list.Count < MaxRects)
				{
					Add(list, rect);
				}
			}
			if (list.Count > 0)
			{
				zones[entry.Key] = list;
			}
		}
		return zones;
	}
}

// The columns of a team's zones, for the route searches: a bitmap per 16 x 16 columns, so a voxel is looked up in O(1),
// and the rectangles, for the share of a coarse place of the graph (many columns) that lies in a zone.
internal sealed class NoGoMap
{
	private const int ChunkBits = 4;

	private const int ChunkSize = 1 << ChunkBits;

	private readonly Dictionary<long, ulong[]> _chunks = new Dictionary<long, ulong[]>();

	private readonly List<NoGoRect> _rects = new List<NoGoRect>();

	private int _minX = int.MaxValue;

	private int _minZ = int.MaxValue;

	private int _maxX = int.MinValue;

	private int _maxZ = int.MinValue;

	public bool IsEmpty => _rects.Count == 0;

	public int RectCount => _rects.Count;

	private static long Key(int cx, int cz)
	{
		return ((long)cx << 32) ^ (uint)cz;
	}

	public void Add(NoGoRect rect)
	{
		_rects.Add(rect);
		_minX = Math.Min(_minX, rect.MinX);
		_minZ = Math.Min(_minZ, rect.MinZ);
		_maxX = Math.Max(_maxX, rect.MaxX);
		_maxZ = Math.Max(_maxZ, rect.MaxZ);
		for (int x = rect.MinX; x <= rect.MaxX; x++)
		{
			for (int z = rect.MinZ; z <= rect.MaxZ; z++)
			{
				long key = Key(x >> ChunkBits, z >> ChunkBits);
				if (!_chunks.TryGetValue(key, out ulong[] bits))
				{
					bits = new ulong[ChunkSize * ChunkSize / 64];
					_chunks[key] = bits;
				}
				int index = ((z & (ChunkSize - 1)) << ChunkBits) | (x & (ChunkSize - 1));
				bits[index >> 6] |= 1UL << (index & 63);
			}
		}
	}

	public bool Contains(int x, int z)
	{
		if (x < _minX || x > _maxX || z < _minZ || z > _maxZ)
		{
			return false;
		}
		if (!_chunks.TryGetValue(Key(x >> ChunkBits, z >> ChunkBits), out ulong[] bits))
		{
			return false;
		}
		int index = ((z & (ChunkSize - 1)) << ChunkBits) | (x & (ChunkSize - 1));
		return (bits[index >> 6] & (1UL << (index & 63))) != 0;
	}

	// The share (0 to 1) of the square of size x size columns from (x, z) that lies in a zone.
	public float Coverage(int x, int z, int size)
	{
		if (size <= 1)
		{
			return Contains(x, z) ? 1f : 0f;
		}
		if (x > _maxX || z > _maxZ || x + size - 1 < _minX || z + size - 1 < _minZ)
		{
			return 0f;
		}
		if (size <= 4)
		{
			int count = 0;
			for (int i = 0; i < size; i++)
			{
				for (int j = 0; j < size; j++)
				{
					if (Contains(x + i, z + j))
					{
						count++;
					}
				}
			}
			return count / (float)(size * size);
		}
		// Allied factions' rectangles may overlap each other: never more than all of it.
		long covered = 0;
		foreach (NoGoRect rect in _rects)
		{
			covered += rect.Overlap(x, z, size);
		}
		return Math.Min(1f, covered / ((float)size * size));
	}
}
