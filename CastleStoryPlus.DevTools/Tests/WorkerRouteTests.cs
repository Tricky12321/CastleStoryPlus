#if UNITY6
using System.Collections;
using System.Collections.Generic;
using Brix.Engine;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.AI.KnowledgeSpace;
using Brix.Game.AI.UI;
using Brix.Game.Components;
using Brix.Game.Semantique;
using Brix.Input;
using Brix.Utils;
using CastleStoryPlus.Building;
using CastleStoryPlus.Core;
using CastleStoryPlus.DevTools.Pathing;
using CastleStoryPlus.DevTools.Testing;
using CastleStoryPlus.Treasure;
using CastleStoryPlus.WorkerAI;
using UnityEngine;

namespace CastleStoryPlus.DevTools.Tests;

// Worker movement: the walk to a thing counts, not the straight line (#69); a bricktron shut in a pit digs its way
// out, also while it keeps trying tasks it cannot reach (#62); a flatten task's height handle dragged down stays where
// it was dragged (#65).
[ParallelTests("Economy")]
internal static class WorkerRouteTests
{
#if !ESSENTIALS
	[GameTest("Tasks", "A stone just behind a wall 3 high counts the walk round the wall: the worker takes the one further off on its own side first (#69)", TimeoutSeconds = 120f)]
	private static IEnumerator NearestByWalk(TestContext t)
	{
		XYZ site = Scenario.Site(t, 15, 9, 4, away: 30);
		Recepteur stockpile = null;
		BuildProject build = Scenario.BuildProject(t, site);
		yield return Scenario.Building(t, TaskTests.Pallet, new XYZ(site.x - 4, site.y, site.z + 3), build, made => stockpile = made.GetComponent<Recepteur>());
		t.NotNull(stockpile, "The stockpile's storage");
		List<XYZ> wall = new List<XYZ>();
		for (int dx = -5; dx <= 5; dx++)
		{
			for (int y = 0; y < 3; y++)
			{
				wall.Add(new XYZ(site.x + dx, site.y + y, site.z));
			}
		}
		TestWorld.PlaceBlocks(t, wall);
		Labor builder = Scenario.Bricktron(t, Occupation.Type.Builder, new XYZ(site.x, site.y, site.z - 2));
		builder.Autonomy = AutonomyStatus.WaitForOrders;
		yield return TestWorld.WaitForGraph(t);
		yield return t.Seconds(1f);
		// Behind the wall, 4 away in a straight line; on the builder's side, 6 away.
		GameObject behind = Scenario.Items(t, RawResources.RawStone, 1, new XYZ(site.x, site.y, site.z + 2).ToVector3())[0];
		GameObject ahead = Scenario.Items(t, RawResources.RawStone, 1, new XYZ(site.x + 6, site.y, site.z - 2).ToVector3())[0];
		yield return t.Seconds(1f);
		t.Log("Builder at " + Units.VoxelOf(builder) + ", stone behind the wall at " + behind.transform.position + ", stone ahead at " + ahead.transform.position);
		RouteDistance.Field field = RouteDistance.For(builder);
		t.NotNull(field, "The builder's walking field");
		int walkBehind = RouteDistance.To(field, behind.transform.position);
		int walkAhead = RouteDistance.To(field, ahead.transform.position);
		t.Record("walkBehind", walkBehind);
		t.Record("walkAhead", walkAhead);
		t.True(walkAhead >= 0, "The stone ahead is not within the builder's walking field");
		t.True(walkBehind < 0 || walkBehind > walkAhead, "The walk round the wall (" + walkBehind + ") is not longer than the walk to the stone ahead (" + walkAhead + ")");
		// A cleanup area's goals, as the game makes them for its items.
		List<Goal> goals = new List<Goal> { behind.GetOrAddComponent<CleanUpGoal>(), ahead.GetOrAddComponent<CleanUpGoal>() };
		t.True(RouteOrder.Sort(builder, goals), "The goals could not be ordered (the builder stands on no route node)");
		t.True(goals[0].gameObject == ahead, "The stone behind the wall came first");
		if (BaseJobs.CoversCleanup(builder.faction, ahead.transform.position))
		{
			t.Log("The base's cleanup task covers the site: the idle chore is not asked");
			yield break;
		}
		// The idle cleanup's item list is renewed every 5 s.
		yield return t.Seconds(6f);
		GameObject planned = AutoCleanup.TryPlan(builder);
		AutoCleanup.Release(builder);
		t.Log("The idle cleanup chose " + ((planned != null) ? planned.transform.position.ToString() : "nothing"));
		if (planned != ahead)
		{
			foreach (KeyValuePair<GameObject, string> entry in AutoCleanup.Explain(builder))
			{
				t.Log(entry.Key.name + " at " + entry.Key.transform.position + ": " + (entry.Value ?? "would pick it up"));
			}
		}
		// In the test world the idle chore may see neither stone (nothing to compare then); it must never take the far one.
		t.True(planned != behind, "The idle cleanup chose the stone behind the wall");
	}
#endif

#if !ESSENTIALS
	[GameTest("Tasks", "An unopened treasure chest near the base is no cleanup chore (idle cleanup or the base cleanup task); loot lying there is (#58)", TimeoutSeconds = 120f)]
	private static IEnumerator ChestIsNoCleanupChore(TestContext t)
	{
		XYZ site = Scenario.Site(t, 11, 7, 4, away: 30);
		Recepteur stockpile = null;
		BuildProject build = Scenario.BuildProject(t, site);
		yield return Scenario.Building(t, TaskTests.Pallet, new XYZ(site.x - 4, site.y, site.z), build, made => stockpile = made.GetComponent<Recepteur>());
		t.NotNull(stockpile, "The stockpile's storage");
		Labor builder = Scenario.Bricktron(t, Occupation.Type.Builder, new XYZ(site.x, site.y, site.z - 2));
		builder.Autonomy = AutonomyStatus.WaitForOrders;
		GameObject chest = Brix.Transactions.Transactor.SpawnUNet(ObjetsDynamiques.Chest, Faction.DefaultNeutral, new XYZ(site.x + 3, site.y, site.z).ToVector3(), Quaternion.identity);
		t.NotNull(chest, "The chest");
		t.Cleanup(() => Scenario.Release(chest));
		yield return t.Seconds(2f);
		if (BaseJobs.CoversCleanup(builder.faction, chest.transform.position))
		{
			// The base's cleanup task (a cleanup area) takes the items there.
			Project task = BaseJobs.Find(builder.faction, cleanup: true);
			t.NotNull(task, "The base's cleanup task");
			List<Goal> goals = new List<Goal>();
			float until = Time.time + 20f;
			while (Time.time < until)
			{
				goals.Clear();
				foreach (IGoalProvider provider in task.GetGoalProviders())
				{
					provider?.GetAll(goals);
				}
				t.False(goals.Exists((Goal goal) => goal != null && goal.gameObject == chest), "The base's cleanup task has a goal for the treasure chest");
				yield return t.Seconds(1f);
			}
			t.Log("The base's cleanup task has " + goals.Count + " goals, none for the chest");
			yield break;
		}
		foreach (KeyValuePair<GameObject, string> entry in AutoCleanup.Explain(builder))
		{
			if (Vector3.Distance(entry.Key.transform.position, chest.transform.position) < 4f)
			{
				t.Log(entry.Key.name + " at " + entry.Key.transform.position + ": " + (entry.Value ?? "would pick it up"));
			}
		}
		GameObject planned = AutoCleanup.TryPlan(builder);
		AutoCleanup.Release(builder);
		t.Log("The idle cleanup chose " + ((planned != null) ? planned.name + " at " + planned.transform.position : "nothing"));
		t.True(planned != chest, "The idle cleanup chose the treasure chest");
		GameObject ingot = Scenario.Items(t, BuildResources.IronIngot, 1, new XYZ(site.x + 3, site.y, site.z + 2).ToVector3())[0];
		yield return t.Seconds(6f);
		planned = AutoCleanup.TryPlan(builder);
		AutoCleanup.Release(builder);
		t.Log("With an iron ingot there, the idle cleanup chose " + ((planned != null) ? planned.name + " at " + planned.transform.position : "nothing"));
		if (planned == null)
		{
			// The idle cleanup only looks round the base: a site the test had to put far away is out of its reach.
			bool considered = false;
			foreach (KeyValuePair<GameObject, string> entry in AutoCleanup.Explain(builder))
			{
				if (entry.Key == ingot)
				{
					considered = true;
					t.Log("The iron ingot: " + (entry.Value ?? "would pick it up") + ClaimedBy(ingot));
				}
			}
			if (!considered)
			{
				t.Skip("The iron ingot at " + site + " is outside the idle cleanup's reach (no flat ground left near the base)");
			}
		}
		t.True(planned == ingot, "The idle cleanup did not choose the iron ingot");
	}
#endif

#if !ESSENTIALS
	// Which workers' idle cleanup has planned to take the item (AutoCleanup's plans), for the log.
	private static string ClaimedBy(GameObject item)
	{
		Dictionary<Labor, GameObject> plans = HarmonyLib.AccessTools.Field(typeof(AutoCleanup), "Plans")?.GetValue(null) as Dictionary<Labor, GameObject>;
		if (plans == null)
		{
			return string.Empty;
		}
		List<string> who = new List<string>();
		foreach (KeyValuePair<Labor, GameObject> plan in plans)
		{
			if (plan.Value == item && plan.Key != null)
			{
				who.Add(plan.Key.name + " (" + plan.Key.Autonomy.Description + ", at " + Units.VoxelOf(plan.Key) + ")");
			}
		}
		return (who.Count > 0) ? "; planned by " + string.Join(", ", who.ToArray()) : string.Empty;
	}
#endif

