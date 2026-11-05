using System;
using System.Collections.Generic;
using Brix.Engine;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Game.Components.Volumes;
using Brix.Input;
using Brix.Pathfinding.Test;
using CastleStoryPlus.Building;
using CastleStoryPlus.Core;
using CastleStoryPlus.Diagnostics;
using CastleStoryPlus.Pathfinding;
using HarmonyLib;
using Motus.Behavior;
using Smooth.Slinq;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.WorkerAI;

// Tasks and things no worker can get to are marked unreachable, shown in red and passed by, until the world near
// them changes. The game only asks its coarse reachability map (Knowledge), which says yes to places no route
// reaches, and remembers nothing when a walk then fails: builders walked to a blueprint on a wall without stairs,
// idle workers to logs behind a cliff, and gave up and went again, over and over, while real work waited. Now:
// - ReachMap floods the fine route graph from where the workers stand, by their own route search's rules. Every
//   blueprint ready to build, every dig voxel open to the air, every tree, plant or boulder to harvest and every
//   repair whose working places all lie off that map is marked; so is a loose item with no place to stand by it,
//   for the idle workers' cleanup and in a cleanup zone.
// - A walk to a blueprint that cannot be found, or fails twice within 2 minutes, marks it as well (a demolition walks
//   to a spot beside the block: the blueprint is the one of the worker's task).
// - A marked task goes 10 places down the list a worker picks its next task from and is only taken when none before
//   it can be ("Can't reach my destination" otherwise); the idle workers' cleanup skips a marked item.
// - A marked blueprint is drawn red; other marked tasks and items get a red ring.
// - A mark is only taken away when a voxel within 3 of the thing changes (a block built or removed, terrain dug, a
//   ladder or bridge placed); the next map then decides again.
// - A blueprint left hanging is drawn red as well: one that is not valid, or one waiting for support that only other
//   blueprints can give, when none of those can be built either (each is waiting itself, or marked unreachable). These
//   marks are made again with each new map, so the blueprint turns back as soon as something under it can be built.
// Host only; marks are not saved (after a load everything is looked at again).
[Feature(Features.UnreachableTasks, Features.UnreachableTasksInfo)]
internal static class UnreachableTasks
{
	// A change this close to a marked thing (in voxels, each axis) makes it worth trying again.
	internal const int Reach = 3;

	// Failed walks that may be passing trouble (a worker in the way) mark a blueprint after this many, within the window.
	private const int SoftFailures = 2;

	private const float SoftFailureWindow = 120f;

	// A mark for failed walks lasts this long; then the map decides again (a walk can fail for passing reasons: a
	// blueprint in the way that has been built since, a door that was shut).
	private const float WalkMarkSeconds = 120f;

	// Most rings drawn at once (a deep quarry can have hundreds of marked voxels).
	private const int MaxRings = 200;

	private static readonly Color Red = new Color(1f, 0.08f, 0.05f, 1f);

	private struct Box
	{
		public XYZ Min;

		public XYZ Max;

		public bool Contains(XYZ p)
		{
			return p.x >= Min.x && p.x <= Max.x && p.y >= Min.y && p.y <= Max.y && p.z >= Min.z && p.z <= Max.z;
		}
	}

	private const string NoRoute = "no route on the map";

	private const string NoPlaceToStand = "loose item with no place to stand by it";

	private sealed class Mark
	{
		public Box Box;

		// A blueprint: drawn red. Anything else: a ring.
		public bool Blueprint;

		public List<Renderer> Tinted;

		public bool Ringed;

		// Passed by but not ringed: a block of an ore mine (much of its ore is out of reach on purpose, and the rings
		// covered the area).
		public bool Quiet;

		// Left hanging (nothing under it can be built): decided anew with each map, not by changes nearby.
		public bool Hanging;

		// Marked for failed walks (not by the map): looked at again by the map after WalkMarkSeconds.
		public bool FromWalk;

		public string Why;

		public float Since;

		// Marked by the map (no route): looked at again with each new map, and let go once it can be reached.
		public bool FromMap;

		// The map's generation it was last judged on.
		public int Generation;
	}

	private struct Failures
	{
		public int Count;

		public float Since;
	}

	private static readonly Dictionary<GameObject, Mark> Marked = new Dictionary<GameObject, Mark>();

	private static readonly Dictionary<GameObject, Failures> SoftFailed = new Dictionary<GameObject, Failures>();

