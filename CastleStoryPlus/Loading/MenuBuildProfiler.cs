using System;
using System.Collections.Generic;
using System.Diagnostics;
using Brix.UI.Builder.Menu;
using CastleStoryPlus.Core;
using HarmonyLib;
using MoonSharp.Interpreter;

namespace CastleStoryPlus.Loading;

// Where building a Lua menu (GameMenu takes most of a load) spends its time: reading its table, creating its
// panels, opening it, and every C# function its Lua calls, by name, with the garbage collections inside each.
// Times include what they call, so they overlap. Measurement only, active during a load.
[Feature(Features.FasterLoading, Features.FasterLoadingInfo)]
[HarmonyPatch(typeof(LuaMenu), nameof(LuaMenu.ScanMenuTable))]
internal static class MenuScanTiming
{
	private static void Prefix(out MenuBuildProfiler.Mark __state)
	{
		__state = MenuBuildProfiler.Begin();
	}

	private static void Postfix(MenuBuildProfiler.Mark __state)
	{
		MenuBuildProfiler.End("menu: read table (ScanMenuTable)", __state);
	}
}

[Feature(Features.FasterLoading, Features.FasterLoadingInfo)]
[HarmonyPatch(typeof(BuilderUtility), nameof(BuilderUtility.AssignTree), new Type[] { typeof(IMenu) })]
internal static class MenuAssignTreeTiming
{
	private static void Prefix(out MenuBuildProfiler.Mark __state)
	{
		__state = MenuBuildProfiler.Begin();
	}

	private static void Postfix(MenuBuildProfiler.Mark __state)
	{
		MenuBuildProfiler.End("menu: assign panel tree", __state);
	}
}

[Feature(Features.FasterLoading, Features.FasterLoadingInfo)]
[HarmonyPatch(typeof(LuaMenu), "AddPanels")]
internal static class MenuAddPanelsTiming
{
	private static void Prefix(out MenuBuildProfiler.Mark __state)
	{
		__state = MenuBuildProfiler.Begin();
	}

	private static void Postfix(MenuBuildProfiler.Mark __state)
	{
		MenuBuildProfiler.End("menu: add panels", __state);
	}
}

[Feature(Features.FasterLoading, Features.FasterLoadingInfo)]
[HarmonyPatch(typeof(BaseMenu), nameof(BaseMenu.Open))]
internal static class MenuOpenTiming
{
	private static void Prefix(out MenuBuildProfiler.Mark __state)
	{
		__state = MenuBuildProfiler.Begin();
	}

	private static void Postfix(MenuBuildProfiler.Mark __state)
	{
		MenuBuildProfiler.End("menu: open", __state);
	}
}

// The steps of opening a menu, to see which of them is slow.
[Feature(Features.FasterLoading, Features.FasterLoadingInfo)]
[HarmonyPatch]
internal static class MenuOpenStepTiming
{
	private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
	{
		yield return AccessTools.Method(typeof(BuilderUtility), nameof(BuilderUtility.InstantiateTree));
		yield return AccessTools.Method(typeof(BuilderUtility), nameof(BuilderUtility.ParentTree));
		yield return AccessTools.Method(typeof(BuilderUtility), nameof(BuilderUtility.ResetTree));
		yield return AccessTools.Method(typeof(BuilderUtility), nameof(BuilderUtility.QueueRefreshTree));
		yield return AccessTools.Method(typeof(BaseMenu), nameof(BaseMenu.Connect), new Type[] { typeof(BaseMenu.MenuState), typeof(bool) });
	}

	private static void Prefix(out MenuBuildProfiler.Mark __state)
	{
		__state = MenuBuildProfiler.Begin();
	}

	private static void Postfix(System.Reflection.MethodBase __originalMethod, MenuBuildProfiler.Mark __state)
	{
		MenuBuildProfiler.End("menu step: " + __originalMethod.Name, __state);
	}
}

// Every C# function called from Lua, by its name (or, for unnamed ones, the method behind it). Hundreds of
// thousands of calls while a map loads, so each costs as little as it can: its bucket name is made once per
// function and the measurement is kept in the call itself.
[Feature(Features.FasterLoading, Features.FasterLoadingInfo)]
[HarmonyPatch(typeof(CallbackFunction), nameof(CallbackFunction.Invoke))]
internal static class LuaCallbackTiming
{
	private static void Prefix(out MenuBuildProfiler.Mark __state)
	{
		__state = MenuBuildProfiler.Begin();
	}

	private static readonly Dictionary<CallbackFunction, string> Names = new Dictionary<CallbackFunction, string>();

	private static string Name(CallbackFunction callback)
	{
		if (Names.TryGetValue(callback, out string name))
		{
			return name;
		}
		name = callback.Name;
		if (string.IsNullOrEmpty(name) || name == "?")
		{
			System.Reflection.MethodInfo method = (callback.ClrCallback != null) ? callback.ClrCallback.Method : null;
			name = (method != null) ? ("? " + ((method.DeclaringType != null) ? method.DeclaringType.FullName + "." : string.Empty) + method.Name) : "?";
		}
		name = "lua -> C# " + name;
		// Kept small: the callbacks of a left game are not kept for long.
		if (Names.Count > 4096)
		{
			Names.Clear();
		}
		Names[callback] = name;
		return name;
	}

	private static void Postfix(CallbackFunction __instance, MenuBuildProfiler.Mark __state)
	{
		if (__state.Start != 0)
		{
			MenuBuildProfiler.End(Name(__instance), __state);
		}
	}
}

internal static class MenuBuildProfiler
{
	// When a measurement started (0: not measured) and the garbage collections by then.
	internal struct Mark
	{
		public long Start;

		public int Gcs;
	}

	internal static Mark Begin()
	{
		if (!LoadProfiler.Active)
		{
			return default(Mark);
		}
		return new Mark { Start = Stopwatch.GetTimestamp(), Gcs = GC.CollectionCount(0) };
	}

	internal static void End(string name, Mark mark)
	{
		if (mark.Start == 0 || !LoadProfiler.Active)
		{
			return;
		}
		LoadProfiler.Add(name, Stopwatch.GetTimestamp() - mark.Start, GC.CollectionCount(0) - mark.Gcs);
	}
}
