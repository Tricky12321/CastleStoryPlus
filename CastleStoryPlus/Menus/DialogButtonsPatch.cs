using Brix.UI.Builder.Menu;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.Menus;

// The dialog prefab only ships a fixed number of button clones; duplicate the last one when a dialog
// needs more (the Quit Game dialog has four buttons with SAVE & LEAVE).
[Feature(Features.SaveAndLeave, Features.SaveAndLeaveInfo)]
[HarmonyPatch(typeof(OkDialogContainerPopulator), nameof(OkDialogContainerPopulator.DisplayGenericDialog))]
internal static class DialogButtonsPatch
{
	private static void Prefix(OkDialogContainerPopulator __instance, OkDialogContainerPopulator.GenericDialog genericDialog)
	{
		if (__instance.btnClones == null || __instance.btnClones.Count == 0 || genericDialog?._btns == null)
		{
			return;
		}
		while (__instance.btnClones.Count < genericDialog._btns.Length)
		{
			ContaineeExtended template = __instance.btnClones[__instance.btnClones.Count - 1];
			GameObject copy = Object.Instantiate(template.gameObject, template.transform.parent, worldPositionStays: false);
			copy.transform.SetSiblingIndex(template.transform.GetSiblingIndex() + 1);
			__instance.btnClones.Add(copy.GetComponent<ContaineeExtended>());
		}
	}
}
