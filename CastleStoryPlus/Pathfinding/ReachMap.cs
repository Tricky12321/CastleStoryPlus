using System.Collections.Generic;
using Brix.Engine;
using Brix.Game;
using Brix.Game.AI;
using Brix.Pathfinding;
using Brix.Pathfinding.Test;
using CastleStoryPlus.Core;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.Pathfinding;

// Where a faction's workers can walk to: every place of the route graph connected to where one of them stands, by
// the rules their own route search uses (walls they can climb, doors of their own faction, no forest, no breaking
// through). The game's own reachability map (Knowledge) is coarser and says yes to places no route reaches (logs
// behind a wall the workers then walk to again and again), so this one is flooded over the fine graph itself. A
// flood takes at most 0.4 ms a frame and starts again when the world changed (at most every 5 seconds) or every so often
// (the workers move); the last finished one is the map in use. Host only.
// A flood reuses its collections: the queue and the visited set, and per faction the map before last (two maps a
// faction, one in use and one being flooded). New ones each time (some 150 000 places on a big island, a flood a
// second or two while workers dig) were half the garbage of a running game.
internal static class ReachMap
{
	// Time a running flood may take per frame, in places visited between checks of the clock. On the Unity 6 port the
	// flood took 2 % of the main thread on the Benchmark save (a change every few seconds kept one running in most
	// frames, 0.4 ms each): a smaller slice and longer waits there, the flood then takes a few seconds and runs less.
#if UNITY6
	private const double MillisecondsPerFrame = 0.2;
#else
	private const double MillisecondsPerFrame = 0.4;
#endif

	private const int NodesPerCheck = 500;

	private static readonly System.Diagnostics.Stopwatch Watch = new System.Diagnostics.Stopwatch();

	// A new flood at the latest this long after the last one, even with no change (workers move to other places).
#if UNITY6
	private const float RefreshSeconds = 30f;
#else
	private const float RefreshSeconds = 20f;
#endif

	// A new flood at the earliest this long after the last one, after a change. A quarry being dug changes the world
	// every second; flooding again each time kept a flood running in nearly every frame.
#if UNITY6
	private const float ChangeDelaySeconds = 10f;
#else
	private const float ChangeDelaySeconds = 5f;
#endif

	private sealed class Flood
	{
		public Faction Faction;

		public HashSet<XYZ> Reached;

		public Queue<PathNode> Open;

		public HashSet<PathNode> Seen;

		public SearchPathRequest.BranchEvaluator Evaluator;
	}

	// The finished map of each faction.
	private static readonly Dictionary<Faction, HashSet<XYZ>> Maps = new Dictionary<Faction, HashSet<XYZ>>();

	// Each faction's map before last, cleared and flooded again next time.
	private static readonly Dictionary<Faction, HashSet<XYZ>> Spare = new Dictionary<Faction, HashSet<XYZ>>();

	private static readonly Queue<PathNode> Open = new Queue<PathNode>();

	private static readonly HashSet<PathNode> Seen = new HashSet<PathNode>();

	private static Flood _running;

	private static readonly Queue<Faction> Pending = new Queue<Faction>();

	private static float _lastStart = -1000f;

	private static bool _changed;

	// Counts up each time a flood finishes, so users know a fresh map is there.
	internal static int Generation { get; private set; }

	internal static void Clear()
	{
		Maps.Clear();
		Spare.Clear();
		Open.Clear();
		Seen.Clear();
		Pending.Clear();
		_running = null;
		_lastStart = -1000f;
		_changed = false;
		Generation = 0;
	}

	internal static void MarkChanged()
	{
		_changed = true;
	}

	internal static bool Has(Faction faction)
	{
		return faction != null && Maps.ContainsKey(faction);
	}

	// Whether a worker of the faction can stand at the place (a node of the fine graph).
	internal static bool Contains(Faction faction, XYZ place)
	{
		return faction != null && Maps.TryGetValue(faction, out HashSet<XYZ> map) && map.Contains(place);
	}

	// Whether a worker can stand beside the place (its own column or up to beside columns out), level with it, up to
	// down blocks lower or up to up blocks higher: from there it reaches it.
	internal static bool Near(Faction faction, XYZ place, int beside, int down, int up)
	{
		if (faction == null || !Maps.TryGetValue(faction, out HashSet<XYZ> map))
		{
			return false;
		}
		for (int dx = -beside; dx <= beside; dx++)
		{
			for (int dz = -beside; dz <= beside; dz++)
			{
				for (int dy = -down; dy <= up; dy++)
				{
					if (map.Contains(new XYZ(place.x + dx, place.y + dy, place.z + dz)))
					{
						return true;
					}
				}
			}
		}
		return false;
	}

