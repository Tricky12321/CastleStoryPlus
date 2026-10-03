using System.Collections.Generic;
using System.IO;
using Brix.Game.AI;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.Diagnostics;

// Modding aid, off by default ([Debug] WorkerTrace): logs every state change of every worker (activity,
// current task, availability, instruction queue, moving) with timestamps to BepInEx/workertrace.log, to
// find where workers lose time between tasks.
[Feature]
internal class WorkerTrace : MonoBehaviour
{
	private const float RefreshSeconds = 1f;

	private static StreamWriter _writer;

	private readonly Dictionary<Labor, string> _states = new Dictionary<Labor, string>();

	private Labor[] _labors = new Labor[0];

	private float _nextRefresh;

	internal static bool Active => _writer != null;

	private static void Enable()
	{
		if (!Plugin.Cfg.Bind("Debug", "WorkerTrace", false, "Log every worker state change to BepInEx/workertrace.log (modding aid).").Value)
		{
			return;
		}
		_writer = new StreamWriter(System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "workertrace.log"), append: false) { AutoFlush = true };
		Plugin.Root.AddComponent<WorkerTrace>();
	}

	internal static void Log(Labor labor, string message)
	{
		_writer?.WriteLine(Time.time.ToString("0.000") + " f" + Time.frameCount + " " + (labor != null ? labor.name : "?") + ": " + message);
	}

	private void Update()
	{
		if (Time.unscaledTime >= _nextRefresh)
		{
			_nextRefresh = Time.unscaledTime + RefreshSeconds;
			_labors = FindObjectsOfType<Labor>();
		}
		foreach (Labor labor in _labors)
		{
			if (labor == null)
			{
				continue;
			}
			string state = StateOf(labor);
			if (!_states.TryGetValue(labor, out string old) || old != state)
			{
				_states[labor] = state;
				Log(labor, state);
			}
		}
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
