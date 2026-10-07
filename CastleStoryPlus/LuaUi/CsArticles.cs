using System;
using System.Collections.Generic;
using Brix.UI.Builder.Lui;
using Brix.UI.Builder.Menu;
using CastleStoryPlus.Core;
using CastleStoryPlus.UI;
using MoonSharp.Interpreter;
using UnityEngine;
using UnityEngine.UI;

namespace CastleStoryPlus.LuaUi;

// [LuaUi] CsTooltips: the in-game tooltips and the help drawn in C#. The game makes two tooltip menus (one for the
// task popup and the bars, one for the task list) and the help, and builds a small menu for every article in them
// while the map loads (53 tooltips, twice, and 22 help pages), though only one shows at a time. Here none is made:
// - The two tooltip menus' blocks in GameMenu are cut out. Their fields get a stand-in with the same events the
//   game's menus fire on them (ev_onSet { targetPanel, tooltip = path }, ev_onReset) and Visible, so the task
//   popup, the task list and the crew buttons (the game's Lua and the mod's) show C# tooltips through it.
// - The help keeps its button and socket, but its menu (Menu_Help.lua) is an empty one; the help sidebar is drawn
//   in C# while its handle is open, and its links and the tutorial's next buttons select pages in C#.
// The article files still load (Data.Article:Init): they are only data, and the mod's own additions to them (the
// quarry and loop limits) show as before.
[Feature(Features.CsMenus, Features.CsMenusInfo)]
internal static class CsArticles
{
	private const string GameMenu = "LUI/Menus/GameMenu.lua";

	private static readonly Dictionary<string, ArticleTooltipView> Tooltips = new Dictionary<string, ArticleTooltipView>();

	private static HelpView _help;

	// The help's page, kept while the help is closed; nil: the welcome page.
	private static DynValue _helpPath = DynValue.Nil;

