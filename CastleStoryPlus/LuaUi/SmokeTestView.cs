using CastleStoryPlus.Core;
using MoonSharp.Interpreter;
using UnityEngine;

namespace CastleStoryPlus.LuaUi;

// [LuaUi] CsSmokeTest: a C# copy of the in-game right bar (home, settings, help, call to arms...), drawn from the
// game's own handles at the right edge below the game's bar. Clicking a button does what the game's does, the
// open menu's button stays dark like the game's, and hotkeys and labels follow. Only for checking the C# menu kit
// in a game; the game's own bar is left as it is.
[Feature(Features.CsMenus, Features.CsMenusInfo)]
internal static class SmokeTestView
{
	private static GameObject _view;

	private static void Enable()
	{
		if (!LuaUiConfig.CsSmokeTest)
		{
			return;
		}
		GameMenuLink.Attached += Show;
		GameMenuLink.Detached += Hide;
	}

	private static void Show()
	{
		Hide();
		Table right = GameMenuLink.Group("right");
		if (right == null)
		{
			Plugin.Log.LogWarning("CsMenus: smoke test found no right bar (_m.mg.right)");
			return;
		}
		RectTransform frame = LuiWidgets.Window(LuiCanvas.Instance.Menus, "C#", LuiStyle.ButtonSize + 28f);
		frame.anchorMin = frame.anchorMax = new Vector2(1f, 0.5f);
		frame.pivot = new Vector2(1f, 0.5f);
		frame.anchoredPosition = new Vector2(-4f, -120f);
		LuaGroupView.Create(frame, right, vertical: true, LuiStyle.ButtonSize, LuiIconButton.Side.Left);
		_view = frame.gameObject;
		Plugin.Log.LogInfo("CsMenus: smoke test shows the right bar's " + GameMenuLink.Children(right).Length + " handles");
	}

	private static void Hide()
	{
		if (_view != null)
		{
			Object.Destroy(_view);
		}
		_view = null;
	}
}
