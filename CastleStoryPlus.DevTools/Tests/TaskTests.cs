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
using CastleStoryPlus.DevTools.Building;
using CastleStoryPlus.DevTools.Endpoints;
using CastleStoryPlus.DevTools.Pathing;
using CastleStoryPlus.DevTools.Testing;
using UnityEngine;
using FactoryBlocks = Brix.External.Factories.Blocks;

namespace CastleStoryPlus.DevTools.Tests;

// The workers' tasks, each set up from scratch with the test's own bricktrons: cutting a tree on an order and in a
// harvest project, carrying loose items into a stockpile, digging in a quarry, and building blueprints with the
// material from a stockpile. Run on a map with trees (tools/run-game-tests.py starts a sandbox on Tortuga).
[ParallelTests("Economy")]
internal static class TaskTests
{
	internal static readonly Factory.AssetKey Pallet = new Factory.AssetKey("Blueprints", "Pallet");

	private static readonly Factory.AssetKey StoneBlueprint = new Factory.AssetKey("Blueprints", FactoryBlocks.Stone_Brick.Name);

	[GameTest("Tasks", "A builder ordered to cut a tree fells it: the tree is gone from the registry and logs lie there", TimeoutSeconds = 240f)]
	private static IEnumerator BuilderFellsTreeOnOrder(TestContext t)
	{
		XYZ tree = NearestTree(t, Scenario.Home(), 45);
		Labor builder = Scenario.Bricktron(t, Occupation.Type.Builder, StandNear(t, tree));
		yield return t.Frames(5);
		t.True(builder.Occupation.Profession.CanChopTrees(), builder.name + " cannot chop trees");
		int logsBefore = Scenario.Count(FactoryBlocks.Wood_DoubleLog_AA);
		Scenario.ProjectCommands.CmdAddTreeGoal(builder.gameObject, tree.ToVector3(), false);
		float started = Time.time;
		float end = Time.time + 180f;
		float nextLog = Time.time + 5f;
		int orders = 1;
		while (TreeRegistry.Instance.GetTree(tree) != 0)
		{
			if (Time.time >= nextLog)
			{
				nextLog = Time.time + 20f;
				DamagedTree damaged = DamagedTree.GetAt(tree);
				t.Log(builder.name + ": " + builder.Activity + ", " + Mathf.RoundToInt(Vector3.Distance(builder.transform.position, tree.ToVector3())) + " from the tree; tree hp " + ((damaged != null) ? damaged.Hp.ToString() : "full"));
				// An order that came to nothing (the builder idle, the tree untouched) is given again, as a player
				// clicks again; how many it took is recorded.
				if (damaged == null && builder.Activity == Activity.Idle && orders < 3)
				{
					orders++;
					Scenario.ProjectCommands.CmdAddTreeGoal(builder.gameObject, tree.ToVector3(), false);
				}
			}
			t.True(Time.time < end, "Waited 180 game seconds for the tree at " + tree + " to be felled");
			yield return null;
		}
		t.Record("gameSeconds", Mathf.Round(Time.time - started));
		t.Record("ordersSent", orders);
		yield return t.UntilGame(() => Scenario.Count(FactoryBlocks.Wood_DoubleLog_AA) > logsBefore, 15f, "the felled tree's logs");
		t.Record("logs", Scenario.Count(FactoryBlocks.Wood_DoubleLog_AA) - logsBefore);
		ReleaseLogsNear(t, tree);
	}

