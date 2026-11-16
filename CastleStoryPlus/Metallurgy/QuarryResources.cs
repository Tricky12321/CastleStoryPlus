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
// A setting of 0 keeps nothing, as in the game itself (and in a project of an older save where everything was switched
// off): coal and blue crystal are kept only when one of the game's three is, or with the ExtrasKept flag, which the
// buttons set when coal or blue crystal is kept without any of the three (QuarryResourceTogglePatch). Before, 0 kept
// coal and blue crystal, and a quarry set to keep nothing waited for storage for its coal for ever.
internal static class QuarryResources
{
	internal const int CoalOff = 8;

	internal const int BlueOff = 16;

	// Coal or blue crystal is kept while none of stone, orange crystal and iron is.
	internal const int ExtrasKept = 32;

	private const int GameKept = (int)GroundResource.All;

	internal static bool Keeps(GroundResource types, int offFlag)
	{
		return ((int)types & offFlag) == 0 && ((int)types & (GameKept | ExtrasKept)) != 0;
	}

	// The setting after a button toggled the flag, from what it was before and what the game's toggle made of it:
	// from keeping nothing, the coal or blue crystal button keeps that one alone; a setting that would keep coal or blue
	// crystal without any of the game's three gets ExtrasKept, so it does not read as "nothing" (0).
	internal static int AfterToggle(int before, int flag, int after)
	{
		if ((before & (GameKept | ExtrasKept)) == 0 && (flag == CoalOff || flag == BlueOff))
		{
			return ExtrasKept | ((CoalOff | BlueOff) & ~flag);
		}
		if ((after & (GameKept | ExtrasKept)) == 0 && (after & (CoalOff | BlueOff)) != (CoalOff | BlueOff))
		{
			return after | ExtrasKept;
		}
		return after;
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
			+ "h.Highlight = function() if " + selected + " and " + LuaKeeps(flag) + " then return HighlightMode.All end return HighlightMode.None end\n"
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

	// Keeps() in the Lua menu: the off flag clear and one of the game's three or ExtrasKept set.
	private static string LuaKeeps(int flag)
	{
		string p = "Data.Project:GetSelected()";
		return "not " + p + ":GetResourceTypeFlag(" + flag + ") and (" + p + ":GetResourceTypeFlag(1) or " + p + ":GetResourceTypeFlag(2) or " + p + ":GetResourceTypeFlag(4) or " + p + ":GetResourceTypeFlag(" + ExtrasKept + "))";
	}

	internal static void AddMenuPatches(string owner, bool coal, bool blue)
	{
		GameUi.GameMenuItems.Extend((GameUi.GameMenuModel menu) =>
		{
			AddToMenu(menu.Popup.Find("quarry"), "quarry", tunnel: false, coal, blue);
			AddToMenu(menu.Popup.Find("tunnel"), "tunnel", tunnel: true, coal, blue);
		});
		LuaInjection.AddPatch(owner, "LUI/Menus/GameMenu.lua", "_m.mh.quarryKeepBrimstone = h\nend\n", LuaInjection.Mode.InsertAfter, Buttons("Quarry", "quarry", coal, blue));
		LuaInjection.AddPatch(owner, "LUI/Menus/GameMenu.lua", "_m.mh.tunnelKeepBrimstone = h\nend\n", LuaInjection.Mode.InsertAfter, Buttons("Tunnel", "tunnel", coal, blue));
	}

	// The C# in-game menu's buttons: after the orange crystal button (coal first, then blue crystal).
	private static void AddToMenu(GameUi.UiSection section, string prefix, bool tunnel, bool coal, bool blue)
	{
		if (coal)
		{
			GameUi.UiButton button = Button(prefix + "KeepCoal", "Keep coal", () => Metallurgy.SpriteFor("coal"), CoalOff, tunnel);
			section.Insert(prefix + "KeepBrimstone", button);
		}
		if (blue)
		{
			GameUi.UiButton button = Button(prefix + "KeepBlueCrystal", "Keep blue crystal", () => Brix.UI.Icons.IconKeys._Raw_Blue_Crystal.Get64(), BlueOff, tunnel);
			bool afterCoal = section.Items.Exists((GameUi.UiItem i) => i.Id == prefix + "KeepCoal");
			section.Insert(afterCoal ? prefix + "KeepCoal" : prefix + "KeepBrimstone", button);
		}
	}

	private static GameUi.UiButton Button(string id, string label, System.Func<UnityEngine.Sprite> icon, int flag, bool tunnel)
	{
		GameUi.UiButton button = new GameUi.UiButton
		{
			Id = id,
			Label = () => label,
			Icon = icon,
			IconColor = () => LuaUi.LuiStyle.White,
			Highlight = () =>
			{
				object project = GameUi.GameUiState.Selected;
				return (tunnel ? project is TunnelProject : project is MineProject) && Keeps(TypesOf(project), flag);
			},
			OnClick = (bool shift, bool ctrl) =>
			{
				Brix.Game.AI.Project project = GameUi.GameUiState.Selected;
				if (GameUi.GameUiState.Paused || project == null || Brix.Game.User.LocalUser == null)
				{
					return;
				}
				Brix.Game.Network.UNetProjectCmd cmd = Brix.Game.User.LocalUser.GetComponent<Brix.Game.Network.UNetProjectCmd>();
				if (tunnel && project is TunnelProject)
				{
					cmd.CallCmdTunnelToggleResource(project.gameObject, flag);
				}
				else if (!tunnel && project is MineProject)
				{
					cmd.CallCmdQuarryToggleResource(project.gameObject, flag);
				}
			}
		};
		if (!tunnel && Economy.QuarryLimit.IsOn())
		{
			Economy.QuarryLimit.Attach(button, flag);
		}
		return button;
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

// A quarry's or tunnel's resource button on the host (the game's Toggle flips one flag): see QuarryResources.AfterToggle.
[Feature]
[HarmonyPatch(typeof(QuarryData), nameof(QuarryData.Toggle))]
internal static class QuarryResourceTogglePatch
{
	private static void Prefix(QuarryData __instance, out int __state)
	{
		__state = (int)__instance.TypesToHarvest;
	}

	private static void Postfix(QuarryData __instance, GroundResource type, int __state)
	{
		int after = (int)__instance.TypesToHarvest;
		int fixedValue = QuarryResources.AfterToggle(__state, (int)type, after);
		if (fixedValue != after)
		{
			__instance.SetTypesToHarvest(fixedValue);
		}
	}
}

[Feature]
[HarmonyPatch(typeof(TunnelData), nameof(TunnelData.Toggle))]
internal static class TunnelResourceTogglePatch
{
	private static void Prefix(TunnelData __instance, out int __state)
	{
		__state = (int)__instance.TypesToHarvest;
	}

	private static void Postfix(TunnelData __instance, GroundResource type, int __state)
	{
		int after = (int)__instance.TypesToHarvest;
		int fixedValue = QuarryResources.AfterToggle(__state, (int)type, after);
		if (fixedValue != after)
		{
			__instance.Set(fixedValue);
		}
	}
}
