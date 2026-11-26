using System.Collections;
using System.Collections.Generic;
using Brix.Engine;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.AI.Damage;
using CastleStoryPlus.Core;
using CastleStoryPlus.DevTools.Testing;
using CastleStoryPlus.Performance;
using UnityEngine;

namespace CastleStoryPlus.DevTools.Tests;

// The health bars over units are redrawn from the receiver's HPChangedSignal (#125, HealthBarRefresh).
[ParallelTests("Combat")]
internal static class HealthBarTests
{
	[GameTest("Combat", "A unit's health bar is told of every change: a hit, a heal and a new maximum that leaves the health as it is (#125)", TimeoutSeconds = 40f)]
	private static IEnumerator BarFollowsHealth(TestContext t)
	{
		XYZ site = Scenario.Site(t, 6, 6);
		Labor corruptron = Scenario.Corruptron(t, new XYZ(site.x, site.y, site.z));
		yield return t.Frames(5);
		DamageReceiver receiver = corruptron.GetComponent<DamageReceiver>();
		t.NotNull(receiver, "The corruptron's damage receiver");
		List<float[]> changes = new List<float[]>();
		var connection = receiver.HPChangedSignal.Connect((float previous, float next) => changes.Add(new[] { previous, next }));
		t.Cleanup(() => connection.Disconnect());

		float max = receiver.MaxHP;
		receiver.ApplyDamage(1f);
		yield return t.Frames(2);
		t.Equal(1, changes.Count, "Signals after a hit");
		t.True(changes[0][0] > changes[0][1], "The hit's signal did not go from more to less health");
		t.True(System.Math.Abs(changes[0][1] - receiver.CurrentHP) < 0.001f, "The hit's signal did not carry the new health");

		changes.Clear();
		receiver.Heal(0.5f);
		yield return t.Frames(2);
		t.Equal(1, changes.Count, "Signals after a heal");
		t.True(changes[0][0] < changes[0][1], "The heal's signal did not go from less to more health");

		// One hit point left: a smaller maximum cannot lower it, only the fill (health over maximum) changes.
		receiver.CurrentHP = 1f;
		yield return t.Frames(2);
		changes.Clear();
		receiver.MaxHP = max - 2f;
		yield return t.Frames(2);
		t.Equal(1f, receiver.CurrentHP, "Health after the smaller maximum");
		t.True(changes.Count >= 1, "A new maximum with the health unchanged sent no signal, so the bar kept its old fill");
	}

	// The ring is drawn by its animator, which writes the fill (_Ring1Progress), whether it shows (_Ring1Thickness) and
	// the side's colour (_Ring1Color) into the renderer's property block; without it the ring shows the material's own
	// values (a yellow ring at about 80 %) on every unit (#196).
	[GameTest("Combat", "A health ring draws the health, visibility and side colour it is given, also after the decoration animator pass (#196)", TimeoutSeconds = 40f)]
	private static IEnumerator RingDrawsWhatItIsGiven(TestContext t)
	{
		t.NotNull(SelectionCircleDispenser.Instance, "The ring dispenser");
		XYZ site = Scenario.Site(t, 6, 6);
		Labor corruptron = Scenario.Corruptron(t, new XYZ(site.x, site.y, site.z));
		yield return t.Frames(5);
		// A ring of its own, kept by position, so the unit's own bar driver does not take it away.
		Vector3 at = corruptron.transform.position + new Vector3(0.37f, 0f, 0.37f);
		SelectionCircle circle = SelectionCircleDispenser.Instance.AddOrGet(at);
		t.Cleanup(() =>
		{
			if (SelectionCircleDispenser.Instance != null)
			{
				SelectionCircleDispenser.Instance.Remove(at);
			}
		});
		Animator animator = circle.GetComponent<Animator>();
		Renderer renderer = circle.GetComponent<Renderer>();
		t.NotNull(animator, "The ring's animator");
		t.NotNull(renderer, "The ring's renderer");

		circle.MainColor(SelectionCircle.MainColors.Enemies);
		circle.ShowHp(true);
		circle.Hp(0.3f);
		if (Plugin.IsEnabled(Features.FrameSavers))
		{
			FrameSavers.CullDecorAnimators();
		}
		t.Equal(AnimatorCullingMode.AlwaysAnimate, animator.cullingMode, "The ring's animator culling after the decoration pass");
		yield return t.Seconds(1.5f);
		t.Record("fill at 0.3", Ring(renderer, "_Ring1Progress"));
		t.True(Mathf.Abs(Ring(renderer, "_Ring1Progress") - 0.3f) < 0.02f, "The ring's fill is not the 30 % it was given: " + Ring(renderer, "_Ring1Progress"));
		t.True(Ring(renderer, "_Ring1Thickness") > 0.5f, "The ring does not show after ShowHp(true): " + Ring(renderer, "_Ring1Thickness"));
		t.True(RingColor(renderer).g < 0.2f, "The enemy ring is not red: " + RingColor(renderer));

		circle.Hp(0.7f);
		yield return t.Seconds(1.5f);
		t.True(Mathf.Abs(Ring(renderer, "_Ring1Progress") - 0.7f) < 0.02f, "The ring's fill did not follow to 70 %: " + Ring(renderer, "_Ring1Progress"));

		circle.ShowHp(false);
		yield return t.Seconds(2f);
		t.True(Ring(renderer, "_Ring1Thickness") < 0.2f, "The ring still shows after ShowHp(false): " + Ring(renderer, "_Ring1Thickness"));
	}

