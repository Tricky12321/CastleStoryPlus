using System;
using System.Collections.Generic;
using Brix.Assets;
using Brix.UI.Icons;
using CastleStoryPlus.UI;
using MoonSharp.Interpreter;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace CastleStoryPlus.LuaUi;

// One of the game's articles (Info/Lua/LUI/Article: the tooltips and the help pages) drawn in C#. An article is a
// Lua function answering a list of items ({ type = "Label", Text = ||... }, ...), which the game turns into a menu
// with one panel per item (Menu_Article.lua, the Panel_<type>.lua files). Here the items are read and drawn the
// same way, item by item: labels, subtitles, dividers, spacers, images, a recipe's materials and the help's links.
// Texts are closures (a quarry's limits, a recipe's name), so they are read again on Refresh.
internal class ArticleView
{
	private class Entry
	{
		public Text Label;

		public Table Item;

		public Image Background;

		public bool Objective;
	}

	private readonly List<Entry> _entries = new List<Entry>();

	private readonly Action<DynValue> _select;

	public RectTransform Root { get; private set; }

	private ArticleView(Action<DynValue> select)
	{
		_select = select;
	}

	// Draws the article at path (a table such as {"Task", "Build", "Goals"}) into parent, width pixels wide. select
	// is what a link to another article does (the help's own links); null where links make no sense (tooltips).
	public static ArticleView Build(Transform parent, Script script, DynValue path, float width, Action<DynValue> select)
	{
		ArticleView view = new ArticleView(select);
		RectTransform root = UiKit.CreateRect("Article", parent);
		VerticalLayoutGroup layout = root.gameObject.AddComponent<VerticalLayoutGroup>();
		layout.spacing = 0f;
		layout.childControlWidth = true;
		layout.childControlHeight = true;
		layout.childForceExpandWidth = true;
		layout.childForceExpandHeight = false;
		LayoutElement size = root.gameObject.AddComponent<LayoutElement>();
		size.minWidth = width;
		size.preferredWidth = width;
		view.Root = root;
		DynValue items = ArticleLua.Items(script, path);
		if (items.Type != DataType.Table)
		{
			LuaBridge.LogOnce("article", "no article at " + ArticleLua.Describe(path));
			return view;
		}
		Table list = items.Table;
		for (int i = 1; i <= list.Length; i++)
		{
			DynValue item = list.Get(i);
			if (item.Type == DataType.Table)
			{
				view.Add(script, item.Table, width);
			}
		}
		view.Refresh();
		return view;
	}

	private void Add(Script script, Table item, float width)
	{
		string type = LuaBridge.String(LuaBridge.Get(item, "type"), string.Empty);
		switch (type)
		{
		case "Label":
			AddText(item, 14, LuiStyle.Gray, TextAnchor.UpperLeft, null, new RectOffset(0, 0, 4, 4));
			break;
		case "MiniLabel":
			AddText(item, 12, LuiStyle.Gray, TextAnchor.UpperLeft, null, new RectOffset(0, 0, 2, 2));
			break;
		case "Subtitle":
			AddText(item, 18, LuiStyle.CastleYellow, ArticleLua.Centered(script, item) ? TextAnchor.MiddleCenter : TextAnchor.MiddleLeft, LuiStyle.Title, new RectOffset(0, 0, 6, 4));
			break;
		case "SquaredLabel":
			AddSquaredLabel(item);
			break;
		case "Divider":
			AddText(item, 13, LuiStyle.CastleYellow, TextAnchor.MiddleLeft, LuiStyle.Bold, new RectOffset(0, 0, 6, 2));
			LuiWidgets.Divider(Root, vertical: false);
			break;
		case "Spacer":
			AddSpace(LuaBridge.Number(LuaBridge.Get(item, "height"), 8f));
			break;
		case "Splitter":
			AddSpace(4f);
			LuiWidgets.Divider(Root, vertical: false);
			AddSpace(4f);
			break;
		case "RawImage":
			AddImage(item, width);
			break;
		case "Recipe":
			AddRecipe(script, item);
			break;
		case "ArticleLink":
			AddLink(script, item, objective: false);
			break;
		case "ObjectiveButton":
			AddLink(script, item, objective: true);
			break;
		default:
			LuaBridge.LogOnce("article", "no C# view for article items of type \"" + type + "\"");
			break;
		}
	}

