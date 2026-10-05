using System;
using System.Collections.Generic;
using System.Text;
using Brix.Components;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.AI.KnowledgeSpace;
using Brix.Game.AI.Nodes;
using Brix.Game.Components;
using Brix.Game.Semantique;
using Brix.Lifecycle.Pooling;
using CastleStoryPlus.Core;
using CastleStoryPlus.WorkerAI;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.Diagnostics;

// What the worker trace (WorkerTrace) adds to explain idle workers, all only while the trace runs:
// - "rejects": why a worker turned tasks down while looking for work (the game's decision errors: too crowded,
//   cannot reach, bag full, no storage...), summed per worker and written every few seconds;
// - "task end": every task that ends, with its result, the error and how long it ran;
// - "path failed": every failed path, with its destination;
// - "find failed": every failed search for something to pick up, with what was wanted, how much the stockpiles
//   hold, whether something could be found without the other workers' reservations, and what the worker carries;
// - "idle while work": every few seconds, the workers standing idle while their faction's projects have tasks.
internal static class WorkerTraceDetails
{
	private const float RejectFlushSeconds = 5f;

	private const float IdleReportSeconds = 10f;

	private static readonly Dictionary<Labor, Dictionary<string, int>> Rejects = new Dictionary<Labor, Dictionary<string, int>>();

	private static readonly Dictionary<Labor, string> LastRejects = new Dictionary<Labor, string>();

	private static readonly List<Goal> Goals = new List<Goal>();

	private static readonly List<IOperable<Labor>> Projects = new List<IOperable<Labor>>();

	private static float _nextFlush;

	private static float _nextIdleReport;

	// What the running FindAndPickUp looks for.
	[ThreadStatic]
	internal static Description Wanted;

	internal static void Reject(Labor labor, AIError error)
	{
		if (labor == null)
		{
			return;
		}
		if (!Rejects.TryGetValue(labor, out Dictionary<string, int> counts))
		{
			counts = new Dictionary<string, int>();
			Rejects[labor] = counts;
		}
		string key = (error != null) ? error.ToString() : "?";
		counts.TryGetValue(key, out int count);
		counts[key] = count + 1;
	}

	internal static void Update()
	{
		if (Time.unscaledTime >= _nextFlush)
		{
			_nextFlush = Time.unscaledTime + RejectFlushSeconds;
			FlushRejects();
		}
		if (Time.unscaledTime >= _nextIdleReport)
		{
			_nextIdleReport = Time.unscaledTime + IdleReportSeconds;
			ReportIdle();
		}
	}

	private static void FlushRejects()
	{
		foreach (KeyValuePair<Labor, Dictionary<string, int>> pair in Rejects)
		{
			if (pair.Key == null || pair.Value.Count == 0)
			{
				continue;
			}
			StringBuilder text = new StringBuilder();
			foreach (KeyValuePair<string, int> reason in pair.Value)
			{
				if (text.Length > 0)
				{
					text.Append(", ");
				}
				text.Append(reason.Key).Append(" x").Append(reason.Value);
			}
			LastRejects[pair.Key] = text.ToString();
			WorkerTrace.Log(pair.Key, "rejects: " + text);
		}
		Rejects.Clear();
	}

