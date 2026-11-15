using System.Collections;
using System.Collections.Generic;
using Brix.Engine;
using Brix.Engine.Nature;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.AI.KnowledgeSpace;
using Brix.Game.Components;
using Brix.Game.Semantique;
using Brix.Input;
using Brix.Legacy;
using CastleStoryPlus.DevTools.Endpoints;
using CastleStoryPlus.DevTools.Pathing;
using CastleStoryPlus.DevTools.Testing;
using UnityEngine;

namespace CastleStoryPlus.DevTools.Tests;

// The player's orders to workers and how long they hold: a worker moved to another task switches at once (and stores
// what it carries first), a worker put on a task stays on it, and a worker told to dig a voxel waits for the next order.
[ParallelTests("Economy")]
internal static class WorkerOrderTests
{
	[GameTest("Tasks", "A quarry worker moved to a tree harvest with stone in its bag digs no more: it stores the stone, then goes to the trees", TimeoutSeconds = 420f)]
	private static IEnumerator ReassignedWorkerStoresThenSwitches(TestContext t)
	{
		XYZ site = Scenario.Site(t, 14, 8);
		Scenario.LeavesMess(t);
		// The trees of the harvest beside the quarry, on its level.
		XYZ tree = XYZ.zero;
		yield return TaskTests.Grove(t, 3, trees => tree = trees[0]);
		BuildProject build = Scenario.BuildProject(t, site);
		yield return Scenario.Building(t, TaskTests.Pallet, new XYZ(site.x - 5, site.y, site.z), build, _ => { });
		MineProject quarry = Quarry(t, site);
		HarvestProject harvest = Scenario.NewProject<HarvestProject>(t, () => Scenario.Groupe.CmdSpawnHarvestProject(tree.ToVector3(), Quaternion.identity, false, (int)NatureType.tree));
		ProjectEndpoints.Commit(harvest);
		Labor worker = Scenario.Bricktron(t, Occupation.Type.Builder, new XYZ(site.x - 1, site.y, site.z - 3));
		yield return t.Frames(5);
		Scenario.Hire(quarry, worker);
		yield return t.UntilGame(() => !worker.recepteur.IsEmpty(), 180f, "the quarry worker to dig something into its bag", 30f, () => t.Log(Describe(worker)));
		List<GameObject> carried = new List<GameObject>();
		foreach (IDescriptor item in worker.recepteur.StoredItems)
		{
			if (item != null && item.GameObject != null)
			{
				carried.Add(item.GameObject);
			}
		}
		t.Record("bagAtSwitch", worker.recepteur.ContentString());
		t.Record("bagFullAtSwitch", worker.recepteur.IsFull());
		t.Log("Moved to the harvest: " + Describe(worker));
		Scenario.Hire(harvest, worker);
		yield return t.Frames(2);
		t.True(worker.Project == harvest, worker.name + " is not on the harvest task");
		// The block it was digging when moved may still come out; it takes no other. (Other free workers may dig the
		// quarry: what counts is the worker's own task.)
		Task atSwitch = worker.CurrentTask;
		yield return t.UntilGame(() =>
		{
			t.True(TaskProject(worker) != quarry || worker.CurrentTask == atSwitch, worker.name + " went on digging in the quarry after it was moved: " + Describe(worker));
			return worker.recepteur.IsEmpty();
		}, 90f, "the worker's bag to be emptied", 15f, () => t.Log(Describe(worker)));
		int stored = InStorage(carried);
		t.Record("carried", carried.Count);
		t.Record("stored", stored);
		t.True(stored == carried.Count, "Of the " + carried.Count + " things in the bag " + stored + " went into a storage (dropped?)");
		yield return t.UntilGame(() =>
		{
			t.True(TaskProject(worker) != quarry || worker.CurrentTask == atSwitch, worker.name + " went back to digging in the quarry: " + Describe(worker));
			return TaskProject(worker) == harvest;
		}, 60f, "the worker to start on the harvest", 15f, () => t.Log(Describe(worker)));
	}

