using System;
using System.Reflection;
using Brix.Game.AI;
using Brix.Game.AI.Damage;
using Brix.Utils.Signals;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.Combat;

// The health bars over units (SelectionCircle, driven by CharacterHighlightDriver and ObjectsHighlightDriver) are only
// redrawn when a unit's HPChangedSignal fires, and the game fires it only when the health itself changes. Gaps that
// left a bar showing an old fill or none at all (#125):
// - A change of the maximum without a change of the health. On a client every change of tools or armour
//   (Toolbag.CompileToolStatistics: Profession.SetHpBonus and the mod's helmet bonus) sets the maximum, and with it
//   the health, locally with no hook; the host's later value is the same, so no signal ever came. (HealthBarMaxPatch)
// - A unit that is hurt already when its bar's handlers are connected (a loaded save, a joining client, a new unit)
//   got no bar until its next change: the driver connects its handlers but never draws once. (HealthBarConnectPatch)
// - A bar is hidden while the unit is in the fog and nothing drew it again when the unit came out. (HealthBarConnectPatch)
// - The bar is taken off a unit that is released to the pool on every life, not only the first.
// A redraw fires the signal with the same value twice: the bars redraw, nothing flashes.
[Feature]
internal static class HealthBarRefresh
{
	private static bool _logged;

	private static readonly MethodInfo RefreshCircle = AccessTools.Method(typeof(CharacterHighlightDriver), "RefreshCircle", new[] { typeof(Labor) });

	private static readonly PropertyInfo Observer = AccessTools.Property(typeof(CharacterHighlightDriver), "observer");

	internal static void Redraw(DamageReceiver receiver)
	{
		try
		{
			if (receiver != null && receiver.MaxHP > 0f)
			{
				receiver.HPChangedSignal.Invoke(receiver.CurrentHP, receiver.CurrentHP);
			}
		}
		catch (Exception ex)
		{
			Failed(ex);
		}
	}

	internal static void RedrawBar(CharacterHighlightDriver driver, Labor labor)
	{
		try
		{
			if (driver != null && labor != null && labor.gameObject.activeInHierarchy)
			{
				RefreshCircle.Invoke(driver, new object[] { labor });
			}
		}
		catch (Exception ex)
		{
			Failed(ex);
		}
	}

	internal static UIGameObserver.Bricktron ObserverOf(CharacterHighlightDriver driver)
	{
		return Observer.GetValue(driver, null) as UIGameObserver.Bricktron;
	}

	internal static void Failed(Exception ex)
	{
		if (!_logged)
		{
			_logged = true;
			Plugin.Log.LogWarning("HealthBarRefresh: a health bar could not be redrawn: " + ex.Message);
		}
	}
}

// A new maximum: the fill is health over maximum, so it moves even when the health stays.
[Feature]
[HarmonyPatch(typeof(DamageReceiver), nameof(DamageReceiver.MaxHP), MethodType.Setter)]
internal static class HealthBarMaxPatch
{
	private static void Prefix(DamageReceiver __instance, out float __state)
	{
		__state = __instance.MaxHP;
	}

	private static void Postfix(DamageReceiver __instance, float __state)
	{
		if (__instance.MaxHP != __state)
		{
			HealthBarRefresh.Redraw(__instance);
		}
	}
}

// A change of tools or armour sets the maximum twice (the game's SetHpBonus, then the mod's helmet bonus), and each
// set moves the health by the difference but never below 1: a fighter at 1 or 2 hit points with a helmet gained health
// on every swap. The damage taken stays what it was: health is the new maximum less it.
[Feature]
[HarmonyPatch(typeof(Toolbag), nameof(Toolbag.CompileToolStatistics))]
internal static class ToolSwapKeepsDamagePatch
{
	private static void Prefix(Toolbag __instance, out float[] __state)
	{
		BricktronDamageReceiver receiver = __instance.GetComponent<BricktronDamageReceiver>();
		__state = (receiver != null) ? new[] { receiver.MaxHP, receiver.CurrentHP } : null;
	}

	private static void Postfix(Toolbag __instance, float[] __state)
	{
		if (__state == null || __state[1] <= 0f)
		{
			return;
		}
		BricktronDamageReceiver receiver = __instance.GetComponent<BricktronDamageReceiver>();
		if (receiver == null)
		{
			return;
		}
		float damage = Mathf.Max(0f, __state[0] - __state[1]);
		float health = Mathf.Clamp(receiver.MaxHP - damage, 1f, receiver.MaxHP);
		if (!Mathf.Approximately(receiver.CurrentHP, health))
		{
			receiver.CurrentHP = health;
		}
	}
}

// The driver of the bars over bricktrons (allies, enemies): once for every unit it connects to, and when one leaves
// the fog; and the bar is taken off again when the unit is released.
[Feature]
[HarmonyPatch(typeof(CharacterHighlightDriver), "ConnectBricktronSignals")]
internal static class HealthBarConnectPatch
{
	private static void Postfix(CharacterHighlightDriver __instance, Labor l)
	{
		HealthBarRefresh.RedrawBar(__instance, l);
		if (l != null)
		{
			// Every life: a pooled unit keeps its signal, and the game's own once-handler is spent after the first.
			l.OnReleaseOrDestroySignal.ConnectOnce((GameObject go) =>
			{
				if (SelectionCircleDispenser.Instance != null && go != null)
				{
					SelectionCircleDispenser.Instance.Remove(go);
				}
			});
		}
	}
}

[Feature]
[HarmonyPatch(typeof(CharacterHighlightDriver), "ConnectToSignals")]
internal static class HealthBarFogPatch
{
	private static void Postfix(CharacterHighlightDriver __instance)
	{
		try
		{
			UIGameObserver.Bricktron observer = HealthBarRefresh.ObserverOf(__instance);
			if (observer != null)
			{
				observer.ExitFog.Connect((Labor labor) => HealthBarRefresh.RedrawBar(__instance, labor)).DisconnectWhen(__instance.OnReleaseOrDestroySignal);
			}
		}
		catch (Exception ex)
		{
			HealthBarRefresh.Failed(ex);
		}
	}
}
