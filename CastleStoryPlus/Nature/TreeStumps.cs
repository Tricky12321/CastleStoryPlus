using System.Collections.Generic;
using BepInEx.Configuration;
using Brix.Components;
using Brix.Engine;
using Brix.Engine.Blocks;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Input;
using Brix.Legacy;
using Brix.Lifecycle.Pooling;
using Brix.Utils;
using CastleStoryPlus.Core;
using HarmonyLib;
using Motus.Behavior;
using UnityEngine;
using UnityEngine.Networking;
using FactoryBlocks = Brix.External.Factories.Blocks;

namespace CastleStoryPlus.Nature;

// A felled tree leaves a stump block that nothing could remove. Tree harvest areas now also clear the stumps in
// their radius, after the same axe work as a tree but [TreeStumps] WorkMultiplier times longer, and a stump gives
// [TreeStumps] Logs logs (a full tree gives 3). The stump goals are ordinary tree goals of the harvest area,
// marked as stumps; the server does the work and removes the block, so it works in multiplayer.
[Feature(Features.TreeStumps, Features.TreeStumpsInfo)]
internal static class TreeStumps
{
	internal static ConfigEntry<float> WorkMultiplier;

	internal static ConfigEntry<int> Logs;

	// DamagedTree.maxHp: the axe damage that fells a tree.
	private const int TreeHp = 180;

	private static readonly Dictionary<TreeGoal, XYZ> StumpGoals = new Dictionary<TreeGoal, XYZ>();

	private static readonly Dictionary<XYZ, int> Damage = new Dictionary<XYZ, int>();

	private static void Enable()
	{
		WorkMultiplier = Plugin.Cfg.Bind("TreeStumps", "WorkMultiplier", 3f, new ConfigDescription("Axe work to remove a tree stump, compared to felling a tree.", new AcceptableValueRange<float>(0.5f, 10f)));
		Logs = Plugin.Cfg.Bind("TreeStumps", "Logs", 1, new ConfigDescription("Logs a removed tree stump gives (a felled tree gives 3).", new AcceptableValueRange<int>(0, 3)));
		GameSession.OnLeave(() =>
		{
			StumpGoals.Clear();
			Damage.Clear();
		});
	}

	internal static bool IsStumpGoal(TreeGoal goal, out XYZ position)
	{
		position = XYZ.zero;
		return goal != null && StumpGoals.TryGetValue(goal, out position);
	}

	internal static bool HasStump(XYZ position)
	{
		BlockEngine engine = BrixSingleton<BlockEngine>.Instance;
		IBlockData block = (engine != null) ? engine.GetBlockData(position) : null;
		return block != null && block.IsOfType(FactoryBlocks.Wood_Stump);
	}

	// Stump blocks within the radius, added to positions and stumps.
	internal static void AddStumpsInRadius(XYZ center, int radius, HashSet<XYZ> positions, HashSet<XYZ> stumps)
	{
		BlockEngine engine = BrixSingleton<BlockEngine>.Instance;
		if (engine == null)
		{
			return;
		}
		foreach (RootBlockData block in engine.RootBlocks)
		{
			if (block.IsOfType(FactoryBlocks.Wood_Stump) && TreeRegistry.IsInRadius(block.Position, center, radius))
			{
				positions.Add(block.Position);
				stumps.Add(block.Position);
			}
		}
	}

	internal static void Mark(TreeGoal goal, XYZ position, bool isStump)
	{
		if (!isStump)
		{
			StumpGoals.Remove(goal);
			return;
		}
		if (!StumpGoals.ContainsKey(goal))
		{
			// Goals are pooled: forget the mark when this one is released.
			goal.OnReleaseOrDestroySignal.ConnectOnce((GameObject g) =>
			{
				StumpGoals.Remove(goal);
			});
		}
		StumpGoals[goal] = position;
	}

