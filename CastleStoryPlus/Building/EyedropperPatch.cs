using Brix.Input;
using CastleStoryPlus.Core;
using HarmonyLib;

namespace CastleStoryPlus.Building;

[Feature(Features.Eyedropper, Features.EyedropperInfo)]
[HarmonyPatch(typeof(InputModeController), nameof(InputModeController.Update))]
internal static class EyedropperPatch
{
	private static void Prefix()
	{
		Eyedropper.Update();
	}
}