	private static int _evaluated = -1;

	private static int _ringId = -1;

	private static CircleHighlighter _ringOwner;

	private static MaterialPropertyBlock _redBlock;

	private static MaterialPropertyBlock _clearBlock;

	private static bool _enabled;

	private static void Enable()
	{
		_enabled = true;
		GameSession.OnLeave(() =>
		{
			Marked.Clear();
			SoftFailed.Clear();
			ReachMap.Clear();
			_evaluated = -1;
			_ringOwner = null;
		});
	}

	internal static int Count => Marked.Count;

	// Every blueprint is tried again (BuildWatchdog: a build task that stalled).
	internal static void ForgetAll()
	{
		Marked.Clear();
		SoftFailed.Clear();
	}

	internal static bool IsMarked(GameObject go)
	{
		return go != null && Marked.ContainsKey(go);
	}

	internal static bool IsMarked(Component component)
	{
		return component != null && IsMarked(component.gameObject);
	}

	// Why the thing is marked (null: not marked).
	internal static string WhyMarked(Component component)
	{
		return (component != null && Marked.TryGetValue(component.gameObject, out Mark mark)) ? mark.Why + " " + (int)(Time.time - mark.Since) + " s ago" : null;
	}

	// Whether the map now has a working place of the task for the faction (null: no map yet).
	internal static bool? MapReaches(Faction faction, Goal goal)
	{
		if (goal == null || !ReachMap.Has(faction))
		{
			return null;
		}
		return CanReach(faction, goal);
	}

	internal static bool IsHanging(Component component)
	{
		return component != null && Marked.TryGetValue(component.gameObject, out Mark mark) && mark.Hanging;
	}

	// ---- Marking ----

	private static void Add(GameObject go, Box box, bool blueprint, string why, Labor labor)
	{
		if (Marked.ContainsKey(go))
		{
			return;
		}
		SoftFailed.Remove(go);
		Marked[go] = new Mark { Box = box, Blueprint = blueprint, Why = why, Since = Time.time, FromMap = ByMap(why), Generation = ReachMap.Generation, Quiet = InOreMine(go) };
		if (WorkerTrace.Active)
		{
			string message = "unreachable: " + go.name + " at " + go.transform.position.ToString("0") + " (" + why + "), skipped until something changes within " + Reach;
			if (labor != null)
			{
				WorkerTrace.Log(labor, message);
			}
			else
			{
				WorkerTrace.Log("UnreachableTasks", message);
			}
		}
	}

	private static bool InOreMine(GameObject go)
	{
		Brix.Game.AI.HarvestProject harvest = go.GetComponentInParent<Brix.Game.AI.HarvestProject>();
		return harvest != null && Metallurgy.OreMine.IsOreMine(harvest);
	}

	// Marks judged by the map's routes (not left hanging, not failed walks) are looked at again with each map.
	private static bool ByMap(string why)
	{
		return why == NoRoute || why == NoPlaceToStand;
	}

	private static void Remove(GameObject go, Mark mark)
	{
		Untint(mark);
		Unring(go, mark);
		Marked.Remove(go);
	}

	// The thing's cells (where a worker stands to work on it, and what it takes up), grown by Reach.
	private static Box BoxOf(GameObject go, IVolume volume)
	{
		bool any = false;
		XYZ min = default(XYZ);
		XYZ max = default(XYZ);
		if (volume != null)
		{
			foreach (VProperty property in new[] { VProperty.fantome, VProperty.access, VProperty.operation, VProperty.occupy })
			{
				foreach (XYZ cell in volume.GetCases(property))
				{
					if (!any)
					{
						min = cell;
						max = cell;
						any = true;
						continue;
					}
					min = new XYZ(Math.Min(min.x, cell.x), Math.Min(min.y, cell.y), Math.Min(min.z, cell.z));
					max = new XYZ(Math.Max(max.x, cell.x), Math.Max(max.y, cell.y), Math.Max(max.z, cell.z));
				}
			}
		}
		if (!any)
		{
			min = VoxelOf(go);
			max = min;
		}
		return new Box
		{
			Min = new XYZ(min.x - Reach, min.y - Reach, min.z - Reach),
			Max = new XYZ(max.x + Reach, max.y + Reach, max.z + Reach)
		};
	}

