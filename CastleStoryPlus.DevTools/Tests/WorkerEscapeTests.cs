#if UNITY6
using System.Collections;
using System.Collections.Generic;
using Brix.Engine;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Game.Semantique;
using CastleStoryPlus.DevTools.Pathing;
using CastleStoryPlus.DevTools.Testing;
using CastleStoryPlus.WorkerAI;
using UnityEngine;
using FactoryBlocks = Brix.External.Factories.Blocks;

namespace CastleStoryPlus.DevTools.Tests;

// Workers shut in a pit together dig out and walk off the tunnel's mouth; a loaded worker deep in a narrow way down
// stores its load instead of having it put down on the stair; a load is never put down on a quarry's floor.
[ParallelTests("Economy")]
internal static class WorkerEscapeTests
{
	[GameTest("Tasks", "Two bricktrons shut in a pit 4 deep together both dig their way out, and neither stays standing on the tunnel's mouth", TimeoutSeconds = 360f)]
	private static IEnumerator TwoWorkersEscapeAndClearTheMouth(TestContext t)
	{
		XYZ site = Scenario.Site(t, 11, 11, 4);
		Scenario.LeavesMess(t);
		List<XYZ> pit = new List<XYZ>();
		for (int d = 1; d <= 4; d++)
		{
			pit.Add(new XYZ(site.x, site.y - d, site.z));
			pit.Add(new XYZ(site.x + 1, site.y - d, site.z));
		}
		Kill(pit);
		yield return TestWorld.WaitForGraph(t);
		Labor first = Scenario.Bricktron(t, Occupation.Type.Builder, new XYZ(site.x, site.y - 4, site.z));
		Labor second = Scenario.Bricktron(t, Occupation.Type.Builder, new XYZ(site.x + 1, site.y - 4, site.z));
		// They wait for orders: once out they stand where the escape left them.
		first.Autonomy = AutonomyStatus.WaitForOrders;
		second.Autonomy = AutonomyStatus.WaitForOrders;
		yield return t.Seconds(1f);
		t.True(Units.VoxelOf(first).y < site.y - 2 && Units.VoxelOf(second).y < site.y - 2, "The bricktrons are not in the pit");
		float started = Time.time;
		yield return t.UntilGame(() => OutOfPit(first, site) && OutOfPit(second, site), 300f, "both bricktrons to get out of the pit", 30f, () =>
		{
			t.Log(first.name + " at " + Units.VoxelOf(first) + ", " + second.name + " at " + Units.VoxelOf(second));
		});
		t.Record("outAfter", Mathf.Round(Time.time - started));
		// The walk on from the mouth.
		yield return t.Seconds(8f);
		foreach (Labor labor in new[] { first, second })
		{
			XYZ at = Units.VoxelOf(labor);
			if (!StuckEscape.LastMouth(labor, out XYZ mouth))
			{
				// Let out by the other's tunnel: it walked out on its own.
				t.Log(labor.name + " walked out through the other's tunnel, at " + at);
				continue;
			}
			t.Log(labor.name + " came out at " + mouth + ", stands at " + at);
			t.True(Mathf.Abs(at.x - mouth.x) > 1 || Mathf.Abs(at.z - mouth.z) > 1 || Mathf.Abs(at.y - mouth.y) > 2, labor.name + " still stands on the tunnel's mouth " + mouth + " (at " + at + ")");
		}
	}

