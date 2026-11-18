using System.Collections;
using System.Collections.Generic;
using Brix.Components;
using Brix.Engine;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.AI.Nodes;
using Brix.Game.Components;
using Brix.Game.Semantique;
using Brix.Legacy;
using Brix.Transactions;
using CastleStoryPlus.Army;
using CastleStoryPlus.Building;
using CastleStoryPlus.Core;
using CastleStoryPlus.DevTools.Testing;
using CastleStoryPlus.Workers;
using UnityEngine;

namespace CastleStoryPlus.DevTools.Tests;

// Soldiers and their kits: a worker with a call to arms role takes its role's kit (#63), kits on a stand are reserved
// by the bricktron going for them (#59), ranged guards hold their place (#60), and units have posts (#61).
internal static class ArmyTests
{
	private static readonly Factory.AssetKey Toolrack = new Factory.AssetKey("Blueprints", "Toolrack");

	// A weapon stand of the test's own (empty), built at once.
	private static IEnumerator NewRack(TestContext t, System.Action<ToolRack> built)
	{
		XYZ site = Scenario.Site(t, 4, 4);
		ToolRack rack = null;
		yield return Scenario.Building(t, Toolrack, site, Scenario.BuildProject(t, site), go => rack = go.GetComponent<ToolRack>());
		t.NotNull(rack, "The weapon stand built for the test");
		// Its room is set up a moment after it is built.
		yield return t.Frames(30);
		t.Log("Stand room for a sword: " + rack.recepteur.CurrentCapacity.Value(Adjectif.sword) + ", base " + rack.recepteur.BaseCapacity.Value(Adjectif.sword));
		built(rack);
	}

	// Puts a whole loose kit of the class on the stand (it may be packed into a bundle there).
	private static void PutKit(TestContext t, ToolRack rack, Occupation.Job job)
	{
		KitBundles.Bundle bundle = KitBundles.ForKit(Kits.KitFor(job));
		t.NotNull(bundle, "The kit bundle of " + job);
		foreach (RecipeInfo piece in bundle.PieceRecipes)
		{
			if (!rack.recepteur.HasRoomFor(piece.Creates))
			{
				t.Skip("The weapon stand has no room for a " + job + " kit");
			}
			GameObject go = Transactor.SpawnUNet(piece.Creates, rack.faction, rack.transform.position, Quaternion.identity);
			t.NotNull(go, piece.Creates.ToString());
			rack.recepteur.AddObject(go);
		}
	}

	private static void HandBack(Labor labor, ToolRack rack)
	{
		if (labor.IsNullOrReleased() || labor.Profession == null || rack == null)
		{
			return;
		}
		List<GameObject> tools = new List<GameObject>();
		labor.Profession.toolbag.Tools(ref tools);
		if (tools.Count > 0)
		{
			ToolNode.StoreToolsIn(labor, rack.gameObject);
		}
	}

