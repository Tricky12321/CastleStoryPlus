using System.Collections.Generic;
using Brix.Engine;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Lifecycle.Pooling;
using Brix.Pathfinding;
using Brix.Pathfinding.Test;
using CastleStoryPlus.Core;
using CastleStoryPlus.Pathfinding;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.WorkerAI;

// A worker shut in a hole (a pit dug too deep, a pocket closed off) digs its own way out. Every few seconds each idle
// worker's place on the route graph is flooded a little: when the flood runs out within MaxPocket places the worker
// is shut in. The cheapest way out is then looked for: a walk of steps at most one block up or down, on solid ground,
// through terrain only, to the nearest place of the faction's route map (ReachMap) outside the pocket. The worker
// digs it block by block, without drops (it is stone in the way, not work). Host only; nothing is saved.
// A worker shut in is seldom idle at the moment of a check (#62): the idle task's walks and sits, and tasks it tries
// and fails on in its pocket, keep a task on it nearly all the time, and an escape waited for it to be free between
// two blocks until it ran out of time. A busy worker is now found as well (still shut in at the same place over
// BusySeconds), and an escaping worker's other tasks are broken off so it digs on.
// A plan walked by its own step rules, not the route graph's: open ground beside the pocket the graph did not join to it
// was taken as a walk, and the worker was sent to dig a block it could never get to (the walk failed again and again,
// it stood there 100 s). Now a plan walks only through the pocket before its first dug block, each block is sent only
// when the worker can dig it from where the route graph lets it stand, and a block it is sent to again and again
// without it going (EscapeRules.MaxDigSends), or cannot get to, has the way planned again from where the worker stands,
// round that block. The tunnel is dug two wide where the ground lets it, so several workers shut in together do not
// jam in it one behind the other, and a worker that got out walks on a few blocks (EscapeRules.ExitDistance) to a free
// place instead of stopping on the tunnel's top step in the way of the others.
[Feature(Features.StuckEscape, Features.StuckEscapeInfo)]
internal class StuckEscape : MonoBehaviour
{
	private const float CheckSeconds = 5f;

	// Route places a pocket has at most (more is a part of the world, not a hole).
	private const int MaxPocket = 120;

	// How far a way out is looked for, in blocks from the worker, and how many places at most.
	private const int MaxReach = 16;

	private const int MaxSearch = 6000;

	// An escape not done by then is given up (looked at again later).
	private const float EscapeSeconds = 180f;

	// A SmartTransfer order that has left its worker standing idle for this long is stuck: a stockpile it cannot
	// step up to leaves the task waiting for ever (the game has no timeout for a current task).
	private const float StuckTransferSeconds = 10f;

	// A worker with a task counts as shut in once it has been in the same pocket this long (its task may yet take it
	// out: a dig opening the pocket).
	private const float BusySeconds = 20f;

	// How often an escaping worker's other task is broken off at most.
	private const float AbortSeconds = 2f;

	private static readonly XYZ[] Sides = { new XYZ(1, 0, 0), new XYZ(-1, 0, 0), new XYZ(0, 0, 1), new XYZ(0, 0, -1) };

	// Dig goals of an escape keep nothing: the blocks are broken without a drop.
	private sealed class NoDrops : IDigGoalResourceSetting
	{
		public GroundResource TypesToHarvest => GroundResource.None;
	}

	private static readonly NoDrops KeepNothing = new NoDrops();

	private sealed class Escape
	{
		public Labor Labor;

		public XYZ Start;

		public XYZ Exit;

		public readonly List<XYZ> Blocks = new List<XYZ>();

		// The blocks that only make the tunnel wider: skipped when they cannot be dug, never a reason to plan again.
		public readonly HashSet<XYZ> Wide = new HashSet<XYZ>();

		// Blocks the worker could not get to: planned round from then on.
		public readonly HashSet<XYZ> Avoid = new HashSet<XYZ>();

		public int Next;

		public DigGoal Goal;

		// How often the current block's dig was sent to the worker.
		public int Sends;

		public int Replans;

		// The worker stands on the route graph outside any pocket: it is out.
		public bool Out;

		public float Until;

		public float LastAbort;
	}

	private static readonly Dictionary<Labor, Escape> Escapes = new Dictionary<Labor, Escape>();

