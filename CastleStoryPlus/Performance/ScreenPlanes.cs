using Brix.Game;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.Performance;

// A ScreenSignaler (on beacons and other things that want to know whether they are on the screen, some 40 in a base)
// tests its renderers against the camera's frustum every frame, and first worked the frustum's six planes out again
// for each of them: the camera's projection and view matrices read and multiplied (ScreenSignaler.GetPlane) 40 times
// a frame for one camera. The planes are kept in one shared array, so they are worked out once per frame and camera
// and the others use them as they are.
[Feature(Features.FrameSavers, Features.FrameSaversInfo)]
[HarmonyPatch(typeof(ScreenSignaler), "GetPlane")]
internal static class ScreenPlanes
{
	private static int _frame = -1;

	private static int _camera;

	private static bool Prefix(Camera cam)
	{
		if (cam == null)
		{
			return true;
		}
		int frame = Time.frameCount;
		int camera = cam.InstanceId();
		if (frame == _frame && camera == _camera)
		{
			return false;
		}
		_frame = frame;
		_camera = camera;
		return true;
	}
}