	[GameTest("Tasks", "A tree harvest project with two builders fells a tree in its circle", TimeoutSeconds = 300f)]
	private static IEnumerator HarvestProjectFellsTrees(TestContext t)
	{
		XYZ tree = NearestTree(t, Scenario.Home(), 45);
		HashSet<XYZ> inCircle = new HashSet<XYZ>(WorldEndpoints.TreesOver(tree.x - 3, tree.z - 3, tree.x + 3, tree.z + 3));
		HarvestProject project = Scenario.NewProject<HarvestProject>(t, () => Scenario.Groupe.CmdSpawnHarvestProject(tree.ToVector3(), Quaternion.identity, false, (int)NatureType.tree));
		ProjectEndpoints.Commit(project);
		XYZ stand = StandNear(t, tree);
		Labor a = Scenario.Bricktron(t, Occupation.Type.Builder, stand);
		Labor b = Scenario.Bricktron(t, Occupation.Type.Builder, stand + new XYZ(1, 0, 0));
		yield return t.Frames(5);
		Scenario.Hire(project, a, b);
		yield return t.Frames(2);
		t.True(a.Project == project && b.Project == project, "The builders are not on the harvest project");
		yield return t.UntilGame(() => Felled(inCircle) > 0, 240f, "a tree near the project's marker to be felled", 30f, () =>
		{
			t.Record("project", ProjectEndpoints.Describe(project));
			t.Log(Describe(a) + "; " + Describe(b));
		});
		t.Record("felled", Felled(inCircle));
		ReleaseLogsNear(t, tree);
	}

	[GameTest("Tasks", "A builder told to clean up carries 4 loose stones into an empty stockpile", TimeoutSeconds = 240f)]
	private static IEnumerator LooseItemsAreHauled(TestContext t)
	{
		XYZ site = Scenario.Site(t, 10, 6);
		Recepteur stockpile = null;
		BuildProject build = Scenario.BuildProject(t, site);
		yield return Scenario.Building(t, Pallet, site, build, made => stockpile = made.GetComponent<Recepteur>());
		t.NotNull(stockpile, "The stockpile's storage");
		int storedBefore = Scenario.Stored(RawResources.RawStone);
		Vector3 pile = new XYZ(site.x + 4, site.y, site.z).ToVector3();
		Scenario.Items(t, RawResources.RawStone, 4, pile);
		Labor builder = Scenario.Bricktron(t, Occupation.Type.Builder, new XYZ(site.x - 3, site.y, site.z));
		yield return t.Frames(5);
		Scenario.ProjectCommands.CmdAddCleanGoal(builder.gameObject, pile, null, false);
		yield return t.UntilGame(() => Scenario.Stored(RawResources.RawStone) >= storedBefore + 4, 180f, "the 4 stones to be stored");
		t.Record("inThisStockpile", stockpile.ContentDescription.Value(Adjectif.stones));
	}

	[GameTest("Tasks", "A quarry of 3 x 3 with two builders and a stockpile: the workers dig ground out of it", TimeoutSeconds = 300f)]
	private static IEnumerator QuarryIsDug(TestContext t)
	{
		XYZ site = Scenario.Site(t, 12, 6);
		// The dug ground stays a hole.
		Scenario.LeavesMess(t);
		BuildProject build = Scenario.BuildProject(t, site);
		yield return Scenario.Building(t, Pallet, new XYZ(site.x - 4, site.y, site.z), build, _ => { });
		// The quarry east of the stockpile: the top ground layer of 3 x 3 (the box reaches 2 above the ground, as the
		// quarry tool's does).
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
		t.True(ground.TrueForAll(v => Voxel.IsFull(v) && !Voxel.IsInvincible(v)), "The quarry's ground is not all diggable");
		MineProject quarry = Scenario.NewProject<MineProject>(t, () => Scenario.Groupe.CmdSpawnMineProject(min, Quaternion.identity, false));
		StairOrientation stairs = StairOrientation.Default;
		Scenario.Groupe.CmdSetMine(quarry.gameObject, min, max, stairs.corner.ToVector3(), stairs.direction.ToVector3(), false);
		QuarryData data = quarry.GetComponent<QuarryData>();
		t.NotNull(data, "The quarry's data");
		stairs = data.StairOrientation;
		Scenario.Groupe.CmdSetMine(quarry.gameObject, data.Bounds.min.ToVector3(), data.Bounds.max.ToVector3(), stairs.corner.ToVector3(), stairs.direction.ToVector3(), true);
		ProjectEndpoints.Commit(quarry);
		Labor a = Scenario.Bricktron(t, Occupation.Type.Builder, new XYZ(site.x, site.y, site.z - 2));
		Labor b = Scenario.Bricktron(t, Occupation.Type.Builder, new XYZ(site.x, site.y, site.z + 2));
		yield return t.Frames(5);
		Scenario.Hire(quarry, a, b);
		float end = Time.time + 240f;
		float nextNote = Time.time + 30f;
		while (Dug(ground) < 3)
		{
			if (Time.time >= nextNote || Time.time >= end)
			{
				nextNote = Time.time + 30f;
				t.Record("quarry", ProjectEndpoints.Describe(quarry));
				t.Record("digGoals", WhyNotDug(a, quarry));
				t.Log(Describe(a) + "; " + Describe(b) + "; dug " + Dug(ground));
			}
			t.True(Time.time < end, "Waited 240 game seconds for 3 voxels of the quarry to be dug");
			yield return null;
		}
		t.Record("dug", Dug(ground));
		t.Record("stoneStored", Scenario.Stored(RawResources.RawStone));
	}