	// The escapes' dig goals (the quarry's floor limit does not hold them back, QuarryStairs.TooDeep).
	private static readonly HashSet<DigGoal> EscapeDigs = new HashSet<DigGoal>();

	// Places workers that dug their way out were just sent to, and until when they are kept for them.
	private static readonly Dictionary<XYZ, float> ExitSpots = new Dictionary<XYZ, float>();

	// Each worker's last tunnel mouth (the escape's last block), for the tests.
	private static readonly Dictionary<Labor, XYZ> LastMouths = new Dictionary<Labor, XYZ>();

	// Where each worker's last escape started. A worker still shut in at the same place after digging is shut in by
	// something digging does not open (stockpiles all round): it is not sent to dig again there, or it would eat away
	// the terrain round it for ever.
	private readonly Dictionary<Labor, XYZ> _triedFrom = new Dictionary<Labor, XYZ>();

	// Busy workers found shut in: where, and since when.
	private readonly Dictionary<Labor, KeyValuePair<XYZ, float>> _busyShutIn = new Dictionary<Labor, KeyValuePair<XYZ, float>>();

	private float _nextCheck;

	private float _nextStuckCheck;

	private static void Enable()
	{
		Plugin.Root.AddComponent<StuckEscape>();
	}

	private void Update()
	{
		if (!NetworkServer.active)
		{
			if (Escapes.Count > 0)
			{
				Escapes.Clear();
			}
			EscapeDigs.Clear();
			ExitSpots.Clear();
			LastMouths.Clear();
			_triedFrom.Clear();
			_busyShutIn.Clear();
			return;
		}
		Advance();
		if (Time.time >= _nextStuckCheck)
		{
			_nextStuckCheck = Time.time + 2f;
			CancelStuckTransfers();
		}
		if (Time.time < _nextCheck)
		{
			return;
		}
		_nextCheck = Time.time + CheckSeconds;
		TestPathfinding pathfinding = TestPathfinding.Instance;
		if (pathfinding == null || !pathfinding.Initialized)
		{
			return;
		}
		foreach (Labor labor in Live<Labor>.Active())
		{
			if (labor.IsNullOrReleased() || labor.faction == null || labor.faction.isAI || Escapes.ContainsKey(labor) || labor.IsPhasedOut || labor.Activity == Activity.Fighting)
			{
				_busyShutIn.Remove(labor);
				continue;
			}
			try
			{
				Check(labor);
			}
			catch (System.Exception ex)
			{
				Plugin.Log.LogWarning("StuckEscape: " + ex.Message);
			}
		}
	}

	// Whether the worker is digging its way out of a pit (StuckReset leaves it alone: the escape has its own limits).
	internal static bool IsEscaping(Labor labor)
	{
		return labor != null && Escapes.ContainsKey(labor);
	}

	// Where the worker's last escape came out (its last block); false when it never dug its way out.
	internal static bool LastMouth(Labor labor, out XYZ mouth)
	{
		return LastMouths.TryGetValue(labor, out mouth);
	}

	internal static bool IsEscapeDig(DigGoal goal)
	{
		return goal != null && EscapeDigs.Contains(goal);
	}

	private void Check(Labor labor)
	{
		if (labor.presence == null || !ReachMap.Has(labor.faction))
		{
			return;
		}
		PathNode node = labor.presence.CurrentPathNode;
		if (node == null || node.IsEmpty || node.Lod != 0)
		{
			return;
		}
		HashSet<XYZ> pocket = Pocket(labor, node);
		if (pocket == null)
		{
			_busyShutIn.Remove(labor);
			return;
		}
		XYZ start = node.RepresentativePosition;
		bool busy = !labor.IsAvailable() || labor.CurrentTask != null;
		if (busy)
		{
			if (!_busyShutIn.TryGetValue(labor, out KeyValuePair<XYZ, float> since) || since.Key != start)
			{
				_busyShutIn[labor] = new KeyValuePair<XYZ, float>(start, Time.time);
				return;
			}
			if (Time.time - since.Value < BusySeconds)
			{
				return;
			}
		}
		if (_triedFrom.TryGetValue(labor, out XYZ tried))
		{
			if (tried == start)
			{
				return;
			}
			_triedFrom.Remove(labor);
		}
		Escape escape = new Escape { Labor = labor, Start = start, Until = Time.time + EscapeSeconds };
		if (!Plan(escape, start, pocket))
		{
			// Nothing to dig: an open way the route graph does not have is no way out by digging.
			return;
		}
		_busyShutIn.Remove(labor);
		Escapes[labor] = escape;
		Plugin.Log.LogInfo("StuckEscape: " + labor.gameObject.name + " is shut in at " + start + " (" + pocket.Count + " places" + (busy ? ", busy with '" + Describe(labor) + "'" : string.Empty) + "), digging " + escape.Blocks.Count + " blocks out to " + escape.Exit + " (" + escape.Wide.Count + " of them to make it two wide)");
	}

