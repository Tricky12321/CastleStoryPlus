using System.Collections.Generic;
using System.Text;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Game.Network;
using Brix.UI;
using Brix.Utils;
using CastleStoryPlus.Core;
using CastleStoryPlus.UI;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

namespace CastleStoryPlus.WorkerAI;

// Selected bricktrons put on a task (a right-click on the task, in the task bar or on its area) start on it at once:
// the game only moved them into its crew and let them finish what they were doing (sitting, a stroll, a walk to
// another task), and a bricktron already in the crew was not touched at all. Now what they do is broken off (when
// it may be) so they look for work in the task right away. A bricktron that has not started on the task within a
// few seconds is reported on screen, with the reason: the task cannot take it (its job, the crew limit), the task has
// nothing to do or is paused, or why it turned the task's work down (cannot reach it, no room in storage, nothing to
// fetch...) as the game's own decision errors say. Host only; the player whose bricktrons they are sees the report.
[Feature(Features.AssignNow, Features.AssignNowInfo)]
internal sealed class AssignNow : MonoBehaviour
{
	private const float CheckSeconds = 0.25f;

	// Game seconds a bricktron has to start on the task before it is reported.
	private const float StartSeconds = 4f;

	private const float BannerSeconds = 5f;

	private static readonly Color BannerColor = new Color(1f, 0.55f, 0.3f, 1f);

	private sealed class Watch
	{
		public Project Project;

		public float Since;

		// The order that stores what the bag holds first; the few seconds to start count from its end.
		public Task Store;

		// The game's reasons for turning the task's work down, by text, counted.
		public readonly Dictionary<string, int> Rejects = new Dictionary<string, int>();
	}

	private static readonly Dictionary<Labor, Watch> Watches = new Dictionary<Labor, Watch>();

	private static readonly List<Labor> Done = new List<Labor>();

	// Reports of this check, by task and reason: the bricktrons' names.
	private static readonly Dictionary<string, List<string>> Reports = new Dictionary<string, List<string>>();

	private static AssignNow _instance;

	private float _next;

	private GameObject _banner;

	private Text _text;

	private float _bannerUntil;

	private static void Enable()
	{
		GameSession.OnLeave(Watches.Clear);
		_instance = Plugin.Root.AddComponent<AssignNow>();
	}

	// Server: the bricktron is in the task's crew; it starts on it now.
	internal static void Begin(Labor labor, Project project)
	{
		if (!NetworkServer.active || labor.IsNullOrReleased() || project == null || project is IdleProject || labor.faction == null || labor.faction.isAI)
		{
			return;
		}
		Task current = labor.CurrentTask;
		bool switching = !WorksOn(labor, project);
		if (current != null && !From(current, project) && !TaskPrediction.LetGoUnstarted(labor, "moved to another task") && current.CanInterrupt)
		{
			labor.AbortWorkSignal.Invoke(TaskResult.ProjectCancelledTask);
		}
		Task store = switching ? StoreBagFirst(labor, project) : null;
		Watches[labor] = new Watch { Project = project, Since = Time.time, Store = store };
	}

	// What the old task left in the bag (stone from a quarry, ore from a tunnel) goes to the nearest storage that takes
	// it before the bricktron starts on the new task (issue #75: it went back to its old task and dug on until the bag
	// was full; a part-full bag also has the new task's digging and harvesting turned down). Without a storage that
	// takes it the bag is kept.
	private static Task StoreBagFirst(Labor labor, Project project)
	{
		if (labor.recepteur == null || labor.recepteur.IsEmpty())
		{
			return null;
		}
		if (labor.BestRecepteurToStoreInventory() == null)
		{
			Plugin.Log.LogInfo("AssignNow: " + labor.name + " keeps its bag (" + labor.recepteur.ContentString() + "), no storage takes it");
			return null;
		}
		Plugin.Log.LogInfo("AssignNow: " + labor.name + " stores its bag (" + labor.recepteur.ContentString() + ") before " + project.name);
		Order.Transfer.GoStore.Send(labor, Labor.DecisionSource.Project);
		return labor.PendingTask;
	}

	// Whether the bricktron was put on a task a moment ago and has not started on it yet.
	internal static bool IsStarting(Labor labor)
	{
		return Watches.Count != 0 && Watches.ContainsKey(labor);
	}