	[GameTest("Tasks", "A worker told to dig a voxel waits for the next order afterwards instead of going back to its task", TimeoutSeconds = 300f)]
	private static IEnumerator ManualDigWaitsForNextOrder(TestContext t)
	{
		XYZ site = Scenario.Site(t, 14, 8);
		Scenario.LeavesMess(t);
		BuildProject build = Scenario.BuildProject(t, site);
		yield return Scenario.Building(t, TaskTests.Pallet, new XYZ(site.x - 5, site.y, site.z), build, _ => { });
		MineProject quarry = Quarry(t, site);
		Labor worker = Scenario.Bricktron(t, Occupation.Type.Builder, new XYZ(site.x - 1, site.y, site.z - 3));
		yield return t.Frames(5);
		Scenario.Hire(quarry, worker);
		yield return t.UntilGame(() => TaskProject(worker) == quarry, 60f, "the worker to start in the quarry", 15f, () => t.Log(Describe(worker)));
		// Two voxels of ground west of the quarry, the player's dig orders.
		XYZ first = new XYZ(site.x - 1, site.y - 1, site.z + 3);
		XYZ second = new XYZ(site.x - 2, site.y - 1, site.z + 3);
		t.True(Voxel.IsFull(first) && Voxel.IsFull(second), "The voxels to dig are not ground");
		OrderDig(worker, first);
		yield return t.UntilGame(() => !Voxel.IsFull(first), 60f, "the ordered voxel to be dug", 15f, () => t.Log(Describe(worker)));
		t.Log("Dug the ordered voxel: " + Describe(worker));
		// What the voxel gave is taken out of the bag (a voxel that gives nothing, a load the player had it drop): the
		// wait after a dig does not depend on carrying something.
		EmptyBag(worker);
		// The player's next click comes a few seconds later: the worker has not gone back to work meanwhile.
		float waitUntil = Time.time + 6f;
		yield return t.UntilGame(() =>
		{
			t.True(TaskProject(worker) == null, worker.name + " went back to work right after the ordered dig: " + Describe(worker));
			return Time.time >= waitUntil;
		}, 10f, "6 game seconds");
		OrderDig(worker, second);
		yield return t.UntilGame(() => !Voxel.IsFull(second), 60f, "the second ordered voxel to be dug", 15f, () => t.Log(Describe(worker)));
		// Without another order the wait ends and it takes up something again (storing its load, work, the idle
		// walk; whether the quarry still has room to store into depends on the earlier tests).
		yield return t.UntilGame(() => CastleStoryPlus.WorkerAI.HoldAfterOrder.IsHeld(worker), 10f, "the worker to wait after the second dig");
		yield return t.UntilGame(() => !CastleStoryPlus.WorkerAI.HoldAfterOrder.IsHeld(worker) && worker.CurrentTask != null, 30f, "the worker to stop waiting and take up something", 10f, () => t.Log(Describe(worker)));
		t.Log("After the wait: " + Describe(worker));
	}

	[GameTest("Tasks", "A worker put on a tree harvest stays on it while another task of higher priority has work", TimeoutSeconds = 480f)]
	private static IEnumerator AssignedWorkerStaysOnTask(TestContext t)
	{
		yield return StaysOnTask(t, false);
	}

	[GameTest("Tasks", "A free worker right-clicked onto a tree of a harvest joins that harvest and stays on it while another task of higher priority has work", TimeoutSeconds = 480f)]
	private static IEnumerator RightClickedWorkerStaysOnTask(TestContext t)
	{
		yield return StaysOnTask(t, true);
	}

	// A harvest with at least two trees; a quarry next to the worker with a higher priority. The worker fells the first
	// tree and then goes on in the harvest, never in the quarry.
	private static IEnumerator StaysOnTask(TestContext t, bool rightClick)
	{
		XYZ site = Scenario.Site(t, 14, 8);
		Scenario.LeavesMess(t);
		XYZ tree = XYZ.zero;
		yield return TaskTests.Grove(t, 3, trees => tree = trees[0]);
		BuildProject build = Scenario.BuildProject(t, site);
		yield return Scenario.Building(t, TaskTests.Pallet, new XYZ(site.x - 5, site.y, site.z), build, _ => { });
		MineProject quarry = Quarry(t, site);
		Scenario.Groupe.CmdRaisePriority(quarry.gameObject);
		Scenario.Groupe.CmdRaisePriority(quarry.gameObject);
		HarvestProject harvest = Scenario.NewProject<HarvestProject>(t, () => Scenario.Groupe.CmdSpawnHarvestProject(tree.ToVector3(), Quaternion.identity, false, (int)NatureType.tree));
		ProjectEndpoints.Commit(harvest);
		yield return t.Frames(5);
		int trees = TreeGoals(harvest);
		t.Record("treesInHarvest", trees);
		t.True(trees >= 2, "The harvest has " + trees + " trees");
		Labor worker = Scenario.Bricktron(t, Occupation.Type.Builder, TaskTests.StandNear(t, tree));
		yield return t.Frames(5);
		Scenario.QuietWorldTasks(t);
		if (rightClick)
		{
			if (!CastleStoryPlus.WorkerAI.DirectTask.TryAssign(worker, (object)tree.ToVector3()))
			{
				t.Fail("The right-click on the tree " + tree + " gave no task; the harvest's tree goals: " + DescribeTreeGoals(harvest, worker));
			}
		}
		else
		{
			Scenario.Hire(harvest, worker);
		}
		yield return t.Frames(2);
		t.Record("projectAfterOrder", (worker.Project != null) ? worker.Project.GetType().Name : "none");
		t.Record("autonomyAfterOrder", worker.Autonomy.Description);
		List<XYZ> treesAtStart = TreePlaces(harvest);
		t.Record("standingTrees", treesAtStart.Count);
		yield return t.UntilGame(() =>
		{
			t.True(TaskProject(worker) != quarry, worker.name + " left the harvest for the quarry: " + Describe(worker));
			return Felled(treesAtStart) >= 2;
		}, 400f, "two trees of the harvest to be felled", 30f, () => t.Log(Describe(worker) + "; felled " + Felled(treesAtStart)));
		t.Record("felled", Felled(treesAtStart));
	}