	// The escape's blocks planned from start (the worker's place) in its pocket, round the blocks it could not get to.
	// False when there is no way out to dig.
	private static bool Plan(Escape escape, XYZ start, HashSet<XYZ> pocket)
	{
		Labor labor = escape.Labor;
		List<XYZ> path = WayOut(labor.gameObject, labor.faction, start, pocket, escape.Avoid, out XYZ exit);
		if (path == null)
		{
			return false;
		}
		List<XYZ> blocks = Blocks(labor.gameObject, start, path, escape.Avoid, escape.Wide);
		if (blocks.Count == 0)
		{
			return false;
		}
		escape.Start = start;
		escape.Exit = exit;
		escape.Blocks.Clear();
		escape.Blocks.AddRange(blocks);
		escape.Next = 0;
		escape.Sends = 0;
		return true;
	}

	// The escape's block could not be dug from where the worker can stand, or its walk failed again and again: the way
	// is planned again from where the worker stands now, round that block. True when the escape is over (given up, or
	// the worker is out after all).
	private static bool Replan(Escape escape, XYZ block, string why)
	{
		Release(escape);
		escape.Avoid.Add(block);
		Labor labor = escape.Labor;
		PathNode node = (labor.presence != null) ? labor.presence.CurrentPathNode : null;
		if (node == null || node.IsEmpty || node.Lod != 0)
		{
			return true;
		}
		HashSet<XYZ> pocket = Pocket(labor, node);
		if (pocket == null)
		{
			escape.Out = true;
			return true;
		}
		escape.Replans++;
		if (escape.Replans > EscapeRules.MaxReplans || !Plan(escape, node.RepresentativePosition, pocket))
		{
			Plugin.Log.LogInfo("StuckEscape: " + labor.gameObject.name + " cannot dig " + block + " (" + why + ") and finds no other way out");
			return true;
		}
		Plugin.Log.LogInfo("StuckEscape: " + labor.gameObject.name + " cannot dig " + block + " (" + why + "): planned again from " + node.RepresentativePosition + ", " + escape.Blocks.Count + " blocks out to " + escape.Exit);
		return false;
	}

	// SmartTransfer orders that have left their worker standing still for too long: the game's stockpile order has
	// no timeout, and a stockpile no worker can step up to leaves the task current for ever.
	private static void CancelStuckTransfers()
	{
		foreach (Labor labor in Live<Labor>.Active())
		{
			if (labor.IsNullOrReleased() || labor.faction == null || labor.faction.isAI)
			{
				continue;
			}
			Task task = labor.CurrentTask;
			if (task == null || task.Description != "SmartTransfer" || labor.Activity != Activity.Idle)
			{
				continue;
			}
			if (Time.time - task.TaskStartedTimestamp < StuckTransferSeconds)
			{
				continue;
			}
			Locomotion4 locomotion = (labor.navigation != null) ? labor.navigation.Locomotion : null;
			if (locomotion == null || !locomotion.IsInPlace())
			{
				continue;
			}
			Plugin.Log.LogInfo("StuckTransfer: " + labor.gameObject.name + " cancels its SmartTransfer order after " + (int)(Time.time - task.TaskStartedTimestamp) + " s standing idle");
			// The game's own handler of this signal also clears the pending task.
			labor.AbortWorkSignal.Invoke(TaskResult.ReceivedNewInstruction);
		}
	}