	private static XYZ VoxelOf(GameObject go)
	{
		Vector3 p = go.transform.position;
		return new XYZ(Mathf.FloorToInt(p.x), Mathf.FloorToInt(p.y), Mathf.FloorToInt(p.z));
	}

	// ---- Failed walks ----

	internal static void OnPathFailed(Labor labor, Exceptions.PathException e)
	{
		if (!NetworkServer.active || e == null || labor.IsNullOrReleased())
		{
			return;
		}
		BuildGoal goal = GoalOf(labor, e);
		if (goal == null || goal.IsNullOrReleased() || Marked.ContainsKey(goal.gameObject))
		{
			return;
		}
		bool certain = e is Exceptions.PathUnfindable || e is Exceptions.PathBlockedSecondTime;
		if (!certain)
		{
			if (!(e is Exceptions.PathExecutionFailed))
			{
				return;
			}
			Failures failures = SoftFailed.TryGetValue(goal.gameObject, out Failures known) && Time.time - known.Since <= SoftFailureWindow ? known : new Failures { Since = Time.time };
			failures.Count++;
			if (failures.Count < SoftFailures)
			{
				SoftFailed[goal.gameObject] = failures;
				return;
			}
		}
		Add(goal.gameObject, BoxOf(goal.gameObject, goal.volume), blueprint: true, "walk failed: " + e.GetType().Name + " (" + labor.name + ")", labor);
		if (Marked.TryGetValue(goal.gameObject, out Mark mark))
		{
			mark.FromWalk = true;
		}
	}

	// The blueprint the failed walk was for: the walk's target when that is the blueprint (bringing material), else
	// the blueprint whose task the worker is on. A demolition walks to a voxel next to the block (a SpaceLocation, no
	// object); a walk to a stockpile for material is not the blueprint's fault and does not count.
	private static BuildGoal GoalOf(Labor labor, Exceptions.PathException e)
	{
		GameObject target = (e.Location != null) ? e.Location.Object : null;
		if (!target.IsNullOrReleased())
		{
			return target.GetComponent<BuildGoal>() ?? target.GetComponentInParent<BuildGoal>();
		}
		Task task = labor.CurrentTask;
		GameObject giver = (task != null) ? task.Giver : null;
		BuildGoal goal = !giver.IsNullOrReleased() ? giver.GetComponent<BuildGoal>() : null;
		return (goal != null && goal.IsAnti) ? goal : null;
	}

	// ---- The map ----

	// Each time ReachMap has a fresh map: the tasks of every player task group that are not marked yet.
	private static void Evaluate()
	{
		if (_evaluated == ReachMap.Generation)
		{
			return;
		}
		_evaluated = ReachMap.Generation;
		List<Goal> goals = new List<Goal>();
		HashSet<Faction> done = new HashSet<Faction>();
		foreach (Labor labor in Live<Labor>.Active())
		{
			Faction faction = labor.faction;
			if (faction == null || faction.isAI || !done.Add(faction) || !ReachMap.Has(faction))
			{
				continue;
			}
			List<IOperable<Labor>> projects = new List<IOperable<Labor>>();
			ProjectDatabase.For(faction).Projects().AddTo(projects);
			List<BuildGoal> builds = new List<BuildGoal>();
			foreach (IOperable<Labor> operable in projects)
			{
				if (!(operable is Project project) || project is IdleProject || project is RaidProject2 || !project.IsStillAlive)
				{
					continue;
				}
				goals.Clear();
				foreach (IGoalProvider provider in project.GetGoalProviders())
				{
					provider.GetAll(goals);
				}
				foreach (Goal goal in goals)
				{
					if (goal is BuildGoal build && !build.IsNullOrReleased() && !build.IsAnti)
					{
						builds.Add(build);
					}
					if (goal.IsNullOrReleased() || !IsJudged(goal))
					{
						continue;
					}
					// Marked by an earlier map (the item still rolling, the map not yet out there): let go once the
					// new map reaches it.
					if (Marked.TryGetValue(goal.gameObject, out Mark marked))
					{
						if (marked.FromMap && !marked.Hanging && marked.Generation != ReachMap.Generation)
						{
							marked.Generation = ReachMap.Generation;
							if (CanReach(faction, goal))
							{
								Unmark(goal.gameObject, marked, "the map reaches it now");
							}
						}
						continue;
					}
					if (!CanReach(faction, goal))
					{
						Add(goal.gameObject, BoxOf(goal.gameObject, goal.volume), goal is BuildGoal, NoRoute, null);
					}
				}
			}
			MarkHanging(builds);
		}
	}

