using System.Collections.Generic;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.Performance;

// Every object that makes sound (some 300 in a base: each unit, workshop, door) tells Wwise where it is every frame
// (AkGameObj.Update). It read its transform's position, forward and up up to three times each to see whether it had
// moved: a dozen calls into Unity per object per frame. It now reads position and rotation once and works out forward
// and up from the rotation, which is what Unity does; Wwise gets the same values, only when they change, as before.
// Most of these objects never move (workshops, stockpiles, doors between uses). One that has stood still for
// StillFrames frames is looked at only every fourth frame (the objects spread over the frames), so a still object
// costs a quarter of the single read it cost before; when it moves again, Wwise hears of it at most three frames
// (some 50 ms) late and the object is back to every frame. Units walk all the time and stay at every frame.
[Feature(Features.FrameSavers, Features.FrameSaversInfo)]
[HarmonyPatch(typeof(AkGameObj), "Update")]
internal static class SoundObjectPositions
{
	private const int StillFrames = 8;

	// Frames between two looks at a still object (a power of two).
	private const int RestFrames = 4;

	// Frames each object has stood still, up to StillFrames.
	private static readonly Dictionary<int, int> Still = new Dictionary<int, int>();

	private static void Enable()
	{
		GameSession.OnLeave(Still.Clear);
	}

	private static bool Prefix(AkGameObj __instance)
	{
		int id = __instance.InstanceId();
		Still.TryGetValue(id, out int still);
		if (still >= StillFrames && ((Time.frameCount + id) & (RestFrames - 1)) != 0)
		{
			return false;
		}
		Transform transform = __instance.transform;
		Vector3 position;
		Quaternion rotation;
#if UNITY6
		transform.GetPositionAndRotation(out position, out rotation);
#else
		position = transform.position;
		rotation = transform.rotation;
#endif
		if (__instance.isEnvironmentAware && __instance.m_envData != null)
		{
			__instance.m_envData.UpdateAuxSend(__instance.gameObject, position);
		}
		AkGameObjPositionData data = __instance.m_posData;
		if (__instance.isStaticObject || data == null)
		{
			return false;
		}
		if (__instance.m_positionOffsetData != null)
		{
			position += rotation * __instance.m_positionOffsetData.positionOffset;
		}
		Vector3 forward = rotation * Vector3.forward;
		Vector3 up = rotation * Vector3.up;
		if (data.position == position && data.forward == forward && data.up == up)
		{
			if (still < StillFrames)
			{
				Still[id] = still + 1;
			}
			return false;
		}
		if (still != 0)
		{
			Still[id] = 0;
		}
		data.position = position;
		data.forward = forward;
		data.up = up;
		AkSoundEngine.SetObjectPosition(__instance.gameObject, position.x, position.y, position.z, forward.x, forward.y, forward.z, up.x, up.y, up.z);
		return false;
	}
}
