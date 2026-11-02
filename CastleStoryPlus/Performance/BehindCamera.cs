using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.Performance;

// Every overhead icon's BehindCameraDetector (some 330 in a big base) asks every frame whether its object is behind
// the camera: it looked up Camera.main, its transform, position and forward (four calls into Unity) for the same
// answer on all of them in a frame, then its own pivot's position. The camera's position and forward are now read
// once per frame; each detector only reads its pivot. The test is the same (an angle over 90 degrees is a negative
// dot product), the signals fire on the same changes. Without a main camera the game's own check runs.
[Feature(Features.FrameSavers, Features.FrameSaversInfo)]
[HarmonyPatch(typeof(BehindCameraDetector), "BrixUpdate")]
internal static class BehindCamera
{
	private static int _frame = -1;

	private static bool _haveCamera;

	private static Vector3 _position;

	private static Vector3 _forward;

	private static bool Prefix(BehindCameraDetector __instance)
	{
		int frame = Time.frameCount;
		if (frame != _frame)
		{
			_frame = frame;
			Camera camera = Camera.main;
			_haveCamera = camera != null;
			if (_haveCamera)
			{
				Transform transform = camera.transform;
				_position = transform.position;
				_forward = transform.forward;
			}
		}
		if (!_haveCamera)
		{
			return true;
		}
		Transform pivot = __instance.pivot;
		bool behind = pivot != null && Vector3.Dot(pivot.position - _position, _forward) < 0f;
		bool was = __instance.isBehind;
		__instance.wasBehind = was;
		__instance.isBehind = behind;
		if (!was && behind)
		{
			__instance.wentBehindCamera.Invoke();
		}
		else if (was && !behind)
		{
			__instance.cameBackIntoCamera.Invoke();
		}
		return false;
	}
}
