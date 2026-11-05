using System.Collections.Generic;
using BepInEx.Configuration;
using Brix.Components;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.AI.Damage;
using Brix.Game.Bricktron;
using Brix.Game.Components;
using Brix.Game.Names;
using Brix.Game.Network;
using Brix.Game.Semantique;
using Brix.Lifecycle.Pooling;
using Brix.Input;
using Brix.Transactions;
using Brix.Utils;
using CastleStoryPlus.Core;
using CastleStoryPlus.Giant;
using CastleStoryPlus.Menus;
using MoonSharp.Interpreter;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using static CastleStoryPlus.UI.UiKit;

namespace CastleStoryPlus.Diagnostics;

// Debug menu, toggled with F8 ([Debug] DebugMenuKey) while debug mode is on. Host only (single player or the host of a multiplayer game):
// - Building: instant build on or off (the game's own editor mode: placed blueprints are built at once, removed
//   blocks go at once);
// - Energy: adds energy to the home crystal (an unnamed firefly stored in it, as brewing does);
// - Time of day: the time now, set it to midnight, dawn, noon, dusk or night, an hour back or on, and stop or start
//   the sun (synced to the other players);
// - Bricktrons: new builders at the home crystal, heal every own bricktron, the worker trace on or off
//   (BepInEx/workertrace.log: what the workers do, and why they reject or fail tasks; works on clients too);
// - Resources: drops 10, 50 or 100 of a resource on the ground where the camera looks;
// - Enemies: corruptrons where the camera looks, kill every enemy within 30 blocks of it, 100% critrate (every hit and
//   shot of the player's bricktrons is a critical hit, whatever their level; not saved);
// - Waves (invasion): next wave now, +1, +5, +10 or +15 minutes before the next wave, freeze the wave timer, and the fixed time
//   between waves (the choices of the new game screen; saved with the world, a longer current wait is cut to it).
// - Game speed: keys 4 and 5 for 10x and 20x on or off (SpeedKeys);
// - Routes: draws the route searches of the selected bricktrons (RouteView): coarse, straightened and walked.
// - Spawn points (SpawnView): the corruptron spawn points as pillars, and moving them (click a pillar, then the ground);
// - Fog of war: on or off (the game's own switch: everything visible, no fog drawn);
// - X-ray (XRayView): hides the terrain near the camera; semi draws the walls of mines, tunnels and caves, full only the ore.
// Wave commands are queued here and carried out by the invasion server script on its next slow update.
[Feature(Features.DebugMenu, Features.DebugMenuInfo)]
internal class DebugMenu : MonoBehaviour
{
	private const float KillRadius = 30f;

	private const int WaveNext = -1;

	private const int WaveFreeze = -2;

	// Commands at or below this set the time between waves: WaveIntervalCommand - seconds.
	private const int WaveIntervalCommand = -1000;

	private static readonly KeyValuePair<string, Ressource>[] ResourceButtons =
	{
		new KeyValuePair<string, Ressource>("Logs", Adjectif.woodBlock),
		new KeyValuePair<string, Ressource>("Planks", Adjectif.plankBlock),
		new KeyValuePair<string, Ressource>("Stone", Adjectif.stones),
		new KeyValuePair<string, Ressource>("Bricks", Adjectif.stoneBlock),
		new KeyValuePair<string, Ressource>("Fibre", Adjectif.plant),
		new KeyValuePair<string, Ressource>("Rope", Adjectif.rope),
		new KeyValuePair<string, Ressource>("Fabric", Adjectif.fabric),
		new KeyValuePair<string, Ressource>("Raw iron", Adjectif.rawIron),
		new KeyValuePair<string, Ressource>("Iron", Adjectif.iron),
		new KeyValuePair<string, Ressource>("Cogs", Adjectif.cog),
		new KeyValuePair<string, Ressource>("Orange cr.", Adjectif.orangeCrystal),
		new KeyValuePair<string, Ressource>("Blue cr.", Adjectif.blueCrystal),
		new KeyValuePair<string, Ressource>("Glass", Adjectif.glass),
		new KeyValuePair<string, Ressource>("Coal", Adjectif.clay),
		new KeyValuePair<string, Ressource>("Steel", Adjectif.terracotta),
		new KeyValuePair<string, Ressource>("Dark cr.", Adjectif.purifiedBlueCrystal)
	};

	private static readonly KeyValuePair<string, float>[] TimeButtons =
	{
		new KeyValuePair<string, float>("Midnight", 0f),
		new KeyValuePair<string, float>("Dawn 06:00", 6f),
		new KeyValuePair<string, float>("Noon 12:00", 12f),
		new KeyValuePair<string, float>("Dusk 18:00", 18f),
		new KeyValuePair<string, float>("Night 21:00", 21f)
	};

