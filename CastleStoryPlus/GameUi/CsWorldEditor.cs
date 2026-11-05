using Brix.UI.Builder.Menu.Component;
using CastleStoryPlus.Core;
using CastleStoryPlus.LuaUi;
using HarmonyLib;

namespace CastleStoryPlus.GameUi;

// [LuaUi] CsWorldEditor: the world editor's menu in C#. WorldEditorMenu.lua (the scene's "Menu: WorldEditor (Lua)",
// GS_WorldEditor, loaded by the world editor mode's worldeditor_ui.lua when the level loads) is not loaded: its menu
// component's Load opens the C# menu instead and its Unload closes it. The views live in the UI assembly
// (WorldEditorRoot) and read the game's world editor directly (Journee, Picking.WorldEditorGrid, WeTerrain,
// WeMarkers), where the Lua menus went through UIGame.
[Feature(Features.CsMenus, Features.CsMenusInfo)]
internal static class CsWorldEditor
{
	internal const string LuaPath = "Gamemodes/world_editor/Menu/WorldEditorMenu.lua";

	internal static bool IsOpen
	{
		get { return UiAssembly.WorldEditorOpen != null && UiAssembly.WorldEditorOpen(); }
	}

	private static void Enable()
	{
		if (!LuaUiConfig.CsWorldEditor)
		{
			return;
		}
		if (UiAssembly.Loads == 0)
		{
			UiAssembly.Load();
		}
		GameSession.OnLeave(Close);
		Plugin.Log.LogInfo("CsWorldEditor: the world editor's menu is C#; WorldEditorMenu.lua is not loaded");
	}

	internal static void Open()
	{
		if (UiAssembly.OpenWorldEditor == null)
		{
			Plugin.Log.LogError("CsWorldEditor: the UI assembly is not loaded; no world editor menu");
			return;
		}
		UiAssembly.OpenWorldEditor();
	}

	internal static void Close()
	{
		UiAssembly.CloseWorldEditor?.Invoke();
	}

	internal static bool IsWorldEditorMenu(LuaMenuComponent component)
	{
		return component != null && component.Path == LuaPath;
	}
}

// WorldEditorMenu.lua's menu component opens the C# menu instead of loading the Lua file.
[Feature(Features.CsMenus, Features.CsMenusInfo)]
[HarmonyPatch(typeof(LuaMenuComponent), nameof(LuaMenuComponent.Load))]
internal static class CsWorldEditorLoadPatch
{
	private static bool Prepare()
	{
		return LuaUiConfig.CsWorldEditor;
	}

	private static bool Prefix(LuaMenuComponent __instance, ref bool __result)
	{
		if (!CsWorldEditor.IsWorldEditorMenu(__instance))
		{
			return true;
		}
		CsWorldEditor.Open();
		__result = true;
		return false;
	}
}

[Feature(Features.CsMenus, Features.CsMenusInfo)]
[HarmonyPatch(typeof(LuaMenuComponent), nameof(LuaMenuComponent.Unload))]
internal static class CsWorldEditorUnloadPatch
{
	private static bool Prepare()
	{
		return LuaUiConfig.CsWorldEditor;
	}

	private static void Prefix(LuaMenuComponent __instance)
	{
		if (CsWorldEditor.IsWorldEditorMenu(__instance))
		{
			CsWorldEditor.Close();
		}
	}
}
