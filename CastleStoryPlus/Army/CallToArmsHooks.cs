using Brix.Legacy;
using Brix.Lua;
using Brix.Game;
using CastleStoryPlus.Core;
using HarmonyLib;
using MoonSharp.Interpreter;
using MoonSharp.Interpreter.Interop;
using UnityEngine;

namespace CastleStoryPlus.Army;

// The call to arms button / hotkey (Lua UIGame.CallToArms) uses the quotas, roles and rally points.
[Feature(Features.CallToArms, Features.CallToArmsInfo)]
[HarmonyPatch(typeof(UIGameObserver), nameof(UIGameObserver.lua_CallToArms))]
internal static class CallToArmsLuaPatch
{
	private static void Enable()
	{
		// Settings button next to the call to arms button in the right-hand bar.
		LuaInjection.AddAction("CallToArmsSettings", CallToArmsPanel.Toggle);
		LuaInjection.AddPatch(Features.CallToArms, "LUI/Menus/GameMenu.lua", "_m.mh.calltoarms = h\nend\n", LuaInjection.Mode.InsertAfter, @"

---Call to Arms settings (Castle Story Plus: soldiers per class, rally points, worker roles)
do
local h = ButtonHandle.New()
h.Label = ||""Call to arms settings""
h.Icon = ||IconKeys._UI_Flag:Get64()
h.OnAction = ||CastleStoryPlus.CallToArmsSettings()
h.hasHotkey = false

_m.mg.right:AddChild(h)
_m.mh.calltoarmssettings = h
end
");
		LuaInjection.AddPatch(Features.CallToArms, "LUI/Menus/GameMenu.lua", ".AddMenuHandleToggle(_m.mh.calltoarms)\n", LuaInjection.Mode.InsertAfter, "\t\t.AddMenuHandleToggle(_m.mh.calltoarmssettings)\n");
	}

	private static bool Prefix(CallbackArguments args, ref DynValue __result)
	{
		GameObject go = args.AsGameObject(0, "CallToArms");
		CallToArms.Execute(go);
		__result = DynValue.Void;
		return false;
	}
}

// The legacy "Event_CallToArms" command does the same.
[Feature(Features.CallToArms, Features.CallToArmsInfo)]
[HarmonyPatch(typeof(Instructions), nameof(Instructions.Batire))]
internal static class CallToArmsInstructionPatch
{
	private static void Postfix()
	{
		Instructions.commendes["Event_CallToArms"] = () =>
		{
			Instructions.Try((SpawnerCristal v) =>
			{
				CallToArms.Execute(v.gameObject);
			}, Instructions.ForSubjects, Instructions.ForTarget);
		};
	}
}
