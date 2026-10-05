using System.Collections.Generic;
using System.Reflection;
using Brix.External.Factories;
using Brix.Game.AI;
using CastleStoryPlus.Core;
using HarmonyLib;

namespace CastleStoryPlus.Metallurgy;

// Mines (quarries) and tunnels choose which resources to keep: stone, orange crystal and iron, each a flag of the
// project's setting (anything not kept is broken without a drop). Coal and blue crystal get a button each. Their flags
// are "off" flags (set = not kept), so the game's default (stone, orange crystal and iron: 7) keeps them too, also in
// projects of older saves; the unmodded game ignores the extra flags.
internal static class QuarryResources
{
	internal const int CoalOff = 8;

	internal const int BlueOff = 16;

	internal static bool Keeps(GroundResource types, int offFlag)
	{
		return ((int)types & offFlag) == 0;
	}

	internal static GroundResource TypesOf(object project)
	{
		if (project is MineProject mine)
		{
			return mine.TypesToHarvest;
		}
		if (project is TunnelProject tunnel)
		{
			return tunnel.TypesToHarvest;
		}
		return GroundResource.None;
	}

	// The two buttons, after the orange crystal button of the quarry ("Quarry") or the tunnel ("Tunnel") menu.
	internal static string Buttons(string kind, string section, bool coal, bool blue)
	{
		string text = string.Empty;
		if (coal)
		{
			text += Button(kind, section, "Coal", CoalOff, "||CastleStoryPlus.ResourceIcon(\"coal\")");
		}
		if (blue)
		{
			text += Button(kind, section, "BlueCrystal", BlueOff, "||IconKeys._Raw_Blue_Crystal:Get64()");
		}
		return text;
	}

	private static string Button(string kind, string section, string name, int flag, string icon)
	{
		string selected = "Data.Project:IsSelectedOfType" + kind + "()";
		// Quarries: "-" and "+" for the resource's dig limit around the button (QuarryLimit).
		bool limits = kind == "Quarry" && Economy.QuarryLimit.IsOn();
		return (limits ? Economy.QuarryLimit.MinusButton(flag) : string.Empty) + "\n--" + section + ".options.Keep" + name + " (Castle Story Plus)\n"
			+ "do\n"
			+ "local h = MenuHandle.New()\n"
			+ "h.Visible = ||" + selected + "\n"
			+ "h.Icon = " + icon + "\n"
			+ "h.IconColor = ||White\n"
			+ "h.IconSize = ||64\n"
			+ "h.Label = ||\"Keep " + (name == "Coal" ? "coal" : "blue crystal") + "\"\n"
			+ "h.OnAction = function() if not UIGame.IsPaused() and " + selected + " then Data.Project:GetSelected():ToggleResourceTypeFlag(" + flag + ") end end\n"
			+ "h.Highlight = function() if " + selected + " and not Data.Project:GetSelected():GetResourceTypeFlag(" + flag + ") then return HighlightMode.All end return HighlightMode.None end\n"
			+ "h.TooltipPath = ||{\"Task\", \"Quarry\", \"KeepBrimstone\"}\n"
			+ "h.onRefreshHighlight = Event.New()\n"
			+ "\n"
			+ "_m.onResourceTypeKeptChanged:AddListener(||h.onRefreshHighlight:Invoke())\n"
			+ "_m.sh." + section + ".ev_onLoad:AddListener(function(m, sh) m.AddMenuHandleToggle(h, sh) end)\n"
			+ "\n"
			+ "_m.mg.projectContext:AddChild(h)\n"
			+ (limits ? Economy.QuarryLimit.CountAndPlus(flag) : string.Empty)
			+ "end\n";
	}

	internal static void AddMenuPatches(string owner, bool coal, bool blue)
	{
		LuaInjection.AddPatch(owner, "LUI/Menus/GameMenu.lua", "_m.mh.quarryKeepBrimstone = h\nend\n", LuaInjection.Mode.InsertAfter, Buttons("Quarry", "quarry", coal, blue));
		LuaInjection.AddPatch(owner, "LUI/Menus/GameMenu.lua", "_m.mh.tunnelKeepBrimstone = h\nend\n", LuaInjection.Mode.InsertAfter, Buttons("Tunnel", "tunnel", coal, blue));
	}

	internal static IEnumerable<MethodBase> StorageMethods()
	{
		yield return AccessTools.Method(typeof(MineProject), "ItemsRequiringStorage");
		yield return AccessTools.Method(typeof(TunnelProject), "ItemsRequiringStorage");
	}
}

[Feature(Features.Metallurgy, Features.MetallurgyInfo)]
internal static class QuarryCoal
{
	private static void Enable()
	{
		QuarryResources.AddMenuPatches(Features.Metallurgy, coal: true, blue: false);
	}
}

[Feature(Features.BlueCrystalDeposits, Features.BlueCrystalDepositsInfo)]
internal static class QuarryBlueCrystal
{
	private static void Enable()
	{
		QuarryResources.AddMenuPatches(Features.BlueCrystalDeposits, coal: false, blue: true);
	}
}

// A coal or blue crystal block of a mine or tunnel is broken without a drop when the project does not keep it.
[Feature(Features.Metallurgy, Features.MetallurgyInfo)]
[HarmonyPatch(typeof(DigGoal), "MustOverkill", new System.Type[] { typeof(Factory.AssetKey) })]
internal static class QuarryCoalOverkillPatch
{
	private static bool Prefix(DigGoal __instance, Factory.AssetKey key, ref bool __result)
	{
		if (key == null || key != Metallurgy.CoalKey)
		{
			return true;
		}
		__result = !QuarryResources.Keeps(__instance.ResourceSetting.TypesToHarvest, QuarryResources.CoalOff);
		return false;
	}
}

[Feature(Features.BlueCrystalDeposits, Features.BlueCrystalDepositsInfo)]
[HarmonyPatch(typeof(DigGoal), "MustOverkill", new System.Type[] { typeof(Factory.AssetKey) })]
internal static class QuarryBlueCrystalOverkillPatch
{
	private static bool Prefix(DigGoal __instance, Factory.AssetKey key, ref bool __result)
	{
		if (key == null || key != RawResources.RawBlueCrystal)
		{
			return true;
		}
		__result = !QuarryResources.Keeps(__instance.ResourceSetting.TypesToHarvest, QuarryResources.BlueOff);
		return false;
	}
}

// Kept coal and blue crystal are carried to storage.
[Feature(Features.Metallurgy, Features.MetallurgyInfo)]
[HarmonyPatch]
internal static class QuarryCoalStoragePatch
{
	private static IEnumerable<MethodBase> TargetMethods()
	{
		return QuarryResources.StorageMethods();
	}

	private static void Postfix(object __instance, HashSet<Factory.AssetKey> __result)
	{
		if (__result != null && QuarryResources.Keeps(QuarryResources.TypesOf(__instance), QuarryResources.CoalOff))
		{
			__result.Add(Metallurgy.CoalKey);
		}
	}
}

[Feature(Features.BlueCrystalDeposits, Features.BlueCrystalDepositsInfo)]
[HarmonyPatch]
internal static class QuarryBlueCrystalStoragePatch
{
	private static IEnumerable<MethodBase> TargetMethods()
	{
		return QuarryResources.StorageMethods();
	}

	private static void Postfix(object __instance, HashSet<Factory.AssetKey> __result)
	{
		if (__result != null && QuarryResources.Keeps(QuarryResources.TypesOf(__instance), QuarryResources.BlueOff))
		{
			__result.Add(RawResources.RawBlueCrystal);
		}
	}
}