	// The places reachable from the node, or null when there are more than MaxPocket (not shut in).
	private static HashSet<XYZ> Pocket(Labor labor, PathNode start)
	{
		GameObject self = labor.gameObject;
		bool forest = labor.Profession != null && labor.Profession.CanWalkInForest();
		SearchPathRequest.BranchEvaluator evaluator = new SearchPathRequest.BranchEvaluator((XYZ p) => !SearchPathRequest.HasBlockerAt(self, p), canClimb: true, forest, canBreakVoxels: false);
		HashSet<PathNode> seen = new HashSet<PathNode> { start };
		HashSet<XYZ> places = new HashSet<XYZ> { start.RepresentativePosition };
		Queue<PathNode> open = new Queue<PathNode>();
		open.Enqueue(start);
		while (open.Count > 0)
		{
			PathNode node = open.Dequeue();
			if (!(node is TerrainPathNode terrain) || terrain.siblings == null)
			{
				continue;
			}
			foreach (PathBranch branch in terrain.siblings)
			{
				PathNode next = branch.Node;
				if (next.IsEmpty || next.Lod != 0 || seen.Contains(next) || !evaluator.CanCross(branch))
				{
					continue;
				}
				seen.Add(next);
				places.Add(next.RepresentativePosition);
				if (places.Count > MaxPocket)
				{
					return null;
				}
				open.Enqueue(next);
			}
		}
		return places;
	}

	// The blocks to dig, in order, for the cheapest walk from start to a place on the faction's routes outside the
	// pocket: steps one block up or down at most, always on solid ground, through terrain only (not buildings or
	// what cannot be broken), and never through an obstacle such as a stockpile (a worker walled in by stockpiles
	// digs round them).
	// Before its first dug block the walk keeps to the pocket (EscapeRules.MayWalk): open ground beside it that the
	// route graph does not join to it cannot be walked to. Returns the walk's places (start not included).
	private static List<XYZ> WayOut(GameObject self, Faction faction, XYZ start, HashSet<XYZ> pocket, HashSet<XYZ> avoid, out XYZ exit)
	{
		exit = start;
		Dictionary<XYZ, int> cost = new Dictionary<XYZ, int> { [start] = 0 };
		Dictionary<XYZ, XYZ> from = new Dictionary<XYZ, XYZ>();
		List<XYZ> open = new List<XYZ> { start };
		int searched = 0;
		while (open.Count > 0 && searched < MaxSearch)
		{
			int best = 0;
			for (int i = 1; i < open.Count; i++)
			{
				if (cost[open[i]] < cost[open[best]])
				{
					best = i;
				}
			}
			XYZ cell = open[best];
			open.RemoveAt(best);
			searched++;
			if (!pocket.Contains(cell) && ReachMap.Contains(faction, cell))
			{
				exit = cell;
				return Path(start, cell, from);
			}
			foreach (XYZ side in Sides)
			{
				for (int dy = -1; dy <= 1; dy++)
				{
					XYZ next = cell + side + new XYZ(0, dy, 0);
					if (Mathf.Abs(next.x - start.x) > MaxReach || Mathf.Abs(next.y - start.y) > MaxReach || Mathf.Abs(next.z - start.z) > MaxReach)
					{
						continue;
					}
					int step = StepCost(self, cell, next, dy, avoid);
					if (step < 0)
					{
						continue;
					}
					if (step == 0 && !EscapeRules.MayWalk(pocket.Contains(cell), pocket.Contains(next), !pocket.Contains(next) && ReachMap.Contains(faction, next)))
					{
						continue;
					}
					// Each block dug costs more than a step walked, so open ways are taken first.
					int total = cost[cell] + 1 + step * 10;
					if (cost.TryGetValue(next, out int known) && known <= total)
					{
						continue;
					}
					cost[next] = total;
					from[next] = cell;
					if (!open.Contains(next))
					{
						open.Add(next);
					}
				}
			}
		}
		return null;
	}

	// Blocks to dig for the step from cell to next (dy up or down), or -1 when it cannot be made.
	private static int StepCost(GameObject self, XYZ cell, XYZ next, int dy, HashSet<XYZ> avoid)
	{
		if (!Solid(next - XYZ.up))
		{
			return -1;
		}
		int blocks = 0;
		foreach (XYZ room in StepRoom(cell, next, dy))
		{
			// The whole body and the head room, not only the feet: a worker dug in under a stockpile could not
			// climb out where the stockpile hung over its head.
			if (SearchPathRequest.HasBlockerAt(self, room))
			{
				return -1;
			}
			if (Voxel.IsEmpty(room))
			{
				continue;
			}
			if (!Diggable(room) || avoid.Contains(room))
			{
				return -1;
			}
			blocks++;
		}
		return blocks;
	}

