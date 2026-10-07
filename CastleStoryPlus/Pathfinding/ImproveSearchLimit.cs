using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using Brix.Game.AI;
using CastleStoryPlus.Core;
using HarmonyLib;

namespace CastleStoryPlus.Pathfinding;

// A route is first found over coarse terrain blocks, then searched again in fine detail near the start to straighten
// it. That second search stopped after 100 nodes, and when it ran out the coarse route was kept. Off the top of a wall
// the right stairs are often more than 100 nodes away in the fine search, so a bricktron walked to far stairs while
// stairs right next to him led down. The fine search now gets more nodes; it still runs a few per update.
[Feature(Features.Pathfinding, Features.PathfindingInfo)]
[HarmonyPatch(typeof(SearchPathRequest), "InitSearch")]
internal static class ImproveSearchLimit
{
	private const int OriginalLimit = 100;

	private const int DefaultLimit = 1000;

	internal static ConfigEntry<int> Limit;

	private static void Enable()
	{
		Limit = Plugin.Cfg.Bind("Pathfinding", "ImproveSearchLimit", DefaultLimit, new ConfigDescription("Nodes the fine search may visit to straighten a coarse route (the game used 100). Higher finds the nearby stairs instead of a detour, at a little more CPU per search.", new AcceptableValueRange<int>(OriginalLimit, 5000)));
	}

	private static int CurrentLimit()
	{
		return (Limit != null) ? Limit.Value : DefaultLimit;
	}

	private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
	{
		MethodInfo current = AccessTools.Method(typeof(ImproveSearchLimit), nameof(CurrentLimit));
		foreach (CodeInstruction instruction in instructions)
		{
			if (instruction.LoadsConstant(OriginalLimit))
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