	private static ConfigEntry<KeyCode> _key;

	private static ConfigEntry<KeyCode> _xrayKey;

	// Speed keys 4 (10x) and 5 (20x) in single player (SpeedKeys), switched in this menu.
	internal static ConfigEntry<bool> FastSpeedKeys;

	// Wave state reported by the invasion server script, and the command waiting for it.
	private static float _waveRemaining = -1f;

	private static bool _waveFrozen;

	private static float _waveReportedAt = -100f;

	private static float _waveDuration = -1f;

	private static int _waveCommand;

	private GameObject _window;

	private Text _status;

	private Text _waveText;

	private Text _timeText;

	private Text _sunLabel;

	private Text _amountLabel;

	private Text _traceLabel;

	private Text _routesLabel;

	private Text _fastSpeedLabel;

	private Text _fogLabel;

	private Text _critLabel;

	private Text _routesText;

	private Text _spawnLabel;

	private Text _spawnText;

	private Text _xrayLabel;

	private Text _xrayText;

	private Text _instantLabel;

	private RectTransform _body;

	private LayoutElement _bodySize;

	private int _amount = 10;

	private float _nextRefresh;

	private static void Enable()
	{
		_key = Plugin.Cfg.Bind("Debug", "DebugMenuKey", KeyCode.F8, "Key that opens the debug menu (energy, bricktrons, resources, enemies, waves). Host only.");
		_xrayKey = Plugin.Cfg.Bind("Debug", "XRayKey", KeyCode.F6, "Key that turns the semi x-ray (the tunnels and shafts under the ground) on and off, also without debug mode. Not while placing blocks or typing.");
		// X, the old default, is also the game's "back to work": one press did both.
		if (_xrayKey.Value == KeyCode.X)
		{
			_xrayKey.Value = KeyCode.F6;
		}
		FastSpeedKeys = Plugin.Cfg.Bind("Debug", "FastSpeedKeys", false, "Speed keys 4 and 5 for very fast game speeds ([GameSpeed] Speed4 and Speed5, 10x and 20x) in single player. Switched in the debug menu.");
		LuaInjection.AddFunction("DebugWave", (ScriptExecutionContext context, CallbackArguments args) =>
		{
			DynValue remaining = args[0];
			_waveRemaining = (remaining.Type == DataType.Number) ? (float)remaining.Number : -1f;
			_waveFrozen = args[1].CastToBool();
			_waveDuration = (args[2].Type == DataType.Number) ? (float)args[2].Number : -1f;
			_waveReportedAt = Time.unscaledTime;
			int command = _waveCommand;
			_waveCommand = 0;
			return DynValue.NewNumber(command);
		});
		LuaInjection.AddPatch(Features.DebugMenu, "Gamemodes/invasion/serverside.lua", "Hooks.Connect(hk_OnSerializing, OnSerializing)\n", LuaInjection.Mode.InsertAfter, @"
---Castle Story Plus: wave commands of the debug menu (F8)
Hooks.Connect(hk_SlowUpdate, function()
	local t = Registry and Registry.waveTimer
	local remaining = -1
	if CSP_FrozenWave ~= nil then
		remaining = CSP_FrozenWave
	elseif t ~= nil and t.running then
		remaining = t:RemainingSeconds()
	end
	local command = CastleStoryPlus.DebugWave(remaining, CSP_FrozenWave ~= nil, sv_Settings and sv_Settings.waveDuration)
	if t == nil then
		return
	end
	if command == -1 then
		CSP_FrozenWave = nil
		NextWave()
		return
	elseif command <= -1000 then
		local duration = -1000 - command
		sv_Settings.waveDuration = duration
		if CSP_FrozenWave ~= nil and CSP_FrozenWave > duration then
			CSP_FrozenWave = duration
		elseif CSP_FrozenWave == nil and t.running and remaining > duration then
			t:Start(duration)
			_SendWaveTime(true)
		end
	elseif command == -2 then
		if CSP_FrozenWave ~= nil then
			CSP_FrozenWave = nil
		elseif t.running then
			CSP_FrozenWave = remaining
		end
	elseif command > 0 then
		if CSP_FrozenWave ~= nil then
			CSP_FrozenWave = CSP_FrozenWave + command
		elseif t.running then
			if remaining < 0 then
				remaining = 0
			end
			t:Start(remaining + command)
			_SendWaveTime(true)
		end
	end
	if CSP_FrozenWave ~= nil then
		t:Start(CSP_FrozenWave)
		_SendWaveTime(true)
	end
end)
");
		Plugin.Root.AddComponent<DebugMenu>();
	}

	private void Awake()
	{
		BuildWindow();
		_window.SetActive(false);
	}

	private void Update()
	{
		if (XRayKeyPressed())
		{
			SetXRay((XRayView.Current == XRayView.Mode.Semi) ? XRayView.Mode.Off : XRayView.Mode.Semi);
		}
		// Only with debug mode on (Castle Story Plus settings); turning it off closes the window.
		if (!DebugMode.On)
		{
			if (_window.activeSelf)
			{
				SetVisible(false);
			}
			return;
		}
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
			FitToScreen();
		}
	}