	// How close to the chest a bricktron counts as having walked up to it (it opens, or shows the hint, from 2.6).
	private const float AtChest = 4f;

#if !ESSENTIALS
	[GameTest("Tasks", "A treasure chest its guardians still guard sends no worker out: the loose item by it is neither fetched as material nor cleaned up, and an idle builder near it never walks up to it; once the guardians are gone the item is free again (#58)", TimeoutSeconds = 240f)]
	private static IEnumerator GuardedChestIsNoErrand(TestContext t)
	{
		if (!Plugin.IsEnabled(Features.Treasure) || !TreasureChests.Active)
		{
			t.Skip("Treasure chests are off in this game");
		}
		Faction enemy = Scenario.EnemyFaction();
		if (enemy == null)
		{
			t.Skip("No enemy faction in this game");
		}
		// Along x, from the home crystal's side out: the builder, 18 blocks of open ground, the chest, the guardians just
		// beyond it. The chest lies away from home, so the builder's own walks (to the gather point by the home crystal)
		// do not pass it: a walk past the chest is not an errand to it (the builder that stood west of an east chest,
		// with the crystal further east, walked past it on its way home). The stockpile stands in the chest's ruin (so the
		// idle cleanup's reach covers the item by the chest).
		XYZ site = Scenario.Site(t, 31, 9, 4, away: 30);
		int home = (Scenario.Home().x >= site.x) ? 1 : -1;
		XYZ chestAt = new XYZ(site.x - home * 9, site.y, site.z);
		Recepteur stockpile = null;
		BuildProject build = Scenario.BuildProject(t, site);
		yield return Scenario.Building(t, TaskTests.Pallet, new XYZ(chestAt.x + home * 6, site.y, site.z - 2), build, made => stockpile = made.GetComponent<Recepteur>());
		t.NotNull(stockpile, "The stockpile's storage");
		GameObject chest = Brix.Transactions.Transactor.SpawnUNet(ObjetsDynamiques.Chest, Faction.DefaultNeutral, chestAt.ToVector3(), Quaternion.identity);
		t.NotNull(chest, "The chest");
		Vector3 chestPos = chest.transform.position;
		TreasureChests.Watch(chest, 2);
		t.Cleanup(() =>
		{
			TreasureChests.Forget(chestPos);
			Scenario.Release(chest);
		});
		List<Labor> guardians = new List<Labor>();
		for (int i = 0; i < 2; i++)
		{
			XYZ at = Scenario.Walkable(new XYZ(chestAt.x - home * 4, chestAt.y, chestAt.z + ((i == 0) ? -3 : 3)));
			Labor beast = Beasts.Spawn(BeastKind.Boulderback, enemy, at.ToVector3(), 1);
			t.NotNull(beast, "Guardian " + (i + 1));
			Scenario.Adopt(t, beast);
			// Held where they stand. Not WaitForOrders: with WorkerAI any task that ends turns that into FreeAgent
			// (FasterWorkers), and the unit goes about its own business.
			beast.Autonomy = AutonomyStatus.HoldingPosition;
			guardians.Add(beast);
		}
		Labor builder = Scenario.Bricktron(t, Occupation.Type.Builder, new XYZ(site.x + home * 9, site.y, site.z));
		builder.Autonomy = AutonomyStatus.HoldingPosition;
		GameObject ingot = Scenario.Items(t, BuildResources.IronIngot, 1, new XYZ(chestAt.x + home * 2, chestAt.y, chestAt.z + 2).ToVector3())[0];
		GameComponent item = ingot.GetComponent<GameComponent>();
		Ressource iron = Apparence.PrincipaleOf(ingot);
		t.NotNull(item, "The iron ingot's game component");
		t.NotNull(iron, "The iron ingot's resource");
		// No player's, as the chest's loot and whatever else lies in a ruin (a player's own things there stay theirs).
		item.faction = Faction.DefaultNeutral;
		yield return TestWorld.WaitForGraph(t);
		// The chests are looked at every second; the idle cleanup's item list is renewed every 5 s.
		yield return t.Seconds(6f);
		t.Log("Chest at " + chestPos + ", guardians at " + guardians[0].transform.position + " and " + guardians[1].transform.position + ", builder at " + Units.VoxelOf(builder) + " (" + builder.Autonomy.Description + "), iron ingot at " + ingot.transform.position);
		t.True(TreasureChests.InGuardedRuin(chestPos), "The chest's ruin is not kept out although its guardians stand by it");
		t.True(TreasureChests.InGuardedRuin(ingot.transform.position), "The iron ingot by the guarded chest is not in its ruin");
		t.False(TreasureChests.InGuardedRuin(builder.transform.position), "The builder 18 blocks away is in the chest's ruin");
		// No material is fetched from there (the game's map-wide search for the nearest thing holding a resource).
		t.False(Knowledge._ClosestObjectToPickup_Test(item, iron), "The iron ingot by the guarded chest may be fetched as material");
		// Nor is it the idle cleanup's.
		GameObject planned = AutoCleanup.TryPlan(builder);
		AutoCleanup.Release(builder);
		t.Log("The idle cleanup chose " + ((planned != null) ? planned.name + " at " + planned.transform.position : "nothing"));
		t.True(planned != ingot, "The idle cleanup chose the iron ingot by the guarded chest");
		t.True(planned != chest, "The idle cleanup chose the guarded chest");
		string why = CleanupReason(builder, ingot);
		t.Log("The iron ingot, to the idle cleanup: " + (why ?? "would pick it up"));
		if (why != null && why.StartsWith("inside the Auto cleanup", System.StringComparison.Ordinal))
		{
			t.Log("The base's cleanup task covers the site: its crew is not the idle cleanup");
		}
		else if (why != null && why != "not considered")
		{
			t.True(why.Contains("treasure chest"), "The idle cleanup left the iron ingot alone for another reason (" + why + ")");
		}
		// The builder left to its own devices for a while never walks up to the chest. Whatever sends it is named in
		// the log: its task, activity and autonomy every 5 s.
		builder.Autonomy = AutonomyStatus.FreeAgent;
		Scenario.Idle(builder);
		float closest = float.MaxValue;
		float until = Time.time + 45f;
		float noteAt = 0f;
		while (Time.time < until)
		{
			float distance = Vector3.Distance(builder.transform.position, chestPos);
			closest = Mathf.Min(closest, distance);
			if (Time.time >= noteAt || distance <= AtChest)
			{
				t.Log(Time.time.ToString("F0") + " s: builder " + distance.ToString("F1") + " from the chest, " + Doing(builder));
				noteAt = Time.time + 5f;
			}
			if (distance <= AtChest)
			{
				break;
			}
			yield return t.Seconds(0.5f);
		}
		t.Record("closest", closest.ToString("F1"));
		t.True(closest > AtChest, "The idle builder walked up to the guarded chest (to " + closest.ToString("F1") + " blocks): " + Doing(builder));
		t.True(chest.activeInHierarchy && !chest.IsNullOrReleased(), "The guarded chest was opened");
		// The guardians gone (lured away or slain), the ruin is open again: the item may be fetched and cleaned up.
		builder.Autonomy = AutonomyStatus.HoldingPosition;
		Scenario.Idle(builder);
		foreach (Labor guardian in guardians)
		{
			Scenario.Discard(guardian.gameObject);
		}
		yield return t.Seconds(6f);
		t.False(TreasureChests.InGuardedRuin(chestPos), "The chest's ruin is still kept out with its guardians gone");
		if (ingot.activeInHierarchy && PickUpAround.IsLoose(ingot))
		{
			t.True(Knowledge._ClosestObjectToPickup_Test(item, iron), "The iron ingot by the unguarded chest may not be fetched as material");
			why = CleanupReason(builder, ingot);
			t.Log("The iron ingot, to the idle cleanup now: " + (why ?? "would pick it up"));
			t.True(why == null || !why.Contains("treasure chest"), "The idle cleanup still leaves the iron ingot alone for the chest (" + why + ")");
		}
	}
#endif

#if !ESSENTIALS
	// Why the idle cleanup would leave the item alone (null: it would pick it up; "not considered": out of its reach).
	private static string CleanupReason(Labor labor, GameObject item)
	{
		foreach (KeyValuePair<GameObject, string> entry in AutoCleanup.Explain(labor))
		{
			if (entry.Key == item)
			{
				return entry.Value;
			}
		}
		return "not considered";
	}
#endif

