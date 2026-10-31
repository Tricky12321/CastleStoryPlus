using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using Brix.Engine;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Game.Semantique;
using Brix.Pathfinding.Test;
using Brix.Utils;
using CastleStoryPlus.Core;
using CastleStoryPlus.Diagnostics;
using HarmonyLib;
using Motus.Behavior;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.WorkerAI;

// An idle worker asks every task it may work for for work, every 2 ticks (Labor.CheckProjectForWork,
// TryWorkInProject). A goal task only checks that it has goals and room before the worker pays for the full ask:
// the storage check for what it yields (BestRecepteurToStore), the resource check for what it needs and a search
// through all its goals, which then turns every goal down (reserved, unreachable, nowhere to store it, bag full).
// Two ticks later the worker asks again, and nothing has changed. With some 40 workers that was most of the
// workers' AI time.
// The job board remembers, per worker and task, an ask that came to nothing, and skips asking that task again
// while nothing it depends on has changed and the memo is young (1 s for the first 3 s of failing, then 3 s, after
// 15 s 5 s). Changes that end a memo: the task's number of goals, a worker joining or leaving the task or leaving
// one of its goals, the worker's own autonomy, operable, class, bag, task group or its moving 8 blocks, the route
// graph being rebuilt, and (for an ask that failed on storage or on finding a resource) any stockpile's content.
// The list of tasks, their order and every other rule of the search stay the game's: a skipped task is passed by
// exactly as one with no work. The idle task and raids are always asked, AI factions are left alone, nothing is
// saved, host only.
// [JobBoard] JobBoardMode: Off (the game's search), Shadow (asks as before, only counts the asks a memo would have
// skipped, and those that would have been skipped wrongly: "jobboard: false skip"), On (skips them).
[Feature(Features.JobBoard, Features.JobBoardInfo)]
internal static class JobBoard
{
	internal enum JobBoardMode
	{
		Off,
		Shadow,
		On
	}

	private sealed class Memo
	{
		internal int ProjectVersion;

		internal int GoalCount;

		internal int WorkerVersion;

		internal int ReachEpoch;

		// -1: the ask did not fail on storage, any change of a stockpile leaves the memo alone.
		internal int StorageEpoch;

		internal XYZ Where;

		internal float At;

		// When the worker's asks of this task began to fail, for the memo's lifetime.
		internal float FailingSince;
	}

	private sealed class Worker
	{
		internal int Version;

		internal readonly Dictionary<IOperable<Labor>, Memo> Memos = new Dictionary<IOperable<Labor>, Memo>();

		// An ask is running; the game's reasons for turning goals down are watched for storage ones.
		internal bool Asking;

		internal bool StorageFailure;
	}

	private const float ShortSeconds = 3f;

	private const float LongSeconds = 15f;

	private const int MoveBlocks = 8;

	private static ConfigEntry<JobBoardMode> _mode;

	private static readonly Dictionary<Labor, Worker> Workers = new Dictionary<Labor, Worker>();

	private static readonly Dictionary<IOperable<Labor>, int> ProjectVersions = new Dictionary<IOperable<Labor>, int>();

	private static int _storageEpoch;

	private static int _reachEpoch;

	private static HashSet<Thought> _storageThoughts;

	private static readonly Dictionary<string, FrameMonitor.Bucket> Buckets = new Dictionary<string, FrameMonitor.Bucket>();

	private static void Enable()
	{
		_mode = Plugin.Cfg.Bind("JobBoard", "JobBoardMode", JobBoardMode.On, "Off: idle workers ask every task for work as the game does. Shadow: the same, and the performance window counts the asks the job board would skip (jobboard: ...). On: an ask that came to nothing is not repeated until something it depends on changes or a few seconds have passed.");
		GameSession.OnLeave(() =>
		{
			Workers.Clear();
			ProjectVersions.Clear();
		});
	}

	internal static JobBoardMode Mode => (_mode != null) ? _mode.Value : JobBoardMode.Off;

	internal static bool IsOn => Mode == JobBoardMode.On;

	// Whether the worker's asks of this task are remembered.
	internal static bool Applies(Labor labor, IOperable<Labor> project)
	{
		if (Mode == JobBoardMode.Off || !NetworkServer.active || labor.IsNullOrReleased() || project == null)
		{
			return false;
		}
		if (project is IdleProject || project is RaidProject2)
		{
			return false;
		}
		return labor.faction != null && !labor.faction.isAI;
	}

