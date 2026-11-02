using System.Collections.Generic;
using Brix.Engine;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.Performance;

// The plants' level of detail: every frame the game goes over 100 of the map's megavoxels (Jardinier) and, for each
// plant in them, compares its distance to the camera with its detail steps (MegaVoxel.CalculateNatureLod), round and
// round over the whole map, about 0.7 % of the main thread with the camera standing still, when nothing can change.
// A megavoxel whose pass found the camera where it was at its last pass (within half a block) is now passed over,
// for at most RestSeconds, so a plant that grew or was planted meanwhile gets its detail within that time; with the
// camera moving every pass runs as before.
[Feature(Features.FrameSavers, Features.FrameSaversInfo)]
[HarmonyPatch(typeof(MegaVoxel), nameof(MegaVoxel.CalculateNatureLod))]
internal static class NatureLodRest
{
	private const float MovedSqr = 0.25f;

	private const float RestSeconds = 2f;

	private struct Pass
	{
		public Vector3 Camera;

		public float Time;
	}

	private static readonly Dictionary<MegaVoxel, Pass> Passes = new Dictionary<MegaVoxel, Pass>();

	private static int _frame = -1;

	private static bool _haveCamera;

	private static Vector3 _camera;

	private static void Enable()
	{
		GameSession.OnLeave(Passes.Clear);
	}

	private static bool Prefix(MegaVoxel __instance)
	{
		if (__instance._natureObjects == null || __instance._natureObjects.Count == 0)
		{
			return true;
		}
		int frame = Time.frameCount;
		if (frame != _frame)
		{
			_frame = frame;
			Camera camera = Camera.main;
			_haveCamera = camera != null;
			if (_haveCamera)
			{
				_camera = camera.transform.position;
			}
		}
		if (!_haveCamera)
		{
			return true;
		}
		float now = Time.time;
		if (Passes.TryGetValue(__instance, out Pass last) && (last.Camera - _camera).sqrMagnitude < MovedSqr && now - last.Time < RestSeconds)
		{
			return false;
		}
		Passes[__instance] = new Pass { Camera = _camera, Time = now };
		return true;
	}
}
