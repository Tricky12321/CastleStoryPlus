using System.Collections;
using System.Collections.Generic;
using Brix.Engine;
using Brix.External.Factories;
using Brix.Game.AI;
using Brix.Game.AI.Damage;
using Brix.Game.Components;
using Brix.Game.Semantique;
using Brix.Input;
using Brix.Legacy;
using Brix.Lifecycle.Pooling;
using CastleStoryPlus.DevTools.Building;
using CastleStoryPlus.DevTools.Endpoints;
using CastleStoryPlus.DevTools.Pathing;
using CastleStoryPlus.DevTools.Testing;
using UnityEngine;
using FactoryBlocks = Brix.External.Factories.Blocks;

namespace CastleStoryPlus.DevTools.Tests;

// Stockpiles, building and repairs: the stockpile wheel's "accept only" dark crystal button, a damaged door repaired
// by a repair zone, a quarry's stair cut without gaps and kept from other digging, and blueprints built floor by floor.
[ParallelTests("Economy")]
internal static class BuildRepairTests
{
	private static readonly Factory.AssetKey StoneBlueprint = new Factory.AssetKey("Blueprints", FactoryBlocks.Stone_Brick.Name);

	[GameTest("Stockpiles", "The stockpile wheel has an 'accept only dark crystal' button and its command makes the stockpile take only dark crystal", TimeoutSeconds = 60f)]
	private static IEnumerator StockpileAcceptsOnlyDarkCrystal(TestContext t)
	{
		t.True(PieContenu.boutons.ContainsKey(Economy.DarkCrystalStockpileWheel.Button), "The wheel files have no " + Economy.DarkCrystalStockpileWheel.Button + " button");
		t.True(Instructions.commendes.ContainsKey("Event_Stockpile_Exclude_PurifiedBlueCrystal"), "No command for the dark crystal button");
		XYZ site = Scenario.Site(t, 6, 6);
		BuildProject build = Scenario.BuildProject(t, site);
		Recepteur stockpile = null;
		yield return Scenario.Building(t, TaskTests.Pallet, site, build, made => stockpile = made.GetComponent<Recepteur>());
		t.NotNull(stockpile, "The stockpile's storage");
		MonoBehaviour[] subjects = Instructions.sujets;
		object target = Instructions.objet;
		try
		{
			Instructions.sujets = new MonoBehaviour[0];
			Instructions.objet = stockpile.gameObject;
			Instructions.Execute("Event_Stockpile_Exclude_PurifiedBlueCrystal");
		}
		finally
		{
			Instructions.sujets = subjects;
			Instructions.objet = target;
		}
		yield return t.Until(() => stockpile.ExclusiveRessource != null, 5f, "the stockpile to take only one resource");
		t.Equal(Adjectif.purifiedBlueCrystal.GetType(), stockpile.ExclusiveRessource.GetType(), "The resource the stockpile takes");
	}