	// The sections' list as tall as its content, but the window no taller than the screen (UiKit.MaxWindowHeight).
	private void FitToScreen()
	{
		RectTransform window = (RectTransform)_bodySize.transform.parent;
		_bodySize.preferredHeight = 0f;
		LayoutRebuilder.ForceRebuildLayoutImmediate(_body);
		LayoutRebuilder.ForceRebuildLayoutImmediate(window);
		float content = LayoutUtility.GetPreferredHeight(_body);
		float rest = LayoutUtility.GetPreferredHeight(window);
		_bodySize.preferredHeight = Mathf.Min(content, MaxWindowHeight - rest);
		LayoutRebuilder.ForceRebuildLayoutImmediate(window);
	}

	private void BuildWindow()
	{
		GameObject canvas = CreateCanvas("DebugMenuCanvas", transform, 950);
		_window = canvas;
		GameObject window = CreatePanel("DebugMenuWindow", canvas.transform);
		Draggable(window);
		SetRect(window.GetComponent<RectTransform>(), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-300f, 0f), new Vector2(560f, 870f));
		VerticalLayoutGroup layout = window.AddComponent<VerticalLayoutGroup>();
		layout.padding = new RectOffset(14, 14, 10, 14);
		layout.spacing = 6f;
		layout.childControlWidth = true;
		layout.childControlHeight = true;
		layout.childForceExpandWidth = true;
		layout.childForceExpandHeight = false;
		// As tall as its sections (it grew past a fixed height as sections were added).
		window.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

		Transform header = CreateRow(window.transform, 28f);
		Flexible(CreateText(header, "Debug menu", 18, Yellow, TextAnchor.MiddleLeft).gameObject);
		CreateButton(header, "Close", 70f, () => SetVisible(false));
		_status = CreateText(CreateRow(window.transform, 20f), string.Empty, 12, Grey, TextAnchor.MiddleLeft);
		Flexible(_status.gameObject);
		// The sections scroll under the header once they are taller than the screen (FitToScreen).
		Transform body = CreateScrollList(window.transform, out _bodySize);
		_body = (RectTransform)body;

		Section(body, "Building");
		Transform building = CreateRow(body, 26f);
		Button instant = CreateButton(building, string.Empty, 160f, () =>
		{
			// The game's own editor mode (its debug sidebar's instant build): what is placed is built at once, without
			// workers or materials, and what is removed goes at once. This machine only; off again when the game is left.
			Brix.Engine.Architecte.editorMode = !Brix.Engine.Architecte.editorMode;
			Brix.Engine.GameSignals.Invoke(Brix.Engine.GameSignals.OnEditorModeChanged, Brix.Engine.Architecte.editorMode);
			SetStatus(Brix.Engine.Architecte.editorMode ? "Instant build on: what you place is built at once, what you remove goes at once." : "Instant build off.");
			Refresh();
		});
		_instantLabel = instant.GetComponentInChildren<Text>();

		Section(body, "Energy (home crystal)");
		Transform energy = CreateRow(body, 26f);
		foreach (int amount in new int[4] { 100, 500, 1000, 5000 })
		{
			int value = amount;
			CreateButton(energy, "+" + amount, 90f, () => Run(() => GiveEnergy(value)));
		}

		Section(body, "Time of day");
		Transform timeRow = CreateRow(body, 26f);
		_timeText = CreateText(timeRow, string.Empty, 13, TextColor, TextAnchor.MiddleLeft);
		Fixed(_timeText.gameObject, 150f, 26f);
		CreateButton(timeRow, "-1 h", 60f, () => Run(() => ShiftTime(-1f)));
		CreateButton(timeRow, "+1 h", 60f, () => Run(() => ShiftTime(1f)));
		Button sun = CreateButton(timeRow, string.Empty, 130f, () => Run(ToggleSun));
		_sunLabel = sun.GetComponentInChildren<Text>();
		Transform times = CreateRow(body, 26f);
		foreach (KeyValuePair<string, float> time in TimeButtons)
		{
			float hour = time.Value;
			CreateButton(times, time.Key, 96f, () => Run(() => SetTime(hour)));
		}