	// Server: one axe swing on a stump.
	internal static void Strike(XYZ position, Labor labor)
	{
		if (!NetworkServer.active || labor == null || !HasStump(position))
		{
			return;
		}
		int damage;
		Damage.TryGetValue(position, out damage);
		damage += Mathf.Max(1, labor.Occupation.Profession.WoodCutting);
		if (damage < Mathf.RoundToInt(TreeHp * WorkMultiplier.Value))
		{
			Damage[position] = damage;
			return;
		}
		Damage.Remove(position);
		BrixSingleton<BlockEngine>.Instance.GetBlockData(position).DeBlock(FactoryBlocks.Wood_Stump.ToBlockInfo().ID);
		for (int i = 0; i < Logs.Value; i++)
		{
			Vector3 at = position.ToVector3() + Vector3.up * (1 + i);
			if (ObjectPoolSingleton.Request(out GameObject log, FactoryBlocks.Wood_DoubleLog_AA, at, Quaternion.Euler(0f, Random.Range(0, 360), 90f)))
			{
				NetworkServer.Spawn(log, FactoryBlocks.Wood_DoubleLog_AA.AssetId);
				log.GetOrAddComponent<AffiliationComponent>().faction = labor.affiliation.faction;
			}
		}
	}
}

// Harvest areas also list the stumps in their radius (same as TreeGoalProvider.Refresh, plus the stumps).
[Feature(Features.TreeStumps, Features.TreeStumpsInfo)]
[HarmonyPatch(typeof(TreeGoalProvider), nameof(TreeGoalProvider.Refresh))]
internal static class TreeStumpsRefreshPatch
{
	private static bool Prefix(TreeGoalProvider __instance)
	{
		if (!NetworkServer.active || InputModeController.DefaultMode == InputMode.worldEditor)
		{
			return true;
		}
		XYZ center = __instance.TreeSearchPosition;
		int radius = __instance.TreeSearchRadius;
		HashSet<XYZ> positions = new HashSet<XYZ>();
		TreeRegistry.Instance.GetTreesInRadius(center, radius).AddTo(positions);
		HashSet<XYZ> stumps = new HashSet<XYZ>();
		TreeStumps.AddStumpsInRadius(center, radius, positions, stumps);
		__instance.RemoveOutdatedGoals(positions);
		__instance.AddNewGoals(positions);
		foreach (KeyValuePair<XYZ, TreeGoal> goal in __instance.goals)
		{
			TreeStumps.Mark(goal.Value, goal.Key, stumps.Contains(goal.Key));
		}
		__instance.timeAtLastRefresh = Time.time;
		return false;
	}
}

// A stump goal chops the stump instead of spawning a damaged tree.
[Feature(Features.TreeStumps, Features.TreeStumpsInfo)]
[HarmonyPatch(typeof(TreeGoal), nameof(TreeGoal.SpawnAndAttackTree))]
internal static class TreeStumpsWorkPatch
{
	private static bool Prefix(TreeGoal __instance, Labor labor, ref Node __result)
	{
		if (!TreeStumps.IsStumpGoal(__instance, out XYZ position))
		{
			return true;
		}
		__result = (StaticNode)Node.Sequence.Do(() =>
		{
			TreeStumps.Strike(position, labor);
		});
		return false;
	}
}

[Feature(Features.TreeStumps, Features.TreeStumpsInfo)]
[HarmonyPatch(typeof(TreeGoal), nameof(TreeGoal.WorkDone))]
internal static class TreeStumpsDonePatch
{
	private static bool Prefix(TreeGoal g, ref bool __result)
	{
		if (!TreeStumps.IsStumpGoal(g, out XYZ position))
		{
			return true;
		}
		__result = !TreeStumps.HasStump(position);
		return false;
	}
}

[Feature(Features.TreeStumps, Features.TreeStumpsInfo)]
[HarmonyPatch(typeof(TreeGoal), nameof(TreeGoal.IsValid))]
internal static class TreeStumpsValidPatch
{
	private static bool Prefix(TreeGoal __instance, ref bool __result)
	{
		if (!TreeStumps.IsStumpGoal(__instance, out XYZ position))
		{
			return true;
		}
		__result = TreeStumps.HasStump(position);
		return false;
	}
}
