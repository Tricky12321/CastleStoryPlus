using System.Collections;
using System.Collections.Generic;
using Brix.Engine;
using Brix.Engine.Blocks;
using Brix.External.Factories;
using Brix.Game.AI.Damage;
using Brix.Game.Components;
using Brix.Legacy;
using Brix.Components;
using Brix.Utils;
using CastleStoryPlus.DevTools.Building;
using CastleStoryPlus.DevTools.Testing;
using UnityEngine;
using FactoryBlocks = Brix.External.Factories.Blocks;
using Object = UnityEngine.Object;

namespace CastleStoryPlus.DevTools.Tests;

// Building stability: a stone brick needs a valid block or solid ground straight under it (its support cell,
// AbstractValidator); a block that loses it becomes a falling block (BlocLibre) and leaves its voxel empty, and the
// check cascades up through everything resting on it. Supported structures must stand.
[ParallelTests]
internal static class StructureTests
{
	// The support check runs in the LateUpdate after a change and the falling block is made a frame or two later;
	// generous, as a slow frame or a missed FixedUpdate adds a frame.
	private const float SettleSeconds = 3f;

	private static readonly Factory.AssetKey StoneBlueprint = new Factory.AssetKey("Blueprints", FactoryBlocks.Stone_Brick.Name);

	private static BlockEngine Engine => BrixSingleton<BlockEngine>.Instance;

	[GameTest("Structures", "A column of 6 stone bricks on the ground and a wall of 5 x 3 stand: no block falls")]
	private static IEnumerator SupportedStructureStands(TestContext t)
	{
		XYZ site = Scenario.Site(t, 9, 5, 8);
		List<XYZ> blocks = new List<XYZ>();
		for (int y = 0; y < 6; y++)
		{
			blocks.Add(new XYZ(site.x - 3, site.y + y, site.z));
		}
		for (int x = 0; x < 5; x++)
		{
			for (int y = 0; y < 3; y++)
			{
				blocks.Add(new XYZ(site.x - 1 + x, site.y + y, site.z));
			}
		}
		int freeBefore = FreeBlocksNear(site, 8);
		Place(t, blocks);
		yield return t.Frames(5);
		yield return t.Seconds(SettleSeconds);
		foreach (XYZ voxel in blocks)
		{
			t.True(Engine.IsBlock(voxel), "The supported block at " + voxel + " is gone");
		}
		t.Equal(freeBefore, FreeBlocksNear(site, 8), "Falling blocks near the structure");
	}

	[GameTest("Structures", "A stone brick placed 3 voxels above the ground, with nothing under it, falls")]
	private static IEnumerator FloatingBlockFalls(TestContext t)
	{
		XYZ site = Scenario.Site(t, 3, 3, 6);
		XYZ floating = new XYZ(site.x, site.y + 3, site.z);
		int freeBefore = FreeBlocksNear(site, 6);
		Place(t, new List<XYZ> { floating });
		yield return t.UntilGame(() => FreeBlocksNear(site, 6) > freeBefore, SettleSeconds * 2f, "the floating block to fall");
		t.False(Engine.IsBlock(floating), "The floating block is still in its voxel " + floating);
	}

	[GameTest("Structures", "A column of 5 loses its bottom block (destroyed as by an attack): the 4 above it fall")]
	private static IEnumerator ColumnCollapsesWhenBaseDestroyed(TestContext t)
	{
		XYZ site = Scenario.Site(t, 3, 3, 8);
		List<XYZ> column = new List<XYZ>();
		for (int y = 0; y < 5; y++)
		{
			column.Add(new XYZ(site.x, site.y + y, site.z));
		}
		Place(t, column);
		yield return t.Until(() => column.TrueForAll(v => Engine.IsBlock(v)), 5f, "the column to be registered");
		yield return t.Seconds(1f);
		t.True(column.TrueForAll(v => Engine.IsBlock(v)), "The column did not stand before its base was taken");
		int freeBefore = FreeBlocksNear(site, 8);
		PlacedBlockDamageReceiver receiver = ((BlockData)Engine.GetBlockData(column[0]).RootOrSelf).PlacedBlock.GetComponent<PlacedBlockDamageReceiver>();
		t.NotNull(receiver, "The bottom block's damage receiver");
		receiver.Kill();
		yield return t.UntilGame(() => column.TrueForAll(v => !Engine.IsBlock(v)), SettleSeconds, "the whole column to come down");
		yield return t.Frames(3);
		int fallen = FreeBlocksNear(site, 8) - freeBefore;
		t.Record("fallingBlocks", fallen);
		t.AtLeast(4f, fallen, "Falling blocks made from the 4 blocks above the base");
	}

