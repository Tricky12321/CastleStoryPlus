using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using static CastleStoryPlus.UI.UiKit;

namespace CastleStoryPlus.UI;

// "All settings": every value of the mod's config file, editable in the game (opened from the Castle Story Plus
// settings window). The sections are listed on the left, the values of the chosen one (or every value matching the
// search) on the right, and the description of the value under the mouse at the bottom. A value is written to
// BepInEx/config/com.tricky12321.castlestoryplus.cfg at once.
// The editor follows the entry's type: ON/OFF for a switch, the next choice for an enum or a list of accepted values,
// a key press for a key, -/+ and a field for a number, a field for anything else (in the file's own text form).
// Values of a feature that is switched off are not bound by the mod in this session (BepInEx keeps them as text, as
// it found them in the file); they are edited as that text and apply when the feature runs.
internal class AllSettingsPanel : MonoBehaviour
{
	private const float WindowWidth = 980f;

	private const float ListHeight = 640f;

	// Set while Embed adds a panel, so an Awake run at once (an active parent) does not build a window.
	private static bool _embedding;

	// Built into another menu (the game's Options): no window of its own, no Escape to close.
	private bool _embedded;

	private float _width = WindowWidth;

	private float _listHeight = ListHeight;

	private static readonly Color On = new Color(0.45f, 0.85f, 0.4f, 1f);

	private static readonly Color Off = new Color(0.9f, 0.3f, 0.22f, 1f);

	private static AllSettingsPanel _instance;

	// One value of the file: bound by the mod (Entry) or kept as text by BepInEx (Orphan).
	private sealed class Setting
	{
		public ConfigDefinition Definition;

		public ConfigEntryBase Entry;

		public bool Orphan;
	}

	private GameObject _window;

	private Transform _sections;

	private Transform _values;

	private InputField _search;

	private Text _description;

	private Text _feedback;

	private string _section;

	private readonly List<Action> _refreshers = new List<Action>();

	private readonly List<Button> _sectionButtons = new List<Button>();

	private ConfigEntryBase _capturing;

	private Text _captureLabel;

	private int _captureFrame;

	public static void Toggle()
	{
		if (_instance == null)
		{
			GameObject go = new GameObject("AllSettingsPanel");
			_instance = go.AddComponent<AllSettingsPanel>();
			_instance.SetVisible(true);
			return;
		}
		_instance.SetVisible(!_instance._window.activeSelf);
	}

	// The settings built into a page of another menu, filling the parent; shown while the page is. Built here, not
	// in Awake: Unity runs Awake only once the object is active, and the page is usually hidden.
	internal static AllSettingsPanel Embed(RectTransform parent, float width)
	{
		GameObject go = new GameObject("CSP all settings", typeof(RectTransform));
		go.transform.SetParent(parent, false);
		AllSettingsPanel panel;
		_embedding = true;
		try
		{
			panel = go.AddComponent<AllSettingsPanel>();
		}
		finally
		{
			_embedding = false;
		}
		panel._embedded = true;
		panel._width = width;
		panel._window = go;
		panel.BuildContent(go.transform);
		return panel;
	}

	private void Awake()
	{
		if (_embedding || _embedded)
		{
			return;
		}
		BuildWindow();
		SetVisible(false);
	}

	private void OnEnable()
	{
		if (_embedded && _window != null)
		{
			SetVisible(true);
			Fit();
		}
	}

	private LayoutElement _sectionsSize;

	private LayoutElement _valuesSize;

	// The lists as tall as the page leaves them (header, description and notes under them take about 160).
	private void Fit()
	{
		float height = ((RectTransform)_window.transform).rect.height;
		if (height <= 0f || _sectionsSize == null)
		{
			return;
		}
		_listHeight = Mathf.Max(200f, height - 160f);
		_sectionsSize.minHeight = _sectionsSize.preferredHeight = _listHeight;
		_valuesSize.minHeight = _valuesSize.preferredHeight = _listHeight;
		LayoutElement lists = _lists.GetComponent<LayoutElement>();
		lists.minHeight = lists.preferredHeight = _listHeight;
	}

	private float _fittedHeight;