	// Every frame on the host.
	internal static void Update()
	{
		if (!NetworkServer.active)
		{
			return;
		}
		TestPathfinding pathfinding = TestPathfinding.Instance;
		if (pathfinding == null || !pathfinding.Initialized || pathfinding.terrainLod == null)
		{
			return;
		}
		if (_running == null)
		{
			if (Pending.Count == 0)
			{
				float since = Time.time - _lastStart;
				if (since < ChangeDelaySeconds || (!_changed && since < RefreshSeconds))
				{
					return;
				}
				_changed = false;
				_lastStart = Time.time;
				foreach (Faction faction in WorkerFactions())
				{
					Pending.Enqueue(faction);
				}
				if (Pending.Count == 0)
				{
					return;
				}
			}
			_running = Start(Pending.Dequeue(), pathfinding);
			if (_running == null)
			{
				return;
			}
		}
		Watch.Reset();
		Watch.Start();
		bool done;
		do
		{
			done = Step(_running, NodesPerCheck);
		}
		while (!done && Watch.Elapsed.TotalMilliseconds < MillisecondsPerFrame);
		if (done)
		{
			if (Maps.TryGetValue(_running.Faction, out HashSet<XYZ> before))
			{
				Spare[_running.Faction] = before;
			}
			Maps[_running.Faction] = _running.Reached;
			Seen.Clear();
			_running = null;
			if (Pending.Count == 0)
			{
				Generation++;
			}
		}
	}

	// The factions with workers of a player (not the AI's corruptrons).
	private static List<Faction> WorkerFactions()
	{
		List<Faction> factions = new List<Faction>();
		foreach (Labor labor in Live<Labor>.Active())
		{
			Faction faction = labor.faction;
			if (faction != null && !faction.isAI && !factions.Contains(faction))
			{
				factions.Add(faction);
			}
		}
		return factions;
	}

	private static Flood Start(Faction faction, TestPathfinding pathfinding)
	{
		Open.Clear();
		Seen.Clear();
		if (!Spare.TryGetValue(faction, out HashSet<XYZ> reached))
		{
			reached = new HashSet<XYZ>();
		}
		Spare.Remove(faction);
		reached.Clear();
		Flood flood = new Flood { Faction = faction, Reached = reached, Open = Open, Seen = Seen };
		Labor any = null;
		foreach (Labor labor in Live<Labor>.Active())
		{
			if (labor.faction != faction || labor.presence == null)
			{
				continue;
			}
			any = any ?? labor;
			PathNode node = labor.presence.CurrentPathNode;
			if (node != null && !node.IsEmpty && node.Lod == 0 && flood.Seen.Add(node))
			{
				flood.Open.Enqueue(node);
				flood.Reached.Add(node.RepresentativePosition);
			}
		}
		if (any == null || flood.Open.Count == 0)
		{
			Spare[faction] = reached;
			return null;
		}
		GameObject self = any.gameObject;
		bool forest = any.Profession != null && any.Profession.CanWalkInForest();
		flood.Evaluator = new SearchPathRequest.BranchEvaluator((XYZ p) => !SearchPathRequest.HasBlockerAt(self, p), canClimb: true, forest, canBreakVoxels: false);
		return flood;
	}

	// Floods up to count places; true when the flood is done.
	private static bool Step(Flood flood, int count)
	{
		for (int i = 0; i < count; i++)
		{
			if (flood.Open.Count == 0)
			{
				return true;
			}
			PathNode node = flood.Open.Dequeue();
			if (!(node is TerrainPathNode terrain) || terrain.siblings == null)
			{
				continue;
			}
			foreach (PathBranch branch in terrain.siblings)
			{
				PathNode next = branch.Node;
				if (next.IsEmpty || next.Lod != 0 || flood.Seen.Contains(next) || !flood.Evaluator.CanCross(branch))
				{
					continue;
				}
				flood.Seen.Add(next);
				flood.Reached.Add(next.RepresentativePosition);
				flood.Open.Enqueue(next);
			}
		}
		return flood.Open.Count == 0;
	}
}
