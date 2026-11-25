#if UNITY6
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using Brix.Pathfinding.Test;
using CastleStoryPlus.Core;
using HarmonyLib;

namespace CastleStoryPlus.Pathfinding;

// Route searches ignored steps and stairs the player had built (#200, point 4): a bricktron at the foot of a cliff
// walked a long way round over the landscape instead of up a few blocks placed as steps. The game's graph had the
// steps (a climb branch onto each block), but weighed a climb as 25 blocks of flat ground and a drop as 17, and every
// smaller step up or down by the same share, while the smooth natural ground costs a flat step almost everywhere.
// With the mod's shorter routes (HeuristicPatch, the navigation mesh, whose links take the graph's weights) the
// searches take the cheapest route by these weights, so they kept to the landscape. A climb onto a block takes a
// bricktron about as long as 5 blocks of walking. The game's two penalties are now settings ([Pathfinding]
// ClimbCost, StepDownCost; StepCostRules has the arithmetic) with defaults near that, so a player's steps weigh what
// climbing them takes and natural ground with the same steps weighs the same. They count for places whose branches
// are made after a change (all of them when a game loads).
[Feature(Features.Pathfinding, Features.PathfindingInfo)]
[HarmonyPatch(typeof(TerrainPathNode), "TryAddRegularNeighborBranch")]
internal static class StepCosts
{
	internal static ConfigEntry<int> Climb;

	internal static ConfigEntry<int> StepDown;

	private static void Enable()
	{
		Climb = Plugin.Cfg.Bind("Pathfinding", "ClimbCost", StepCostRules.DefaultClimbPenalty, new ConfigDescription("Route cost of climbing up a ledge one block high, on top of a flat step's 10 (the game used 240, as much as 25 blocks of walking, so bricktrons went a long way round instead of up a few steps the player had built). Steps up of less than a block cost their share. Counts for the route graph built after the change (on loading a game).", new AcceptableValueRange<int>(StepCostRules.MinPenalty, StepCostRules.MaxPenalty)));
		StepDown = Plugin.Cfg.Bind("Pathfinding", "StepDownCost", StepCostRules.DefaultStepDownPenalty, new ConfigDescription("Route cost of stepping down a ledge one block high, on top of a flat step's 10 (the game used 160). Steps down of less than a block cost their share. Counts for the route graph built after the change (on loading a game).", new AcceptableValueRange<int>(StepCostRules.MinPenalty, StepCostRules.MaxPenalty)));
	}

	internal static int ClimbPenalty()
	{
		return (Climb != null) ? StepCostRules.Clamp(Climb.Value) : StepCostRules.DefaultClimbPenalty;
	}

	internal static int StepDownPenalty()
	{
		return (StepDown != null) ? StepCostRules.Clamp(StepDown.Value) : StepCostRules.DefaultStepDownPenalty;
	}

	// The game's 240 (climb) and 160 (step down) in the branch weights become the settings.
	private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
	{
		MethodInfo climb = AccessTools.Method(typeof(StepCosts), nameof(ClimbPenalty));
		MethodInfo stepDown = AccessTools.Method(typeof(StepCosts), nameof(StepDownPenalty));
		int climbs = 0;
		int stepDowns = 0;
		foreach (CodeInstruction instruction in instructions)
		{
			MethodInfo call = null;
			if (instruction.LoadsConstant(StepCostRules.GameClimbPenalty))
			{
				call = climb;
				climbs++;
			}
			else if (instruction.LoadsConstant(StepCostRules.GameStepDownPenalty))
			{
				call = stepDown;
				stepDowns++;
			}
			if (call == null)
			{
				yield return instruction;
				continue;
			}
			CodeInstruction replacement = new CodeInstruction(OpCodes.Call, call);
			replacement.labels.AddRange(instruction.labels);
			replacement.blocks.AddRange(instruction.blocks);
			yield return replacement;
		}
		if (climbs != 2 || stepDowns != 2)
		{
			Plugin.Log.LogWarning("StepCosts: expected 2 climb and 2 step down penalties in TryAddRegularNeighborBranch, found " + climbs + " and " + stepDowns);
		}
	}
}
#endif
