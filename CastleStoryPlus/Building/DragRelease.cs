using System.Collections.Generic;
using System.Reflection;
using Brix.Input;
using CastleStoryPlus.Core;
using HarmonyLib;

namespace CastleStoryPlus.Building;

// A drag build (a row of blocks, a wall) only ends when the game sees the mouse button go up while the cursor is over
// the world. Released over the interface, outside the window or while the window lost focus, the game never saw it,
// and the row kept following the cursor with the button up. Every time a placement picker follows the cursor, a drag
// whose button is no longer held is now dropped (nothing placed), and the held block follows the cursor again.
[Feature(Features.DragRelease, Features.DragReleaseInfo)]
[HarmonyPatch]
internal static class DragRelease
{
	private const string PlaceAction = "cam_action1";

	private static IEnumerable<MethodBase> TargetMethods()
	{
		yield return AccessTools.Method(typeof(GrabberComponentV2), nameof(GrabberComponentV2.OnHoverAny));
		yield return AccessTools.Method(typeof(ObjectPlacementPicker), nameof(ObjectPlacementPicker.OnHoverAny));
		yield return AccessTools.Method(typeof(BlueprintPlacementPicker), nameof(BlueprintPlacementPicker.OnHoverAny));
		yield return AccessTools.Method(typeof(ProjectPlacementPicker), nameof(ProjectPlacementPicker.OnHoverTerrain));
	}

	private static void Prefix(GrabberComponentV2 __instance)
	{
		// In the frame the button goes up the cursor is followed before the release is handled (which places the
		// row): only a release the game did not take in that frame ends the drag.
		if (!__instance._holdingClick || PickingUtility.Player == null || PickingUtility.Player.GetButton(PlaceAction) || PickingUtility.Player.GetButtonUp(PlaceAction))
		{
			return;
		}
		__instance._holdingClick = false;
		if (__instance._heldDragger != null)
		{
			__instance._heldDragger.ClearSlaves();
		}
	}
}
