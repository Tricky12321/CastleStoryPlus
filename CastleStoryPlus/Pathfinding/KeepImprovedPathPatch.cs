using System.Collections.Generic;
using Brix.Game.AI;
using CastleStoryPlus.Core;
using HarmonyLib;

namespace CastleStoryPlus.Pathfinding;

// WrapUpSearch called InitImprove() again. It already ran before ImproveLoop, and calling it again reset
// SuccessLink to the coarse result, throwing away the improved path (workers paused after a pick-up).
[Feature(Features.Pathfinding, Features.PathfindingInfo)]
[HarmonyPatch(typeof(SearchPathRequest), nameof(SearchPathRequest.WrapUpSearch))]
internal static class KeepImprovedPathPatch
{
	private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
	{
		List<CodeInstruction> code = new List<CodeInstruction>(instructions);
		for (int i = 0; i < code.Count - 1; i++)
		{
			if (code[i].IsLdarg(0) && code[i + 1].Calls(AccessTools.Method(typeof(SearchPathRequest), nameof(SearchPathRequest.InitImprove))))
			{
				code[i].opcode = System.Reflection.Emit.OpCodes.Nop;
				code[i].operand = null;
				code[i + 1].opcode = System.Reflection.Emit.OpCodes.Nop;
				code[i + 1].operand = null;
				break;
			}
		}
		return code;
	}
}