	[GameTest("Combat", "The ring under a hurt unit shows that unit's health, not the same fill for every unit (#196)", TimeoutSeconds = 40f)]
	private static IEnumerator HurtUnitRingShowsItsHealth(TestContext t)
	{
		XYZ site = Scenario.Site(t, 6, 6);
		Labor knight = Scenario.Bricktron(t, Occupation.Type.Knight, new XYZ(site.x, site.y, site.z));
		yield return t.Frames(5);
		DamageReceiver receiver = knight.GetComponent<DamageReceiver>();
		t.NotNull(receiver, "The knight's damage receiver");
		receiver.ApplyDamage(receiver.MaxHP * 0.6f);
		yield return t.Seconds(1.5f);
		SelectionCircle circle = knight.GetComponentInChildren<SelectionCircle>();
		if (circle == null)
		{
			t.Skip("The hurt knight got no ring (not seen by the local side here)");
			yield break;
		}
		Renderer renderer = circle.GetComponent<Renderer>();
		float expected = receiver.CurrentHP / receiver.MaxHP;
		t.Record("expected fill", expected);
		t.Record("fill", Ring(renderer, "_Ring1Progress"));
		t.True(Mathf.Abs(Ring(renderer, "_Ring1Progress") - expected) < 0.03f, "The ring's fill " + Ring(renderer, "_Ring1Progress") + " is not the knight's health " + expected);
		t.True(Ring(renderer, "_Ring1Thickness") > 0.5f, "The hurt knight's ring shows no health");
	}

	private static float Ring(Renderer renderer, string property)
	{
		MaterialPropertyBlock block = new MaterialPropertyBlock();
		renderer.GetPropertyBlock(block);
		return block.GetFloat(property);
	}

	private static Color RingColor(Renderer renderer)
	{
		MaterialPropertyBlock block = new MaterialPropertyBlock();
		renderer.GetPropertyBlock(block);
		return block.GetColor("_Ring1Color");
	}

	[GameTest("Combat", "A change of tools or armour never heals a bricktron that has one hit point left", TimeoutSeconds = 40f)]
	private static IEnumerator ToolSwapDoesNotHeal(TestContext t)
	{
		XYZ site = Scenario.Site(t, 6, 6);
		Labor knight = Scenario.Bricktron(t, Occupation.Type.Knight, new XYZ(site.x, site.y, site.z));
		yield return t.Frames(5);
		DamageReceiver receiver = knight.GetComponent<DamageReceiver>();
		Toolbag toolbag = knight.GetComponent<Toolbag>();
		t.NotNull(receiver, "The knight's damage receiver");
		t.NotNull(toolbag, "The knight's toolbag");
		receiver.CurrentHP = 1f;
		for (int i = 0; i < 3; i++)
		{
			toolbag.CompileToolStatistics();
			yield return t.Frames(1);
			t.Equal(1f, receiver.CurrentHP, "Health after tool change " + (i + 1));
		}
	}
}
