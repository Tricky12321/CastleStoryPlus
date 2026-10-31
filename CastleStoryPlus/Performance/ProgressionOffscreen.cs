using System.Collections.Generic;
using Brix.Game.Components;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.Performance;

// When a storage's content changes, its AnimatedProgression (the pallet's animator that drops the goods into place
// and raises the heap) is stepped by hand for a second: Animator.Update every frame, which works out the whole
// animator there and then on the main thread (about 0.4 ms a call in a big base; one or two storages are at it at
// any moment). A storage off the screen is not stepped while nothing can see it: the time is kept and given to the
// animator in one step when the storage comes on the screen, when its second is up, or when its content changes
// again, so it ends in the same state as before. A storage on the screen is stepped every frame as before.
[Feature(Features.FrameSavers, Features.FrameSaversInfo)]
[HarmonyPatch(typeof(AnimatedProgression), "Update")]
[HarmonyPriority(Priority.Last)]
internal static class ProgressionOffscreen
{
	// How far around the storage's position counts as on the screen (world units): the pallet, its heap and shadow.
	private const float Margin = 8f;

	private static readonly AccessTools.FieldRef<AnimatedProgression, Animator> AnimatorOf = AccessTools.FieldRefAccess<AnimatedProgression, Animator>("animator");

	private static readonly AccessTools.FieldRef<AnimatedProgression, bool> Dirty = AccessTools.FieldRefAccess<AnimatedProgression, bool>("_dirty");

	private static readonly AccessTools.FieldRef<AnimatedProgression, float> Wait = AccessTools.FieldRefAccess<AnimatedProgression, float>("_waitForTransition");

	// Time not yet given to the animator, per storage.
	internal static readonly Dictionary<AnimatedProgression, float> Pending = new Dictionary<AnimatedProgression, float>();

	private static void Enable()
	{
		GameSession.OnLeave(Pending.Clear);
	}

	private static bool Prefix(AnimatedProgression __instance, bool __runOriginal)
	{
		if (!__runOriginal)
		{
			return false;
		}
		Animator animator = AnimatorOf(__instance);
		if (animator == null)
		{
			return true;
		}
		Pending.TryGetValue(__instance, out float pending);
		if (Dirty(__instance))
		{
			// The game reads the animator and sets its next trigger: it first gets the time it is owed.
			Flush(__instance, animator, pending);
			return true;
		}
		float wait = Wait(__instance);
		if (!(wait > 0f) && !animator.IsInTransition(0))
		{
			if (pending > 0f)
			{
				// Owed time first; the game looks again next frame and stops it when it is done.
				Flush(__instance, animator, pending);
				return false;
			}
			Pending.Remove(__instance);
			return true;
		}
		if (pending == 0f && OnScreen(__instance.transform.position))
		{
			return true;
		}
		float dt = Time.deltaTime;
		pending += dt;
		wait -= dt;
		Wait(__instance) = wait;
		if (!(wait > 0f) || OnScreen(__instance.transform.position))
		{
			Flush(__instance, animator, pending);
		}
		else
		{
			Pending[__instance] = pending;
		}
		return false;
	}

	private static void Flush(AnimatedProgression progression, Animator animator, float pending)
	{
		Pending.Remove(progression);
		if (pending > 0f)
		{
			animator.Update(pending);
		}
	}

	private static int _frame = -1;

	private static Camera _camera;

	private static Matrix4x4 _view;

	private static float _tanY;

	private static float _tanX;

	private static float _far;

	// The main camera's view, worked out once a frame: a sphere around the position against the view's sides.
	private static bool OnScreen(Vector3 position)
	{
		if (Time.frameCount != _frame)
		{
			_frame = Time.frameCount;
			_camera = Camera.main;
			if (_camera != null && !_camera.orthographic)
			{
				_view = _camera.worldToCameraMatrix;
				_tanY = Mathf.Tan(_camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
				_tanX = _tanY * _camera.aspect;
				_far = _camera.farClipPlane;
			}
		}
		if (_camera == null || _camera.orthographic)
		{
			return true;
		}
		// Camera space looks down -z.
		Vector3 local = _view.MultiplyPoint3x4(position);
		float depth = -local.z;
		if (depth < -Margin || depth > _far + Margin)
		{
			return false;
		}
		// A side of the view at angle atan(tan) leans from the forward axis; the sphere is in when its centre is
		// within Margin of the inside of each side.
		float slackY = Margin * Mathf.Sqrt(1f + _tanY * _tanY);
		float slackX = Margin * Mathf.Sqrt(1f + _tanX * _tanX);
		return Mathf.Abs(local.y) <= depth * _tanY + slackY && Mathf.Abs(local.x) <= depth * _tanX + slackX;
	}
}
