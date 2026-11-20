using System.Collections.Generic;
using Brix.Engine;
using Brix.Engine.Nature;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Input;
using Brix.Input.ContextualOrders;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.WorkerAI;

// Select bricktrons and right-click a concrete task (a blueprint or demolish ghost, a tree of a harvest area, a
// block of a dig or landscape area, a boulder, crystal or plant of a harvest area, an item of a cleanup area): they
// start that very task at once. The game's right-click only sent them there (or made a new task of its own next to
// the area's), and took them out of their crew. Now the task of the area is given to them as their project would
// give it, so they keep their crew and go back to its work afterwards.
// Several selected bricktrons on a task only one can do (digging, a tree...) take the nearest free tasks of the same
// area around the click. A right-click on anything else, or with the secondary or ternary action, is the game's.
// The host decides (the right-click order is a network command); the client only points the order at a blueprint.
[Feature(Features.DirectTask, Features.DirectTaskInfo)]
internal static class DirectTask
{
	// How far from the clicked task another free task of the same area may be, for further selected bricktrons.
	private const float SpreadRadius = 6f;

	private static readonly List<Goal> Goals = new List<Goal>();

	private static readonly List<IOperable<Labor>> Projects = new List<IOperable<Labor>>();

	// Server: gives the task under the click to the bricktron. False when there is no task there.
	internal static bool TryAssign(Labor labor, object target)
	{
		if (labor.IsNullOrReleased() || labor.faction == null || labor.faction.isAI)
		{
			return false;
		}
		Goal goal = FindGoal(labor, target, out Project project, out Vector3 point);
		if (goal == null)
		{
			return false;
		}
		if (TaskReservation.IsExclusive(goal) && IsTaken(goal, labor))
		{
			goal = NearestFree(labor, goal, project, point);
			if (goal == null)
			{
				return false;
			}
		}
		if (project != null)
		{
			JoinCrew(labor, project);
			goal.ConnectProject(project);
		}
		TaskReservation.Reserve(goal, labor);
		goal.Instruction.Send(labor, goal.gameObject, Labor.DecisionSource.Project);
		Plugin.Log.LogInfo("Direct task: " + labor.gameObject.name + " -> " + goal.GetType().Name + " at " + goal.voxel.position);
		return true;
	}

	// As the game's assign (UNetProjectCmd.CmdChangeProject): a full crew takes one more.
	private static void JoinCrew(Labor labor, Project project)
	{
		if (project is IdleProject || project is RaidProject2)
		{
			return;
		}
		if (labor.Project != project && project.IsFull())
		{
			project.MaximumCrewSize++;
		}
		if (project.Hire(labor))
		{
			labor.Autonomy = AutonomyStatus.LockedToOperable;
		}
	}

	private static Goal FindGoal(Labor labor, object target, out Project project, out Vector3 point)
	{
		project = null;
		point = Vector3.zero;
		if (target is GameObject go)
		{
			if (go == null)
			{
				return null;
			}
			point = go.transform.position;
			Blueprint blueprint = go.GetComponentInParent<Blueprint>();
			if (blueprint != null)
			{
				BuildGoal build = blueprint.GetComponent<BuildGoal>();
				project = blueprint.Project;
				return Usable(build, labor) ? build : null;
			}
			CleanUpGoal cleanup = go.GetComponent<CleanUpGoal>();
			if (Usable(cleanup, labor))
			{
				project = FindProject(labor, cleanup);
				return cleanup;
			}
			return null;
		}
		if (!(target is Vector3 position))
		{
			return null;
		}
		point = position;
		XYZ voxel = XYZ.FromVector3(position);
		// A boulder, crystal or plant is worked from its goal position, not from where it was clicked.
		XYZ? natureGoal = null;
		NatureData nature = NatureRegistry.HasNatureAt(voxel) ? NatureRegistry.NatureAt(voxel) : null;
		if (nature != null)
		{
			natureGoal = XYZ.FromVector3(nature.GoalPosition);
		}
		foreach (Project candidate in OwnProjects(labor))
		{
			Goals.Clear();
			AllGoals(candidate, Goals);
			foreach (Goal goal in Goals)
			{
				// A tree's or boulder's goal belongs to no player (Neutral); the area holding it is the player's.
				if (!Usable(goal, labor, ownArea: true) || goal is BuildGoal)
				{
					continue;
				}
				XYZ at = goal.voxel.position;
				// A tree's goal stands at the tree's voxel.
				if (at == voxel || XYZ.FromVector3(goal.transform.position) == voxel || (natureGoal.HasValue && at == natureGoal.Value))
				{
					project = candidate;
					return goal;
				}
			}
		}
		return null;
	}