	private static void Enable()
	{
		if (!LuaUiConfig.CsTooltips)
		{
			return;
		}
		LuaInjection.AddFunction("CsTooltipSet", (ScriptExecutionContext context, CallbackArguments args) =>
		{
			Set(Name(args), (args.Count > 1 && args[1].Type == DataType.Table) ? args[1].Table : null);
			return DynValue.Void;
		});
		LuaInjection.AddFunction("CsTooltipReset", (ScriptExecutionContext context, CallbackArguments args) =>
		{
			Hide(Name(args));
			return DynValue.Void;
		});
		LuaInjection.AddFunction("CsTooltipShown", (ScriptExecutionContext context, CallbackArguments args) =>
		{
			return DynValue.NewBoolean(Tooltips.TryGetValue(Name(args), out ArticleTooltipView view) && view != null && view.Shown);
		});
		LuaInjection.AddFunction("CsHelpSelect", (ScriptExecutionContext context, CallbackArguments args) =>
		{
			if (args.Count > 0 && args[0].Type == DataType.Table)
			{
				SelectHelp(args[0]);
			}
			return DynValue.Void;
		});
		GameMenuLink.ReplaceMenuBlock(Features.CsMenus, GameMenu, "---lyt: tooltip menu\ndo\n", "menu_tooltip", StandIn("tooltip"));
		GameMenuLink.ReplaceMenuBlock(Features.CsMenus, GameMenu, "---lyt: tooltip task menu\ndo\n", "menu_tooltipTask", StandIn("task"));
		// The help's menu: empty, so its button, socket and the hotkey that opens it work as before.
		LuaInjection.AddPatch(Features.CsMenus, "LUI/Menus/Game/Menu_Help.lua", "require(\"LUI2.lua\")\n-------\n--require(\"LTD.lua\")\n", LuaInjection.Mode.InsertAfter, @"
-- Castle Story Plus: [LuaUi] CsTooltips draws the help in C#; this empty menu only fills the help's socket.
if true then
	local m = Menu:New()
	m.LoadArticleMenus = function() end
	m.SelectArticle = function(a, b) CastleStoryPlus.CsHelpSelect(b or a) end
	m.SetMenuHandle = function(h) return m end
	m.SetFlag(MenuFlag.OpenOnLoad)
	m.SetFlag(MenuFlag.OpenedByParentMenu)
	m.SetFlag(MenuFlag.OpensChildMenus)
	return m
end
");
		GameMenuLink.Attached += Show;
		GameMenuLink.Detached += Drop;
	}

	// The stand-in for one of GameMenu's tooltip menus: the stub (any other call does nothing) with the tooltip
	// menu's events and Visible, which the task list asks before it moves its tooltip.
	private static string StandIn(string name)
	{
		return "(function()\n"
			+ "\tlocal m = CastleStoryPlus.MenuStub()\n"
			+ "\tm.ev_onSet = Event.New()\n"
			+ "\tm.ev_onReset = Event.New()\n"
			+ "\tm.ev_onSet:AddListener(function(t) CastleStoryPlus.CsTooltipSet(\"" + name + "\", t) end)\n"
			+ "\tm.ev_onReset:AddListener(function() CastleStoryPlus.CsTooltipReset(\"" + name + "\") end)\n"
			+ "\tm.Visible = function() return CastleStoryPlus.CsTooltipShown(\"" + name + "\") end\n"
			+ "\tm.LoadArticleMenus = function() end\n"
			+ "\treturn m\n"
			+ "end)()";
	}

	private static string Name(CallbackArguments args)
	{
		return (args.Count > 0 && args[0].Type == DataType.String) ? args[0].String : "tooltip";
	}

	private static void Show()
	{
		Drop();
		Table help = GameMenuLink.Handle("mh", "tutorial");
		if (help != null)
		{
			_help = HelpView.Create(LuiCanvas.Instance.Menus, help);
		}
		else
		{
			Plugin.Log.LogWarning("CsMenus: no help handle in GameMenu");
		}
		Plugin.Log.LogInfo("CsMenus: the tooltips and the help are drawn in C#");
	}

	private static void Drop()
	{
		foreach (ArticleTooltipView view in Tooltips.Values)
		{
			if (view != null)
			{
				UnityEngine.Object.Destroy(view.gameObject);
			}
		}
		Tooltips.Clear();
		if (_help != null)
		{
			UnityEngine.Object.Destroy(_help.gameObject);
		}
		_help = null;
		_helpPath = DynValue.Nil;
	}

	// A tooltip menu's ev_onSet: { tooltip = path, targetPanel = the hovered panel, size, offset }.
	private static void Set(string name, Table settings)
	{
		if (settings == null || GameMenuLink.Script == null)
		{
			return;
		}
		DynValue path = LuaBridge.Get(settings, "tooltip");
		if (path.Type != DataType.Table)
		{
			Hide(name);
			return;
		}
		RectTransform target = null;
		Table panel = LuaBridge.GetTable(settings, "targetPanel");
		// The C# task list (CsTaskList) hands an anchor table standing for its row.
		target = (panel != null) ? LuaAnchors.Find(panel) as RectTransform : null;
		if (target == null && panel != null && LuiBuilder.MapHelper.HasPanel(panel))
		{
			try
			{
				IPanel found = LuiBuilder.MapHelper.GetPanel(panel);
				target = (found != null) ? found.GetTrs() : null;
			}
			catch (Exception)
			{
				target = null;
			}
		}
		float width = ArticleTooltipView.DefaultWidth;
		DynValue size = LuaBridge.Get(settings, "size");
		if (size.Type == DataType.UserData && size.UserData != null && size.UserData.TryGet<Vector2>(out Vector2 sized))
		{
			width = sized.x;
		}
		View(name).Show(GameMenuLink.Script, path, target, width);
	}

	// A C# view's button with a TooltipPath (the bars' and sidebars' handles): its article, beside the button.
	public static void ShowFor(DynValue path, RectTransform target)
	{
		if (!LuaUiConfig.CsTooltips || GameMenuLink.Script == null || path.Type != DataType.Table)
		{
			return;
		}
		View("tooltip").Show(GameMenuLink.Script, path, target, ArticleTooltipView.DefaultWidth);
	}

	public static void HideFor(RectTransform target)
	{
		if (Tooltips.TryGetValue("tooltip", out ArticleTooltipView view) && view != null && view.Target == target)
		{
			view.Hide();
		}
	}

	private static void Hide(string name)
	{
		if (Tooltips.TryGetValue(name, out ArticleTooltipView view) && view != null)
		{
			view.Hide();
		}
	}

	private static ArticleTooltipView View(string name)
	{
		if (!Tooltips.TryGetValue(name, out ArticleTooltipView view) || view == null)
		{
			view = ArticleTooltipView.Create(LuiCanvas.Instance.Tooltips, name);
			Tooltips[name] = view;
		}
		return view;
	}

	// The help's page: a link, the tutorial's next button, or the game's own SelectArticle on the help's menu.
	public static void SelectHelp(DynValue path)
	{
		_helpPath = path;
		if (_help != null)
		{
			_help.PageChanged();
		}
	}

	public static DynValue HelpPath
	{
		get { return _helpPath; }
	}
}

// One tooltip: an article in a dark box above the hovered button (below it where there is no room above), kept on
// screen, as Menu_Tooltip.lua shows it. Not a raycast target, so it never takes the hover from the button.
internal class ArticleTooltipView : MonoBehaviour
{
	public const float DefaultWidth = 200f;

