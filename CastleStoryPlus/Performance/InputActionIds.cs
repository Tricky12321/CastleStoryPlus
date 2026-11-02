using System;
using System.Collections.Generic;
using CastleStoryPlus.Core;
using HarmonyLib;
using Rewired;

namespace CastleStoryPlus.Performance;

// The game asks its input library (Rewired) for its actions by name, every frame and from many places: the camera's
// keys and mouse buttons, the picking, the input mode controller, the UI (GetButton("cam_action1") and the like, some
// 60 call sites). Each such call looks the name up with a case-insensitive string hash before it reaches the action,
// about 2 % of the main thread in a big base. The name is now turned into the action's id once (Rewired's own
// mapping) and the id overload is called, which is what Rewired does after the lookup. A name Rewired does not know
// goes the old way, so its warning is the same.
[Feature(Features.FrameSavers, Features.FrameSaversInfo)]
[HarmonyPatch]
internal static class InputActionIds
{
	private static readonly Dictionary<string, int> Ids = new Dictionary<string, int>(StringComparer.Ordinal);

	// The action's id, or -1 when Rewired has no such action (or is not ready yet: asked again next time).
	private static int IdOf(string name)
	{
		if (name == null)
		{
			return -1;
		}
		if (Ids.TryGetValue(name, out int id))
		{
			return id;
		}
		if (!ReInput.isReady)
		{
			return -1;
		}
		id = ReInput.mapping.GetActionId(name);
		Ids[name] = id;
		return id;
	}

	[HarmonyPatch(typeof(Player), nameof(Player.GetButton), new[] { typeof(string) })]
	[HarmonyPrefix]
	private static bool Button(Player __instance, string actionName, ref bool __result)
	{
		int id = IdOf(actionName);
		if (id < 0)
		{
			return true;
		}
		__result = __instance.GetButton(id);
		return false;
	}

	[HarmonyPatch(typeof(Player), nameof(Player.GetButtonDown), new[] { typeof(string) })]
	[HarmonyPrefix]
	private static bool ButtonDown(Player __instance, string actionName, ref bool __result)
	{
		int id = IdOf(actionName);
		if (id < 0)
		{
			return true;
		}
		__result = __instance.GetButtonDown(id);
		return false;
	}

	[HarmonyPatch(typeof(Player), nameof(Player.GetButtonUp), new[] { typeof(string) })]
	[HarmonyPrefix]
	private static bool ButtonUp(Player __instance, string actionName, ref bool __result)
	{
		int id = IdOf(actionName);
		if (id < 0)
		{
			return true;
		}
		__result = __instance.GetButtonUp(id);
		return false;
	}

	[HarmonyPatch(typeof(Player), nameof(Player.GetNegativeButton), new[] { typeof(string) })]
	[HarmonyPrefix]
	private static bool NegativeButton(Player __instance, string actionName, ref bool __result)
	{
		int id = IdOf(actionName);
		if (id < 0)
		{
			return true;
		}
		__result = __instance.GetNegativeButton(id);
		return false;
	}

	[HarmonyPatch(typeof(Player), nameof(Player.GetNegativeButtonDown), new[] { typeof(string) })]
	[HarmonyPrefix]
	private static bool NegativeButtonDown(Player __instance, string actionName, ref bool __result)
	{
		int id = IdOf(actionName);
		if (id < 0)
		{
			return true;
		}
		__result = __instance.GetNegativeButtonDown(id);
		return false;
	}

	[HarmonyPatch(typeof(Player), nameof(Player.GetButtonTimePressed), new[] { typeof(string) })]
	[HarmonyPrefix]
	private static bool ButtonTimePressed(Player __instance, string actionName, ref float __result)
	{
		int id = IdOf(actionName);
		if (id < 0)
		{
			return true;
		}
		__result = __instance.GetButtonTimePressed(id);
		return false;
	}

	[HarmonyPatch(typeof(Player), nameof(Player.GetAxis), new[] { typeof(string) })]
	[HarmonyPrefix]
	private static bool Axis(Player __instance, string actionName, ref float __result)
	{
		int id = IdOf(actionName);
		if (id < 0)
		{
			return true;
		}
		__result = __instance.GetAxis(id);
		return false;
	}
}
