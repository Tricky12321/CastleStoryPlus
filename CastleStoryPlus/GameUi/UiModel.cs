using System;
using System.Collections.Generic;
using CastleStoryPlus.LuaUi;
using UnityEngine;

namespace CastleStoryPlus.GameUi;

// The in-game menu as C# data, in place of GameMenu.lua's handles: what each button shows and does is a set of
// delegates read by the views (CsButtonView...) when the game state changes (GameUiState.Changed) and a few times
// a second while shown. Features add their own items to the menus through GameMenuItems.
internal abstract class UiItem
{
	// A name to find the item by (and to place another after it).
	public string Id;

	public Func<bool> Visible;

	public bool IsVisible()
	{
		return Visible == null || Visible();
	}
}

// A square icon button (the bars, the catalogues, the task popup's actions).
internal sealed class UiButton : UiItem
{
	public Func<string> Label;

	public Func<Sprite> Icon;

	public Func<Color> IconColor;

	// Text in the lower right corner (a count), and its colour.
	public Func<string> Count;

	public Func<Color> CountColor;

	// The key bound to it, in the upper left corner.
	public Func<string> Hotkey;

	// A yellow frame: the held blueprint, a task type being placed, a kept resource.
	public Func<bool> Highlight;

	// The darker background of an open menu.
	public Func<bool> Selected;

	// Greyed out (and not clickable) when false.
	public Func<bool> Enabled;

	// Shown on hover, as a tooltip (a ##key is localized).
	public Func<string> Tooltip;

	// A second line under the label, where the button is drawn as a row (a recipe's cost); rich text.
	public Func<string> Detail;

	// Where the button is drawn as a row and has a Step: the value shown between its "-" and "+" (a recipe's loop
	// limit), and the tooltip of that counter.
	public Func<string> StepValue;

	public Func<string> StepTooltip;

	// Left click with Shift and Ctrl.
	public Action<bool, bool> OnClick;

	// Ctrl+click acts this many times (the workshop's recipes).
	public Func<int> CtrlRepeat;

	// Small "+" and "-" over the button's top (the mod's limits: workers per task, quarry depth...), called with 1
	// or -1; true when the value changed.
	public Func<int, bool> Step;

	public string LabelText()
	{
		return (Label != null) ? Label() : string.Empty;
	}

	public void Click(bool shift, bool ctrl)
	{
		if (OnClick == null || (Enabled != null && !Enabled()))
		{
			return;
		}
		int times = (ctrl && CtrlRepeat != null) ? Mathf.Max(1, CtrlRepeat()) : 1;
		for (int i = 0; i < times; i++)
		{
			OnClick(shift, ctrl);
		}
		GameUiState.Announce();
	}
}

// A value with - and + beside it (the mod's limits: workers per task, quarry depth, loop count...).
internal sealed class UiStepper : UiItem
{
	public Func<string> Label;

	public Func<string> Value;

	// Called with -1 or +1 (-10 or +10 with Shift).
	public Action<int> Step;

	public Func<string> Tooltip;
}

// A line of text (the top bar's task name, the population count...).
internal sealed class UiText : UiItem
{
	public Func<string> Text;

	public Func<Color> Color;

	public int FontSize = LuiStyle.FontSize;

	// The title face (a popup's heading) instead of the bold one.
	public bool Title;
}

// What a workshop is busy with: a picture in a frame, a small caption over a name, a line under it (rich text) and
// a bar that fills up (hidden below 0). A click acts (removes the recipe from the queue).
internal sealed class UiStatus : UiItem
{
	public Func<Sprite> Icon;

	public Func<string> Caption;

	public Func<string> Title;

	public Func<string> Detail;

	// 0 to 1; below 0 no bar.
	public Func<float> Progress;

	public Func<string> Tooltip;

	public Action OnClick;
}

// One of a few named choices side by side, the chosen one lit (a workshop's priority).
internal sealed class UiChoice : UiItem
{
	public Func<string> Label;

	public string[] Options;

	public Func<int> Selected;

	public Action<int> Select;
}

// A text field with a button beside it (the blueprint library's name and save).
internal sealed class UiInput : UiItem
{
	// The text shown while the field is not being typed in.
	public Func<string> Text;

	// Told the text when typing ends (Enter or leaving the field), and before the button acts.
	public Action<string> Set;

	public UiButton Button;
}

// A wide row: a picture on the left and a text beside it (the blueprint library's saved blueprints).
internal sealed class UiRow : UiItem
{
	public Func<Sprite> Icon;

	public Func<Color> IconColor;

	public Func<string> Text;

	public Func<bool> Highlight;

	public Action OnClick;
}

// An article page (the help): the page's file name, and what its links do.
internal sealed class UiArticle : UiItem
{
	public string Folder = "Help";

	public Func<string> Page;

	public Action<string> Select;

	public float Width = 252f;
}

// A bar that fills up (the firefly nest's progress).
internal sealed class UiProgress : UiItem
{
	// 0 to 1.
	public Func<float> Value;