	[GameTest("Tasks", "Eight builders put on an 8 x 8 quarry stay on it until it is dug out, also while there are fewer blocks left than workers: none walks off to the gather point or to another quarry of higher priority next to it (#86)", TimeoutSeconds = 300f)]
	private static IEnumerator FullCrewStaysOnBigQuarry(TestContext t)
	{
		XYZ site = Scenario.Site(t, 20, 14);
		Scenario.LeavesMess(t);
		int top = site.y - 1;
		// 8 x 8, one layer, on the west half, keeping nothing (no storage needed); a small one of higher priority and no
		// crew on the east half. Towards the end there are fewer blocks than workers: the ones without a block of their
		// own must wait for the last ones, not leave.
		MineProject quarry = Quarry(t, new Vector3(site.x - 9, top, site.z - 4), new Vector3(site.x - 2, top + 2, site.z + 3));
		quarry.GetComponent<QuarryData>().SetTypesToHarvest(0);
		MineProject other = Quarry(t, new Vector3(site.x + 3, top, site.z - 2), new Vector3(site.x + 6, top + 2, site.z + 1));
		other.GetComponent<QuarryData>().SetTypesToHarvest(0);
		Scenario.Groupe.CmdRaisePriority(other.gameObject);
		Scenario.Groupe.CmdRaisePriority(other.gameObject);
		Labor[] crew = new Labor[8];
		for (int i = 0; i < crew.Length; i++)
		{
			crew[i] = Scenario.Bricktron(t, Occupation.Type.Builder, new XYZ(site.x - 9 + i, site.y, site.z - 6));
		}
		yield return t.Frames(5);
		Scenario.Hire(quarry, crew);
		yield return t.Frames(2);
		Vector3 middle = new Vector3(site.x - 5.5f, site.y, site.z - 0.5f);
		float farthest = 0f;
		yield return t.UntilGame(() =>
		{
			if (QuarryDone(quarry))
			{
				return true;
			}
			foreach (Labor worker in crew)
			{
				t.True(worker.Project == quarry, worker.name + " left the quarry's crew: " + Describe(worker));
				t.True(TaskProject(worker) != other, worker.name + " works in the other quarry: " + Describe(worker));
				Vector3 at = worker.transform.position;
				float away = new Vector2(at.x - middle.x, at.z - middle.z).magnitude;
				farthest = Mathf.Max(farthest, away);
				// The gather point (the home crystal) is a plot or more away; 20 leaves room for a step aside round the quarry.
				t.AtMost(20f, away, worker.name + "'s distance from the quarry's middle (" + Describe(worker) + ")");
			}
			return false;
		}, 240f, "the quarry to be dug out", 30f, () => t.Log("quarry " + ProjectEndpoints.Describe(quarry)));
		t.Record("farthest", Mathf.Round(farthest * 10f) / 10f);
	}

