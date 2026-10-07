using System.Collections.Generic;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Game.Network;
using Brix.Lua;
using CastleStoryPlus.Core;
using HarmonyLib;
using MoonSharp.Interpreter;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.WorkerAI;

// How many bricktrons may work on a task at a time, for example a quarry with 4: a "Workers" button among the task's
// actions shows the limit ("All" when there is none) with small "+" and "-" over it. Once that many work on the task
// (its own crew, helpers from other tasks and free workers alike) no one else takes work from it, and any more than
// that (the limit lowered, or several taking it in the same moment) stop. The limit is kept in spare bits of the
// task's own state flags (ProjectFlags), so it is synced to multiplayer clients and saved with the task by the game
// itself; clients send the change to the host.
[Feature(Features.WorkLimit, Features.WorkLimitInfo)]
internal static class WorkLimit
{
	private const int Shift = 7;

	private const int Mask = 31;

	internal const int Most = Mask;

	// The step down from no limit.
	private const int FirstLimit = 8;

	private const int SetLimitHash = 1129595235;

	private const float CountSeconds = 0.5f;

	private static readonly Dictionary<Project, List<Labor>> Working = new Dictionary<Project, List<Labor>>();

	private static float _countedAt = -1f;

	private static void Enable()
	{
		NetworkBehaviour.RegisterCommandDelegate(typeof(UNetProjectCmd), SetLimitHash, InvokeSetLimit);
		GameSession.OnLeave(() =>
		{
			Working.Clear();
			_countedAt = -1f;
		});
		UI.StepButtons.Register(Features.WorkLimit);
		LuaInjection.AddFunction("WorkLimit", (ScriptExecutionContext context, CallbackArguments args) =>
		{
			Project project = (args.Count > 0 && !args[0].IsNil()) ? args.AsComponent<Project>(0, "WorkLimit", allowNil: true) : null;
			int limit = Get(project);
			return DynValue.NewString((project == null) ? string.Empty : ((limit == 0) ? "All" : limit.ToString()));
		});
		LuaInjection.AddFunction("WorkLimitStep", (ScriptExecutionContext context, CallbackArguments args) =>
		{
			Project project = (args.Count > 1 && !args[0].IsNil()) ? args.AsComponent<Project>(0, "WorkLimitStep", allowNil: true) : null;
			return DynValue.NewBoolean(Step(project, (args.Count > 1) ? (int)args[1].Number : 0));
		});
		// Among the task's actions, before pause and delete.
		LuaInjection.AddPatch(Features.WorkLimit, "LUI/Menus/GameMenu.lua", "---task.actions.delete\ndo\n", LuaInjection.Mode.InsertBefore, @"---task.actions.workLimit (Castle Story Plus)
do
local h = MenuHandle.New()
h.Visible = ||Data.Project:IsSelectedNotOfTypeIdle()
h.Label = ||""Workers at a time""
h.Icon = ||IconKeys._Bricktron:Get64()
h.IconSize = ||32
h.OnAction = function() end
h.Count = function() if Data.Project:IsSelectedNotOfTypeIdle() then return CastleStoryPlus.WorkLimit(Data.Project:GetSelected()) end return """" end
h.CountColor = ||CastleYellow
h.CountFontSize = ||14
h.ev_refresh = Event.New()
h.Step = function(direction) if not Data.Project:IsSelectedNotOfTypeIdle() then return false end return CastleStoryPlus.WorkLimitStep(Data.Project:GetSelected(), direction) end
h.StepChanged = h.ev_refresh

_m.onSetSelectedProject:AddListener(||h.ev_refresh:Invoke())
_m.sh.taskActions.ev_onLoad:AddListener(function(m, sh) m.AddMenuHandleToggle(h, sh) end)

_m.mg.projectContext:AddChild(h)
_m.mh.taskActionsWorkLimit = h
end

");
	}

	// The limit, 0 when there is none.
	internal static int Get(Project project)
	{
		return (project != null && project.State != null) ? (ProjectFlags.Get(project.State) >> Shift) & Mask : 0;
	}

	private static void Set(Project project, int limit)
	{
		if (project == null || project.State == null || project is IdleProject)
		{
			return;
		}
		int flags = ProjectFlags.Get(project.State);
		ProjectFlags.Set(project.State, (flags & ~(Mask << Shift)) | (Mathf.Clamp(limit, 0, Mask) << Shift));
		_countedAt = -1f;
		if (NetworkServer.active)
		{
			ReleaseExtraCrew(project);
		}
	}

	// Server: a crew larger than the limit is cut down to it, the rest go to the free workers (as the game's "remove
	// worker" does).
	private static void ReleaseExtraCrew(Project project)
	{
		int limit = Get(project);
		if (limit == 0)
		{
			return;
		}
		List<Labor> crew = new List<Labor>(project.Crew());
		for (int i = crew.Count - 1; i >= limit; i--)
		{
			crew[i].AssignToIdle();
			crew[i].Autonomy = AutonomyStatus.FreeAgent;
		}
	}

	// The crew size a task takes: the game's, or the limit when it is lower.
	internal static int Capacity(IOperable<Labor> operable, int max)
	{
		int limit = Get(operable as Project);
		if (limit == 0)
		{
			return max;
		}
		return (max == -1) ? limit : Mathf.Min(max, limit);
	}

	// All -> 8 -> 7 ... 1 going down; 1 ... 31 -> All going up.
	internal static int Next(int limit, int direction)
	{
		if (direction < 0)
		{
			return (limit == 0) ? FirstLimit : Mathf.Max(1, limit - 1);
		}
		if (direction > 0)
		{
			return (limit == 0 || limit >= Most) ? 0 : limit + 1;
		}
		return limit;
	}

	// Client or host: shown at once; the host does the same.
	private static bool Step(Project project, int direction)
	{
		if (project == null || project is IdleProject || User.LocalUser == null)
		{
			return false;
		}
		int limit = Get(project);
		int next = Next(limit, direction);
		if (next == limit)
		{
			return false;
		}
		Set(project, next);
		UNetProjectCmd cmd = User.LocalUser.GetComponent<UNetProjectCmd>();
		if (cmd == null || cmd.isServer)
		{
			return true;
		}
		NetworkWriter writer = new NetworkWriter();
		writer.Write((short)0);
		writer.Write((short)5);
		writer.WritePackedUInt32((uint)SetLimitHash);
		writer.Write(cmd.GetComponent<NetworkIdentity>().netId);
		writer.Write(project.gameObject);
		writer.WritePackedUInt32((uint)next);
		cmd.SendCommandInternal(writer, 0, "CmdSetWorkLimit");
		return true;
	}

	private static void InvokeSetLimit(NetworkBehaviour obj, NetworkReader reader)
	{
		if (!NetworkServer.active)
		{
			return;
		}
		GameObject go = reader.ReadGameObject();
		int limit = (int)reader.ReadPackedUInt32();
		if (go == null || !Affiliation.IsOwnedBy(go, obj.gameObject))
		{
			return;
		}
		Set(go.GetComponent<Project>(), limit);
	}

	// Server: whether one more may take work from the task; the labor already working on it always may.
	internal static bool HasRoom(Project project, Labor labor)
	{
		int limit = Get(project);
		if (limit == 0)
		{
			return true;
		}
		Count();
		if (!Working.TryGetValue(project, out List<Labor> working))
		{
			return true;
		}
		return working.Count < limit || working.Contains(labor);
	}

	// Who works on which limited task (their current or next task came from one of its goals), counted twice a
	// second; any above a task's limit stop, those not in its crew first.
	private static void Count()
	{
		if (_countedAt >= 0f && Time.time - _countedAt < CountSeconds)
		{
			return;
		}
		_countedAt = Time.time;
		Working.Clear();
		foreach (Labor labor in Live<Labor>.Active())
		{
			Project project = TaskOf(labor);
			if (project == null || Get(project) == 0)
			{
				continue;
			}
			if (!Working.TryGetValue(project, out List<Labor> working))
			{
				working = new List<Labor>();
				Working[project] = working;
			}
			working.Add(labor);
		}
		foreach (KeyValuePair<Project, List<Labor>> pair in Working)
		{
			int limit = Get(pair.Key);
			List<Labor> working = pair.Value;
			if (working.Count <= limit)
			{
				continue;
			}
			working.Sort((Labor a, Labor b) => ((a.Project == pair.Key) ? 0 : 1).CompareTo((b.Project == pair.Key) ? 0 : 1));
			for (int i = working.Count - 1; i >= limit; i--)
			{
				working[i].AbortWorkSignal.Invoke(TaskResult.ProjectCancelledTask);
				working.RemoveAt(i);
			}
		}
	}

	private static Project TaskOf(Labor labor)
	{
		GameObject giver = (labor.CurrentTask != null) ? labor.CurrentTask.Giver : null;
		if (giver == null && labor.PendingTask != null)
		{
			giver = labor.PendingTask.Giver;
		}
		return (giver != null) ? giver.GetComponentInParent<Project>() : null;
	}
}

// A task with as many working on it as its limit is left out of the tasks other workers look at.
[Feature(Features.WorkLimit, Features.WorkLimitInfo)]
[HarmonyPatch(typeof(Labor), nameof(Labor.RefreshAvailableProjects))]
internal static class WorkLimitAvailablePatch
{
	[HarmonyPriority(Priority.Last)]
	private static void Postfix(Labor __instance, List<IOperable<Labor>> projects)
	{
		if (projects == null || !NetworkServer.active)
		{
			return;
		}
		// A loop rather than RemoveAll with a lambda: this runs every time a worker looks for work, and the lambda
		// (it needs the worker) would be a new object each time.
		for (int i = projects.Count - 1; i >= 0; i--)
		{
			if (projects[i] is Project project && !WorkLimit.HasRoom(project, __instance))
			{
				projects.RemoveAt(i);
			}
		}
	}
}

// The task's marker shows its limit in place of the crew size ("x 0/4" rather than "x 0/8") while it has one.
[Feature(Features.WorkLimit, Features.WorkLimitInfo)]
[HarmonyPatch(typeof(BaliseOverheadDisplayDriver), "BuildString", new System.Type[0])]
internal static class WorkLimitMarkerPatch
{
	private static readonly Dictionary<int, string> Texts = new Dictionary<int, string>();

	private static void Postfix(BaliseOverheadDisplayDriver __instance, ref string __result)
	{
		Project project = (__instance.balise != null) ? __instance.balise.project : null;
		int limit = WorkLimit.Get(project);
		if (limit == 0)
		{
			return;
		}
		int key = project.CrewCount() * 64 + limit;
		if (!Texts.TryGetValue(key, out string text))
		{
			text = " x " + project.CrewCount() + "/" + limit;
			Texts[key] = text;
		}
		__result = text;
	}
}

// The task takes no more crew than its limit: full (and over full) count against the lower of the two.
[Feature(Features.WorkLimit, Features.WorkLimitInfo)]
[HarmonyPatch(typeof(WorkForceProvider), nameof(WorkForceProvider.IsFull))]
internal static class WorkLimitFullPatch
{
	private static void Postfix(IOperable<Labor> ____operable, int ____maxPopulation, ref bool __result)
	{
		int capacity = WorkLimit.Capacity(____operable, ____maxPopulation);
		if (capacity != ____maxPopulation && ____operable is Project project)
		{
			__result = project.CrewCount() >= capacity;
		}
	}
}

[Feature(Features.WorkLimit, Features.WorkLimitInfo)]
[HarmonyPatch(typeof(WorkForceProvider), nameof(WorkForceProvider.IsOverPopulated))]
internal static class WorkLimitOverPopulatedPatch
{
	private static void Postfix(IOperable<Labor> ____operable, int ____maxPopulation, ref bool __result)
	{
		int capacity = WorkLimit.Capacity(____operable, ____maxPopulation);
		if (capacity != ____maxPopulation && ____operable is Project project)
		{
			__result = project.CrewCount() > capacity;
		}
	}
}
