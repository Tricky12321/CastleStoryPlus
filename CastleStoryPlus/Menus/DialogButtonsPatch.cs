using System.Collections.Generic;
using Brix.UI.Builder.Menu;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.Menus;

// The dialog prefab only ships a fixed number of button clones; duplicate the last one when a dialog
// needs more (the Quit Game dialog has five buttons with the save buttons).
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
			ContaineeExtended added = copy.GetComponent<ContaineeExtended>();
			Added.Add(added);
			__instance.btnClones.Add(added);
		}
	}

	// The game hides the buttons a dialog does not use through its container, which does not know the copies: a
	// copy kept its last label and showed in the next, shorter dialog (a second CANCEL). Copies are shown only
	// when the dialog uses them.
	private static void Postfix(OkDialogContainerPopulator __instance, OkDialogContainerPopulator.GenericDialog genericDialog)
	{
		if (__instance.btnClones == null || genericDialog?._btns == null)
		{
			return;
		}
		for (int i = 0; i < __instance.btnClones.Count; i++)
		{
			ContaineeExtended button = __instance.btnClones[i];
			if (button != null && Added.Contains(button))
			{
				button.gameObject.SetActive(i < genericDialog._btns.Length);
			}
		}
	}

	private static readonly HashSet<ContaineeExtended> Added = new HashSet<ContaineeExtended>();
}
