using System.Collections.Generic;
using System.Reflection;
using Brix.Input;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.Performance;

// Every frame the mouse picking (Picking.Update) lets each picker of the current input mode cast the mouse's ray into
// the world to see what it points at: the pie menu pickers (units, objects, ground, blueprints, beacons, catapult
// targets), the cursor and drag pickers and the action pickers, each its own Physics.Raycast up to 1000 units along
// the same ray, many with the same layers. While the pickers are chosen nothing in the world moves, so a raycast with
// the same ray, length and layers as one before it in that pass gets the same hit and is not cast again. A picker
// that takes up a held object (which can put a ghost in the world) starts the sharing over.
[Feature(Features.FrameSavers, Features.FrameSaversInfo)]
[HarmonyPatch(typeof(Picking), "SelectActivePickers")]
internal static class PickingRaycasts
{
	private struct Cast
	{
		public Ray Ray;

		public float Distance;

		public int Mask;

		public QueryTriggerInteraction Triggers;

		public bool Hit;

		public RaycastHit Result;
	}

	private const int MostCasts = 16;

	private static readonly List<Cast> Casts = new List<Cast>(MostCasts);

	private static bool _sharing;

	private static void Prefix()
	{
		Casts.Clear();
		_sharing = true;
	}

	private static void Finalizer()
	{
		_sharing = false;
		Casts.Clear();
	}

	internal static void Forget()
	{
		Casts.Clear();
	}

	// Takes the place of Physics.Raycast(ray, out hit, distance, mask, triggers) in VoxelRaycastHit.PlaceAtRaycast.
	internal static bool Raycast(Ray ray, out RaycastHit hit, float distance, int mask, QueryTriggerInteraction triggers)
	{
		if (!_sharing)
		{
			return Physics.Raycast(ray, out hit, distance, mask, triggers);
		}
		for (int i = 0; i < Casts.Count; i++)
		{
			Cast cast = Casts[i];
			if (cast.Mask == mask && cast.Distance == distance && cast.Triggers == triggers && Same(cast.Ray.origin, ray.origin) && Same(cast.Ray.direction, ray.direction))
			{
				hit = cast.Result;
				return cast.Hit;
			}
		}
		bool result = Physics.Raycast(ray, out hit, distance, mask, triggers);
		if (Casts.Count < MostCasts)
		{
			Casts.Add(new Cast { Ray = ray, Distance = distance, Mask = mask, Triggers = triggers, Hit = result, Result = hit });
		}
		return result;
	}

	// Exactly the same (Unity's == lets vectors a hundred thousandth apart pass).
	private static bool Same(Vector3 a, Vector3 b)
	{
		return a.x == b.x && a.y == b.y && a.z == b.z;
	}
}

[Feature(Features.FrameSavers, Features.FrameSaversInfo)]
[HarmonyPatch(typeof(VoxelRaycastHit), nameof(VoxelRaycastHit.PlaceAtRaycast))]
internal static class PickingRaycastsCastPatch
{
	private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
	{
		MethodInfo raycast = AccessTools.Method(typeof(Physics), nameof(Physics.Raycast), new[] { typeof(Ray), typeof(RaycastHit).MakeByRefType(), typeof(float), typeof(int), typeof(QueryTriggerInteraction) });
		MethodInfo shared = AccessTools.Method(typeof(PickingRaycasts), nameof(PickingRaycasts.Raycast));
		int replaced = 0;
		foreach (CodeInstruction instruction in instructions)
		{
			if (raycast != null && instruction.Calls(raycast))
			{
				instruction.opcode = System.Reflection.Emit.OpCodes.Call;
				instruction.operand = shared;
				replaced++;
			}
			yield return instruction;
		}
		if (replaced != 1)
		{
			Plugin.Log.LogWarning("PickingRaycasts: found " + replaced + " raycasts in VoxelRaycastHit.PlaceAtRaycast instead of 1");
		}
	}
}

// Taking up a held object can put something new in the world: later pickers cast their own rays again.
[Feature(Features.FrameSavers, Features.FrameSaversInfo)]
[HarmonyPatch(typeof(Picker), nameof(Picker.PlaceAtRaycast))]
internal static class PickingRaycastsHoldPatch
{
	private static void Prefix(Picker __instance, out bool __state)
	{
		__state = __instance.HeldKey != null;
	}

	private static void Postfix(bool __state)
	{
		if (__state)
		{
			PickingRaycasts.Forget();
		}
	}
}
