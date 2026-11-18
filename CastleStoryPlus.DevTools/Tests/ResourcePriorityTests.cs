using System.Collections;
using System.Collections.Generic;
using Brix.Engine;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Beacons;
using Brix.Game.AI.KnowledgeSpace;
using Brix.Game.Components;
using Brix.Game.Semantique;
using Brix.Input;
using CastleStoryPlus.DevTools.Building;
using CastleStoryPlus.DevTools.Endpoints;
using CastleStoryPlus.DevTools.Testing;
using CastleStoryPlus.WorkerAI;
using UnityEngine;
using FactoryBlocks = Brix.External.Factories.Blocks;

namespace CastleStoryPlus.DevTools.Tests;

// Resource priorities (Features.ResourcePriority): where workers choose between resources, the one of the higher
// priority is taken before the nearer one of a lower priority, and one at 0 is never taken. Each test sets the
// priorities of the player's team itself and puts them back (the tests of this class run alone: the setting is
// shared by every worker of the team).
internal static class ResourcePriorityTests
{
	private static int KindIndex(string id)
	{
		ResourcePriority.Kind[] kinds = ResourcePriority.Kinds;
		for (int i = 0; i < kinds.Length; i++)
		{
			if (kinds[i].Id == id)
			{
				return i;
			}
		}
		return -1;
	}

	// The player's priority for a kind (host), put back to neutral when the test ends.
	private static void Priority(TestContext t, string id, int priority)
	{
		int kind = KindIndex(id);
		t.True(kind >= 0, "No resource kind '" + id + "' in the priority table");
		ResourcePriority.Set(Scenario.Player, kind, priority);
		t.Cleanup(() => ResourcePriority.Set(Scenario.Player, kind, ResourcePriority.Neutral));
	}

	// The goals as a clean-up or harvest area orders them for the worker (a copy).
	private static List<Goal> Ordered(Labor labor, List<Goal> goals)
	{
		List<Goal> copy = new List<Goal>(goals);
		ResourcePrioritySelect.Order(labor, copy);
		return copy;
	}

	// The first thing in the worker's hands, by its kind in the priority table (null: nothing yet).
	private static GameObject FirstCarried(Labor labor)
	{
		foreach (IDescriptor item in labor.recepteur.StoredItems)
		{
			if (item != null && item.GameObject != null)
			{
				return item.GameObject;
			}
		}
		return null;
	}

	private static bool IsKind(GameObject go, string id)
	{
		return go != null && ResourcePriority.KindOf(Apparence.PrincipaleOf(go)) == KindIndex(id);
	}

	[GameTest("ResourcePriority", "A resource's cost is its distance over the weight of its priority: 5 beats 3 beyond 2.5 times the distance, 1 only when 2.5 times as near, 0 never", NeedsHostGame = false)]
	private static IEnumerator CostFollowsPriority(TestContext t)
	{
		t.Equal(ResourcePriority.Cost(10f, 3), 10f, "Neutral priority costs the distance");
		t.True(ResourcePriority.Cost(20f, 5) < ResourcePriority.Cost(10f, 3), "A resource at 5 twice as far did not beat one at 3");
		t.True(ResourcePriority.Cost(30f, 5) > ResourcePriority.Cost(10f, 3), "A resource at 5 three times as far beat one at 3");
		t.True(ResourcePriority.Cost(10f, 1) > ResourcePriority.Cost(20f, 3), "A resource at 1 beat one at 3 twice as far");
		t.Equal(float.MaxValue, ResourcePriority.Cost(1f, 0), "A resource at 0 costs");
		for (int p = 1; p < 5; p++)
		{
			t.True(ResourcePriority.Cost(10f, p) > ResourcePriority.Cost(10f, p + 1), "Priority " + p + " is not dearer than " + (p + 1));
		}
		yield break;
	}