	[GameTest("Repairs", "A door at half health inside a repair zone is repaired by the zone's builder", TimeoutSeconds = 240f)]
	private static IEnumerator RepairZoneRepairsDoor(TestContext t)
	{
		XYZ site = Scenario.Site(t, 8, 6);
		BuildProject build = Scenario.BuildProject(t, site);
		Porte door = null;
		yield return Scenario.Building(t, Blueprints.Door, site, build, made => door = made.GetComponent<Porte>());
		t.NotNull(door, "The door");
		IDamageReceiver health = door.damageReceiver;
		t.NotNull(health, "The door's health");
		t.Record("doorHealth", health.GetType().Name + " " + health.CurrentHP + "/" + health.MaxHP + ", regen " + health.RegenPerSecond);
		t.Record("doorAt", door.transform.position.ToString() + ", anchor " + CastleStoryPlus.Building.DoorRepair.Anchor(door));
		// Doors heal by themselves (slowly, from 10 seconds after the last hit): off for the test, so only a repair counts.
		float regen = health.RegenPerSecond;
		health.RegenPerSecond = 0f;
		t.Cleanup(() =>
		{
			if (door != null)
			{
				health.RegenPerSecond = regen;
			}
		});
		health.ApplyDamage(health.MaxHP * 0.5f, null);
		yield return t.Frames(2);
		t.True(health.IsDamaged, "The door is not damaged after taking half its health");
		RepairProject zone = Scenario.NewProject<RepairProject>(t, () => Scenario.Groupe.CmdSpawnGoupe("Groupes", "Repair", site.ToVector3(), Quaternion.identity, false));
		ProjectEndpoints.Commit(zone);
		Labor builder = Scenario.Bricktron(t, Occupation.Type.Builder, TaskTests.StandNear(t, site));
		yield return t.Frames(5);
		Scenario.Hire(zone, builder);
		float started = Time.time;
		bool sawGoal = false;
		yield return t.UntilGame(() =>
		{
			sawGoal |= zone.repairProvider.GoalCount() > 0;
			return door == null || !health.IsDamaged;
		}, 180f, "the door to be repaired", 30f, () =>
		{
			t.Record("zone", ProjectEndpoints.Describe(zone));
			t.Record("repairGoals", zone.repairProvider.GoalCount());
			t.Log("door " + health.CurrentHP + "/" + health.MaxHP + "; builder " + builder.Activity + " at " + XYZ.FromVector3(builder.transform.position));
		});
		t.NotNull(door, "The door (gone while it was repaired)");
		t.True(sawGoal, "The repair zone never made a repair task for the door");
		t.Record("gameSeconds", Mathf.Round(Time.time - started));
		t.Record("doorHealthAfter", health.CurrentHP + "/" + health.MaxHP);
	}

	[GameTest("Repairs", "A door destroyed inside a repair zone is put back up as the zone's blueprint of the same door, which builds a new door in its place (#100)", TimeoutSeconds = 120f)]
	private static IEnumerator RepairZoneRebuildsDestroyedDoor(TestContext t)
	{
		XYZ site = Scenario.Site(t, 8, 6);
		BuildProject build = Scenario.BuildProject(t, site);
		Porte door = null;
		yield return Scenario.Building(t, Blueprints.Door, site, build, made => door = made.GetComponent<Porte>());
		t.NotNull(door, "The door");
		Vector3 position = door.transform.position;
		Quaternion rotation = door.transform.rotation;
		GameObject old = door.gameObject;
		RepairProject zone = Scenario.NewProject<RepairProject>(t, () => Scenario.Groupe.CmdSpawnGoupe("Groupes", "Repair", site.ToVector3(), Quaternion.identity, false));
		ProjectEndpoints.Commit(zone);
		yield return t.Frames(5);
		IDamageReceiver health = door.damageReceiver;
		t.NotNull(health, "The door's health");
		health.ApplyDamage(health.MaxHP + 1f, null);
		yield return t.Until(() => old == null || !old.activeInHierarchy, 5f, "the door to be destroyed");
		Blueprint rebuilt = null;
		yield return t.Until(() =>
		{
			foreach (Blueprint blueprint in Object.FindObjectsOfType<Blueprint>())
			{
				if (blueprint != null && blueprint.Project == zone && blueprint.BlueprintResult != null && blueprint.BlueprintResult.Name == ObjetsDynamiques.Porte.Name && Vector3.Distance(blueprint.transform.position, position) < 0.1f)
				{
					rebuilt = blueprint;
					return true;
				}
			}
			return false;
		}, 10f, "a door blueprint of the repair zone where the door stood");
		t.Cleanup(() =>
		{
			if (rebuilt != null && rebuilt.gameObject.activeInHierarchy)
			{
				Scenario.Discard(rebuilt.gameObject);
			}
		});
		t.True(Quaternion.Angle(rebuilt.transform.rotation, rotation) < 1f, "The blueprint is turned " + rebuilt.transform.rotation.eulerAngles + ", the door was " + rotation.eulerAngles);
		InstantBuild.Complete(new[] { rebuilt });
		Porte again = null;
		yield return t.Until(() =>
		{
			foreach (Porte candidate in Object.FindObjectsOfType<Porte>())
			{
				// The old door may come back from the object pool as the new one.
				if (candidate != null && candidate.gameObject.activeInHierarchy && Vector3.Distance(candidate.transform.position, position) < 0.1f)
				{
					again = candidate;
					return true;
				}
			}
			return false;
		}, 15f, "a new door where the old one stood");
		t.Cleanup(() =>
		{
			if (again != null && again.gameObject.activeInHierarchy)
			{
				Scenario.Discard(again.gameObject);
			}
		});
		t.Record("rebuiltAt", XYZ.FromVector3(again.transform.position));
	}