	// What the bricktron is about, for the log.
	private static string Doing(Labor labor)
	{
		Project project = labor.Project;
		return "task " + ((project != null) ? project.name : "none") + ", activity " + labor.Activity + ", autonomy " + labor.Autonomy.Description + ", at " + Units.VoxelOf(labor);
	}

	[GameTest("Tasks", "A bricktron standing idle in a pit 4 deep digs its way out (#62)", TimeoutSeconds = 300f)]
	private static IEnumerator IdleWorkerLeavesPit(TestContext t)
	{
		XYZ site = Scenario.Site(t, 7, 7, 4);
		yield return Pit(t, site, 4);
		Labor builder = Scenario.Bricktron(t, Occupation.Type.Builder, new XYZ(site.x, site.y - 4, site.z));
		yield return t.Seconds(1f);
		t.Log("In the pit at " + Units.VoxelOf(builder));
		t.True(Units.VoxelOf(builder).y < site.y - 2, "The builder is not in the pit");
		yield return t.UntilGame(() => OutOfPit(builder, site), 240f, "the builder to get out of the pit");
		t.Log("Out at " + Units.VoxelOf(builder));
	}

	[GameTest("Tasks", "A bricktron in a pit 4 deep that is never idle (a new sit as soon as one ends, as the idle task gives) digs its way out (#62)", TimeoutSeconds = 360f)]
	private static IEnumerator BusyWorkerLeavesPit(TestContext t)
	{
		XYZ site = Scenario.Site(t, 7, 7, 4);
		yield return Pit(t, site, 4);
		Labor builder = Scenario.Bricktron(t, Occupation.Type.Builder, new XYZ(site.x, site.y - 4, site.z));
		builder.Autonomy = AutonomyStatus.WaitForOrders;
		yield return t.Seconds(1f);
		t.Log("In the pit at " + Units.VoxelOf(builder));
		t.True(Units.VoxelOf(builder).y < site.y - 2, "The builder is not in the pit");
		int sits = 0;
		// It stands still after its escape (it waits for orders): the way out is dug once its walking field reaches
		// the ground round the pit.
		yield return t.UntilGame(() =>
		{
			if (OutOfPit(builder, site) || ReachesUp(builder, site.y))
			{
				return true;
			}
			if (builder.IsAvailable() && builder.CurrentTask == null)
			{
				Order.Package.Of(Order.ForLabor.Sit, Labor.DecisionSource.Project).Send(builder);
				sits++;
			}
			return false;
		}, 300f, "the builder to get out of the pit");
		t.Record("sits", sits);
		t.Log("Out at " + Units.VoxelOf(builder) + " after " + sits + " sits");
	}