	// Blueprints left hanging. A blueprint that can be built (and is not marked unreachable) can hold up the ones
	// waiting on it, and those in turn the ones waiting on them; a waiting blueprint none of whose support places is
	// taken by such a blueprint will not be built, nor will one that is not valid.
	private static void MarkHanging(List<BuildGoal> builds)
	{
		Dictionary<XYZ, BuildGoal> cellOwner = new Dictionary<XYZ, BuildGoal>();
		HashSet<BuildGoal> live = new HashSet<BuildGoal>();
		List<BuildGoal> waiting = new List<BuildGoal>();
		List<BuildGoal> invalid = new List<BuildGoal>();
		foreach (BuildGoal build in builds)
		{
			Blueprint blueprint = build.Blueprint;
			if (blueprint == null || blueprint.Released || !blueprint.State.HasFlag(PlacementFlags.placed))
			{
				continue;
			}
			if (build.volume != null)
			{
				foreach (XYZ cell in build.volume.GetCases(VProperty.fantome))
				{
					cellOwner[cell] = build;
				}
			}
			if (!blueprint.State.HasFlag(PlacementFlags.valid))
			{
				invalid.Add(build);
			}
			else if (!blueprint.State.HasFlag(PlacementFlags.buildable))
			{
				waiting.Add(build);
			}
			else if (!Marked.TryGetValue(build.gameObject, out Mark mark) || mark.Hanging)
			{
				live.Add(build);
			}
		}
		// Spread from what can be built to what waits on it, until nothing more comes alive.
		bool grew = true;
		while (grew)
		{
			grew = false;
			for (int i = waiting.Count - 1; i >= 0; i--)
			{
				if (RestsOnLive(waiting[i], cellOwner, live))
				{
					live.Add(waiting[i]);
					waiting.RemoveAt(i);
					grew = true;
				}
			}
		}
		HashSet<BuildGoal> hanging = new HashSet<BuildGoal>(waiting);
		hanging.UnionWith(invalid);
		// Hanging marks no longer right go; reach marks are left alone.
		List<GameObject> unmark = null;
		foreach (KeyValuePair<GameObject, Mark> entry in Marked)
		{
			if (entry.Value.Hanging && (entry.Key == null || !hanging.Contains(entry.Key.GetComponent<BuildGoal>())))
			{
				unmark = unmark ?? new List<GameObject>();
				unmark.Add(entry.Key);
			}
		}
		if (unmark != null)
		{
			foreach (GameObject go in unmark)
			{
				Remove(go, Marked[go]);
			}
		}
		foreach (BuildGoal build in hanging)
		{
			if (Marked.ContainsKey(build.gameObject))
			{
				continue;
			}
			Add(build.gameObject, BoxOf(build.gameObject, build.volume), blueprint: true, "left hanging: nothing under it can be built", null);
			Marked[build.gameObject].Hanging = true;
		}
	}

	private static bool RestsOnLive(BuildGoal build, Dictionary<XYZ, BuildGoal> cellOwner, HashSet<BuildGoal> live)
	{
		ValidatorComponent validator = build.GetComponent<ValidatorComponent>();
		IVolume volume = (validator != null) ? validator.VData.Volume : build.volume;
		if (volume == null)
		{
			return true;
		}
		foreach (XYZ cell in volume.GetCases(VProperty.mainSupportReq))
		{
			if (cellOwner.TryGetValue(cell, out BuildGoal under) && under != build && live.Contains(under))
			{
				return true;
			}
		}
		return false;
	}

	// Kinds of task looked at, and only those a worker could take now: a blueprint ready to build (not one waiting
	// for what holds it up), a dig voxel open to the air (not one deep in a quarry, which the digging opens up), a stair
	// step the digging has reached.
	private static bool IsJudged(Goal goal)
	{
		if (goal is BuildGoal build)
		{
			Blueprint blueprint = build.Blueprint;
			return blueprint != null && blueprint.State.HasFlag(PlacementFlags.valid) && blueprint.State.HasFlag(PlacementFlags.buildable) && build.Commited;
		}
		if (goal is DigGoal)
		{
			return IsOpen(goal.voxel.position);
		}
		// A quarry's or tunnel's stair step is only cut once the digging has reached it (the voxel above dug out).
		if (goal is BuildStairsGoal stairs)
		{
			return stairs.IsValid();
		}
		// A cleanup zone's item: one lying where no worker can stand by it (logs on a hill with no way up) was offered
		// over and over, every idle worker rejecting it.
		return goal is TreeGoal || goal is NatureHarvestGoal || goal is RepairGoal || (goal is CleanUpGoal && PickUpAround.IsLoose(goal.gameObject));
	}