	private Text NewText(Transform parent, int size, Color color, TextAnchor alignment, Font font)
	{
		Text text = LuiWidgets.Label(parent, string.Empty, size, color, alignment, font);
		text.horizontalOverflow = HorizontalWrapMode.Wrap;
		text.verticalOverflow = VerticalWrapMode.Overflow;
		text.supportRichText = true;
		return text;
	}

	// A text in a row of its own, padded as the game's panel pads it.
	private void AddText(Table item, int size, Color color, TextAnchor alignment, Font font, RectOffset padding)
	{
		RectTransform row = Row(padding);
		Text text = NewText(row, size, color, alignment, font);
		_entries.Add(new Entry { Label = text, Item = item });
	}

	private RectTransform Row(RectOffset padding)
	{
		RectTransform row = UiKit.CreateRect("Row", Root);
		VerticalLayoutGroup layout = row.gameObject.AddComponent<VerticalLayoutGroup>();
		layout.padding = padding;
		layout.childControlWidth = true;
		layout.childControlHeight = true;
		layout.childForceExpandWidth = true;
		layout.childForceExpandHeight = false;
		return row;
	}

	private void AddSquaredLabel(Table item)
	{
		RectTransform box = Row(new RectOffset(8, 8, 6, 6));
		Image background = box.gameObject.AddComponent<Image>();
		background.color = new Color(0f, 0f, 0f, 0.5f);
		background.raycastTarget = false;
		LayoutElement size = box.gameObject.AddComponent<LayoutElement>();
		size.minHeight = LuaBridge.Number(LuaBridge.Get(item, "height"), 100f);
		Text text = NewText(box, 14, LuiStyle.Gray, TextAnchor.MiddleLeft, null);
		_entries.Add(new Entry { Label = text, Item = item });
	}

	private void AddSpace(float height)
	{
		RectTransform space = UiKit.CreateRect("Space", Root);
		LayoutElement size = space.gameObject.AddComponent<LayoutElement>();
		size.minHeight = height;
		size.preferredHeight = height;
	}

	// The help's pictures (Info/Lua/LUI/Data/Tutorial/Textures), at most as wide as the article, centred.
	private void AddImage(Table item, float width)
	{
		string path = LuaBridge.String(LuaBridge.Get(item, "path"), null);
		Texture2D texture = null;
		if (!string.IsNullOrEmpty(path))
		{
			try
			{
				texture = SubAsset_Texture2D.GetAssetSync(path, clearOnSceneChanged: true);
			}
			catch (Exception ex)
			{
				LuaBridge.LogOnce("article image", path + ": " + ex.Message);
			}
		}
		if (texture == null)
		{
			return;
		}
		RectTransform row = UiKit.CreateRect("Image", Root);
		HorizontalLayoutGroup layout = row.gameObject.AddComponent<HorizontalLayoutGroup>();
		layout.childAlignment = TextAnchor.MiddleCenter;
		layout.padding = new RectOffset(0, 0, 4, 4);
		layout.childControlWidth = true;
		layout.childControlHeight = true;
		layout.childForceExpandWidth = false;
		layout.childForceExpandHeight = false;
		float shown = Mathf.Min(LuaBridge.Number(LuaBridge.Get(item, "width"), 224f), width);
		float height = shown * texture.height / Mathf.Max(1f, texture.width);
		RectTransform picture = UiKit.CreateRect("Picture", row);
		RawImage image = picture.gameObject.AddComponent<RawImage>();
		image.texture = texture;
		image.raycastTarget = false;
		UiKit.Fixed(picture.gameObject, shown, height);
		picture.gameObject.AddComponent<Outline>().effectColor = LuiStyle.CastleYellow;
	}

