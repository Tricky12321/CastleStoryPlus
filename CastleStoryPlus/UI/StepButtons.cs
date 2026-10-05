using System.Globalization;
using CastleStoryPlus.Core;

namespace CastleStoryPlus.UI;

// Small "+" and "-" buttons over the top of a popup menu button (the quarry's resource and depth buttons). A menu
// handle in the game's Lua gets them by having h.Step = function(direction) ... end, returning true when something
// changed, and h.StepChanged, an event invoked after a change so the buttons show the new value. Registered by each
// feature that uses them; the popup menu's Lua is patched once.
internal static class StepButtons
{
	private static bool _registered;

	internal static void Register(string owner)
	{
		if (_registered)
		{
			return;
		}
		_registered = true;
		// Once the game has made the button's panel (p), the "+" and "-" go on top of it.
		LuaInjection.AddPatch(owner, "LUI/Menus/Game/Menu_Popup.lua", "local p_arrow = _m.AddArrow(p)\n", LuaInjection.Mode.InsertAfter,
			"if mHandle.Step then\n" + Button(1, -12f) + Button(-1, 12f) + "end\n");
	}

	// One button, x from the middle of the menu button. Made into a local first, as the game's Lua does: the game's
	// MoonSharp breaks (IndexOutOfRange) when a C# function such as AddPanel gets a call that returns several values
	// as its last argument.
	private static string Button(int direction, float x)
	{
		return "do local step = loadfile(\"LUI/Panels/ButtonPanel.lua\")({skipMap = {bgImg = true, hoverImg = true, hoverLbl = true, hoverHotkeyLbl = true, fgLbl = true}})\n"
			+ ".Width(||22, true)\n"
			+ ".Height(||22, true)\n"
			+ ".Image_Enabled(||false, true)\n"
			+ ".FrontImage_Sprite(||IconKeys." + ((direction < 0) ? "_UI_Minus" : "_UI_Plus") + ":Get64())\n"
			+ ".FrontImage_Color(||CastleYellow)\n"
			+ ".FrontImage_Width(||18, true)\n"
			+ ".FrontImage_Height(||18, true)\n"
			+ ".Button_OnAction(function() if not UIGame.IsPaused() and mHandle.Step(" + direction + ") and mHandle.StepChanged then mHandle.StepChanged:Invoke() end end)\n"
			+ ".SetWidgetHandle(Widgets.LayoutElement, Handles.IgnoreLayout, ||true, true)\n"
			+ ".SetWidgetHandle(Widgets.LayoutElement, Handles.AnchoredWidth, ||22, true)\n"
			+ ".SetWidgetHandle(Widgets.LayoutElement, Handles.AnchoredHeight, ||22, true)\n"
			+ ".SetWidgetHandle(Widgets.LayoutElement, Handles.AnchorMin, ||Vector2.New(0.5, 1), true)\n"
			+ ".SetWidgetHandle(Widgets.LayoutElement, Handles.AnchorMax, ||Vector2.New(0.5, 1), true)\n"
			+ ".SetWidgetHandle(Widgets.LayoutElement, Handles.Pivot, ||Vector2.New(0.5, 0), true)\n"
			+ ".SetWidgetHandle(Widgets.LayoutElement, Handles.AnchoredPosition, ||Vector2.New(" + x.ToString(CultureInfo.InvariantCulture) + ", 2), true)\n"
			+ "p.AddPanel(step) end\n";
	}
}
