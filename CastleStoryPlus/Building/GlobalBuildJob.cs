using System.Collections.Generic;
using Brix.Components;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Beacons;
using Brix.Game.Components;
using Brix.Game.Network;
using Brix.Lifecycle.Pooling;
using Brix.UI.GameSelector;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.Building;

// One build task per team. The game put each placed blueprint in the selected task group: a new build task each time
// build mode was opened without one, and when the selected group had no build list (the idle group, the repair task)
// placing threw a NullReferenceException and the blueprint was never handed to anyone, so it was never built. Every
// placed blueprint (and every removal blueprint) now goes into the team's one build task: the selected build task, else
// the one with the most blueprints, made when there is none. The blueprints of any other build task are moved into it
// and the emptied task deleted. A blueprint that lands outside the selected task is committed at once, as the game
// does when its task is deselected.
[Feature(Features.GlobalBuildJob, Features.GlobalBuildJobInfo)]
[HarmonyPatch(typeof(UNetBlueprint), "_FinishSpawnBlueprint")]
internal static class GlobalBuildJob
{
	// Set while a blueprint is handed to the team's build task instead of the selected one: it is committed when the
	// task takes it (GlobalBuildJobCommitPatch).
	internal static bool CommitAdded;

	private static void Prefix(UNetBlueprint __instance, bool instant, ref GameObject buildProject)
	{
		if (instant)
		{
			return;
		}
		Redirect(__instance, ref buildProject);
	}

	private static void Postfix(Blueprint blueprint)
	{
		if (CommitAdded)
		{
			Commit(blueprint);
		}
	}

	private static void Finalizer()
	{
		CommitAdded = false;
	}

	// Points the blueprint at the team's build task.
	internal static void Redirect(UNetBlueprint unet, ref GameObject buildProject)
	{
		User user = unet.GetComponent<User>();
		BuildProject selected = (buildProject != null) ? buildProject.GetComponent<BuildProject>() : null;
		BuildProject global = (user != null) ? For(user, selected) : null;
		if (global == null || global.gameObject == buildProject)
		{
			return;
		}
		buildProject = global.gameObject;
		CommitAdded = true;
	}

	internal static void Commit(Blueprint blueprint)
	{
		if (blueprint == null || blueprint.Released)
		{
			return;
		}
		BuildGoal goal = blueprint.GetComponent<BuildGoal>();
		if (goal != null)
		{
			goal.Commit();
		}
	}

	// The team's build task for work ordered without a build task in hand (moving a building): the one with the most
	// workers (then the most blueprints), not a new empty one, with the others merged into it.
	internal static BuildProject TeamProject(User user)
	{
		BuildProject best = null;
		foreach (GameObject go in BrixSingleton<AutoList>.Instance.GetInstances(Groupes.Construction))
		{
			BuildProject project = (go != null) ? go.GetComponent<BuildProject>() : null;
			if (project == null || !project.IsStillAlive || project.faction != user.faction)
			{
				continue;
			}
			if (best == null || project.CrewCount() > best.CrewCount() || (project.CrewCount() == best.CrewCount() && Blueprints(project).Count > Blueprints(best).Count))
			{
				best = project;
			}
		}
		return For(user, best);
	}

	// The team's build task: the selected one when the player placed in a build task, else the one with the most
	// blueprints, or a new one when there is none. The other build tasks are merged into it.
	private static BuildProject For(User user, BuildProject selected)
	{
		Faction faction = user.faction;
		List<BuildProject> projects = new List<BuildProject>();
		foreach (GameObject go in BrixSingleton<AutoList>.Instance.GetInstances(Groupes.Construction))
		{
			BuildProject project = (go != null) ? go.GetComponent<BuildProject>() : null;
			if (project != null && project.IsStillAlive && project.faction == faction)
			{
				projects.Add(project);
			}
		}
		BuildProject global = (selected != null && projects.Contains(selected)) ? selected : null;
		foreach (BuildProject project in projects)
		{
			if (global == null || (global != selected && Blueprints(project).Count > Blueprints(global).Count))
			{
				global = project;
			}
		}
		if (global == null)
		{
			return Create(user);
		}
		BuildGoalProvider target = global.GetComponent<BuildGoalProvider>();
		foreach (BuildProject other in projects)
		{
			if (other == global)
			{
				continue;
			}
			List<Blueprint> moving = new List<Blueprint>(Blueprints(other));
			foreach (Blueprint blueprint in moving)
			{
				if (blueprint != null && !blueprint.Released)
				{
					target.AddObject(blueprint.gameObject);
					Commit(blueprint);
				}
			}
			int crew = MoveCrew(other, global);
			Plugin.Log.LogInfo("GlobalBuildJob: moved " + moving.Count + " blueprints and " + crew + " workers of " + other.name + " into " + global.name);
			if (Blueprints(other).Count == 0 && !UIGameSelector.IsSelected(other.gameObject))
			{
				other.RequestDelete();
			}
		}
		return global;
	}

