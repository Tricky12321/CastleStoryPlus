using System.Collections.Generic;
using BepInEx.Configuration;
using Brix.Components;
using Brix.Game;
using Brix.External.Factories;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Game.Semantique;
using Brix.Legacy;
using Brix.Lifecycle.Pooling;
using CastleStoryPlus.Core;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.WorkerAI;

// Failsafe for a build task that stops: a build task of a player with blueprints left and workers in its crew that
// has not finished a block for [BuildWatchdog] Seconds (60) is reset, at most once each period: every blueprint is
// tried again (the marks of blueprints judged unreachable are dropped), its workers' task and resource reservations
// are let go, and each worker of its crew stops what it does and looks for work afresh (what they carry stays in
// their hands). Each reset is written to the log. Host only.
[Feature(Features.StuckReset, Features.StuckResetInfo)]
internal sealed class BuildWatchdog : MonoBehaviour
{
	private const float CheckSeconds = 5f;

	private sealed class Watch
	{
		public int Goals;

		public float Since;
	}

	private static ConfigEntry<float> _seconds;

	private static readonly Dictionary<BuildProject, Watch> Watches = new Dictionary<BuildProject, Watch>();

	private readonly List<BuildProject> _projects = new List<BuildProject>();

	private readonly List<Labor> _crew = new List<Labor>();

	private float _next;

	private static void Enable()
	{
		_seconds = Plugin.Cfg.Bind("BuildWatchdog", "Seconds", 60f, new ConfigDescription("A build task with blueprints and workers that has not finished a block this long is reset: blueprints tried again, its workers look for work afresh (0: off).", new AcceptableValueRange<float>(0f, 600f)));
		GameSession.OnLeave(Watches.Clear);
		Plugin.Root.AddComponent<BuildWatchdog>();
	}

	private void Update()
	{
		if (!NetworkServer.active || _seconds.Value <= 0f || Time.time < _next)
		{
			return;
		}
		_next = Time.time + CheckSeconds;
		_projects.Clear();
		foreach (GameObject go in BrixSingleton<AutoList>.Instance.GetInstances(Groupes.Construction))
		{
			BuildProject project = (go != null) ? go.GetComponent<BuildProject>() : null;
			if (project != null && project.IsStillAlive && project.faction != null && !project.faction.isAI)
			{
				_projects.Add(project);
			}
		}
		foreach (BuildProject project in _projects)
		{
			try
			{
				Check(project);
			}
			catch (System.Exception ex)
			{
				Plugin.Log.LogWarning("BuildWatchdog: " + ex.Message);
				Watches.Remove(project);
			}
		}
		if (Watches.Count > _projects.Count)
		{
			List<BuildProject> gone = new List<BuildProject>();
			foreach (BuildProject project in Watches.Keys)
			{
				if (!_projects.Contains(project))
				{
					gone.Add(project);
				}
			}
			foreach (BuildProject project in gone)
			{
				Watches.Remove(project);
			}
		}
	}

	private void Check(BuildProject project)
	{
		int goals = project.GoalCounts();
		if (goals == 0 || project.CrewCount() == 0)
		{
			Watches.Remove(project);
			return;
		}
		// A block finished (or blueprints added or removed): the task is moving.
		if (!Watches.TryGetValue(project, out Watch watch) || watch.Goals != goals)
		{
			Watches[project] = new Watch { Goals = goals, Since = Time.time };
			return;
		}
		if (Time.time - watch.Since < _seconds.Value)
		{
			return;
		}
		watch.Since = Time.time;
		_crew.Clear();
		_crew.AddRange(project.Workforce);
		Plugin.Log.LogInfo("BuildWatchdog: build task " + project.name + " finished no block in " + Mathf.RoundToInt(_seconds.Value) + " s (" + goals + " left, " + _crew.Count + " workers): reset");
		UnreachableTasks.ForgetAll();
		foreach (Labor labor in _crew)
		{
			if (labor.IsNullOrReleased() || labor.Activity == Activity.Fighting)
			{
				continue;
			}
			TaskReservation.Release(labor);
			ResourceReservation.Release(labor);
			if (labor.CurrentTask != null && labor.Operable == null)
			{
				labor.AbortWorkSignal.Invoke(TaskResult.ProjectCancelledTask);
			}
		}
	}
}