	// Whether a memo says the ask would come to nothing again.
	internal static bool WouldSkip(Labor labor, IOperable<Labor> project)
	{
		if (!Workers.TryGetValue(labor, out Worker worker) || !worker.Memos.TryGetValue(project, out Memo memo))
		{
			return false;
		}
		if (!project.IsStillAlive)
		{
			worker.Memos.Remove(project);
			return false;
		}
		float now = Time.time;
		float failingFor = now - memo.FailingSince;
		float lifetime = (failingFor < ShortSeconds) ? 1f : ((failingFor < LongSeconds) ? 3f : 5f);
		if (now - memo.At > lifetime)
		{
			return false;
		}
		if (memo.WorkerVersion != worker.Version || memo.ProjectVersion != ProjectVersion(project) || memo.ReachEpoch != _reachEpoch)
		{
			return false;
		}
		if (memo.StorageEpoch >= 0 && memo.StorageEpoch != _storageEpoch)
		{
			return false;
		}
		if (project is Project goals && memo.GoalCount != goals.GoalCounts())
		{
			return false;
		}
		XYZ here = labor.voxel.position;
		return Math.Abs(here.x - memo.Where.x) <= MoveBlocks && Math.Abs(here.y - memo.Where.y) <= MoveBlocks && Math.Abs(here.z - memo.Where.z) <= MoveBlocks;
	}

	internal static void BeginAsk(Labor labor)
	{
		Worker worker = WorkerOf(labor);
		worker.Asking = true;
		worker.StorageFailure = false;
	}

	// The ask has ended: failed (a FailException) or the worker was given the task's work.
	internal static void EndAsk(Labor labor, IOperable<Labor> project, FailException error, bool wouldSkip)
	{
		if (labor.IsNullOrReleased() || project == null)
		{
			return;
		}
		Worker worker = WorkerOf(labor);
		bool storage = worker.StorageFailure;
		worker.Asking = false;
		worker.StorageFailure = false;
		// The counts' names are only put together while the performance window measures: each is a new string.
		bool counting = FrameMonitor.Detailed;
		string type = counting ? project.GetType().Name : null;
		if (error == null)
		{
			worker.Memos.Remove(project);
			if (counting)
			{
				Count("jobboard: success");
				Count("jobboard: success " + type);
				if (wouldSkip)
				{
					Count("jobboard: false skip");
					Count("jobboard: false skip " + type);
				}
			}
			return;
		}
		if (counting)
		{
			Count("jobboard: fail " + type);
			Count("jobboard: fail reason " + error.GetType().Name);
		}
		if (error is Exceptions.WorkFailedException failed && failed.Problem != null && IsStorageThought(failed.Problem.thought))
		{
			storage = true;
		}
		float now = Time.time;
		if (!worker.Memos.TryGetValue(project, out Memo memo))
		{
			memo = new Memo { FailingSince = now };
			worker.Memos[project] = memo;
		}
		memo.At = now;
		memo.WorkerVersion = worker.Version;
		memo.ProjectVersion = ProjectVersion(project);
		memo.ReachEpoch = _reachEpoch;
		memo.StorageEpoch = storage ? _storageEpoch : (-1);
		memo.GoalCount = (project is Project goals) ? goals.GoalCounts() : 0;
		memo.Where = labor.voxel.position;
	}

	internal static void DecisionError(AIError error, Labor labor)
	{
		if (labor == null || error == null || !Workers.TryGetValue(labor, out Worker worker) || !worker.Asking)
		{
			return;
		}
		if (IsStorageThought(error.thought))
		{
			worker.StorageFailure = true;
		}
	}

	private static bool IsStorageThought(Thought thought)
	{
		if (thought == null)
		{
			return false;
		}
		if (_storageThoughts == null)
		{
			_storageThoughts = new HashSet<Thought>
			{
				Exceptions.NoCorrespondingStorage.Thought,
				Exceptions.CannotFindWhatINeedToPickUp.Thought,
				Exceptions.LookingForStorage.Thought,
				Exceptions.CouldNotFindStorageForInventory.InventoryIsFull
			};
		}
		return _storageThoughts.Contains(thought);
	}

	private static Worker WorkerOf(Labor labor)
	{
		if (!Workers.TryGetValue(labor, out Worker worker))
		{
			worker = new Worker();
			Workers[labor] = worker;
		}
		return worker;
	}