	private const float PollSeconds = 0.25f;

	private RectTransform _panel;

	private ArticleView _article;

	private DynValue _path = DynValue.Nil;

	private Vector2 _mouse;

	private float _readAt;

	public RectTransform Target { get; private set; }

	public bool Shown
	{
		get { return _panel != null && _panel.gameObject.activeSelf; }
	}

	public static ArticleTooltipView Create(Transform parent, string name)
	{
		RectTransform panel = UiKit.CreateRect("CS tooltip " + name, parent);
		panel.anchorMin = panel.anchorMax = new Vector2(0.5f, 0.5f);
		panel.pivot = new Vector2(0.5f, 0f);
		Image background = panel.gameObject.AddComponent<Image>();
		LuiStyle.Paint(background, LuiStyle.FrameSprite, LuiStyle.BgPanel);
		background.raycastTarget = false;
		VerticalLayoutGroup layout = panel.gameObject.AddComponent<VerticalLayoutGroup>();
		layout.padding = new RectOffset(16, 16, 8, 8);
		layout.childControlWidth = true;
		layout.childControlHeight = true;
		layout.childForceExpandWidth = true;
		layout.childForceExpandHeight = false;
		ContentSizeFitter fitter = panel.gameObject.AddComponent<ContentSizeFitter>();
		fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
		fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
		ArticleTooltipView view = panel.gameObject.AddComponent<ArticleTooltipView>();
		view._panel = panel;
		panel.gameObject.SetActive(false);
		return view;
	}

	public void Show(Script script, DynValue path, RectTransform target, float width)
	{
		Target = target;
		_mouse = Input.mousePosition;
		// An article's items are made when it is asked for (a recipe's tooltip has the recipe in it), so it is
		// drawn again each time it is set, as the game refreshes its panel.
		if (_article != null)
		{
			Destroy(_article.Root.gameObject);
		}
		_path = path;
		_article = ArticleView.Build(_panel, script, path, Mathf.Max(64f, width - 32f), null);
		foreach (Graphic graphic in _article.Root.GetComponentsInChildren<Graphic>(true))
		{
			graphic.raycastTarget = false;
		}
		_readAt = Time.unscaledTime;
		_panel.gameObject.SetActive(true);
		_panel.SetAsLastSibling();
		LayoutRebuilder.ForceRebuildLayoutImmediate(_panel);
		Place();
	}

	public void Hide()
	{
		Target = null;
		_path = DynValue.Nil;
		if (_panel != null)
		{
			_panel.gameObject.SetActive(false);
		}
	}