	[GameTest("ResourcePriority", "A cleanup area's goals for a log and a stone at the same distance: the higher priority first (both ways), a resource at 0 left out, and the game's priority number agrees", TimeoutSeconds = 120f)]
	private static IEnumerator CleanupGoalsFollowPriority(TestContext t)
	{
		XYZ site = Scenario.Site(t, 12, 8);
		Labor builder = Scenario.Bricktron(t, Occupation.Type.Builder, new XYZ(site.x, site.y, site.z - 2));
		builder.Autonomy = AutonomyStatus.WaitForOrders;
		GameObject log = Scenario.Items(t, FactoryBlocks.Wood_DoubleLog_AA, 1, new XYZ(site.x + 3, site.y, site.z + 1).ToVector3())[0];
		GameObject stone = Scenario.Items(t, RawResources.RawStone, 1, new XYZ(site.x - 3, site.y, site.z + 1).ToVector3())[0];
		yield return t.Seconds(2f);
		t.True(IsKind(log, "wood"), "The log is not wood to the priority table");
		t.True(IsKind(stone, "stone"), "The stone is not stone to the priority table");
		CleanUpGoal logGoal = log.GetOrAddComponent<CleanUpGoal>();
		CleanUpGoal stoneGoal = stone.GetOrAddComponent<CleanUpGoal>();
		List<Goal> goals = new List<Goal> { stoneGoal, logGoal };

		Priority(t, "wood", 5);
		Priority(t, "stone", 1);
		List<Goal> ordered = Ordered(builder, goals);
		t.Equal(2, ordered.Count, "Goals ordered");
		t.True(ordered[0] == logGoal, "Wood at 5 and stone at 1: the stone came first");
		float time = 0f;
		t.True(logGoal.PriorityFor(builder, ref time) > stoneGoal.PriorityFor(builder, ref time), "The game's priority number of the log (5) is not above the stone's (1)");

		Priority(t, "wood", 1);
		Priority(t, "stone", 5);
		ordered = Ordered(builder, goals);
		t.True(ordered[0] == stoneGoal, "Stone at 5 and wood at 1: the log came first");
		t.True(stoneGoal.PriorityFor(builder, ref time) > logGoal.PriorityFor(builder, ref time), "The game's priority number of the stone (5) is not above the log's (1)");

		Priority(t, "stone", 0);
		ordered = Ordered(builder, goals);
		t.Equal(1, ordered.Count, "Goals with the stone at 0");
		t.True(ordered[0] == logGoal, "The stone at 0 was not left out");
		t.Equal(int.MinValue, stoneGoal.PriorityFor(builder, ref time), "The game's priority number of a stone at 0");

		// Neutral again: nothing changes, both are there.
		Priority(t, "stone", 3);
		Priority(t, "wood", 3);
		t.Equal(false, ResourcePriority.Customised(builder.faction), "The team is customised with every resource at 3");
	}

	[GameTest("ResourcePriority", "An idle builder's tidying takes the loose item of the higher priority first (a log or a stone at the same distance, both ways) and never one at 0", TimeoutSeconds = 180f)]
	private static IEnumerator IdleCleanupFollowsPriority(TestContext t)
	{
		XYZ site = Scenario.Site(t, 12, 8, 4, away: 30);
		Recepteur stockpile = null;
		BuildProject build = Scenario.BuildProject(t, site);
		yield return Scenario.Building(t, TaskTests.Pallet, new XYZ(site.x, site.y, site.z + 3), build, made => stockpile = made.GetComponent<Recepteur>());
		t.NotNull(stockpile, "The stockpile's storage");
		Labor builder = Scenario.Bricktron(t, Occupation.Type.Builder, new XYZ(site.x, site.y, site.z - 3));
		builder.Autonomy = AutonomyStatus.WaitForOrders;
		// Level with each other, the same distance from the builder, clear of the stockpile.
		GameObject log = Scenario.Items(t, FactoryBlocks.Wood_DoubleLog_AA, 1, new XYZ(site.x + 3, site.y, site.z - 1).ToVector3())[0];
		GameObject bricks = Scenario.Items(t, FactoryBlocks.Stone_DoubleBrick, 1, new XYZ(site.x - 3, site.y, site.z - 1).ToVector3())[0];
		yield return t.Seconds(2f);
		if (BaseJobs.CoversCleanup(builder.faction, log.transform.position))
		{
			t.Skip("The base's cleanup task covers the site: the idle chore is not asked");
			yield break;
		}
		// The idle cleanup's item list is renewed every 5 s.
		yield return t.Seconds(6f);
		Priority(t, "wood", 5);
		Priority(t, "bricks", 1);
		foreach (KeyValuePair<GameObject, string> entry in AutoCleanup.Explain(builder))
		{
			t.Log(entry.Key.name + " at " + entry.Key.transform.position + ": " + (entry.Value ?? "would pick it up"));
		}
		GameObject planned = AutoCleanup.TryPlan(builder);
		AutoCleanup.Release(builder);
		if (planned == null)
		{
			t.Skip("The idle cleanup considers neither item (outside its reach of the base at " + site + ")");
			yield break;
		}
		t.True(planned == log, "Wood at 5 and bricks at 1: the idle cleanup chose " + planned.name);

		Priority(t, "wood", 1);
		Priority(t, "bricks", 5);
		planned = AutoCleanup.TryPlan(builder);
		AutoCleanup.Release(builder);
		t.True(planned == bricks, "Bricks at 5 and wood at 1: the idle cleanup chose " + ((planned != null) ? planned.name : "nothing"));

		Priority(t, "bricks", 0);
		planned = AutoCleanup.TryPlan(builder);
		AutoCleanup.Release(builder);
		t.True(planned != bricks, "The idle cleanup chose the bricks at 0");
	}

