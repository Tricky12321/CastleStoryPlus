using Brix.NewUI.OverheadDisplay;
using Brix.Util;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.Performance;

// Every overhead icon (over each stockpile, bricktron, beacon and building, some 300-400 in a big base) checks every
// frame whether the camera is close enough to show it (OverheadDisplay.CheckCloseEnough): it looked up Camera.main,
// its transform and position, and the screen's height, four calls into Unity per icon for what is the same for all of
// them in a frame. The camera's position and the distance limit are now read once per frame and each icon only reads
// its own pivot's position. The comparison is the game's own, and the icon is shown or hidden again only when the
// answer changed, as before. With no main camera (or no pivot) the game's own check runs.
[Feature(Features.FrameSavers, Features.FrameSaversInfo)]
[HarmonyPatch(typeof(OverheadDisplay), nameof(OverheadDisplay.CheckCloseEnough))]
internal static class OverheadDistance
{
	private static int _frame = -1;

	private static bool _haveCamera;

	private static Vector3 _cameraPosition;

	private static float _limit;

	private static bool Prefix(OverheadDisplay __instance)
	{
		Transform pivot = __instance.pivot;
		if (pivot == null || !CameraPosition(out Vector3 camera))
		{
			return true;
		}
		bool close = (camera - pivot.position).sqrMagnitude < _limit;
		if (close != __instance.IsCloseEnough)
		{
			__instance.IsCloseEnough = close;
			__instance.ShowOrHide();
		}
		return false;
	}

	// The main camera's position this frame (read once per frame). False when there is no main camera.
	internal static bool CameraPosition(out Vector3 position)
	{
		int frame = Time.frameCount;
		if (frame != _frame)
		{
			_frame = frame;
			Camera camera = Camera.main;
			_haveCamera = camera != null;
			if (_haveCamera)
			{
				_cameraPosition = camera.transform.position;
				_limit = Calcule.Sqr((float)Screen.height / 5f);
			}
		}
		position = _cameraPosition;
		return _haveCamera;
	}
}