	// Server: the task would not take the bricktron.
	internal static void Refused(Labor labor, Project project)
	{
		if (labor.IsNullOrReleased() || project == null || labor.faction == null || labor.faction.isAI)
		{
			return;
		}
		project.CanHire(labor, out Thought thought);
		string reason = (thought != null) ? Text(thought.GetLabel()) : "the task cannot take it";
		Add(labor, project, reason);
		Flush();
	}

	internal static void Reject(Labor labor, AIError error)
	{
		if (labor == null || error == null || error.thought == null || !Watches.TryGetValue(labor, out Watch watch))
		{
			return;
		}
		Goal goal = GoalWorkers.Evaluating;
		if (goal != null && goal.project != watch.Project)
		{
			return;
		}
		string reason = Text(error.thought.GetLabel());
		watch.Rejects.TryGetValue(reason, out int count);
		watch.Rejects[reason] = count + 1;
	}

	private void Update()
	{
		UpdateBanner();
		if (Watches.Count == 0 || Time.unscaledTime < _next)
		{
			return;
		}
		_next = Time.unscaledTime + CheckSeconds;
		Done.Clear();
		foreach (KeyValuePair<Labor, Watch> pair in Watches)
		{
			Labor labor = pair.Key;
			Watch watch = pair.Value;
			if (labor.IsNullOrReleased() || watch.Project == null || !watch.Project.IsStillAlive || labor.Project != watch.Project || WorksOn(labor, watch.Project))
			{
				Done.Add(labor);
				continue;
			}
			if (watch.Store != null)
			{
				if (labor.CurrentTask == watch.Store || labor.PendingTask == watch.Store)
				{
					watch.Since = Time.time;
					continue;
				}
				watch.Store = null;
				watch.Since = Time.time;
			}
			if (Time.time - watch.Since >= StartSeconds)
			{
				Add(labor, watch.Project, Reason(labor, watch));
				Done.Add(labor);
			}
		}
		foreach (Labor labor in Done)
		{
			Watches.Remove(labor);
		}
		Flush();
	}

	private static string Reason(Labor labor, Watch watch)
	{
		Project project = watch.Project;
		if (PauseTasks.IsPaused(project))
		{
			return "the task is paused";
		}
		if (!project.HasGoals())
		{
			return "the task has nothing to do right now";
		}
		string best = null;
		int most = 0;
		foreach (KeyValuePair<string, int> reject in watch.Rejects)
		{
			if (reject.Value > most)
			{
				best = reject.Key;
				most = reject.Value;
			}
		}
		if (best != null)
		{
			return best;
		}
		Task current = labor.CurrentTask;
		if (current != null && !current.CanInterrupt)
		{
			return "it is still busy with " + (string.IsNullOrEmpty(current.Description) ? "another task" : current.Description);
		}
		return "it found nothing in the task it could do (see the problems log, F9)";
	}

	private static void Add(Labor labor, Project project, string reason)
	{
		string key = TaskName(project) + "\n" + reason;
		if (!Reports.TryGetValue(key, out List<string> names))
		{
			names = new List<string>();
			Reports[key] = names;
		}
		names.Add(labor.name);
		Diagnostics.SystemLog.AddProblem(Diagnostics.SystemLog.Severity.Warning, "Bricktron", labor.name + " can't start " + TaskName(project) + ": " + reason, null);
		Plugin.Log.LogInfo("AssignNow: " + labor.name + " can't start " + project.name + ": " + reason);
	}

	// One line per task and reason, shown to the player whose bricktrons they are.
	private static void Flush()
	{
		if (Reports.Count == 0)
		{
			return;
		}
		StringBuilder text = new StringBuilder();
		foreach (KeyValuePair<string, List<string>> report in Reports)
		{
			string[] parts = report.Key.Split('\n');
			List<string> names = report.Value;
			if (text.Length > 0)
			{
				text.Append('\n');
			}
			text.Append((names.Count == 1) ? names[0] : (names.Count + " bricktrons (" + string.Join(", ", names.ToArray()) + ")"))
				.Append(" can't start ").Append(parts[0]).Append(": ").Append(parts[1]);
		}
		Reports.Clear();
		if (_instance != null)
		{
			_instance.Show(text.ToString());
		}
	}

	// Whether the bricktron's task (or the one it is about to start) comes from the task.
	private static bool WorksOn(Labor labor, Project project)
	{
		return From(labor.CurrentTask, project) || From(labor.PendingTask, project);
	}