	[GameTest("Structures", "A block beside the top of a column (an overhang, nothing under it) falls; the column stands")]
	private static IEnumerator OverhangFalls(TestContext t)
	{
		XYZ site = Scenario.Site(t, 5, 3, 6);
		List<XYZ> column = new List<XYZ>();
		for (int y = 0; y < 3; y++)
		{
			column.Add(new XYZ(site.x, site.y + y, site.z));
		}
		Place(t, column);
		yield return t.Until(() => column.TrueForAll(v => Engine.IsBlock(v)), 5f, "the column to be registered");
		XYZ overhang = new XYZ(site.x + 1, site.y + 2, site.z);
		Place(t, new List<XYZ> { overhang });
		yield return t.Until(() => Engine.IsBlock(overhang), 5f, "the overhanging block to be registered");
		yield return t.UntilGame(() => !Engine.IsBlock(overhang), SettleSeconds * 2f, "the overhanging block to fall");
		for (int frame = 0; frame <= 60; frame++)
		{
			if (frame % 10 == 0)
			{
				string state = string.Empty;
				foreach (XYZ voxel in column)
				{
					state += Engine.IsBlock(voxel) ? "#" : ".";
				}
				t.Log("Frame " + frame + " after the fall, the column bottom to top: " + state);
			}
			yield return null;
		}
		foreach (XYZ voxel in column)
		{
			t.True(Engine.IsBlock(voxel), "The column's block at " + voxel + " fell with the overhang");
		}
	}

	[GameTest("Structures", "The removed middle of a column: the blocks above fall, the block under it stays")]
	private static IEnumerator RemovedMiddleDropsTheTop(TestContext t)
	{
		XYZ site = Scenario.Site(t, 3, 3, 8);
		List<XYZ> column = new List<XYZ>();
		for (int y = 0; y < 5; y++)
		{
			column.Add(new XYZ(site.x, site.y + y, site.z));
		}
		Place(t, column);
		yield return t.Until(() => column.TrueForAll(v => Engine.IsBlock(v)), 5f, "the column to be registered");
		yield return t.Seconds(1f);
		IBlockData middle = Engine.GetBlockData(column[2]);
		t.NotNull(middle, "The middle block's data");
		middle.RootOrSelf.DestroyBlock(true, true);
		yield return t.UntilGame(() => !Engine.IsBlock(column[3]) && !Engine.IsBlock(column[4]), SettleSeconds, "the two blocks above the gap to fall");
		yield return t.Seconds(1f);
		t.True(Engine.IsBlock(column[0]) && Engine.IsBlock(column[1]), "The blocks under the gap fell too");
	}

	[GameTest("Structures", "Blueprint check: a stone brick on the ground is buildable, one in the air is not")]
	private static IEnumerator BlueprintNeedsSupport(TestContext t)
	{
		XYZ site = Scenario.Site(t, 3, 3, 6);
		BlueprintPlacer.Fit onGround = BlueprintPlacer.Try(StoneBlueprint, site, 0);
		BlueprintPlacer.Fit inAir = BlueprintPlacer.Try(StoneBlueprint, new XYZ(site.x, site.y + 3, site.z), 0);
		t.NotNull(onGround, "The fitted blueprint on the ground");
		t.NotNull(inAir, "The fitted blueprint in the air");
		t.Record("onGround", onGround.Check.ToString());
		t.Record("inAir", inAir.Check.ToString());
		t.True((onGround.Check & ValidationResult.Buildable) != 0, "A stone brick on the ground is not buildable: " + onGround.Check);
		t.True((inAir.Check & ValidationResult.Buildable) == 0, "A stone brick in the air is buildable: " + inAir.Check);
		yield break;
	}

	[GameTest("Structures", "A tower of 15 stone bricks stands", TimeoutSeconds = 60f)]
	private static IEnumerator TallTowerStands(TestContext t)
	{
		XYZ site = Scenario.Site(t, 3, 3, 17);
		List<XYZ> tower = Column(site, 15);
		Place(t, tower);
		yield return t.Until(() => tower.TrueForAll(v => Engine.IsBlock(v)), 5f, "the tower to be registered");
		yield return t.Seconds(SettleSeconds);
		foreach (XYZ voxel in tower)
		{
			t.True(Engine.IsBlock(voxel), "The tower's block at " + voxel + " fell");
		}
	}