	[GameTest("Tasks", "A builder put on a quarry with blocks left does not go after loose logs by a stockpile next to it (#81)", TimeoutSeconds = 240f)]
	private static IEnumerator AssignedWorkerLeavesLooseLogs(TestContext t)
	{
		XYZ site = Scenario.Site(t, 14, 8);
		Scenario.LeavesMess(t);
		BuildProject build = Scenario.BuildProject(t, site);
		yield return Scenario.Building(t, TaskTests.Pallet, new XYZ(site.x - 5, site.y, site.z), build, _ => { });
		// Loose logs within the idle cleanup's reach of the stockpile, and a quarry keeping nothing for two builders.
		List<GameObject> logs = Scenario.Items(t, Brix.External.Factories.Blocks.Wood_DoubleLog_AA, 4, new XYZ(site.x - 3, site.y, site.z + 3).ToVector3());
		MineProject quarry = Quarry(t, site);
		quarry.GetComponent<QuarryData>().SetTypesToHarvest(0);
		Labor a = Scenario.Bricktron(t, Occupation.Type.Builder, new XYZ(site.x, site.y, site.z - 3));
		Labor b = Scenario.Bricktron(t, Occupation.Type.Builder, new XYZ(site.x + 1, site.y, site.z - 3));
		yield return t.Frames(5);
		Scenario.Hire(quarry, a, b);
		yield return t.Frames(2);
		List<Vector3> lying = logs.ConvertAll((GameObject log) => log.transform.position);
		float end = Time.time + 60f;
		yield return t.UntilGame(() =>
		{
			foreach (Labor worker in new[] { a, b })
			{
				t.False(CastleStoryPlus.WorkerAI.AutoCleanup.IsCarrying(worker), worker.name + " went after the loose logs: " + Describe(worker));
				t.True(worker.Project == quarry, worker.name + " left the quarry: " + Describe(worker));
			}
			for (int i = 0; i < logs.Count; i++)
			{
				t.True(logs[i] != null && logs[i].activeInHierarchy && Vector3.Distance(logs[i].transform.position, lying[i]) < 2f, "A loose log was picked up or moved (from " + lying[i] + ")");
			}
			return Time.time >= end || QuarryDone(quarry);
		}, 70f, "60 game seconds of digging", 20f, () => t.Log(Describe(a) + "; " + Describe(b)));
	}

	private static bool QuarryDone(MineProject quarry)
	{
		List<Goal> goals = new List<Goal>();
		foreach (IGoalProvider provider in quarry.GetGoalProviders())
		{
			provider?.GetAll(goals);
		}
		return goals.Count == 0;
	}

	// ---- helpers

	// The player's dig order, as the right-click with the secondary action sends it (ContextualActor.SendOrder).
	private static void OrderDig(Labor worker, XYZ voxel)
	{
		worker.AssignToIdle();
		OrderPackage<Labor> package = Order.Package.OfLocation(Order.ForLabor.GoDig, new SpaceLocation(voxel.ToVector3(), walkable: false));
		package.DecisionSource = Labor.DecisionSource.User;
		package.Send(worker);
	}

	private static void EmptyBag(Labor worker)
	{
		foreach (IDescriptor item in new List<IDescriptor>(worker.recepteur.StoredItems))
		{
			if (item != null && item.GameObject != null)
			{
				worker.recepteur.ObjectUntracked(item.GameObject);
				Scenario.Release(item.GameObject);
			}
		}
	}

	// A 4 x 4 quarry of the top ground layer east of the site's middle, committed.
	private static MineProject Quarry(TestContext t, XYZ site)
	{
		int top = site.y - 1;
		return Quarry(t, new Vector3(site.x + 2, top, site.z - 2), new Vector3(site.x + 5, top + 2, site.z + 1));
	}

	// A quarry from min to max (max.y two above the top ground layer), committed.
	private static MineProject Quarry(TestContext t, Vector3 min, Vector3 max)
	{
		int top = (int)max.y - 2;
		List<XYZ> ground = new List<XYZ>();
		for (int x = (int)min.x; x <= (int)max.x; x++)
		{
			for (int z = (int)min.z; z <= (int)max.z; z++)
			{
				ground.Add(new XYZ(x, top, z));
			}
		}
		t.True(ground.TrueForAll(v => Voxel.IsFull(v) && !Voxel.IsInvincible(v)), "The quarry's ground is not all diggable");
		MineProject quarry = Scenario.NewProject<MineProject>(t, () => Scenario.Groupe.CmdSpawnMineProject(min, Quaternion.identity, false));
		StairOrientation stairs = StairOrientation.Default;
		Scenario.Groupe.CmdSetMine(quarry.gameObject, min, max, stairs.corner.ToVector3(), stairs.direction.ToVector3(), false);
		QuarryData data = quarry.GetComponent<QuarryData>();
		t.NotNull(data, "The quarry's data");
		stairs = data.StairOrientation;
		Scenario.Groupe.CmdSetMine(quarry.gameObject, data.Bounds.min.ToVector3(), data.Bounds.max.ToVector3(), stairs.corner.ToVector3(), stairs.direction.ToVector3(), true);
		ProjectEndpoints.Commit(quarry);
		return quarry;
	}

