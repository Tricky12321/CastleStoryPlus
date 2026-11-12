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
		// A tree near the quarry, on the same level (one far below a cliff may have no way there).
		XYZ tree = TreeOnLevel(t, site, 40);
		Scenario.LeavesMess(t);
		BuildProject build = Scenario.BuildProject(t, site);
		yield return Scenario.Building(t, TaskTests.Pallet, new XYZ(site.x - 5, site.y, site.z), build, _ => { });
		MineProject quarry = Quarry(t, site, out List<XYZ> ground);
		HarvestProject harvest = Scenario.NewProject<HarvestProject>(t, () => Scenario.Groupe.CmdSpawnHarvestProject(tree.ToVector3(), Quaternion.identity, false, (int)NatureType.tree));
		ProjectEndpoints.Commit(harvest);
		Labor worker = Scenario.Bricktron(t, Occupation.Type.Builder, new XYZ(site.x - 1, site.y, site.z - 3));
		yield return t.Frames(5);
		Scenario.Hire(quarry, worker);
		yield return t.UntilGame(() => !worker.recepteur.IsEmpty(), 180f, "the quarry worker to dig something into its bag", 30f, () => t.Log(Describe(worker)));
		int dugAtSwitch = Dug(ground);
		int storedAtSwitch = Scenario.Stored(RawResources.RawStone);
		t.Record("bagAtSwitch", worker.recepteur.ContentString());
		t.Record("bagFullAtSwitch", worker.recepteur.IsFull());
		t.Log("Moved to the harvest: " + Describe(worker));
		Scenario.Hire(harvest, worker);
		yield return t.Frames(2);
		t.True(worker.Project == harvest, worker.name + " is not on the harvest task");
		// The block it was digging when moved may still come out; no other.
		int mostDug = dugAtSwitch + 1;
		yield return t.UntilGame(() =>
		{
			t.True(Dug(ground) <= mostDug, worker.name + " went on digging in the quarry after it was moved (" + (Dug(ground) - dugAtSwitch) + " more blocks): " + Describe(worker));
			return worker.recepteur.IsEmpty();
		}, 90f, "the worker's bag to be emptied", 15f, () => t.Log(Describe(worker)));
		t.Record("storedSinceSwitch", Scenario.Stored(RawResources.RawStone) - storedAtSwitch);
		t.True(Scenario.Stored(RawResources.RawStone) > storedAtSwitch, "The stone in the bag was not put into the stockpile (dropped?)");
		yield return t.UntilGame(() =>
		{
			t.True(Dug(ground) <= mostDug, worker.name + " went back to digging in the quarry: " + Describe(worker));
			return TaskProject(worker) == harvest;
		}, 60f, "the worker to start on the harvest", 15f, () => t.Log(Describe(worker)));
		t.Record("dugAfterSwitch", Dug(ground) - dugAtSwitch);
	}

	[GameTest("Tasks", "A worker told to dig a voxel waits for the next order afterwards instead of going back to its task", TimeoutSeconds = 300f)]
	private static IEnumerator ManualDigWaitsForNextOrder(TestContext t)
	{
		XYZ site = Scenario.Site(t, 14, 8);
		Scenario.LeavesMess(t);
		BuildProject build = Scenario.BuildProject(t, site);
		yield return Scenario.Building(t, TaskTests.Pallet, new XYZ(site.x - 5, site.y, site.z), build, _ => { });
		MineProject quarry = Quarry(t, site, out List<XYZ> ground);
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
		int dugAfterOrder = Dug(ground);
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
		t.Equal(dugAfterOrder, Dug(ground), "Quarry blocks dug while the worker waited");
		OrderDig(worker, second);
		yield return t.UntilGame(() => !Voxel.IsFull(second), 60f, "the second ordered voxel to be dug", 15f, () => t.Log(Describe(worker)));
		// Without another order it goes back to work in the end.
		yield return t.UntilGame(() => TaskProject(worker) == quarry || Dug(ground) > dugAfterOrder, 60f, "the worker to go back to the quarry", 15f, () => t.Log(Describe(worker)));
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
		XYZ tree = TreeWithNeighbour(t, Scenario.Home(), 45);
		XYZ site = Scenario.Site(t, 14, 8);
		Scenario.LeavesMess(t);
		BuildProject build = Scenario.BuildProject(t, site);
		yield return Scenario.Building(t, TaskTests.Pallet, new XYZ(site.x - 5, site.y, site.z), build, _ => { });
		MineProject quarry = Quarry(t, site, out List<XYZ> ground);
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
		int startTrees = TreeGoals(harvest);
		yield return t.UntilGame(() =>
		{
			t.True(TaskProject(worker) != quarry && Dug(ground) == 0, worker.name + " left the harvest for the quarry: " + Describe(worker));
			return startTrees - TreeGoals(harvest) >= 2;
		}, 400f, "two trees of the harvest to be felled", 30f, () => t.Log(Describe(worker) + "; trees left " + TreeGoals(harvest)));
		t.Record("felled", startTrees - TreeGoals(harvest));
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
	private static MineProject Quarry(TestContext t, XYZ site, out List<XYZ> ground)
	{
		int top = site.y - 1;
		Vector3 min = new Vector3(site.x + 2, top, site.z - 2);
		Vector3 max = new Vector3(site.x + 5, top + 2, site.z + 1);
		ground = new List<XYZ>();
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

	// The tree nearest the site within 3 voxels of its height, untouched.
	private static XYZ TreeOnLevel(TestContext t, XYZ site, int radius)
	{
		XYZ best = XYZ.zero;
		int bestDistance = int.MaxValue;
		foreach (XYZ tree in WorldEndpoints.TreesOver(site.x - radius, site.z - radius, site.x + radius, site.z + radius))
		{
			int distance = WorldEndpoints.FlatDistance(tree, site);
			if (Mathf.Abs(tree.y - site.y) <= 3 && distance >= 64 && distance < bestDistance && TreeRegistry.Instance.GetTree(tree) != 0 && DamagedTree.GetAt(tree) == null)
			{
				best = tree;
				bestDistance = distance;
			}
		}
		if (bestDistance == int.MaxValue)
		{
			t.Skip("No tree on the level of " + site + " within " + radius + " voxels");
		}
		t.Log("Tree at " + best);
		return best;
	}

	// A tree with another tree within 3 voxels, so a harvest round it has two.
	private static XYZ TreeWithNeighbour(TestContext t, XYZ near, int radius)
	{
		List<XYZ> all = new List<XYZ>();
		foreach (XYZ tree in WorldEndpoints.TreesOver(near.x - radius, near.z - radius, near.x + radius, near.z + radius))
		{
			if (TreeRegistry.Instance.GetTree(tree) != 0 && DamagedTree.GetAt(tree) == null)
			{
				all.Add(tree);
			}
		}
		XYZ best = XYZ.zero;
		float bestDistance = float.MaxValue;
		foreach (XYZ tree in all)
		{
			float distance = Mathf.Sqrt(WorldEndpoints.FlatDistance(tree, near));
			if (distance < 6f || distance >= bestDistance)
			{
				continue;
			}
			foreach (XYZ other in all)
			{
				if (other != tree && WorldEndpoints.FlatDistance(tree, other) <= 9)
				{
					best = tree;
					bestDistance = distance;
					break;
				}
			}
		}
		if (bestDistance == float.MaxValue)
		{
			t.Skip("No two trees close together within " + radius + " voxels of " + near);
		}
		return best;
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

	private static int Dug(List<XYZ> voxels)
	{
		int dug = 0;
		foreach (XYZ voxel in voxels)
		{
			if (!Voxel.IsFull(voxel))
			{
				dug++;
			}
		}
		return dug;
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
