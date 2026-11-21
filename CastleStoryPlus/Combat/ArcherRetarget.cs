using System.Collections.Generic;
using Brix.Engine;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.AI.Nodes;
using Brix.Game.Components;
using Brix.Game.Locomotion;
using CastleStoryPlus.Core;
using HarmonyLib;
using Motus.Behavior;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.Combat;

// A ranged fighter (archer, arbalist, slinger, ...) of a player's team that could not shoot its target sat and
// stuttered for as long as the target stayed where it was, often behind another enemy in a crowd (#165). The game
// tests the shot (Profession.CanFireAt) three ways: picking a target and deciding to shoot (from the block's centre
// while the bow is down, from the bow while it is up), and again all through the aim, from the bow drawn and turned to
// the target. Where the drawn bow's test fails and the others pass, the fighter aims for ObstructionTimeOut (3 s), gives
// up ("impossible" sound, Archery.AimingToIdleArchery), and the fight's next shot (AttackInfo.DoRangedAction) takes the
// same target again, as the target is only looked for anew every 100 ticks and the test there passes again. Where the
// test in DoRangedAction fails, the whole fight order fails, the bow comes down and the instinct, testing from the
// block's centre, hands it the same target again. Nothing in it ever changes until the fighter is moved a block.
//
// Now a target the fighter could not shoot from where it stands (the aim given up, the shot not let off, or the test
// before the shot failed) is left be for a while (BlockedShots: 4 s, doubled each time again up to 16 s, on that block
// only): every test of the shot at it from that block fails, so the fight and the instinct pick another enemy it can
// shoot. The enemy in the line of fire comes first (the front one of a crowd, its own target then), else the nearest
// one in sight (the game's own search). When the time is up the target is tried again from the same block, without the
// fighter having to move; a shot that gets off forgets it at once. The fight order no longer fails on a blocked shot,
// it looks for its next target at once instead.
//
// A target the player ordered (attack) is kept: blocked from where the fighter stands it does not shoot another, but
// walks towards it until it can shoot it, as the game does for an ordered target out of sight; on each new block the
// shot is tried afresh. The host decides (combat runs there); a client keeps nothing.
[Feature(Features.ArcherRetarget, Features.ArcherRetargetInfo)]
internal static class ArcherRetarget
{
	internal static readonly BlockedShots<Profession, GameObject> Blocked = new BlockedShots<Profession, GameObject>();

	private static readonly RaycastHit[] Hits = new RaycastHit[16];

	private static readonly IComparer<RaycastHit> ByDistance = new NearestFirst();

	private sealed class NearestFirst : IComparer<RaycastHit>
	{
		public int Compare(RaycastHit a, RaycastHit b)
		{
			return a.distance.CompareTo(b.distance);
		}
	}

	private static void Enable()
	{
		GameSession.OnLeave(Blocked.Clear);
	}

	internal static bool IsPlayers(Labor labor)
	{
		return !labor.IsNullOrReleased() && labor.faction != null && !labor.faction.isAI;
	}

	internal static XYZ CellOf(Component unit)
	{
		return XYZ.FromVector3(unit.transform.position);
	}

	// Host: the fighter could not shoot the target from where it stands.
	internal static void Block(Profession profession, GameObject target)
	{
		if (profession == null || target.IsNullOrReleased() || !NetworkServer.active || !IsPlayers(profession.GetComponent<Labor>()))
		{
			return;
		}
		XYZ cell = CellOf(profession);
		Blocked.Block(profession, target, cell.x, cell.y, cell.z, Time.time);
	}

	internal static bool IsBlocked(Profession profession, GameObject target)
	{
		if (Blocked.Count == 0 || profession == null || target == null)
		{
			return false;
		}
		XYZ cell = CellOf(profession);
		return Blocked.IsBlocked(profession, target, cell.x, cell.y, cell.z, Time.time);
	}

	// The first enemy on the straight line from the fighter's bow to its target that it can shoot, looking past allies
	// and neutral units; none when the castle or rock is in the way first, or the target itself.
	internal static GameObject EnemyInTheWay(AttackInfo info, GameObject target)
	{
		ProjectileWeapon weapon = info.ProjectileWeapon;
		Labor attacker = info.Attacker;
		if (weapon == null || attacker.IsNullOrReleased())
		{
			return null;
		}
		Vector3 eye = info.Locomotion.ProjectileAimingPosition;
		Vector3 delta = Location.Of(target).RangeHitPosition - eye;
		float distance = delta.magnitude;
		if (distance < 0.5f)
		{
			return null;
		}
		int count = Physics.RaycastNonAlloc(eye, delta / distance, Hits, distance, weapon.AmmoCollisionLayer, QueryTriggerInteraction.Ignore);
		System.Array.Sort(Hits, 0, count, ByDistance);
		GameObject found = null;
		for (int i = 0; i < count; i++)
		{
			Collider collider = Hits[i].collider;
			if (collider == null || collider.transform.IsChildOf(attacker.transform))
			{
				continue;
			}
			GameObject hit = ProjectileAiming.GetSignificantParent(collider).gameObject;
			if (hit == target || hit.GetComponent<Labor>() == null)
			{
				break;
			}
			if (Affiliation.IsEnemy(attacker.gameObject, hit) && info.CanFireAt(hit))
			{
				found = hit;
				break;
			}
		}
		System.Array.Clear(Hits, 0, Hits.Length);
		return found;
	}

