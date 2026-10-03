using System;
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

	// The game's UI font, taken from any Text in the scene.
	public static Font Font
	{
		get
		{
			if (_font == null)
			{
				Text any = UnityEngine.Object.FindObjectOfType<Text>();
				_font = (any != null) ? any.font : Resources.GetBuiltinResource<Font>("Arial.ttf");
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
