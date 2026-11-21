using System.Collections;
using Brix.Engine;
using Brix.Game;
using Brix.Game.AI;
using CastleStoryPlus.Combat;
using CastleStoryPlus.Core;
using CastleStoryPlus.DevTools.Testing;
using UnityEngine;

namespace CastleStoryPlus.DevTools.Tests;

// Archers that cannot shoot their target from where they stand (#165): a corruptron behind another in the line of fire,
// and a target left be (ArcherRetarget) while another enemy is in plain view. The corruptrons wait for orders, so they
// stay where they are put.
[ParallelTests("Combat")]
internal static class ArcherTargetTests
{
	[GameTest("Combat", "An archer with a corruptron behind another in its line of fire shoots within 20 s and keeps shooting until both are dead (#165)", TimeoutSeconds = 240f)]
	private static IEnumerator ShootsIntoACrowd(TestContext t)
	{
		XYZ site = Scenario.Site(t, 16, 6);
		Labor archer = Scenario.Bricktron(t, Occupation.Type.Archer, new XYZ(site.x - 6, site.y, site.z));
		Labor front = Still(Scenario.Corruptron(t, new XYZ(site.x + 1, site.y, site.z)));
		Labor back = Still(Scenario.Corruptron(t, new XYZ(site.x + 3, site.y, site.z)));
		yield return t.Frames(5);
		float frontMax = Scenario.Hp(front);
		float backMax = Scenario.Hp(back);
		float started = Time.time;
		yield return t.UntilGame(() => Scenario.Hp(front) < frontMax || Scenario.Hp(back) < backMax, 20f, "the archer to hit one of the corruptrons", 5f, () => Note(t, archer));
		t.Record("firstHitAfter", Mathf.Round(Time.time - started));
		yield return t.UntilGame(() => !Scenario.Alive(front) && !Scenario.Alive(back), 150f, "both corruptrons to die", 10f, () => Note(t, archer));
		t.Record("bothDeadAfter", Mathf.Round(Time.time - started));
		t.True(Scenario.Alive(archer), "The archer died");
	}

	[GameTest("Combat", "An archer ordered to attack a corruptron behind another one shoots within 20 s (#165)", TimeoutSeconds = 180f)]
	private static IEnumerator OrderedAtTheBackOfACrowd(TestContext t)
	{
		XYZ site = Scenario.Site(t, 16, 6);
		Labor archer = Scenario.Bricktron(t, Occupation.Type.Archer, new XYZ(site.x - 6, site.y, site.z));
		Labor front = Still(Scenario.Corruptron(t, new XYZ(site.x + 1, site.y, site.z)));
		Labor back = Still(Scenario.Corruptron(t, new XYZ(site.x + 3, site.y, site.z)));
		yield return t.Frames(5);
		float frontMax = Scenario.Hp(front);
		float backMax = Scenario.Hp(back);
		Scenario.ProjectCommands.CmdAttack(archer.gameObject, back.gameObject);
		float started = Time.time;
		yield return t.UntilGame(() => Scenario.Hp(front) < frontMax || Scenario.Hp(back) < backMax, 20f, "the ordered archer to hit a corruptron", 5f, () => Note(t, archer));
		t.Record("firstHitAfter", Mathf.Round(Time.time - started));
		t.Record("hit", (Scenario.Hp(back) < backMax) ? "the ordered target" : "the one in front");
	}

	[GameTest("Combat", "An archer whose nearest enemy is left be as not shootable shoots another enemy in plain view instead, and tries the first again later without moving (#165)", TimeoutSeconds = 180f)]
	private static IEnumerator ShootsAnotherWhileItsTargetIsBlocked(TestContext t)
	{
		if (!Plugin.IsEnabled(Features.ArcherRetarget))
		{
			t.Skip("ArcherRetarget is switched off");
		}
		XYZ site = Scenario.Site(t, 16, 10);
		Labor archer = Scenario.Bricktron(t, Occupation.Type.Archer, new XYZ(site.x - 5, site.y, site.z));
		Labor near = Still(Scenario.Corruptron(t, new XYZ(site.x + 2, site.y, site.z - 2)));
		Labor far = Still(Scenario.Corruptron(t, new XYZ(site.x + 5, site.y, site.z + 3)));
		// The near one stays blocked from where the archer stands for as long as the test needs, from before the archer
		// can pick it (a shot let off at it before the block would spoil the count).
		Profession profession = archer.Profession;
		ArcherRetarget.Block(profession, near.gameObject);
		t.Cleanup(() => ArcherRetarget.Blocked.Forget(profession, near.gameObject));
		t.True(ArcherRetarget.IsBlocked(profession, near.gameObject), "The near corruptron is not left be");
		yield return t.Frames(5);
		KeepBlocked(profession, near);
		t.False(profession.CanFireAt(Location.Of(near.gameObject)), "The near corruptron left be can still be shot at");
		t.True(profession.CanFireAt(Location.Of(far.gameObject)), "The far corruptron cannot be shot at");
		float nearMax = Scenario.Hp(near);
		float farMax = Scenario.Hp(far);
		Vector3 stood = archer.transform.position;
		yield return t.UntilGame(() =>
		{
			KeepBlocked(profession, near);
			return Scenario.Hp(far) < farMax;
		}, 25f, "the archer to hit the far corruptron", 5f, () => Note(t, archer));
		t.Equal(nearMax, Scenario.Hp(near), "The near corruptron's hit points while it was left be");
		// Left alone, the block runs out and the near one is tried again from the same place.
		if (Scenario.Alive(near) && (archer.transform.position - stood).sqrMagnitude < 0.25f)
		{
			yield return t.UntilGame(() => !ArcherRetarget.IsBlocked(profession, near.gameObject), BlockedShots<Profession, GameObject>.MaxHold + 1f, "the near corruptron to be tried again");
			t.True(profession.CanFireAt(Location.Of(near.gameObject)) || !Scenario.Alive(near), "The near corruptron cannot be shot at again after the block ran out");
		}
	}

	// Blocks the target again once its block ran out or the archer stepped onto another block.
	private static void KeepBlocked(Profession profession, Labor target)
	{
		if (!ArcherRetarget.IsBlocked(profession, target.gameObject))
		{
			ArcherRetarget.Block(profession, target.gameObject);
		}
	}

	// Stays where it is put (the archer comes to no harm and the line of fire stays as set).
	private static Labor Still(Labor corruptron)
	{
		corruptron.Autonomy = AutonomyStatus.WaitForOrders;
		return corruptron;
	}

	private static void Note(TestContext t, Labor archer)
	{
		t.Record("blocked", ArcherRetarget.Blocked.Count);
		t.Record("archerAt", XYZ.FromVector3(archer.transform.position).ToString());
		t.Record("archerTask", (archer.CurrentTask != null) ? archer.CurrentTask.ToString() : "none");
	}
}
