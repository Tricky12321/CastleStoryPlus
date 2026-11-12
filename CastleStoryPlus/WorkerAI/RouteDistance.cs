using System.Collections.Generic;
using Brix.Engine;
using Brix.Game;
using Brix.Game.AI;
using Brix.Pathfinding;
using Brix.Pathfinding.Test;
using UnityEngine;

namespace CastleStoryPlus.WorkerAI;

// How far a worker has to walk to a place, over the route graph, instead of as the crow flies (#69). The game picks
// the log to fetch, the tree to fell and the loose item to store by the straight line to it, so a worker on a hill ran
// all the way down and round the slope for a log that lay just below its feet, while logs and trees on its own level
// waited. A flood from the worker's own place over the fine route graph (by the rules of its route search: walls it
// can climb, doors of its own faction, no breaking through) gives the walking cost to every place within MaxCost:
// a block walked counts 1, a block climbed up or stepped down ClimbCost more. A flood is kept for KeepSeconds while
// the worker stands on the same place, so the many tasks of one search share it. Host only.
internal static class RouteDistance
{
	// Walking cost a flood reaches at most, and places it visits at most.
	internal const int MaxCost = 160;

	private const int MaxNodes = 6000;

	// What a block up or down costs on top of the block walked (a climb is slow).
	private const int ClimbCost = 3;

	private const float KeepSeconds = 4f;

	// Fields not asked for this long are dropped.
	private const float ForgetSeconds = 30f;

	internal sealed class Field
	{
		public PathNode Start;

		public float At;

		public float AskedAt;

		public readonly Dictionary<XYZ, int> Cost = new Dictionary<XYZ, int>();
	}

	private static readonly Dictionary<Labor, Field> Fields = new Dictionary<Labor, Field>();

	private static readonly Stack<Field> Spare = new Stack<Field>();

	private static readonly Dictionary<PathNode, int> Best = new Dictionary<PathNode, int>();

	private static readonly List<PathNode>[] Buckets = new List<PathNode>[MaxCost + 1];

	private static float _nextPrune;

	internal static void Clear()
	{
		Fields.Clear();
		Spare.Clear();
	}

	// The worker's walking field, made now when there is none for the place it stands on; null when it stands on no
	// place of the route graph.
	internal static Field For(Labor labor)
	{
		if (labor == null || labor.presence == null)
		{
			return null;
		}
		PathNode start = labor.presence.CurrentPathNode;
		if (start == null || start.IsEmpty || start.Lod != 0)
		{
			return null;
		}
		Prune();
		if (Fields.TryGetValue(labor, out Field field) && field.Start == start && Time.time - field.At < KeepSeconds)
		{
			field.AskedAt = Time.time;
			return field;
		}
		TestPathfinding pathfinding = TestPathfinding.Instance;
		if (pathfinding == null || !pathfinding.Initialized)
		{
			return null;
		}
		if (field == null)
		{
			field = (Spare.Count > 0) ? Spare.Pop() : new Field();
			Fields[labor] = field;
		}
		field.Start = start;
		field.At = Time.time;
		field.AskedAt = Time.time;
		Flood(labor, start, field.Cost);
		return field;
	}

	// The field made for the worker during this search, if it still stands where it was made; none is made here.
	internal static Field Current(Labor labor)
	{
		if (labor == null || labor.presence == null || !Fields.TryGetValue(labor, out Field field))
		{
			return null;
		}
		return (field.Start == labor.presence.CurrentPathNode && Time.time - field.At < KeepSeconds) ? field : null;
	}

