using System.Collections.Generic;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.AI.Damage;
using Brix.Game.AI.InstructionType;
using Brix.Game.Components;
using Brix.UI.Icons;
using CastleStoryPlus.Core;
using HarmonyLib;
using Motus.Behavior;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.Combat;

// Artificers heal by shooting: instead of the staff's healing aura (only while holding position), an artificer shoots
// a green healing bolt at the most wounded bricktron of its team within its attack range, one at a time, with the
// same aiming, animation and cooldown as its attack. Each bolt heals 10% of the target's maximum health and does no
// damage. Healing comes before attacking; an artificer carrying out a player's order is left alone. The staff stays
// in attack mode (no aura), also while holding position. Host only; the bolt is tinted green on every machine that
// has the mod.
[Feature(Features.ArtificerHealing, Features.ArtificerHealingInfo)]
internal class ArtificerHealing : MonoBehaviour
{
	internal const float HealFraction = 0.1f;

	// Team mates above this share of their health are not worth a bolt.
	private const float WoundedBelow = 0.95f;

	private const float CheckSeconds = 0.5f;

	private const float UnitListSeconds = 2f;

	// A team mate the artificer failed to shoot at is skipped by that artificer for this long.
	private const float FailedSkipSeconds = 3f;

	internal static readonly HashSet<ArtificerStaff> Staffs = new HashSet<ArtificerStaff>();

	// Counts as fighting while it runs, so the artificer's instinct does not replace it with an attack at once.
	internal static readonly CombatInstruction<Location> HealShot = new CombatInstruction<Location>("HealShot", IconKeys.WardHeal, (Labor labor, Location value) => (StaticNode)Node.Sequence.Do((Labor l) =>
	{
		l.Activity = Activity.Fighting;
	}, labor).Do((Labor l, Location target) => l.Occupation.Profession.ShootAt(target), labor, value));

	// Artificers shooting a healing bolt now.
	internal static readonly HashSet<Labor> Healing = new HashSet<Labor>();

	private readonly List<ArtificerStaff> _staffs = new List<ArtificerStaff>();

	private readonly Dictionary<Labor, Task> _tasks = new Dictionary<Labor, Task>();

	private readonly Dictionary<Labor, KeyValuePair<GameObject, float>> _failed = new Dictionary<Labor, KeyValuePair<GameObject, float>>();

	private readonly List<Labor> _units = new List<Labor>();

	private float _nextCheck;

	private float _nextUnitList;

	private static void Enable()
	{
		Plugin.Root.AddComponent<ArtificerHealing>();
	}

	private void Update()
	{
		if (!NetworkServer.active || Time.time < _nextCheck)
		{
			return;
		}
		_nextCheck = Time.time + CheckSeconds;
		Staffs.RemoveWhere((ArtificerStaff s) => s == null);
		Healing.RemoveWhere((Labor l) => l == null || !_tasks.TryGetValue(l, out Task t) || t == null || !l.HasCurrentOrPendingTask(t));
		if (Staffs.Count == 0)
		{
			_tasks.Clear();
			_failed.Clear();
			return;
		}
		if (Time.time >= _nextUnitList)
		{
			_nextUnitList = Time.time + UnitListSeconds;
			Live<Labor>.Active(_units);
		}
		_staffs.Clear();
		_staffs.AddRange(Staffs);
		foreach (ArtificerStaff staff in _staffs)
		{
			Check(staff);
		}
	}

	private void Check(ArtificerStaff staff)
	{
		CharacterState wielder = staff.wielder;
		if (wielder == null || !staff.gameObject.activeInHierarchy)
		{
			return;
		}
		Labor labor = wielder.GetComponent<Labor>();
		if (labor == null || !labor.gameObject.activeInHierarchy || !CanHeal(labor))
		{
			return;
		}
		Profession profession = labor.Profession;
		if (profession == null || !profession.ProjectileAttackAvailable(out ProjectileWeapon weapon) || weapon == null)
		{
			return;
		}
		GameObject target = MostWounded(labor, profession, Mathf.Min(weapon.MaxRange, profession.AggroDistance));
		if (target == null)
		{
			return;
		}
		Location location = Location.Of(target);
		Task task = HealShot.GetTask(labor, location);
		if (task == null)
		{
			return;
		}
		task.InitialPackage = new OrderPackage<Labor, Location>(HealShot, location, Labor.DecisionSource.Character);
		task.TaskEnded.ConnectOnce((TaskResult result) =>
		{
			Healing.Remove(labor);
			// Only a shot the artificer could not take (out of sight or reach) skips that team mate for a while; an
			// interrupted one does not.
			if (result == TaskResult.WorkerFailed && labor != null)
			{
				_failed[labor] = new KeyValuePair<GameObject, float>(target, Time.time + FailedSkipSeconds);
			}
		});
		_tasks[labor] = task;
		Healing.Add(labor);
		labor.SendOrder(task, Labor.DecisionSource.Character);
	}

