using System.Collections.Generic;
using BepInEx.Configuration;
using Brix.Components;
using Brix.Engine;
using Brix.Engine.Blocks;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.AI.Damage;
using Brix.Game.AI.InstructionType;
using Brix.Game.AI.Nodes;
using Brix.Game.Components;
using Brix.Input;
using Brix.Lifecycle.Pooling;
using Brix.UI.Icons;
using CastleStoryPlus.Core;
using CastleStoryPlus.Giant;
using HarmonyLib;
using Motus.Behavior;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.WorkerAI;

// Idle chore: a builder with nothing to do repairs damaged blocks near the base (home crystal and stockpiles), as
// the player's "repair" order does: no materials, the block gets its health back. Only offered by IdleProject, so any
// real work comes first, and real work interrupts it (the health already given back stays). Blocks are left alone
// while they are being hit, inside a repair zone (its workers do them), or for a while after a worker could not get
// to them. Destroyed blocks are not rebuilt and workshops are not repaired.
[Feature(Features.AutoRepair, Features.AutoRepairInfo)]
internal static class AutoRepair
{
	internal static ConfigEntry<int> Radius;

	// A block hit this recently is under attack: workers stay away.
	private const float UnderAttackSeconds = 20f;

	// A claim expires on its own in case a task is dropped without running its Finally.
	private const float ClaimSeconds = 90f;

	// A block a worker set out for and did not finish is left alone for a while.
	private const float FailedSeconds = 60f;

	private static readonly Dictionary<XYZ, float> Claims = new Dictionary<XYZ, float>();

	private static readonly Dictionary<Labor, XYZ> Plans = new Dictionary<Labor, XYZ>();

	private static readonly Dictionary<XYZ, float> Failed = new Dictionary<XYZ, float>();

	private static readonly Dictionary<XYZ, float> HitAt = new Dictionary<XYZ, float>();

	public static readonly LaborInstruction<Location> Repair = new LaborInstruction<Location>("AutoRepair", IconKeys.RepairTask, (Labor labor, Location location) => RepairNode(labor, location));

	private static void Enable()
	{
		Radius = Plugin.Cfg.Bind("AutoRepair", "Radius", 15, "Idle builders repair damaged blocks within this many blocks of the home crystal or a stockpile (" + BaseRanges.Min + " to " + BaseRanges.Max + "; also set on the home crystal's task).");
		BaseRanges.Register(Features.AutoRepair, Radius, "Auto repair range (blocks)", "RepairTask", new Color(0.35f, 0.9f, 0.35f, 1f), true);
		PlacedBlockDamageReceiver.OnBlockDamaged.Connect((BlockData block, bool stone) =>
		{
			if (block != null)
			{
				HitAt[block.Position] = Time.time;
			}
		});
		GameSession.OnLeave(() =>
		{
			Claims.Clear();
			Plans.Clear();
			Failed.Clear();
			HitAt.Clear();
		});
	}

	// The nearest damaged block near the base this builder may repair, claimed. False if none.
	public static bool TryPlan(Labor labor, out XYZ target)
	{
		target = XYZ.zero;
		if (!NetworkServer.active || labor == null || !labor.recepteur.IsEmpty() || labor.Profession.OccupationType != Occupation.Type.Builder || InputModeController.DefaultMode == InputMode.worldEditor)
		{
			return false;
		}
		BlockEngine engine = BrixSingleton<BlockEngine>.Instance;
		if (engine == null || engine.BlockTracker == null || engine.BlockTracker.DamagedBlocks.Count == 0)
		{
			return false;
		}
		ExpireClaims();
		List<Vector3> anchors = Anchors(labor.faction);
		if (anchors.Count == 0)
		{
			return false;
		}
		List<RepairProject> zones = RepairZones();
		Vector3 position = labor.transform.position;
		float radius = BaseRanges.Clamped(Radius);
		float best = float.MaxValue;
		bool found = false;
		foreach (BlockData block in engine.BlockTracker.DamagedBlocks)
		{
			if (block == null || !block.IsDamaged || block.IsInvincible)
			{
				continue;
			}
			XYZ voxel = block.Position;
			if (Claims.ContainsKey(voxel) || Waiting(Failed, voxel, 0f) || Waiting(HitAt, voxel, UnderAttackSeconds))
			{
				continue;
			}
			Vector3 at = voxel.ToVector3();
			if (!Near(anchors, at, radius) || InZone(zones, voxel))
			{
				continue;
			}
			float distance = (at - position).sqrMagnitude;
			if (distance < best)
			{
				best = distance;
				target = voxel;
				found = true;
			}
		}
		if (!found)
		{
			return false;
		}
		Claims[target] = Time.time + ClaimSeconds;
		Plans[labor] = target;
		return true;
	}

