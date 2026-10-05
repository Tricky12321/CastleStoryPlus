using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using Brix.Game.AI;
using Brix.Pathfinding;
using CastleStoryPlus.Core;
using HarmonyLib;

namespace CastleStoryPlus.Pathfinding;

// The A* queues rank a node by distance-to-goal * 45 + length * 10 + weight * 2. A flat step has length 1 and
// weight 1, so it costs 12, and the distance weight should be about that. At 45 (eps ~3.75) the search runs
// straight at the goal and takes the first way around whatever is in between, e.g. up the side of a wall that
// faces the goal, along it and back down. Lower gives shorter routes, but each search visits more nodes.
[Feature(Features.Pathfinding, Features.PathfindingInfo)]
[HarmonyPatch]
internal static class HeuristicPatch
{
	private const int OriginalWeight = 45;

	private const int DefaultWeight = 14;

	private const int DefaultEnemyWeight = 20;

	// The game gives corruptrons 20 search steps per update (workers 100).
	internal const int OriginalEnemyBudget = 20;

	private const int DefaultEnemyBudget = 50;

	internal static ConfigEntry<int> Weight;

	internal static ConfigEntry<int> EnemyWeight;

	internal static ConfigEntry<int> EnemyBudget;

	private static void Enable()
	{
		Weight = Plugin.Cfg.Bind("Pathfinding", "HeuristicWeight", DefaultWeight, new ConfigDescription("A* distance weight. A flat step costs 12: close to 12 gives the shortest routes, higher searches faster but takes detours (the game used 45).", new AcceptableValueRange<int>(12, OriginalWeight)));
		EnemyWeight = Plugin.Cfg.Bind("Pathfinding", "EnemyHeuristicWeight", DefaultEnemyWeight, new ConfigDescription("A* distance weight for enemies (AI factions). Lower finds better ways around cliffs and walls, but their searches take more steps.", new AcceptableValueRange<int>(12, OriginalWeight)));
		EnemyBudget = Plugin.Cfg.Bind("Pathfinding", "EnemySearchBudget", DefaultEnemyBudget, new ConfigDescription("Search steps per update for each enemy's route search (the game used 20). Higher finds routes sooner, at more CPU per enemy.", new AcceptableValueRange<int>(OriginalEnemyBudget, 200)));
	}

	// Set while an AI faction's (corruptrons') search runs: they have their own weight and budget. Their searches
	// cross the whole map, and at the game's weight they ran straight at their target and got stuck at cliffs.
	internal static bool AiSearch;

	private static int CurrentWeight()
	{
		if (AiSearch)
		{
			return (EnemyWeight != null) ? EnemyWeight.Value : DefaultEnemyWeight;
		}
		return (Weight != null) ? Weight.Value : DefaultWeight;
	}

	private static IEnumerable<MethodBase> TargetMethods()
	{
		yield return AccessTools.Method(typeof(PathAStarQueue), nameof(PathAStarQueue.Evaluate));
		yield return AccessTools.Method(typeof(SearchPathRequest.AStarQueue), nameof(SearchPathRequest.AStarQueue.Evaluate));
		yield return AccessTools.Method(typeof(SearchPathRequest.UnrollQueue), nameof(SearchPathRequest.UnrollQueue.Evaluate));
	}

	private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
	{
		MethodInfo current = AccessTools.Method(typeof(HeuristicPatch), nameof(CurrentWeight));
		foreach (CodeInstruction instruction in instructions)
		{
			if (instruction.LoadsConstant(OriginalWeight))
			{
				CodeInstruction replacement = new CodeInstruction(OpCodes.Call, current);
				replacement.labels.AddRange(instruction.labels);
				replacement.blocks.AddRange(instruction.blocks);
				yield return replacement;
			}
			else
			{
				yield return instruction;
			}
		}
	}
}

// Marks the search steps of AI factions for HeuristicPatch and gives them the enemy search budget.
[Feature(Features.Pathfinding, Features.PathfindingInfo)]
[HarmonyPatch]
internal static class HeuristicAiSearchPatch
{
	private static IEnumerable<MethodBase> TargetMethods()
	{
		yield return AccessTools.Method(typeof(SearchPathRequest), "Work");
		yield return AccessTools.Method(typeof(SearchPathRequest), "LoopImprovePath");
	}

	// __0 is the step budget of this update; enemies get theirs raised so the better search finishes as soon.
	private static void Prefix(SearchPathRequest __instance, ref int __0, out bool __state)
	{
		__state = HeuristicPatch.AiSearch;
		Navigation navigation = __instance.Navigation;
		bool ai = navigation != null && navigation.faction != null && navigation.faction.isAI;
		HeuristicPatch.AiSearch = ai;
		if (ai && HeuristicPatch.EnemyBudget != null && __0 == HeuristicPatch.OriginalEnemyBudget)
		{
			__0 = HeuristicPatch.EnemyBudget.Value;
		}
	}

	private static System.Exception Finalizer(System.Exception __exception, bool __state)
	{
		HeuristicPatch.AiSearch = __state;
		return __exception;
	}
}
