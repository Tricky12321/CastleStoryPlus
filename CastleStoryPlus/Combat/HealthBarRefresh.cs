using Brix.Game.AI.Damage;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine.Networking;

namespace CastleStoryPlus.Combat;

// The health bars over units (SelectionCircle, driven by CharacterHighlightDriver and ObjectsHighlightDriver) are only
// redrawn when a unit's HPChangedSignal fires, and the game fires it only when the health itself changes. A change of
// the maximum alone (a researched helmet bonus, a first state a client gets of a unit that is already hurt, a maximum
// that arrives on a client after the health) left the bar showing the old fill while the unit had the right health.
// Here such a change fires the signal too, with the same value twice: the bars redraw, nothing flashes.
[Feature]
internal static class HealthBarRefresh
{
	internal static void Redraw(DamageReceiver receiver)
	{
		if (receiver != null && receiver.MaxHP > 0f)
		{
			receiver.HPChangedSignal.Invoke(receiver.CurrentHP, receiver.CurrentHP);
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

// Client: what the host sent redraws the bar when it changed the maximum or is the unit's first state while hurt.
[Feature]
[HarmonyPatch(typeof(DamageReceiver), nameof(DamageReceiver.OnDeserialize))]
internal static class HealthBarClientPatch
{
	private static void Prefix(DamageReceiver __instance, out float[] __state)
	{
		__state = new[] { __instance.MaxHP, __instance.CurrentHP };
	}

	private static void Postfix(DamageReceiver __instance, bool initialState, float[] __state)
	{
		if (NetworkServer.active)
		{
			return;
		}
		bool maxChanged = __instance.MaxHP != __state[0];
		bool firstWhileHurt = initialState && __instance.IsDamaged;
		if (maxChanged || firstWhileHurt)
		{
			HealthBarRefresh.Redraw(__instance);
		}
	}
}
