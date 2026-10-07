using System.Collections.Generic;
using Brix.Game.AI;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.Combat;

// A shot's own damage. The game hands the shooter's ranged damage (Profession.RangeDamage) to ProjectileWeapon.Fire,
// but an arrow, bolt, magic shot or thrown rock never uses it: its hit does the fixed damage of its kind's attack
// (AttackTemplate.Arrow...), shared by every shot of that kind. So what the mod adds to RangeDamage (Experience's
// combat level, Fletching) did nothing for shots. Here each shot keeps a damage factor from when it leaves (host): the
// shooter's RangeDamage with the mod's bonuses over it without them, times a critical hit's 2; its hit then uses a
// copy of the shared attack with that much damage, and the shared one is put back.
[Feature]
internal static class ShotDamage
{
	// Host: the factor of each shot in flight with one other than 1.
	private static readonly Dictionary<KinematicProjectile, float> Factors = new Dictionary<KinematicProjectile, float>();

	// Set while RangeDamage is read without the mod's bonuses; their patches leave it as it is.
	internal static bool Raw;

	private static void Enable()
	{
		GameSession.OnLeave(Factors.Clear);
	}

	internal static void Scale(KinematicProjectile projectile, float factor)
	{
		if (projectile == null)
		{
			return;
		}
		Factors[projectile] = Factor(projectile) * factor;
	}

	internal static float Factor(KinematicProjectile projectile)
	{
		return (projectile != null && Factors.TryGetValue(projectile, out float factor)) ? factor : 1f;
	}

	internal static void Forget(KinematicProjectile projectile)
	{
		Factors.Remove(projectile);
	}

	// The shooter's ranged damage with the mod's bonuses over it without them; 1 for a shooter without a profession.
	internal static float Bonus(GameObject source)
	{
		Profession profession = (source != null) ? source.GetComponent<Profession>() : null;
		if (profession == null)
		{
			return 1f;
		}
		int boosted = profession.RangeDamage;
		int raw;
		Raw = true;
		try
		{
			raw = profession.RangeDamage;
		}
		finally
		{
			Raw = false;
		}
		return (raw > 0 && boosted > 0) ? (float)boosted / raw : 1f;
	}
}

// Host: a shot takes its shooter's ranged damage bonus when it leaves; healing bolts do no damage and are left out.
[Feature]
[HarmonyPatch(typeof(ProjectileWeapon), nameof(ProjectileWeapon.Fire))]
internal static class ShotDamageFirePatch
{
	private static void Postfix(ProjectileAttack __result, GameObject source)
	{
		if (!NetworkServer.active || __result == null)
		{
			return;
		}
		KinematicProjectile projectile = __result.projectile as KinematicProjectile;
		if (projectile == null || ArtificerHealBoltFirePatch.Bolts.ContainsKey(projectile))
		{
			return;
		}
		float bonus = ShotDamage.Bonus(source);
		if (!Mathf.Approximately(bonus, 1f))
		{
			ShotDamage.Scale(projectile, bonus);
		}
	}
}

// The hit uses a copy of the shared attack with the shot's damage, for this hit only.
[Feature]
[HarmonyPatch(typeof(ProjectileAttack), nameof(ProjectileAttack.Apply))]
internal static class ShotDamageHitPatch
{
	private static void Prefix(ProjectileAttack __instance, out AttackTemplate __state)
	{
		__state = null;
		KinematicProjectile projectile = __instance.projectile as KinematicProjectile;
		AttackTemplate attack = __instance.attack;
		float factor = ShotDamage.Factor(projectile);
		if (attack == null || Mathf.Approximately(factor, 1f))
		{
			return;
		}
		__state = attack;
		__instance.attack = new AttackTemplate
		{
			damage = attack.damage * factor,
			type = attack.type,
			ForceFactor = attack.ForceFactor,
			Reaction = attack.Reaction,
			Lift = attack.Lift
		};
	}

	private static void Postfix(ProjectileAttack __instance, AttackTemplate __state)
	{
		if (__state != null)
		{
			__instance.attack = __state;
		}
	}
}

// Projectiles are pooled: a reused one starts without a factor.
[Feature]
[HarmonyPatch(typeof(KinematicProjectile), nameof(KinematicProjectile.ResetComponent))]
internal static class ShotDamageResetPatch
{
	private static void Postfix(KinematicProjectile __instance)
	{
		ShotDamage.Forget(__instance);
	}
}