	private static int ProjectVersion(IOperable<Labor> project)
	{
		ProjectVersions.TryGetValue(project, out int version);
		return version;
	}

	internal static void ProjectChanged(IOperable<Labor> project)
	{
		if (project == null || Mode == JobBoardMode.Off)
		{
			return;
		}
		ProjectVersions.TryGetValue(project, out int version);
		ProjectVersions[project] = version + 1;
	}

	internal static void WorkerChanged(Labor labor)
	{
		if (labor == null || Mode == JobBoardMode.Off)
		{
			return;
		}
		if (Workers.TryGetValue(labor, out Worker worker))
		{
			worker.Version++;
		}
	}

	internal static void StorageChanged()
	{
		_storageEpoch++;
	}

	internal static void RoutesChanged()
	{
		_reachEpoch++;
	}

	internal static void Forget(Labor labor)
	{
		if (labor != null)
		{
			Workers.Remove(labor);
		}
	}

	// A count for the performance window (only while it measures, like the other buckets).
	internal static void Count(string name)
	{
		if (!FrameMonitor.Detailed)
		{
			return;
		}
		if (!Buckets.TryGetValue(name, out FrameMonitor.Bucket bucket))
		{
			bucket = FrameMonitor.Named(name);
			Buckets[name] = bucket;
		}
		bucket.FrameCalls++;
	}

	// The time the board's own checks take.
	internal static void Charge(long ticks)
	{
		if (!FrameMonitor.Detailed)
		{
			return;
		}
		if (!Buckets.TryGetValue("jobboard: checks", out FrameMonitor.Bucket bucket))
		{
			bucket = FrameMonitor.Named("jobboard: checks");
			Buckets["jobboard: checks"] = bucket;
		}
		bucket.FrameTicks += ticks;
		bucket.FrameCalls++;
	}
}

// Each task a worker looks at: skipped when a memo says the ask would come to nothing (On), or counted (Shadow); a
// real ask is followed to its end for the memo.
[Feature(Features.JobBoard, Features.JobBoardInfo)]
[HarmonyPatch(typeof(Labor), nameof(Labor.TryWorkInProject))]
internal static class JobBoardAskPatch
{
	// Set by the prefix for its postfix (the method does not call itself).
	private static bool _wouldSkip;

	private static bool _skipped;

	[HarmonyPriority(Priority.Last)]
	private static bool Prefix(Labor __instance, List<IOperable<Labor>> projects, Var<int> index, ref Node __result, out IOperable<Labor> __state)
	{
		__state = null;
		_wouldSkip = false;
		_skipped = false;
		int i = index.Get();
		if (projects == null || i >= projects.Count)
		{
			return true;
		}
		IOperable<Labor> project = projects[i];
		if (!JobBoard.Applies(__instance, project))
		{
			return true;
		}
		__state = project;
		long start = System.Diagnostics.Stopwatch.GetTimestamp();
		_wouldSkip = JobBoard.WouldSkip(__instance, project);
		JobBoard.Charge(System.Diagnostics.Stopwatch.GetTimestamp() - start);
		if (!_wouldSkip)
		{
			return true;
		}
		JobBoard.Count("jobboard: memo would skip");
		if (!JobBoard.IsOn)
		{
			return true;
		}
		// As the game passes by a task with no work for the worker.
		JobBoard.Count("jobboard: skipped");
		_skipped = true;
		index.Set(i + 1);
		__result = Node.Empty;
		return false;
	}

	private static void Postfix(Labor __instance, Var<FailException> error, ref Node __result, IOperable<Labor> __state)
	{
		if (__state == null || _skipped)
		{
			return;
		}
		if (__result == Node.Empty)
		{
			JobBoard.Count("jobboard: no work");
			return;
		}
		JobBoard.Count("jobboard: attempts");
		// The game's ask leaves its FailException in error when it fails and clears it when the worker is given the
		// work. It is cleared first so a failure of an earlier task is not taken for this one's; the loop's end
		// result is the same (the last failure, or none).
		Node ask = __result;
		__result = (StaticNode)Node.Sequence.Do((Labor l, Var<FailException> e) =>
		{
			e.Set(null);
			JobBoard.BeginAsk(l);
		}, __instance, error).Do((Node n) => n, ask).Do((Labor l, IOperable<Labor> p, Var<FailException> e, bool w) =>
		{
			JobBoard.EndAsk(l, p, e.Get(), w);
		}, __instance, __state, error, _wouldSkip);
	}
}