	// Workers of the player's faction standing idle while its projects have tasks.
	private static void ReportIdle()
	{
		Faction faction = (User.LocalUser != null) ? User.LocalUser.faction : null;
		if (faction == null)
		{
			return;
		}
		List<string> idle = new List<string>();
		foreach (Labor labor in UnityEngine.Object.FindObjectsOfType<Labor>())
		{
			if (labor == null || !faction.IsSame(labor.gameObject) || (labor.Activity != Activity.Idle && labor.Activity != Activity.Chilling))
			{
				continue;
			}
			Task task = labor.CurrentTask;
			bool idleTask = task == null || task.Giver == null || task.Giver.GetComponent<Goal>() == null;
			if (!idleTask)
			{
				continue;
			}
			string project = (labor.Project != null) ? labor.Project.GetType().Name : "-";
			LastRejects.TryGetValue(labor, out string rejects);
			idle.Add(labor.name + " (" + project + (labor.recepteur != null && !labor.recepteur.IsEmpty() ? ", carries " + Describe(labor.recepteur.ContentDescription) : string.Empty) + (string.IsNullOrEmpty(rejects) ? string.Empty : ", last rejects: " + rejects) + ")");
		}
		if (idle.Count == 0)
		{
			return;
		}
		List<string> work = new List<string>();
		Projects.Clear();
		ProjectDatabase.For(faction).Projects().AddTo(Projects);
		foreach (IOperable<Labor> operable in Projects)
		{
			Project project = operable as Project;
			if (project == null || project is IdleProject)
			{
				continue;
			}
			Goals.Clear();
			foreach (IGoalProvider provider in project.GetGoalProviders())
			{
				provider?.GetAll(Goals);
			}
			int open = 0;
			foreach (Goal goal in Goals)
			{
				if (!goal.IsNullOrReleased() && goal.IsValid() && goal.AssignedWorkerCount() == 0)
				{
					open++;
				}
			}
			if (Goals.Count > 0)
			{
				work.Add(project.GetType().Name + " " + open + "/" + Goals.Count + " open, " + project.CrewCount() + " crew");
			}
		}
		if (work.Count == 0)
		{
			return;
		}
		WorkerTrace.Log((Labor)null, "idle while work: " + string.Join("; ", idle.ToArray()) + " | projects: " + string.Join("; ", work.ToArray()));
	}

	// Why a search for something to pick up found nothing.
	internal static void FindFailed(Labor labor)
	{
		Description wanted = Wanted;
		StringBuilder text = new StringBuilder("find failed");
		if (wanted == null)
		{
			WorkerTrace.Log(labor, text.Append(" (wanted: unknown)").ToString());
			return;
		}
		foreach (Ressource resource in wanted.GetRessources().ToList())
		{
			if (resource == null || resource.quantifiable.valeur == 0)
			{
				continue;
			}
			int stock = Stock(labor.faction, resource);
			IGameComponent withReservations = Knowledge.Instance.ClosestObjectToPickup(labor, resource, labor.faction);
			ResourceReservation.Bypass = true;
			IGameComponent without;
			try
			{
				without = Knowledge.Instance.ClosestObjectToPickup(labor, resource, labor.faction);
			}
			finally
			{
				ResourceReservation.Bypass = false;
			}
			text.Append(" | ").Append(resource.GetType().Name).Append(" x").Append(resource.quantifiable.valeur)
				.Append(": stock ").Append(stock)
				.Append(", reachable source ").Append(Name(withReservations));
			if (withReservations == null && without != null)
			{
				text.Append(" (blocked by reservations: ").Append(Name(without)).Append(')');
			}
			if (without != null)
			{
				GameObject item = without.GameObject;
				Recepteur source = without.recepteur;
				GameObject first = (source != null && source.FirstObjectOf(resource, out GameObject stored)) ? stored : item;
				if (first != null && labor.recepteur != null && !labor.recepteur.HasRoomFor(first))
				{
					text.Append(" (no room in the bag for it)");
				}
			}
		}
		text.Append(" | bag ").Append((labor.recepteur == null || labor.recepteur.IsEmpty()) ? "empty" : Describe(labor.recepteur.ContentDescription));
		WorkerTrace.Log(labor, text.ToString());
	}

	// "RawIron=10, OrangeCrystal=2" (the game's own content text says "Fill" for everything carried in a bag).
	internal static string Describe(Description description)
	{
		if (description == null)
		{
			return "?";
		}
		List<string> parts = new List<string>();
		foreach (Ressource resource in description.GetRessources().ToList())
		{
			if (resource != null && resource.quantifiable != null && resource.quantifiable.valeur != 0)
			{
				parts.Add(resource.GetType().Name + "=" + resource.quantifiable.valeur);
			}
		}
		return (parts.Count == 0) ? "nothing" : string.Join(", ", parts.ToArray());
	}

	private static string Name(IGameComponent component)
	{
		return (component == null || component.GameObject == null) ? "none" : component.GameObject.name;
	}

	private static int Stock(Faction faction, Ressource resource)
	{
		int count = 0;
		HashSet<GameObject> stockpiles = BrixSingleton<AutoList>.Instance?.GetInstances(ObjetsDynamiques.Palette);
		if (stockpiles == null)
		{
			return 0;
		}
		foreach (GameObject go in stockpiles)
		{
			GameComponent component = (go != null) ? go.GetComponent<GameComponent>() : null;
			if (component == null || component.faction != faction || component.recepteur == null)
			{
				continue;
			}
			count += component.recepteur.ContentDescription.Value(resource);
		}
		return count;
	}
}

