using System;
using System.Collections.Generic;
using System.Diagnostics;
using Brix.Game.AI;
using HarmonyLib;
using Motus.Behavior;
using UnityEngine;

namespace CastleStoryPlus.Diagnostics;

// Where a worker's behaviour tree spends its time. "fixed: Labor" is the whole tree of every worker each tick: five
// sequences run side by side under one parallel node (Labor.Initialize): the current task's work, switching to a
// pending task, looking for work, the autonomy's expiry and getting unstuck from another unit or a tree. Each of the
// five is charged to a bucket of its own ("labor tree: 3 LookForWork"), and the current task's work also to one per
// kind of task ("labor work: DigGoal", "labor work: GoStore"): the goal's type for a task from a goal, else the
// instruction's name. What "fixed: Labor" has beyond the five is the tree's root itself.
// Only the root's own parallel node is timed (the first one a worker's tick runs); the others inside the work are
// part of their sequence. The root's loop is run here as the game runs it (ParallelSequencer.AllDone), with a clock
// read around each sequence. Timed on its own, not on FrameMonitor's stack, like LuaFunctionTiming. With
// [Performance] MeasureAllocations each sequence and kind of work is also charged what it allocated.
internal static class LaborTreeTiming
{
	// Labor.Initialize adds its sequences in this order.
	private static readonly string[] SequenceNames = new string[5] { "1 CurrentWork", "2 PendingWork", "3 LookForWork", "4 Autonomy", "5 VoxelPresence" };

	private static readonly FrameMonitor.Bucket[] Sequences = new FrameMonitor.Bucket[SequenceNames.Length];

	private const int MostTasks = 512;

	// The work bucket of each task seen, so a task's kind is worked out once.
	private static readonly Dictionary<Task, FrameMonitor.Bucket> WorkBuckets = new Dictionary<Task, FrameMonitor.Bucket>();

	private static readonly Dictionary<int, FrameMonitor.Bucket> OtherSequences = new Dictionary<int, FrameMonitor.Bucket>();

	// The worker whose fixed update runs, until its tree's root parallel node is reached.
	private static Labor _labor;

	internal static void Patch(Harmony harmony)
	{
		try
		{
			harmony.Patch(AccessTools.Method(typeof(Brix.Game.Components.Behavior), "BrixFixedUpdate"), prefix: new HarmonyMethod(typeof(LaborTreeTiming), nameof(TickPrefix)), finalizer: new HarmonyMethod(typeof(LaborTreeTiming), nameof(TickFinalizer)));
			harmony.Patch(AccessTools.Method(typeof(ParallelSequencer.AllDone), "TickParallelNodes"), prefix: new HarmonyMethod(typeof(LaborTreeTiming), nameof(RootPrefix)));
			for (int i = 0; i < SequenceNames.Length; i++)
			{
				Sequences[i] = FrameMonitor.Named("labor tree: " + SequenceNames[i]);
			}
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning("Performance: could not measure the workers' behaviour tree: " + ex.Message);
		}
	}

	private static void TickPrefix(Brix.Game.Components.Behavior __instance)
	{
		_labor = __instance as Labor;
	}

	private static void TickFinalizer()
	{
		_labor = null;
	}

	// ParallelSequencer.AllDone.TickParallelNodes as the game has it, timed per sequence.
	private static bool RootPrefix(ParallelSequencer.AllDone __instance)
	{
		Labor labor = _labor;
		if (labor == null)
		{
			return true;
		}
		_labor = null;
		List<NodeRun> runs = __instance._subNodeRuns;
		bool named = runs.Count == SequenceNames.Length;
		for (int i = 0; i < runs.Count; i++)
		{
			NodeRun nodeRun = runs[i];
			Task task = (named && i == 0) ? labor.CurrentTask : null;
			bool allocations = FrameMonitor.Allocations;
			int gcs = allocations ? GC.CollectionCount(0) : 0;
			long used = allocations ? GC.GetTotalMemory(false) : 0L;
			long start = Stopwatch.GetTimestamp();
			NodeState state;
			try
			{
				state = nodeRun.Run();
			}
			finally
			{
				long ticks = Stopwatch.GetTimestamp() - start;
				// What the sequence allocated, as FrameMonitor measures it (a call with a collection inside is left out).
				long bytes = (allocations && GC.CollectionCount(0) == gcs) ? GC.GetTotalMemory(false) - used : 0L;
				Charge(named ? Sequences[i] : OtherSequence(i), ticks, bytes);
				if (task != null)
				{
					Charge(WorkBucket(task), ticks, bytes);
				}
			}
			if (state != NodeState.Running)
			{
				if (nodeRun.State == NodeState.Fail)
				{
					__instance.Exception = nodeRun.Exception;
					__instance.State = NodeState.Fail;
					break;
				}
				nodeRun.Dispose();
				runs.RemoveAt(i);
				i--;
			}
		}
		return false;
	}

	private static void Charge(FrameMonitor.Bucket bucket, long ticks, long bytes)
	{
		if (bucket != null)
		{
			bucket.FrameTicks += ticks;
			bucket.FrameCalls++;
			if (bytes > 0)
			{
				bucket.FrameBytes += bytes;
			}
		}
	}

	private static FrameMonitor.Bucket OtherSequence(int index)
	{
		if (!OtherSequences.TryGetValue(index, out FrameMonitor.Bucket bucket))
		{
			bucket = FrameMonitor.Named("labor tree: sequence " + (index + 1));
			OtherSequences[index] = bucket;
		}
		return bucket;
	}

	private static FrameMonitor.Bucket WorkBucket(Task task)
	{
		if (!WorkBuckets.TryGetValue(task, out FrameMonitor.Bucket bucket))
		{
			if (WorkBuckets.Count >= MostTasks)
			{
				WorkBuckets.Clear();
			}
			bucket = FrameMonitor.Named("labor work: " + WorkName(task));
			WorkBuckets[task] = bucket;
		}
		return bucket;
	}

	// A goal's task is described with the worker's and the goal's names, one of a kind; its goal's type is not.
	private static string WorkName(Task task)
	{
		try
		{
			if (task.Giver != null)
			{
				Goal goal = task.Giver.GetComponent<Goal>();
				if (goal != null)
				{
					return goal.GetType().Name;
				}
			}
		}
		catch (Exception)
		{
		}
		return string.IsNullOrEmpty(task.Description) ? "unnamed" : task.Description;
	}
}
