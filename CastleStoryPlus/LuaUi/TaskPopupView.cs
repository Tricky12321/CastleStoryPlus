using System;
using System.Collections.Generic;
using Brix.UI.Builder.Lui;
using Brix.UI.Builder.Menu;
using Brix.UI.Icons;
using CastleStoryPlus.Core;
using CastleStoryPlus.UI;
using MoonSharp.Interpreter;
using UnityEngine;
using UnityEngine.UI;

namespace CastleStoryPlus.LuaUi;

// [LuaUi] CsTaskPopup: the selected task's popup over the task list (Menu_Popup.lua: build and blueprint options,
// landscape, quarry, tunnel and harvest options, goals, priority, ok / apply / cancel / delete, the workshop's
// recipes, input, output and queue, the crystal's population and spawn) drawn in C#. Its block in GameMenu is cut
// out and the menu field gets a recorder with the popup's events and Visible: GameMenu's build coroutine adds its
// sections and dividers to it in order (_m.menu_popup.AddSection(_m.sh.goals).AddDivider(...)...), and the task
// list, the game's or the C# one, tells it which task row to stand over (ev_onSet { targetPanel }) and when to go
// (ev_onReset). Each section's buttons come from its handle's ev_onLoad event, called once with a recorder as the
// game's popup does in its AddSection, so the game's handles and every mod's (the work limit, pause, build needs,
// quarry resources, depth and limits, loop limit, workshop priority) are drawn alike. Every button acts through
// its handle (OnAction, Step), as the game's popup does, so all goes through the game's own Lua (multiplayer
// commands included). Sections are read when they say so (ev_refresh: a task selected, a status or priority set)
// and their buttons a few times a second only while their section shows.
[Feature(Features.CsMenus, Features.CsMenusInfo)]
internal static class CsTaskPopup
{
	private const string Recorder = "menu_popup";

	private static CsTaskPopupView _view;

	// What the task list last told the popup: show over this place, or nothing.
	private static bool _targeted;

	private static Transform _target;

	private static void Enable()
	{
		if (!LuaUiConfig.CsTaskPopup)
		{
			return;
		}
		LuaMenuRecorder.Register();
		LuaInjection.AddFunction("CsPopupSet", (ScriptExecutionContext context, CallbackArguments args) =>
		{
			Set((args.Count > 0 && args[0].Type == DataType.Table) ? args[0].Table : null);
			return DynValue.Void;
		});
		LuaInjection.AddFunction("CsPopupReset", (ScriptExecutionContext context, CallbackArguments args) =>
		{
			_targeted = false;
			_target = null;
			return DynValue.Void;
		});
		LuaInjection.AddFunction("CsPopupVisible", (ScriptExecutionContext context, CallbackArguments args) =>
		{
			return DynValue.NewBoolean(_view != null && _view.Shown);
		});
		GameMenuLink.ReplaceMenuBlock(Features.CsMenus, "LUI/Menus/GameMenu.lua", "---lyt: popup menu\ndo\n", "menu_popup", StandIn());
		GameMenuLink.Attached += Show;
		GameMenuLink.Detached += Hide;
	}

	// The recorder in the popup's place, with the popup's events and Visible, which the task list uses.
	private static string StandIn()
	{
		// The cut block also set the popup's height and place, which the tooltip menu's block below reads.
		return "(function()\n"
			+ "\t_m.popupMenuHeight = 72\n"
			+ "\t_m.popupMenuPosY = _m.projectMenuHeight + 8\n"
			+ "\tlocal m = CastleStoryPlus.MenuRecorder(\"" + Recorder + "\")\n"
			+ "\tm.ev_onSet = Event.New()\n"
			+ "\tm.ev_onReset = Event.New()\n"
			+ "\tm.ev_onSet:AddListener(function(t) CastleStoryPlus.CsPopupSet(t) end)\n"
			+ "\tm.ev_onReset:AddListener(function() CastleStoryPlus.CsPopupReset() end)\n"
			+ "\tm.Visible = function() return CastleStoryPlus.CsPopupVisible() end\n"
			+ "\tm.ResetButtons = function() end\n"
			+ "\treturn m\n"
			+ "end)()";
	}

	// ev_onSet { targetPanel = the selected task's row (a Lua panel of the game's task list, or a C# view's anchor) }.
	private static void Set(Table settings)
	{
		_targeted = settings != null;
		_target = null;
		Table panel = (settings != null) ? LuaBridge.GetTable(settings, "targetPanel") : null;
		if (panel == null)
		{
			return;
		}
		_target = LuaAnchors.Find(panel);
		if (_target == null && LuiBuilder.MapHelper.HasPanel(panel))
		{
			try
			{
				IPanel found = LuiBuilder.MapHelper.GetPanel(panel);
				_target = (found != null) ? found.GetTrs() : null;
			}
			catch (Exception)
			{
				_target = null;
			}
		}
	}