// The game's reasons for turning goals down during an ask: storage ones tie the memo to the stockpiles' content.
[Feature(Features.JobBoard, Features.JobBoardInfo)]
[HarmonyPatch(typeof(AIErrorsStore), nameof(AIErrorsStore.AddDecisionError))]
internal static class JobBoardDecisionErrorPatch
{
	private static void Prefix(AIError error, Labor labor)
	{
		JobBoard.DecisionError(error, labor);
	}
}

// The worker's own changes: autonomy, operable, class.
[Feature(Features.JobBoard, Features.JobBoardInfo)]
[HarmonyPatch]
internal static class JobBoardWorkerPatch
{
	private static IEnumerable<MethodBase> TargetMethods()
	{
		yield return AccessTools.PropertySetter(typeof(Labor), nameof(Labor.Autonomy));
		yield return AccessTools.PropertySetter(typeof(Labor), nameof(Labor.Operable));
		yield return AccessTools.Method(typeof(Labor), nameof(Labor.OnOccupationChange));
	}

	private static void Postfix(Labor __instance)
	{
		JobBoard.WorkerChanged(__instance);
	}
}

// A worker joins or leaves a task: the task's room and the worker's group change.
[Feature(Features.JobBoard, Features.JobBoardInfo)]
[HarmonyPatch]
internal static class JobBoardCrewPatch
{
	private static IEnumerable<MethodBase> TargetMethods()
	{
		yield return AccessTools.Method(typeof(Project), nameof(Project.OnHire));
		yield return AccessTools.Method(typeof(Project), nameof(Project.OnFire));
	}

	private static void Postfix(Project __instance, Labor labor)
	{
		JobBoard.ProjectChanged(__instance);
		JobBoard.WorkerChanged(labor);
	}
}

// A worker leaves a goal (done, failed or given up): the goal is free for others, and the worker looks afresh.
[Feature(Features.JobBoard, Features.JobBoardInfo)]
[HarmonyPatch(typeof(Goal), nameof(Goal.UnassignWorker))]
internal static class JobBoardGoalFreedPatch
{
	private static void Prefix(Goal __instance, Task task)
	{
		if (JobBoard.Mode == JobBoard.JobBoardMode.Off)
		{
			return;
		}
		Project project = __instance.project;
		if (project == null && __instance != null)
		{
			project = __instance.GetComponentInParent<Project>();
		}
		JobBoard.ProjectChanged(project);
		if (task != null)
		{
			JobBoard.WorkerChanged(task.Worker as Labor);
		}
	}
}

// A container's content changed (once per fixed update, as the game sends it on): a worker's bag is the worker's
// change, anything else (stockpiles, workshops, blueprints) the stockpiles'.
[Feature(Features.JobBoard, Features.JobBoardInfo)]
[HarmonyPatch(typeof(Recepteur), "FixedUpdate")]
internal static class JobBoardStorageChangedPatch
{
	private static void Prefix(Recepteur __instance)
	{
		if (!__instance._inventoryChanged || JobBoard.Mode == JobBoard.JobBoardMode.Off)
		{
			return;
		}
		Labor labor = __instance.GetComponent<Labor>();
		if (labor != null)
		{
			JobBoard.WorkerChanged(labor);
		}
		else
		{
			JobBoard.StorageChanged();
		}
	}
}

// The route graph was rebuilt for changed blocks: what can be reached may have changed.
[Feature(Features.JobBoard, Features.JobBoardInfo)]
[HarmonyPatch(typeof(TestPathfinding), "RefreshLod")]
internal static class JobBoardRoutesChangedPatch
{
	private static void Prefix()
	{
		JobBoard.RoutesChanged();
	}
}

// A pooled worker that is reused, or a destroyed one, has no memos.
[Feature(Features.JobBoard, Features.JobBoardInfo)]
[HarmonyPatch]
internal static class JobBoardForgetPatch
{
	private static IEnumerable<MethodBase> TargetMethods()
	{
		yield return AccessTools.Method(typeof(Labor), nameof(Labor.OnRequested));
		yield return AccessTools.Method(typeof(Labor), "OnDestroy");
	}

	private static void Prefix(Labor __instance)
	{
		JobBoard.Forget(__instance);
	}
}