	private void LateUpdate()
	{
		if (_embedded && _window != null)
		{
			float height = ((RectTransform)_window.transform).rect.height;
			if (Mathf.Abs(height - _fittedHeight) > 1f)
			{
				_fittedHeight = height;
				Fit();
			}
		}
	}

	private Transform _lists;

	private void OnDestroy()
	{
		if (_instance == this)
		{
			_instance = null;
		}
	}

	private void SetVisible(bool visible)
	{
		_capturing = null;
		_window.SetActive(visible);
		if (visible)
		{
			_feedback.text = string.Empty;
			// Features switched on or off since the window was built bind or drop values: list them again.
			FillSections();
			ShowValues();
		}
	}

	private void Update()
	{
		if (!_window.activeSelf)
		{
			return;
		}
		if (_capturing != null)
		{
			UpdateCapture();
		}
		else if (!_embedded && UnityEngine.Input.GetKeyDown(KeyCode.Escape) && !_search.isFocused)
		{
			SetVisible(false);
		}
	}

	// ---- the file's values

	private static List<Setting> AllSettings()
	{
		List<Setting> settings = new List<Setting>();
		HashSet<ConfigDefinition> bound = new HashSet<ConfigDefinition>();
		foreach (KeyValuePair<ConfigDefinition, ConfigEntryBase> pair in Plugin.Cfg)
		{
			settings.Add(new Setting { Definition = pair.Key, Entry = pair.Value });
			bound.Add(pair.Key);
		}
		foreach (KeyValuePair<ConfigDefinition, string> pair in Orphans())
		{
			if (!bound.Contains(pair.Key))
			{
				settings.Add(new Setting { Definition = pair.Key, Orphan = true });
			}
		}
		settings.Sort((Setting a, Setting b) =>
		{
			int bySection = string.CompareOrdinal(a.Definition.Section, b.Definition.Section);
			return (bySection != 0) ? bySection : string.CompareOrdinal(a.Definition.Key, b.Definition.Key);
		});
		return settings;
	}

	// The values BepInEx read from the file that nothing has bound (a protected member of ConfigFile).
	private static Dictionary<ConfigDefinition, string> Orphans()
	{
		const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
		PropertyInfo property = typeof(ConfigFile).GetProperty("OrphanedEntries", flags);
		object value = (property != null) ? property.GetValue(Plugin.Cfg, null) : null;
		if (value == null)
		{
			FieldInfo field = typeof(ConfigFile).GetField("OrphanedEntries", flags) ?? typeof(ConfigFile).GetField("<OrphanedEntries>k__BackingField", flags);
			value = (field != null) ? field.GetValue(Plugin.Cfg) : null;
		}
		return (value as Dictionary<ConfigDefinition, string>) ?? new Dictionary<ConfigDefinition, string>();
	}

	// ---- window

	private void BuildWindow()
	{
		GameObject canvasGo = CreateCanvas("AllSettingsCanvas", transform, 911);
		_window = CreateWindow(canvasGo.transform, WindowWidth);
		Draggable(_window);
		RectTransform windowRect = _window.GetComponent<RectTransform>();
		windowRect.anchorMin = new Vector2(0.5f, 0.5f);
		windowRect.anchorMax = new Vector2(0.5f, 0.5f);
		windowRect.pivot = new Vector2(0.5f, 0.5f);
		windowRect.anchoredPosition = Vector2.zero;
		BuildContent(_window.transform);
	}

