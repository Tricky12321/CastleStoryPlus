using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Brix.Input;
using HarmonyLib;
using Rewired;
using UnityEngine;

namespace CastleStoryPlus.Core;

// At the start of a game the camera could scroll a long way to one side by itself. Two readers of the mouse can cause
// it, both fed by a cursor position/delta that is not trustworthy right after a level load, a focus change or the
// cursor entering the window (the position can still be (0,0)):
//  1. The game's edge nudge: Curseur.Update calls CheckToNudgeScreen every frame, which moves the camera while the
//     cursor is within 5 px of a window edge (CameraScript.CallMove). With the cursor at (0,0) that is a constant
//     scroll left and back. CheckToNudgeScreen is skipped until the cursor has really moved inside the window.
//  2. Drag-pan and drag-rotate read the mouse's frame delta from Rewired (GetAxis(0/1) in CameraScript), which can be
//     a huge jump then. Those calls are sent through Axis: no delta until armed, and a clamped delta.
// The rule is the pure CameraMouseFilter. Normal edge nudging and dragging are unchanged once the cursor has moved.
internal static class CameraMouseGuard
{
	private static readonly CameraMouseFilter Filter = new CameraMouseFilter();

	private static int _tickFrame = -1;

	internal static void Reset()
	{
		Filter.Reset();
		_tickFrame = -1;
	}

	// Once per frame (the first caller of the frame does the work).
	internal static CameraMouseFilter Tick()
	{
		int frame = Time.frameCount;
		if (frame != _tickFrame)
		{
			_tickFrame = frame;
			Vector3 mouse = Input.mousePosition;
			bool button = Input.GetMouseButton(0) || Input.GetMouseButton(1) || Input.GetMouseButton(2);
			Filter.Tick(Application.isFocused, Cursor.lockState != CursorLockMode.None, mouse.x, mouse.y, Screen.width, Screen.height, button);
		}
		return Filter;
	}

	internal static float Axis(ControllerWithAxes controller, int index)
	{
		float raw = controller.GetAxis(index);
		return Tick().Delta(raw);
	}
}

// The game's edge nudge only runs once the cursor has moved inside the window since the level loaded.
[Feature(Features.CameraMouseGuard, Features.CameraMouseGuardInfo)]
[HarmonyPatch(typeof(Curseur), "CheckToNudgeScreen")]
internal static class CameraMouseGuardNudge
{
	private static bool Prefix()
	{
		return CameraMouseGuard.Tick().Armed;
	}
}

[Feature(Features.CameraMouseGuard, Features.CameraMouseGuardInfo)]
[HarmonyPatch]
internal static class CameraMouseGuardDelta
{
	private static IEnumerable<MethodBase> TargetMethods()
	{
		yield return AccessTools.Method(typeof(CameraScript), "GetMovementVectorsFromInputs");
		yield return AccessTools.Method(typeof(CameraScript), nameof(CameraScript.UpdateCameraFPS));
	}

	private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
	{
		MethodInfo axis = AccessTools.Method(typeof(CameraMouseGuard), nameof(CameraMouseGuard.Axis));
		int replaced = 0;
		foreach (CodeInstruction instruction in instructions)
		{
			if (instruction.opcode == OpCodes.Callvirt && instruction.operand is MethodInfo method && method.Name == "GetAxis"
				&& typeof(ControllerWithAxes).IsAssignableFrom(method.DeclaringType)
				&& method.GetParameters().Length == 1 && method.GetParameters()[0].ParameterType == typeof(int))
			{
				replaced++;
				yield return new CodeInstruction(OpCodes.Call, axis).WithLabels(instruction.labels);
			}
			else
			{
				yield return instruction;
			}
		}
		if (replaced == 0)
		{
			Plugin.Log.LogWarning("CameraMouseGuard: no mouse GetAxis call found in " + original.Name + "; the mouse delta is not filtered.");
		}
	}
}

// Every level has a new camera; its Start forgets the mouse.
[Feature(Features.CameraMouseGuard, Features.CameraMouseGuardInfo)]
[HarmonyPatch(typeof(CameraScript), "Start")]
internal static class CameraMouseGuardReset
{
	private static void Prefix()
	{
		CameraMouseGuard.Reset();
	}
}