	// For a cooldown table: true while the time given (plus the extra seconds) has not passed.
	private static bool Waiting(Dictionary<XYZ, float> table, XYZ voxel, float extra)
	{
		return table.TryGetValue(voxel, out float at) && Time.time < at + extra;
	}

	private static Node RepairNode(Labor labor, Location location)
	{
		XYZ voxel = XYZ.FromVector3(location.Position);
		return Node.Sequence.Label("Auto repair")
			.Do((Labor l, Location at) => l.Repair(at), labor, location)
			.Finally((Labor l) =>
			{
				// Still damaged: the worker could not get to it or was called away.
				BlockData block = Voxel.GetBlockData(voxel) as BlockData;
				if (block != null && block.IsDamaged)
				{
					Failed[voxel] = Time.time + FailedSeconds;
				}
				Release(l);
			}, labor);
	}

	public static void Release(Labor labor)
	{
		if (labor != null && Plans.TryGetValue(labor, out XYZ voxel))
		{
			Plans.Remove(labor);
			Claims.Remove(voxel);
		}
	}

	// The home crystal and the faction's stockpiles.
	private static List<Vector3> Anchors(Faction faction)
	{
		List<Vector3> result = new List<Vector3>();
		FireflyNest nest = GiantBricktron.HomeNest(faction);
		if (nest != null)
		{
			result.Add(nest.transform.position);
		}
		HashSet<GameObject> stockpiles = BrixSingleton<AutoList>.Instance?.GetInstances(ObjetsDynamiques.Palette);
		if (stockpiles != null)
		{
			foreach (GameObject pile in stockpiles)
			{
				if (pile == null || !pile.activeInHierarchy)
				{
					continue;
				}
				GameComponent component = pile.GetComponent<GameComponent>();
				if (component != null && component.faction == faction)
				{
					result.Add(pile.transform.position);
				}
			}
		}
		return result;
	}

	private static bool Near(List<Vector3> anchors, Vector3 at, float radius)
	{
		foreach (Vector3 anchor in anchors)
		{
			if ((anchor - at).sqrMagnitude <= radius * radius)
			{
				return true;
			}
		}
		return false;
	}

	private static List<RepairProject> RepairZones()
	{
		List<RepairProject> result = new List<RepairProject>();
		HashSet<GameObject> zones = BrixSingleton<AutoList>.Instance?.GetInstances(Groupes.Repair);
		if (zones == null)
		{
			return result;
		}
		foreach (GameObject zone in zones)
		{
			RepairProject project = (zone != null && zone.activeInHierarchy) ? zone.GetComponent<RepairProject>() : null;
			if (project != null && project.repairProvider != null)
			{
				result.Add(project);
			}
		}
		return result;
	}

	private static bool InZone(List<RepairProject> zones, XYZ voxel)
	{
		foreach (RepairProject zone in zones)
		{
			if (zone.IsInRange(voxel))
			{
				return true;
			}
		}
		return false;
	}

	private static void ExpireClaims()
	{
		List<XYZ> expired = null;
		foreach (KeyValuePair<XYZ, float> pair in Claims)
		{
			if (Time.time > pair.Value)
			{
				if (expired == null)
				{
					expired = new List<XYZ>();
				}
				expired.Add(pair.Key);
			}
		}
		if (expired == null)
		{
			return;
		}
		foreach (XYZ voxel in expired)
		{
			Claims.Remove(voxel);
		}
		List<Labor> stale = new List<Labor>();
		foreach (KeyValuePair<Labor, XYZ> pair in Plans)
		{
			if (pair.Key == null || !Claims.ContainsKey(pair.Value))
			{
				stale.Add(pair.Key);
			}
		}
		foreach (Labor labor in stale)
		{
			Plans.Remove(labor);
		}
	}
}

// After picking up loose items (AutoCleanupPatch) and before tidying stockpiles or going to the gather point, an
// idle builder repairs damaged blocks nearby.
[Feature(Features.AutoRepair, Features.AutoRepairInfo)]
[HarmonyPatch(typeof(IdleProject), nameof(IdleProject._GetInstruction))]
[HarmonyPriority(Priority.HigherThanNormal)]
internal static class AutoRepairPatch
{
	private static bool Prefix(IdleProject __instance, Labor labor, Var<OrderPackage<Labor>> result, bool __runOriginal)
	{
		if (!__runOriginal || !labor.recepteur.IsEmpty() || !__instance.GatherAtIdlePoint(labor))
		{
			return __runOriginal;
		}
		if (!AutoRepair.TryPlan(labor, out XYZ voxel))
		{
			return true;
		}
		OrderPackage<Labor> package = Order.Package.OfLocation(AutoRepair.Repair, new SpaceLocation(voxel.ToVector3(), Quaternion.identity), Labor.DecisionSource.Project);
		// Interruptible, so real work always wins.
		package.SetPrepare((BaseLabor worker) => __instance.Prepare(worker as Labor, false, Activity.Chilling));
		result.Set(package);
		return false;
	}
}