	private static float _nextField;

	private static bool ReachesUp(Labor labor, int y)
	{
		if (Time.time < _nextField)
		{
			return false;
		}
		_nextField = Time.time + 2f;
		RouteDistance.Field field = RouteDistance.For(labor);
		if (field == null)
		{
			return false;
		}
		foreach (XYZ place in field.Cost.Keys)
		{
			if (place.y >= y)
			{
				return true;
			}
		}
		return false;
	}

	// A pit of one column dug depth deep under the standing voxel (the voxels' own damage receivers, as digging does,
	// so the route graph hears of it).
	// Out of the pit: up at the ground's height, or off the site (on a slope the nearest place on the routes can lie
	// lower down the hill, and the way out is dug sideways to it).
	private static bool OutOfPit(Labor builder, XYZ site)
	{
		XYZ at = Units.VoxelOf(builder);
		return at.y >= site.y || Mathf.Max(Mathf.Abs(at.x - site.x), Mathf.Abs(at.z - site.z)) > 3;
	}

	private static IEnumerator Pit(TestContext t, XYZ at, int depth)
	{
		Scenario.LeavesMess(t);
		for (int d = 1; d <= depth; d++)
		{
			using (IVoxel voxel = Voxel.GetVoxel(new XYZ(at.x, at.y - d, at.z)))
			{
				if (voxel != null && voxel.DamageReceiver != null)
				{
					voxel.DamageReceiver.Kill();
				}
			}
		}
		yield return TestWorld.WaitForGraph(t);
	}