	// The header (title, search, close), the section and value lists, the description and notes.
	private void BuildContent(Transform root)
	{
		if (_embedded)
		{
			RectTransform rect = (RectTransform)root;
			rect.anchorMin = Vector2.zero;
			rect.anchorMax = Vector2.one;
			rect.pivot = new Vector2(0.5f, 0.5f);
			rect.offsetMin = Vector2.zero;
			rect.offsetMax = Vector2.zero;
			// A dark plate under the lists, as the game's pages have their own rows: the menu's picture is behind.
			Image plate = root.gameObject.AddComponent<Image>();
			plate.color = new Color(0f, 0f, 0f, 0.45f);
			VerticalLayoutGroup layout = root.gameObject.AddComponent<VerticalLayoutGroup>();
			layout.padding = new RectOffset(10, 10, 8, 8);
			layout.spacing = 4f;
			layout.childControlWidth = true;
			layout.childControlHeight = true;
			layout.childForceExpandWidth = true;
			layout.childForceExpandHeight = false;
		}
		Transform header = CreateRow(root, 30f);
		Text title = CreateText(header, "ALL SETTINGS", 18, Yellow, TextAnchor.MiddleLeft);
		title.fontStyle = FontStyle.Bold;
		Flexible(title.gameObject);
		_search = CreateInputField(header, "Search all settings", 260f);
		_search.onValueChanged.AddListener((string text) => ShowValues());
		if (!_embedded)
		{
			CreateButton(header, "X", 28f, () => SetVisible(false));
		}

		Transform lists = CreateRow(root, _listHeight);
		_lists = lists;
		lists.GetComponent<HorizontalLayoutGroup>().childAlignment = TextAnchor.UpperLeft;
		_sections = CreateScrollList(lists, out LayoutElement sectionsSize);
		_sectionsSize = sectionsSize;
		sectionsSize.minWidth = 210f;
		sectionsSize.preferredWidth = 210f;
		sectionsSize.minHeight = _listHeight;
		sectionsSize.preferredHeight = _listHeight;
		_values = CreateScrollList(lists, out LayoutElement valuesSize);
		_valuesSize = valuesSize;
		valuesSize.flexibleWidth = 1f;
		valuesSize.minHeight = _listHeight;
		valuesSize.preferredHeight = _listHeight;

		_description = CreateText(root, string.Empty, 13, TextColor, TextAnchor.UpperLeft);
		_description.horizontalOverflow = HorizontalWrapMode.Wrap;
		Fixed(_description.gameObject, _width - 28f, 54f);
		_feedback = CreateText(root, string.Empty, 12, Yellow, TextAnchor.UpperLeft);
		_feedback.horizontalOverflow = HorizontalWrapMode.Wrap;
		Text note = CreateText(root, "Saved at once to BepInEx/config/com.tricky12321.castlestoryplus.cfg and used at once. A few values read when the game starts (graphics, menus) apply after a restart.", 12, Grey, TextAnchor.UpperLeft);
		note.horizontalOverflow = HorizontalWrapMode.Wrap;
	}

	private void FillSections()
	{
		Clear(_sections);
		_sectionButtons.Clear();
		List<string> names = new List<string>();
		foreach (Setting setting in AllSettings())
		{
			if (names.Count == 0 || names[names.Count - 1] != setting.Definition.Section)
			{
				names.Add(setting.Definition.Section);
			}
		}
		if (_section == null || !names.Contains(_section))
		{
			_section = (names.Count > 0) ? names[0] : null;
		}
		foreach (string name in names)
		{
			string section = name;
			Button button = CreateButton(_sections, section, 0f, () =>
			{
				_section = section;
				_search.text = string.Empty;
				ShowValues();
			});
			Text label = button.GetComponentInChildren<Text>();
			label.alignment = TextAnchor.MiddleLeft;
			label.rectTransform.offsetMin = new Vector2(8f, 0f);
			_sectionButtons.Add(button);
		}
	}

	private void ShowValues()
	{
		Clear(_values);
		_refreshers.Clear();
		_capturing = null;
		string search = (_search.text ?? string.Empty).Trim();
		bool searching = search.Length > 0;
		foreach (Button button in _sectionButtons)
		{
			Text label = button.GetComponentInChildren<Text>();
			label.color = (!searching && label.text == _section) ? Yellow : TextColor;
		}
		int shown = 0;
		foreach (Setting setting in AllSettings())
		{
			if (searching ? !Matches(setting, search) : setting.Definition.Section != _section)
			{
				continue;
			}
			AddValueRow(setting, searching);
			shown++;
		}
		if (shown == 0)
		{
			CreateText(_values, searching ? "No setting matches \"" + search + "\"." : "No settings.", 13, Grey, TextAnchor.MiddleLeft);
		}
		_description.text = string.Empty;
		Refresh();
	}