	// The nearest goal of the same kind and area that nobody works on, near the click.
	private static Goal NearestFree(Labor labor, Goal clicked, Project project, Vector3 point)
	{
		if (project == null)
		{
			return null;
		}
		Goals.Clear();
		AllGoals(project, Goals);
		Goal best = null;
		float bestDistance = SpreadRadius * SpreadRadius;
		foreach (Goal goal in Goals)
		{
			if (goal == clicked || goal.GetType() != clicked.GetType() || !Usable(goal, labor, ownArea: true) || IsTaken(goal, labor))
			{
				continue;
			}
			float distance = (goal.transform.position - point).sqrMagnitude;
			if (distance <= bestDistance)
			{
				best = goal;
				bestDistance = distance;
			}
		}
		return best;
	}

	private static bool IsTaken(Goal goal, Labor labor)
	{
		return goal.HasOtherWorkerAssigned(labor) || TaskReservation.OtherReservations(goal, labor) > 0;
	}

	private static bool Usable(Goal goal, Labor labor, bool ownArea = false)
	{
		return !goal.IsNullOrReleased() && goal.IsValid() && (ownArea || goal.faction == null || goal.faction == labor.faction);
	}

	private static Project FindProject(Labor labor, Goal wanted)
	{
		foreach (Project candidate in OwnProjects(labor))
		{
			Goals.Clear();
			AllGoals(candidate, Goals);
			if (Goals.Contains(wanted))
			{
				return candidate;
			}
		}
		return null;
	}

	private static List<Project> OwnProjects(Labor labor)
	{
		Projects.Clear();
		ProjectDatabase.For(labor.faction).Projects().AddTo(Projects);
		List<Project> result = new List<Project>();
		foreach (IOperable<Labor> operable in Projects)
		{
			if (operable is Project project && project != null)
			{
				result.Add(project);
			}
		}
		return result;
	}

	private static void AllGoals(Project project, List<Goal> goals)
	{
		foreach (IGoalProvider provider in project.GetGoalProviders())
		{
			provider?.GetAll(goals);
		}
	}

	// Client: the blueprint under the cursor, if the right-click was on one of the player's shown blueprints.
	internal static GameObject BlueprintUnderCursor()
	{
		if (Camera.main == null || Picking.Instance == null || PickingUtility.Player == null)
		{
			return null;
		}
		Vector2 mouse = PickingUtility.Player.controllers.Mouse.screenPosition;
		return BlueprintAlong(Camera.main.ScreenPointToRay(mouse), Picking.Instance.MaxDistance);
	}

	// How many hidden blueprints a click may pass through before it gives up on finding a shown one behind them.
	private const int MaxHiddenBlueprints = 16;

	// The first of the player's blueprints along the ray, if nothing else is in front of it. A hidden blueprint (its
	// renderers switched off, #147) keeps its colliders, so it is passed through: a click on it is a click on what is
	// behind it, as the game's own order ray (which has no blueprint layer) sees it.
	internal static GameObject BlueprintAlong(Ray ray, float distance)
	{
		VoxelRaycastHit hit = new VoxelRaycastHit();
		LayerBundle layers = new LayerBundle((int)UnityLayer.Terrain, (int)UnityLayer.Blueprints, (int)UnityLayer.FreeBlocks, (int)UnityLayer.DynamicObjects);
		for (int pass = 0; pass < MaxHiddenBlueprints; pass++)
		{
			if (!hit.PlaceAtRaycast(ray, distance, layers) || hit.HitGameObject == null)
			{
				return null;
			}
			Blueprint blueprint = hit.HitGameObject.GetComponentInParent<Blueprint>();
			if (blueprint == null)
			{
				return null;
			}
			if (IsShown(blueprint))
			{
				if (User.LocalUser == null || !User.LocalUser.faction.IsSame(blueprint.gameObject))
				{
					return null;
				}
				return blueprint.gameObject;
			}
			// A ray that starts inside the hidden blueprint's collider does not hit that collider again.
			float travelled = Vector3.Distance(ray.origin, hit.RayHit.point) + 0.01f;
			distance -= travelled;
			if (distance <= 0f)
			{
				return null;
			}
			ray = new Ray(ray.GetPoint(travelled), ray.direction);
		}
		return null;
	}