	[GameTest("ResourcePriority", "A quarry's workers leave the ground alone while stone is at 0, and dig it when it is back at 3", TimeoutSeconds = 420f)]
	private static IEnumerator QuarryLeavesStoneAtZero(TestContext t)
	{
		XYZ site = Scenario.Site(t, 12, 6);
		Scenario.LeavesMess(t);
		BuildProject build = Scenario.BuildProject(t, site);
		yield return Scenario.Building(t, TaskTests.Pallet, new XYZ(site.x - 4, site.y, site.z), build, _ => { });
		int top = site.y - 1;
		Vector3 min = new Vector3(site.x + 2, top, site.z - 1);
		Vector3 max = new Vector3(site.x + 4, top + 2, site.z + 1);
		List<XYZ> ground = new List<XYZ>();
		for (int x = (int)min.x; x <= (int)max.x; x++)
		{
			for (int z = (int)min.z; z <= (int)max.z; z++)
			{
				ground.Add(new XYZ(x, top, z));
			}
		}
		MineProject quarry = Scenario.NewProject<MineProject>(t, () => Scenario.Groupe.CmdSpawnMineProject(min, Quaternion.identity, false));
		StairOrientation stairs = StairOrientation.Default;
		Scenario.Groupe.CmdSetMine(quarry.gameObject, min, max, stairs.corner.ToVector3(), stairs.direction.ToVector3(), false);
		QuarryData data = quarry.GetComponent<QuarryData>();
		t.NotNull(data, "The quarry's data");
		stairs = data.StairOrientation;
		Scenario.Groupe.CmdSetMine(quarry.gameObject, data.Bounds.min.ToVector3(), data.Bounds.max.ToVector3(), stairs.corner.ToVector3(), stairs.direction.ToVector3(), true);
		ProjectEndpoints.Commit(quarry);
		Priority(t, "stone", 0);
		Labor a = Scenario.Bricktron(t, Occupation.Type.Builder, new XYZ(site.x, site.y, site.z - 2));
		Labor b = Scenario.Bricktron(t, Occupation.Type.Builder, new XYZ(site.x, site.y, site.z + 2));
		yield return t.Frames(5);
		Scenario.Hire(quarry, a, b);
		yield return t.Seconds(40f);
		t.Equal(0, Dug(ground), "Voxels of the quarry dug in 40 game seconds with stone at 0");
		Priority(t, "stone", 3);
		yield return t.UntilGame(() => Dug(ground) >= 1, 240f, "a voxel of the quarry to be dug once stone is back at 3", 30f, () =>
		{
			t.Record("quarry", ProjectEndpoints.Describe(quarry));
			t.Record("digGoals", TaskTests.WhyNotDug(a, quarry));
			t.Log(a.name + " " + a.Activity + " autonomy " + a.Autonomy + " on " + ((a.Project != null) ? a.Project.GetType().Name + (a.Project == quarry ? " (the quarry)" : string.Empty) : "nothing") + " task " + ((a.CurrentTask != null) ? a.CurrentTask.ToString() : "none") + " at " + CastleStoryPlus.DevTools.Pathing.Units.VoxelOf(a) + ", " + b.name + " " + b.Activity + ", customised " + ResourcePriority.Customised(a.faction) + ", stone " + ResourcePriority.Get(a.faction, KindIndex("stone")));
		});
		t.Record("dug", Dug(ground));
	}