	[GameTest("Structures", "A column of 10 loses its base: all 9 above it come down")]
	private static IEnumerator TenHighColumnCascades(TestContext t)
	{
		XYZ site = Scenario.Site(t, 3, 3, 12);
		List<XYZ> column = Column(site, 10);
		Place(t, column);
		yield return t.Until(() => column.TrueForAll(v => Engine.IsBlock(v)), 5f, "the column to be registered");
		yield return t.Seconds(1f);
		Engine.GetBlockData(column[0]).RootOrSelf.DestroyBlock(true, true);
		yield return t.UntilGame(() => column.TrueForAll(v => !Engine.IsBlock(v)), SettleSeconds, "the whole column to come down");
	}

	[GameTest("Structures", "Two columns side by side; one loses its base: only that one falls, the other stands")]
	private static IEnumerator NeighbourColumnStands(TestContext t)
	{
		XYZ site = Scenario.Site(t, 5, 3, 6);
		List<XYZ> falling = Column(site, 4);
		List<XYZ> standing = Column(new XYZ(site.x + 1, site.y, site.z), 4);
		List<XYZ> both = new List<XYZ>(falling);
		both.AddRange(standing);
		Place(t, both);
		yield return t.Until(() => both.TrueForAll(v => Engine.IsBlock(v)), 5f, "the columns to be registered");
		yield return t.Seconds(1f);
		Engine.GetBlockData(falling[0]).RootOrSelf.DestroyBlock(true, true);
		yield return t.UntilGame(() => falling.TrueForAll(v => !Engine.IsBlock(v)), SettleSeconds, "the first column to come down");
		yield return t.Seconds(1f);
		foreach (XYZ voxel in standing)
		{
			t.True(Engine.IsBlock(voxel), "The neighbouring column's block at " + voxel + " fell too");
		}
	}

	[GameTest("Structures", "The ground under a stone brick is dug away: the brick falls")]
	private static IEnumerator BlockFallsWhenGroundRemoved(TestContext t)
	{
		XYZ site = Scenario.Site(t, 3, 3, 4);
		Scenario.LeavesMess(t);
		XYZ brick = new XYZ(site.x, site.y, site.z);
		XYZ ground = new XYZ(site.x, site.y - 1, site.z);
		t.True(Voxel.IsFull(ground) && !Voxel.IsInvincible(ground), "The ground under the brick cannot be dug");
		Place(t, new List<XYZ> { brick });
		yield return t.Until(() => Engine.IsBlock(brick), 5f, "the brick to be registered");
		yield return t.Seconds(1f);
		// As digging does: the ground voxel's own damage receiver (TerrainDamageReceiver) out of hit points.
		using (IVoxel voxel = Voxel.GetVoxel(ground))
		{
			t.True(voxel != null && voxel.DamageReceiver != null, "The ground voxel has no damage receiver");
			voxel.DamageReceiver.Kill();
		}
		yield return t.UntilGame(() => !Voxel.IsFull(ground), SettleSeconds, "the ground to be dug away");
		yield return t.UntilGame(() => !Engine.IsBlock(brick), SettleSeconds, "the brick to fall into the hole");
	}

	private static List<XYZ> Column(XYZ bottom, int height)
	{
		List<XYZ> column = new List<XYZ>();
		for (int y = 0; y < height; y++)
		{
			column.Add(new XYZ(bottom.x, bottom.y + y, bottom.z));
		}
		return column;
	}

	// Stone bricks placed in one frame (they register together in the next, so a column holds itself up); taken
	// away again by the clean-up together with the falling blocks they made.
	private static void Place(TestContext t, List<XYZ> voxels)
	{
		HashSet<BlocLibre> before = new HashSet<BlocLibre>(Object.FindObjectsOfType<BlocLibre>());
		TestWorld.PlaceBlocks(t, voxels);
		t.Cleanup(() =>
		{
			foreach (BlocLibre free in Object.FindObjectsOfType<BlocLibre>())
			{
				if (free != null && !before.Contains(free) && NearAny(free.transform.position, voxels, 12f))
				{
					Scenario.Release(free.gameObject);
				}
			}
		});
	}

	private static bool NearAny(Vector3 position, List<XYZ> voxels, float distance)
	{
		foreach (XYZ voxel in voxels)
		{
			if (Vector3.Distance(position, voxel.ToVector3()) <= distance)
			{
				return true;
			}
		}
		return false;
	}

	private static int FreeBlocksNear(XYZ site, float radius)
	{
		int count = 0;
		Vector3 center = site.ToVector3();
		foreach (BlocLibre free in Object.FindObjectsOfType<BlocLibre>())
		{
			if (free != null && free.gameObject.activeInHierarchy && Vector3.Distance(free.transform.position, center) <= radius)
			{
				count++;
			}
		}
		return count;
	}
}
