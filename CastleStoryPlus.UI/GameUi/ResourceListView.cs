using System.Collections.Generic;
using Brix.Lua;
using CastleStoryPlus.GameUi;
using CastleStoryPlus.LuaUi;
using UnityEngine;
using UnityEngine.UI;

namespace CastleStoryPlus.UI;

// The resource list of the C# in-game menu, as Panel_StorageList.lua drew it: in the top-right corner left of the
// right-hand bar (left of a right-hand sidebar while one is open), the stockpile room used and free, then one row
// per resource the team has stocked once (ResourceSeen), red at 0. Read twice a second.
internal class ResourceListView : MonoBehaviour
{
	internal const float Width = 220f;

	private const float RowHeight = 24f;

	private const float IconSize = 22f;

	private const float ReadSeconds = 0.5f;

	private static readonly Color NameColor = new Color(0.85f, 0.85f, 0.85f, 1f);

	private static readonly Color Orange = new Color(1f, 0.55f, 0.1f, 1f);

	// Not listed, as Panel_Storage leaves them out.
	private static readonly HashSet<LuaCrafting.LuaResource> Excluded = new HashSet<LuaCrafting.LuaResource>
	{
		LuaCrafting.LuaResource.None,
		LuaCrafting.LuaResource.SentinelWard,
		LuaCrafting.LuaResource.HealingWard,
		LuaCrafting.LuaResource.CaptureWard,
		LuaCrafting.LuaResource.LanternWard,
		LuaCrafting.LuaResource.PylonWard,
		LuaCrafting.LuaResource.BearTrap
	};

	private sealed class Row
	{
		public LuaCrafting.LuaResource Resource;

		public GameObject Root;

		public Image Icon;

		public Text Name;

		public Text Count;
	}

	private GameMenuModel _menu;

	private RectTransform _rect;

	private Text _used;

	private Text _free;

	private Text _empty;

	private readonly List<Row> _rows = new List<Row>();

	private float _readAt = -1f;

	internal static void Create(Transform parent, GameMenuModel menu)
	{
		RectTransform rect = UiKit.CreateRect("CS resource list", parent);
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
		ResourceListView view = rect.gameObject.AddComponent<ResourceListView>();
		view._menu = menu;
		view._rect = rect;
		view.Build();
	}

	private void Build()
	{
		Text header = Label(_rect, "RESOURCES", TextAnchor.MiddleLeft, new Color(0.6f, 0.6f, 0.6f, 1f), 12);
		Height(header.gameObject);
		_used = StorageRow("Storage used");
		_free = StorageRow("Storage free");
		_empty = Label(_rect, "-", TextAnchor.MiddleLeft, LuiStyle.CastleRed, 14);
		Height(_empty.gameObject);
		foreach (LuaCrafting.LuaResource resource in CraftingCatalog.ResourceOrder)
		{
			if (!Excluded.Contains(resource))
			{
				_rows.Add(ResourceRow(resource));
			}
		}
	}

	private Text StorageRow(string text)
	{
		Transform row = UiKit.CreateRow(_rect, RowHeight);
		Text name = Label(row, text, TextAnchor.MiddleLeft, NameColor, 14);
		UiKit.Flexible(name.gameObject);
		Text value = Label(row, string.Empty, TextAnchor.MiddleRight, LuiStyle.CastleYellow, 14);
		UiKit.Fixed(value.gameObject, 90f, RowHeight);
		return value;
	}

	private Row ResourceRow(LuaCrafting.LuaResource resource)
	{
		Transform row = UiKit.CreateRow(_rect, RowHeight);
		RectTransform icon = UiKit.CreateRect("Icon", row);
		UiKit.Fixed(icon.gameObject, IconSize, IconSize);
		Image image = icon.gameObject.AddComponent<Image>();
		image.preserveAspect = true;
		image.raycastTarget = false;
		Text name = Label(row, string.Empty, TextAnchor.MiddleLeft, NameColor, 14);
		UiKit.Flexible(name.gameObject);
		Text count = Label(row, string.Empty, TextAnchor.MiddleRight, LuiStyle.CastleYellow, 14);
		UiKit.Fixed(count.gameObject, 48f, RowHeight);
		row.gameObject.SetActive(false);
		return new Row { Resource = resource, Root = row.gameObject, Icon = image, Name = name, Count = count };
	}

	private static Text Label(Transform parent, string text, TextAnchor alignment, Color color, int size)
	{
		return LuiWidgets.Label(parent, text, size, color, alignment, LuiStyle.Regular);
	}

	private static void Height(GameObject go)
	{
		LayoutElement element = go.AddComponent<LayoutElement>();
		element.minHeight = RowHeight;
		element.preferredHeight = RowHeight;
	}

	private void Update()
	{
		bool shown = ResourceList.Shown;
		Image background = GetComponent<Image>();
		if (background.enabled != shown)
		{
			background.enabled = shown;
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
		// Left of the right-hand bar, or of the right-hand sidebar while one is open.
		_rect.anchoredPosition = new Vector2((_menu.Right.Open != null) ? -328f : -56f, -64f);
		if (_readAt >= 0f && Time.unscaledTime - _readAt < ReadSeconds)
		{
			return;
		}
		_readAt = Time.unscaledTime;
		Read();
	}

	private void Read()
	{
		int capacity = ResourceList.Capacity;
		int used = ResourceList.Used;
		int free = Mathf.Max(0, capacity - used);
		_used.text = used + " / " + capacity;
		int percent = (capacity > 0) ? Mathf.FloorToInt(free * 100f / capacity + 0.5f) : 0;
		_free.text = free + " (" + percent + "%)";
		_free.color = (capacity <= 0 || free <= 0) ? LuiStyle.CastleRed : (((float)used / capacity >= 0.9f) ? Orange : LuiStyle.CastleYellow);
		Dictionary<LuaCrafting.LuaResource, int> stored = CraftingCatalog.Stored();
		bool any = false;
		foreach (Row row in _rows)
		{
			stored.TryGetValue(row.Resource, out int count);
			if (count > 0 && !ResourceSeen.IsSeen(row.Resource))
			{
				ResourceSeen.MarkSeen(row.Resource);
			}
			bool listed = count > 0 || ResourceSeen.IsSeen(row.Resource);
			if (row.Root.activeSelf != listed)
			{
				row.Root.SetActive(listed);
			}
			if (!listed)
			{
				continue;
			}
			any = true;
			Sprite sprite = CraftingCatalog.StockpiledIcon(row.Resource);
			if (row.Icon.sprite != sprite)
			{
				row.Icon.sprite = sprite;
			}
			row.Icon.enabled = sprite != null;
			row.Name.text = CraftingCatalog.ResourceName(row.Resource);
			row.Name.color = (count <= 0) ? LuiStyle.CastleRed : NameColor;
			row.Count.text = count.ToString();
			row.Count.color = (count <= 0) ? LuiStyle.CastleRed : LuiStyle.CastleYellow;
		}
		if (_empty.gameObject.activeSelf == any)
		{
			_empty.gameObject.SetActive(!any);
		}
	}
}
