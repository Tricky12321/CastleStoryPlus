using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;

namespace CastleStoryPlus.Core;

// Small transpiler helpers.
internal static class IL
{
	// Replaces every integer constant 'from' with 'to'.
	public static IEnumerable<CodeInstruction> ReplaceInt(IEnumerable<CodeInstruction> instructions, int from, int to)
	{
		foreach (CodeInstruction instruction in instructions)
		{
			if (instruction.LoadsConstant(from))
			{
				CodeInstruction replacement = new CodeInstruction(OpCodes.Ldc_I4, to);
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
