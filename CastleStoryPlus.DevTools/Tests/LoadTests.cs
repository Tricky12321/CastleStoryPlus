using System.Collections;
using System.Collections.Generic;
using Brix.Engine;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Game.Semantique;
using CastleStoryPlus.DevTools.Building;
using CastleStoryPlus.DevTools.Endpoints;
using CastleStoryPlus.DevTools.Pathing;
using CastleStoryPlus.DevTools.Testing;
using UnityEngine;
using FactoryBlocks = Brix.External.Factories.Blocks;

namespace CastleStoryPlus.DevTools.Tests;

// Workers carrying a load they cannot store (issue #49): they must not stand frozen with it, but put it down (or
// store it, when there is room) and take the work that needs empty hands.
[ParallelTests("Economy")]
internal static class LoadTests
{
	private static readonly Factory.AssetKey StoneBlueprint = new Factory.AssetKey("Blueprints", FactoryBlocks.Stone_Brick.Name);

	[GameTest("Tasks", "A builder with a full bag of stones and no storage for them puts them down at once and builds its task's blueprints", TimeoutSeconds = 240f)]
	private static IEnumerator LoadedBuilderBuildsWithoutStorage(TestContext t)
	{
		XYZ site = Scenario.Site(t, 10, 6);
		BuildProject build = Scenario.BuildProject(t, site);
		Labor builder = Scenario.Bricktron(t, Occupation.Type.Builder, new XYZ(site.x - 3, site.y, site.z));
		yield return t.Frames(5);
		int loaded = Load(t, builder, RawResources.RawStone, 40);
		t.Record("loaded", loaded);
		if (builder.BestRecepteurToStoreInventory() != null)
		{
			t.Skip("A stockpile has room for stones (" + builder.BestRecepteurToStoreInventory().name + ")");
		}
		List<XYZ> wall = Wall(t, site, build);
		Scenario.Hire(build, builder);
		float started = Time.time;
		// The game's "store or drop" tried for 10 seconds before putting the load down where it stood (and could fail
		// to); now it is put down at once, by the gather point when that is near.
		yield return t.UntilGame(() => builder.recepteur.IsEmpty(), 40f, "the builder to put its stones down", 2f, () => t.Log(Describe(builder)));
		t.Record("putDownAfter", Mathf.Round((Time.time - started) * 10f) / 10f);
		yield return t.UntilGame(() => wall.TrueForAll(v => Voxel.IsBlock(v)), 120f, "the 2 blueprints to be built by the builder", 10f, () => t.Log(Describe(builder)));
		t.Record("gameSeconds", Mathf.Round(Time.time - started));
	}

	// Puts up to max of the item into the worker's bag (as many as it takes); returns how many.
	private static int Load(TestContext t, Labor labor, Factory.AssetKey key, int max)
	{
		int loaded = 0;
		foreach (GameObject item in Scenario.Items(t, key, max, labor.transform.position + Vector3.up * 3f))
		{
			if (labor.recepteur.HasRoomFor(item))
			{
				labor.recepteur.AddObject(item);
				loaded++;
			}
			else
			{
				Scenario.Release(item);
			}
		}
		t.True(loaded > 0, "The bag took no " + key.Name);
		return loaded;
	}

	// Two stone brick blueprints of the task, with their stone blocks lying loose by them; the task committed.
	private static List<XYZ> Wall(TestContext t, XYZ site, BuildProject build)
	{
		Scenario.Items(t, FactoryBlocks.Stone_DoubleBrick, 4, new XYZ(site.x + 3, site.y, site.z + 2).ToVector3());
		List<XYZ> wall = new List<XYZ> { new XYZ(site.x + 2, site.y, site.z - 1), new XYZ(site.x + 2, site.y, site.z) };
		foreach (XYZ voxel in wall)
		{
			BlueprintPlacer.Result placed = BlueprintPlacer.Place(StoneBlueprint, voxel, 0, build, force: false);
			t.True(placed.Placed, "Stone brick blueprint at " + voxel + ": " + placed.Outcome + " " + placed.Detail);
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
		return wall;
	}

	internal static string Describe(Labor labor)
	{
		if (!Scenario.Alive(labor))
		{
			return "(gone)";
		}
		Task task = labor.CurrentTask;
		return labor.name + ": " + labor.Activity + " in '" + ((task != null) ? task.Description : "-") + "' at " + Units.VoxelOf(labor) + " on " + ((labor.Project != null) ? labor.Project.GetType().Name : "nothing") + (labor.recepteur.IsEmpty() ? ", bag empty" : ", carrying " + labor.recepteur.ContentString());
	}
}