	[GameTest("Tasks", "Two builders build 3 stone brick blueprints with the stone blocks in a stockpile", TimeoutSeconds = 300f)]
	private static IEnumerator WorkersBuildBlueprints(TestContext t)
	{
		XYZ site = Scenario.Site(t, 10, 6);
		BuildProject build = Scenario.BuildProject(t, site);
		Recepteur stockpile = null;
		yield return Scenario.Building(t, Pallet, new XYZ(site.x - 3, site.y, site.z), build, made => stockpile = made.GetComponent<Recepteur>());
		t.NotNull(stockpile, "The stockpile's storage");
		// A stone brick takes a stone block (Adjectif.stoneBlock: the loose Blocks.Stone_DoubleBrick a quarry yields),
		// not raw stone. Put straight into the stockpile (carrying is LooseItemsAreHauled's test).
		foreach (GameObject block in Scenario.Items(t, FactoryBlocks.Stone_DoubleBrick, 12, stockpile.transform.position + Vector3.up))
		{
			stockpile.AddObject(block);
		}
		Labor a = Scenario.Bricktron(t, Occupation.Type.Builder, new XYZ(site.x, site.y, site.z - 2));
		Labor b = Scenario.Bricktron(t, Occupation.Type.Builder, new XYZ(site.x, site.y, site.z + 2));
		yield return t.Frames(5);
		List<XYZ> wall = new List<XYZ> { new XYZ(site.x + 2, site.y, site.z - 1), new XYZ(site.x + 2, site.y, site.z), new XYZ(site.x + 2, site.y, site.z + 1) };
		List<Blueprint> blueprints = new List<Blueprint>();
		foreach (XYZ voxel in wall)
		{
			BlueprintPlacer.Result placed = BlueprintPlacer.Place(StoneBlueprint, voxel, 0, build, force: false);
			t.True(placed.Placed, "Stone brick blueprint at " + voxel + ": " + placed.Outcome + " " + placed.Detail);
			blueprints.Add(placed.Blueprint);
		}
		t.Cleanup(() =>
		{
			foreach (XYZ voxel in wall)
			{
				IBlockData block = Voxel.GetBlockData(voxel);
				if (block != null && !block.IsNull && Voxel.IsBlock(voxel))
				{
					block.RootOrSelf.DeBlock();
				}
			}
		});
		ProjectEndpoints.Commit(build);
		Scenario.Hire(build, a, b);
		float started = Time.time;
		float end = Time.time + 240f;
		float nextNote = Time.time + 30f;
		while (!wall.TrueForAll(v => Voxel.IsBlock(v)))
		{
			if (Time.time >= nextNote || Time.time >= end)
			{
				// What the blueprints and the project look like, kept with the result for a failed run.
				nextNote = Time.time + 30f;
				List<object> states = new List<object>();
				foreach (Blueprint blueprint in blueprints)
				{
					if (blueprint != null && !blueprint.Released)
					{
						states.Add(BlueprintEndpoints.Describe(blueprint, StoneBlueprint.Name, false));
					}
				}
				t.Record("blueprints", states);
				t.Record("project", ProjectEndpoints.Describe(build));
				t.Record("stoneBlocksInStockpile", stockpile.ContentDescription.Value(Adjectif.stoneBlock));
				t.Record("buildSearch", BuildSearchEndpoints.Explain(a, build));
				if (blueprints[0] != null && blueprints[0].recepteur != null)
				{
					List<string> takes = new List<string>();
					foreach (KeyValuePair<System.Type, Adjectif> pair in blueprints[0].recepteur.BaseCapacity.DicoAdjectif)
					{
						if (pair.Value != null && pair.Value.quantifiable != null && pair.Value.quantifiable.valeur > 0)
						{
							takes.Add(pair.Key.Name + " " + pair.Value.quantifiable.valeur);
						}
					}
					t.Record("blueprintTakes", takes);
				}
				t.Log(Describe(a) + "; " + Describe(b) + "; " + Scenario.Count(FactoryBlocks.Stone_DoubleBrick) + " stone blocks");
			}
			t.True(Time.time < end, "Waited 240 game seconds for the 3 blueprints to be built");
			yield return null;
		}
		t.Record("gameSeconds", Mathf.Round(Time.time - started));
	}