	public static bool Targeted
	{
		get { return _targeted; }
	}

	public static Transform Target
	{
		get { return _target; }
	}

	private static void Show()
	{
		Hide();
		LuaMenuRecorder recorder = LuaMenuRecorder.Find(Recorder);
		if (recorder == null)
		{
			Plugin.Log.LogWarning("CsMenus: the task popup's menu was not replaced; is another mod changing GameMenu.lua?");
			return;
		}
		_view = CsTaskPopupView.Create(LuiCanvas.Instance.Menus, recorder);
		Plugin.Log.LogInfo("CsMenus: the task popup is drawn in C#");
	}

	private static void Hide()
	{
		if (_view != null)
		{
			UnityEngine.Object.Destroy(_view.gameObject);
		}
		_view = null;
		_targeted = false;
		_target = null;
	}
}

// The popup: a row of sections (and dividers between them) over the selected task's row in the task list, as
// GameMenu places the game's (72 high, its bottom 8 above the 72 high task list, between the side bars) and as the
// game's popup follows its row (its left edge 50 left of the row, kept on screen).
internal class CsTaskPopupView : MonoBehaviour
{
	private const float Height = 72f;

	private const float Bottom = 72f + 8f;

	private const float Side = 48f + 1f;

	private const float RowOffset = -50f;

	private const float ButtonSize = 64f;

	private const float ReadSeconds = 0.5f;

	private class Section
	{
		public Table Handle;

		public RectTransform Rect;

		public Text Title;

		public bool Shown;

		public readonly List<LuaHandleView> Buttons = new List<LuaHandleView>();

		public readonly List<LuaElementView> Elements = new List<LuaElementView>();
	}

	private LuaMenuRecorder _recorder;

	private RectTransform _rect;

	private RectTransform _row;

	private int _done;

	private readonly List<Section> _sections = new List<Section>();

	private readonly Dictionary<Table, Section> _byHandle = new Dictionary<Table, Section>();

	private readonly List<LuaElementView> _dividers = new List<LuaElementView>();

	private readonly List<Action> _unlisten = new List<Action>();

	private bool _dirty = true;

	private float _readAt;

	public bool Shown { get; private set; }

	public static CsTaskPopupView Create(Transform parent, LuaMenuRecorder recorder)
	{
		RectTransform rect = UiKit.CreateRect("CS task popup", parent);
		rect.anchorMin = Vector2.zero;
		rect.anchorMax = Vector2.zero;
		rect.pivot = Vector2.zero;
		rect.anchoredPosition = new Vector2(Side, Bottom);
		rect.sizeDelta = new Vector2(0f, Height);
		HorizontalLayoutGroup layout = rect.gameObject.AddComponent<HorizontalLayoutGroup>();
		layout.spacing = 0f;
		layout.childAlignment = TextAnchor.LowerLeft;
		layout.childControlWidth = true;
		layout.childControlHeight = true;
		layout.childForceExpandWidth = false;
		layout.childForceExpandHeight = false;
		ContentSizeFitter fitter = rect.gameObject.AddComponent<ContentSizeFitter>();
		fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
		fitter.verticalFit = ContentSizeFitter.FitMode.Unconstrained;
		CsTaskPopupView view = rect.gameObject.AddComponent<CsTaskPopupView>();
		view._recorder = recorder;
		view._rect = rect;
		view._row = rect;
		rect.gameObject.SetActive(false);
		LuiCanvas.Instance.Tick += view.Tick;
		return view;
	}

	private void Tick()
	{
		if (_recorder == null)
		{
			return;
		}
		// GameMenu adds the sections in its build coroutine, a few frames after the view is made.
		if (_recorder.Calls.Count > _done)
		{
			Build();
		}
		if (_dirty || Time.unscaledTime - _readAt >= ReadSeconds)
		{
			ReadSections();
		}
		bool shown = CsTaskPopup.Targeted && AnySection();
		if (shown != Shown)
		{
			Shown = shown;
			_rect.gameObject.SetActive(shown);
			SetPolling();
		}
		if (shown)
		{
			Place();
		}
	}