	// The workers of a merged build task go with its blueprints: deleting the task sent them to the idle group, so the
	// team's build task was left with no one to build (or demolish for a move).
	private static int MoveCrew(BuildProject from, BuildProject to)
	{
		int moved = 0;
		foreach (Labor labor in new List<Labor>(from.Crew()))
		{
			if (labor.IsNullOrReleased())
			{
				continue;
			}
			AutonomyStatus autonomy = labor.Autonomy;
			if (to.IsFull())
			{
				to.MaximumCrewSize++;
			}
			if (to.Hire(labor))
			{
				labor.Autonomy = autonomy;
				moved++;
			}
		}
		return moved;
	}

	private static HashSet<Blueprint> Blueprints(BuildProject project)
	{
		BuildGoalProvider provider = project.GetComponent<BuildGoalProvider>();
		return (provider != null) ? provider.TrackedBlueprints : new HashSet<Blueprint>();
	}

	// As the game makes a build task for blueprints (UNetBlueprint.CmdSendBlueprintsToNewProject).
	private static BuildProject Create(User user)
	{
		ObjectPoolSingleton.RequestComponent<BeaconDriver>(out BeaconDriver driver, Groupes.Construction);
		driver.faction = user.faction;
		driver.affiliation.NetworkUserCreator = user.gameObject;
		if (driver.GetComponent<NetworkIdentity>() != null)
		{
			NetworkServer.Spawn(driver.gameObject, Groupes.Construction.AssetId);
		}
		Plugin.Log.LogInfo("GlobalBuildJob: made the build task of " + user.faction);
		return driver.GetComponent<BuildProject>();
	}
}

// Removal blueprints go to the same build task.
[Feature(Features.GlobalBuildJob, Features.GlobalBuildJobInfo)]
[HarmonyPatch(typeof(UNetBlueprint), nameof(UNetBlueprint.CmdPlaceAnti))]
internal static class GlobalBuildJobAntiPatch
{
	private static void Prefix(UNetBlueprint __instance, bool instant, ref GameObject BuildProject)
	{
		if (instant)
		{
			return;
		}
		GlobalBuildJob.Redirect(__instance, ref BuildProject);
	}

	private static void Finalizer()
	{
		GlobalBuildJob.CommitAdded = false;
	}
}

[Feature(Features.GlobalBuildJob, Features.GlobalBuildJobInfo)]
[HarmonyPatch(typeof(BuildGoalProvider), nameof(BuildGoalProvider.OnObjectTracked))]
internal static class GlobalBuildJobCommitPatch
{
	private static void Postfix(GameObject blueprintGO)
	{
		if (GlobalBuildJob.CommitAdded && blueprintGO != null)
		{
			GlobalBuildJob.Commit(blueprintGO.GetComponent<Blueprint>());
		}
	}
}

// The build button (and its hotkey) made a new, empty build task each time, which then took over the team's
// blueprints. With a build task already there, it is selected instead.
[Feature(Features.GlobalBuildJob, Features.GlobalBuildJobInfo)]
[HarmonyPatch(typeof(UIGameObserver), "lua_NewBuildTask")]
internal static class GlobalBuildJobNewTaskPatch
{
	private static bool Prefix()
	{
		User user = User.LocalUser;
		if (user == null || user.faction == null)
		{
			return true;
		}
		BuildProject best = null;
		foreach (GameObject go in BrixSingleton<AutoList>.Instance.GetInstances(Groupes.Construction))
		{
			BuildProject project = (go != null) ? go.GetComponent<BuildProject>() : null;
			if (project == null || !project.IsStillAlive || project.faction != user.faction)
			{
				continue;
			}
			if (best == null || project.CrewCount() > best.CrewCount())
			{
				best = project;
			}
		}
		if (best == null)
		{
			return true;
		}
		UIGameSelector.Select(best.gameObject);
		return false;
	}
}