		Section(body, "Bricktrons");
		Transform units = CreateRow(body, 26f);
		CreateButton(units, "+1 builder", 100f, () => Run(() => SpawnBuilders(1)));
		CreateButton(units, "+5 builders", 100f, () => Run(() => SpawnBuilders(5)));
		CreateButton(units, "Heal all mine", 120f, () => Run(HealOwn));
		Button trace = CreateButton(units, string.Empty, 150f, () =>
		{
			if (WorkerTrace.Active)
			{
				WorkerTrace.Stop();
			}
			else
			{
				WorkerTrace.Start();
			}
			SetStatus(WorkerTrace.Active ? "Worker trace on: BepInEx/workertrace.log" : "Worker trace off.");
			Refresh();
		});
		_traceLabel = trace.GetComponentInChildren<Text>();

		Section(body, "Game speed (single player)");
		Transform speedRow = CreateRow(body, 26f);
		Button fastSpeed = CreateButton(speedRow, string.Empty, 260f, () =>
		{
			FastSpeedKeys.Value = !FastSpeedKeys.Value;
			SetStatus(FastSpeedKeys.Value ? "Keys 4 and 5: 10x and 20x speed." : "Keys 4 and 5 off.");
			Refresh();
		});
		_fastSpeedLabel = fastSpeed.GetComponentInChildren<Text>();

		Section(body, "Routes (all your bricktrons)");
		Transform routes = CreateRow(body, 26f);
		Button routesButton = CreateButton(routes, string.Empty, 130f, () =>
		{
			RouteView.Toggle();
			SetStatus(RouteView.Enabled ? "Routes on: every route your bricktrons search from now on is drawn." : "Routes off.");
			Refresh();
		});
		_routesLabel = routesButton.GetComponentInChildren<Text>();
		Legend(routes, "coarse", RouteView.CoarseColor);
		Legend(routes, "straightened", RouteView.ImprovedColor);
		Legend(routes, "walked", RouteView.FinalColor);
		Legend(routes, "next task", RouteView.NextColor);
		_routesText = CreateText(CreateRow(body, 20f), string.Empty, 12, Grey, TextAnchor.MiddleLeft);
		Flexible(_routesText.gameObject);

		Section(body, "Spawn points (invasion)");
		Transform spawnRow = CreateRow(body, 26f);
		Button spawnButton = CreateButton(spawnRow, string.Empty, 150f, () =>
		{
			SpawnView.Toggle();
			SetStatus(SpawnView.Enabled ? "Spawn points shown: click a pillar, then the ground, to move it." : "Spawn points hidden.");
			Refresh();
		});
		_spawnLabel = spawnButton.GetComponentInChildren<Text>();
		Legend(spawnRow, "may spawn", SpawnView.ClearColor);
		Legend(spawnRow, "too close", SpawnView.BlockedColor);
		Legend(spawnRow, "drawn last", SpawnView.LastColor);
		_spawnText = CreateText(CreateRow(body, 20f), string.Empty, 12, Grey, TextAnchor.MiddleLeft);
		Flexible(_spawnText.gameObject);

		Section(body, "Fog of war");
		Transform fogRow = CreateRow(body, 26f);
		Button fog = CreateButton(fogRow, string.Empty, 160f, () =>
		{
			// The game's own switch (as its Lua SetFogOfWar): everything visible, the fog drawn no more.
			LocalFactionVision.ToggleFogSystem(!LocalFactionVision.IsEnabled());
			SetStatus(LocalFactionVision.IsEnabled() ? "Fog of war on." : "Fog of war off: everything is visible.");
			Refresh();
		});
		_fogLabel = fog.GetComponentInChildren<Text>();

		Section(body, "X-ray (this machine's view only)");
		Transform xray = CreateRow(body, 26f);
		CreateButton(xray, "Off", 50f, () => SetXRay(XRayView.Mode.Off));
		CreateButton(xray, "Semi: caves", 100f, () => SetXRay(XRayView.Mode.Semi));
		CreateButton(xray, "Full: ore", 80f, () => SetXRay(XRayView.Mode.Full));
		_xrayLabel = CreateText(xray, string.Empty, 12, TextColor, TextAnchor.MiddleLeft);
		Fixed(_xrayLabel.gameObject, 90f, 26f);
		Legend(xray, "orange", XRayView.OrangeColor);
		Legend(xray, "blue", XRayView.BlueColor);
		Legend(xray, "iron", XRayView.IronColor);
		Legend(xray, "coal", Grey);
		_xrayText = CreateText(CreateRow(body, 20f), string.Empty, 12, Grey, TextAnchor.MiddleLeft);
		Flexible(_xrayText.gameObject);