	[GameTest("Tasks", "A bricktron carrying stones at the bottom of a way down one block wide and 5 deep takes them up to a stockpile; nothing is put down on the way", TimeoutSeconds = 300f)]
	private static IEnumerator LoadedWorkerInNarrowWayStores(TestContext t)
	{
		XYZ site = Scenario.Site(t, 16, 9, 4);
		Scenario.LeavesMess(t);
		BuildProject build = Scenario.BuildProject(t, site);
		yield return Scenario.Building(t, TaskTests.Pallet, new XYZ(site.x - 5, site.y, site.z + 3), build, _ => { });
		// A way down along x one block wide: a step down each block for 5 blocks, then 2 blocks of floor at the bottom.
		int x0 = site.x - 3;
		List<XYZ> way = new List<XYZ>();
		for (int i = 1; i <= 7; i++)
		{
			int feet = site.y - Mathf.Min(i, 5);
			for (int y = feet; y < site.y; y++)
			{
				way.Add(new XYZ(x0 + i, y, site.z));
			}
		}
		Kill(way);
		yield return TestWorld.WaitForGraph(t);
		Labor worker = Scenario.Bricktron(t, Occupation.Type.Builder, new XYZ(x0 + 7, site.y - 5, site.z));
		worker.Autonomy = AutonomyStatus.WaitForOrders;
		yield return t.Frames(5);
		List<GameObject> stones = new List<GameObject>();
		foreach (GameObject item in Scenario.Items(t, RawResources.RawStone, 6, worker.transform.position + Vector3.up * 3f))
		{
			if (worker.recepteur.HasRoomFor(item))
			{
				worker.recepteur.AddObject(item);
				stones.Add(item);
			}
			else
			{
				Scenario.Release(item);
			}
		}
		t.True(stones.Count > 0, "The bag took no stones");
		yield return t.Seconds(1f);
		LoadRelief.Relieve(worker);
		yield return t.UntilGame(() => worker.recepteur.IsEmpty(), 200f, "the worker to get rid of its stones", 20f, () =>
		{
			t.Log(worker.name + " at " + Units.VoxelOf(worker) + " carrying " + worker.recepteur.ContentString());
		});
		int stored = 0;
		foreach (GameObject stone in stones)
		{
			if (stone == null)
			{
				continue;
			}
			XYZ at = XYZ.FromVector3(stone.transform.position);
			bool loose = stone.GetComponentInParent<Recepteur>() == null;
			if (loose)
			{
				t.False(at.x > x0 && at.x <= x0 + 7 && at.z == site.z && at.y < site.y, "A stone was put down in the way down at " + at);
			}
			else
			{
				stored++;
			}
		}
		t.Record("stored", stored);
		t.Equal(stones.Count, stored, "Stones in a stockpile");
	}

	[GameTest("Tasks", "A bricktron with a load no storage takes, standing in a quarry, does not put it down on the quarry's floor", TimeoutSeconds = 180f)]
	private static IEnumerator NoLoadPutDownInAQuarry(TestContext t)
	{
		XYZ site = Scenario.Site(t, 9, 9);
		Scenario.LeavesMess(t);
		int top = site.y - 1;
		MineProject quarry = BuildRepairTests.NewQuarry(t, site, top, 4, out QuarryData data);
		yield return t.Frames(5);
		VoxelBounds box = data.Bounds;
		Labor worker = Scenario.Bricktron(t, Occupation.Type.Builder, new XYZ(site.x, site.y, site.z));
		worker.Autonomy = AutonomyStatus.WaitForOrders;
		yield return t.Frames(5);
		GameObject plank = Scenario.Items(t, FactoryBlocks.Wood_DoublePlank_AA, 1, worker.transform.position + Vector3.up * 3f)[0];
		t.True(worker.recepteur.HasRoomFor(plank), "The bag has no room for a plank");
		worker.recepteur.AddObject(plank);
		if (worker.BestRecepteurToStoreInventory() != null)
		{
			t.Skip("A storage takes planks (" + worker.BestRecepteurToStoreInventory().name + ")");
		}
		t.True(QuarryStairs.InQuarry(Units.VoxelOf(worker)), "The worker at " + Units.VoxelOf(worker) + " does not stand in the quarry " + box.min + " - " + box.max);
		t.False(LoadRelief.CanDropHere(worker), "A load may be put down in the quarry at " + Units.VoxelOf(worker));
		LoadRelief.Relieve(worker);
		yield return t.Seconds(30f);
		XYZ at = XYZ.FromVector3(plank.transform.position);
		bool carried = worker.recepteur.Carries(plank);
		t.Record("carried", carried);
		t.Record("plankAt", at);
		if (!carried)
		{
			t.False(at.x >= box.min.x && at.x <= box.max.x && at.z >= box.min.z && at.z <= box.max.z && at.y <= box.max.y, "The plank was put down in the quarry at " + at);
		}
		t.NotNull(quarry, "The quarry");
	}

	private static bool OutOfPit(Labor labor, XYZ site)
	{
		XYZ at = Units.VoxelOf(labor);
		return at.y >= site.y || Mathf.Max(Mathf.Abs(at.x - site.x), Mathf.Abs(at.z - site.z)) > 3;
	}

	// The voxels broken as digging breaks them (their own damage receivers), so the route graph hears of it.
	private static void Kill(List<XYZ> voxels)
	{
		foreach (XYZ at in voxels)
		{
			using (IVoxel voxel = Voxel.GetVoxel(at))
			{
				if (voxel != null && voxel.DamageReceiver != null)
				{
					voxel.DamageReceiver.Kill();
				}
			}
		}
	}
}
#endif