	private static bool IsOpen(XYZ p)
	{
		return !Voxel.IsFull(p + XYZ.up) || !Voxel.IsFull(p - XYZ.up) || !Voxel.IsFull(p + XYZ.right) || !Voxel.IsFull(p - XYZ.right) || !Voxel.IsFull(p + XYZ.forward) || !Voxel.IsFull(p - XYZ.forward);
	}

	// A worker of the faction can stand on one of the task's working places (its access or operation cells); a task
	// without any is reached from beside it.
	private static bool CanReach(Faction faction, Goal goal)
	{
		if (goal is CleanUpGoal)
		{
			return ReachMap.Near(faction, VoxelOf(goal.gameObject), 1, 2, 1);
		}
		IVolume volume = goal.volume;
		bool anyCell = false;
		if (volume != null)
		{
			foreach (VProperty property in new[] { VProperty.access, VProperty.operation })
			{
				foreach (XYZ cell in volume.GetCases(property))
				{
					anyCell = true;
					if (ReachMap.Contains(faction, cell))
					{
						return true;
					}
				}
			}
		}
		return !anyCell && ReachMap.Near(faction, VoxelOf(goal.gameObject), 1, 2, 1);
	}

	// The idle workers' cleanup: a loose item with no place a worker can stand by it is marked and skipped.
	internal static bool ItemReachable(Labor labor, GameObject item)
	{
		if (!_enabled || labor == null || item == null)
		{
			return true;
		}
		Faction faction = labor.faction;
		if (Marked.TryGetValue(item, out Mark marked))
		{
			if (!marked.FromMap || marked.Hanging || marked.Generation == ReachMap.Generation || !ReachMap.Has(faction))
			{
				return false;
			}
			marked.Generation = ReachMap.Generation;
			if (!ReachMap.Near(faction, VoxelOf(item), 1, 2, 1))
			{
				return false;
			}
			Unmark(item, marked, "the map reaches it now");
			return true;
		}
		if (!ReachMap.Has(faction))
		{
			return true;
		}
		XYZ at = VoxelOf(item);
		if (ReachMap.Near(faction, at, 1, 2, 1))
		{
			return true;
		}
		// Still falling or rolling (a felled tree's logs): judged once it lies still.
		Rigidbody body = item.GetComponent<Rigidbody>();
		if (body != null && !body.isKinematic && !body.IsSleeping() && body.velocity.sqrMagnitude > 0.01f)
		{
			return true;
		}
		Add(item, BoxOf(item, null), blueprint: false, NoPlaceToStand, null);
		return false;
	}

	private static void Unmark(GameObject go, Mark mark, string why)
	{
		if (WorkerTrace.Active && go != null)
		{
			WorkerTrace.Log("UnreachableTasks", "reachable again: " + go.name + " at " + go.transform.position.ToString("0") + " (" + why + ")");
		}
		Remove(go, mark);
	}

	// The navigation graph is rebuilt for these changed voxels: marked things near one of them may be reachable now.
	internal static void OnVoxelsChanged(HashSet<XYZ> voxels)
	{
		if (voxels == null || voxels.Count == 0)
		{
			return;
		}
		ReachMap.MarkChanged();
		if (Marked.Count == 0)
		{
			return;
		}
		List<GameObject> unmark = null;
		foreach (KeyValuePair<GameObject, Mark> entry in Marked)
		{
			foreach (XYZ voxel in voxels)
			{
				if (!entry.Value.Hanging && entry.Value.Box.Contains(voxel))
				{
					unmark = unmark ?? new List<GameObject>();
					unmark.Add(entry.Key);
					if (WorkerTrace.Active && entry.Key != null)
					{
						WorkerTrace.Log("UnreachableTasks", "maybe reachable: " + entry.Key.name + " at " + entry.Key.transform.position.ToString("0") + " (voxel " + voxel + " changed)");
					}
					break;
				}
			}
		}
		if (unmark != null)
		{
			foreach (GameObject go in unmark)
			{
				Remove(go, Marked[go]);
			}
		}
	}

