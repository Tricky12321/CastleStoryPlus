using System.Collections.Generic;
using System.Reflection;
using Brix.Game.AI;
using Brix.Game.AI.Damage;
using Brix.Game.Locomotion;
using Brix.Lifecycle.Pooling;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.Giant;

// Walking and running speed of a 3x bricktron (the animations already run faster through the animator speed).
[Feature(Features.GiantBricktron, Features.GiantBricktronInfo)]
[HarmonyPatch]
internal static class GiantMoveSpeedPatch
{
	private static IEnumerable<MethodBase> TargetMethods()
	{
		yield return AccessTools.PropertyGetter(typeof(Walking), "SpeedMperS");
		yield return AccessTools.PropertyGetter(typeof(Running), "SpeedMperS");
	}

	private static void Postfix(LocomotionWalkableCycleState __instance, ref float __result)
	{
		Locomotion4 locomotion = __instance.Locomotion;
		if (locomotion != null)
		{
			__result *= GiantBricktron.SpeedFactor(locomotion.Labor);
		}
	}
}

// 3x health: a 3x bricktron takes a third of the damage (healing and regeneration are negative and unchanged).
[Feature(Features.GiantBricktron, Features.GiantBricktronInfo)]
[HarmonyPatch(typeof(BricktronDamageReceiver), nameof(BricktronDamageReceiver.ApplyDamage), new[] { typeof(float), typeof(DamageType), typeof(DamageReaction), typeof(Vector3), typeof(Vector3), typeof(float), typeof(GameObject) })]
internal static class GiantHealthPatch
{
	private static void Prefix(BricktronDamageReceiver __instance, ref float value)
	{
		if (value > 0f && GiantBricktron.IsGiant(__instance.Labor))
		{
			value /= Mathf.Max(0.01f, GiantBricktron.Health.Value);
		}
	}
}

// The worker sacrificed for an upgrade dies for good: its firefly is removed instead of flying home, so it
// neither respawns nor refunds energy.
[Feature(Features.GiantBricktron, Features.GiantBricktronInfo)]
[HarmonyPatch(typeof(Corpse), nameof(Corpse.Spawn))]
internal static class GiantSacrifice
{
	private static readonly HashSet<GameObject> Marked = new HashSet<GameObject>();

	private static readonly HashSet<Corpse> Corpses = new HashSet<Corpse>();

	private static void Enable()
	{
		GameSession.OnLeave(() =>
		{
			Marked.Clear();
			Corpses.Clear();
		});
	}

	public static void Mark(GameObject labor)
	{
		Marked.Add(labor);
	}

	private static void Postfix(GameObject original, Corpse __result)
	{
		if (original != null && __result != null && Marked.Remove(original))
		{
			Corpses.Add(__result);
		}
	}

	internal static bool TakeCorpse(Corpse corpse)
	{
		return Corpses.Remove(corpse);
	}

	internal static void Forget(GameObject labor)
	{
		Marked.Remove(labor);
	}
}

// The corpse sends its firefly off after a few seconds; remove it right after that.
[Feature(Features.GiantBricktron, Features.GiantBricktronInfo)]
[HarmonyPatch(typeof(Corpse), nameof(Corpse.ForceSpawns))]
internal static class GiantSacrificeFireflyPatch
{
	private static void Postfix(Corpse __instance)
	{
		if (!GiantSacrifice.TakeCorpse(__instance) || !NetworkServer.active || __instance.firefly == null)
		{
			return;
		}
		GameObject firefly = __instance.firefly.gameObject;
		__instance.firefly = null;
		ObjectPoolSingleton.Release(firefly);
	}
}

// A pooled bricktron that is reused is never a pending sacrifice.
[Feature(Features.GiantBricktron, Features.GiantBricktronInfo)]
[HarmonyPatch(typeof(Labor), nameof(Labor.OnRequested))]
internal static class GiantSacrificeResetPatch
{
	private static void Prefix(Labor __instance)
	{
		GiantSacrifice.Forget(__instance.gameObject);
	}
}