	[GameTest("Tasks", "A quarry of 5 x 5, 5 deep, dug by three builders: every stair step above the dug floor is cut, none is dug away", TimeoutSeconds = 600f)]
	private static IEnumerator QuarryStairsHaveNoGaps(TestContext t)
	{
		XYZ site = Scenario.Site(t, 9, 9);
		Scenario.LeavesMess(t);
		int top = site.y - 1;
		int depth = 5;
		MineProject quarry = NewQuarry(t, site, top, depth, out QuarryData data);
		QuarryGoalProvider2 provider = quarry.GetComponentInChildren<QuarryGoalProvider2>();
		t.NotNull(provider, "The quarry's goal provider");
		yield return t.Frames(5);
		List<XYZ> steps = new List<XYZ>(provider.stairs.Keys);
		t.True(steps.Count >= depth, "The quarry has " + steps.Count + " stair steps");
		t.Record("steps", steps.Count);
		XYZ stand = TaskTests.StandNear(t, new XYZ(site.x, site.y, site.z - 4));
		Labor a = Scenario.Bricktron(t, Occupation.Type.Builder, stand);
		Labor b = Scenario.Bricktron(t, Occupation.Type.Builder, stand + new XYZ(1, 0, 0));
		Labor c = Scenario.Bricktron(t, Occupation.Type.Builder, stand + new XYZ(-1, 0, 0));
		yield return t.Frames(5);
		Scenario.Hire(quarry, a, b, c);
		int bottom = top - depth + 1;
		yield return t.UntilGame(() => Gaps(steps).Count > 0 || (provider.GoalCount() == 0 && BottomDug(provider, steps, data.Bounds, bottom)), 480f, "the quarry to be dug out", 60f, () =>
		{
			t.Record("quarry", ProjectEndpoints.Describe(quarry));
			t.Record("whyNotDug", TaskTests.WhyNotDug(a, quarry));
			t.Log("lowest dug " + LowestDug(data.Bounds) + "; steps cut " + Cut(steps) + "/" + steps.Count + "; " + a.name + " " + a.Activity + " at " + Units.VoxelOf(a) + ", task " + ((a.CurrentTask != null) ? a.CurrentTask.GetType().Name : "none"));
		});
		t.Record("lowestDug", LowestDug(data.Bounds));
		t.Record("bottom", bottom);
		List<XYZ> gaps = Gaps(steps);
		t.True(gaps.Count == 0, "Stair steps dug away (gaps): " + string.Join(", ", gaps.ConvertAll(g => g.ToString()).ToArray()));
		// The last steps are cut once the ground above them is gone.
		yield return t.UntilGame(() => Uncut(steps).Count == 0, 90f, "every step under dug ground to be cut");
		t.Record("stepsCut", Cut(steps));
	}

