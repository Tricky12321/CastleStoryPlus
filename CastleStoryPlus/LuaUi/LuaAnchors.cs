using System.Collections.Generic;
using Brix.UI.Builder.Widget;
using CastleStoryPlus.Core;
using HarmonyLib;
using MoonSharp.Interpreter;
using UnityEngine;

namespace CastleStoryPlus.LuaUi;

// Lua tables that stand for a C# view's place on screen, for the game's Lua menus that follow a panel: the task
// popup and the task tooltip are told {targetPanel = <a Lua panel>} and keep themselves over that panel's
// transform. A C# view has no Lua panel, so it hands them one of these tables instead, and the patch below points
// the following at the C# view's transform. A table is made per place (a task's row, its crew...) and kept.
internal static class LuaAnchors
{
	private class ByReference : IEqualityComparer<Table>
	{
		public bool Equals(Table a, Table b)
		{
			return ReferenceEquals(a, b);
		}

		public int GetHashCode(Table table)
		{
			return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(table);
		}
	}

	private static readonly Dictionary<Table, Transform> Places = new Dictionary<Table, Transform>(new ByReference());

	// A new anchor table in the script, standing for the transform.
	public static Table Create(Script script, Transform place)
	{
		Table anchor = new Table(script);
		anchor.Set("csAnchor", DynValue.True);
		Places[anchor] = place;
		return anchor;
	}

	// The transform an anchor table stands for; null for any other table, or when its view is gone.
	public static Transform Find(Table anchor)
	{
		return (anchor != null && Places.TryGetValue(anchor, out Transform place) && place != null) ? place : null;
	}

	public static void Forget(Script script)
	{
		List<Table> gone = new List<Table>();
		foreach (KeyValuePair<Table, Transform> pair in Places)
		{
			if (pair.Value == null || pair.Key.OwnerScript == script)
			{
				gone.Add(pair.Key);
			}
		}
		foreach (Table anchor in gone)
		{
			Places.Remove(anchor);
		}
	}
}

// A Lua menu told to follow an anchor table follows the C# view's transform. The game only knows its own Lua
// panels there (LuiBuilder.MapHelper) and otherwise keeps what it followed before.
[Feature(Features.CsMenus, Features.CsMenusInfo)]
[HarmonyPatch(typeof(LayoutElementWidgetComponent), "SetPosTrackTarget")]
internal static class LuaAnchorTrackPatch
{
	private static bool Prepare()
	{
		return LuaUiConfig.CsTaskList;
	}

	private static void Postfix(LayoutElementWidgetComponent __instance, Table t)
	{
		if (t == null)
		{
			return;
		}
		DynValue target = t.RawGet("targetPanel");
		if (target.Type != DataType.Table)
		{
			return;
		}
		Transform place = LuaAnchors.Find(target.Table);
		if (place == null || place == __instance._posTrackTargetTrs)
		{
			return;
		}
		__instance.EndPosTracking();
		__instance._posTrackTargetTrs = place;
		__instance.BeginPosTracking();
	}
}
