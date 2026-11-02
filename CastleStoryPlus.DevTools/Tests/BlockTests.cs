using System.Collections;
using System.Collections.Generic;
using Brix.Components;
using Brix.Engine;
using Brix.Engine.Blocks;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.AI.Damage;
using Brix.Game.Components;
using Brix.Game.Network;
using Brix.Utils;
using CastleStoryPlus.DevTools.Building;
using CastleStoryPlus.DevTools.Testing;
using FactoryBlocks = Brix.External.Factories.Blocks;

namespace CastleStoryPlus.DevTools.Tests;

// Single blocks and blueprints: where a blueprint may go (not into a block, not resting on another blueprint),
// a blueprint finished becomes a block and a demolition takes it away, and a block's hit points: hurt it stays,
// out of hit points it is destroyed.
[ParallelTests]
internal static class BlockTests
{
	private static readonly Factory.AssetKey StoneBlueprint = new Factory.AssetKey("Blueprints", FactoryBlocks.Stone_Brick.Name);

	private static BlockEngine Engine => BrixSingleton<BlockEngine>.Instance;

	[GameTest("Blocks", "A blueprint where a stone brick already is: obstructed")]
	private static IEnumerator BlueprintIntoBlockIsObstructed(TestContext t)
	{
		XYZ site = Scenario.Site(t, 3, 3, 4);
		TestWorld.PlaceBlocks(t, new List<XYZ> { site });
		yield return t.Until(() => Engine.IsBlock(site), 5f, "the brick to be registered");
		BlueprintPlacer.Fit fit = BlueprintPlacer.Try(StoneBlueprint, site, 0);
		t.NotNull(fit, "The fitted blueprint");
		t.Record("check", fit.Check.ToString());
		t.True((fit.Check & ValidationResult.Obstructed) != 0, "A blueprint inside a brick is not obstructed: " + fit.Check);
	}

	[GameTest("Blocks", "A blueprint resting on another blueprint: valid but not buildable until the one under it is built")]
	private static IEnumerator BlueprintOnBlueprintWaits(TestContext t)
	{
		XYZ site = Scenario.Site(t, 3, 3, 4);
		BuildProject project = Scenario.BuildProject(t, site);
		BlueprintPlacer.Result lower = BlueprintPlacer.Place(StoneBlueprint, site, 0, project, force: false);
		t.True(lower.Placed, "The lower blueprint: " + lower.Outcome + " " + lower.Detail);
		RemoveAfter(t, lower.Blueprint);
		yield return t.Frames(3);
		BlueprintPlacer.Fit upper = BlueprintPlacer.Try(StoneBlueprint, new XYZ(site.x, site.y + 1, site.z), 0);
		t.NotNull(upper, "The fitted upper blueprint");
		t.Record("upper", upper.Check.ToString());
		t.True((upper.Check & ValidationResult.Obstructed) == 0, "The upper blueprint is obstructed: " + upper.Check);
		t.True((upper.Check & ValidationResult.Buildable) == 0, "The upper blueprint is buildable though it rests on a blueprint: " + upper.Check);
		t.True((upper.Check & ValidationResult.Valid) != 0, "The upper blueprint is not valid (it rests on a blueprint): " + upper.Check);
	}

	[GameTest("Blocks", "A stone brick blueprint finished (as the last material arriving does) becomes a stone brick")]
	private static IEnumerator FinishedBlueprintBecomesBlock(TestContext t)
	{
		XYZ site = Scenario.Site(t, 3, 3, 4);
		BuildProject project = Scenario.BuildProject(t, site);
		BlueprintPlacer.Result placed = BlueprintPlacer.Place(StoneBlueprint, site, 0, project, force: false);
		t.True(placed.Placed, "The blueprint: " + placed.Outcome + " " + placed.Detail);
		RemoveBlockAfter(t, site);
		InstantBuild.Complete(new[] { placed.Blueprint });
		yield return t.Until(() => Engine.IsBlock(site), 5f, "the stone brick to be there");
	}

	[GameTest("Blocks", "A stone brick demolished at once with the remove tool's command is gone")]
	private static IEnumerator DemolishedBlockIsGone(TestContext t)
	{
		XYZ site = Scenario.Site(t, 3, 3, 4);
		TestWorld.PlaceBlocks(t, new List<XYZ> { site });
		yield return t.Until(() => Engine.IsBlock(site), 5f, "the brick to be registered");
		yield return t.Frames(3);
		BlueprintPlacer.Result result = BlueprintPlacer.Demolish(StoneBlueprint, site, 0, null, instant: true);
		t.True(result.Placed, "The demolition: " + result.Outcome + " " + result.Detail);
		yield return t.UntilGame(() => !Engine.IsBlock(site), 5f, "the brick to be gone");
	}

	[GameTest("Blocks", "A stone brick that loses a third of its hit points stays, damaged")]
	private static IEnumerator DamagedBlockStays(TestContext t)
	{
		XYZ site = Scenario.Site(t, 3, 3, 4);
		TestWorld.PlaceBlocks(t, new List<XYZ> { site });
		yield return t.Until(() => Engine.IsBlock(site), 5f, "the brick to be registered");
		PlacedBlockDamageReceiver receiver = Receiver(t, site);
		float max = receiver.MaxHP;
		receiver.ApplyDamage(max / 3f, DamageType.Melee, null);
		yield return t.Frames(5);
		t.Record("hp", receiver.CurrentHP + " of " + max);
		t.True(Engine.IsBlock(site), "The brick is gone after a third of its hit points");
		t.True(receiver.CurrentHP < max, "The brick took no damage");
	}

	[GameTest("Blocks", "A stone brick that loses all its hit points is destroyed")]
	private static IEnumerator BlockDestroyedByDamage(TestContext t)
	{
		XYZ site = Scenario.Site(t, 3, 3, 4);
		TestWorld.PlaceBlocks(t, new List<XYZ> { site });
		yield return t.Until(() => Engine.IsBlock(site), 5f, "the brick to be registered");
		PlacedBlockDamageReceiver receiver = Receiver(t, site);
		receiver.ApplyDamage(receiver.MaxHP * 2f, DamageType.Melee, null);
		yield return t.UntilGame(() => !Engine.IsBlock(site), 5f, "the brick to be destroyed");
	}

	// ---- helpers

	private static PlacedBlockDamageReceiver Receiver(TestContext t, XYZ voxel)
	{
		BlockData block = Engine.GetBlockData(voxel).RootOrSelf as BlockData;
		t.NotNull(block, "The brick's block data");
		PlacedBlockDamageReceiver receiver = block.PlacedBlock.GetComponent<PlacedBlockDamageReceiver>();
		t.NotNull(receiver, "The brick's damage receiver");
		return receiver;
	}

	// The player's delete of a blueprint not yet built (UNetBlueprint.CmdDestroyBluePrint).
	private static void RemoveAfter(TestContext t, Blueprint blueprint)
	{
		t.Cleanup(() =>
		{
			if (blueprint != null && !blueprint.Released)
			{
				User.LocalUser.GetComponent<UNetBlueprint>().CmdDestroyBluePrint(blueprint.gameObject);
			}
		});
	}

	private static void RemoveBlockAfter(TestContext t, XYZ voxel)
	{
		t.Cleanup(() =>
		{
			IBlockData block = Engine.GetBlockData(voxel);
			if (block != null && Engine.IsBlock(voxel))
			{
				block.RootOrSelf.DeBlock();
			}
		});
	}
}
