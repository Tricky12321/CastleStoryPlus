using System.Collections;
using System.Collections.Generic;
using Brix.Components;
using Brix.Engine;
using Brix.External.Factories;
using Brix.Game.AI;
using Brix.Game.AI.Nodes;
using Brix.Game.Components;
using Brix.Game.Semantique;
using Brix.Lifecycle.Pooling;
using Brix.Transactions;
using CastleStoryPlus.Army;
using CastleStoryPlus.Building;
using CastleStoryPlus.Core;
using CastleStoryPlus.DevTools.Pathing;
using CastleStoryPlus.DevTools.Testing;
using UnityEngine;

namespace CastleStoryPlus.DevTools.Tests;

// Kit bundles (KitBundles) on a weapon stand of the player: a loose knight kit is packed into one bundle, a builder
// equipping from the stand unpacks it and becomes a knight, and handing the kit in packs it again.
internal static class KitBundleTests
{
	private static readonly Factory.AssetKey Toolrack = new Factory.AssetKey("Blueprints", "Toolrack");

	[GameTest("KitBundles", "A loose knight kit on a weapon stand is packed; equipping unpacks it, storing packs it again", TimeoutSeconds = 60f)]
	private static IEnumerator PackEquipStore(TestContext t)
	{
		if (!Plugin.IsEnabled(Features.KitBundles))
		{
			t.Skip("KitBundles is switched off");
		}
		ToolRack rack = PlayerRack();
		if (rack == null)
		{
			// A cleared world has none: one of the test's own, built at once.
			XYZ site = Scenario.Site(t, 4, 4);
			yield return Scenario.Building(t, Toolrack, site, Scenario.BuildProject(t, site), go => rack = go.GetComponent<ToolRack>());
			t.NotNull(rack, "The weapon stand built for the test");
		}
		Labor labor = Scenario.Bricktron(t, Occupation.Type.Builder, XYZ.FromVector3(rack.transform.position) + new XYZ(0, 0, -2));
		yield return t.Frames(5);
		KitBundles.Bundle knight = KitBundles.All[0];
		Recepteur recepteur = rack.recepteur;
		int before = recepteur.ContentDescription.Value(knight.Resource);
		int swordsBefore = recepteur.ContentDescription.Value(Adjectif.sword);
		t.Record("bundlesBefore", before);
		foreach (RecipeInfo piece in knight.PieceRecipes)
		{
			if (!recepteur.HasRoomFor(piece.Creates))
			{
				t.Skip("The weapon stand has no room for a knight kit");
			}
		}
		t.Cleanup(() =>
		{
			// The builder hands back what it still wears, and the bundle made here goes again.
			List<GameObject> tools = new List<GameObject>();
			labor.Profession.toolbag.Tools(ref tools);
			if (tools.Count > 0)
			{
				ToolNode.StoreToolsIn(labor, rack.gameObject);
			}
			if (recepteur.ContentDescription.Value(knight.Resource) > before && recepteur.FirstObjectOf(knight.Resource, out GameObject extra))
			{
				extra.SafeNetworkDestroy();
			}
		});
		foreach (RecipeInfo piece in knight.PieceRecipes)
		{
			GameObject go = Transactor.SpawnUNet(piece.Creates, rack.faction, rack.transform.position, Quaternion.identity);
			t.NotNull(go, piece.Creates.ToString());
			recepteur.AddObject(go);
		}
		t.Log("Put a sword, a shield and a helmet on " + rack.name);
		yield return t.Until(() => recepteur.ContentDescription.Value(knight.Resource) > before, 5f, "the loose kit to be packed into a bundle");
		t.Equal(before + 1, recepteur.ContentDescription.Value(knight.Resource), "Knight kits on the stand after packing");

		MixedRacks.Want(labor, Occupation.Job.Knight);
		ToolNode.EquipRackContent(labor, rack.gameObject);
		yield return t.Until(() => labor.Occupation.CurrentJob == Occupation.Job.Knight, 5f, "the builder to become a knight");
		t.Equal(before, recepteur.ContentDescription.Value(knight.Resource), "Knight kits on the stand after equipping");

		bool stored = ToolNode.StoreToolsIn(labor, rack.gameObject);
		t.True(stored, "The knight could not hand in its kit");
		yield return t.Until(() => labor.Occupation.CurrentJob == Occupation.Job.Builder, 5f, "the knight to become a builder");
		t.Equal(before + 1, recepteur.ContentDescription.Value(knight.Resource), "Knight kits on the stand after storing");
		t.True(recepteur.ContentDescription.Value(Adjectif.sword) <= swordsBefore, "A loose sword of the test is left on the stand");
	}

	private static ToolRack PlayerRack()
	{
		HashSet<GameObject> racks = BrixSingleton<AutoList>.Instance.GetInstances(ObjetsDynamiques.Toolrack);
		if (racks == null || Brix.Game.User.LocalUser == null)
		{
			return null;
		}
		foreach (GameObject go in racks)
		{
			if (go != null && go.activeInHierarchy && Brix.Game.User.LocalUser.faction.IsSame(go))
			{
				ToolRack rack = go.GetComponent<ToolRack>();
				if (rack != null && rack.recepteur != null)
				{
					return rack;
				}
			}
		}
		return null;
	}
}