	[GameTest("Tasks", "A felled tree drops its 3 logs on the ground next to it", TimeoutSeconds = 240f)]
	private static IEnumerator FelledTreeDropsThreeLogs(TestContext t)
	{
		XYZ tree = NearestTree(t, Scenario.Home(), 45);
		Labor builder = Scenario.Bricktron(t, Occupation.Type.Builder, StandNear(t, tree));
		yield return t.Frames(5);
		int logsBefore = LogsNear(tree);
		Scenario.ProjectCommands.CmdAddTreeGoal(builder.gameObject, tree.ToVector3(), false);
		yield return t.UntilGame(() => TreeRegistry.Instance.GetTree(tree) == 0, 180f, "the tree at " + tree + " to be felled", 30f, () => t.Log(Describe(builder)));
		yield return t.UntilGame(() => LogsNear(tree) >= logsBefore + 3, 20f, "3 logs by the stump");
		t.Record("logs", LogsNear(tree) - logsBefore);
		ReleaseLogsNear(t, tree);
	}

	[GameTest("Tasks", "A builder told to clean up carries 3 loose lumps of iron ore into an empty stockpile", TimeoutSeconds = 240f)]
	private static IEnumerator LooseIronIsHauled(TestContext t)
	{
		XYZ site = Scenario.Site(t, 10, 6);
		BuildProject build = Scenario.BuildProject(t, site);
		yield return Scenario.Building(t, Pallet, site, build, _ => { });
		int storedBefore = Scenario.Stored(RawResources.RawIron);
		Vector3 pile = new XYZ(site.x + 4, site.y, site.z).ToVector3();
		Scenario.Items(t, RawResources.RawIron, 3, pile);
		Labor builder = Scenario.Bricktron(t, Occupation.Type.Builder, new XYZ(site.x - 3, site.y, site.z));
		yield return t.Frames(5);
		Scenario.ProjectCommands.CmdAddCleanGoal(builder.gameObject, pile, null, false);
		yield return t.UntilGame(() => Scenario.Stored(RawResources.RawIron) >= storedBefore + 3, 180f, "the 3 lumps of iron ore to be stored", 30f, () => t.Log(Describe(builder)));
	}