	[GameTest("Army", "A worker whose call to arms role is Knight takes the knight kit from a stand with more archer kits, and one sent for a knight kit takes nothing when only archer kits are left (#63)", TimeoutSeconds = 90f)]
	private static IEnumerator RoleKitFromMixedStand(TestContext t)
	{
		if (!Plugin.IsEnabled(Features.MixedRacks) || !Plugin.IsEnabled(Features.CallToArms))
		{
			t.Skip("MixedRacks or CallToArms is switched off");
		}
		ToolRack rack = null;
		yield return NewRack(t, r => rack = r);
		PutKit(t, rack, Occupation.Job.Archer);
		PutKit(t, rack, Occupation.Job.Archer);
		PutKit(t, rack, Occupation.Job.Knight);
		yield return t.Until(() => Kits.KitsFor(rack, Occupation.Job.Knight) == 1 && Kits.KitsFor(rack, Occupation.Job.Archer) == 2, 5f, "the kits to be on the stand");
		XYZ near = XYZ.FromVector3(rack.transform.position) + new XYZ(0, 0, -2);
		Labor knight = Scenario.Bricktron(t, Occupation.Type.Builder, near);
		Labor other = Scenario.Bricktron(t, Occupation.Type.Builder, near + new XYZ(1, 0, 0));
		t.Cleanup(() =>
		{
			HandBack(knight, rack);
			HandBack(other, rack);
		});
		yield return t.Frames(5);
		WorkerStats.Modify(knight.state, (WorkerStats s) => s.CallToArmsRole = (int)Occupation.Job.Knight);
		ToolNode.EquipRackContent(knight, rack.gameObject);
		yield return t.Until(() => knight.Occupation.CurrentOccupation != Occupation.Type.Builder, 5f, "the worker with the Knight role to take a kit");
		t.Equal(Occupation.Job.Knight, knight.Occupation.CurrentJob, "Class taken by the worker with the Knight role");

		t.Log("After the knight: knight kits " + Kits.KitsFor(rack, Occupation.Job.Knight) + ", archer kits " + Kits.KitsFor(rack, Occupation.Job.Archer) + ", other's role " + CallToArms.RoleOf(other) + ", other's job " + other.Occupation.CurrentJob);
		MixedRacks.Want(other, Occupation.Job.Knight);
		ToolNode.EquipRackContent(other, rack.gameObject);
		yield return t.Frames(30);
		t.Equal(Occupation.Job.Builder, other.Occupation.CurrentJob, "Class taken by a worker sent for a knight kit the stand no longer has");
		t.Equal(2, Kits.KitsFor(rack, Occupation.Job.Archer), "Archer kits left on the stand");
	}

	[GameTest("Army", "A knight kit on a stand is reserved by the first worker going for it: a second worker does not choose that stand (#59)", TimeoutSeconds = 90f)]
	private static IEnumerator KitIsReserved(TestContext t)
	{
		if (!Plugin.IsEnabled(Features.KitReservation))
		{
			t.Skip("KitReservation is switched off");
		}
		ToolRack rack = null;
		yield return NewRack(t, r => rack = r);
		PutKit(t, rack, Occupation.Job.Knight);
		yield return t.Until(() => Kits.KitsFor(rack, Occupation.Job.Knight) == 1, 5f, "the kit to be on the stand");
		XYZ near = XYZ.FromVector3(rack.transform.position) + new XYZ(0, 0, -2);
		Labor first = Scenario.Bricktron(t, Occupation.Type.Builder, near);
		Labor second = Scenario.Bricktron(t, Occupation.Type.Builder, near + new XYZ(1, 0, 0));
		t.Cleanup(() =>
		{
			KitReservation.Release(first);
			KitReservation.Release(second);
		});
		// The workers' map knows a new stand (and new workers) a moment later.
		yield return t.UntilGame(() => Brix.Game.AI.KnowledgeSpace.Knowledge.Instance.CanReach(first, rack) && Brix.Game.AI.KnowledgeSpace.Knowledge.Instance.CanReach(second, rack), 20f, "the workers to know the way to the stand");
		GameObject chosen = Kits.ClosestRackWithKit(first, Occupation.Job.Knight);
		t.Log("Chosen: " + ((chosen != null) ? chosen.name : "none") + ", can equip " + first.CanEquipRackContent(rack.gameObject) + ", can reach " + Brix.Game.AI.KnowledgeSpace.Knowledge.Instance.CanReach(first, rack));
		t.True(chosen == rack.gameObject, "The first worker did not choose the test's stand");
		t.Equal(0, KitReservation.FreeKits(rack, Occupation.Job.Knight, second), "Knight kits free for the second worker");
		GameObject second1 = Kits.ClosestRackWithKit(second, Occupation.Job.Knight);
		t.False(second1 == rack.gameObject, "The second worker chose the stand whose only knight kit is reserved");
		KitReservation.Release(first);
		t.Equal(1, KitReservation.FreeKits(rack, Occupation.Job.Knight, second), "Knight kits free once the reservation ends");
	}