	[GameTest("Tasks", "A quarry's cut stair step and the ground under it are kept from dig tasks that are not the quarry's own (a worker digging out of a pit, another task), also after its list of steps is made again as after a load", TimeoutSeconds = 300f)]
	private static IEnumerator QuarryStairsKeptFromOtherDigging(TestContext t)
	{
		XYZ site = Scenario.Site(t, 9, 9);
		Scenario.LeavesMess(t);
		int top = site.y - 1;
		MineProject quarry = NewQuarry(t, site, top, 4, out QuarryData data);
		QuarryGoalProvider2 provider = quarry.GetComponentInChildren<QuarryGoalProvider2>();
		t.NotNull(provider, "The quarry's goal provider");
		yield return t.Frames(5);
		t.True(provider.stairs.Count > 0, "The quarry has no stair steps");
		// The highest step is at the surface: it can be cut at once.
		XYZ step = XYZ.zero;
		bool found = false;
		foreach (XYZ planned in provider.stairs.Keys)
		{
			if (!found || planned.y > step.y)
			{
				step = planned;
				found = true;
			}
		}
		XYZ under = step - XYZ.up;
		t.Record("step", step);
		t.True(WorkerAI.QuarryStairs.Keeps(step), "The step " + step + " still to be cut is not kept");
		t.True(WorkerAI.QuarryStairs.Keeps(under), "The ground " + under + " under the step is not kept");
		// The middle of the quarry is ground to dig, no step.
		XYZ middle = new XYZ(site.x, top, site.z);
		t.False(WorkerAI.QuarryStairs.Keeps(middle), "The ground " + middle + " in the middle of the quarry is kept as a step");
		// As after a load: the list of steps is empty until it is made again, and the steps are kept all the same.
		provider.stairs.Clear();
		t.True(WorkerAI.QuarryStairs.Keeps(step), "The step " + step + " is not kept once the quarry's list of steps was emptied");
		t.True(provider.stairs.ContainsKey(step), "The quarry's list of steps was not made again");
		XYZ stand = TaskTests.StandNear(t, new XYZ(site.x, site.y, site.z - 4));
		Labor cutter = Scenario.Bricktron(t, Occupation.Type.Builder, stand);
		yield return t.Frames(5);
		Scenario.Hire(quarry, cutter);
		yield return t.UntilGame(() => QuarryGoalProvider2.IsStair(step), 150f, "the top step " + step + " to be cut", 30f, () =>
		{
			t.Record("quarry", ProjectEndpoints.Describe(quarry));
		});
		t.True(WorkerAI.QuarryStairs.Keeps(step), "The cut step " + step + " is not kept");
		// Dig tasks that are not the quarry's own, as a worker digging out of a pit makes them: on the step and under it.
		Labor stray = Scenario.Bricktron(t, Occupation.Type.Builder, stand + new XYZ(1, 0, 0));
		yield return t.Frames(5);
		DigGoal onStep = StrayDig(t, step);
		DigGoal underStep = StrayDig(t, under);
		yield return t.Frames(5);
		t.False(Matches(underStep, stray), "A builder would take a dig task on the ground " + under + " under the step");
		t.False(Matches(onStep, stray), "A builder would take a dig task on the step " + step);
		// Sent straight to it (as the pit escape does), the builder digs neither.
		underStep.Instruction.Send(stray, underStep.gameObject, Labor.DecisionSource.Project);
		yield return t.Seconds(20f);
		t.True(!Voxel.IsEmpty(under), "The ground " + under + " under the step was dug away");
		onStep.Instruction.Send(stray, onStep.gameObject, Labor.DecisionSource.Project);
		yield return t.Seconds(20f);
		t.True(QuarryGoalProvider2.IsStair(step), "The step " + step + " was dug away");
		t.True(!Voxel.IsEmpty(under), "The ground " + under + " under the step was dug away");
	}