	// The real decision path: a worker hired on a clean-up task chooses between a log and a stone brick lying at the
	// same distance, through the game's own goal selection.
	private static IEnumerator CleanupTask(TestContext t, string higher, bool zeroTheOther)
	{
		XYZ site = Scenario.Site(t, 12, 8, 4, away: 30);
		Recepteur stockpile = null;
		BuildProject build = Scenario.BuildProject(t, site);
		yield return Scenario.Building(t, TaskTests.Pallet, new XYZ(site.x, site.y, site.z + 3), build, made => stockpile = made.GetComponent<Recepteur>());
		t.NotNull(stockpile, "The stockpile's storage");
		GameObject log = Scenario.Items(t, FactoryBlocks.Wood_DoubleLog_AA, 1, new XYZ(site.x + 3, site.y, site.z - 1).ToVector3())[0];
		GameObject bricks = Scenario.Items(t, FactoryBlocks.Stone_DoubleBrick, 1, new XYZ(site.x - 3, site.y, site.z - 1).ToVector3())[0];
		if (BaseJobs.CoversCleanup(Scenario.Player, log.transform.position))
		{
			t.Skip("The base's cleanup task covers the site");
			yield break;
		}
		string other = (higher == "wood") ? "bricks" : "wood";
		Priority(t, higher, 5);
		Priority(t, other, zeroTheOther ? 0 : 1);
		CleanupProject project = Scenario.NewProject<CleanupProject>(t, () => Scenario.Groupe.CmdSpawnGoupe("Groupes", "Cleanup", new XYZ(site.x, site.y, site.z - 1).ToVector3(), Quaternion.identity, false));
		BeaconDriver driver = project.GetComponent<BeaconDriver>();
		driver.Beacon.radius = 8;
		driver.NetworkNetworkRadius = 8;
		ProjectEndpoints.Commit(project);
		// Both items are goals of the area before the worker chooses (the area finds them one after the other).
		yield return t.UntilGame(() => project.GoalCounts() >= 2, 30f, "both items to be goals of the clean-up area");
		Labor builder = Scenario.Bricktron(t, Occupation.Type.Builder, new XYZ(site.x, site.y, site.z - 3));
		yield return t.Frames(5);
		Scenario.Hire(project, builder);
		GameObject wanted = (higher == "wood") ? log : bricks;
		GameObject left = (higher == "wood") ? bricks : log;
		GameObject first = null;
		yield return t.UntilGame(() =>
		{
			first = FirstCarried(builder);
			return first != null;
		}, 120f, "the clean-up worker to pick something up");
		t.True(first == wanted, higher + " at 5 and " + other + " at " + (zeroTheOther ? 0 : 1) + ": the clean-up worker took " + first.name + " first");
		if (zeroTheOther)
		{
			// Everything the worker will take is in the stockpile; the one at 0 is left where it lies.
			yield return t.UntilGame(() => FirstCarried(builder) == null && Scenario.Stored(Is(wanted)) >= 1, 120f, "the item at 5 to be stored");
			yield return t.Seconds(30f);
			t.True(left.activeInHierarchy && (left.transform.parent == null || left.transform.parent.GetComponentInParent<Recepteur>() == null), "The item at 0 was picked up and stored");
		}
	}

	private static Factory.AssetKey Is(GameObject go)
	{
		return go.GetComponent<FactoryImprint>().AssetKey;
	}

	[GameTest("ResourcePriority", "A worker on a clean-up task takes a log before a stone brick at the same distance when wood is at 5 and bricks at 1", TimeoutSeconds = 240f)]
	private static IEnumerator CleanupTaskTakesWoodFirst(TestContext t)
	{
		return CleanupTask(t, "wood", false);
	}

	[GameTest("ResourcePriority", "A worker on a clean-up task takes the stone brick first when bricks are at 5 and wood at 1", TimeoutSeconds = 240f)]
	private static IEnumerator CleanupTaskTakesBricksFirst(TestContext t)
	{
		return CleanupTask(t, "bricks", false);
	}

	[GameTest("ResourcePriority", "A worker on a clean-up task never takes the stone brick while bricks are at 0, and still takes the log", TimeoutSeconds = 300f)]
	private static IEnumerator CleanupTaskLeavesZero(TestContext t)
	{
		return CleanupTask(t, "wood", true);
	}

