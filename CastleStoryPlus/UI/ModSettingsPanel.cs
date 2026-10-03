using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using CastleStoryPlus.Core;
using CastleStoryPlus.Giant;
using CastleStoryPlus.Menus;
using CastleStoryPlus.Saving;
using UnityEngine;
using UnityEngine.UI;
using static CastleStoryPlus.UI.UiKit;

namespace CastleStoryPlus.UI;

// "Castle Story Plus settings" window, opened from the in-game Settings menu. Every value is a BepInEx
// config entry, so a change here is written to BepInEx/config/com.tricky12321.castlestoryplus.cfg at once.
// Autosave, firefly energy, 3x bricktron values and keys apply immediately; feature switches apply after a restart.
[Feature]
internal class ModSettingsPanel : MonoBehaviour
{
	private class KeyRow
	{
		public ConfigEntry<KeyCode> Entry;

		public string Prefix;

		public Text Label;
	}

	private class FeatureRow
	{
		public ConfigEntry<bool> Entry;

		public Text Label;
	}

	private const float EnergyStep = 0.05f;

	private const float EnergyMin = 1f;

	private const float EnergyMax = 3f;

	private static readonly Color On = new Color(0.45f, 0.85f, 0.4f, 1f);

	private static readonly Color Off = new Color(0.9f, 0.3f, 0.22f, 1f);

	private static ModSettingsPanel _instance;

	private GameObject _window;

	private Text _autosaveText;

	private Text _energyText;

	private Text _feedbackText;

	private readonly List<KeyRow> _keyRows = new List<KeyRow>();

	private readonly List<FeatureRow> _featureRows = new List<FeatureRow>();

	private readonly List<Action> _refreshers = new List<Action>();

	private KeyRow _capturing;

	private int _captureFrame;

	private static void Enable()
	{
		LuaInjection.AddAction("ModSettings", Toggle);
		// After the quicksave button (and the "if not multiplayer" block around it), so it is there in multiplayer too.
		LuaInjection.AddPatch("ModSettings", "LUI/Menus/GameMenu.lua", "h.OnAction = ||MenuCalls.QuickSave()\n\n\t_m.mg.settings:AddChild(h)\n\t_m.mh.settings.onLoad:AddListener(function(m) m.AddMenuHandleToggle(h, _m.sh.settings) end)\n\tend\nend\n", LuaInjection.Mode.InsertAfter, @"
---Castle Story Plus settings (autosave, firefly energy, keys, feature switches)
do
local h = ButtonHandle.New()
h.Icon = ||IconKeys.UI_Settings:Get64()
h.IconColor = ||CastleYellow
h.IconSize = ||32
h.Label = ||""Castle Story Plus settings""
h.hasFgLabel = true
h.OnAction = ||CastleStoryPlus.ModSettings()

_m.mg.settings:AddChild(h)
_m.mh.settings.onLoad:AddListener(function(m) m.AddMenuHandleToggle(h, _m.sh.settings) end)
end
");
	}

	public static void Toggle()
	{
		if (_instance == null)
		{
			GameObject go = new GameObject("ModSettingsPanel");
			_instance = go.AddComponent<ModSettingsPanel>();
			_instance.SetVisible(true);
			return;
		}
		_instance.SetVisible(!_instance._window.activeSelf);
	}

	private void Awake()
	{
		BuildWindow();
		SetVisible(false);
	}

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
			_feedbackText.text = string.Empty;
			Refresh();
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
		else if (UnityEngine.Input.GetKeyDown(KeyCode.Escape))
		{
			SetVisible(false);
		}
	}

	private void Refresh()
	{
		if (_autosaveText != null)
		{
			_autosaveText.text = AutoSave.Describe(AutoSave.Minutes.Value);
		}
		_energyText.text = "x" + Plugin.EnergyMultiplier.Value.ToString("0.00");
		foreach (Action refresher in _refreshers)
		{
			refresher();
		}
		foreach (KeyRow row in _keyRows)
		{
			row.Label.text = (row == _capturing) ? "Press a key..." : (row.Prefix + row.Entry.Value);
		}
		bool restartNeeded = false;
		foreach (FeatureRow row in _featureRows)
		{
			bool value = row.Entry.Value;
			row.Label.text = row.Entry.Definition.Key + ": " + (value ? "ON" : "OFF");
			row.Label.color = value ? On : Off;
			if (Plugin.LoadedFeatures.TryGetValue(row.Entry.Definition.Key, out bool loaded) && loaded != value)
			{
				restartNeeded = true;
			}
		}
		if (restartNeeded)
		{
			_feedbackText.text = "Restart the game to apply the feature changes.";
		}
	}

