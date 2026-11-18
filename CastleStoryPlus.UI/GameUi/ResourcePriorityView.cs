using System.Collections.Generic;
using Brix.Game;
using Brix.Game.Network;
using CastleStoryPlus.GameUi;
using CastleStoryPlus.LuaUi;
using CastleStoryPlus.WorkerAI;
using UnityEngine;
using UnityEngine.UI;

namespace CastleStoryPlus.UI;

// The resource priority table of the C# in-game menu: left of the resource list (or in its place while the list is
// hidden), one row per resource with "-" and "+" round the priority (0 = never, 1 to 5, 3 neutral; see
// ResourcePriority). Shown with the "Priorities" button on the right-hand bar. Read four times a second; a change is
// set at once and sent to the host.
internal class ResourcePriorityView : MonoBehaviour
{
	private const float Width = 200f;

	private const float RowHeight = 24f;

	private const float Gap = 8f;

	private const float ReadSeconds = 0.25f;

	private static readonly Color NameColor = new Color(0.85f, 0.85f, 0.85f, 1f);

	private GameMenuModel _menu;

	private RectTransform _rect;

	private Image _background;

	private readonly List<Text> _values = new List<Text>();

	private float _readAt = -1f;

	internal static void Create(Transform parent, GameMenuModel menu)
	{
		RectTransform rect = UiKit.CreateRect("CS resource priorities", parent);
		rect.anchorMin = rect.anchorMax = new Vector2(1f, 1f);
		rect.pivot = new Vector2(1f, 1f);
		rect.sizeDelta = new Vector2(Width, 0f);
		Image background = rect.gameObject.AddComponent<Image>();
		UiTheme.Round(background, LuiStyle.BgDark, UiTheme.PanelRadius);
		background.raycastTarget = false;
		VerticalLayoutGroup layout = rect.gameObject.AddComponent<VerticalLayoutGroup>();
		layout.padding = new RectOffset(8, 8, 6, 6);
		layout.spacing = 1f;
		layout.childControlWidth = true;
		layout.childControlHeight = true;
		layout.childForceExpandWidth = true;
		layout.childForceExpandHeight = false;
		ContentSizeFitter fitter = rect.gameObject.AddComponent<ContentSizeFitter>();
		fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
		ResourcePriorityView view = rect.gameObject.AddComponent<ResourcePriorityView>();
		view._menu = menu;
		view._rect = rect;
		view._background = background;
		view.Build();
	}

	private void Build()
	{
		Text header = LuiWidgets.Label(_rect, "PRIORITY (0 = never)", 12, new Color(0.6f, 0.6f, 0.6f, 1f), TextAnchor.MiddleLeft, LuiStyle.Regular);
		LayoutElement element = header.gameObject.AddComponent<LayoutElement>();
		element.minHeight = RowHeight;
		element.preferredHeight = RowHeight;
		ResourcePriority.Kind[] kinds = ResourcePriority.Kinds;
		for (int i = 0; i < kinds.Length; i++)
		{
			int kind = i;
			Transform row = UiKit.CreateRow(_rect, RowHeight);
			Text name = LuiWidgets.Label(row, kinds[i].Name, 14, NameColor, TextAnchor.MiddleLeft, LuiStyle.Regular);
			UiKit.Flexible(name.gameObject);
			UiKit.CreateButton(row, "-", 24f, () => Step(kind, -1));
			// Numbers are right-aligned.
			Text value = LuiWidgets.Label(row, string.Empty, 14, LuiStyle.CastleYellow, TextAnchor.MiddleRight, LuiStyle.Regular);
			UiKit.Fixed(value.gameObject, 22f, RowHeight);
			_values.Add(value);
			UiKit.CreateButton(row, "+", 24f, () => Step(kind, 1));
		}
	}

	private static void Step(int kind, int direction)
	{
		if (GameUiState.Paused || !ResourcePriority.Enabled)
		{
			return;
		}
		Faction faction = (User.LocalUser != null) ? User.LocalUser.faction : null;
		ResourcePriority.Change(kind, Mathf.Clamp(ResourcePriority.Get(faction, kind) + direction, ResourcePriority.Never, ResourcePriority.Most));
	}

	private void Update()
	{
		bool shown = ResourcePriority.Shown;
		if (_background.enabled != shown)
		{
			_background.enabled = shown;
			for (int i = 0; i < transform.childCount; i++)
			{
				transform.GetChild(i).gameObject.SetActive(shown);
			}
			_readAt = -1f;
		}
		if (!shown)
		{
			return;
		}
		// Left of the resource list while it is shown; both left of the right-hand bar, or of the right-hand sidebar.
		float x = (_menu.Right.Open != null) ? -328f : -56f;
		if (ResourceList.Enabled && ResourceList.Shown)
		{
			x -= ResourceListView.Width + Gap;
		}
		_rect.anchoredPosition = new Vector2(x, -64f);
		if (_readAt >= 0f && Time.unscaledTime - _readAt < ReadSeconds)
		{
			return;
		}
		_readAt = Time.unscaledTime;
		Faction faction = (User.LocalUser != null) ? User.LocalUser.faction : null;
		for (int i = 0; i < _values.Count; i++)
		{
			int priority = ResourcePriority.Get(faction, i);
			_values[i].text = priority.ToString();
			_values[i].color = (priority == ResourcePriority.Never) ? LuiStyle.CastleRed : ((priority == ResourcePriority.Neutral) ? NameColor : LuiStyle.CastleYellow);
		}
	}
}