	// A committed quarry over the site, depth blocks deep from top down, keeping nothing (the dug ground is broken up
	// and needs no storage).
	internal static MineProject NewQuarry(TestContext t, XYZ site, int top, int depth, out QuarryData data)
	{
		Vector3 min = new Vector3(site.x - 2, top - depth + 1, site.z - 2);
		Vector3 max = new Vector3(site.x + 2, top + 2, site.z + 2);
		MineProject quarry = Scenario.NewProject<MineProject>(t, () => Scenario.Groupe.CmdSpawnMineProject(min, Quaternion.identity, false));
		StairOrientation stairs = StairOrientation.Default;
		Scenario.Groupe.CmdSetMine(quarry.gameObject, min, max, stairs.corner.ToVector3(), stairs.direction.ToVector3(), false);
		data = quarry.GetComponent<QuarryData>();
		t.NotNull(data, "The quarry's data");
		stairs = data.StairOrientation;
		Scenario.Groupe.CmdSetMine(quarry.gameObject, data.Bounds.min.ToVector3(), data.Bounds.max.ToVector3(), stairs.corner.ToVector3(), stairs.direction.ToVector3(), true);
		data.SetTypesToHarvest(0);
		ProjectEndpoints.Commit(quarry);
		return quarry;
	}

	// Dig tasks of a stray dig keep nothing: what is dug is broken without a drop.
	private sealed class KeepNothing : IDigGoalResourceSetting
	{
		public GroundResource TypesToHarvest => GroundResource.None;
	}

	// A dig task of no project at the voxel, as the pit escape (StuckEscape) makes them; released by the clean-up.
	private static DigGoal StrayDig(TestContext t, XYZ voxel)
	{
		t.True(ObjectPoolSingleton.RequestComponent<DigGoal>(out DigGoal goal, Test.DigGoal, voxel.ToVector3(), Quaternion.identity) && goal != null, "No dig task could be made at " + voxel);
		goal.RebuildNode();
		goal.ResourceSetting = new KeepNothing();
		goal.UseHeightAdjustment = false;
		t.Cleanup(() => Scenario.Release(goal.gameObject));
		return goal;
	}

	private static bool Matches(DigGoal goal, Labor labor)
	{
		AIErrorsStore errors = new AIErrorsStore();
		float time = 0f;
		bool unmapped = false;
		return goal.IsMatchFor(labor, errors, ref time, ref unmapped);
	}

	// MaxSpeed 4: at 10x the builder now and then stopped with the row at 5 of 6 and the column at 1 of 3 (about 1 run
	// in 3); at 4x it built them in every run.
	[GameTest("Tasks", "A builder with a ground row of 6 stone bricks and a column of 3 beside it builds no column block 2 or more above the ground row before the row is finished", TimeoutSeconds = 420f, MaxSpeed = 4f)]
	private static IEnumerator BuildsFloorByFloor(TestContext t)
	{
		XYZ site = Scenario.Site(t, 12, 6);
		BuildProject build = Scenario.BuildProject(t, site);
		Recepteur stockpile = null;
		yield return Scenario.Building(t, TaskTests.Pallet, new XYZ(site.x - 4, site.y, site.z), build, made => stockpile = made.GetComponent<Recepteur>());
		t.NotNull(stockpile, "The stockpile's storage");
		foreach (GameObject block in Scenario.Items(t, FactoryBlocks.Stone_DoubleBrick, 14, stockpile.transform.position + Vector3.up))
		{
			stockpile.AddObject(block);
		}
		// The column next to the stockpile, the row further away: the builder comes back to the column every trip.
		List<XYZ> column = new List<XYZ>();
		for (int y = 0; y < 3; y++)
		{
			column.Add(new XYZ(site.x - 1, site.y + y, site.z + 2));
		}
		List<XYZ> row = new List<XYZ>();
		for (int x = 0; x < 6; x++)
		{
			row.Add(new XYZ(site.x + 1 + x, site.y, site.z - 1));
		}
		List<XYZ> all = new List<XYZ>(column);
		all.AddRange(row);
		foreach (XYZ voxel in all)
		{
			BlueprintPlacer.Result placed = BlueprintPlacer.Place(StoneBlueprint, voxel, 0, build, force: true);
			t.True(placed.Placed, "Stone brick blueprint at " + voxel + ": " + placed.Outcome + " " + placed.Detail);
		}
		t.Cleanup(() =>
		{
			foreach (XYZ voxel in all)
			{
				IBlockData block = Voxel.GetBlockData(voxel);
				if (block != null && !block.IsNull && Voxel.IsBlock(voxel))
				{
					block.RootOrSelf.DeBlock();
				}
			}
		});
		ProjectEndpoints.Commit(build);
		Labor builder = Scenario.Bricktron(t, Occupation.Type.Builder, new XYZ(site.x - 2, site.y, site.z - 2));
		yield return t.Frames(5);
		Scenario.Hire(build, builder);
		int early = 0;
		float end = Time.time + 360f;
		while (!all.TrueForAll(v => Voxel.IsBlock(v)))
		{
			bool rowDone = row.TrueForAll(v => Voxel.IsBlock(v));
			for (int y = 2; y < column.Count; y++)
			{
				if (!rowDone && Voxel.IsBlock(column[y]))
				{
					early = Mathf.Max(early, y);
				}
			}
			t.True(Time.time < end, "Waited 360 game seconds for the blueprints to be built (row " + row.FindAll(v => Voxel.IsBlock(v)).Count + "/6, column " + column.FindAll(v => Voxel.IsBlock(v)).Count + "/3)");
			yield return null;
		}
		t.True(early == 0, "Column block " + early + " above the ground was built before the ground row was finished");
	}

