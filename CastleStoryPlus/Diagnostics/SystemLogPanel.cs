using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Network;
using CastleStoryPlus.Core;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using static CastleStoryPlus.UI.UiKit;

namespace CastleStoryPlus.Diagnostics;

// The system log window, toggled with F9 ([Debug] SystemLogKey). Three tabs:
// - Bricktrons: every bricktron of the player's faction (or of all factions) with its job, activity, task, how long
//   it has been on it and what it carries; idle ones in yellow, fighting ones in red, ones on the same task for over
//   two minutes in orange. Tasks only exist on the host, so a multiplayer client sees jobs and activities only.
// - Problems: errors and warnings of the game, the mod and Harmony, and bricktron tasks that failed (SystemLog).
//   Click one to see its details.
// - Log: the mod's own log lines.
// The window refreshes once a second while it is open.
[Feature(Features.SystemLog, Features.SystemLogInfo)]
internal class SystemLogPanel : MonoBehaviour
{
	private enum Tab
	{
		Bricktrons,
		Problems,
		Log
	}

	private const float RefreshInterval = 1f;

	private const float StuckSeconds = 120f;

	private const int MaxRows = 200;

	private static readonly Color Idle = new Color(1f, 0.85f, 0.3f, 1f);

	private static readonly Color Fighting = new Color(1f, 0.4f, 0.35f, 1f);

	private static readonly Color Stuck = new Color(1f, 0.6f, 0.2f, 1f);

	private static readonly Color Warning = new Color(1f, 0.75f, 0.3f, 1f);

	private static readonly Color Error = new Color(1f, 0.4f, 0.35f, 1f);

	private static ConfigEntry<KeyCode> _key;

	private GameObject _window;

	private Transform _rows;

	private Text _summary;

	private Text _details;

	private GameObject _detailsBox;

	private readonly Dictionary<Tab, Button> _tabButtons = new Dictionary<Tab, Button>();

	private Text _problemsTabLabel;

	private Text _factionLabel;

	private Tab _tab = Tab.Bricktrons;

	private bool _allFactions;

	private float _nextRefresh;

	private SystemLog.Entry _selected;

	private static void Enable()
	{
		_key = Plugin.Cfg.Bind("Debug", "SystemLogKey", KeyCode.F9, "Key that opens the system log window (what every bricktron is doing, problems, the mod's log).");
		Plugin.Root.AddComponent<SystemLogPanel>();
	}

	private void Awake()
	{
		BuildWindow();
		_window.SetActive(false);
	}

	private void Update()
	{
		if (UnityEngine.Input.GetKeyDown(_key.Value))
		{
			SetVisible(!_window.activeSelf);
		}
		if (!_window.activeSelf)
		{
			return;
		}
		if (UnityEngine.Input.GetKeyDown(KeyCode.Escape))
		{
			SetVisible(false);
			return;
		}
		if (Time.unscaledTime >= _nextRefresh)
		{
			Refresh();
		}
	}

	private void SetVisible(bool visible)
	{
		_window.SetActive(visible);
		if (visible)
		{
			Refresh();
		}
	}

	private void ShowTab(Tab tab)
	{
		_tab = tab;
		_selected = null;
		Refresh();
	}

	private void BuildWindow()
	{
		GameObject canvas = CreateCanvas("SystemLogCanvas", transform, 950);
		_window = canvas;
		GameObject window = CreatePanel("SystemLogWindow", canvas.transform);
		RectTransform rect = window.GetComponent<RectTransform>();
		SetRect(rect, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1100f, 700f));
		VerticalLayoutGroup layout = window.AddComponent<VerticalLayoutGroup>();
		layout.padding = new RectOffset(14, 14, 10, 14);
		layout.spacing = 6f;
		layout.childControlWidth = true;
		layout.childControlHeight = true;
		layout.childForceExpandWidth = true;
		layout.childForceExpandHeight = false;

		Transform header = CreateRow(window.transform, 28f);
		Flexible(CreateText(header, "System log", 18, Yellow, TextAnchor.MiddleLeft).gameObject);
		foreach (Tab tab in new Tab[3] { Tab.Bricktrons, Tab.Problems, Tab.Log })
		{
			Tab target = tab;
			Button button = CreateButton(header, tab.ToString(), 120f, () => ShowTab(target));
			_tabButtons[tab] = button;
			if (tab == Tab.Problems)
			{
				_problemsTabLabel = button.GetComponentInChildren<Text>();
			}
		}
		CreateButton(header, "Close", 70f, () => SetVisible(false));

