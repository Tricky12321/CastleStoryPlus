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

		public readonly List<XYZ> Blocks = new List<XYZ>();

		public int Next;

		public DigGoal Goal;

		public float Until;
	}

	private readonly Dictionary<Labor, Escape> _escapes = new Dictionary<Labor, Escape>();

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
			if (_escapes.Count > 0)
			{
				_escapes.Clear();
			}
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
			if (labor.IsNullOrReleased() || labor.faction == null || labor.faction.isAI || _escapes.ContainsKey(labor) || !labor.IsAvailable() || labor.CurrentTask != null)
			{
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
			return;
		}
		XYZ start = node.RepresentativePosition;
		List<XYZ> blocks = WayOut(labor.faction, start, pocket, out XYZ exit);
		if (blocks == null)
		{
			return;
		}
		Escape escape = new Escape { Labor = labor, Until = Time.time + EscapeSeconds };
		escape.Blocks.AddRange(blocks);
		_escapes[labor] = escape;
		Plugin.Log.LogInfo("StuckEscape: " + labor.gameObject.name + " is shut in at " + start + " (" + pocket.Count + " places), digging " + blocks.Count + " blocks out to " + exit);
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
			labor.AbortWorkSignal.Invoke(TaskResult.ReceivedNewInstruction);
			labor.PendingTask = null;
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
	// what cannot be broken).
	private static List<XYZ> WayOut(Faction faction, XYZ start, HashSet<XYZ> pocket, out XYZ exit)
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
				return Blocks(start, cell, from);
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
					int step = StepCost(cell, next, dy);
					if (step < 0)
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
	private static int StepCost(XYZ cell, XYZ next, int dy)
	{
		if (!Solid(next - XYZ.up))
		{
			return -1;
		}
		int blocks = 0;
		foreach (XYZ room in StepRoom(cell, next, dy))
		{
			if (Voxel.IsEmpty(room))
			{
				continue;
			}
			if (!Diggable(room))
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

	private static bool Diggable(XYZ cell)
	{
		return Voxel.IsTerrain(cell) && !Voxel.IsInvincible(cell) && !Voxel.IsBlock(cell);
	}

	private static List<XYZ> Blocks(XYZ start, XYZ end, Dictionary<XYZ, XYZ> from)
	{
		List<XYZ> path = new List<XYZ>();
		XYZ at = end;
		while (at != start)
		{
			path.Add(at);
			at = from[at];
		}
		path.Reverse();
		List<XYZ> blocks = new List<XYZ>();
		XYZ previous = start;
		foreach (XYZ cell in path)
		{
			foreach (XYZ room in StepRoom(previous, cell, cell.y - previous.y))
			{
				if (!Voxel.IsEmpty(room) && !blocks.Contains(room))
				{
					blocks.Add(room);
				}
			}
			previous = cell;
		}
		return blocks;
	}

	// Gives each escaping worker its next block once the last is gone.
	private void Advance()
	{
		if (_escapes.Count == 0)
		{
			return;
		}
		List<Labor> done = null;
		foreach (Escape escape in _escapes.Values)
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
			Finish(_escapes[labor]);
			_escapes.Remove(labor);
		}
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
			// Still digging it; a worker that dropped the task gets it again.
			if (escape.Labor.IsAvailable() && escape.Labor.CurrentTask == null)
			{
				Send(escape.Labor, escape.Goal);
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
			return false;
		}
		XYZ block = escape.Blocks[escape.Next];
		if (!ObjectPoolSingleton.RequestComponent<DigGoal>(out DigGoal goal, Brix.External.Factories.Test.DigGoal, block.ToVector3(), Quaternion.identity) || goal == null)
		{
			return true;
		}
		goal.RebuildNode();
		goal.ResourceSetting = KeepNothing;
		goal.UseHeightAdjustment = false;
		escape.Goal = goal;
		Send(escape.Labor, goal);
		return false;
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
		if (escape.Goal != null && !escape.Goal.IsNullOrReleased())
		{
			ObjectPoolSingleton.Release(escape.Goal.gameObject);
		}
		escape.Goal = null;
	}

	private static void Finish(Escape escape)
	{
		Release(escape);
		if (!escape.Labor.IsNullOrReleased())
		{
			Plugin.Log.LogInfo("StuckEscape: " + escape.Labor.gameObject.name + (escape.Next >= escape.Blocks.Count ? " dug its way out" : " gave up digging out"));
		}
	}
}