	// The real fetch of a workshop: a workbench with a Bow ordered (a plank and a rope) fetches from the stockpile.
	private static IEnumerator BowFetch(TestContext t, int planks, bool expectPlanksFirst, bool expectNoPlanks)
	{
		XYZ site = Scenario.Site(t, 12, 6);
		Recepteur stockpile = null;
		CraftingStation station = null;
		BuildProject build = Scenario.BuildProject(t, site);
		yield return Scenario.Building(t, TaskTests.Pallet, new XYZ(site.x - 4, site.y, site.z), build, go => stockpile = go.GetComponent<Recepteur>());
		yield return Scenario.Building(t, Blueprints.Workbench, new XYZ(site.x + 2, site.y, site.z), build, go => station = go.GetComponent<CraftingStation>());
		t.NotNull(stockpile, "The stockpile's storage");
		t.NotNull(station, "The workbench's crafting station");
		foreach (GameObject item in Scenario.Items(t, FactoryBlocks.Wood_DoublePlank_AA, 1, stockpile.transform.position + Vector3.up))
		{
			stockpile.AddObject(item);
		}
		foreach (GameObject item in Scenario.Items(t, BuildResources.Rope, 1, stockpile.transform.position + Vector3.up))
		{
			stockpile.AddObject(item);
		}
		Priority(t, "planks", planks);
		Labor worker = Scenario.Bricktron(t, Occupation.Type.Builder, new XYZ(site.x, site.y, site.z - 2));
		yield return t.Frames(5);
		Scenario.ProjectCommands.CmdCraftingAssignWorker(station.gameObject, worker.gameObject);
		yield return t.Frames(2);
		t.True(station.Operator == worker, "The worker is not the workbench's operator");
		Scenario.ProjectCommands.CmdcraftingQueueRecipe(station.gameObject, CookBook.Bow.id, 1);
		GameObject first = null;
		if (expectNoPlanks)
		{
			// The rope is fetched; the plank at 0 never is.
			yield return t.Seconds(40f);
			t.Equal(1, stockpile.ContentDescription.Value(Adjectif.plankBlock), "Planks left in the stockpile with planks at 0");
			yield break;
		}
		yield return t.UntilGame(() =>
		{
			first = FirstCarried(worker);
			return first != null;
		}, 120f, "the workbench's worker to fetch something");
		bool planksFirst = IsKind(first, "planks");
		t.True(planksFirst == expectPlanksFirst, "Planks at " + planks + ": the worker fetched " + first.name + " first");
	}

	[GameTest("ResourcePriority", "A workbench's worker fetches the plank before the rope for a bow when planks are at 5 (rope neutral)", TimeoutSeconds = 240f)]
	private static IEnumerator WorkbenchFetchesPlankFirst(TestContext t)
	{
		return BowFetch(t, 5, true, false);
	}

	[GameTest("ResourcePriority", "A workbench's worker fetches the rope before the plank for a bow when planks are at 1 (rope neutral)", TimeoutSeconds = 240f)]
	private static IEnumerator WorkbenchFetchesRopeFirst(TestContext t)
	{
		return BowFetch(t, 1, false, false);
	}

	[GameTest("ResourcePriority", "A workbench's worker never fetches the plank for a bow while planks are at 0", TimeoutSeconds = 240f)]
	private static IEnumerator WorkbenchLeavesPlanksAtZero(TestContext t)
	{
		return BowFetch(t, 0, false, true);
	}

	[GameTest("ResourcePriority", "Blueprint materials keep the game's choice: bricks at 0 still build a stone brick blueprint", TimeoutSeconds = 300f)]
	private static IEnumerator BuildingIgnoresPriorities(TestContext t)
	{
		XYZ site = Scenario.Site(t, 10, 6);
		BuildProject build = Scenario.BuildProject(t, site);
		Recepteur stockpile = null;
		yield return Scenario.Building(t, TaskTests.Pallet, new XYZ(site.x - 3, site.y, site.z), build, made => stockpile = made.GetComponent<Recepteur>());
		foreach (GameObject block in Scenario.Items(t, FactoryBlocks.Stone_DoubleBrick, 2, stockpile.transform.position + Vector3.up))
		{
			stockpile.AddObject(block);
		}
		Priority(t, "bricks", 0);
		Labor a = Scenario.Bricktron(t, Occupation.Type.Builder, new XYZ(site.x, site.y, site.z - 2));
		yield return t.Frames(5);
		XYZ voxel = new XYZ(site.x + 2, site.y, site.z);
		BlueprintPlacer.Result placed = BlueprintPlacer.Place(new Factory.AssetKey("Blueprints", FactoryBlocks.Stone_Brick.Name), voxel, 0, build, force: false);
		t.True(placed.Placed, "Stone brick blueprint: " + placed.Outcome + " " + placed.Detail);
		t.Cleanup(() =>
		{
			IBlockData block = Voxel.GetBlockData(voxel);
			if (block != null && !block.IsNull && Voxel.IsBlock(voxel))
			{
				block.RootOrSelf.DeBlock();
			}
		});
		ProjectEndpoints.Commit(build);
		Scenario.Hire(build, a);
		yield return t.UntilGame(() => Voxel.IsBlock(voxel), 200f, "the stone brick to be built with bricks at 0");
	}

	private static int Dug(List<XYZ> voxels)
	{
		int dug = 0;
		foreach (XYZ voxel in voxels)
		{
			if (Voxel.IsEmpty(voxel))
			{
				dug++;
			}
		}
		return dug;
	}
}