	[GameTest("Army", "A guarding archer with a corruptron in range shoots it without leaving its place (#60)", TimeoutSeconds = 120f)]
	private static IEnumerator ArcherGuardsPlace(TestContext t)
	{
		if (!Plugin.IsEnabled(Features.RangedGuard))
		{
			t.Skip("RangedGuard is switched off");
		}
		XYZ site = Scenario.Site(t, 14, 6);
		Labor archer = Scenario.Bricktron(t, Occupation.Type.Archer, new XYZ(site.x - 5, site.y, site.z));
		yield return t.Frames(10);
		SoldierOrdersNetwork.SendGuard(archer.gameObject, true);
		t.True(RangedGuard.IsGuarding(archer), "The archer is not guarding");
		Vector3 start = archer.transform.position;
		Labor corruptron = Scenario.Corruptron(t, new XYZ(site.x + 4, site.y, site.z));
		float before = Scenario.Hp(corruptron);
		float farthest = 0f;
		yield return t.UntilGame(() =>
		{
			farthest = Mathf.Max(farthest, RangedFlat(archer.transform.position - start));
			return Scenario.Hp(corruptron) < before || !Scenario.Alive(archer);
		}, 60f, "an arrow to hit the corruptron");
		t.Record("farthest", farthest);
		t.AtMost(1.5f, farthest, "Blocks the guarding archer moved from its place");
		SoldierOrdersNetwork.SendGuard(archer.gameObject, false);
		t.False(RangedGuard.IsGuarding(archer), "The archer still guards after Stop guarding");
	}

	private static float RangedFlat(Vector3 v)
	{
		return new Vector3(v.x, 0f, v.z).magnitude;
	}

	[GameTest("Army", "A knight keeps its post: sent elsewhere, Back to post brings it there again (#61)", TimeoutSeconds = 120f)]
	private static IEnumerator KnightBackToPost(TestContext t)
	{
		if (!Plugin.IsEnabled(Features.SoldierPosts))
		{
			t.Skip("SoldierPosts is switched off");
		}
		XYZ site = Scenario.Site(t, 12, 6);
		Labor knight = Scenario.Bricktron(t, Occupation.Type.Knight, new XYZ(site.x - 4, site.y, site.z));
		yield return t.Frames(10);
		int set = SoldierPosts.SetHere(new[] { knight });
		t.Equal(1, set, "Units given a post");
		t.True(SoldierPosts.TryGet(knight, out Vector3 post), "The knight has no post");
		t.AtMost(0.1f, (post - knight.transform.position).magnitude, "Distance of the post from where the knight stood");
		Vector3 away = Scenario.Walkable(new XYZ(site.x + 4, site.y, site.z)).ToVector3();
		Scenario.ProjectCommands.CallCmdAddMoveToGoal(knight.gameObject, away, drop: false, queued: false);
		yield return t.UntilGame(() => RangedFlat(knight.transform.position - post) > 4f, 30f, "the knight to walk away from its post");
		yield return t.UntilGame(() => knight.CurrentTask == null, 30f, "the knight to arrive");
		t.Equal(1, SoldierPosts.SendBack(new[] { knight }), "Units sent back to their posts");
		yield return t.UntilGame(() => RangedFlat(knight.transform.position - post) < 1.5f, 30f, "the knight to be back at its post");
		SoldierPosts.Clear(new[] { knight });
		t.False(SoldierPosts.Has(knight), "The knight still has a post after Clear post");
	}

	// The buttons and sub-wheels a wheel of the game's compiled wheels names.
	private static List<string> WheelTerms(string name)
	{
		List<string> terms = new List<string>();
		if (!PieContenu.menus.TryGetValue(name, out PieContenu.Menu menu))
		{
			return null;
		}
		foreach (PieContenu.Instruction instruction in menu.instructions)
		{
			if (instruction.args != null)
			{
				terms.AddRange(instruction.args);
			}
		}
		return terms;
	}