	[GameTest("Tasks", "A flatten task's height handle dragged 3 blocks down stays 3 down while held and after it is let go (#65)", TimeoutSeconds = 60f)]
	private static IEnumerator FlattenHandleDraggedDown(TestContext t)
	{
		XYZ site = Scenario.Site(t, 9, 9, 4);
		LandscapeProject project = Scenario.NewProject<LandscapeProject>(t, () => Scenario.Groupe.CmdSpawnLandscapeProject(site.ToVector3(), Quaternion.identity, false));
		yield return t.Seconds(1f);
		LandscapeController controller = project.GetComponentInChildren<LandscapeController>(true);
		t.NotNull(controller, "The flatten task's controller");
		LandscapeProjectUI ui = controller.ui;
		t.NotNull(ui, "The flatten task's UI");
		DraggableVolumePlane plane = ui.threshold;
		t.NotNull(plane, "The flatten task's height plane");
		DragHandle handle = plane.handle;
		int start = plane.GetY();
		Vector3 origin = handle.Origin;
		t.Log("Height " + start + ", handle at " + origin);
		handle.DragStarted();
		List<int> heights = new List<int>();
		Vector3 down = origin + Vector3.down * 3.3f + Vector3.forward * 0.25f;
		for (int i = 0; i < 6; i++)
		{
			handle.Drag(origin, down);
			heights.Add(plane.GetY());
			yield return t.Frames(1);
		}
		t.Log("While held: " + string.Join(", ", heights.ConvertAll(h => h.ToString()).ToArray()));
		handle.DragEnded();
		int released = plane.GetY();
		yield return t.Seconds(1f);
		int after = plane.GetY();
		t.Record("start", start);
		t.Record("released", released);
		t.Record("after", after);
		foreach (int height in heights)
		{
			t.Equal(start - 3, height, "The height while the handle is held");
		}
		t.Equal(start - 3, released, "The height when the handle is let go");
		t.Equal(start - 3, after, "The height a second later");
	}
}
#endif