	private static bool Matches(Setting setting, string search)
	{
		string description = (setting.Entry != null && setting.Entry.Description != null) ? setting.Entry.Description.Description : string.Empty;
		return Contains(setting.Definition.Key, search) || Contains(setting.Definition.Section, search) || Contains(description, search);
	}

	private static bool Contains(string text, string search)
	{
		return text != null && text.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
	}

	private void Refresh()
	{
		foreach (Action refresher in _refreshers)
		{
			refresher();
		}
		if (_embedded)
		{
			GameStyle();
		}
	}

	private void Changed(Setting setting)
	{
		_feedback.text = setting.Orphan ? setting.Definition.Key + " applies when its feature is switched on." : string.Empty;
		if (setting.Definition.Section == "Features")
		{
			// The feature switched at once (FeatureToggle): its values are bound now, or left as text.
			_feedback.text = setting.Definition.Key + " switched " + (((bool)setting.Entry.BoxedValue) ? "on" : "off") + ".";
			FillSections();
			ShowValues();
			return;
		}
		Refresh();
	}

	// ---- one value

	private void AddValueRow(Setting setting, bool withSection)
	{
		Transform row = CreateRow(_values, _embedded ? 40f : 28f);
		// A raycast target over the whole row, so pointing at it shows its description.
		Image hover = row.gameObject.AddComponent<Image>();
		hover.color = _embedded ? GameRow : new Color(1f, 1f, 1f, 0.02f);
		OnPointerEnter(row.gameObject, () => _description.text = Describe(setting));
		string name = withSection ? setting.Definition.Section + " > " + setting.Definition.Key : setting.Definition.Key;
		if (_embedded)
		{
			// As the game's option rows: the name in capitals, a line of description under it.
			HorizontalLayoutGroup rowLayout = row.GetComponent<HorizontalLayoutGroup>();
			rowLayout.padding = new RectOffset(10, 8, 2, 2);
			Transform names = CreateRect("Names", row);
			VerticalLayoutGroup column = names.gameObject.AddComponent<VerticalLayoutGroup>();
			column.childControlWidth = true;
			column.childControlHeight = true;
			column.childForceExpandWidth = true;
			column.childForceExpandHeight = false;
			column.childAlignment = TextAnchor.MiddleLeft;
			Flexible(names.gameObject);
			Text title = CreateText(names, Spaced(name).ToUpper(), 13, GameText, TextAnchor.MiddleLeft);
			title.font = CastleStoryPlus.LuaUi.LuiStyle.Bold;
			string line = ShortDescription(setting);
			if (line.Length > 0)
			{
				Text sub = CreateText(names, line, 10, GameTextSoft, TextAnchor.MiddleLeft);
				sub.font = CastleStoryPlus.LuaUi.LuiStyle.Regular;
				// One line, cut at the editor: wrapped, and only one line high.
				sub.horizontalOverflow = HorizontalWrapMode.Wrap;
				sub.verticalOverflow = VerticalWrapMode.Truncate;
				LayoutElement subSize = sub.gameObject.GetComponent<LayoutElement>() ?? sub.gameObject.AddComponent<LayoutElement>();
				subSize.preferredHeight = 12f;
				subSize.minHeight = 12f;
				subSize.minWidth = 0f;
				subSize.preferredWidth = 0f;
			}
		}
		else
		{
			Text label = CreateText(row, name, 13, setting.Orphan ? Grey : TextColor, TextAnchor.MiddleLeft);
			Flexible(label.gameObject);
		}
		if (setting.Orphan)
		{
			AddTextEditor(row, setting);
			return;
		}
		ConfigEntryBase entry = setting.Entry;
		Type type = entry.SettingType;
		object[] choices = Choices(entry);
		if (type == typeof(bool))
		{
			AddSwitch(row, setting);
		}
		else if (type == typeof(KeyCode))
		{
			AddKeyEditor(row, setting);
		}
		else if (type.IsEnum || choices != null)
		{
			AddChoiceEditor(row, setting, choices ?? Enum.GetValues(type).Cast<object>().ToArray());
		}
		else if (type == typeof(int) || type == typeof(float) || type == typeof(double) || type == typeof(long))
		{
			AddNumberEditor(row, setting);
		}
		else
		{
			AddTextEditor(row, setting);
		}
	}