	[GameTest("Tasks", "A deleted project lets its bricktrons go: they are on no project (idle) afterwards", TimeoutSeconds = 60f)]
	private static IEnumerator DeletedProjectReleasesCrew(TestContext t)
	{
		XYZ site = Scenario.Site(t, 8, 6);
		HarvestProject project = Scenario.NewProject<HarvestProject>(t, () => Scenario.Groupe.CmdSpawnHarvestProject(site.ToVector3(), Quaternion.identity, false, (int)NatureType.tree));
		ProjectEndpoints.Commit(project);
		Labor a = Scenario.Bricktron(t, Occupation.Type.Builder, new XYZ(site.x - 2, site.y, site.z));
		Labor b = Scenario.Bricktron(t, Occupation.Type.Builder, new XYZ(site.x + 2, site.y, site.z));
		yield return t.Frames(5);
		Scenario.Hire(project, a, b);
		yield return t.Frames(2);
		t.True(a.Project == project && b.Project == project, "The builders are not on the project");
		Scenario.Groupe.CmdDelete(project.gameObject);
		yield return t.Until(() => a.Project != project && b.Project != project, 5f, "the builders to leave the deleted project");
		t.True(a.Project == null || a.Project is IdleProject, a.name + " went to a " + a.Project?.GetType().Name);
		t.True(b.Project == null || b.Project is IdleProject, b.name + " went to a " + b.Project?.GetType().Name);
	}

	[GameTest("Tasks", "Hiring three bricktrons onto a project with room for one: the crew size grows to take them all", TimeoutSeconds = 60f)]
	private static IEnumerator HiringGrowsFullCrew(TestContext t)
	{
		XYZ site = Scenario.Site(t, 8, 6);
		BuildProject project = Scenario.BuildProject(t, site);
		for (int i = 0; i < 8 && project.MaximumCrewSize > 1; i++)
		{
			Scenario.Groupe.CmdRemoveProjectCapacity(project.gameObject);
		}
		t.Equal(1, project.MaximumCrewSize, "Crew size before hiring");
		Labor[] crew = new Labor[3];
		for (int i = 0; i < 3; i++)
		{
			crew[i] = Scenario.Bricktron(t, Occupation.Type.Builder, new XYZ(site.x - 2 + i * 2, site.y, site.z));
		}
		yield return t.Frames(5);
		Scenario.Hire(project, crew);
		yield return t.Frames(2);
		t.Record("crewSize", project.MaximumCrewSize);
		foreach (Labor labor in crew)
		{
			t.True(labor.Project == project, labor.name + " is not on the project");
		}
		t.AtLeast(3f, project.MaximumCrewSize, "Crew size after hiring 3");
	}

	[GameTest("Tasks", "A project's priority goes up and down with the task bar's buttons", TimeoutSeconds = 30f)]
	private static IEnumerator PriorityRaisedAndLowered(TestContext t)
	{
		XYZ site = Scenario.Site(t, 8, 6);
		BuildProject project = Scenario.BuildProject(t, site);
		yield return t.Frames(2);
		ProjectPriority.Bias start = project.PriorityBias;
		t.Record("start", start.ToString());
		Scenario.Groupe.CmdRaisePriority(project.gameObject);
		yield return t.Frames(2);
		ProjectPriority.Bias raised = project.PriorityBias;
		t.Record("raised", raised.ToString());
		t.True(raised != start || start == ProjectPriority.Bias.High, "Raising the priority changed nothing (" + start + ")");
		Scenario.Groupe.CmdLowerPriority(project.gameObject);
		yield return t.Frames(2);
		t.Equal(start, project.PriorityBias, "Priority after raising and lowering again");
	}

	// ---- helpers

	// The tree nearest to the point with ground next to it a bricktron can stand on.
	internal static XYZ NearestTree(TestContext t, XYZ near, int radius)
	{
		XYZ best = XYZ.zero;
		int bestDistance = int.MaxValue;
		foreach (XYZ tree in WorldEndpoints.TreesOver(near.x - radius, near.z - radius, near.x + radius, near.z + radius))
		{
			// FlatDistance is squared.
			int distance = Mathf.RoundToInt(Mathf.Sqrt(WorldEndpoints.FlatDistance(tree, near)));
			if (distance >= 6 && distance < bestDistance && TreeRegistry.Instance.GetTree(tree) != 0 && DamagedTree.GetAt(tree) == null)
			{
				best = tree;
				bestDistance = distance;
			}
		}
		if (bestDistance == int.MaxValue)
		{
			t.Skip("No tree within " + radius + " voxels of " + near + " (run on a map with trees)");
		}
		t.Log("Tree at " + best + ", " + bestDistance + " voxels away");
		return best;
	}

