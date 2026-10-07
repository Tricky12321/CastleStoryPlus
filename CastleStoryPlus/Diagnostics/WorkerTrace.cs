using System.Collections.Generic;
using System.IO;
using Brix.Game.AI;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.Diagnostics;

// Modding aid, off by default ([Debug] WorkerTrace, or the debug menu on F8 while playing): logs every state change
// of every worker (activity, current task, availability, instruction queue, moving) with timestamps to
// BepInEx/workertrace.log, to find where workers lose time between tasks. WorkerTraceDetails adds why: rejected
// tasks, ended tasks, failed paths, failed searches for materials, and idle workers while there is work.
[Feature]
internal class WorkerTrace : MonoBehaviour
{
	private const float RefreshSeconds = 1f;

	private static StreamWriter _writer;

	private static readonly Dictionary<Labor, string> _states = new Dictionary<Labor, string>();

	// What the logged state line is made of, compared every frame; the line itself is only built when it changes.
	private struct Snapshot
	{
		public Activity Activity;

		public object Task;

		public object Pending;

		public bool Available;

		public bool Delay;

		public int Queue;

		public bool Moving;

		public object Project;

		public bool Same(Snapshot other)
		{
			return Activity == other.Activity && Task == other.Task && Pending == other.Pending && Available == other.Available && Delay == other.Delay && Queue == other.Queue && Moving == other.Moving && Project == other.Project;
		}
	}

	private static readonly Dictionary<Labor, Snapshot> _snapshots = new Dictionary<Labor, Snapshot>();

	private readonly List<Labor> _labors = new List<Labor>();

	private float _nextRefresh;

	private static bool _started;

	internal static bool Active => _writer != null;

	private static void Enable()
	{
		GameSession.OnLeave(() =>
		{
			_states.Clear();
			_snapshots.Clear();
		});
		Plugin.Root.AddComponent<WorkerTrace>();
		if (Plugin.Cfg.Bind("Debug", "WorkerTrace", false, "Log every worker state change, and why workers reject or fail tasks, to BepInEx/workertrace.log (modding aid; can also be switched on in the debug menu, F8).").Value)
		{
			Start();
		}
	}

	// The first start of a session starts a new file; later starts add to it.
	internal static void Start()
	{
		if (_writer != null)
		{
			return;
		}
		_writer = new StreamWriter(System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "workertrace.log"), append: _started) { AutoFlush = true };
		_started = true;
		_states.Clear();
		_writer.WriteLine("---- trace started " + System.DateTime.Now.ToString("dd-MM-yyyy HH:mm:ss"));
	}

	internal static void Stop()
	{
		if (_writer == null)
		{
			return;
		}
		_writer.WriteLine("---- trace stopped " + System.DateTime.Now.ToString("dd-MM-yyyy HH:mm:ss"));
		_writer.Dispose();
		_writer = null;
	}

	internal static void Log(Labor labor, string message)
	{
		Log(labor != null ? labor.name : "?", message);
	}

	internal static void Log(string who, string message)
	{
		_writer?.WriteLine(Time.time.ToString("0.000") + " f" + Time.frameCount + " " + who + ": " + message);
	}

	private void Update()
	{
		if (!Active)
		{
			return;
		}
		WorkerTraceDetails.Update();
		if (Time.unscaledTime >= _nextRefresh)
		{
			_nextRefresh = Time.unscaledTime + RefreshSeconds;
			Live<Labor>.Active(_labors);
		}
		foreach (Labor labor in _labors)
		{
			if (labor == null)
			{
				continue;
			}
			Snapshot snapshot = SnapshotOf(labor);
			if (_snapshots.TryGetValue(labor, out Snapshot was) && was.Same(snapshot))
			{
				continue;
			}
			_snapshots[labor] = snapshot;
			string state = StateOf(labor);
			if (!_states.TryGetValue(labor, out string old) || old != state)
			{
				_states[labor] = state;
				Log(labor, state);
			}
		}
	}

	private static Snapshot SnapshotOf(Labor labor)
	{
		Locomotion4 locomotion = labor.navigation != null ? labor.navigation.Locomotion : null;
		return new Snapshot
		{
			Activity = labor.Activity,
			Task = labor.CurrentTask,
			Pending = labor.PendingTask,
			Available = labor.IsAvailable(),
			Delay = labor.DelayBeforeAvailable > 0f,
			Queue = labor.InstructionQueue.Count,
			Moving = !(locomotion == null || locomotion.IsInPlace()),
			Project = labor.Project
		};
	}

	private static string StateOf(Labor labor)
	{
		Locomotion4 locomotion = labor.navigation != null ? labor.navigation.Locomotion : null;
		return labor.Activity
			+ " | task=" + (labor.CurrentTask != null ? labor.CurrentTask.Description : "-")
			+ (labor.PendingTask != null ? " pending=" + labor.PendingTask.Description : "")
			+ " | " + (labor.IsAvailable() ? "available" : "busy")
			+ (labor.DelayBeforeAvailable > 0f ? " delay" : "")
			+ " | queue=" + labor.InstructionQueue.Count
			+ " | " + (locomotion == null || locomotion.IsInPlace() ? "still" : "moving")
			+ " | project=" + (labor.Project != null ? labor.Project.GetType().Name : "-");
	}
}

[Feature]
[HarmonyPatch(typeof(Goal), nameof(Goal.EndWork))]
internal static class WorkerTraceEndWorkPatch
{
	private static void Prefix(Goal __instance, Task work)
	{
		if (WorkerTrace.Active)
		{
			WorkerTrace.Log(work?.Worker as Labor, ">> EndWork " + __instance.GetType().Name + " result=" + work?.result);
		}
	}
}