	private void Build()
	{
		for (; _done < _recorder.Calls.Count; _done++)
		{
			LuaMenuRecorder.Call call = _recorder.Calls[_done];
			Table first = LuaMenuRecorder.TableArg(call, 0);
			switch (call.Method)
			{
			case "AddSection":
				if (first != null)
				{
					AddSection(first);
				}
				break;
			case "AddDivider":
				if (first != null)
				{
					_dividers.Add(CsPopupDividerView.Create(_row, first));
				}
				break;
			case "AddMenuHandleToggle":
				if (first != null)
				{
					AddButton(first, _row, null);
				}
				break;
			// The task tooltip menu (P6's stand-in or the game's): the C# buttons show their articles themselves.
			case "SetTooltipMenu":
				break;
			default:
				LuaBridge.LogOnce("task popup", "no C# view for " + call.Method);
				break;
			}
		}
		_dirty = true;
	}

	// A section as the game's popup makes it (Menu_Popup.AddSection): a row 72 high with its title over its top
	// left, filled by its ev_onLoad listeners, which are called once with a recorder standing for the popup.
	private void AddSection(Table handle)
	{
		if (_byHandle.ContainsKey(handle))
		{
			return;
		}
		RectTransform rect = UiKit.CreateRect("Section", _row);
		HorizontalLayoutGroup layout = rect.gameObject.AddComponent<HorizontalLayoutGroup>();
		layout.padding = new RectOffset(2, 2, 2, 2);
		layout.spacing = 1f;
		layout.childAlignment = TextAnchor.LowerLeft;
		layout.childControlWidth = true;
		layout.childControlHeight = true;
		layout.childForceExpandWidth = false;
		layout.childForceExpandHeight = false;
		LayoutElement element = rect.gameObject.AddComponent<LayoutElement>();
		element.minHeight = Height;
		element.preferredHeight = Height;
		// The game's popup has no background of its own; this one gives each section the bars' dark background,
		// which also keeps clicks between its buttons off the world.
		Image background = rect.gameObject.AddComponent<Image>();
		LuiStyle.Paint(background, LuiStyle.FrameSprite, LuiStyle.BgDefault);
		Section section = new Section { Handle = handle, Rect = rect };
		if (!LuaBridge.Bool(LuaBridge.Get(handle, "hideHeader"), false))
		{
			Text title = LuiWidgets.Label(rect, string.Empty, LuiStyle.SmallFontSize, LuiStyle.CastleYellow, TextAnchor.UpperLeft, LuiStyle.Bold);
			title.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
			UiKit.SetRect(title.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
			title.rectTransform.offsetMin = new Vector2(8f, 6f);
			title.rectTransform.offsetMax = new Vector2(-8f, -4f);
			title.gameObject.AddComponent<Shadow>().effectColor = new Color(0f, 0f, 0f, 0.8f);
			section.Title = title;
		}
		_sections.Add(section);
		_byHandle[handle] = section;
		Table refresh = LuaBridge.GetTable(handle, "ev_refresh");
		if (refresh != null)
		{
			_unlisten.Add(LuaBridge.Listen(refresh, handle.OwnerScript, () => _dirty = true));
		}
		Table onLoad = LuaBridge.GetTable(handle, "ev_onLoad");
		if (onLoad == null)
		{
			return;
		}
		LuaMenuRecorder loaded = LuaMenuRecorder.Create(handle.OwnerScript, "popup section");
		LuaBridge.CallMethod(onLoad, "Invoke", DynValue.NewTable(loaded.Table), DynValue.NewTable(handle));
		foreach (LuaMenuRecorder.Call call in loaded.Calls)
		{
			Table first = LuaMenuRecorder.TableArg(call, 0);
			Section into = Into(LuaMenuRecorder.TableArg(call, 1), section);
			switch (call.Method)
			{
			case "AddMenuHandleToggle":
				if (first != null)
				{
					AddButton(first, into.Rect, into);
				}
				break;
			case "AddPanelHandle":
				if (first != null)
				{
					AddPanel(first, into);
				}
				break;
			case "AddDivider":
				if (first != null)
				{
					into.Elements.Add(CsPopupDividerView.Create(into.Rect, first));
				}
				break;
			default:
				LuaBridge.LogOnce("task popup section", "no C# view for " + call.Method);
				break;
			}
		}
	}

	private Section Into(Table handle, Section fallback)
	{
		return (handle != null && _byHandle.TryGetValue(handle, out Section found)) ? found : fallback;
	}

	// A handle's button (Menu_Popup.AddMenuHandleToggle): 64 pixels, its count under the icon, no label beside it;
	// what it does is in its article tooltip (CsTooltips), else in its label as a plain tooltip. A handle with a
	// Step closure (the work limit, the quarry's limits and depth) gets small "+" and "-" over its top, as
	// StepButtons gives the game's.
	private void AddButton(Table handle, Transform parent, Section section)
	{
		LuaHandleView view = LuaHandleView.Create(parent, handle, ButtonSize, LuiIconButton.Side.Right);
		bool article = LuaUiConfig.CsTooltips && !LuaBridge.Get(handle, "TooltipPath").IsNil();
		view.HideLabel = article;
		view.LabelAsTooltip = !article;
		view.Polling = false;
		view.Read();
		if (!LuaBridge.Get(handle, "Step").IsNil())
		{
			AddStep(view, 1, -12f);
			AddStep(view, -1, 12f);
		}
		if (section != null)
		{
			section.Buttons.Add(view);
		}
	}

	private static void AddStep(LuaHandleView view, int direction, float x)
	{
		LuiIconButton step = LuiWidgets.IconButton(view.transform, 22f);
		step.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
		RectTransform rect = (RectTransform)step.transform;
		rect.anchorMin = new Vector2(0.5f, 1f);
		rect.anchorMax = new Vector2(0.5f, 1f);
		rect.pivot = new Vector2(0.5f, 0f);
		rect.anchoredPosition = new Vector2(x, 2f);
		rect.sizeDelta = new Vector2(22f, 22f);
		step.SetIcon(((direction < 0) ? IconKeys._UI_Minus : IconKeys._UI_Plus).Get64());
		step.SetIconSize(18f);
		step.SetIconColor(LuiStyle.CastleYellow);
		step.SetLabel(string.Empty);
		step.SetBackground(LuiStyle.BgNone);
		step.OnClick = (bool shift, bool ctrl) =>
		{
			if (ClockConfig.Paused || view.Handle == null)
			{
				return;
			}
			if (view.Step(direction))
			{
				Table changed = LuaBridge.GetTable(view.Handle, "StepChanged");
				if (changed != null)
				{
					LuaBridge.CallMethod(changed, "Invoke");
				}
			}
		};
	}

	// A handle drawn from its own panel file (Menu_Popup.AddPanelHandle): the crystal's energy bar is the only one
	// the game has (Panel_ProgressHorizontal.lua).
	private static void AddPanel(Table handle, Section section)
	{
		string path = LuaBridge.String(LuaBridge.Value(handle, "Path"), string.Empty);
		if (path.EndsWith("Panel_ProgressHorizontal.lua", StringComparison.Ordinal))
		{
			section.Elements.Add(CsPopupProgressView.Create(section.Rect, handle));
			return;
		}
		LuaBridge.LogOnce("task popup", "no C# view for the panel " + path);
	}

	private void ReadSections()
	{
		_dirty = false;
		_readAt = Time.unscaledTime;
		bool changed = false;
		foreach (Section section in _sections)
		{
			bool shown = LuaBridge.Bool(LuaBridge.Value(section.Handle, "Visible"), true);
			if (shown != section.Shown)
			{
				section.Shown = shown;
				section.Rect.gameObject.SetActive(shown);
				changed = true;
			}
			if (shown && section.Title != null)
			{
				section.Title.text = LuaBridge.String(LuaBridge.Value(section.Handle, "Label"), string.Empty);
				section.Title.color = LuaBridge.Color(LuaBridge.Value(section.Handle, "LabelColor"), LuiStyle.CastleYellow);
			}
		}
		if (changed)
		{
			SetPolling();
		}
	}

	private bool AnySection()
	{
		foreach (Section section in _sections)
		{
			if (section.Shown)
			{
				return true;
			}
		}
		return false;
	}

	// Buttons are read a few times a second only while their section shows; otherwise on their own events.
	private void SetPolling()
	{
		foreach (Section section in _sections)
		{
			bool polling = Shown && section.Shown;
			foreach (LuaHandleView button in section.Buttons)
			{
				if (button != null && button.Polling != polling)
				{
					button.Polling = polling;
					if (polling)
					{
						button.MarkDirty();
					}
				}
			}
			foreach (LuaElementView element in section.Elements)
			{
				if (element != null && element.Polling != polling)
				{
					element.Polling = polling;
					if (polling)
					{
						element.MarkDirty();
					}
				}
			}
		}
	}

	// Over the selected task's row: its left edge 50 left of the row's, kept between the side bars.
	private void Place()
	{
		RectTransform layer = (RectTransform)_rect.parent;
		float x = Side;
		Transform target = CsTaskPopup.Target;
		if (target is RectTransform row)
		{
			Vector3[] corners = new Vector3[4];
			row.GetWorldCorners(corners);
			x = layer.InverseTransformPoint(corners[0]).x - layer.rect.xMin + RowOffset;
		}
		float most = layer.rect.width - Side - _rect.rect.width;
		x = Mathf.Max(Side, Mathf.Min(x, most));
		Vector2 position = new Vector2(Mathf.Round(x), Bottom);
		if (_rect.anchoredPosition != position)
		{
			_rect.anchoredPosition = position;
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
		_recorder = null;
	}
}

// A divider between the popup's sections (VerticalDividerPanelC.lua): a thin line 48 high, shown while its handle
// says so (the build divider only with a build task selected...).
internal class CsPopupDividerView : LuaElementView
{
	public static CsPopupDividerView Create(Transform parent, Table handle)
	{
		RectTransform rect = UiKit.CreateRect("Divider", parent);
		LayoutElement element = rect.gameObject.AddComponent<LayoutElement>();
		element.minWidth = 9f;
		element.preferredWidth = 9f;
		element.minHeight = 72f;
		element.preferredHeight = 72f;
		Image background = rect.gameObject.AddComponent<Image>();
		background.color = LuiStyle.BgDefault;
		RectTransform line = UiKit.CreateRect("Line", rect);
		UiKit.SetRect(line, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1f, 48f));
		Image image = line.gameObject.AddComponent<Image>();
		image.color = LuiStyle.Gray;
		image.raycastTarget = false;
		CsPopupDividerView view = rect.gameObject.AddComponent<CsPopupDividerView>();
		view.Polling = false;
		view.Bind(handle);
		return view;
	}

	protected override void Read()
	{
		Visible(true);
	}
}

// The crystal's energy bar (Panel_ProgressHorizontal.lua): Progress (0 to 1) with ProgressText and ProgressCount,
// read again on ev_refreshProgress (the nest's progress set) and ev_refresh.
internal class CsPopupProgressView : LuaElementView
{
	private Text _caption;

