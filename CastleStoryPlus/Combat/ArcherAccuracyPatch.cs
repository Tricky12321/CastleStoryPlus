using System.Collections.Generic;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Lifecycle.Pooling;
using Brix.Utils;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.Combat;

// A bricktron's arrow fired with line of sight to its target has no inaccuracy, steers onto the target
// and ignores everything else, so it always hits. No bricktron arrow damages allies.
internal static class Homing
{
	// Per shot; projectiles are pooled, so the entry is replaced on every FireAt.
	public static readonly Dictionary<KinematicProjectile, Location> Targets = new Dictionary<KinematicProjectile, Location>();

	public static bool IsBricktronArrow(KinematicProjectile projectile, GameObject attacker)
	{
		return projectile.Archetype == KinematicProjectile.AttackArchetype.Arrow && attacker != null && attacker.GetComponent<Labor>() != null;
	}

	public static bool HasLineOfSight(KinematicProjectile projectile, GameObject attacker, Vector3 eye, Location target)
	{
		if (target == null || target.Object.IsNullOrReleased())
		{
			return false;
		}
		Vector3 delta = target.RangeHitPosition - eye;
		RaycastHit[] hits = Physics.RaycastAll(eye, delta.normalized, delta.magnitude + 0.5f, projectile.LayerCollisionMask, QueryTriggerInteraction.Ignore);
		System.Array.Sort(hits, (RaycastHit a, RaycastHit b) => a.distance.CompareTo(b.distance));
		foreach (RaycastHit hit in hits)
		{
			if (hit.transform.IsChildOf(attacker.transform))
			{
				continue;
			}
			GameObject hitObject = ProjectileAiming.GetSignificantParent(hit.collider).gameObject;
			if (hitObject == target.Object)
			{
				return true;
			}
			if (Affiliation.IsAllied(attacker, hitObject))
			{
				continue;
			}
			return false;
		}
		// Nothing in between: the target's own collider may not be on the collision layers.
		return true;
	}

	public static bool IsHoming(KinematicProjectile projectile, out Location target)
	{
		return Targets.TryGetValue(projectile, out target) && target != null && !projectile.Target.IsNullOrReleased();
	}
}

[Feature(Features.ArcherAccuracy, Features.ArcherAccuracyInfo)]
[HarmonyPatch(typeof(KinematicProjectile), nameof(KinematicProjectile.FireAt))]
internal static class FireAtPatch
{
	private static bool Prefix(Factory.AssetKey ammoAssetKey, GameObject attacker, Vector3 origin, Vector3 aimOrigin, Location target, Vector3 anticipatedTargetPosition, float power, float inaccuracy, int hitPointDamage, ref KinematicProjectile __result)
	{
		Quaternion rotation = Quaternion.FromToRotation(Vector3.forward, attacker.transform.forward);
		ObjectPoolSingleton.RequestComponent<KinematicProjectile>(out KinematicProjectile t, ammoAssetKey, origin, rotation);
		Vector3 vector = ProjectilePhysics.ComputeFiringVelocity(power, anticipatedTargetPosition - origin);
		bool homing = Homing.IsBricktronArrow(t, attacker) && Homing.HasLineOfSight(t, attacker, aimOrigin, target);
		if (homing)
		{
			Homing.Targets[t] = target;
			inaccuracy = 0f;
		}
		else
		{
			Homing.Targets.Remove(t);
		}
		Vector3 inaccurateVelocity = Projectile.GetInaccurateVelocity(vector, inaccuracy);
		t.InitBeforeSpawn(origin, aimOrigin, inaccurateVelocity, attacker, target.Object, anticipatedTargetPosition, hitPointDamage);
		t.enabled = true;
		__result = t;
		return false;
	}
}

[Feature(Features.ArcherAccuracy, Features.ArcherAccuracyInfo)]
[HarmonyPatch(typeof(KinematicProjectile), nameof(KinematicProjectile.AcceptCollision))]
internal static class AcceptCollisionPatch
{
	private static bool Prefix(KinematicProjectile __instance, RaycastHit hit, ref bool __result)
	{
		GameObject hitObject = ProjectileAiming.GetSignificantParent(hit.collider).gameObject;
		if (Homing.IsHoming(__instance, out _))
		{
			__result = hitObject == __instance.Target;
			return false;
		}
		// Bricktron arrows never hit allies, whatever the game mode's arrowShootThroughAllies says.
		if (__instance.Archetype == KinematicProjectile.AttackArchetype.Arrow && __instance.Attacker != null && hitObject != __instance.Target && Affiliation.IsAllied(__instance.Attacker, hitObject))
		{
			__result = false;
			return false;
		}
		return true;
	}
}

// Restarts the ballistic arc every frame, aimed at where the target is now.
[Feature(Features.ArcherAccuracy, Features.ArcherAccuracyInfo)]
[HarmonyPatch(typeof(KinematicProjectile), nameof(KinematicProjectile.Move))]
internal static class SteerPatch
{
	private static void Prefix(KinematicProjectile __instance)
	{
		if (__instance == null || ((bool)__instance.magic && __instance.UseMagicProjectileComponent) || !Homing.IsHoming(__instance, out Location target))
		{
			return;
		}
		Vector3 toTarget = target.RangeHitPosition - __instance.transform.position;
		float speed = __instance.FiredVelocity.magnitude;
		Vector3 velocity = ProjectilePhysics.ComputeFiringVelocity(speed, toTarget);
		if (float.IsNaN(velocity.x) || float.IsNaN(velocity.y) || velocity.sqrMagnitude < 0.01f)
		{
			velocity = toTarget.normalized * speed;
		}
		__instance.FiredVelocity = velocity;
		__instance.FiredPosition = __instance.transform.position;
	}
}