	// The walking cost to a thing at the position: to the cheapest place by it (its own column or one out, level with
	// it, up to 2 lower or 1 higher), -1 when no such place lies within the field.
	internal static int To(Field field, Vector3 position)
	{
		if (field == null)
		{
			return -1;
		}
		XYZ at = XYZ.FromVector3(position);
		int best = -1;
		for (int dx = -1; dx <= 1; dx++)
		{
			for (int dz = -1; dz <= 1; dz++)
			{
				for (int dy = -2; dy <= 1; dy++)
				{
					if (field.Cost.TryGetValue(new XYZ(at.x + dx, at.y + dy, at.z + dz), out int cost))
					{
						// Reaching out from the next column costs a little, so of two equal routes the one ending
						// right at the thing counts first.
						cost += (dx != 0 || dz != 0) ? 1 : 0;
						if (best < 0 || cost < best)
						{
							best = cost;
						}
					}
				}
			}
		}
		return best;
	}

	// A cost to rank things by: the walking cost when the field reaches it, else past every walking cost, ranked among
	// themselves by twice the straight line (a far or blocked route is longer than the line).
	internal static int Rank(Field field, Vector3 from, Vector3 position)
	{
		int cost = To(field, position);
		if (cost >= 0)
		{
			return cost;
		}
		return MaxCost + 1 + Mathf.RoundToInt(2f * Vector3.Distance(from, position));
	}

	private static void Flood(Labor labor, PathNode start, Dictionary<XYZ, int> cost)
	{
		cost.Clear();
		Best.Clear();
		for (int i = 0; i < Buckets.Length; i++)
		{
			if (Buckets[i] != null)
			{
				Buckets[i].Clear();
			}
		}
		GameObject self = labor.gameObject;
		bool forest = labor.Profession != null && labor.Profession.CanWalkInForest();
		SearchPathRequest.BranchEvaluator evaluator = new SearchPathRequest.BranchEvaluator((XYZ p) => !SearchPathRequest.HasBlockerAt(self, p), canClimb: true, forest, canBreakVoxels: false);
		Best[start] = 0;
		Bucket(0).Add(start);
		int visited = 0;
		for (int c = 0; c <= MaxCost && visited < MaxNodes; c++)
		{
			List<PathNode> bucket = Buckets[c];
			if (bucket == null)
			{
				continue;
			}
			for (int i = 0; i < bucket.Count && visited < MaxNodes; i++)
			{
				PathNode node = bucket[i];
				if (Best[node] != c)
				{
					continue;
				}
				XYZ at = node.RepresentativePosition;
				if (cost.ContainsKey(at))
				{
					continue;
				}
				cost[at] = c;
				visited++;
				if (!(node is TerrainPathNode terrain) || terrain.siblings == null)
				{
					continue;
				}
				foreach (PathBranch branch in terrain.siblings)
				{
					PathNode next = branch.Node;
					if (next.IsEmpty || next.Lod != 0 || !evaluator.CanCross(branch))
					{
						continue;
					}
					int step = 1 + ClimbCost * Mathf.Abs(next.RepresentativePosition.y - at.y);
					int total = c + step;
					if (total > MaxCost || (Best.TryGetValue(next, out int known) && known <= total))
					{
						continue;
					}
					Best[next] = total;
					Bucket(total).Add(next);
				}
			}
		}
		Best.Clear();
		for (int i = 0; i < Buckets.Length; i++)
		{
			if (Buckets[i] != null)
			{
				Buckets[i].Clear();
			}
		}
	}

	private static List<PathNode> Bucket(int cost)
	{
		List<PathNode> bucket = Buckets[cost];
		if (bucket == null)
		{
			bucket = new List<PathNode>();
			Buckets[cost] = bucket;
		}
		return bucket;
	}

	private static void Prune()
	{
		if (Time.time < _nextPrune)
		{
			return;
		}
		_nextPrune = Time.time + ForgetSeconds;
		List<Labor> old = null;
		foreach (KeyValuePair<Labor, Field> pair in Fields)
		{
			if (pair.Key == null || Time.time - pair.Value.AskedAt > ForgetSeconds)
			{
				(old ?? (old = new List<Labor>())).Add(pair.Key);
			}
		}
		if (old == null)
		{
			return;
		}
		foreach (Labor labor in old)
		{
			Field field = Fields[labor];
			Fields.Remove(labor);
			field.Cost.Clear();
			field.Start = null;
			Spare.Push(field);
		}
	}
}
