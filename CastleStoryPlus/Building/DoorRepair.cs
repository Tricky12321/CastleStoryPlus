using System.Collections.Generic;
using Brix.Components;
using Brix.Engine;
using Brix.External.Factories;
using Brix.Game.AI;
using Brix.Game.AI.Damage;
using Brix.Game.Components;
using Brix.Legacy;
using Brix.Lifecycle.Pooling;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.Building;

// Doors are objects with health of their own, not blocks, so the game's repairs never saw them: a repair zone only
// makes repair tasks for damaged blocks, and a repair task heals what is in its voxel (a block, a plant, or an object
// that occupies it, which a door does not). A damaged door now gets a repair task in the voxel it stands on, the task
// heals the door, and auto repair (AutoRepair) takes damaged doors near the base like damaged blocks.
[Feature(Features.DoorRepair, Features.DoorRepairInfo)]
internal static class DoorRepair
{
	private static readonly Factory.AssetKey[] Kinds = { ObjetsDynamiques.Porte, ObjetsDynamiques.HeavyDoor, ObjetsDynamiques.ShakyDoor };

	// The damaged doors by the voxel they stand on, found again at most once a second.
	private static readonly Dictionary<XYZ, Porte> Damaged = new Dictionary<XYZ, Porte>();

	private static float _foundAt = -1f;

	private static int _foundFrame = -1;

	internal static bool On { get; private set; }

	private static void Enable()
	{
		On = true;
	}

	private static void Disable()
	{
		On = false;
		Damaged.Clear();
		_foundAt = -1f;
	}

	// The voxel a door's repair task stands in: the lowest voxel of the door's opening.
	internal static XYZ Anchor(Porte door)
	{
		return XYZ.FromVector3(door.transform.position + Vector3.up * 0.5f);
	}

	internal static bool IsDamaged(Porte door)
	{
		if (door == null || !door.gameObject.activeInHierarchy)
		{
			return false;
		}
		IDamageReceiver health = door.damageReceiver;
		return health != null && health.IsDamaged && !health.IsInvincible && health.CurrentHP > 0f;
	}

	// The damaged doors of every team, by their anchor voxel.
	internal static Dictionary<XYZ, Porte> DamagedDoors()
	{
		if (_foundFrame == Time.frameCount || (_foundAt >= 0f && Time.time - _foundAt < 1f))
		{
			return Damaged;
		}
		_foundAt = Time.time;
		_foundFrame = Time.frameCount;
		Damaged.Clear();
		AutoList list = BrixSingleton<AutoList>.Instance;
		if (list == null)
		{
			return Damaged;
		}
		foreach (Factory.AssetKey kind in Kinds)
		{
			HashSet<GameObject> doors = list.GetInstances(kind);
			if (doors == null)
			{
				continue;
			}
			foreach (GameObject go in doors)
			{
				Porte door = (go != null) ? go.GetComponent<Porte>() : null;
				if (IsDamaged(door))
				{
					Damaged[Anchor(door)] = door;
				}
			}
		}
		return Damaged;
	}

	internal static Porte DamagedDoorAt(XYZ voxel)
	{
		if (!On)
		{
			return null;
		}
		Dictionary<XYZ, Porte> doors = DamagedDoors();
		if (doors.Count == 0 || !doors.TryGetValue(voxel, out Porte door))
		{
			return null;
		}
		// Healed since the list was made.
		return IsDamaged(door) ? door : null;
	}
}

// A repair zone makes a repair task in the voxel of a damaged door inside it (refreshed every 15 seconds and when
// the zone moves, as for blocks).
[Feature(Features.DoorRepair, Features.DoorRepairInfo)]
[HarmonyPatch(typeof(RepairGoalProvider), "VoxelNeedsGoal")]
internal static class DoorRepairGoalPatch
{
	private static void Postfix(XYZ pos, ref SpawnOrder __result)
	{
		if (__result.key == null && DoorRepair.DamagedDoorAt(pos) != null)
		{
			__result = new SpawnOrder(pos, XYZ.zero, Test.RepairGoal);
		}
	}
}

// A repair task heals what is in its voxel; in a damaged door's voxel (where there is no block) that is the door.
[Feature(Features.DoorRepair, Features.DoorRepairInfo)]
[HarmonyPatch(typeof(AdaptativeDamageReceiver), "FindProxiedDamageReceiver")]
internal static class DoorRepairTargetPatch
{
	private static void Postfix(AdaptativeDamageReceiver __instance, XYZ position, ref IDamageReceiver __result)
	{
		if (!(__result is NullDamageReceiver) && !(__result is DieOnDamage_DamageReceiver))
		{
			return;
		}
		if (__instance == null || __instance.GetComponent<RepairGoal>() == null)
		{
			return;
		}
		Porte door = DoorRepair.DamagedDoorAt(position);
		IDamageReceiver health = (door != null) ? door.damageReceiver : null;
		if (health == null)
		{
			return;
		}
		if (__result is DieOnDamage_DamageReceiver made)
		{
			made.Dispose();
		}
		__result = health;
	}
}
