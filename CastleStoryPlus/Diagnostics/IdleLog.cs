using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx.Configuration;
using Brix.Game;
using Brix.Game.AI;
using CastleStoryPlus.Core;
using CastleStoryPlus.WorkerAI;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.Diagnostics;

// Always-on log of bricktrons that stand still: a player's worker with a job that has had no task for at least
// [Debug] IdleLogSeconds (15) gets a line in BepInEx/idle.log when it starts working again (and every minute while it
// still waits): how long, where, its job, the task it ended last (and how), and why it turned down tasks in the
// meantime (the game's decision errors, counted). Cheap enough to leave on, unlike the full WorkerTrace. Host only.
[Feature(Features.IdleLog, Features.IdleLogInfo)]
internal sealed class IdleLog : MonoBehaviour
{
	private const float CheckSeconds = 1f;

	private const float RepeatSeconds = 60f;

	private sealed class Wait
	{
		public float Since = -1f;

		public float Reported;

		public string LastTask;

		public readonly Dictionary<string, int> Rejects = new Dictionary<string, int>();
	}

	private static ConfigEntry<float> _minSeconds;

	private static StreamWriter _writer;

	private static readonly Dictionary<Labor, Wait> Waits = new Dictionary<Labor, Wait>();

	private readonly List<Labor> _labors = new List<Labor>();

	private float _next;

	private static void Enable()
	{
		_minSeconds = Plugin.Cfg.Bind("Debug", "IdleLogSeconds", 15f, new ConfigDescription("A worker with a job that has had no task this long is written to BepInEx/idle.log, with why (0: off).", new AcceptableValueRange<float>(0f, 600f)));
		GameSession.OnLeave(Waits.Clear);
		Plugin.Root.AddComponent<IdleLog>();
	}

	private static Wait WaitOf(Labor labor)
	{
		if (!Waits.TryGetValue(labor, out Wait wait))
		{
			wait = new Wait();
			Waits[labor] = wait;
		}
		return wait;
	}

	private static bool Watched(Labor labor)
	{
		if (labor == null || labor.Project == null || labor.Project is IdleProject)
		{
			return false;
		}
		Faction faction = Faction.GetFaction(labor.gameObject);
		return faction != null && !faction.isAI;
	}

	private static bool Waiting(Labor labor)
	{
		return labor.CurrentTask == null && labor.PendingTask == null && labor.InstructionQueue.Count == 0 && labor.Activity != Activity.Fighting;
	}

	private void Update()
	{
		if (!NetworkServer.active || _minSeconds.Value <= 0f || Time.time < _next)
		{
			return;
		}
		_next = Time.time + CheckSeconds;
		Live<Labor>.Active(_labors);
		foreach (Labor labor in _labors)
		{
			if (!Watched(labor))
			{
				Waits.Remove(labor);
				continue;
			}
			Wait wait = WaitOf(labor);
			if (Waiting(labor))
			{
				if (wait.Since < 0f)
				{
					wait.Since = Time.time;
					wait.Reported = Time.time;
					wait.Rejects.Clear();
				}
				else if (Time.time - wait.Since >= _minSeconds.Value && Time.time - wait.Reported >= RepeatSeconds)
				{
					wait.Reported = Time.time;
					Write(labor, wait, "still waiting");
				}
				continue;
			}
			if (wait.Since >= 0f && Time.time - wait.Since >= _minSeconds.Value)
			{
				Write(labor, wait, "waited, now " + (labor.CurrentTask != null ? labor.CurrentTask.Description : (labor.PendingTask != null ? labor.PendingTask.Description : "busy")));
			}
			wait.Since = -1f;
		}
	}

	private static void Open()
	{
		if (_writer == null)
		{
			_writer = new StreamWriter(System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "idle.log"), append: false) { AutoFlush = true };
			_writer.WriteLine("---- idle log started " + System.DateTime.Now.ToString("dd-MM-yyyy HH:mm:ss"));
		}
	}

	// A line from another feature (BaseArea: a worker gone out of the base area of its own accord) in the same file,
	// with the time in front.
	internal static void Line(string text)
	{
		Open();
		_writer.WriteLine(System.DateTime.Now.ToString("HH:mm:ss") + " game " + Time.time.ToString("0") + " s | " + text);
	}

	private static void Write(Labor labor, Wait wait, string what)
	{
		Open();
		Vector3 at = labor.transform.position;
		StringBuilder text = new StringBuilder();
		text.Append(System.DateTime.Now.ToString("HH:mm:ss")).Append(" game ").Append(Time.time.ToString("0")).Append(" s | ")
			.Append(labor.name).Append(' ').Append(what)
			.Append(" | idle ").Append((Time.time - wait.Since).ToString("0.0")).Append(" s")
			.Append(" | at ").Append(Mathf.FloorToInt(at.x)).Append(',').Append(Mathf.FloorToInt(at.y)).Append(',').Append(Mathf.FloorToInt(at.z))
			.Append(" | job ").Append(labor.Project.GetType().Name).Append(' ').Append(labor.Project.name)
			.Append(" | activity ").Append(labor.Activity)
			.Append(" | last task ").Append(wait.LastTask ?? "-");
		if (wait.Rejects.Count > 0)
		{
			text.Append(" | rejected:");
			foreach (KeyValuePair<string, int> reject in wait.Rejects)
			{
				text.Append(' ').Append(reject.Key).Append(" x").Append(reject.Value).Append(';');
			}
		}
		_writer.WriteLine(text.ToString());
	}

	[Feature(Features.IdleLog, Features.IdleLogInfo)]
	[HarmonyPatch(typeof(Task), "OnFinally")]
	private static class TaskEndPatch
	{
		private static void Prefix(Task __instance)
		{
			if (__instance.Worker is Labor labor && Waits.TryGetValue(labor, out Wait wait))
			{
				float seconds = (__instance.TaskStartedTimestamp > 0f) ? (Time.time - __instance.TaskStartedTimestamp) : 0f;
				wait.LastTask = (string.IsNullOrEmpty(__instance.Description) ? "(task)" : __instance.Description) + " " + __instance.result
					+ ((__instance.Exception != null) ? (" " + __instance.Exception.GetType().Name) : string.Empty)
					+ " after " + seconds.ToString("0.0") + " s";
			}
		}
	}

	[Feature(Features.IdleLog, Features.IdleLogInfo)]
	[HarmonyPatch(typeof(AIErrorsStore), nameof(AIErrorsStore.AddDecisionError))]
	private static class RejectPatch
	{
		private static void Prefix(AIError error, Labor labor)
		{
			if (labor == null || !Waits.TryGetValue(labor, out Wait wait) || wait.Since < 0f || wait.Rejects.Count > 20)
			{
				return;
			}
			string key = (error != null) ? error.ToString() : "?";
			Goal goal = GoalWorkers.Evaluating;
			if (goal != null)
			{
				key += " on " + goal.GetType().Name;
			}
			wait.Rejects.TryGetValue(key, out int count);
			wait.Rejects[key] = count + 1;
		}
	}
}