	// ---- Every frame ----

	internal static void Update()
	{
		if (NetworkServer.active)
		{
			ReachMap.Update();
			Evaluate();
		}
		Refresh();
	}

	// Drops marks of things that are gone (built, harvested, picked up, released to their pool) and draws the rest.
	private static void Refresh()
	{
		if (Marked.Count == 0)
		{
			return;
		}
		List<GameObject> gone = null;
		foreach (KeyValuePair<GameObject, Mark> entry in Marked)
		{
			if (IsGone(entry.Key) || (entry.Value.FromWalk && Time.time - entry.Value.Since > WalkMarkSeconds))
			{
				gone = gone ?? new List<GameObject>();
				gone.Add(entry.Key);
			}
		}
		if (gone != null)
		{
			foreach (GameObject go in gone)
			{
				Remove(go, Marked[go]);
			}
		}
		if (Time.frameCount % 15 != 0)
		{
			return;
		}
		CircleHighlighter owner = CircleHighlighter._circleHighlighter;
		if (owner != null && owner != _ringOwner)
		{
			_ringOwner = owner;
			_ringId = CircleHighlighter.GetId();
			foreach (Mark mark in Marked.Values)
			{
				mark.Ringed = false;
			}
		}
		int rings = 0;
		foreach (KeyValuePair<GameObject, Mark> entry in Marked)
		{
			Mark mark = entry.Value;
			if (mark.Blueprint)
			{
				if (mark.Tinted == null)
				{
					Tint(entry.Key, mark);
				}
				continue;
			}
			if (mark.Quiet)
			{
				continue;
			}
			if (mark.Ringed)
			{
				rings++;
				continue;
			}
			if (owner == null || rings >= MaxRings)
			{
				continue;
			}
			try
			{
				CircleHighlight ring = CircleHighlighter.HighlightGameObject(_ringId, entry.Key, Red, BuildPriority.Radius(entry.Key));
				ring.OccludedOpacity = 1f;
				mark.Ringed = true;
				rings++;
			}
			catch (Exception ex)
			{
				Plugin.Log.LogWarning("UnreachableTasks: ring on " + entry.Key.name + ": " + ex.Message);
			}
		}
	}

	private static bool IsGone(GameObject go)
	{
		if (go.IsNullOrReleased() || !go.activeInHierarchy)
		{
			return true;
		}
		Blueprint blueprint = go.GetComponent<Blueprint>();
		return blueprint != null && blueprint.Released;
	}

	// The blueprint's own renderers (not a ring under it) get a red tint; its materials are shared with every
	// blueprint of the kind, so the colour goes on each renderer alone.
	private static void Tint(GameObject go, Mark mark)
	{
		if (_redBlock == null)
		{
			_redBlock = new MaterialPropertyBlock();
			_redBlock.SetColor("_Color", Red);
			_redBlock.SetFloat("_FillOpacity", 0.85f);
		}
		mark.Tinted = new List<Renderer>();
		foreach (Renderer renderer in go.GetComponentsInChildren<Renderer>())
		{
			if (renderer == null || renderer.GetComponentInParent<CircleHighlight>() != null)
			{
				continue;
			}
			renderer.SetPropertyBlock(_redBlock);
			mark.Tinted.Add(renderer);
		}
	}

	private static void Untint(Mark mark)
	{
		if (mark.Tinted == null)
		{
			return;
		}
		_clearBlock = _clearBlock ?? new MaterialPropertyBlock();
		foreach (Renderer renderer in mark.Tinted)
		{
			if (renderer != null)
			{
				renderer.SetPropertyBlock(_clearBlock);
			}
		}
		mark.Tinted = null;
	}

	private static void Unring(GameObject go, Mark mark)
	{
		if (!mark.Ringed)
		{
			return;
		}
		mark.Ringed = false;
		try
		{
			CircleHighlighter.Remove(_ringId, go);
		}
		catch (Exception)
		{
			// The thing (and its ring with it) was already destroyed.
		}
	}
}

// A failed walk to a blueprint counts against it.
[Feature(Features.UnreachableTasks, Features.UnreachableTasksInfo)]
[HarmonyPatch(typeof(Labor), nameof(Labor.HandlePathFail))]
internal static class UnreachableTasksPathFailPatch
{
	private static void Prefix(Labor __instance, Exceptions.PathException e)
	{
		try
		{
			UnreachableTasks.OnPathFailed(__instance, e);
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning("UnreachableTasks: " + ex.Message);
		}
	}
}