	private LuiProgressBar _bar;

	public static CsPopupProgressView Create(Transform parent, Table handle)
	{
		RectTransform rect = UiKit.CreateRect("Progress", parent);
		UiKit.Fixed(rect.gameObject, 200f, 64f);
		VerticalLayoutGroup layout = rect.gameObject.AddComponent<VerticalLayoutGroup>();
		layout.padding = new RectOffset(8, 8, 8, 8);
		layout.spacing = 4f;
		layout.childAlignment = TextAnchor.MiddleLeft;
		layout.childControlWidth = true;
		layout.childControlHeight = true;
		layout.childForceExpandWidth = true;
		layout.childForceExpandHeight = false;
		CsPopupProgressView view = rect.gameObject.AddComponent<CsPopupProgressView>();
		view._caption = LuiWidgets.Label(rect, string.Empty, LuiStyle.SmallFontSize, LuiStyle.CastleYellow, TextAnchor.MiddleLeft, LuiStyle.Bold);
		view._caption.gameObject.AddComponent<LayoutElement>().preferredHeight = 16f;
		view._bar = LuiWidgets.ProgressBar(rect, 184f, 20f);
		// Energy is blue, as the game's crystal light.
		view._bar.SetColor(LuiStyle.CastleBlue);
		Table progress = LuaBridge.GetTable(handle, "ev_refreshProgress");
		if (progress != null)
		{
			CsUnlisten.Add(rect.gameObject, LuaBridge.Listen(progress, handle.OwnerScript, view.MarkDirty));
		}
		view.Polling = false;
		view.Bind(handle);
		return view;
	}

	protected override void Read()
	{
		if (!Visible(true))
		{
			return;
		}
		_caption.text = LuaBridge.String(LuaBridge.Value(Handle, "ProgressText"), string.Empty);
		_bar.Set(LuaBridge.Number(LuaBridge.Value(Handle, "Progress"), 0f), LuaBridge.String(LuaBridge.Value(Handle, "ProgressCount"), string.Empty));
	}

}

// Removes a Lua event listener when its view goes (for listeners besides those LuaElementView keeps itself).
internal class CsUnlisten : MonoBehaviour
{
	private readonly List<Action> _unlisten = new List<Action>();

	public static void Add(GameObject owner, Action unlisten)
	{
		CsUnlisten holder = owner.GetComponent<CsUnlisten>();
		if (holder == null)
		{
			holder = owner.AddComponent<CsUnlisten>();
		}
		holder._unlisten.Add(unlisten);
	}

	private void OnDestroy()
	{
		if (GameMenuLink.Script != null)
		{
			foreach (Action unlisten in _unlisten)
			{
				unlisten();
			}
		}
		_unlisten.Clear();
	}
}