		Section(body, "Resources (dropped where the camera looks)");
		Transform amountRow = CreateRow(body, 26f);
		_amountLabel = CreateText(amountRow, string.Empty, 13, TextColor, TextAnchor.MiddleLeft);
		Fixed(_amountLabel.gameObject, 90f, 26f);
		foreach (int amount in new int[3] { 10, 50, 100 })
		{
			int value = amount;
			CreateButton(amountRow, value.ToString(), 60f, () =>
			{
				_amount = value;
				Refresh();
			});
		}
		Transform row = null;
		for (int i = 0; i < ResourceButtons.Length; i++)
		{
			if (i % 5 == 0)
			{
				row = CreateRow(body, 26f);
			}
			KeyValuePair<string, Ressource> resource = ResourceButtons[i];
			CreateButton(row, resource.Key, 100f, () => Run(() => DropResource(resource.Value, resource.Key)));
		}

		Section(body, "Enemies (where the camera looks)");
		Transform enemies = CreateRow(body, 26f);
		CreateButton(enemies, "+1 corruptron", 110f, () => Run(() => SpawnEnemies(1)));
		CreateButton(enemies, "+5 corruptrons", 115f, () => Run(() => SpawnEnemies(5)));
		CreateButton(enemies, "Kill enemies near", 135f, () => Run(KillEnemies));
		Button crit = CreateButton(enemies, string.Empty, 140f, () =>
		{
			CastleStoryPlus.Combat.CriticalHits.Forced = !CastleStoryPlus.Combat.CriticalHits.Forced;
			SetStatus(CastleStoryPlus.Combat.CriticalHits.Forced ? "Every hit and shot of your bricktrons is a critical hit." : "Critical hits by level again.");
			Refresh();
		});
		_critLabel = crit.GetComponentInChildren<Text>();