	private static bool From(Task task, Project project)
	{
		GameObject giver = (task != null) ? task.Giver : null;
		if (giver == null)
		{
			return false;
		}
		if (giver == project.gameObject)
		{
			return true;
		}
		Goal goal = giver.GetComponent<Goal>();
		return goal != null && goal.project == project;
	}

	private static string TaskName(Project project)
	{
		string name = project.name;
		int bracket = name.IndexOf(" (", System.StringComparison.Ordinal);
		return (bracket > 0) ? name.Substring(0, bracket) : name;
	}

	// A thought's text: the game's ##key in the player's language.
	private static string Text(string label)
	{
		if (string.IsNullOrEmpty(label))
		{
			return "?";
		}
		return label.StartsWith("##", System.StringComparison.Ordinal) ? I2Helper.TryGet(label) : label;
	}

	private void Show(string message)
	{
		if (User.LocalUser == null)
		{
			return;
		}
		EnsureBanner();
		_text.text = message;
		_bannerUntil = Time.unscaledTime + BannerSeconds;
		_banner.SetActive(true);
		MenuSound.PlaySound(MenuSound.Prefix.Other, MenuSound.Suffix.Negative);
	}

	private void UpdateBanner()
	{
		if (_banner == null || !_banner.activeSelf)
		{
			return;
		}
		float left = _bannerUntil - Time.unscaledTime;
		if (left <= 0f)
		{
			_banner.SetActive(false);
			return;
		}
		Color color = _text.color;
		color.a = Mathf.Clamp01(left / 0.5f);
		_text.color = color;
	}

	private void EnsureBanner()
	{
		if (_banner != null)
		{
			_text.color = BannerColor;
			return;
		}
		GameObject canvas = UiKit.CreateCanvas("AssignNowCanvas", transform, 950);
		RectTransform rect = UiKit.CreateRect("Banner", canvas.transform);
		UiKit.SetRect(rect, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -290f), new Vector2(1400f, 120f));
		_banner = rect.gameObject;
		_text = UiKit.CreateText(rect, string.Empty, 26, BannerColor, TextAnchor.MiddleCenter);
		_text.fontStyle = FontStyle.Bold;
		UiKit.SetRect(_text.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
		Outline outline = _text.gameObject.AddComponent<Outline>();
		outline.effectColor = new Color(0f, 0f, 0f, 0.9f);
		outline.effectDistance = new Vector2(2f, -2f);
		_banner.SetActive(false);
	}
}

// Server: a bricktron moved into a task's crew starts on it now; one the task would not take is reported.
[Feature(Features.AssignNow, Features.AssignNowInfo)]
[HarmonyPatch(typeof(UNetProjectCmd), nameof(UNetProjectCmd.CmdChangeProject))]
internal static class AssignNowChangePatch
{
	private static void Postfix(GameObject laborGO, GameObject projectGO)
	{
		Labor labor = (laborGO != null) ? laborGO.GetComponent<Labor>() : null;
		Project project = (projectGO != null) ? projectGO.GetComponent<Project>() : null;
		if (labor.IsNullOrReleased() || project == null || project is IdleProject)
		{
			return;
		}
		if (labor.Project == project)
		{
			AssignNow.Begin(labor, project);
		}
		else
		{
			AssignNow.Refused(labor, project);
		}
	}
}

// A bricktron already in the task's crew: the game did nothing; it starts on the task now (host).
[Feature(Features.AssignNow, Features.AssignNowInfo)]
[HarmonyPatch(typeof(UIGameActor), nameof(UIGameActor.AssignCharacterToProject))]
internal static class AssignNowCrewPatch
{
	private static void Prefix(Project project, Labor labor)
	{
		if (project != null && !labor.IsNullOrReleased() && project.Crew().Contains(labor))
		{
			AssignNow.Begin(labor, project);
		}
	}
}

// The game's reasons for turning work down, while a bricktron is watched.
[Feature(Features.AssignNow, Features.AssignNowInfo)]
[HarmonyPatch(typeof(AIErrorsStore), nameof(AIErrorsStore.AddDecisionError))]
internal static class AssignNowRejectPatch
{
	private static void Prefix(AIError error, Labor labor)
	{
		AssignNow.Reject(labor, error);
	}
}