[Feature]
[HarmonyPatch(typeof(AIErrorsStore), nameof(AIErrorsStore.AddDecisionError))]
internal static class WorkerTraceRejectPatch
{
	private static void Prefix(AIError error, Labor labor)
	{
		if (WorkerTrace.Active)
		{
			WorkerTraceDetails.Reject(labor, error);
		}
	}
}

[Feature]
[HarmonyPatch(typeof(Task), "OnFinally")]
internal static class WorkerTraceTaskEndPatch
{
	private static void Prefix(Task __instance)
	{
		if (!WorkerTrace.Active)
		{
			return;
		}
		BaseLabor worker = __instance.Worker;
		float seconds = (__instance.TaskStartedTimestamp > 0f) ? (Time.time - __instance.TaskStartedTimestamp) : 0f;
		string error = (__instance.Exception != null) ? (" error=" + __instance.Exception.GetType().Name) : string.Empty;
		string extra = string.Empty;
		// A workshop's own task: which workshop, what it makes and what it holds.
		CraftingStation station = (worker != null && !(worker is Labor)) ? worker.GetComponent<CraftingStation>() : null;
		if (station != null)
		{
			Recipe recipe = station.CurrentRecipe;
			extra = " | recipe " + ((recipe != null && recipe.Creates != null) ? recipe.Creates.Name : "none")
				+ ((recipe != null) ? (" needs work " + recipe.NeedsWork + ", heat " + recipe.NeedsHeat) : string.Empty)
				+ " | holds " + ((station.recepteur != null) ? WorkerTraceDetails.Describe(station.recepteur.ContentDescription) : "?");
		}
		WorkerTrace.Log((worker != null) ? worker.name : "?", "task end: " + (string.IsNullOrEmpty(__instance.Description) ? "(task)" : __instance.Description) + " result=" + __instance.result + error + " after " + seconds.ToString("0.0") + " s" + extra);
	}
}

[Feature]
[HarmonyPatch(typeof(Labor), nameof(Labor.HandlePathFail))]
internal static class WorkerTracePathFailPatch
{
	private static void Prefix(Labor __instance, Exceptions.PathException e)
	{
		if (!WorkerTrace.Active)
		{
			return;
		}
		string destination = "?";
		try
		{
			destination = (e != null && e.Location != null) ? e.Location.ToString() : "?";
		}
		catch (Exception)
		{
		}
		WorkerTrace.Log(__instance, "path failed: " + ((e != null) ? e.GetType().Name : "?") + " to " + destination + " from " + __instance.transform.position.ToString("0"));
	}
}

// What the three FindAndPickUp overloads look for, for the "find failed" line.
[Feature]
[HarmonyPatch]
internal static class WorkerTraceWantedPatch
{
	private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
	{
		yield return AccessTools.Method(typeof(PickUpNode), nameof(PickUpNode.FindAndPickUp), new Type[] { typeof(Labor), typeof(Profile), typeof(int) });
		yield return AccessTools.Method(typeof(PickUpNode), nameof(PickUpNode.FindAndPickUp), new Type[] { typeof(Labor), typeof(Recepteur), typeof(int) });
		yield return AccessTools.Method(typeof(PickUpNode), nameof(PickUpNode.FindAndPickUp), new Type[] { typeof(Labor), typeof(Description), typeof(int) });
	}

	private static void Prefix(object[] __args)
	{
		if (!WorkerTrace.Active)
		{
			return;
		}
		object target = __args[1];
		WorkerTraceDetails.Wanted = (target is Profile profile) ? profile.description : ((target is Recepteur recepteur) ? recepteur.CurrentCapacity : (target as Description));
	}

	private static void Finalizer()
	{
		WorkerTraceDetails.Wanted = null;
	}
}

[Feature]
[HarmonyPatch(typeof(PickUpNode), "NodeFindAndPickUp")]
internal static class WorkerTraceFindFailedPatch
{
	private static void Prefix(Labor labor, GameObject item)
	{
		if (WorkerTrace.Active && item.IsNullOrReleased())
		{
			WorkerTraceDetails.FindFailed(labor);
		}
	}
}
