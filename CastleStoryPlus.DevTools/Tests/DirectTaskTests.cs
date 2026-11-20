using System.Collections;
using System.Collections.Generic;
using Brix.Engine;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Game.Network;
using CastleStoryPlus.DevTools.Building;
using CastleStoryPlus.DevTools.Testing;
using CastleStoryPlus.WorkerAI;
using UnityEngine;
using FactoryBlocks = Brix.External.Factories.Blocks;

namespace CastleStoryPlus.DevTools.Tests;

// The right-click on a blueprint (DirectTask): only a shown blueprint is a target, and only for the workers of the
// selection; soldiers get the game's move on the ground behind it (#147).
[ParallelTests]
internal static class DirectTaskTests
{
	private static readonly Factory.AssetKey StoneBlueprint = new Factory.AssetKey("Blueprints", FactoryBlocks.Stone_Brick.Name);

	[GameTest("Tasks", "A ray onto a shown blueprint finds it; onto the same blueprint hidden (renderers off) it passes through to the ground and finds no blueprint (#147)")]
	private static IEnumerator HiddenBlueprintIsNoTarget(TestContext t)
	{
		XYZ site = Scenario.Site(t, 3, 3, 4);
		BuildProject project = Scenario.BuildProject(t, site);
		BlueprintPlacer.Result placed = BlueprintPlacer.Place(StoneBlueprint, site, 0, project, force: false);
		t.True(placed.Placed, "The blueprint: " + placed.Outcome + " " + placed.Detail);
		Blueprint blueprint = placed.Blueprint;
		t.Cleanup(() =>
		{
			if (blueprint != null && !blueprint.Released)
			{
				User.LocalUser.GetComponent<UNetBlueprint>().CmdDestroyBluePrint(blueprint.gameObject);
			}
		});
		StateRenderer renderer = blueprint.GetComponent<StateRenderer>();
		t.NotNull(renderer, "The blueprint's StateRenderer");
		yield return t.Frames(3);
		Ray down = new Ray(site.ToVector3() + Vector3.up * 8f, Vector3.down);

		renderer.Show(true);
		yield return t.Frames(1);
		t.True(DirectTask.IsShown(blueprint), "The blueprint is not shown after Show(true)");
		GameObject shown = DirectTask.BlueprintAlong(down, 20f);
		t.Record("shownHit", shown != null ? shown.name : "none");
		t.True(shown == blueprint.gameObject, "The ray onto the shown blueprint found " + (shown != null ? shown.name : "nothing"));

		renderer.Show(false);
		yield return t.Frames(1);
		t.True(!DirectTask.IsShown(blueprint), "The blueprint is shown after Show(false)");
		GameObject hidden = DirectTask.BlueprintAlong(down, 20f);
		t.Record("hiddenHit", hidden != null ? hidden.name : "none");
		t.True(hidden == null, "The ray onto the hidden blueprint found " + (hidden != null ? hidden.name : "nothing"));
		renderer.Show(true);
	}

	[GameTest("Tasks", "A mixed selection splits into workers (builders, who take the blueprint) and soldiers (knights and archers, who get the move on the ground) (#147)")]
	private static IEnumerator SoldiersAreNotWorkers(TestContext t)
	{
		XYZ site = Scenario.Site(t, 6, 3, 4);
		Labor builder = Scenario.Bricktron(t, Occupation.Type.Builder, new XYZ(site.x - 2, site.y, site.z));
		Labor knight = Scenario.Bricktron(t, Occupation.Type.Knight, new XYZ(site.x, site.y, site.z));
		Labor archer = Scenario.Bricktron(t, Occupation.Type.Archer, new XYZ(site.x + 2, site.y, site.z));
		yield return t.Frames(5);
		List<GameObject> selection = new List<GameObject> { builder.gameObject, knight.gameObject, archer.gameObject };
		List<GameObject> workers = new List<GameObject>();
		List<GameObject> others = new List<GameObject>();
		DirectTask.SplitByWork(selection, workers, others);
		t.Record("workers", workers.Count);
		t.Record("others", others.Count);
		t.True(workers.Count == 1 && workers[0] == builder.gameObject, "The workers of the selection: " + workers.Count);
		t.True(others.Count == 2 && others.Contains(knight.gameObject) && others.Contains(archer.gameObject), "The soldiers of the selection: " + others.Count);
	}
}