	private void LateUpdate()
	{
		// The button went away under the mouse (its menu closed): no exit event comes.
		if (Target != null && !Target.gameObject.activeInHierarchy)
		{
			Hide();
			return;
		}
		if (_article != null && Time.unscaledTime - _readAt >= PollSeconds)
		{
			_readAt = Time.unscaledTime;
			_article.Refresh();
		}
		Place();
	}

	// Above the target's top edge, centred on it; below it when there is no room above; inside the screen.
	private void Place()
	{
		RectTransform layer = (RectTransform)_panel.parent;
		Rect bounds = layer.rect;
		Vector2 size = _panel.rect.size;
		float centre;
		float top;
		float bottom;
		if (Target != null)
		{
			Vector3[] corners = new Vector3[4];
			Target.GetWorldCorners(corners);
			Vector2 low = layer.InverseTransformPoint(corners[0]);
			Vector2 high = layer.InverseTransformPoint(corners[2]);
			centre = (low.x + high.x) * 0.5f;
			top = high.y;
			bottom = low.y;
		}
		else
		{
			Canvas canvas = layer.GetComponentInParent<Canvas>();
			Camera camera = (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay) ? canvas.worldCamera : null;
			RectTransformUtility.ScreenPointToLocalPointInRectangle(layer, _mouse, camera, out Vector2 local);
			centre = local.x;
			top = local.y + 16f;
			bottom = local.y - 16f;
		}
		float y = top + 8f;
		if (y + size.y > bounds.yMax)
		{
			y = bottom - 8f - size.y;
		}
		y = Mathf.Clamp(y, bounds.yMin, Mathf.Max(bounds.yMin, bounds.yMax - size.y));
		float x = Mathf.Clamp(centre, bounds.xMin + size.x * 0.5f, Mathf.Max(bounds.xMin + size.x * 0.5f, bounds.xMax - size.x * 0.5f));
		_panel.anchoredPosition = new Vector2(x, y);
	}
}

// The help sidebar (Menu_Help.lua) in C#: beside the right bar while the help's handle is open, a header with the
// page's title and a close button, and the page below it, scrolling. Starts on the welcome page; links and the
// tutorial's next buttons change the page.
internal class HelpView : MonoBehaviour
{
	private const float Width = 272f;

	// GameMenu's content overlays leave this much room at the bottom (the task list).
	private const float Bottom = 154f;

	private const float BarWidth = 48f;

	private const float PollSeconds = 0.25f;

	private const string WelcomeTitle = "##help_sidebar_welcometocastlestory_title";

	private Table _handle;

	private RectTransform _panel;

	private Text _title;

	private Transform _content;

	private ScrollRect _scroll;

	private ArticleView _article;

	private DynValue _shownPath = DynValue.Nil;

	private readonly List<Action> _unlisten = new List<Action>();

	private bool _shown;

	private bool _dirty = true;

	private float _readAt;

	public static HelpView Create(Transform parent, Table handle)
	{
		RectTransform panel = UiKit.CreateRect("CS help", parent);
		panel.anchorMin = new Vector2(1f, 0f);
		panel.anchorMax = new Vector2(1f, 1f);
		panel.pivot = new Vector2(1f, 0.5f);
		panel.offsetMin = new Vector2(-BarWidth - Width, Bottom);
		panel.offsetMax = new Vector2(-BarWidth, 0f);
		Image background = panel.gameObject.AddComponent<Image>();
		LuiStyle.Paint(background, LuiStyle.FrameSprite, LuiStyle.BgPanel);
		VerticalLayoutGroup layout = panel.gameObject.AddComponent<VerticalLayoutGroup>();
		layout.padding = new RectOffset(0, 0, 0, 4);
		layout.childControlWidth = true;
		layout.childControlHeight = true;
		layout.childForceExpandWidth = true;
		layout.childForceExpandHeight = false;
		HelpView view = panel.gameObject.AddComponent<HelpView>();
		view._handle = handle;
		view._panel = panel;
		view.Build();
		view.Listen();
		LuiCanvas.Instance.Tick += view.Tick;
		view.SetShown(false);
		view.Tick();
		return view;
	}

