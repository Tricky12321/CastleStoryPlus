using System.Collections.Generic;
using Brix.Lua;
using Brix.UI.Builder.Menu;
using Brix.UI.Builder.Panel;
using CastleStoryPlus.Core;
using HarmonyLib;
using MoonSharp.Interpreter;
using UnityEngine.UI;

namespace CastleStoryPlus.Menus;

// A new Invasion world can have its enemy waves come every 5, 10 (default), 15, 20 or 30 minutes: a "Waves every"
// choice under the difficulty on the new game screen. It sets the game's own wave time (sv_Settings.waveDuration),
// which the game saves with the world, so a loaded world keeps it; the first wave still comes the difficulty's
// head start later. Only the host chooses; the wave timer runs on the host.
[Feature(Features.WaveInterval, Features.WaveIntervalInfo)]
[HarmonyPatch(typeof(BaseMapStarterMenu), "Footer", MethodType.Getter)]
internal static class WaveInterval
{
	internal static readonly int[] Minutes = new int[5] { 5, 10, 15, 20, 30 };

	internal const int DefaultMinutes = 10;

	internal static int Chosen = DefaultMinutes;

	private const string Invasion = "invasion";

	private static void Enable()
	{
		LuaInjection.AddFunction("WaveIntervalMinutes", (ScriptExecutionContext context, CallbackArguments args) => DynValue.NewNumber(Chosen));
		// A new world only (sv_OnNewLevelLoaded), on the host, before the first wave's timer is made.
		LuaInjection.AddPatch(Features.WaveInterval, "Gamemodes/invasion/serverside.lua", "SetSaveData(\"invasion\", \"doneInit\", true)", LuaInjection.Mode.InsertBefore,
			"local wavesEvery = CastleStoryPlus.WaveIntervalMinutes() if wavesEvery ~= nil and wavesEvery > 0 then sv_Settings.waveDuration = wavesEvery * 60 end\n  ");
	}

	// After the difficulty (map, mode, difficulty, ...).
	private static void Postfix(BaseMapStarterMenu __instance, ref PanelList __result)
	{
		if (__result == null)
		{
			return;
		}
		BaseMapStarterMenu menu = __instance;
		System.Action<DropdownPanelComponent> refresh = (DropdownPanelComponent panel) => Refresh(menu, panel);
		DropdownPanel dropdown = DropdownPanel.New(refresh, refresh).As(menu.IsReskin ? DropdownPanel.Variant.StandardBigReskin : DropdownPanel.Variant.StandardBig);
		__result.Insert(System.Math.Min(3, __result.Count), dropdown);
	}

	private static bool Shown(BaseMapStarterMenu menu)
	{
		return menu.DisplayedMap != null && menu.DisplayedMode != null && menu.DisplayedMode.codename == Invasion
			&& menu.IsNewGame && !menu.IsExistingAsset && !menu.IsWorldEditorMenu && !menu.IsCreditIslandMenu && menu.IsNotMultiplayerOrIsHost;
	}

	private static void Refresh(BaseMapStarterMenu menu, DropdownPanelComponent panel)
	{
		if (!Shown(menu))
		{
			panel.gameObject.SetActive(false);
			return;
		}
		panel.gameObject.SetActive(true);
		panel._keyLabel.text = "Waves every";
		panel._keyLabel.color = BaseMenu.Colors.Black;
		panel._valueLabel.color = BaseMenu.Colors.Black;
		panel._arrowImage.color = BaseMenu.Colors.Black;
		panel._arrowImage.gameObject.SetActive(true);
		panel._dropdown.interactable = true;
		panel._dropdown.onValueChanged.RemoveAllListeners();
		panel._dropdown.options = new List<Dropdown.OptionData>();
		foreach (int minutes in Minutes)
		{
			panel._dropdown.options.Add(new Dropdown.OptionData(Label(minutes)));
		}
		int index = System.Array.IndexOf(Minutes, Chosen);
		panel._dropdown.value = (index >= 0) ? index : System.Array.IndexOf(Minutes, DefaultMinutes);
		panel._valueLabel.text = Label(Chosen);
		panel._dropdown.onValueChanged.AddListener((int chosen) =>
		{
			Chosen = Minutes[chosen];
			panel._valueLabel.text = Label(Chosen);
			MenuSound.PlaySound(menu.SoundPrefix, MenuSound.Suffix.Select);
		});
	}

	private static string Label(int minutes)
	{
		return minutes + " MIN";
	}
}