	// The cells a worker passes through on the step: its body (two high) where it ends up, and the room over its
	// head to go up or down.
	private static IEnumerable<XYZ> StepRoom(XYZ cell, XYZ next, int dy)
	{
		yield return next;
		yield return next + XYZ.up;
		if (dy == 1)
		{
			yield return cell + XYZ.up + XYZ.up;
		}
		else if (dy == -1)
		{
			yield return next + XYZ.up + XYZ.up;
		}
	}

	private static bool Solid(XYZ cell)
	{
		return !Voxel.IsEmpty(cell);
	}

	// Terrain only, and never a quarry's stair step or the ground under one (QuarryStairs.Keeps): the way out goes round
	// the stair instead of taking it away.
	private static bool Diggable(XYZ cell)
	{
		return Voxel.IsTerrain(cell) && !Voxel.IsInvincible(cell) && !Voxel.IsBlock(cell) && !QuarryStairs.Keeps(cell);
	}

	private static List<XYZ> Path(XYZ start, XYZ end, Dictionary<XYZ, XYZ> from)
	{
		List<XYZ> path = new List<XYZ>();
		XYZ at = end;
		while (at != start)
		{
			path.Add(at);
			at = from[at];
		}
		path.Reverse();
		return path;
	}

	// The blocks to dig along the walk, in order: each step's own room, then the same room beside it (to the left of the
	// way) where that can be dug and has ground under it, so the tunnel is two wide (wide: those blocks).
	private static List<XYZ> Blocks(GameObject self, XYZ start, List<XYZ> path, HashSet<XYZ> avoid, HashSet<XYZ> wide)
	{
		wide.Clear();
		List<XYZ> blocks = new List<XYZ>();
		XYZ previous = start;
		foreach (XYZ cell in path)
		{
			int dy = cell.y - previous.y;
			foreach (XYZ room in StepRoom(previous, cell, dy))
			{
				if (!Voxel.IsEmpty(room) && !blocks.Contains(room))
				{
					blocks.Add(room);
				}
			}
			EscapeRules.Side(cell.x - previous.x, cell.z - previous.z, out int sx, out int sz);
			XYZ side = new XYZ(sx, 0, sz);
			if (StepCost(self, previous + side, cell + side, dy, avoid) > 0)
			{
				foreach (XYZ room in StepRoom(previous + side, cell + side, dy))
				{
					if (!Voxel.IsEmpty(room) && !blocks.Contains(room))
					{
						blocks.Add(room);
						wide.Add(room);
					}
				}
			}
			previous = cell;
		}
		return blocks;
	}

	// Gives each escaping worker its next block once the last is gone.
	private void Advance()
	{
		if (Escapes.Count == 0)
		{
			return;
		}
		List<Labor> done = null;
		foreach (Escape escape in Escapes.Values)
		{
			if (Step(escape))
			{
				(done ?? (done = new List<Labor>())).Add(escape.Labor);
			}
		}
		if (done == null)
		{
			return;
		}
		foreach (Labor labor in done)
		{
			Escape escape = Escapes[labor];
			Finish(escape);
			Escapes.Remove(labor);
			// Only an escape that dug its whole way: one given up (the worker was kept from it) is tried again.
			if (!labor.IsNullOrReleased() && Done(escape))
			{
				_triedFrom[labor] = escape.Start;
			}
		}
		List<Labor> gone = null;
		foreach (Labor labor in _triedFrom.Keys)
		{
			if (labor.IsNullOrReleased())
			{
				(gone ?? (gone = new List<Labor>())).Add(labor);
			}
		}
		foreach (Labor labor in LastMouths.Keys)
		{
			if (labor.IsNullOrReleased())
			{
				(gone ?? (gone = new List<Labor>())).Add(labor);
			}
		}
		foreach (Labor labor in _busyShutIn.Keys)
		{
			if (labor.IsNullOrReleased())
			{
				(gone ?? (gone = new List<Labor>())).Add(labor);
			}
		}
		if (gone != null)
		{
			foreach (Labor labor in gone)
			{
				_triedFrom.Remove(labor);
				_busyShutIn.Remove(labor);
				LastMouths.Remove(labor);
			}
		}
	}

	private static bool Done(Escape escape)
	{
		return escape.Out || escape.Next >= escape.Blocks.Count;
	}

