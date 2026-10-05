using System;
using Brix.Components;
using Brix.Game.Utils;
using UnityEngine;
using UnityEngine.UI;

namespace CastleStoryPlus.UI;

// uGUI helpers and colours shared by the mod's code-built windows (call to arms, mod settings).
internal static class UiKit
{
	public static readonly Color Background = new Color(0.07f, 0.07f, 0.09f, 0.94f);

	public static readonly Color Yellow = new Color(1f, 0.8f, 0.25f, 1f);

	public static readonly Color Grey = new Color(0.6f, 0.6f, 0.6f, 1f);

	public static readonly Color TextColor = new Color(0.88f, 0.88f, 0.88f, 1f);

	private static Font _font;

	// The game's UI font (Proxima Nova, as the game's own menus use). Not just any Text in the scene: in a game that
	// can be a font that draws nothing at the mod's sizes, which left the call to arms window without any text.
	public static Font Font
	{
		get
		{
			if (_font == null)
			{
				LayoutPanelManager fonts = BrixSingleton<LayoutPanelManager>.Instance;
				_font = (fonts != null) ? fonts.ProximaNovaRegular : null;
				if (_font == null)
				{
					_font = Resources.GetBuiltinResource<Font>("Arial.ttf");
				}
			}
			return _font;
		}
	}

	public static GameObject CreateCanvas(string name, Transform parent, int sortingOrder)
	{
		GameObject canvasGo = new GameObject(name, typeof(RectTransform));
		canvasGo.transform.SetParent(parent, worldPositionStays: false);
		Canvas canvas = canvasGo.AddComponent<Canvas>();
		canvas.renderMode = RenderMode.ScreenSpaceOverlay;
		canvas.sortingOrder = sortingOrder;
		CanvasScaler scaler = canvasGo.AddComponent<CanvasScaler>();
		scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
		scaler.referenceResolution = new Vector2(1920f, 1080f);
		scaler.matchWidthOrHeight = 1f;
		canvasGo.AddComponent<GraphicRaycaster>();
		return canvasGo;
	}

	// A vertical window that grows with its content.
	public static GameObject CreateWindow(Transform parent, float width)
	{
		GameObject window = CreatePanel("Window", parent);
		RectTransform windowRect = window.GetComponent<RectTransform>();
		windowRect.sizeDelta = new Vector2(width, 0f);
		VerticalLayoutGroup layout = window.AddComponent<VerticalLayoutGroup>();
		layout.padding = new RectOffset(14, 14, 10, 14);
		layout.spacing = 4f;
		layout.childControlWidth = true;
		layout.childControlHeight = true;
		layout.childForceExpandWidth = true;
		layout.childForceExpandHeight = false;
		ContentSizeFitter fitter = window.AddComponent<ContentSizeFitter>();
		fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
		return window;
	}

