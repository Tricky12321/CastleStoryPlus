using System.Collections.Generic;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Components;
using CastleStoryPlus.Core;
using UnityEngine;
using UnityEngine.UI;
using static CastleStoryPlus.UI.UiKit;

namespace CastleStoryPlus.Giant;

// "giant bricktron" window, opened from the button in the right-hand bar: select two workers, see who is upgraded
// and who is sacrificed, the cost and the limit, and upgrade.
[Feature(Features.GiantBricktron, Features.GiantBricktronInfo)]
internal class GiantPanel : MonoBehaviour
{
	private const float RefreshSeconds = 0.3f;

	private static readonly Color Bad = new Color(0.9f, 0.3f, 0.22f, 1f);

	private static GiantPanel _instance;

	private GameObject _window;

	private Text _upgradeText;

	private Text _sacrificeText;

	private Text _costText;

	private Text _limitText;

	private Text _statusText;

	private Button _upgradeButton;

	private float _nextRefresh;

	private static void Enable()
	{
		LuaInjection.AddAction("GiantBricktron", Toggle);
		LuaInjection.AddPatch(Features.GiantBricktron, "LUI/Menus/GameMenu.lua", "_m.mh.calltoarms = h\nend\n", LuaInjection.Mode.InsertAfter, @"

---giant bricktron (Castle Story Plus: upgrade a worker by sacrificing another)
do
local h = ButtonHandle.New()
h.Label = ||""giant bricktron""
h.Icon = ||IconKeys.UI_Plus:Get64()
h.OnAction = ||CastleStoryPlus.GiantBricktron()
h.hasHotkey = false

_m.mg.right:AddChild(h)
_m.mh.giantbricktron = h
end
");
		LuaInjection.AddPatch(Features.GiantBricktron, "LUI/Menus/GameMenu.lua", ".AddMenuHandleToggle(_m.mh.calltoarms)\n", LuaInjection.Mode.InsertAfter, "\t\t.AddMenuHandleToggle(_m.mh.giantbricktron)\n");
	}

	public static void Toggle()
	{
		if (_instance == null)
		{
			GameObject go = new GameObject("GiantPanel");
			_instance = go.AddComponent<GiantPanel>();
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
		_window.SetActive(visible);
		if (visible)
		{
			_statusText.text = string.Empty;
			Refresh();
		}
	}

	private void Update()
	{
		if (_window.activeSelf && Time.unscaledTime >= _nextRefresh)
		{
			Refresh();
		}
	}

	private static List<Labor> SelectedWorkers()
	{
		List<Labor> result = new List<Labor>();
		if (UIGameObserver.bricktrons == null)
		{
			return result;
		}
		foreach (Labor labor in UIGameObserver.bricktrons.CheckSelection())
		{
			if (labor != null)
			{
				result.Add(labor);
			}
		}
		return result;
	}

	private void Refresh()
	{
		_nextRefresh = Time.unscaledTime + RefreshSeconds;
		List<Labor> selected = SelectedWorkers();
		Labor upgrade = null;
		Labor sacrifice = null;
		if (selected.Count == 2)
		{
			GiantBricktron.Order(selected[0], selected[1], out upgrade, out sacrifice);
		}
		_upgradeText.text = "Upgraded: " + ((upgrade != null) ? GiantBricktron.NameOf(upgrade) : "-");
		_sacrificeText.text = "Sacrificed: " + ((sacrifice != null) ? GiantBricktron.NameOf(sacrifice) : "-");
		Faction faction = User.LocalUser != null ? User.LocalUser.faction : null;
		if (faction != null)
		{
			_limitText.text = "giant bricktrons: " + GiantBricktron.CountGiants(faction) + " / " + GiantBricktron.Limit(faction) + " (one per " + GiantBricktron.BricktronsPerGiant.Value + " bricktrons)";
			_costText.text = GiantBricktron.CostText(faction);
		}
		string problem = (selected.Count == 2) ? GiantBricktron.Check(upgrade, sacrifice, out FireflyNest _, out int _) : "Select exactly 2 workers (" + selected.Count + " selected).";
		_upgradeButton.interactable = problem == null;
		if (problem != null)
		{
			_statusText.text = problem;
			_statusText.color = Bad;
		}
		else if (_statusText.color == Bad)
		{
			_statusText.text = "Ready.";
			_statusText.color = Yellow;
		}
	}

	private void DoUpgrade()
	{
		List<Labor> selected = SelectedWorkers();
		if (selected.Count != 2)
		{
			return;
		}
		GiantBricktron.Order(selected[0], selected[1], out Labor upgrade, out Labor sacrifice);
		string name = GiantBricktron.NameOf(upgrade);
		string problem = GiantNetwork.SendUpgrade(upgrade, sacrifice);
		_statusText.text = (problem == null) ? (name + " is now a giant bricktron.") : problem;
		_statusText.color = (problem == null) ? Yellow : Bad;
		_nextRefresh = Time.unscaledTime + 1.5f;
	}

	private void BuildWindow()
	{
		GameObject canvasGo = CreateCanvas("GiantCanvas", transform, 900);
		_window = CreateWindow(canvasGo.transform, 380f);
		RectTransform windowRect = _window.GetComponent<RectTransform>();
		windowRect.anchorMin = new Vector2(1f, 0.5f);
		windowRect.anchorMax = new Vector2(1f, 0.5f);
		windowRect.pivot = new Vector2(1f, 0.5f);
		windowRect.anchoredPosition = new Vector2(-64f, 0f);

		Transform header = CreateRow(_window.transform, 30f);
		Text title = CreateText(header, "GIANT BRICKTRON", 18, Yellow, TextAnchor.MiddleLeft);
		title.fontStyle = FontStyle.Bold;
		Flexible(title.gameObject);
		CreateButton(header, "X", 28f, () => SetVisible(false));

		AddNote("Select 2 workers. The one with the most experience becomes a giant bricktron: " + GiantBricktron.Size.Value + "x as big, everything it does " + GiantBricktron.Speed.Value + "x as fast, " + GiantBricktron.Health.Value + "x health, carries " + GiantBricktron.Carry.Value + "x as much, and it still counts as one bricktron. The other worker is sacrificed and does not come back.");
		_upgradeText = AddLine();
		_sacrificeText = AddLine();
		_costText = AddLine();
		_limitText = AddLine();
		Transform row = CreateRow(_window.transform, 30f);
		_upgradeButton = CreateButton(row, "Upgrade", 0f, DoUpgrade);
		Flexible(_upgradeButton.gameObject);
		_statusText = AddNote(string.Empty);
		_statusText.color = Yellow;
	}

	private Text AddLine()
	{
		Transform row = CreateRow(_window.transform, 22f);
		Text text = CreateText(row, string.Empty, 14, TextColor, TextAnchor.MiddleLeft);
		Flexible(text.gameObject);
		return text;
	}

	private Text AddNote(string text)
	{
		Text note = CreateText(_window.transform, text, 12, Grey, TextAnchor.UpperLeft);
		note.horizontalOverflow = HorizontalWrapMode.Wrap;
		return note;
	}
}