	// The game's option rows: yellow, dark text.
	private static readonly Color GameRow = new Color(1f, 197f / 255f, 10f / 255f, 1f);

	private static readonly Color GameRowSelected = Color.white;

	private static readonly Color GameText = new Color(0.1f, 0.1f, 0.1f, 1f);

	private static readonly Color GameTextSoft = new Color(0.1f, 0.1f, 0.1f, 0.75f);

	// "MaxDistance" as "Max Distance", "AutoSpawn" as "Auto Spawn".
	private static string Spaced(string key)
	{
		System.Text.StringBuilder text = new System.Text.StringBuilder();
		for (int i = 0; i < key.Length; i++)
		{
			if (i > 0 && char.IsUpper(key[i]) && char.IsLower(key[i - 1]))
			{
				text.Append(' ');
			}
			text.Append(key[i]);
		}
		return text.ToString();
	}

	// The description's first sentence, short enough for one line under the name.
	private static string ShortDescription(Setting setting)
	{
		string text = (setting.Entry != null && setting.Entry.Description != null) ? setting.Entry.Description.Description : string.Empty;
		if (string.IsNullOrEmpty(text))
		{
			return string.Empty;
		}
		int end = text.IndexOfAny(new[] { '.', ':', ';', '(' });
		if (end > 0)
		{
			text = text.Substring(0, end);
		}
		return (text.Length > 90) ? text.Substring(0, 87) + "..." : text;
	}

	// A flat button of one colour (the kit's buttons are a dark sprite tinted on hover).
	private static void Flat(Button button, Color color)
	{
		Image image = button.GetComponent<Image>();
		if (image != null)
		{
			image.sprite = null;
			image.color = color;
		}
		ColorBlock colors = button.colors;
		colors.normalColor = Color.white;
		colors.highlightedColor = new Color(0.92f, 0.92f, 0.92f, 1f);
		colors.pressedColor = new Color(0.8f, 0.8f, 0.8f, 1f);
#if UNITY6
		colors.selectedColor = Color.white;
#endif
		button.colors = colors;
	}

	// The game's look on what the rows hold (buttons, fields, texts): run after every refresh, as the editors set their
	// own colours.
	private void GameStyle()
	{
		foreach (Button button in _values.GetComponentsInChildren<Button>(true))
		{
			Flat(button, new Color(0f, 0f, 0f, 0.08f));
			foreach (Text text in button.GetComponentsInChildren<Text>(true))
			{
				text.color = GameText;
				text.font = CastleStoryPlus.LuaUi.LuiStyle.Bold;
			}
		}
		foreach (InputField field in _values.GetComponentsInChildren<InputField>(true))
		{
			Image image = field.GetComponent<Image>();
			if (image != null)
			{
				image.color = new Color(1f, 1f, 1f, 0.85f);
			}
			if (field.textComponent != null)
			{
				field.textComponent.color = GameText;
			}
		}
		string search = (_search.text ?? string.Empty).Trim();
		foreach (Button button in _sectionButtons)
		{
			Text label = button.GetComponentInChildren<Text>();
			bool selected = search.Length == 0 && label.text == _section;
			Flat(button, selected ? GameRowSelected : GameRow);
			label.color = GameText;
			label.font = CastleStoryPlus.LuaUi.LuiStyle.Bold;
		}
	}

	private string Describe(Setting setting)
	{
		string head = "[" + setting.Definition.Section + "] " + setting.Definition.Key;
		if (setting.Orphan)
		{
			return head + ": its feature is off in this session, so the mod has not read it; the value is kept as text and applies when the feature is switched on.";
		}
		ConfigEntryBase entry = setting.Entry;
		string text = head + " (default " + TomlTypeConverter.ConvertToString(entry.DefaultValue, entry.SettingType) + ")";
		if (entry.Description != null && !string.IsNullOrEmpty(entry.Description.Description))
		{
			text += ": " + entry.Description.Description;
		}
		return text;
	}

