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
using FactoryBlocks = Brix.External.Factories.Blocks;

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

	[GameTest("Tasks", "A worker that joins a quarry with planks in its bag stores them in a storage before it digs a block (#129, #124)", TimeoutSeconds = 300f)]
	private static IEnumerator StoresForeignLoadBeforeDigging(TestContext t)
	{
		XYZ site = Scenario.Site(t, 14, 8);
		Scenario.LeavesMess(t);
		BuildProject build = Scenario.BuildProject(t, site);
		yield return Scenario.Building(t, TaskTests.Pallet, new XYZ(site.x - 5, site.y, site.z), build, _ => { });
		MineProject quarry = Quarry(t, site);
		// It keeps nothing: the pallet holds the plank (one resource at a time), so there is no storage for stone, and the blocks are dug without a drop.
		quarry.GetComponent<QuarryData>().SetTypesToHarvest(0);
		Labor worker = Scenario.Bricktron(t, Occupation.Type.Builder, new XYZ(site.x - 1, site.y, site.z - 3));
		yield return t.Frames(5);
		int before = QuarryGround(site);
		GameObject plank = Scenario.Items(t, FactoryBlocks.Wood_DoublePlank_AA, 1, worker.transform.position + Vector3.up * 3f)[0];
		t.True(worker.recepteur.HasRoomFor(plank), "The bag has no room for a plank");
		worker.recepteur.AddObject(plank);
		Scenario.Hire(quarry, worker);
		yield return t.UntilGame(() =>
		{
			if (worker.recepteur.Carries(plank))
			{
				t.True(QuarryGround(site) == before, worker.name + " dug a block with the plank in its bag: " + Describe(worker));
				return false;
			}
			return true;
		}, 120f, "the worker to put the plank down", 15f, () => t.Log(Describe(worker)));
		t.Equal(1, InStorage(new List<GameObject> { plank }), "Planks in a storage (not dropped)");
		t.Equal(before, QuarryGround(site), "Quarry blocks left when the plank was stored");
		yield return t.UntilGame(() => QuarryGround(site) < before, 120f, "the worker to dig in the quarry afterwards", 15f, () => t.Log(Describe(worker)));
	}

	[GameTest("Tasks", "Reset all bricktrons: a worker waiting after its dig order is let go of the wait and looks for work at once (#121)", TimeoutSeconds = 300f)]
	private static IEnumerator ResetAllBricktrons(TestContext t)
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
		XYZ voxel = new XYZ(site.x - 1, site.y - 1, site.z + 3);
		t.True(Voxel.IsFull(voxel), "The voxel to dig is not ground");
		OrderDig(worker, voxel);
		yield return t.UntilGame(() => !Voxel.IsFull(voxel), 60f, "the ordered voxel to be dug", 15f, () => t.Log(Describe(worker)));
		yield return t.UntilGame(() => CastleStoryPlus.WorkerAI.HoldAfterOrder.IsHeld(worker), 10f, "the worker to wait after the dig order");
		int count = CastleStoryPlus.WorkerAI.BricktronRecovery.ResetTeam(Scenario.Player);
		t.True(count >= 1, "The reset reached " + count + " bricktrons");
		t.True(!CastleStoryPlus.WorkerAI.HoldAfterOrder.IsHeld(worker), worker.name + " still waits for an order after the reset");
		t.True(worker.PendingTask == null, worker.name + " still has a queued task after the reset");
		yield return t.UntilGame(() => worker.CurrentTask != null && TaskProject(worker) != null, 40f, "the worker to take up work after the reset", 10f, () => t.Log(Describe(worker)));
		t.Log("After the reset: " + Describe(worker));
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

	[GameTest("Tasks", "With at most 12 bricktrons per task (TaskCrewSize, #88) a new quarry (keeping nothing) starts at 8 and takes 12: twelve builders put on it all join its crew (a thirteenth does not) and stay on it, more than 8 work on it at once, and a work limit of 5 cuts the crew down to 5", TimeoutSeconds = 240f, Exclusive = true)]
	private static IEnumerator TwelveOnOneQuarry(TestContext t)
	{
		int setting = CastleStoryPlus.WorkerAI.TaskCrewSize.Setting.Value;
		MineProject quarry = null;
		t.Cleanup(() =>
		{
			if (quarry != null)
			{
				CastleStoryPlus.WorkerAI.WorkLimit.Set(quarry, 0);
			}
			CastleStoryPlus.WorkerAI.TaskCrewSize.Set(setting);
		});
		CastleStoryPlus.WorkerAI.TaskCrewSize.Set(12);
		XYZ site = Scenario.Site(t, 20, 14);
		Scenario.LeavesMess(t);
		int top = site.y - 1;
		quarry = Quarry(t, new Vector3(site.x - 9, top - 1, site.z - 4), new Vector3(site.x - 2, top + 2, site.z + 3));
		// It keeps nothing, so its ground can be dug without a storage for the stone.
		quarry.GetComponent<QuarryData>().SetTypesToHarvest(0);
		t.Equal(8, quarry.MaximumCrewSize, "A new quarry's crew size");
		Labor[] crew = new Labor[13];
		for (int i = 0; i < crew.Length; i++)
		{
			crew[i] = Scenario.Bricktron(t, Occupation.Type.Builder, new XYZ(site.x - 9 + i % 8, site.y, site.z - 6 - i / 8 * 2));
		}
		yield return t.Frames(5);
		Labor[] twelve = new Labor[12];
		System.Array.Copy(crew, twelve, 12);
		Scenario.Hire(quarry, twelve);
		yield return t.Frames(2);
		t.Equal(12, quarry.CrewCount(), "Crew after putting 12 on the quarry");
		Scenario.Hire(quarry, crew[12]);
		yield return t.Frames(2);
		t.Equal(12, quarry.CrewCount(), "Crew after putting a thirteenth on it");
		t.True(crew[12].Project != quarry, "The thirteenth joined a full crew of 12");
		int most = 0;
		yield return t.UntilGame(() =>
		{
			t.Equal(12, quarry.CrewCount(), "The quarry's crew");
			int working = 0;
			foreach (Labor worker in twelve)
			{
				if (TaskProject(worker) == quarry)
				{
					working++;
				}
			}
			most = Mathf.Max(most, working);
			return most > 8;
		}, 120f, "more than 8 builders working on the quarry at once", 20f, () => t.Log("most working " + most + "; " + Describe(twelve[0]) + "; " + Describe(twelve[11])));
		t.Record("mostWorking", most);
		// "Workers at a time" steps up to the task's 12: All, then 12 going down; 12, then All going up.
		int stepMost = CastleStoryPlus.WorkerAI.WorkLimit.MostFor(quarry);
		t.Equal(12, stepMost, "Workers at a time goes up to");
		t.Equal(12, CastleStoryPlus.WorkerAI.WorkLimit.Next(0, -1, stepMost), "Workers at a time one step down from All");
		t.Equal(0, CastleStoryPlus.WorkerAI.WorkLimit.Next(12, 1, stepMost), "Workers at a time one step up from 12");
		t.Equal(10, CastleStoryPlus.WorkerAI.WorkLimit.Next(9, 1, stepMost), "Workers at a time one step up from 9");
		CastleStoryPlus.WorkerAI.WorkLimit.Set(quarry, 5);
		yield return t.Frames(2);
		t.Equal(5, quarry.CrewCount(), "Crew after a work limit of 5");
	}

	[GameTest("Tasks", "Eight builders put on an 8 x 8 quarry that keeps stone, with no storage for it, stay on it for a minute: none walks off to the gather point or to another quarry of higher priority next to it that can be dug (#86)", TimeoutSeconds = 300f)]
	private static IEnumerator FullCrewStaysOnBigQuarry(TestContext t)
	{
		XYZ site = Scenario.Site(t, 20, 14);
		Scenario.LeavesMess(t);
		int top = site.y - 1;
		// 8 x 8 and 2 deep on the west half, keeping stone with no storage anywhere (a full stockpile, as it often is):
		// none of its blocks may be dug now, but they are still the task's work. A small quarry of higher priority and no
		// crew on the east half keeps nothing, so it could be dug.
		MineProject quarry = Quarry(t, new Vector3(site.x - 9, top - 1, site.z - 4), new Vector3(site.x - 2, top + 2, site.z + 3));
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
		float end = Time.time + 60f;
		yield return t.UntilGame(() =>
		{
			foreach (Labor worker in crew)
			{
				t.True(worker.Project == quarry, worker.name + " left the quarry's crew" + ((worker.Project == other) ? " for the other quarry" : string.Empty) + ": " + Describe(worker) + "; the quarry has " + GoalCount(quarry) + " goals, alive " + quarry.IsStillAlive);
				t.True(TaskProject(worker) != other, worker.name + " works in the other quarry: " + Describe(worker));
				Vector3 at = worker.transform.position;
				float away = new Vector2(at.x - middle.x, at.z - middle.z).magnitude;
				farthest = Mathf.Max(farthest, away);
				// The gather point (the home crystal) is a plot or more away; 20 leaves room for a step aside round the quarry.
				t.AtMost(20f, away, worker.name + "'s distance from the quarry's middle (" + Describe(worker) + ")");
			}
			return Time.time >= end;
		}, 70f, "a minute on the quarry", 30f, () => t.Log("quarry " + ProjectEndpoints.Describe(quarry)));
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
		return GoalCount(quarry) == 0;
	}

	private static int GoalCount(Project project)
	{
		List<Goal> goals = new List<Goal>();
		foreach (IGoalProvider provider in project.GetGoalProviders())
		{
			provider?.GetAll(goals);
		}
		// A dug block's goal goes back to its pool a moment later: those no longer count.
		return goals.FindAll((Goal goal) => goal != null && !goal.IsNullOrReleased() && goal.IsValid()).Count;
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

	// The quarry's top layer (Quarry(t, site)) that is still ground.
	private static int QuarryGround(XYZ site)
	{
		int count = 0;
		for (int x = site.x + 2; x <= site.x + 5; x++)
		{
			for (int z = site.z - 2; z <= site.z + 1; z++)
			{
				if (Voxel.IsFull(new XYZ(x, site.y - 1, z)))
				{
					count++;
				}
			}
		}
		return count;
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