	private void Build()
	{
		Transform header = UiKit.CreateRow(_panel, 40f);
		((HorizontalLayoutGroup)header.GetComponent<HorizontalLayoutGroup>()).padding = new RectOffset(10, 4, 0, 0);
		_title = LuiWidgets.Label(header, string.Empty, LuiStyle.TitleFontSize, LuiStyle.CastleYellow, TextAnchor.MiddleLeft, LuiStyle.Title);
		UiKit.Flexible(_title.gameObject);
		LuiIconButton close = LuiWidgets.IconButton(header, 32f);
		close.SetLabel(string.Empty);
		close.SetCount("X", LuiStyle.CastleYellow);
		close.SetBackground(LuiStyle.BgNone);
		close.OnClick = (bool shift, bool ctrl) =>
		{
			LuaBridge.CallMethod(_handle, "Close");
			_dirty = true;
		};
		LuiWidgets.Divider(_panel, vertical: false);
		_content = UiKit.CreateScrollList(_panel, out LayoutElement size);
		size.flexibleHeight = 1f;
		size.minHeight = 64f;
		_scroll = _content.GetComponentInParent<ScrollRect>();
		VerticalLayoutGroup list = _content.GetComponent<VerticalLayoutGroup>();
		list.padding = new RectOffset(10, 10, 6, 6);
		list.spacing = 0f;
	}

	private void Listen()
	{
		Script script = _handle.OwnerScript;
		foreach (string name in new string[] { "onOpen", "onClose" })
		{
			Table luaEvent = LuaBridge.GetTable(_handle, name);
			if (luaEvent != null)
			{
				_unlisten.Add(LuaBridge.Listen(luaEvent, script, () => _dirty = true));
			}
		}
	}

	// The page changed: shown now when the help is open, else when it opens next.
	public void PageChanged()
	{
		_dirty = true;
		if (_shown)
		{
			ShowPage();
		}
	}

	private void Tick()
	{
		if (_handle == null || (!_dirty && Time.unscaledTime - _readAt < PollSeconds))
		{
			return;
		}
		_dirty = false;
		_readAt = Time.unscaledTime;
		bool shown = LuaBridge.Get(_handle, "parent").Type == DataType.Table && LuaBridge.Bool(LuaBridge.CallMethod(_handle, "IsOpened"), false);
		if (shown != _shown)
		{
			SetShown(shown);
		}
		if (!_shown)
		{
			return;
		}
		if (!SamePath(CsArticles.HelpPath, _shownPath) || _article == null)
		{
			ShowPage();
		}
		else
		{
			_article.Refresh();
		}
	}

	private static bool SamePath(DynValue a, DynValue b)
	{
		if (a.IsNil() || b.IsNil())
		{
			return a.IsNil() && b.IsNil();
		}
		return a.Type == DataType.Table && b.Type == DataType.Table && a.Table == b.Table;
	}

	private void ShowPage()
	{
		Script script = _handle.OwnerScript;
		DynValue path = CsArticles.HelpPath;
		bool welcome = path.IsNil();
		DynValue page = welcome ? ArticleLua.Path(script, "WelcomeToCastleStory") : path;
		_shownPath = path;
		if (_article != null)
		{
			Destroy(_article.Root.gameObject);
		}
		_article = ArticleView.Build(_content, script, page, Width - 20f - 8f, CsArticles.SelectHelp);
		_title.text = welcome ? LuaBridge.Localized(WelcomeTitle) : ArticleLua.Title(script, page);
		if (_scroll != null)
		{
			_scroll.verticalNormalizedPosition = 1f;
		}
	}

	private void SetShown(bool shown)
	{
		_shown = shown;
		_panel.gameObject.SetActive(shown);
		if (shown)
		{
			ShowPage();
		}
	}

	private void OnDestroy()
	{
		if (LuiCanvas.Exists)
		{
			LuiCanvas.Instance.Tick -= Tick;
		}
		if (GameMenuLink.Script != null)
		{
			foreach (Action unlisten in _unlisten)
			{
				unlisten();
			}
		}
		_unlisten.Clear();
		_handle = null;
	}
}