	private void AddSwitch(Transform row, Setting setting)
	{
		ConfigEntryBase entry = setting.Entry;
		Button button = CreateButton(row, string.Empty, 80f, () =>
		{
			entry.BoxedValue = !(bool)entry.BoxedValue;
			Changed(setting);
		});
		Text value = button.GetComponentInChildren<Text>();
		_refreshers.Add(() =>
		{
			bool on = (bool)entry.BoxedValue;
			value.text = _embedded ? (on ? "ENABLED" : "DISABLED") : (on ? "ON" : "OFF");
			value.color = on ? On : Off;
		});
	}

	// The next of the accepted values on each click (the first after the last).
	private void AddChoiceEditor(Transform row, Setting setting, object[] choices)
	{
		ConfigEntryBase entry = setting.Entry;
		Button button = CreateButton(row, string.Empty, 200f, () =>
		{
			int index = Array.IndexOf(choices, entry.BoxedValue);
			entry.BoxedValue = choices[(index + 1) % choices.Length];
			Changed(setting);
		});
		Text value = button.GetComponentInChildren<Text>();
		value.color = Yellow;
		_refreshers.Add(() => value.text = Text(entry));
	}

	private void AddKeyEditor(Transform row, Setting setting)
	{
		ConfigEntryBase entry = setting.Entry;
		Text value = null;
		Button button = CreateButton(row, string.Empty, 160f, () =>
		{
			_capturing = entry;
			_captureLabel = value;
			_captureFrame = Time.frameCount;
			value.text = "Press a key...";
		});
		value = button.GetComponentInChildren<Text>();
		value.color = Yellow;
		_refreshers.Add(() => value.text = Text(entry));
	}

	private void UpdateCapture()
	{
		if (Time.frameCount == _captureFrame)
		{
			return;
		}
		if (UnityEngine.Input.GetKeyDown(KeyCode.Escape))
		{
			_capturing = null;
			Refresh();
			return;
		}
		if (!UnityEngine.Input.anyKeyDown)
		{
			return;
		}
		foreach (KeyCode key in Enum.GetValues(typeof(KeyCode)))
		{
			if (key >= KeyCode.Mouse0 && key <= KeyCode.Mouse6)
			{
				continue;
			}
			if (UnityEngine.Input.GetKeyDown(key))
			{
				_capturing.BoxedValue = key;
				_capturing = null;
				_captureLabel = null;
				Refresh();
				return;
			}
		}
	}

	// -/+ by a step that fits the value (a twentieth of its accepted range, or a tenth of its size), and a field for
	// any value; BepInEx keeps it inside the accepted range.
	private void AddNumberEditor(Transform row, Setting setting)
	{
		ConfigEntryBase entry = setting.Entry;
		CreateButton(row, "-", 26f, () => Step(setting, -1));
		InputField field = CreateInputField(row, string.Empty, 100f);
		field.textComponent.alignment = TextAnchor.MiddleRight;
		field.contentType = (entry.SettingType == typeof(int) || entry.SettingType == typeof(long)) ? InputField.ContentType.IntegerNumber : InputField.ContentType.DecimalNumber;
		field.onEndEdit.AddListener((string text) =>
		{
			Set(setting, text);
		});
		CreateButton(row, "+", 26f, () => Step(setting, 1));
		_refreshers.Add(() =>
		{
			if (!field.isFocused)
			{
				field.text = Text(entry);
			}
		});
	}

	private void Step(Setting setting, int direction)
	{
		ConfigEntryBase entry = setting.Entry;
		double value = Convert.ToDouble(entry.BoxedValue, CultureInfo.InvariantCulture);
		double step = StepFor(entry, value);
		double next = Math.Round(value / step) * step + direction * step;
		if (Range(entry, out double min, out double max))
		{
			next = Math.Max(min, Math.Min(max, next));
		}
		entry.BoxedValue = Convert.ChangeType(next, entry.SettingType, CultureInfo.InvariantCulture);
		Changed(setting);
	}

	private static double StepFor(ConfigEntryBase entry, double value)
	{
		bool whole = entry.SettingType == typeof(int) || entry.SettingType == typeof(long);
		double step;
		if (Range(entry, out double min, out double max) && max > min)
		{
			step = Nice((max - min) / 20.0);
		}
		else
		{
			double size = Math.Abs(value);
			step = (size > 0.0) ? Nice(size / 10.0) : (whole ? 1.0 : 0.1);
		}
		return whole ? Math.Max(1.0, Math.Round(step)) : step;
	}

