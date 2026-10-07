using Brix.Components;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Semantique;
using BepInEx.Configuration;
using Brix.Lifecycle.Pooling;
using CastleStoryPlus.Core;
using MoonSharp.Interpreter;
using UnityEngine;

namespace CastleStoryPlus.UI;

// Mounts Lua/LUI/Panels/Gamemodes/Panel_StorageList.lua in the top-right corner, left of the right-hand
// icon bar, and removes the old resource icon grid under the minimap (the "build stockpiles" alert stays). A
// "Resources" button on the right-hand bar, under the settings, shows and hides it ([ResourceList] Shown).
[Feature(Features.ResourceList, Features.ResourceListInfo)]
internal static class ResourceList
{
	private static int _frame = -1;

	private static int _capacity;

	private static int _used;

	private static ConfigEntry<bool> _shown;

	private static void Enable()
	{
		_shown = Plugin.Cfg.Bind("ResourceList", "Shown", true, "Whether the resource list in the top-right corner is shown (the \"Resources\" button on the right-hand bar toggles it).");
		LuaInjection.AddFunction("ResourceListShown", (ScriptExecutionContext context, CallbackArguments args) =>
		{
			return DynValue.NewBoolean(_shown.Value);
		});
		LuaInjection.AddAction("ToggleResourceList", () =>
		{
			_shown.Value = !_shown.Value;
		});
		// Stockpile room of the local player, in the game's own encumbrance units (what a stockpile holds).
		LuaInjection.AddFunction("StorageCapacity", (ScriptExecutionContext context, CallbackArguments args) =>
		{
			Measure();
			return DynValue.NewNumber(_capacity);
		});
		LuaInjection.AddFunction("StorageUsed", (ScriptExecutionContext context, CallbackArguments args) =>
		{
			Measure();
			return DynValue.NewNumber(_used);
		});
		LuaInjection.AddPatch(Features.ResourceList, "LUI/Menus/GameMenu.lua", "_m.menu_top = m\nend\n", LuaInjection.Mode.InsertAfter, @"
---lyt: resource list (top right) - Castle Story Plus
do
local list =
	dofile(""LUI/Panels/Gamemodes/Panel_StorageList.lua"")

local p =
	LayoutPanels.Vertical:New()
	.SetWidgetHandle(Widgets.LayoutElement, Handles.Visible, ||CastleStoryPlus.ResourceListShown(), true)
	.SetWidgetHandle(Widgets.LayoutElement, Handles.IgnoreLayout, ||true, true)
	.SetWidgetHandle(Widgets.LayoutElement, Handles.AnchorMin, ||Vector2.New(1, 1), true)
	.SetWidgetHandle(Widgets.LayoutElement, Handles.AnchorMax, ||Vector2.New(1, 1), true)
	.SetWidgetHandle(Widgets.LayoutElement, Handles.Pivot, ||Vector2.New(1, 1), true)
	.SetWidgetHandle(Widgets.LayoutElement, Handles.AnchoredPosition, ||Vector2.New(-56, -64), true)
	.SetWidgetHandle(Widgets.LayoutElement, Handles.AnchoredWidth, ||220, true)
	.SetWidgetHandle(Widgets.LayoutElement, Handles.AutoFit, ||true, true)
	.SetWidgetHandle(Widgets.LayoutElement, Handles.AutoFitHorizontal, ||FitMode.Unconstrained, true)
	.SetWidgetHandle(Widgets.LayoutElement, Handles.AutoFitVertical, ||FitMode.PreferredSize, true)
	.SetWidgetHandle(Widgets.LayoutGroup, Handles.Spacing, ||0, true)
	.SetWidgetHandle(Widgets.LayoutGroup, Handles.ChildForceExpandWidth, ||true, true)
	.SetWidgetHandle(Widgets.Image, Handles.Enabled, ||false, true)
	.SetWidgetHandle(Widgets.Image, Handles.Raycasts, ||false, true)

p.AddPanel(list)
Data.Storage.ev_onSetContent:AddListener(||p.Refresh())

_m.AddPanel(p)
_m.resourceList = p

---menu handle: show/hide the resource list (right bar, under the settings) - Castle Story Plus
local h = ButtonHandle.New()
h.Label = ||""Resources""
h.Icon = ||IconKeys.UI_Stockpile:Get64()
h.Highlight = function() if CastleStoryPlus.ResourceListShown() then return HighlightMode.All end return HighlightMode.None end
h.hasHotkey = false
h.OnAction = function()
	CastleStoryPlus.ToggleResourceList()
	p.Refresh()
	if h.ev_refresh then h.ev_refresh:Invoke() end
end

_m.mg.right:AddChild(h)
_m.mh.resourceList = h
end
");
		LuaInjection.AddPatch(Features.ResourceList, "LUI/Menus/GameMenu.lua", ".AddMenuHandleToggle(_m.mh.settings)\n", LuaInjection.Mode.InsertAfter, "\t\t.AddMenuHandleToggle(_m.mh.resourceList)\n");
		LuaInjection.AddPatch(Features.ResourceList, "LUI/Panels/Gamemodes/Panel_Storage.lua", "_t:CreateAlertPanel(_p)\n_t:CreateStoragePanel(_p)\n", LuaInjection.Mode.Replace, "_t:CreateAlertPanel(_p)\n--resource grid replaced by the Castle Story Plus resource list\n");
	}

	// Sums all stockpiles of the local player once per frame.
	private static void Measure()
	{
		if (_frame == Time.frameCount)
		{
			return;
		}
		_frame = Time.frameCount;
		_capacity = 0;
		_used = 0;
		var stockpiles = BrixSingleton<AutoList>.Instance?.GetInstances(ObjetsDynamiques.Palette);
		if (stockpiles == null)
		{
			return;
		}
		foreach (GameObject go in stockpiles)
		{
			if (go == null || !go.activeInHierarchy || !Affiliation.BelongsToMe(go))
			{
				continue;
			}
			Recepteur recepteur = go.GetComponent<Recepteur>();
			if (recepteur == null)
			{
				continue;
			}
			// Same as Recepteur.GetProportionEncombrement.
			_capacity += recepteur.BaseCapacity.Value(Adjectif.encombrement);
			_used += recepteur.ContentDescription.Value(Adjectif.encombrement);
		}
	}
}