	// A recipe's materials: one row per material, its icon and "2x name" (Panel_Recipe.lua).
	private void AddRecipe(Script script, Table item)
	{
		DynValue rows = ArticleLua.Recipe(script, item);
		if (rows.Type != DataType.Table)
		{
			return;
		}
		for (int i = 1; i <= rows.Table.Length; i++)
		{
			DynValue row = rows.Table.Get(i);
			if (row.Type != DataType.Table)
			{
				continue;
			}
			Transform line = UiKit.CreateRow(Root, 34f);
			((HorizontalLayoutGroup)line.GetComponent<HorizontalLayoutGroup>()).padding = new RectOffset(8, 0, 1, 1);
			RectTransform icon = UiKit.CreateRect("Icon", line);
			Image image = icon.gameObject.AddComponent<Image>();
			image.sprite = LuaBridge.Sprite(LuaBridge.Get(row.Table, "icon"));
			image.enabled = image.sprite != null;
			image.preserveAspect = true;
			image.raycastTarget = false;
			UiKit.Fixed(icon.gameObject, 32f, 32f);
			Text text = NewText(line, 12, LuiStyle.CastleYellow, TextAnchor.MiddleLeft, null);
			text.text = LuaBridge.String(LuaBridge.Get(row.Table, "text"), string.Empty);
			UiKit.Flexible(text.gameObject);
		}
	}

	// A link to another help article (ArticleLink), or the tutorial's "next" button, which only works once its
	// objective is done (ObjectiveButton): an arrow and a label on a dark strip.
	private void AddLink(Script script, Table item, bool objective)
	{
		Transform line = UiKit.CreateRow(Root, objective ? 32f : 26f);
		HorizontalLayoutGroup layout = (HorizontalLayoutGroup)line.GetComponent<HorizontalLayoutGroup>();
		layout.padding = new RectOffset(objective ? 20 : 20, objective ? 20 : 0, 4, 4);
		Image background = line.gameObject.AddComponent<Image>();
		background.color = new Color(0f, 0f, 0f, 0.5f);
		RectTransform icon = UiKit.CreateRect("Arrow", line);
		Image arrow = icon.gameObject.AddComponent<Image>();
		arrow.sprite = IconKeys.UI_Right.Get64();
		arrow.color = LuiStyle.CastleYellow;
		arrow.preserveAspect = true;
		arrow.raycastTarget = false;
		UiKit.Fixed(icon.gameObject, 16f, 16f);
		Text text = NewText(line, objective ? 14 : 12, LuiStyle.CastleYellow, TextAnchor.MiddleLeft, LuiStyle.Bold);
		UiKit.Flexible(text.gameObject);
		Entry entry = new Entry { Label = text, Item = item, Background = background, Objective = objective };
		_entries.Add(entry);
		ArticleButton button = line.gameObject.AddComponent<ArticleButton>();
		button.Background = background;
		button.Enabled = () => !entry.Objective || ArticleLua.Done(script, item);
		button.OnClick = () =>
		{
			if (objective)
			{
				LuaBridge.Value(item, "GoToNextTooltip");
			}
			string id = LuaBridge.String(LuaBridge.Get(item, "article_id"), null);
			if (_select != null && !string.IsNullOrEmpty(id))
			{
				_select(ArticleLua.Path(script, id));
			}
		};
	}

	// Reads every text again (and the tutorial buttons' state).
	public void Refresh()
	{
		foreach (Entry entry in _entries)
		{
			if (entry.Label == null)
			{
				continue;
			}
			string text;
			Color color;
			if (entry.Background != null)
			{
				Script script = entry.Item.OwnerScript;
				if (entry.Objective)
				{
					text = LuaBridge.String(LuaBridge.Value(entry.Item, "ButtonText"), "x");
					color = ArticleLua.Done(script, entry.Item) ? LuiStyle.CastleYellow : LuiStyle.Gray;
				}
				else
				{
					text = ArticleLua.Title(script, ArticleLua.Path(script, LuaBridge.String(LuaBridge.Get(entry.Item, "article_id"), string.Empty)));
					color = LuiStyle.CastleYellow;
				}
			}
			else
			{
				text = LuaBridge.String(LuaBridge.Value(entry.Item, "Text"), string.Empty);
				DynValue luaColor = LuaBridge.Value(entry.Item, "Color");
				color = luaColor.IsNil() ? entry.Label.color : LuaBridge.Color(luaColor, entry.Label.color);
			}
			text = LuaBridge.Localized(text);
			if (entry.Label.text != text)
			{
				entry.Label.text = text;
			}
			entry.Label.color = color;
		}
	}
}

