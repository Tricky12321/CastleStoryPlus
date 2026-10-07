using System;
using System.Collections;
using System.Diagnostics;
using Brix.Lua;
using Brix.UI.Builder.Menu.Component;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.Loading;

// Lua menus built as coroutines (the game UI) were resumed once per frame, and their scripts yield after every
// panel or help article, so building the UI took hundreds of frames while loading a save, and the loading
// screen waits for all of them. The yields only spread the work out, they wait for nothing, so each menu is now
// resumed until it is done or the frame's shared time budget is spent. Every menu still advances at least once
// per frame, as before.
[Feature(Features.FasterLoading, Features.FasterLoadingInfo)]
[HarmonyPatch(typeof(LuaMenuComponent), nameof(LuaMenuComponent.DoCoroutine))]
internal static class LuaMenuBatchPatch
{
	private const long BudgetMilliseconds = 40;

	private static readonly WaitForEndOfFrame EndOfFrame = new WaitForEndOfFrame();

	private static readonly Stopwatch FrameClock = new Stopwatch();

	private static int _frame = -1;

	// Totals for the load timing log.
	public static int Resumes;

	public static long Ticks;

	private static bool Prefix(LuaMenuComponent __instance, ref IEnumerator __result)
	{
		__result = DoCoroutine(__instance);
		return false;
	}

	private static IEnumerator DoCoroutine(LuaMenuComponent menu)
	{
		LuaMenuComponent.CoroutineCount++;
		try
		{
			while (!ResumeThisFrame(menu))
			{
				yield return EndOfFrame;
			}
		}
		finally
		{
			LuaMenuComponent.CoroutineCount--;
			menu._isStop = null;
		}
	}

	// True when the menu has finished loading.
	private static bool ResumeThisFrame(LuaMenuComponent menu)
	{
		if (_frame != Time.frameCount)
		{
			_frame = Time.frameCount;
			FrameClock.Reset();
			FrameClock.Start();
		}
		do
		{
			if (menu._isStop == null || Resume(menu))
			{
				return true;
			}
		}
		while (FrameClock.ElapsedMilliseconds < BudgetMilliseconds);
		return false;
	}

	private static bool Resume(LuaMenuComponent menu)
	{
		long start = Stopwatch.GetTimestamp();
		LoadingStatus.CurrentMenu = menu.Path;
		bool done = true;
		try
		{
			done = menu._isStop(menu._tMenu, menu);
			return done;
		}
		catch (Exception e)
		{
			// The game's catcher does not say which menu stopped; the rest of that menu is never built.
			Plugin.Log.LogError("Lua menu " + menu.Path + " stopped: " + e.GetType().Name + ": " + e.Message + LuaWhere(menu));
			LuaLibs.LuaExceptionCatcher(e);
			return true;
		}
		finally
		{
			long ticks = Stopwatch.GetTimestamp() - start;
			Resumes++;
			Ticks += ticks;
			if (LoadProfiler.Active)
			{
				LoadProfiler.Add("lua menu " + menu.Path, ticks);
				// Each step of the menu's build (the code up to its next yield), by where it yielded.
				if (!done)
				{
					LoadProfiler.Add("lua menu step " + YieldedAt(menu), ticks);
				}
			}
		}
	}

	// The Lua file, line and function where the menu's coroutine is waiting.
	private static string YieldedAt(LuaMenuComponent menu)
	{
		try
		{
			MoonSharp.Interpreter.Coroutine coroutine = menu._tMenu.Tuple[1].Coroutine;
			foreach (MoonSharp.Interpreter.Debugging.WatchItem item in coroutine.GetStackTrace(0))
			{
				MoonSharp.Interpreter.Debugging.SourceRef location = item.Location;
				if (location != null)
				{
					return coroutine.OwnerScript.GetSourceCode(location.SourceIdx).Name + ":" + location.FromLine + " " + (item.Name ?? "?");
				}
			}
		}
		catch (Exception)
		{
		}
		return menu.Path + " ?";
	}

	// Where the menu's Lua coroutine was when it stopped, from its call stack.
	private static string LuaWhere(LuaMenuComponent menu)
	{
		try
		{
			MoonSharp.Interpreter.Coroutine coroutine = menu._tMenu.Tuple[1].Coroutine;
			System.Text.StringBuilder text = new System.Text.StringBuilder();
			foreach (MoonSharp.Interpreter.Debugging.WatchItem item in coroutine.GetStackTrace(0))
			{
				MoonSharp.Interpreter.Debugging.SourceRef location = item.Location;
				string source = (location != null) ? coroutine.OwnerScript.GetSourceCode(location.SourceIdx).Name : "?";
				text.Append("\n  at " + (item.Name ?? "?") + " " + source + ((location != null) ? ":" + location.FromLine : string.Empty));
			}
			return text.ToString();
		}
		catch (Exception ex)
		{
			return " (no Lua stack: " + ex.Message + ")";
		}
	}
}