	private static List<XYZ> Gaps(List<XYZ> steps)
	{
		List<XYZ> result = new List<XYZ>();
		foreach (XYZ step in steps)
		{
			if (Voxel.IsEmpty(step))
			{
				result.Add(step);
			}
		}
		return result;
	}

	// Steps whose ground above is dug but which are not cut yet.
	private static List<XYZ> Uncut(List<XYZ> steps)
	{
		List<XYZ> result = new List<XYZ>();
		foreach (XYZ step in steps)
		{
			if (Voxel.IsEmpty(step + XYZ.up) && !QuarryGoalProvider2.IsStair(step))
			{
				result.Add(step);
			}
		}
		return result;
	}

	private static int Cut(List<XYZ> steps)
	{
		int cut = 0;
		foreach (XYZ step in steps)
		{
			if (QuarryGoalProvider2.IsStair(step))
			{
				cut++;
			}
		}
		return cut;
	}

	// Every voxel of the bottom layer that is not a step or under one is dug.
	private static bool BottomDug(QuarryGoalProvider2 provider, List<XYZ> steps, VoxelBounds bounds, int bottom)
	{
		HashSet<XYZ> kept = new HashSet<XYZ>(steps);
		for (int x = bounds.min.x; x <= bounds.max.x; x++)
		{
			for (int z = bounds.min.z; z <= bounds.max.z; z++)
			{
				XYZ voxel = new XYZ(x, bottom, z);
				if (kept.Contains(voxel) || kept.Contains(voxel + XYZ.up) || Voxel.IsInvincible(voxel))
				{
					continue;
				}
				if (!Voxel.IsEmpty(voxel))
				{
					return false;
				}
			}
		}
		return true;
	}

	private static int LowestDug(VoxelBounds bounds)
	{
		int lowest = bounds.max.y;
		for (int x = bounds.min.x; x <= bounds.max.x; x++)
		{
			for (int z = bounds.min.z; z <= bounds.max.z; z++)
			{
				for (int y = bounds.min.y; y <= bounds.max.y; y++)
				{
					if (Voxel.IsEmpty(new XYZ(x, y, z)))
					{
						lowest = Mathf.Min(lowest, y);
						break;
					}
				}
			}
		}
		return lowest;
	}
}
