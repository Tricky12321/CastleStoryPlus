using System.Collections.Generic;
using Brix.Game.AI;
using CastleStoryPlus.Core;
using HarmonyLib;

namespace CastleStoryPlus.WorkerAI;

// Workers pick up their next task faster.
[Feature(Features.WorkerAI, Features.WorkerAIInfo)]
internal static class FasterWorkers
{
	private static void Enable()
	{
		// Staggers idle workers up to 17 ticks apart per project they check. 1 halves that.
		Labor._tickOffset = 1;
		// After a direct order a worker became InstinctOnly, which only turned into FreeAgent after a
		// 3 second wait. Soldiers still fall back to InstinctOnly through FreeAgent's validFor/onInvalid.
		AutonomyStatus.waitForOrder.onTaskEnded = (AutonomyStatus self) => AutonomyStatus.FreeAgent;
	}
}

// LookForWork ran every 10 ticks; now every 2.
[Feature(Features.WorkerAI, Features.WorkerAIInfo)]
[HarmonyPatch(typeof(Labor), nameof(Labor.Initialize))]
internal static class LookForWorkIntervalPatch
{
	private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
	{
		List<CodeInstruction> code = new List<CodeInstruction>(instructions);
		for (int i = 0; i < code.Count - 1; i++)
		{
			if (code[i].Calls(AccessTools.Method(typeof(Labor), nameof(Labor.LookForWorkSequence))) && code[i + 1].LoadsConstant(10))
			{
				code[i + 1] = new CodeInstruction(System.Reflection.Emit.OpCodes.Ldc_I4_2).MoveLabelsFrom(code[i + 1]);
				break;
			}
		}
		return code;
	}
}

// The waits inside LookForWork were 10 ticks; now 2.
[Feature(Features.WorkerAI, Features.WorkerAIInfo)]
[HarmonyPatch(typeof(Labor), nameof(Labor.LookForWorkSequence))]
internal static class LookForWorkWaitsPatch
{
	private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
	{
		return IL.ReplaceInt(instructions, 10, 2);
	}
}
