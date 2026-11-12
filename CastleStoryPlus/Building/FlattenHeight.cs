using System.Collections.Generic;
using Brix.Engine;
using Brix.Game.AI;
using Brix.Game.AI.UI;
using Brix.Input;
using Brix.Utils.Locomotion;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.Building;

// The height handle of a flatten task (and a quarry's depth handle) jumped two blocks for one dragged: while dragging,
// the whole distance from where the drag started was added to the height at every step, and once more when the drag
// ended. The height now is the height at the start of the drag plus the distance dragged. The distance is measured
// from where the handle stood when the drag started: the handle itself moves with the height at every step, so
// measured from the handle the height flipped between the start and the dragged height every frame and ended on
// either one when the mouse was let go (#65).
// A flatten task's height could also only be set between the lowest and the highest ground in its area (one block
// above or below on fairly flat ground): the handle, the height and the area the task works in all stopped there. They
// now reach Extra blocks further up and down, so ground can be raised or dug down to any height within that.
[Feature]
[HarmonyPatch(typeof(DraggableVolumePlane), "OnDragStarted")]
internal static class FlattenHeight
{
	// Blocks a flatten task's height may lie above its highest or below its lowest ground.
	internal const int Extra = 16;

	// The height and the handle's height when the drag started.
	private static readonly Dictionary<DraggableVolumePlane, KeyValuePair<int, float>> Start = new Dictionary<DraggableVolumePlane, KeyValuePair<int, float>>();

	private static void Postfix(DraggableVolumePlane __instance)
	{
		if (Start.Count > 100)
		{
			Start.Clear();
		}
		Start[__instance] = new KeyValuePair<int, float>(__instance.GetY(), __instance.handle.Origin.y);
	}

	internal static bool TryStart(DraggableVolumePlane plane, out int y, out float handleY)
	{
		if (Start.TryGetValue(plane, out KeyValuePair<int, float> start))
		{
			y = start.Key;
			handleY = start.Value;
			return true;
		}
		y = 0;
		handleY = 0f;
		return false;
	}

	// The height for a drag that started at startY with the handle at startHandleY and is now at handleY: whole blocks
	// dragged, rounded toward the start.
	internal static int Dragged(int startY, float startHandleY, float handleY)
	{
		return startY + (int)Brix.Util.Calcule.RoundToward(handleY - startHandleY, 0f);
	}

	// A flatten task's (or tree farm's) height plane, as opposed to a quarry's depth.
	internal static bool IsFlatten(DraggableVolumePlane plane)
	{
		return plane != null && plane.volume is RectZone;
	}
}

[Feature]
[HarmonyPatch(typeof(DraggableVolumePlane), "SetYFromDrag")]
internal static class FlattenHeightDragPatch
{
	private static bool Prefix(DraggableVolumePlane __instance)
	{
		if (!FlattenHeight.TryStart(__instance, out int start, out float startHandleY))
		{
			return true;
		}
		__instance.SetY(FlattenHeight.Dragged(start, startHandleY, __instance.handle.Destination.y));
		return false;
	}
}

[Feature]
[HarmonyPatch(typeof(DraggableVolumePlane), nameof(DraggableVolumePlane.SetY))]
internal static class FlattenHeightClampPatch
{
	private static bool Prefix(DraggableVolumePlane __instance, int y)
	{
		if (!FlattenHeight.IsFlatten(__instance))
		{
			return true;
		}
		VoxelBounds bounds = __instance.Volume;
		int min = bounds.min.y - __instance.relativeMinY - FlattenHeight.Extra;
		int max = bounds.max.y + __instance.relativeMaxY + FlattenHeight.Extra;
		__instance.Y = Mathf.Clamp(y, min, max);
		__instance.UpdateVisuals();
		return false;
	}
}

// The handle can be dragged that far.
[Feature]
[HarmonyPatch(typeof(DraggableVolumePlane), "UpdateDrag")]
internal static class FlattenHeightHandlePatch
{
	private static void Postfix(DraggableVolumePlane __instance)
	{
		if (!FlattenHeight.IsFlatten(__instance))
		{
			return;
		}
		VoxelBounds bounds = __instance.Volume;
		Vector3 corner = __instance.CornerWithDrag;
		Vector3 min = corner.Flatten(bounds.VectorMin - Vector3.up * FlattenHeight.Extra);
		Vector3 max = corner.Flatten(bounds.VectorMax + Vector3.up * FlattenHeight.Extra);
		__instance.handle.SetMinMax(min, max);
	}
}

// The task works in its area up to the height (raised ground) or down to it (dug out), not only between its lowest
// and highest ground.
[Feature]
[HarmonyPatch(typeof(LandscapeController), "UpdateProvider")]
internal static class FlattenHeightAreaPatch
{
	private static bool Prefix(LandscapeController __instance)
	{
		LandscapeProjectUI ui = __instance.ui;
		if (ui == null || Brix.Game.User.LocalUser == null)
		{
			return true;
		}
		int threshold = ui.GetThresholdY();
		VoxelBounds bounds = ui.GetVolume();
		Vector3 min = bounds.min.ToVector3();
		Vector3 max = bounds.max.ToVector3();
		// Filled up to the height: blocks at it; dug down to it: blocks above it.
		min.y = Mathf.Min(min.y, threshold + 1);
		max.y = Mathf.Max(max.y, threshold);
		Brix.Game.User.LocalUser.GetComponent<Brix.Game.Network.UNetGroupe>().CallCmdSetLandscape(__instance.gameObject, min, max, threshold);
		return false;
	}
}