	private static string DescribeTreeGoals(Project project, Labor labor)
	{
		List<Goal> goals = new List<Goal>();
		foreach (IGoalProvider provider in project.GetGoalProviders())
		{
			provider?.GetAll(goals);
		}
		List<string> parts = new List<string>();
		foreach (Goal goal in goals)
		{
			if (goal is TreeGoal && parts.Count < 6)
			{
				parts.Add(goal.voxel.position + "/" + XYZ.FromVector3(goal.transform.position) + " valid " + goal.IsValid() + " faction " + ((goal.faction != null) ? goal.faction.name : "none") + " taken " + goal.HasOtherWorkerAssigned(labor));
			}
		}
		return string.Join("; ", parts.ToArray());
	}

	private static List<XYZ> TreePlaces(Project project)
	{
		List<Goal> goals = new List<Goal>();
		foreach (IGoalProvider provider in project.GetGoalProviders())
		{
			provider?.GetAll(goals);
		}
		List<XYZ> places = new List<XYZ>();
		foreach (Goal goal in goals)
		{
			XYZ place = XYZ.FromVector3(goal.transform.position);
			if (goal is TreeGoal && !goal.IsNullOrReleased() && TreeRegistry.Instance.GetTree(place) != 0)
			{
				places.Add(place);
			}
		}
		return places;
	}

	// How many of the things lie in a storage (a stockpile, not a bricktron's bag).
	private static int InStorage(List<GameObject> items)
	{
		HashSet<GameObject> wanted = new HashSet<GameObject>(items);
		int count = 0;
		foreach (Recepteur recepteur in Object.FindObjectsOfType<Recepteur>())
		{
			if (recepteur == null || recepteur.GetComponent<Labor>() != null)
			{
				continue;
			}
			foreach (IDescriptor item in recepteur.StoredItems)
			{
				if (item != null && item.GameObject != null && wanted.Contains(item.GameObject))
				{
					count++;
				}
			}
		}
		return count;
	}

	private static int Felled(List<XYZ> trees)
	{
		int felled = 0;
		foreach (XYZ tree in trees)
		{
			if (TreeRegistry.Instance.GetTree(tree) == 0)
			{
				felled++;
			}
		}
		return felled;
	}

	private static int TreeGoals(Project project)
	{
		int count = 0;
		List<Goal> goals = new List<Goal>();
		foreach (IGoalProvider provider in project.GetGoalProviders())
		{
			provider?.GetAll(goals);
		}
		foreach (Goal goal in goals)
		{
			if (goal is TreeGoal && !goal.IsNullOrReleased() && goal.IsValid())
			{
				count++;
			}
		}
		return count;
	}

	// The task area the worker's current (or next) task comes from; null for anything else (an order, idling).
	private static Project TaskProject(Labor labor)
	{
		Project project = From(labor.CurrentTask);
		return (project != null) ? project : From(labor.PendingTask);
	}

	private static Project From(Task task)
	{
		GameObject giver = (task != null) ? task.Giver : null;
		if (giver == null)
		{
			return null;
		}
		Project project = giver.GetComponent<Project>();
		if (project != null && !(project is IdleProject))
		{
			return project;
		}
		Goal goal = giver.GetComponent<Goal>();
		return (goal != null) ? goal.project : null;
	}

	private static string Describe(Labor labor)
	{
		if (!Scenario.Alive(labor))
		{
			return "(gone)";
		}
		Task task = labor.CurrentTask;
		return labor.name + ": " + labor.Activity + " at " + Units.VoxelOf(labor) + " on " + ((labor.Project != null) ? labor.Project.GetType().Name : "nothing") + " (" + labor.Autonomy.Description + ")"
			+ ((task != null) ? ", task " + task.Description + ((TaskProject(labor) != null) ? " of " + TaskProject(labor).GetType().Name : string.Empty) : ", no task")
			+ (labor.recepteur.IsEmpty() ? string.Empty : ", carrying " + labor.recepteur.ContentString());
	}
}