	// Open ground 2-4 voxels from the point.
	internal static XYZ StandNear(TestContext t, XYZ point)
	{
		for (int r = 2; r <= 6; r++)
		{
			foreach (XYZ step in new[] { new XYZ(r, 0, 0), new XYZ(-r, 0, 0), new XYZ(0, 0, r), new XYZ(0, 0, -r) })
			{
				XYZ ground = Scenario.Ground(point.x + step.x, point.z + step.z);
				// A voxel of the route graph: a unit spawned off it cannot walk anywhere (PathInvalid).
				if (Mathf.Abs(ground.y - point.y) <= 3 && Voxel.IsOpen(ground) && Voxel.IsOpen(ground + new XYZ(0, 1, 0)) && PathProbe.Ground(ground.x, ground.z, out XYZ walkable) && walkable == ground)
				{
					return ground;
				}
			}
		}
		t.Skip("No open ground next to " + point);
		return XYZ.zero;
	}

	// For each dig goal of the quarry: whether the game would give it to the worker, and if not why
	// (DigGoal.IsMatchFor's error, the reachability), as the worker's own search asks.
	private static List<string> WhyNotDug(Labor labor, Project quarry)
	{
		List<string> result = new List<string>();
		foreach (DigGoal goal in quarry.goalParent.GetComponentsInChildren<DigGoal>())
		{
			AIErrorsStore errors = new AIErrorsStore();
			float time = 0f;
			bool unmapped = false;
			bool match = goal.IsMatchFor(labor, errors, ref time, ref unmapped);
			string why = match ? "match" : ((errors.GetMostImportantError(labor) != null) ? errors.GetMostImportantError(labor).ToString() : "no");
			result.Add(XYZ.FromVector3(goal.transform.position) + ": " + why + ", reach " + Knowledge.Instance.CanReach(labor, goal) + ", valid " + goal.IsValid() + ", other worker " + goal.HasOtherWorkerAssigned(labor) + ", could store " + goal.CouldStore(labor) + ", bag full " + labor.recepteur.IsFull() + ", breaks voxels " + labor.Occupation.Profession.CanBreakVoxels());
			if (result.Count >= 10)
			{
				break;
			}
		}
		return result;
	}

	private static string Describe(Labor labor)
	{
		if (!Scenario.Alive(labor))
		{
			return "(gone)";
		}
		return labor.name + ": " + labor.Activity + " at " + Units.VoxelOf(labor) + " on " + ((labor.Project != null) ? labor.Project.GetType().Name : "nothing") + (labor.recepteur.IsEmpty() ? string.Empty : ", carrying " + labor.recepteur.ContentString());
	}

	private static int Felled(HashSet<XYZ> trees)
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

	private static int LogsNear(XYZ tree)
	{
		int count = 0;
		foreach (FactoryImprint imprint in Object.FindObjectsOfType<FactoryImprint>())
		{
			if (imprint != null && Scenario.Is(imprint.gameObject, FactoryBlocks.Wood_DoubleLog_AA) && Vector3.Distance(imprint.transform.position, tree.ToVector3()) < 10f)
			{
				count++;
			}
		}
		return count;
	}

	// The loose logs a felled tree left are taken away again by the clean-up.
	private static void ReleaseLogsNear(TestContext t, XYZ tree)
	{
		t.Cleanup(() =>
		{
			foreach (FactoryImprint imprint in Object.FindObjectsOfType<FactoryImprint>())
			{
				if (imprint != null && Scenario.Is(imprint.gameObject, FactoryBlocks.Wood_DoubleLog_AA) && imprint.transform.parent == null && Vector3.Distance(imprint.transform.position, tree.ToVector3()) < 12f)
				{
					Scenario.Release(imprint.gameObject);
				}
			}
		});
	}
}