		Transform tools = CreateRow(window.transform, 26f);
		_summary = CreateText(tools, string.Empty, 13, Grey, TextAnchor.MiddleLeft);
		Flexible(_summary.gameObject);
		Button faction = CreateButton(tools, string.Empty, 140f, () =>
		{
			_allFactions = !_allFactions;
			Refresh();
		});
		_factionLabel = faction.GetComponentInChildren<Text>();
		CreateButton(tools, "Clear", 70f, () =>
		{
			if (_tab == Tab.Problems)
			{
				SystemLog.ClearProblems();
			}
			else if (_tab == Tab.Log)
			{
				SystemLog.ClearLog();
			}
			_selected = null;
			Refresh();
		});

		_rows = CreateScrollList(window.transform, out LayoutElement listSize);
		listSize.minHeight = 470f;
		listSize.preferredHeight = 470f;
		listSize.flexibleHeight = 1f;

		_detailsBox = CreatePanel("Details", window.transform);
		_detailsBox.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.25f);
		LayoutElement detailsSize = _detailsBox.AddComponent<LayoutElement>();
		detailsSize.minHeight = 110f;
		detailsSize.preferredHeight = 110f;
		_details = CreateText(_detailsBox.transform, string.Empty, 12, TextColor, TextAnchor.UpperLeft);
		_details.horizontalOverflow = HorizontalWrapMode.Wrap;
		_details.verticalOverflow = VerticalWrapMode.Truncate;
		SetRect(_details.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, new Vector2(-12f, -8f));
	}

	private void Refresh()
	{
		_nextRefresh = Time.unscaledTime + RefreshInterval;
		for (int i = _rows.childCount - 1; i >= 0; i--)
		{
			Destroy(_rows.GetChild(i).gameObject);
		}
		foreach (KeyValuePair<Tab, Button> pair in _tabButtons)
		{
			pair.Value.GetComponent<Image>().color = (pair.Key == _tab) ? new Color(1f, 0.85f, 0.4f, 1f) : Color.white;
		}
		int problems = SystemLog.Problems().Count;
		_problemsTabLabel.text = (problems > 0) ? ("Problems (" + problems + ")") : "Problems";
		_factionLabel.transform.parent.gameObject.SetActive(_tab == Tab.Bricktrons);
		_factionLabel.text = _allFactions ? "All factions" : "My faction";
		_detailsBox.SetActive(_tab != Tab.Bricktrons);
		switch (_tab)
		{
			case Tab.Bricktrons:
				ShowBricktrons();
				break;
			case Tab.Problems:
				ShowEntries(SystemLog.Problems(), "No problems so far.");
				break;
			case Tab.Log:
				ShowEntries(SystemLog.Log(), "Nothing logged yet.");
				break;
		}
	}

	private void ShowBricktrons()
	{
		Faction local = (User.LocalUser != null) ? User.LocalUser.faction : null;
		List<Labor> labors = new List<Labor>();
		foreach (Labor labor in FindObjectsOfType<Labor>())
		{
			if (labor != null && (_allFactions || (local != null && local.IsSame(labor.gameObject))))
			{
				labors.Add(labor);
			}
		}
		labors.Sort((Labor a, Labor b) => string.CompareOrdinal(a.gameObject.name, b.gameObject.name));
		int working = 0;
		int idle = 0;
		int fighting = 0;
		int stuck = 0;
		Header("Name", "Job", "Activity", "Task", "Time", "Carrying");
		foreach (Labor labor in labors)
		{
			Task task = labor.CurrentTask;
			float seconds = (task != null) ? (Time.time - task.TaskStartedTimestamp) : 0f;
			Color color = TextColor;
			switch (labor.Activity)
			{
				case Activity.Fighting:
					fighting++;
					color = Fighting;
					break;
				case Activity.Idle:
				case Activity.Chilling:
					idle++;
					color = Idle;
					break;
				default:
					working++;
					break;
			}
			if (task != null && seconds > StuckSeconds && labor.Activity == Activity.Working)
			{
				stuck++;
				color = Stuck;
			}
			string job = (labor.Occupation != null) ? labor.Occupation.CurrentJob.ToString() : "?";
			string description = (task != null) ? (string.IsNullOrEmpty(task.Description) ? "(task)" : task.Description) : (NetworkServer.active ? "-" : "(host only)");
			string time = (task != null) ? FormatSeconds(seconds) : string.Empty;
			string carrying = (labor.recepteur == null || labor.recepteur.IsEmpty()) ? string.Empty : labor.recepteur.ContentString();
			Row(color, null, labor.gameObject.name, job, labor.Activity.ToString(), description, time, carrying);
			if (_rows.childCount >= MaxRows)
			{
				break;
			}
		}
		_summary.text = labors.Count + " bricktrons: " + working + " working, " + idle + " idle, " + fighting + " fighting" + ((stuck > 0) ? (", " + stuck + " on the same task for over 2 min") : string.Empty);
	}

	private void ShowEntries(List<SystemLog.Entry> entries, string empty)
	{
		int errors = 0;
		int warnings = 0;
		foreach (SystemLog.Entry entry in entries)
		{
			if (entry.Severity == SystemLog.Severity.Error)
			{
				errors++;
			}
			else if (entry.Severity == SystemLog.Severity.Warning)
			{
				warnings++;
			}
		}
		_summary.text = (entries.Count == 0) ? empty : (entries.Count + " entries: " + errors + " errors, " + warnings + " warnings. Click one for details.");
		Header("Time", "Level", "Source", "Message", "Count", string.Empty);
		// Newest first.
		for (int i = entries.Count - 1; i >= 0 && _rows.childCount < MaxRows; i--)
		{
			SystemLog.Entry entry = entries[i];
			Color color = (entry.Severity == SystemLog.Severity.Error) ? Error : ((entry.Severity == SystemLog.Severity.Warning) ? Warning : TextColor);
			string count = (entry.Count > 1) ? ("x" + entry.Count) : string.Empty;
			Row(color, () => Select(entry), entry.Time.ToString("HH:mm:ss"), entry.Severity.ToString(), entry.Source, FirstLine(entry.Message), count, string.Empty);
		}
		ShowDetails();
	}

	private void Select(SystemLog.Entry entry)
	{
		_selected = entry;
		ShowDetails();
	}

	private void ShowDetails()
	{
		if (_selected == null)
		{
			_details.text = "Click an entry to see all of it here.";
			return;
		}
		string text = _selected.Time.ToString("dd-MM-yyyy HH:mm:ss") + "  " + _selected.Severity + "  [" + _selected.Source + "]" + ((_selected.Count > 1) ? ("  x" + _selected.Count) : string.Empty) + "\n" + _selected.Message;
		if (!string.IsNullOrEmpty(_selected.Details))
		{
			text += "\n" + _selected.Details.TrimEnd();
		}
		_details.text = text;
	}

	private void Header(string a, string b, string c, string d, string e, string f)
	{
		Row(Grey, null, a, b, c, d, e, f);
	}

	// Name 170, job 90, activity 80, task (flexible), time 60 (right-aligned), carrying 200. The entry tabs use the
	// same columns for time, level, source, message, count.
	private void Row(Color color, Action onClick, string a, string b, string c, string d, string e, string f)
	{
		Transform row = CreateRow(_rows, 20f);
		if (onClick != null)
		{
			Image hit = row.gameObject.AddComponent<Image>();
			hit.color = new Color(1f, 1f, 1f, 0.03f);
			Button button = row.gameObject.AddComponent<Button>();
			button.targetGraphic = hit;
			button.onClick.AddListener(() => onClick());
		}
		Cell(row, a, 170f, color, TextAnchor.MiddleLeft);
		Cell(row, b, 90f, color, TextAnchor.MiddleLeft);
		Cell(row, c, (_tab == Tab.Bricktrons) ? 80f : 160f, color, TextAnchor.MiddleLeft);
		Text task = CreateText(row, d, 13, color, TextAnchor.MiddleLeft);
		task.horizontalOverflow = HorizontalWrapMode.Wrap;
		task.verticalOverflow = VerticalWrapMode.Truncate;
		Flexible(task.gameObject);
		Cell(row, e, 60f, color, TextAnchor.MiddleRight);
		if (_tab == Tab.Bricktrons)
		{
			Cell(row, f, 200f, color, TextAnchor.MiddleLeft);
		}
	}

	private static void Cell(Transform row, string text, float width, Color color, TextAnchor alignment)
	{
		Text cell = CreateText(row, text ?? string.Empty, 13, color, alignment);
		cell.horizontalOverflow = HorizontalWrapMode.Wrap;
		cell.verticalOverflow = VerticalWrapMode.Truncate;
		Fixed(cell.gameObject, width, 20f);
	}

	private static string FormatSeconds(float seconds)
	{
		int total = Mathf.Max(0, Mathf.FloorToInt(seconds));
		return (total / 60).ToString("00") + ":" + (total % 60).ToString("00");
	}

	private static string FirstLine(string text)
	{
		if (string.IsNullOrEmpty(text))
		{
			return string.Empty;
		}
		int end = text.IndexOf('\n');
		string line = (end < 0) ? text : text.Substring(0, end);
		return (line.Length > 160) ? (line.Substring(0, 160) + "...") : line;
	}
}