	private bool CanHeal(Labor labor)
	{
		if (labor.Profession == null || labor.Profession.IsCorruptron())
		{
			return false;
		}
		// Already shooting a bolt.
		if (_tasks.TryGetValue(labor, out Task task) && task != null && labor.HasCurrentOrPendingTask(task))
		{
			return false;
		}
		// A player's order (moving, attacking a chosen target, ...) goes first.
		AutonomyStatus.Ids autonomy = labor.Autonomy.Id;
		if (autonomy == AutonomyStatus.Ids.waitForOrder || autonomy == AutonomyStatus.Ids.waitForOrderDefensive)
		{
			return false;
		}
		return labor.Activity != Activity.Working;
	}

	private GameObject MostWounded(Labor artificer, Profession profession, float range)
	{
		Vector3 position = artificer.transform.position;
		GameObject skip = null;
		if (_failed.TryGetValue(artificer, out KeyValuePair<GameObject, float> failed))
		{
			if (Time.time < failed.Value)
			{
				skip = failed.Key;
			}
			else
			{
				_failed.Remove(artificer);
			}
		}
		GameObject best = null;
		float bestShare = WoundedBelow;
		foreach (Labor unit in _units)
		{
			if (unit == null || unit == artificer || unit.gameObject == skip || !unit.gameObject.activeInHierarchy || !Affiliation.IsAllied(artificer.gameObject, unit.gameObject))
			{
				continue;
			}
			if ((unit.transform.position - position).sqrMagnitude > range * range)
			{
				continue;
			}
			BricktronDamageReceiver receiver = unit.GetComponent<BricktronDamageReceiver>();
			if (receiver == null || receiver.MaxHP <= 0f || receiver.CurrentHP <= 0f)
			{
				continue;
			}
			float share = receiver.CurrentHP / receiver.MaxHP;
			if (share >= bestShare || !profession.CanFireAt(Location.Of(unit.gameObject)))
			{
				continue;
			}
			best = unit.gameObject;
			bestShare = share;
		}
		return best;
	}

	internal static bool IsArtificer(GameObject go)
	{
		if (go == null)
		{
			return false;
		}
		foreach (ArtificerStaff staff in Staffs)
		{
			if (staff != null && staff.wielder != null && staff.wielder.gameObject == go)
			{
				return true;
			}
		}
		return false;
	}
}

// Staffs register when they get (or lose) a wielder.
[Feature(Features.ArtificerHealing, Features.ArtificerHealingInfo)]
[HarmonyPatch(typeof(ArtificerStaff), "RefreshWielder")]
internal static class ArtificerHealingWielderPatch
{
	private static void Postfix(ArtificerStaff __instance)
	{
		if (__instance.wielder != null)
		{
			ArtificerHealing.Staffs.Add(__instance);
		}
		else
		{
			ArtificerHealing.Staffs.Remove(__instance);
		}
	}
}

// The staff stays in attack mode while holding position (it heals with bolts instead of the aura).
[Feature(Features.ArtificerHealing, Features.ArtificerHealingInfo)]
[HarmonyPatch(typeof(ArtificerStaff), "OnWielderChangeState")]
internal static class ArtificerHealingStatePatch
{
	private static bool Prefix(ArtificerStaff __instance)
	{
		if (__instance.wielder == null || !__instance.wielder.Autonomy.IsDefensive)
		{
			return true;
		}
		// Only switch once (switching plays sounds); a staff left in healing mode has no ammo.
		if (__instance.weapon != null && __instance.weapon.AmmoCountGetter <= 0)
		{
			__instance.ToFire();
		}
		return false;
	}
}

// A bolt an artificer fires at a team mate is a healing bolt: it ends on the first hit, heals a team mate it hits
// and never deals damage.
[Feature(Features.ArtificerHealing, Features.ArtificerHealingInfo)]
[HarmonyPatch(typeof(KinematicProjectile), nameof(KinematicProjectile.FireAt))]
internal static class ArtificerHealBoltFirePatch
{
	// Healing bolts, with their DestroyOnHit setting when fired (projectiles are pooled).
	internal static readonly Dictionary<KinematicProjectile, bool> Bolts = new Dictionary<KinematicProjectile, bool>();

