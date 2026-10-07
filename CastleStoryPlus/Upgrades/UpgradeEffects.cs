using Brix.Game.AI;
using Brix.Game.AI.Damage;
using Brix.Game.Locomotion;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.Upgrades;

// What the researched tiers do. Every effect reads the tier of the unit's own faction, so it applies at once to
// every bricktron of the team, including new ones. Soldiers are bricktrons with any job but builder.
internal static class UpgradeEffects
{
	public static bool IsSoldier(Labor labor)
	{
		return labor != null && labor.Occupation != null && labor.Occupation.CurrentJob != Occupation.Job.Builder;
	}

	public static bool HasJob(Labor labor, Occupation.Job job)
	{
		return labor != null && labor.Occupation != null && labor.Occupation.CurrentJob == job;
	}
}

// Sharpened Blades: more melee damage.
[Feature(Features.Upgrades, Features.UpgradesInfo)]
[HarmonyPatch(typeof(Profession), nameof(Profession.MeleeDamage), MethodType.Getter)]
internal static class SharpenedBladesPatch
{
	private static void Postfix(Profession __instance, ref int __result)
	{
		float bonus = TeamUpgrades.Value(__instance.gameObject, UpgradeLines.SharpenedBlades);
		if (bonus > 0f)
		{
			__result = Mathf.RoundToInt(__result * (1f + bonus));
		}
	}
}

// Heavy Blades: more damage to blocks and buildings.
[Feature(Features.Upgrades, Features.UpgradesInfo)]
[HarmonyPatch(typeof(Profession), nameof(Profession.SiegeDamage), MethodType.Getter)]
internal static class HeavyBladesPatch
{
	private static void Postfix(Profession __instance, ref float __result)
	{
		__result *= 1f + TeamUpgrades.Value(__instance.gameObject, UpgradeLines.HeavyBlades);
	}
}

// Fletching: more arrow damage for archers.
[Feature(Features.Upgrades, Features.UpgradesInfo)]
[HarmonyPatch(typeof(Profession), nameof(Profession.RangeDamage), MethodType.Getter)]
internal static class FletchingPatch
{
	private static void Postfix(Profession __instance, ref int __result)
	{
		// Shots take it through ShotDamage, which reads RangeDamage once without it.
		if (CastleStoryPlus.Combat.ShotDamage.Raw || !UpgradeEffects.HasJob(__instance.labor, Occupation.Job.Archer))
		{
			return;
		}
		float bonus = TeamUpgrades.Value(__instance.gameObject, UpgradeLines.Fletching);
		if (bonus > 0f)
		{
			__result = Mathf.RoundToInt(__result * (1f + bonus));
		}
	}
}

// Steady Aim: less spread on an archer's shots. Shots with a clear line of sight to the target already have none
// with ArcherAccuracy; this narrows the others (lobbed over walls, moving targets).
[Feature(Features.Upgrades, Features.UpgradesInfo)]
[HarmonyPatch(typeof(Archery), nameof(Archery.ShootAt))]
internal static class SteadyAimPatch
{
	private static void Prefix(Archery __instance, ref float inaccuracy)
	{
		Locomotion4 locomotion = __instance.Locomotion;
		if (locomotion == null)
		{
			return;
		}
		Labor labor = locomotion.GetComponent<Labor>();
		if (UpgradeEffects.HasJob(labor, Occupation.Job.Archer))
		{
			inaccuracy *= 1f - TeamUpgrades.Value(locomotion.gameObject, UpgradeLines.SteadyAim);
		}
	}
}

// Winch: shorter reload between crossbow bolts.
[Feature(Features.Upgrades, Features.UpgradesInfo)]
[HarmonyPatch(typeof(Profession), nameof(Profession.JobRangedCooldown), MethodType.Getter)]
internal static class WinchPatch
{
	private static void Postfix(Profession __instance, ref float __result)
	{
		if (UpgradeEffects.HasJob(__instance.labor, Occupation.Job.Arbalist))
		{
			__result *= 1f - TeamUpgrades.Value(__instance.gameObject, UpgradeLines.Winch);
		}
	}
}

// Chainmail, Padded Gambeson, Warded Plate and Shield Rims: a soldier takes less damage of each type, and a
// blocked hit costs less stamina. Applied to the attack's value before the game decides the reaction, so a hit
// the armour survives does not play the death reaction. Server side, where attacks are resolved.
[Feature(Features.Upgrades, Features.UpgradesInfo)]
[HarmonyPatch(typeof(BricktronDamageReceiver), nameof(BricktronDamageReceiver.ReceiveAttack))]
internal static class ArmourPatch
{
	private static void Prefix(BricktronDamageReceiver __instance, ref float value, DamageType type, Vector3 force)
	{
		if (value <= 0f || !UpgradeEffects.IsSoldier(__instance.Labor))
		{
			return;
		}
		GameObject unit = __instance.gameObject;
		switch (type)
		{
		case DamageType.Melee:
			value *= 1f - TeamUpgrades.Value(unit, UpgradeLines.Chainmail);
			break;
		case DamageType.Piercing:
			value *= 1f - TeamUpgrades.Value(unit, UpgradeLines.Gambeson);
			break;
		case DamageType.Magical:
		case DamageType.Explosive:
			value *= 1f - TeamUpgrades.Value(unit, UpgradeLines.WardedPlate);
			break;
		}
		float rims = TeamUpgrades.Value(unit, UpgradeLines.ShieldRims);
		if (rims > 0f && __instance.CanBlockAttack(type, force))
		{
			value *= 1f - rims;
		}
	}
}

// Shield Rims: a wider blocking arc.
[Feature(Features.Upgrades, Features.UpgradesInfo)]
[HarmonyPatch(typeof(Profession), nameof(Profession.BlockingAngle), MethodType.Getter)]
internal static class ShieldRimsArcPatch
{
	private static void Postfix(Profession __instance, ref float __result)
	{
		if (__result > 0f)
		{
			__result += TeamUpgrades.Tier(__instance.gameObject, UpgradeLines.ShieldRims) * UpgradeLines.ShieldRimsDegreesPerTier;
		}
	}
}

// Reinforced Helmet: more max health for soldiers. The game sets max health from the prefab and the tools'
// protection every time the tools change; the helmet scales the result. The job is updated only after this
// (from the same tool change), so it is read from the new tools here.
[Feature(Features.Upgrades, Features.UpgradesInfo)]
[HarmonyPatch(typeof(Profession), nameof(Profession.SetHpBonus))]
internal static class HelmetPatch
{
	private static void Postfix(Profession __instance)
	{
		Labor labor = __instance.labor;
		if (labor == null || labor.Occupation == null || labor.Occupation.GetJobFromToolbag() == Occupation.Job.Builder)
		{
			return;
		}
		float bonus = TeamUpgrades.Value(__instance.gameObject, UpgradeLines.Helmet);
		BricktronDamageReceiver receiver = __instance.GetComponent<BricktronDamageReceiver>();
		if (bonus > 0f && receiver != null)
		{
			receiver.MaxHP = Mathf.Round(receiver.MaxHP * (1f + bonus));
		}
	}
}
