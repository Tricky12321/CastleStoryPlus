using System.Collections;
using System.Collections.Generic;
using Brix.Engine;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.AI.Damage;
using CastleStoryPlus.DevTools.Testing;

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
