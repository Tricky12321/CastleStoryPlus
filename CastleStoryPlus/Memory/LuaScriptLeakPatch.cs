using System;
using Brix.Lua;
using Brix.UI.Builder.Lui;
using Brix.UI.Builder.Menu.Component;
using CastleStoryPlus.Core;
using HarmonyLib;
using MoonSharp.Interpreter;

namespace CastleStoryPlus.Memory;

// A Lua menu component that loads its menu file owns the Lua script it runs in. When the component is destroyed
// with its scene, Release only forgot the script; it was never killed, so it stayed alive with all its tables,
// closures and C# panels (the static widget maps only drop entries of dead scripts), and its hooks on
// session-wide signals (update, slow update) kept running. Only menus unloaded from Lua were closed properly, so
// every menu of the title screen, lobby and options leaked on each trip into a game and back.
// Here the script is closed the way Unload does it: hooks dropped, widget maps cleared, script killed.
[Feature(Features.GameLeakFixes, Features.GameLeakFixesInfo)]
[HarmonyPatch(typeof(LuaMenuComponent), "Release")]
internal static class LuaMenuScriptPatch
{
	private struct Owned
	{
		public Script Script;

		public bool IsOwner;
	}

	private static int _closed;

	private static void Prefix(LuaMenuComponent __instance, out Owned __state)
	{
		__state = new Owned
		{
			Script = __instance._ownerScript,
			IsOwner = __instance._isOwnerScriptOwner
		};
	}

	// After the menu itself is closed and unloaded (base.Release), so its close handlers still run in a live script.
	private static void Postfix(LuaMenuComponent __instance, Owned __state)
	{
		Script script = __state.Script;
		if (script == null || !__state.IsOwner || !script.IsAlive)
		{
			return;
		}
		try
		{
			script.OnScriptKilled -= __instance._OnScriptKilled;
			LuaRegistry.DishookScript(script);
			LuiBuilder.MapHelper.RemoveAllAtScript(script);
			Script.Kill(ref script);
			_closed++;
			Plugin.Log.LogInfo("GameLeakFixes: closed the Lua script of menu '" + __instance.Path + "' (" + _closed + " this session)");
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning("GameLeakFixes: could not close the Lua script of menu '" + __instance.Path + "': " + ex);
		}
	}
}