	// True when the escape is over (out, given up, the worker gone).
	private static bool Step(Escape escape)
	{
		if (escape.Labor.IsNullOrReleased() || Time.time > escape.Until)
		{
			return true;
		}
		if (escape.Goal != null && !escape.Goal.IsNullOrReleased() && !Voxel.IsEmpty(escape.Goal.voxel.position))
		{
			// Still digging it; a worker that dropped the task gets it again, one busy with another breaks it off.
			if (escape.Labor.IsAvailable() && escape.Labor.CurrentTask == null)
			{
				XYZ at = escape.Goal.voxel.position;
				if (escape.Sends >= EscapeRules.MaxDigSends)
				{
					// Sent again and again and still there: the worker cannot get to it (its walk failed, or the task
					// was taken off it each time).
					if (escape.Wide.Contains(at))
					{
						Release(escape);
						escape.Next++;
						escape.Sends = 0;
						return false;
					}
					return Replan(escape, at, "sent " + escape.Sends + " times");
				}
				Send(escape.Labor, escape.Goal);
				escape.Sends++;
			}
			else
			{
				BreakOff(escape);
			}
			return false;
		}
		Release(escape);
		while (escape.Next < escape.Blocks.Count && Voxel.IsEmpty(escape.Blocks[escape.Next]))
		{
			escape.Next++;
		}
		if (escape.Next >= escape.Blocks.Count)
		{
			return true;
		}
		if (!escape.Labor.IsAvailable() || escape.Labor.CurrentTask != null)
		{
			BreakOff(escape);
			return false;
		}
		XYZ block = escape.Blocks[escape.Next];
		if (QuarryStairs.Keeps(block))
		{
			// A quarry's stair step (or the ground under one) since the way was planned: given up, planned again later.
			return true;
		}
		// Only a block the worker can dig from a place the route graph lets it stand on (its pocket now).
		PathNode node = (escape.Labor.presence != null) ? escape.Labor.presence.CurrentPathNode : null;
		if (node != null && !node.IsEmpty && node.Lod == 0)
		{
			HashSet<XYZ> pocket = Pocket(escape.Labor, node);
			if (pocket == null)
			{
				// The worker's part of the graph is no pocket any more: it is out.
				escape.Out = true;
				return true;
			}
			if (!CanDigFromPocket(pocket, block))
			{
				if (escape.Wide.Contains(block))
				{
					escape.Next++;
					return false;
				}
				return Replan(escape, block, "out of reach of where it can stand");
			}
		}
		if (!ObjectPoolSingleton.RequestComponent<DigGoal>(out DigGoal goal, Brix.External.Factories.Test.DigGoal, block.ToVector3(), Quaternion.identity) || goal == null)
		{
			return true;
		}
		goal.RebuildNode();
		goal.ResourceSetting = KeepNothing;
		goal.UseHeightAdjustment = false;
		escape.Goal = goal;
		EscapeDigs.Add(goal);
		Send(escape.Labor, goal);
		escape.Sends = 1;
		return false;
	}

	private static bool CanDigFromPocket(HashSet<XYZ> pocket, XYZ block)
	{
		foreach (XYZ place in pocket)
		{
			if (EscapeRules.CanDigFrom(place.x, place.y, place.z, block.x, block.y, block.z))
			{
				return true;
			}
		}
		return false;
	}

	// The escaping worker's task, when it is not the escape's own dig: broken off (at most every AbortSeconds), so the
	// worker is free for the next block. A task in the pocket leads nowhere anyway.
	private static void BreakOff(Escape escape)
	{
		Labor labor = escape.Labor;
		Task task = labor.CurrentTask;
		if (task == null || Time.time - escape.LastAbort < AbortSeconds)
		{
			return;
		}
		if (escape.Goal != null && !escape.Goal.IsNullOrReleased() && task.Target == escape.Goal.gameObject)
		{
			return;
		}
		escape.LastAbort = Time.time;
		labor.AbortWorkSignal.Invoke(TaskResult.ReceivedNewInstruction);
	}

	private static string Describe(Labor labor)
	{
		Task task = labor.CurrentTask ?? labor.PendingTask;
		return (task != null) ? task.Description : "waiting";
	}