// A marked task goes DeferBy places down the list a worker picks its next task from (UnreachableTasksDeferPatch),
// and is only taken when no task before it could be: one the worker can get to always comes first.
[Feature(Features.UnreachableTasks, Features.UnreachableTasksInfo)]
[HarmonyPatch(typeof(GoalSelector), nameof(GoalSelector.ProcessGoalAndReturnIsBest))]
internal static class UnreachableTasksSkipPatch
{
	private static bool Prefix(GoalSelector __instance, Labor labor, Goal goal, GoalSelector.GoalSelectionData selectionData, ref bool __result)
	{
		if (goal.IsNullOrReleased() || !UnreachableTasks.IsMarked(goal.gameObject))
		{
			return true;
		}
		if (selectionData != null && selectionData.BestGoal.IsNullOrReleased())
		{
			return true;
		}
		if (__instance.errorStore != null)
		{
			__instance.errorStore.AddDecisionError(SharedWorkException<Exceptions.CantReach>.Get.Problem, labor);
		}
		__result = false;
		return false;
	}
}

// The tasks a worker picks from, in the game's order, with each marked task held back until DeferBy more have come
// (or the list ends).
[Feature(Features.UnreachableTasks, Features.UnreachableTasksInfo)]
[HarmonyPatch(typeof(GoalSelector), "_SelectBestGoal")]
internal static class UnreachableTasksDeferPatch
{
	internal const int DeferBy = 10;

	private static void Prefix(ref IEnumerator<Goal> goals)
	{
		if (goals != null)
		{
			goals = new Deferred(goals);
		}
	}

	private sealed class Deferred : IEnumerator<Goal>
	{
		private readonly IEnumerator<Goal> _inner;

		// Held back marked tasks and how many tasks have come since each.
		private readonly List<KeyValuePair<Goal, int>> _held = new List<KeyValuePair<Goal, int>>();

		private bool _innerDone;

		public Deferred(IEnumerator<Goal> inner)
		{
			_inner = inner;
		}

		public Goal Current { get; private set; }

		object System.Collections.IEnumerator.Current => Current;

		public bool MoveNext()
		{
			if (_held.Count > 0 && (_innerDone || _held[0].Value >= DeferBy))
			{
				Current = _held[0].Key;
				_held.RemoveAt(0);
				return true;
			}
			while (!_innerDone)
			{
				if (!_inner.MoveNext())
				{
					_innerDone = true;
					break;
				}
				Goal goal = _inner.Current;
				if (goal != null && !goal.IsNullOrReleased() && UnreachableTasks.IsMarked(goal.gameObject))
				{
					_held.Add(new KeyValuePair<Goal, int>(goal, 0));
					continue;
				}
				for (int i = 0; i < _held.Count; i++)
				{
					_held[i] = new KeyValuePair<Goal, int>(_held[i].Key, _held[i].Value + 1);
				}
				Current = goal;
				return true;
			}
			if (_held.Count > 0)
			{
				Current = _held[0].Key;
				_held.RemoveAt(0);
				return true;
			}
			return false;
		}

		public void Reset()
		{
			throw new NotSupportedException();
		}

		public void Dispose()
		{
			_inner.Dispose();
		}
	}
}

// Every batch of changed voxels the navigation graph is rebuilt for (blocks built or removed, terrain dug, walkable
// objects placed), once the batch has settled. RefreshLod rather than the one-line Rebuild that calls it, which the
// JIT may inline.
[Feature(Features.UnreachableTasks, Features.UnreachableTasksInfo)]
[HarmonyPatch(typeof(TestPathfinding), "RefreshLod")]
internal static class UnreachableTasksRebuildPatch
{
	private static void Prefix(HashSet<XYZ> dirtyVoxels)
	{
		try
		{
			UnreachableTasks.OnVoxelsChanged(dirtyVoxels);
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning("UnreachableTasks: " + ex.Message);
		}
	}
}

[Feature(Features.UnreachableTasks, Features.UnreachableTasksInfo)]
[HarmonyPatch(typeof(InputModeController), nameof(InputModeController.Update))]
internal static class UnreachableTasksInputPatch
{
	private static void Prefix()
	{
		try
		{
			UnreachableTasks.Update();
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning("UnreachableTasks: " + ex.Message);
		}
	}
}
