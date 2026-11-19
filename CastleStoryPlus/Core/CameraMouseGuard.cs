using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Brix.Input;
using HarmonyLib;
using Rewired;
using UnityEngine;

namespace CastleStoryPlus.Core;

// At the start of a game the camera could scroll a long way to one side by itself. The camera (CameraScript) takes the
// mouse's frame delta straight from Rewired (ReInput.controllers.Mouse.GetAxis(0/1)) for drag-pan and drag-rotate, and
// right after a level load, a focus change or the cursor entering the window that delta can be a huge jump. The three
// GetAxis(int) calls in the camera are sent through Axis, which asks the pure rule (CameraMouseFilter): no movement
// while the window has no focus or the cursor is outside it, nothing until the cursor has really moved after a load or
// focus change, and a clamped delta. The keyboard and the scroll wheel are not touched.
[Feature(Features.CameraMouseGuard, Features.CameraMouseGuardInfo)]
[HarmonyPatch]
internal static class CameraMouseGuard
{
	private static readonly CameraMouseFilter Filter = new CameraMouseFilter();

	private static int _tickFrame = -1;

	private static IEnumerable<MethodBase> TargetMethods()
	{
		yield return AccessTools.Method(typeof(CameraScript), "GetMovementVectorsFromInputs");
		yield return AccessTools.Method(typeof(CameraScript), nameof(CameraScript.UpdateCameraFPS));
	}

	private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
	{
		MethodInfo axis = AccessTools.Method(typeof(CameraMouseGuard), nameof(Axis));
		foreach (CodeInstruction instruction in instructions)
		{
			if (instruction.opcode == OpCodes.Callvirt && instruction.operand is MethodInfo method && method.Name == "GetAxis"
				&& typeof(ControllerWithAxes).IsAssignableFrom(method.DeclaringType)
				&& method.GetParameters().Length == 1 && method.GetParameters()[0].ParameterType == typeof(int))
			{
				yield return new CodeInstruction(OpCodes.Call, axis).WithLabels(instruction.labels);
			}
			else
			{
				yield return instruction;
			}
		}
	}

	// Every level has a new camera; its Start forgets the mouse.
	[HarmonyPatch(typeof(CameraScript), "Start")]
	[HarmonyPrefix]
	private static void OnCameraStart()
	{
		Filter.Reset();
		_tickFrame = -1;
	}

	private static float Axis(ControllerWithAxes controller, int index)
	{
		float raw = controller.GetAxis(index);
		int frame = Time.frameCount;
		if (frame != _tickFrame)
		{
			_tickFrame = frame;
			Vector3 mouse = Input.mousePosition;
			Filter.Tick(Application.isFocused, Cursor.lockState != CursorLockMode.None, mouse.x, mouse.y, Screen.width, Screen.height);
		}
		return Filter.Delta(raw);
	}
}
