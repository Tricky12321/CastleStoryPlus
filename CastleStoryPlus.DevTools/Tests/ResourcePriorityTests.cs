using System.Collections;
using System.Collections.Generic;
using Brix.Engine;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.AI.KnowledgeSpace;
using Brix.Game.Components;
using Brix.Game.Semantique;
using Brix.Input;
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
		List<Goal> ordered = ResourcePrioritySelect.Order(builder, goals);
		t.Equal(2, ordered.Count, "Goals ordered");
		t.True(ordered[0] == logGoal, "Wood at 5 and stone at 1: the stone came first");
		float time = 0f;
		t.True(logGoal.PriorityFor(builder, ref time) > stoneGoal.PriorityFor(builder, ref time), "The game's priority number of the log (5) is not above the stone's (1)");

		Priority(t, "wood", 1);
		Priority(t, "stone", 5);
		ordered = ResourcePrioritySelect.Order(builder, goals);
		t.True(ordered[0] == stoneGoal, "Stone at 5 and wood at 1: the log came first");
		t.True(stoneGoal.PriorityFor(builder, ref time) > logGoal.PriorityFor(builder, ref time), "The game's priority number of the stone (5) is not above the log's (1)");

		Priority(t, "stone", 0);
		ordered = ResourcePrioritySelect.Order(builder, goals);
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

	[GameTest("ResourcePriority", "A workshop's worker with a stone and a log both in a stockpile fetches the higher priority first when the recipe wants both, and never the one at 0", TimeoutSeconds = 120f)]
	private static IEnumerator IngredientsFollowPriority(TestContext t)
	{
		XYZ site = Scenario.Site(t, 10, 6);
		Recepteur stockpile = null;
		BuildProject build = Scenario.BuildProject(t, site);
		yield return Scenario.Building(t, TaskTests.Pallet, new XYZ(site.x - 3, site.y, site.z), build, made => stockpile = made.GetComponent<Recepteur>());
		t.NotNull(stockpile, "The stockpile's storage");
		foreach (GameObject item in Scenario.Items(t, FactoryBlocks.Wood_DoubleLog_AA, 1, stockpile.transform.position + Vector3.up))
		{
			stockpile.AddObject(item);
		}
		foreach (GameObject item in Scenario.Items(t, RawResources.RawStone, 1, stockpile.transform.position + Vector3.up))
		{
			stockpile.AddObject(item);
		}
		Labor builder = Scenario.Bricktron(t, Occupation.Type.Builder, new XYZ(site.x, site.y, site.z - 2));
		builder.Autonomy = AutonomyStatus.WaitForOrders;
		// Both are known to the worker (one at a time, the game's own search) before the choice is tested.
		Description onlyStone = new Description();
		onlyStone.Add(Adjectif.New(Adjectif.stones.GetType(), 1));
		Description onlyWood = new Description();
		onlyWood.Add(Adjectif.New(Adjectif.woodBlock.GetType(), 1));
		yield return t.UntilGame(() => Find(builder, onlyStone) != null && Find(builder, onlyWood) != null, 30f, "the stone and the log in the stockpile to be known to the worker");
		// A recipe's ingredients: the stone listed first, as the game's first match.
		Description wanted = new Description();
		wanted.Add(Adjectif.New(Adjectif.stones.GetType(), 1));
		wanted.Add(Adjectif.New(Adjectif.woodBlock.GetType(), 1));

		Priority(t, "wood", 5);
		Priority(t, "stone", 1);
		GameObject found = Find(builder, wanted);
		t.Log("Builder at " + builder.transform.position + ", stockpile at " + stockpile.transform.position + ", found " + ((found != null) ? found.name + " at " + found.transform.position : "nothing"));
		t.NotNull(found, "Nothing found for a stone and a log in the stockpile");
		t.True(IsKind(found, "wood"), "Wood at 5 and stone at 1: the worker went for " + found.name);

		Priority(t, "wood", 1);
		Priority(t, "stone", 5);
		found = Find(builder, wanted);
		t.True(IsKind(found, "stone"), "Stone at 5 and wood at 1: the worker went for " + ((found != null) ? found.name : "nothing"));

		Priority(t, "stone", 0);
		found = Find(builder, wanted);
		t.True(IsKind(found, "wood"), "Stone at 0: the worker went for " + ((found != null) ? found.name : "nothing"));
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
		yield return t.UntilGame(() => Dug(ground) >= 1, 240f, "a voxel of the quarry to be dug once stone is back at 3");
		t.Record("dug", Dug(ground));
	}

	// The game's search, as a new decision of the worker makes it: its resource cache lives for one decision (frame).
	private static GameObject Find(Labor labor, Description wanted)
	{
		labor.decisionFrame++;
		return labor.Find(wanted);
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