	private static void Postfix(KinematicProjectile __result, GameObject attacker, Location target)
	{
		if (__result == null || __result.Archetype != KinematicProjectile.AttackArchetype.ArtificerShot || target == null)
		{
			return;
		}
		GameObject victim = target.Object;
		if (victim == null || victim == attacker || victim.GetComponent<Labor>() == null || !Affiliation.IsAllied(attacker, victim))
		{
			return;
		}
		// Left as the game has it: a bolt destroyed on the hit is gone before the hit event reaches the clients
		// ("Did not find target for syncEvent").
		if (!Bolts.ContainsKey(__result))
		{
			Bolts[__result] = __result.DestroyOnHit;
		}
		GlowTint.ApplyBolt(__result.gameObject, GlowTint.Green);
	}
}

[Feature(Features.ArtificerHealing, Features.ArtificerHealingInfo)]
[HarmonyPatch(typeof(KinematicProjectile), "ProcessCollision")]
internal static class ArtificerHealBoltHitPatch
{
	private static bool Prefix(KinematicProjectile __instance, RaycastHit hit)
	{
		if (!ArtificerHealBoltFirePatch.Bolts.ContainsKey(__instance))
		{
			return true;
		}
		Transform parent = (hit.collider != null) ? ProjectileAiming.GetSignificantParent(hit.collider) : null;
		GameObject attacker = __instance.NetworkAttacker;
		if (parent != null && attacker != null && parent.gameObject != attacker && Affiliation.IsAllied(attacker, parent.gameObject))
		{
			BricktronDamageReceiver receiver = parent.GetComponent<BricktronDamageReceiver>();
			if (receiver != null && receiver.CurrentHP > 0f)
			{
				receiver.Heal(receiver.MaxHP * ArtificerHealing.HealFraction);
			}
		}
		return false;
	}
}

[Feature(Features.ArtificerHealing, Features.ArtificerHealingInfo)]
[HarmonyPatch(typeof(KinematicProjectile), nameof(KinematicProjectile.ResetComponent))]
internal static class ArtificerHealBoltResetPatch
{
	private static void Postfix(KinematicProjectile __instance)
	{
		if (ArtificerHealBoltFirePatch.Bolts.TryGetValue(__instance, out bool destroyOnHit))
		{
			__instance.DestroyOnHit = destroyOnHit;
			ArtificerHealBoltFirePatch.Bolts.Remove(__instance);
		}
		GlowTint.Apply(__instance.gameObject, null);
	}
}

// Clients: a bolt flying from an artificer to a team mate is tinted green (the host tints it when firing).
[Feature(Features.ArtificerHealing, Features.ArtificerHealingInfo)]
[HarmonyPatch(typeof(MagicProjectile), nameof(MagicProjectile.OnStartClient))]
internal static class ArtificerHealBoltClientPatch
{
	private static void Postfix(MagicProjectile __instance)
	{
		if (NetworkServer.active)
		{
			return;
		}
		KinematicProjectile projectile = __instance.GetComponent<KinematicProjectile>();
		if (projectile == null || projectile.Archetype != KinematicProjectile.AttackArchetype.ArtificerShot)
		{
			return;
		}
		GameObject attacker = projectile.NetworkAttacker;
		GameObject victim = __instance.NetworktargetGO;
		bool heal = attacker != null && victim != null && victim != attacker && victim.GetComponent<Labor>() != null && Affiliation.IsAllied(attacker, victim);
		GlowTint.ApplyBolt(__instance.gameObject, heal ? GlowTint.Green : (Color?)null);
	}
}

// While an artificer shoots a healing bolt, its instinct does not send it to fight (which would interrupt the bolt).
[Feature(Features.ArtificerHealing, Features.ArtificerHealingInfo)]
[HarmonyPatch(typeof(Labor), "HasToFight")]
internal static class ArtificerHealingFightPatch
{
	private static bool Prefix(Labor __instance, ref bool __result)
	{
		if (ArtificerHealing.Healing.Contains(__instance))
		{
			__result = false;
			return false;
		}
		return true;
	}
}

// A bolt's trail is a pooled object of its own, fetched again for every shot: it is put back to its own colours when
// it is fetched, and tinted with the bolt.
[Feature(Features.ArtificerHealing, Features.ArtificerHealingInfo)]
[HarmonyPatch(typeof(ArrowTrailer), nameof(ArrowTrailer.OnRequested))]
internal static class ArtificerHealBoltTrailPatch
{
	private static void Postfix(ArrowTrailer __instance)
	{
		if (__instance.trail != null)
		{
			GlowTint.Apply(__instance.trail.gameObject, null);
		}
	}
}