		Section(body, "Invasion waves");
		_waveText = CreateText(CreateRow(body, 20f), string.Empty, 13, TextColor, TextAnchor.MiddleLeft);
		Flexible(_waveText.gameObject);
		Transform waves = CreateRow(body, 26f);
		CreateButton(waves, "Next wave now", 110f, () => QueueWave(WaveNext));
		CreateButton(waves, "+1 min", 60f, () => QueueWave(60));
		CreateButton(waves, "+5 min", 60f, () => QueueWave(300));
		CreateButton(waves, "+10 min", 60f, () => QueueWave(600));
		CreateButton(waves, "+15 min", 60f, () => QueueWave(900));
		CreateButton(waves, "Freeze / resume", 120f, () => QueueWave(WaveFreeze));
		Transform interval = CreateRow(body, 26f);
		Fixed(CreateText(interval, "Waves every", 13, TextColor, TextAnchor.MiddleLeft).gameObject, 90f, 26f);
		foreach (int minutes in WaveInterval.Minutes)
		{
			int value = minutes;
			CreateButton(interval, value + " min", 70f, () => QueueWave(WaveIntervalCommand - value * 60));
		}
	}

	// The x-ray key, in a game and not while placing blocks (BlockAnchor's key), in a menu or typing in a text field.
	private static bool XRayKeyPressed()
	{
		if (_xrayKey == null || _xrayKey.Value == KeyCode.None || !UnityEngine.Input.GetKeyDown(_xrayKey.Value))
		{
			return false;
		}
		if (Brix.Engine.Voxel.Engine == null || InputModeController._mode == InputMode.blueprint || InputModeController.IsInMenu())
		{
			return false;
		}
		if (UnityEngine.Input.GetKey(KeyCode.LeftControl) || UnityEngine.Input.GetKey(KeyCode.RightControl) || UnityEngine.Input.GetKey(KeyCode.LeftAlt) || UnityEngine.Input.GetKey(KeyCode.RightAlt))
		{
			return false;
		}
		GameObject focused = (UnityEngine.EventSystems.EventSystem.current != null) ? UnityEngine.EventSystems.EventSystem.current.currentSelectedGameObject : null;
		InputField field = (focused != null) ? focused.GetComponent<InputField>() : null;
		return field == null || !field.isFocused;
	}

	private void SetXRay(XRayView.Mode mode)
	{
		XRayView.Set(mode);
		SetStatus(mode == XRayView.Mode.Off ? "X-ray off." : (mode == XRayView.Mode.Semi ? "X-ray: the walls of mines, tunnels and caves" : "X-ray: only the ore") + " within " + XRayView.Radius + " blocks of where the camera looks.");
		Refresh();
	}

	private static void Legend(Transform parent, string name, Color color)
	{
		Text text = CreateText(parent, name, 12, color, TextAnchor.MiddleLeft);
		Fixed(text.gameObject, name.Length * 8f + 10f, 26f);
	}

	private static void Section(Transform parent, string title)
	{
		Text text = CreateText(CreateRow(parent, 24f), title, 14, Yellow, TextAnchor.LowerLeft);
		Flexible(text.gameObject);
	}

	private void Refresh()
	{
		_nextRefresh = Time.unscaledTime + 0.5f;
		_amountLabel.text = "Amount: " + _amount;
		Journee day = Journee.instance;
		_timeText.text = (day != null) ? "Now " + Clock(day.Heure) + (day.IsDayTime ? " (day)" : " (night)") : "No day cycle";
		_sunLabel.text = (day != null && day.IsPaused) ? "Sun: stopped" : "Sun: moving";
		_traceLabel.text = WorkerTrace.Active ? "Worker trace: on" : "Worker trace: off";
		_routesLabel.text = RouteView.Enabled ? "Routes: on" : "Routes: off";
		_fastSpeedLabel.text = FastSpeedKeys.Value ? "Keys 4/5 = 10x/20x: on" : "Keys 4/5 = 10x/20x: off";
		_instantLabel.text = Brix.Engine.Architecte.editorMode ? "Instant build: on" : "Instant build: off";
		_fogLabel.text = LocalFactionVision.IsEnabled() ? "Fog of war: on" : "Fog of war: off";
		_critLabel.text = CastleStoryPlus.Combat.CriticalHits.Forced ? "100% critrate: on" : "100% critrate: off";
		_routesText.text = RouteView.Enabled ? RouteView.Summary : string.Empty;
		_spawnLabel.text = SpawnView.Enabled ? "Spawn points: on" : "Spawn points: off";
		_spawnText.text = SpawnView.Enabled ? SpawnView.Summary : string.Empty;
		_xrayLabel.text = "X-ray: " + XRayView.Current.ToString().ToLower();
		_xrayText.text = XRayView.Summary;
		if (!NetworkServer.active)
		{
			SetStatus("Host only: these commands run on the host.");
		}
		bool waveKnown = Time.unscaledTime - _waveReportedAt < 5f;
		if (!waveKnown)
		{
			_waveText.text = "No invasion wave timer (invasion games only, host only).";
		}
		else if (_waveRemaining < 0f)
		{
			_waveText.text = "The wave timer has not started yet.";
		}
		else
		{
			int seconds = Mathf.Max(0, Mathf.CeilToInt(_waveRemaining));
			_waveText.text = "Next wave in " + (seconds / 60) + ":" + (seconds % 60).ToString("00") + (_waveFrozen ? " (frozen)" : string.Empty) + ((_waveDuration > 0f) ? ", waves every " + Mathf.RoundToInt(_waveDuration / 60f) + " min" : string.Empty) + ((_waveCommand != 0) ? " - command sent" : string.Empty);
		}
	}

	private void SetStatus(string message)
	{
		_status.text = message;
	}

	private void Run(System.Func<string> command)
	{
		if (!NetworkServer.active)
		{
			SetStatus("Host only: these commands run on the host.");
			return;
		}
		string result;
		try
		{
			result = command();
		}
		catch (System.Exception e)
		{
			Plugin.Log.LogError("Debug menu: " + e);
			result = "Failed: " + e.Message;
		}
		SetStatus(result);
		Plugin.Log.LogInfo("Debug menu: " + result);
	}

	private void QueueWave(int command)
	{
		if (!NetworkServer.active)
		{
			SetStatus("Host only: these commands run on the host.");
			return;
		}
		if (Time.unscaledTime - _waveReportedAt >= 5f)
		{
			SetStatus("No invasion wave timer in this game.");
			return;
		}
		_waveCommand = command;
		string message;
		if (command == WaveNext)
		{
			message = "Next wave: starting.";
		}
		else if (command == WaveFreeze)
		{
			message = "Wave timer: freeze / resume.";
		}
		else if (command <= WaveIntervalCommand)
		{
			message = "Waves every " + ((WaveIntervalCommand - command) / 60) + " min.";
		}
		else
		{
			message = "Wave timer: +" + (command / 60) + " min.";
		}
		SetStatus(message);
		Refresh();
	}

	// Hours as 24-hour HH:mm.
	private static string Clock(float hours)
	{
		int minutes = Mathf.FloorToInt(Mathf.Repeat(hours, 24f) * 60f);
		return (minutes / 60).ToString("00") + ":" + (minutes % 60).ToString("00");
	}

	private string SetTime(float hours)
	{
		Journee day = Journee.instance;
		if (day == null)
		{
			return "No day cycle in this game.";
		}
		day.SetTime(Mathf.Repeat(hours, 24f));
		if (UNetJourneeSync.instance != null)
		{
			UNetJourneeSync.instance.ForceUpdateHeure();
		}
		Refresh();
		return "Time of day: " + Clock(day.Heure) + ".";
	}

	private string ShiftTime(float hours)
	{
		Journee day = Journee.instance;
		return (day != null) ? SetTime(day.Heure + hours) : "No day cycle in this game.";
	}

	private string ToggleSun()
	{
		Journee day = Journee.instance;
		if (day == null)
		{
			return "No day cycle in this game.";
		}
		day.IsPaused = !day.IsPaused;
		Refresh();
		return day.IsPaused ? "The sun stands still." : "The sun moves again.";
	}

	private static Faction LocalFaction()
	{
		return (User.LocalUser != null) ? User.LocalUser.faction : null;
	}

	private static string GiveEnergy(int amount)
	{
		FireflyNest nest = GiantBricktron.HomeNest(LocalFaction());
		if (nest == null)
		{
			return "No home crystal.";
		}
		// As brewing does it: an unnamed firefly holding the energy, stored in the crystal.
		GameObject go = Transactor.SpawnUNet(Brix.External.Factories.Environment.Firefly, nest.faction, nest.transform.position + Vector3.up, Quaternion.identity);
		if (go == null)
		{
			return "Could not make a firefly.";
		}
		go.GetComponent<Firefly>().pureEnergy = amount;
		nest.mainRecepteur.AddObject(go, true);
		return "+" + amount + " energy (" + nest.GetAvailablePurifiedEnergy() + " in the crystal).";
	}

	private static string SpawnBuilders(int count)
	{
		Faction faction = LocalFaction();
		FireflyNest nest = GiantBricktron.HomeNest(faction);
		if (nest == null)
		{
			return "No home crystal.";
		}
		for (int i = 0; i < count; i++)
		{
			Vector3 position = nest.transform.position + Vector3.up * 4f + new Vector3(Random.Range(-1.5f, 1.5f), 0f, Random.Range(-1.5f, 1.5f));
			GameObject go = Transactor.SpawnUNet(Test.Bricktron, faction, position, Quaternion.identity);
			if (go == null)
			{
				continue;
			}
			Nom nom = go.GetComponent<Nom>();
			if (nom != null)
			{
				nom.SetNom(NameDispenserGO.GetDispenserGO(NameDispenserGO.Dispensers.Bricktrons).GetName());
			}
			go.GetComponent<Occupation>().SetOccupation(Occupation.Type.Builder, true);
		}
		return "+" + count + " builder" + ((count == 1) ? string.Empty : "s") + " at the home crystal.";
	}

	private static string HealOwn()
	{
		Faction faction = LocalFaction();
		int healed = 0;
		foreach (Labor labor in Live<Labor>.Active())
		{
			if (labor == null || faction == null || !faction.IsSame(labor.gameObject))
			{
				continue;
			}
			BricktronDamageReceiver receiver = labor.GetComponent<BricktronDamageReceiver>();
			if (receiver != null && receiver.CurrentHP > 0f && receiver.CurrentHP < receiver.MaxHP)
			{
				receiver.CurrentHP = receiver.MaxHP;
				healed++;
			}
		}
		return "Healed " + healed + " bricktrons.";
	}

	private string DropResource(Ressource resource, string name)
	{
		Factory.AssetKey key = Description.RepresentativeOf(resource);
		if (key == null)
		{
			return "No item for " + name + ".";
		}
		Vector3 center = Target();
		List<GameObject> dropped = new List<GameObject>();
		for (int i = 0; i < _amount; i++)
		{
			GameObject go = Transactor.SpawnUNet(key, null, SpawnSpread.Place(center, i, _amount), Quaternion.identity);
			if (go != null)
			{
				SpawnSpread.Calm(go);
				dropped.Add(go);
			}
		}
		if (dropped.Count > 0)
		{
			LogStorage(dropped[0], resource, name);
		}
		return "Dropped " + dropped.Count + " " + name + ".";
	}

	// Where the dropped kind could be stored: the item, every own stockpile's capacity for it and whether it has room,
	// and whether the workers can carry it (the log line "storage check").
	private static void LogStorage(GameObject item, Ressource resource, string name)
	{
		Faction faction = LocalFaction();
		IDescriptor descriptor = item.GetComponent<IDescriptor>();
		string text = name + ": item " + item.name + " holds " + ((descriptor != null && descriptor.Description != null) ? WorkerTraceDetails.Describe(descriptor.Description) : "?");
		AutoList list = BrixSingleton<AutoList>.Instance;
		HashSet<GameObject> piles = (list != null) ? list.GetInstances(ObjetsDynamiques.Palette) : null;
		int own = 0;
		int withRoom = 0;
		List<string> details = new List<string>();
		if (piles != null)
		{
			foreach (GameObject pile in piles)
			{
				Recepteur recepteur = (pile != null && pile.activeInHierarchy && Faction.GetFaction(pile) == faction) ? pile.GetComponent<Recepteur>() : null;
				if (recepteur == null)
				{
					continue;
				}
				own++;
				bool room = recepteur.HasRoomFor(item);
				if (room)
				{
					withRoom++;
				}
				if (details.Count < 12)
				{
					details.Add((room ? "room" : "no room") + " (base " + recepteur.BaseCapacity.Value(resource) + ", with exclusions " + recepteur.BaseCapacityWithExclusions.Value(resource) + ", current " + recepteur.CurrentCapacity.Value(resource) + ", holds " + WorkerTraceDetails.Describe(recepteur.ContentDescription) + ")");
				}
			}
		}
		Labor worker = null;
		foreach (Labor labor in Live<Labor>.Active())
		{
			if (labor != null && labor.faction == faction && labor.recepteur != null)
			{
				worker = labor;
				break;
			}
		}
		text += " | own stockpiles " + own + ", with room " + withRoom + ": " + string.Join("; ", details.ToArray());
		if (worker != null)
		{
			text += " | worker " + worker.name + " can carry it: " + worker.recepteur.HasRoomFor(item) + ", best storage: " + ((worker.BestRecepteurToStore(item) != null) ? worker.BestRecepteurToStore(item).name : "none");
		}
		Plugin.Log.LogInfo("Debug menu: storage check " + text);
	}

	private static string SpawnEnemies(int count)
	{
		Faction enemy = Faction.FindByName("Corruptrons") ?? Faction.DefaultEnemy;
		if (enemy == null)
		{
			return "No enemy faction in this game.";
		}
		Vector3 center = Target();
		for (int i = 0; i < count; i++)
		{
			Vector3 position = center + Vector3.up + new Vector3(Random.Range(-2f, 2f), 0f, Random.Range(-2f, 2f));
			GameObject go = Transactor.SpawnUNet(Test.Corruptron, enemy, position, Quaternion.identity);
			if (go != null)
			{
				go.GetComponent<Occupation>().SetOccupation(Occupation.Type.Corruptron, true);
			}
		}
		return "+" + count + " corruptron" + ((count == 1) ? string.Empty : "s") + ".";
	}

	private static string KillEnemies()
	{
		Faction local = LocalFaction();
		Vector3 center = Target();
		int killed = 0;
		foreach (Locomotion4 unit in FindObjectsOfType<Locomotion4>())
		{
			if (unit == null || unit.IsCorpse || !unit.gameObject.activeInHierarchy || (local != null && local.IsSame(unit.gameObject)))
			{
				continue;
			}
			Faction faction = Faction.GetFaction(unit.gameObject);
			if (faction == null || !faction.isAI || (unit.transform.position - center).sqrMagnitude > KillRadius * KillRadius)
			{
				continue;
			}
			unit.Kill(center, Vector3.up);
			killed++;
		}
		return "Killed " + killed + " enemies within " + KillRadius + " blocks.";
	}

	// The ground under the point the camera looks at.
	private static Vector3 Target()
	{
		Vector3 target = CameraScript.Target;
		VoxelRaycastHit hit = new VoxelRaycastHit();
		LayerBundle layers = new LayerBundle((int)UnityLayer.Terrain, (int)UnityLayer.FreeBlocks);
		if (hit.PlaceAtRaycast(new Ray(target + Vector3.up * 60f, Vector3.down), 200f, layers))
		{
			return hit.WorldHitVector + Vector3.up * 0.5f;
		}
		return target;
	}
}