	// A blueprint is shown while any of its renderers is on; the game hides one by switching them all off
	// (StateRenderer.Show(false)) and leaves its colliders in place.
	internal static bool IsShown(Blueprint blueprint)
	{
		if (blueprint == null)
		{
			return false;
		}
		foreach (MeshRenderer renderer in blueprint.GetComponentsInChildren<MeshRenderer>())
		{
			if (renderer != null && renderer.enabled)
			{
				return true;
			}
		}
		return false;
	}

	// Whether the host gives this unit's right-click order to a BuilderActor (ContextualAction): only those take a
	// task under the click; knights and archers get the game's move or attack order.
	internal static bool TakesDirectTask(GameObject unit)
	{
		if (unit == null)
		{
			return false;
		}
		Occupation occupation = unit.GetComponent<Occupation>();
		if (occupation == null)
		{
			return false;
		}
		Occupation.Type type = occupation.CurrentOccupation;
		return type == Occupation.Type.Builder || type == Occupation.Type.Corruptron || type == Occupation.Type.Biftron || type == Occupation.Type.Minitron;
	}

	// Splits a selection into the units that take a task under the click and the others (soldiers).
	internal static void SplitByWork(List<GameObject> units, List<GameObject> workers, List<GameObject> others)
	{
		foreach (GameObject unit in units)
		{
			if (TakesDirectTask(unit))
			{
				workers.Add(unit);
			}
			else
			{
				others.Add(unit);
			}
		}
	}
}

// Server: a right-click order (primary action) on a task gives the bricktron that task instead of the game's order.
[Feature(Features.DirectTask, Features.DirectTaskInfo)]
[HarmonyPatch(typeof(ContextualActor), nameof(ContextualActor.Prepare))]
internal static class DirectTaskOrderPatch
{
	private static bool Prefix(ContextualActor __instance, object target)
	{
		if (__instance.actionType != ActionType.Primary || !(__instance is BuilderActor))
		{
			return true;
		}
		if (!DirectTask.TryAssign(__instance.subject, target))
		{
			return true;
		}
		// Given: nothing left for SendOrder to do.
		__instance.orderPackage = null;
		return false;
	}
}

// Client: a right-click on a shown blueprint is sent as an order on the blueprint (the game sends the ground behind
// it) for the workers of the selection. Soldiers in the same selection get the game's order on the ground, and a
// selection without workers, or a click on a hidden blueprint, is left to the game altogether (#147).
[Feature(Features.DirectTask, Features.DirectTaskInfo)]
[HarmonyPatch(typeof(PickerOrderGiver), nameof(PickerOrderGiver.DirectOrder), new System.Type[] { typeof(List<GameObject>), typeof(Vector3) })]
internal static class DirectTaskBlueprintClickPatch
{
	private static bool Prefix(PickerOrderGiver __instance, List<GameObject> listGO, Vector3 target)
	{
		if (listGO == null || PickingUtility.GetActionType() != ActionType.Primary)
		{
			return true;
		}
		// New lists: the order giver keeps its own copy, and the soldiers' order below comes back through this prefix.
		List<GameObject> workers = new List<GameObject>();
		List<GameObject> others = new List<GameObject>();
		DirectTask.SplitByWork(listGO, workers, others);
		if (workers.Count == 0)
		{
			return true;
		}
		GameObject blueprint = DirectTask.BlueprintUnderCursor();
		if (blueprint == null)
		{
			return true;
		}
		__instance.DirectOrder(workers, blueprint);
		if (others.Count > 0)
		{
			__instance.DirectOrder(others, target);
		}
		return false;
	}
}