	// 1, 2 or 5 times a power of ten, at most the given step.
	private static double Nice(double step)
	{
		double power = Math.Pow(10.0, Math.Floor(Math.Log10(step)));
		double scaled = step / power;
		return ((scaled >= 5.0) ? 5.0 : (scaled >= 2.0) ? 2.0 : 1.0) * power;
	}

	// A field in the file's own text form (strings, other types, and values of features that are off).
	private void AddTextEditor(Transform row, Setting setting)
	{
		InputField field = CreateInputField(row, string.Empty, 260f);
		field.onEndEdit.AddListener((string text) => Set(setting, text));
		_refreshers.Add(() =>
		{
			if (!field.isFocused)
			{
				field.text = setting.Orphan ? OrphanText(setting) : Text(setting.Entry);
			}
		});
	}

	private void Set(Setting setting, string text)
	{
		if (setting.Orphan)
		{
			Orphans()[setting.Definition] = text;
			Plugin.Cfg.Save();
			Changed(setting);
			return;
		}
		string before = Text(setting.Entry);
		setting.Entry.SetSerializedValue(text);
		if (Text(setting.Entry) == before && text.Trim() != before)
		{
			_feedback.text = "\"" + text + "\" is not a value " + setting.Definition.Key + " takes.";
			Refresh();
			return;
		}
		Changed(setting);
	}

	private static string OrphanText(Setting setting)
	{
		return Orphans().TryGetValue(setting.Definition, out string text) ? text : string.Empty;
	}

	private static string Text(ConfigEntryBase entry)
	{
		return TomlTypeConverter.ConvertToString(entry.BoxedValue, entry.SettingType);
	}

	// The values of an AcceptableValueList, or null.
	private static object[] Choices(ConfigEntryBase entry)
	{
		AcceptableValueBase accepted = (entry.Description != null) ? entry.Description.AcceptableValues : null;
		if (accepted == null || !accepted.GetType().Name.StartsWith("AcceptableValueList", StringComparison.Ordinal))
		{
			return null;
		}
		PropertyInfo values = accepted.GetType().GetProperty("AcceptableValues");
		IEnumerable list = (values != null) ? values.GetValue(accepted, null) as IEnumerable : null;
		return (list != null) ? list.Cast<object>().ToArray() : null;
	}

	// The bounds of an AcceptableValueRange.
	private static bool Range(ConfigEntryBase entry, out double min, out double max)
	{
		min = 0.0;
		max = 0.0;
		AcceptableValueBase accepted = (entry.Description != null) ? entry.Description.AcceptableValues : null;
		if (accepted == null || !accepted.GetType().Name.StartsWith("AcceptableValueRange", StringComparison.Ordinal))
		{
			return false;
		}
		PropertyInfo minValue = accepted.GetType().GetProperty("MinValue");
		PropertyInfo maxValue = accepted.GetType().GetProperty("MaxValue");
		if (minValue == null || maxValue == null)
		{
			return false;
		}
		min = Convert.ToDouble(minValue.GetValue(accepted, null), CultureInfo.InvariantCulture);
		max = Convert.ToDouble(maxValue.GetValue(accepted, null), CultureInfo.InvariantCulture);
		return true;
	}

	// ---- helpers

	// Not an EventTrigger: that takes every pointer event, the mouse wheel too, so the list would not scroll over it.
	private static void OnPointerEnter(GameObject go, Action action)
	{
		go.AddComponent<Hover>().Enter = action;
	}

	private sealed class Hover : MonoBehaviour, IPointerEnterHandler
	{
		public Action Enter;

		public void OnPointerEnter(PointerEventData eventData)
		{
			Enter?.Invoke();
		}
	}

	private static void Clear(Transform parent)
	{
		for (int i = parent.childCount - 1; i >= 0; i--)
		{
			Transform child = parent.GetChild(i);
			child.SetParent(null, false);
			Destroy(child.gameObject);
		}
	}
}