	internal static IEnumerator<ActivityProgress> Nothing()
	{
		yield break;
	}
}

// Every test of a shot at a target left be from this block fails, so the fighter's searches pass it over.
[Feature(Features.ArcherRetarget, Features.ArcherRetargetInfo)]
[HarmonyPatch(typeof(Profession), nameof(Profession.CanFireAt))]
internal static class ArcherRetargetTestPatch
{
	private static bool Prefix(Profession __instance, Location target, ref bool __result)
	{
		if (ArcherRetarget.Blocked.Count == 0 || target == null || !ArcherRetarget.IsBlocked(__instance, target.Object))
		{
			return true;
		}
		__result = false;
		return false;
	}
}

// The aim given up: the target could not be shot from the drawn bow for ObstructionTimeOut.
[Feature(Features.ArcherRetarget, Features.ArcherRetargetInfo)]
[HarmonyPatch(typeof(Archery), nameof(Archery.AimingToIdleArchery))]
internal static class ArcherRetargetAimPatch
{
	private static void Prefix(Archery __instance)
	{
		if (!NetworkServer.active || __instance.IsArcheryTargetMissing() || !__instance.IsArcheryAvailable())
		{
			return;
		}
		Profession profession = __instance.Profession;
		GameObject target = __instance.Aiming.Target.Object;
		if (!ArcherRetarget.IsBlocked(profession, target))
		{
			ArcherRetarget.Block(profession, target);
		}
	}
}

// The shot let off forgets the target; one not let off (blocked at the last moment) blocks it.
[Feature(Features.ArcherRetarget, Features.ArcherRetargetInfo)]
[HarmonyPatch(typeof(Archery), nameof(Archery.ProjectileFire))]
internal static class ArcherRetargetFirePatch
{
	private static void Postfix(Archery __instance)
	{
		if (!NetworkServer.active || __instance.IsArcheryTargetMissing())
		{
			return;
		}
		Profession profession = __instance.Profession;
		GameObject target = __instance.Aiming.Target.Object;
		if (__instance.ProjectileFired)
		{
			ArcherRetarget.Blocked.Forget(profession, target);
		}
		else if (!ArcherRetarget.IsBlocked(profession, target))
		{
			ArcherRetarget.Block(profession, target);
		}
	}
}

// The test before the shot failed: the game failed the whole fight order here. Now the target is left be, the enemy in
// the line of fire becomes the target (not for an ordered target) or the next target is looked for at once.
[Feature(Features.ArcherRetarget, Features.ArcherRetargetInfo)]
[HarmonyPatch(typeof(AttackInfo), nameof(AttackInfo.DoRangedAction))]
internal static class ArcherRetargetShotPatch
{
	// AttackInfo keeps a target it found for 100 ticks before it looks again.
	private const int KeepTicks = 100;

	private static bool Prefix(AttackInfo __instance, ref IEnumerator<ActivityProgress> __result)
	{
		GameObject target = __instance.RangedTarget;
		if (target == null || !NetworkServer.active || !ArcherRetarget.IsPlayers(__instance.Attacker))
		{
			return true;
		}
		Locomotion4 locomotion = __instance.Locomotion;
		if (!locomotion.IsInArcheryStance || !locomotion.IsReadyToShoot || __instance.CurrentFrame < __instance.NextAttackFrame || !locomotion.IsInGroundedState())
		{
			return true;
		}
		if (__instance.CanFireAt(target))
		{
			return true;
		}
		Profession profession = __instance.Profession;
		if (!target.IsNullOrReleased() && !ArcherRetarget.IsBlocked(profession, target))
		{
			ArcherRetarget.Block(profession, target);
		}
		if (__instance.AttackDestination != AttackInfo.AttackDestinationBehaviour.MustAttackDestination && !target.IsNullOrReleased())
		{
			GameObject front = ArcherRetarget.EnemyInTheWay(__instance, target);
			if (front != null)
			{
				__instance.RangedTarget = front;
				__instance.NextRangedTargetFrame = __instance.CurrentFrame + KeepTicks;
				return true;
			}
		}
		__instance.RangedTarget = null;
		__instance.NextRangedTargetFrame = __instance.CurrentFrame;
		__result = ArcherRetarget.Nothing();
		return false;
	}
}