	// ---- actions

	private void StepAutosave(int direction)
	{
		int current = Array.IndexOf(AutoSave.Choices, AutoSave.Minutes.Value);
		if (current < 0)
		{
			// A custom value from the config file: continue from the nearest choice.
			current = 0;
			while (current < AutoSave.Choices.Length - 1 && AutoSave.Choices[current] < AutoSave.Minutes.Value)
			{
				current++;
			}
			if (direction > 0 && AutoSave.Choices[current] > AutoSave.Minutes.Value)
			{
				current--;
			}
		}
		int next = Mathf.Clamp(current + direction, 0, AutoSave.Choices.Length - 1);
		AutoSave.Minutes.Value = AutoSave.Choices[next];
		Refresh();
	}

	private void StepEnergy(int direction)
	{
		float value = Mathf.Round(Plugin.EnergyMultiplier.Value / EnergyStep) * EnergyStep + direction * EnergyStep;
		Plugin.EnergyMultiplier.Value = Mathf.Clamp(value, EnergyMin, EnergyMax);
		Refresh();
	}

	private void StartCapture(KeyRow row)
	{
		_capturing = row;
		_captureFrame = Time.frameCount;
		Refresh();
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
				_capturing.Entry.Value = key;
				_capturing = null;
				Refresh();
				return;
			}
		}
	}

	private void ToggleFeature(FeatureRow row)
	{
		row.Entry.Value = !row.Entry.Value;
		_feedbackText.text = string.Empty;
		Refresh();
	}

	// ---- window

	private void BuildWindow()
	{
		GameObject canvasGo = CreateCanvas("ModSettingsCanvas", transform, 910);
		_window = CreateWindow(canvasGo.transform, 500f);
		RectTransform windowRect = _window.GetComponent<RectTransform>();
		windowRect.anchorMin = new Vector2(0.5f, 0.5f);
		windowRect.anchorMax = new Vector2(0.5f, 0.5f);
		windowRect.pivot = new Vector2(0.5f, 0.5f);
		windowRect.anchoredPosition = Vector2.zero;

		Transform header = CreateRow(_window.transform, 30f);
		Text title = CreateText(header, "CASTLE STORY PLUS " + Plugin.Version, 18, Yellow, TextAnchor.MiddleLeft);
		title.fontStyle = FontStyle.Bold;
		Flexible(title.gameObject);
		CreateButton(header, "X", 28f, () => SetVisible(false));

		if (AutoSave.Minutes != null)
		{
			AddSection("SAVING");
			_autosaveText = AddStepperRow("Autosave every", StepAutosave);
		}

		AddSection("ECONOMY");
		_energyText = AddStepperRow("Energy per brewed firefly", StepEnergy);

		if (GiantBricktron.Speed != null)
		{
			AddSection("3X BRICKTRON");
			AddFloatStepper("Speed of everything it does", GiantBricktron.Speed, 0.5f, 1f, 5f, "x0.0");
			AddFloatStepper("Health", GiantBricktron.Health, 0.5f, 1f, 5f, "x0.0");
			AddFloatStepper("Cost (energy of new bricktrons)", GiantBricktron.CostMultiplier, 0.25f, 0f, 5f, "x0.00");
			AddIntStepper("Bricktrons per 3x bricktron", GiantBricktron.BricktronsPerGiant, 1, 1, 30);
		}

		if (SpeedKeys.Speed2 != null)
		{
			AddSection("GAME SPEED (SINGLE PLAYER)");
			AddFloatStepper("Speed of the second speed key", SpeedKeys.Speed2, 0.5f, 1f, 10f, "0.0x");
			AddFloatStepper("Speed of the third speed key", SpeedKeys.Speed3, 0.5f, 1f, 10f, "0.0x");
		}

		AddSection("KEYS");
		AddKeyRow("Move a building (hold + click)", "Building", "MoveStructureKey", string.Empty);
		AddKeyRow("Copy an area", "Building", "CopyKey", "Ctrl+");
		AddKeyRow("Paste the copy", "Building", "PasteKey", "Ctrl+");
		AddKeyRow("Normal game speed", "GameSpeed", "NormalSpeedKey", string.Empty);
		AddKeyRow("Second game speed", "GameSpeed", "Speed2Key", string.Empty);
		AddKeyRow("Third game speed", "GameSpeed", "Speed3Key", string.Empty);

		AddSection("FEATURES (APPLY AFTER A RESTART)");
		List<ConfigEntry<bool>> switches = new List<ConfigEntry<bool>>(Plugin.FeatureSwitches);
		switches.Sort((ConfigEntry<bool> a, ConfigEntry<bool> b) => string.CompareOrdinal(a.Definition.Key, b.Definition.Key));
		Transform row = null;
		for (int i = 0; i < switches.Count; i++)
		{
			if (i % 2 == 0)
			{
				row = CreateRow(_window.transform, 28f);
			}
			FeatureRow featureRow = new FeatureRow { Entry = switches[i] };
			Button button = CreateButton(row, string.Empty, 0f, () => ToggleFeature(featureRow));
			Flexible(button.gameObject);
			featureRow.Label = button.GetComponentInChildren<Text>();
			featureRow.Label.fontSize = 12;
			_featureRows.Add(featureRow);
		}
		if (switches.Count % 2 == 1)
		{
			Flexible(CreateRect("Spacer", row).gameObject);
		}

		_feedbackText = AddNote(string.Empty);
		_feedbackText.color = Yellow;
		AddNote("Saved to BepInEx/config/com.tricky12321.castlestoryplus.cfg.");
	}

	private void AddSection(string text)
	{
		Transform row = CreateRow(_window.transform, 26f);
		Text label = CreateText(row, text, 12, Grey, TextAnchor.LowerLeft);
		Flexible(label.gameObject);
	}

	private Text AddNote(string text)
	{
		Text note = CreateText(_window.transform, text, 12, Grey, TextAnchor.UpperLeft);
		note.horizontalOverflow = HorizontalWrapMode.Wrap;
		return note;
	}

	private Text AddStepperRow(string name, Action<int> step)
	{
		Transform row = CreateRow(_window.transform, 26f);
		Text label = CreateText(row, name, 14, TextColor, TextAnchor.MiddleLeft);
		Flexible(label.gameObject);
		CreateButton(row, "-", 26f, () => step(-1));
		Text value = CreateText(row, string.Empty, 15, Yellow, TextAnchor.MiddleRight);
		Fixed(value.gameObject, 70f, 26f);
		CreateButton(row, "+", 26f, () => step(1));
		return value;
	}

	private void AddFloatStepper(string name, ConfigEntry<float> entry, float step, float min, float max, string format)
	{
		Text value = AddStepperRow(name, (int direction) =>
		{
			entry.Value = Mathf.Clamp(Mathf.Round(entry.Value / step) * step + direction * step, min, max);
			Refresh();
		});
		_refreshers.Add(() => value.text = entry.Value.ToString(format));
	}

	private void AddIntStepper(string name, ConfigEntry<int> entry, int step, int min, int max)
	{
		Text value = AddStepperRow(name, (int direction) =>
		{
			entry.Value = Mathf.Clamp(entry.Value + direction * step, min, max);
			Refresh();
		});
		_refreshers.Add(() => value.text = entry.Value.ToString());
	}

	// Only shown when the feature that owns the key is on (its entry is bound at startup).
	private void AddKeyRow(string name, string section, string key, string prefix)
	{
		if (!Plugin.Cfg.TryGetEntry(new ConfigDefinition(section, key), out ConfigEntry<KeyCode> entry))
		{
			return;
		}
		Transform row = CreateRow(_window.transform, 26f);
		Text label = CreateText(row, name, 14, TextColor, TextAnchor.MiddleLeft);
		Flexible(label.gameObject);
		KeyRow keyRow = new KeyRow { Entry = entry, Prefix = prefix };
		Button button = CreateButton(row, string.Empty, 130f, () => StartCapture(keyRow));
		keyRow.Label = button.GetComponentInChildren<Text>();
		_keyRows.Add(keyRow);
	}
}
