using Brix.Game.AI;
using Brix.Game.AI.Damage;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.Experience;

// 1 work XP per successfully finished task.
[Feature(Features.Experience, Features.ExperienceInfo)]
[HarmonyPatch(typeof(Goal), nameof(Goal.EndWork))]
internal static class TaskXpPatch
{
	private static void Prefix(Task work)
	{
		WorkerExperience.AwardTaskCompleted(work);
	}
}

// 1 combat XP per hit on an enemy.
[Feature(Features.Experience, Features.ExperienceInfo)]
[HarmonyPatch(typeof(BricktronDamageReceiver), nameof(BricktronDamageReceiver.ReceiveAttack))]
internal static class HitXpPatch
{
	private static void Postfix(BricktronDamageReceiver __instance, float value, GameObject attacker)
	{
		if (value > 0f)
		{
			WorkerExperience.AwardCombat(attacker, __instance.gameObject, WorkerExperience.CombatXpPerHit);
		}
	}
}

// 5 combat XP per kill.
[Feature(Features.Experience, Features.ExperienceInfo)]
[HarmonyPatch(typeof(BricktronDamageReceiver), nameof(BricktronDamageReceiver._Kill))]
internal static class KillXpPatch
{
	private static void Postfix(BricktronDamageReceiver __instance, GameObject attacker)
	{
		WorkerExperience.AwardCombat(attacker, __instance.gameObject, WorkerExperience.CombatXpPerKill);
	}
}

// Combat level: +5% melee and ranged damage per level.
[Feature(Features.Experience, Features.ExperienceInfo)]
[HarmonyPatch(typeof(Profession), nameof(Profession.MeleeDamage), MethodType.Getter)]
internal static class MeleeDamagePatch
{
	private static void Postfix(Profession __instance, ref int __result)
	{
		__result = Mathf.RoundToInt(__result * WorkerExperience.Multiplier(WorkerExperience.CombatLevel(__instance.GetComponent<Labor>())));
	}
}

[Feature(Features.Experience, Features.ExperienceInfo)]
[HarmonyPatch(typeof(Profession), nameof(Profession.RangeDamage), MethodType.Getter)]
internal static class RangeDamagePatch
{
	private static void Postfix(Profession __instance, ref int __result)
	{
		// Shots take it through ShotDamage, which reads RangeDamage once without it.
		if (CastleStoryPlus.Combat.ShotDamage.Raw)
		{
			return;
		}
		__result = Mathf.RoundToInt(__result * WorkerExperience.Multiplier(WorkerExperience.CombatLevel(__instance.GetComponent<Labor>())));
	}
}

// Work level: +5% work animation speed per level.
[Feature(Features.Experience, Features.ExperienceInfo)]
[HarmonyPatch(typeof(Locomotion4), nameof(Locomotion4.DoWork))]
internal static class WorkSpeedStartPatch
{
	private static void Postfix(Locomotion4 __instance)
	{
		WorkerExperience.SetWorkAnimationSpeed(__instance.GetComponent<Labor>(), working: true);
	}
}

[Feature(Features.Experience, Features.ExperienceInfo)]
[HarmonyPatch(typeof(Locomotion4), nameof(Locomotion4.StopWork))]
internal static class WorkSpeedStopPatch
{
	private static void Prefix(Locomotion4 __instance)
	{
		WorkerExperience.SetWorkAnimationSpeed(__instance.GetComponent<Labor>(), working: false);
	}
}