	public Func<string> Text;
}

// A setting that is on or off, as a row: its label and a check box (the world editor's options).
internal sealed class UiToggle : UiItem
{
	public Func<string> Label;

	public Func<bool> Value;

	public Action<bool> Set;

	// Greyed out (and not clickable) when false.
	public Func<bool> Enabled;

	// The row's width (the world editor's panels are 320 wide).
	public float Width = 296f;
}

// A value on a slider: its label and the value as text over the slider (the world editor's time, grid opacity).
internal sealed class UiSlider : UiItem
{
	public Func<string> Label;

	public Func<float> Min;

	public Func<float> Max;

	public Func<float> Value;

	// Told each new value while the handle is dragged.
	public Action<float> Set;

	// The value as shown (a time as 12:30, a percentage).
	public Func<float, string> Format;

	public bool WholeNumbers;

	public Func<bool> Enabled;

	public float Width = 296f;
}

// A picture (a texture, part of it) at a fixed size: the world editor's map preview.
internal sealed class UiPicture : UiItem
{
	public Func<Texture> Texture;

	// The part of the texture shown (x, y, width, height in 0 to 1); null for all of it.
	public Func<Rect> Uv;

	public float Width = 256f;

	public float Height = 256f;
}

// How a section lays out its buttons: a grid of squares, or a wide row each (icon, label, detail).
internal enum UiLayout
{
	Grid,
	Rows
}

// A titled group of items, laid out as a grid of buttons (or rows for the other items).
internal sealed class UiSection
{
	public string Id;

	public UiLayout Layout = UiLayout.Grid;

	public Func<string> Title;

	public Func<bool> Visible;

	public float CellSize = 64f;

	public readonly List<UiItem> Items = new List<UiItem>();

	public bool IsVisible()
	{
		return Visible == null || Visible();
	}

	public T Add<T>(T item) where T : UiItem
	{
		Items.Add(item);
		return item;
	}

	// After the item with that id (or at the end).
	public T Insert<T>(string afterId, T item) where T : UiItem
	{
		int at = Items.FindIndex((UiItem i) => i.Id == afterId);
		Items.Insert((at < 0) ? Items.Count : at + 1, item);
		return item;
	}

	// Before the item with that id (or at the end).
	public T InsertBefore<T>(string beforeId, T item) where T : UiItem
	{
		int at = Items.FindIndex((UiItem i) => i.Id == beforeId);
		Items.Insert((at < 0) ? Items.Count : at, item);
		return item;
	}
}

// A sidebar (the new-task menu, the catalogues, the settings): sections under a title, opened from a bar button.
internal sealed class UiPanel
{
	public string Id;

	public Func<string> Title;

	public readonly List<UiSection> Sections = new List<UiSection>();

	// Told when the panel opens and closes (hotkey modes, sounds).
	public Action OnOpen;

	public Action OnClose;

	// The group it belongs to (at most one panel of an exclusive group is open).
	public UiGroup Group;

	public bool IsOpen
	{
		get { return Group != null && Group.Open == this; }
	}

	public UiSection Section(string id, Func<string> title)
	{
		UiSection section = new UiSection { Id = id, Title = title };
		Sections.Add(section);
		return section;
	}

	// A section placed after the one with that id (or at the end).
	public UiSection SectionAfter(string afterId, string id, Func<string> title)
	{
		UiSection section = new UiSection { Id = id, Title = title };
		int at = Sections.FindIndex((UiSection s) => s.Id == afterId);
		Sections.Insert((at < 0) ? Sections.Count : at + 1, section);
		return section;
	}

	public UiSection Find(string id)
	{
		return Sections.Find((UiSection s) => s.Id == id);
	}

	public void Open()
	{
		Group?.SetOpen(this);
	}

	public void Close()
	{
		if (IsOpen)
		{
			Group.SetOpen(null);
		}
	}

	public void Toggle()
	{
		if (IsOpen)
		{
			Close();
		}
		else
		{
			Open();
		}
	}
}

// Panels of which at most one is open (GameMenu's exclusive left and right groups).
internal sealed class UiGroup
{
	public UiPanel Open { get; private set; }

	// Told after a panel opened or closed.
	public event Action Changed;

	public void SetOpen(UiPanel panel)
	{
		if (Open == panel)
		{
			return;
		}
		UiPanel before = Open;
		Open = panel;
		if (before != null && before.OnClose != null)
		{
			Run(before.OnClose, "close " + before.Id);
		}
		if (panel != null && panel.OnOpen != null)
		{
			Run(panel.OnOpen, "open " + panel.Id);
		}
		if (Changed != null)
		{
			Run(Changed, "group changed");
		}
		GameUiState.Announce();
	}

	public void CloseAll()
	{
		SetOpen(null);
	}

	private static void Run(Action action, string what)
	{
		try
		{
			action();
		}
		catch (Exception ex)
		{
			Plugin.Log.LogError("GameUi: " + what + ": " + ex);
		}
	}
}