	private static void Send(Labor labor, Goal goal)
	{
		if (labor.Project != null)
		{
			goal.ConnectProject(labor.Project);
		}
		goal.Instruction.Send(labor, goal.gameObject, Labor.DecisionSource.Project);
	}

	private static void Release(Escape escape)
	{
		if (escape.Goal != null)
		{
			EscapeDigs.Remove(escape.Goal);
			if (!escape.Goal.IsNullOrReleased())
			{
				ObjectPoolSingleton.Release(escape.Goal.gameObject);
			}
		}
		escape.Goal = null;
	}

	private static void Finish(Escape escape)
	{
		Release(escape);
		if (escape.Labor.IsNullOrReleased())
		{
			return;
		}
		if (!Done(escape))
		{
			Plugin.Log.LogInfo("StuckEscape: " + escape.Labor.gameObject.name + " gave up digging out");
			return;
		}
		LastMouths[escape.Labor] = Mouth(escape);
		XYZ? spot = ExitSpot(escape);
		Plugin.Log.LogInfo("StuckEscape: " + escape.Labor.gameObject.name + " dug its way out" + (spot.HasValue ? ", walks on to " + spot.Value : string.Empty));
		if (spot.HasValue)
		{
			Order.Package.OfLocation(Order.Move.RunToward, Location.Of(spot.Value), Labor.DecisionSource.Project).Send(escape.Labor);
		}
	}

	// A free place on the faction's routes a few blocks on from the tunnel's mouth (EscapeRules.ExitDistance from the
	// way out and from the tunnel's last block), not one another worker stands on or was just sent to; null when there
	// is none near.
	private static XYZ? ExitSpot(Escape escape)
	{
		Faction faction = escape.Labor.faction;
		if (faction == null || !ReachMap.Has(faction))
		{
			return null;
		}
		float now = Time.time;
		List<XYZ> stale = null;
		foreach (KeyValuePair<XYZ, float> taken in ExitSpots)
		{
			if (taken.Value < now)
			{
				(stale ?? (stale = new List<XYZ>())).Add(taken.Key);
			}
		}
		if (stale != null)
		{
			foreach (XYZ old in stale)
			{
				ExitSpots.Remove(old);
			}
		}
		List<XYZ> standing = new List<XYZ>();
		foreach (Labor other in Live<Labor>.Active())
		{
			if (other != escape.Labor && !other.IsNullOrReleased())
			{
				standing.Add(XYZ.FromVector3(other.transform.position));
			}
		}
		XYZ mouth = Mouth(escape);
		HashSet<XYZ> seen = new HashSet<XYZ> { escape.Exit };
		Queue<XYZ> open = new Queue<XYZ>();
		open.Enqueue(escape.Exit);
		while (open.Count > 0 && seen.Count < 400)
		{
			XYZ cell = open.Dequeue();
			if (EscapeRules.FarFrom(cell.x, cell.z, escape.Exit.x, escape.Exit.z, EscapeRules.ExitDistance) && EscapeRules.FarFrom(cell.x, cell.z, mouth.x, mouth.z, EscapeRules.ExitDistance) && Free(cell, standing))
			{
				ExitSpots[cell] = now + 15f;
				return cell;
			}
			foreach (XYZ side in Sides)
			{
				for (int dy = -1; dy <= 1; dy++)
				{
					XYZ next = cell + side + new XYZ(0, dy, 0);
					if (seen.Contains(next) || !ReachMap.Contains(faction, next))
					{
						continue;
					}
					seen.Add(next);
					open.Enqueue(next);
				}
			}
		}
		return null;
	}

	private static XYZ Mouth(Escape escape)
	{
		return (escape.Blocks.Count > 0) ? escape.Blocks[escape.Blocks.Count - 1] : escape.Exit;
	}

	// No worker stands within a block of the place, and none was just sent there.
	private static bool Free(XYZ cell, List<XYZ> standing)
	{
		foreach (XYZ taken in ExitSpots.Keys)
		{
			if (!EscapeRules.FarFrom(cell.x, cell.z, taken.x, taken.z, 2) && Mathf.Abs(cell.y - taken.y) <= 2)
			{
				return false;
			}
		}
		foreach (XYZ at in standing)
		{
			if (!EscapeRules.FarFrom(cell.x, cell.z, at.x, at.z, 2) && Mathf.Abs(cell.y - at.y) <= 2)
			{
				return false;
			}
		}
		return true;
	}
}