	[GameTest("Army", "The artificer's wheel over itself has Set post, Back to post and Clear post, as the other soldiers' have (#103)", TimeoutSeconds = 30f)]
	private static IEnumerator ArtificerWheelHasPosts(TestContext t)
	{
		if (!Plugin.IsEnabled(Features.SoldierPosts))
		{
			t.Skip("SoldierPosts is switched off");
		}
		yield return null;
		List<string> artificer = WheelTerms("Artificer");
		t.NotNull(artificer, "The artificer's wheel");
		t.Record("artificer", string.Join(" ", artificer.ToArray()));
		t.True(artificer.Exists((string term) => term.Trim() == "PlusArtificerSelf"), "The artificer's wheel opens no wheel over itself");
		List<string> self = WheelTerms("PlusArtificerSelf");
		t.NotNull(self, "The artificer's wheel over itself");
		t.Record("self", string.Join(" ", self.ToArray()));
		foreach (string button in new[] { "PlusSetPost", "PlusBackToPost", "PlusClearPost" })
		{
			t.True(self.Exists((string term) => term.Trim() == button), button + " is not in the artificer's wheel over itself");
		}
		t.NotNull(WheelTerms("PlusArtificerMain"), "The artificer's wheel anywhere else");
	}

	[GameTest("Army", "A guarding archer shows the guard's icon in its name tag, and its own icon again after Stop guarding (#98)", TimeoutSeconds = 60f)]
	private static IEnumerator GuardingArcherIcon(TestContext t)
	{
		if (!Plugin.IsEnabled(Features.RangedGuard))
		{
			t.Skip("RangedGuard is switched off");
		}
		XYZ site = Scenario.Site(t, 6, 6);
		Labor archer = Scenario.Bricktron(t, Occupation.Type.Archer, site);
		yield return t.Frames(10);
		archer.Autonomy = AutonomyStatus.InstinctOnly;
		Sprite guard = Brix.UI.Icons.IconKeys._Defend_Area.Get32();
		t.NotNull(guard, "The guard's icon");
		t.True(archer.Autonomy.GetIcon(archer) != guard, "The archer shows the guard's icon before guarding");
		SoldierOrdersNetwork.SendGuard(archer.gameObject, true);
		t.True(RangedGuard.IsGuarding(archer), "The archer is not guarding");
		t.True(archer.Autonomy.GetIcon(archer) == guard, "The guarding archer does not show the guard's icon");
		SoldierOrdersNetwork.SendGuard(archer.gameObject, false);
		t.True(archer.Autonomy.GetIcon(archer) != guard, "The archer still shows the guard's icon after Stop guarding");
	}

	[GameTest("Army", "A looping kit order's stock limit counts the kits soldiers of the class wear, as the call to arms counts its sets (#99)", TimeoutSeconds = 60f)]
	private static IEnumerator WornKitsCountForLoopLimit(TestContext t)
	{
		if (!Plugin.IsEnabled(Features.KitBundles) || !Plugin.IsEnabled(Features.CraftLoopLimit))
		{
			t.Skip("KitBundles or CraftLoopLimit is switched off");
		}
		KitBundles.Bundle kit = System.Array.Find(KitBundles.All, (KitBundles.Bundle b) => b.Job == Occupation.Job.Archer);
		t.NotNull(kit, "The archer kit");
		Faction faction = Scenario.Player;
		int before = CastleStoryPlus.Economy.CraftLoopLimit.Worn(faction, kit.Resource);
		XYZ site = Scenario.Site(t, 6, 6);
		Labor archer = Scenario.Bricktron(t, Occupation.Type.Archer, site);
		yield return t.Frames(10);
		t.Equal(Occupation.Job.Archer, archer.Occupation.CurrentJob, "The archer's class");
		t.Equal(before + 1, CastleStoryPlus.Economy.CraftLoopLimit.Worn(faction, kit.Resource), "Archer kits worn");
		int coming = CastleStoryPlus.Economy.CraftLoopLimit.Coming(faction, kit.Resource, kit.Key, cached: false);
		t.Record("coming", coming);
		t.True(coming >= before + 1, "The worn kits are not counted with the kits on the way");
	}
}