	// A vertically scrolling list. Rows go into the returned content; set the visible height with the LayoutElement.
	public static Transform CreateScrollList(Transform parent, out LayoutElement size)
	{
		RectTransform scroll = CreateRect("ScrollList", parent);
		// Transparent, but a raycast target so the mouse wheel scrolls anywhere over the list.
		Image background = scroll.gameObject.AddComponent<Image>();
		background.color = new Color(0f, 0f, 0f, 0.15f);
		size = scroll.gameObject.AddComponent<LayoutElement>();
		RectTransform viewport = CreateRect("Viewport", scroll);
		SetRect(viewport, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
		viewport.gameObject.AddComponent<RectMask2D>();
		RectTransform content = CreateRect("Content", viewport);
		content.anchorMin = new Vector2(0f, 1f);
		content.anchorMax = new Vector2(1f, 1f);
		content.pivot = new Vector2(0.5f, 1f);
		content.anchoredPosition = Vector2.zero;
		content.sizeDelta = Vector2.zero;
		VerticalLayoutGroup layout = content.gameObject.AddComponent<VerticalLayoutGroup>();
		layout.spacing = 2f;
		layout.padding = new RectOffset(4, 4, 2, 2);
		layout.childControlWidth = true;
		layout.childControlHeight = true;
		layout.childForceExpandWidth = true;
		layout.childForceExpandHeight = false;
		ContentSizeFitter fitter = content.gameObject.AddComponent<ContentSizeFitter>();
		fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
		ScrollRect scrollRect = scroll.gameObject.AddComponent<ScrollRect>();
		scrollRect.horizontal = false;
		scrollRect.vertical = true;
		scrollRect.movementType = ScrollRect.MovementType.Clamped;
		scrollRect.scrollSensitivity = 24f;
		scrollRect.viewport = viewport;
		scrollRect.content = content;
		return content;
	}

	public static RectTransform CreateRect(string name, Transform parent)
	{
		GameObject go = new GameObject(name, typeof(RectTransform));
		go.transform.SetParent(parent, worldPositionStays: false);
		return go.GetComponent<RectTransform>();
	}

	public static void SetRect(RectTransform rect, Vector2 anchorMin, Vector2 anchorMax, Vector2 position, Vector2 size)
	{
		rect.anchorMin = anchorMin;
		rect.anchorMax = anchorMax;
		rect.anchoredPosition = position;
		rect.sizeDelta = size;
	}

	public static GameObject CreatePanel(string name, Transform parent)
	{
		RectTransform rect = CreateRect(name, parent);
		Image image = rect.gameObject.AddComponent<Image>();
		image.color = Background;
		return rect.gameObject;
	}

	public static Transform CreateRow(Transform parent, float height)
	{
		RectTransform rect = CreateRect("Row", parent);
		HorizontalLayoutGroup layout = rect.gameObject.AddComponent<HorizontalLayoutGroup>();
		layout.spacing = 6f;
		layout.childAlignment = TextAnchor.MiddleLeft;
		layout.childControlWidth = true;
		layout.childControlHeight = true;
		layout.childForceExpandWidth = false;
		layout.childForceExpandHeight = false;
		LayoutElement element = rect.gameObject.AddComponent<LayoutElement>();
		element.minHeight = height;
		element.preferredHeight = height;
		return rect;
	}

	public static Text CreateText(Transform parent, string text, int size, Color color, TextAnchor alignment)
	{
		Text label = CreateRect("Text", parent).gameObject.AddComponent<Text>();
		label.font = Font;
		label.fontSize = size;
		label.color = color;
		label.alignment = alignment;
		label.text = text;
		label.raycastTarget = false;
		label.horizontalOverflow = HorizontalWrapMode.Overflow;
		return label;
	}

	public static Button CreateButton(Transform parent, string text, float width, Action onClick)
	{
		RectTransform rect = CreateRect("Button", parent);
		Image image = rect.gameObject.AddComponent<Image>();
		image.color = Color.white;
		Button button = rect.gameObject.AddComponent<Button>();
		ColorBlock colors = button.colors;
		colors.normalColor = new Color(0.22f, 0.22f, 0.26f, 1f);
		colors.highlightedColor = new Color(0.34f, 0.34f, 0.4f, 1f);
		colors.pressedColor = new Color(0.5f, 0.42f, 0.18f, 1f);
		colors.disabledColor = new Color(0.14f, 0.14f, 0.16f, 0.7f);
		colors.colorMultiplier = 1f;
		button.colors = colors;
		button.targetGraphic = image;
		button.onClick.AddListener(() => onClick());
		Text label = CreateText(rect, text, 13, TextColor, TextAnchor.MiddleCenter);
		SetRect(label.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
		if (width > 0f)
		{
			Fixed(rect.gameObject, width, 24f);
		}
		else
		{
			LayoutElement element = rect.gameObject.AddComponent<LayoutElement>();
			element.minHeight = 24f;
			element.preferredHeight = 24f;
		}
		return button;
	}

	// A one-line text field with a grey hint while it is empty.
	public static InputField CreateInputField(Transform parent, string placeholder, float width)
	{
		RectTransform rect = CreateRect("InputField", parent);
		Image image = rect.gameObject.AddComponent<Image>();
		image.color = new Color(0.14f, 0.14f, 0.17f, 1f);
		Text hint = CreateText(rect, placeholder, 14, Grey, TextAnchor.MiddleLeft);
		hint.fontStyle = FontStyle.Italic;
		SetRect(hint.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, new Vector2(-12f, 0f));
		Text text = CreateText(rect, string.Empty, 14, TextColor, TextAnchor.MiddleLeft);
		text.supportRichText = false;
		text.horizontalOverflow = HorizontalWrapMode.Wrap;
		SetRect(text.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, new Vector2(-12f, 0f));
		InputField field = rect.gameObject.AddComponent<InputField>();
		field.targetGraphic = image;
		field.textComponent = text;
		field.placeholder = hint;
		field.lineType = InputField.LineType.SingleLine;
		field.caretColor = Yellow;
		Fixed(rect.gameObject, width, 26f);
		return field;
	}

	public static void Fixed(GameObject go, float width, float height)
	{
		LayoutElement element = go.GetComponent<LayoutElement>();
		if (element == null)
		{
			element = go.AddComponent<LayoutElement>();
		}
		element.minWidth = width;
		element.preferredWidth = width;
		element.minHeight = height;
		element.preferredHeight = height;
		element.flexibleWidth = 0f;
	}

	public static void Flexible(GameObject go)
	{
		LayoutElement element = go.GetComponent<LayoutElement>();
		if (element == null)
		{
			element = go.AddComponent<LayoutElement>();
		}
		element.flexibleWidth = 1f;
		element.minWidth = 0f;
	}
}