// A clickable strip in an article (a link, the tutorial's next button), lighter while hovered.
internal class ArticleButton : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerClickHandler
{
	private static readonly Color Normal = new Color(0f, 0f, 0f, 0.5f);

	public Image Background;

	public Func<bool> Enabled;

	public Action OnClick;

	public void OnPointerEnter(PointerEventData eventData)
	{
		if (Enabled == null || Enabled())
		{
			Background.color = LuiStyle.FgHover;
		}
	}

	public void OnPointerExit(PointerEventData eventData)
	{
		Background.color = Normal;
	}

	public void OnPointerClick(PointerEventData eventData)
	{
		if (eventData.button != PointerEventData.InputButton.Left || OnClick == null || (Enabled != null && !Enabled()))
		{
			return;
		}
		OnClick();
	}

	private void OnDisable()
	{
		if (Background != null)
		{
			Background.color = Normal;
		}
	}
}

// What the articles need from the game's Lua (Data.Article, Data.Recipe, Meta), as small Lua functions compiled
// once into the menu's script, so they read the game's tables exactly as its own panels do.
internal static class ArticleLua
{
	private const string Key = "CastleStoryPlus.Articles";

	private const string Code = @"
local H = {}
local paths = {}
H.items = function(path)
	local f = Data.Article:GetFunc(path)
	if not f then return nil end
	return f()
end
H.path = function(id)
	paths[id] = paths[id] or { id }
	return paths[id]
end
H.title = function(path)
	return Data.Article:GetTitle(path)
end
H.done = function(item)
	return item.objective ~= nil and item.objective.Completed == true
end
H.centered = function(item)
	return item.alignment ~= nil and item.alignment == Alignment.MiddleCenter
end
H.describe = function(path)
	return Data.Article:GetString(path)
end
H.recipe = function(item)
	local rows = {}
	local infos = Data.Recipe:GetResourceInfos(item.recipe, item.showInput, item.showOutput, true)
	for i = 1, #infos do
		local resource = infos[i][1]
		local count = infos[i][2]
		local meta = Meta.Resource[resource]
		local name = meta and meta.Name() or '?'
		local text = name
		if item.showCount then text = tostring(count) .. 'x <color=#' .. HexGray .. '>' .. name .. '</color>' end
		rows[#rows + 1] = { text = text, icon = meta and meta.Icon() or nil }
	end
	return rows
end
return H
";

	private static Table Helpers(Script script)
	{
		if (script == null)
		{
			return null;
		}
		DynValue helpers = script.Registry.Get(Key);
		if (helpers.Type != DataType.Table)
		{
			try
			{
				helpers = script.DoString(Code);
			}
			catch (InterpreterException ex)
			{
				LuaBridge.LogOnce("article helpers", ex.DecoratedMessage ?? ex.Message);
				return null;
			}
			script.Registry.Set(Key, helpers);
		}
		return (helpers.Type == DataType.Table) ? helpers.Table : null;
	}

	private static DynValue Call(Script script, string name, DynValue arg)
	{
		Table helpers = Helpers(script);
		return (helpers != null) ? LuaBridge.Call(helpers.Get(name), "article " + name, arg) : DynValue.Nil;
	}

	public static DynValue Items(Script script, DynValue path)
	{
		return Call(script, "items", path);
	}

	// The game's path table for a help article's id ({"MakingBricks"}), the same table every time.
	public static DynValue Path(Script script, string id)
	{
		return Call(script, "path", DynValue.NewString(id));
	}

	public static string Title(Script script, DynValue path)
	{
		return LuaBridge.String(Call(script, "title", path), string.Empty);
	}

	public static bool Done(Script script, Table item)
	{
		return LuaBridge.Bool(Call(script, "done", DynValue.NewTable(item)), false);
	}

	public static bool Centered(Script script, Table item)
	{
		return LuaBridge.Bool(Call(script, "centered", DynValue.NewTable(item)), false);
	}

	public static DynValue Recipe(Script script, Table item)
	{
		return Call(script, "recipe", DynValue.NewTable(item));
	}

	public static string Describe(DynValue path)
	{
		Script script = (path.Type == DataType.Table) ? path.Table.OwnerScript : null;
		return LuaBridge.String(Call(script, "describe", path), "?");
	}
}
