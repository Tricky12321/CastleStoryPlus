using System.Collections.Generic;
using Brix.External.Factories;
using Brix.Lua;
using Brix.UI.Builder.Menu.Component;
using Brix.UI.Icons;
using CastleStoryPlus.Core;
using CastleStoryPlus.UI;
using MoonSharp.Interpreter;
using UnityEngine;
using UnityEngine.UI;

namespace CastleStoryPlus.LuaUi;

// [LuaUi] CsScoreboard: the gamemode's top-right panel (GS_Scoreboard) drawn in C# for invasion, the tutorial
// (which uses the invasion panel) and sandbox. The game's Lua panel (InvasionMenu.lua, SandboxMenu.lua and their
// panels) took seconds to build when a map loads. Each gamemode's ui.lua asks CastleStoryPlus.CsScoreboard first;
// for other gamemodes (conquest, credit island, grab the gems) it answers false and the game's Lua panel is built
// as before. The values are read through a small Lua function run in the gamemode's own script (the same Registry,
// Data.Storage and raids the Lua panel read), a few times a second; only the drawing is C#. The view is a child of
// GS_Scoreboard, so GameMenu hiding the scoreboard while a left sidebar is open (GS_Scoreboard:SetVisible, its
// CanvasGroup) hides it too.
[Feature(Features.CsMenus, Features.CsMenusInfo)]
internal class ScoreboardView : MonoBehaviour
{
	// The gamemode script's side: values for the view, the next wave action and the sidebar spacer flag.
	private const string LuaSide = @"
require(""LUI2.lua"")
if CastleStoryPlus_ScoreboardState == nil then
	local _s = { waveTotals = {}, spacer = false }
	CastleStoryPlus_ScoreboardState = _s
	Hooks.Connect(UIGame.hk_SetSidebarSpacer, function(flag) if type(flag) == ""table"" then flag = flag[1] end _s.spacer = flag and true or false end)
	Hooks.Connect(FactionStorage.hk_OnSetContent, function() Data.Storage:OnSetContent() end)
	Data.Storage:OnSetContent()

	function CastleStoryPlus_ScoreboardNext()
		if IsServer() then
			Picking.ResetCommandMode()
			NextWave()
		end
	end

	function CastleStoryPlus_Scoreboard(withWaves)
		local d = {}
		d.spacer = _s.spacer
		d.mode = UIGame.GetModeName()
		d.server = IsServer()
		d.elapsed = tostring(Registry.gameTimer:FormatedElapsedSeconds(Registry.gameTimerOffset or 0))
		d.noRoom = not Data.Storage:HasResourceCount(Resource.None)
		local names = """"
		if IsMultiplayer() then
			local users = fy_User:GetAll()
			for i = 1, #users do
				if i > 1 then names = names .. "" & "" end
				names = names .. users[i].name
			end
		end
		d.names = names
		d.minitrons = Registry.minitronKillCount or 0
		d.corruptrons = Registry.corruptronKillCount or 0
		d.biftrons = Registry.biftronKillCount or 0
		d.magitrons = Registry.magitronKillCount or 0
		d.survived = Registry.waveSurvived or 0
		if Registry.waveTimer ~= nil then
			d.timer = Registry.waveTimer:FormatedRemainingSeconds()
			d.timerSeconds = Registry.waveTimer:RemainingSeconds()
		end
		if withWaves then
			local counts = {}
			local raids = fy_RaidProject:GetAllOfFaction(Faction.defaultEnemy)
			for r = 1, #raids do
				local crew = Project.GetCrew(raids[r])
				for c = 1, #crew do
					local labor = crew[c]
					local wave = labor.waveNumber or ((Registry.waveNumber or 1) - 1)
					local t = counts[wave]
					if t == nil then t = { 0, 0, 0, 0 } counts[wave] = t end
					local oc = labor:GetOccupation()
					if oc == Occupations.Minitron then t[1] = t[1] + 1
					elseif oc == Occupations.Corruptron then t[2] = t[2] + 1
					elseif oc == Occupations.Biftron then t[3] = t[3] + 1
					elseif oc == Occupations.Magitron then t[4] = t[4] + 1 end
				end
			end
			local ids = {}
			for wave, t in pairs(counts) do
				if t[1] + t[2] + t[3] + t[4] > 0 then
					table.insert(ids, wave)
					local total = _s.waveTotals[wave]
					if total == nil then total = { 0, 0, 0, 0 } _s.waveTotals[wave] = total end
					for i = 1, 4 do if t[i] > total[i] then total[i] = t[i] end end
				end
			end
			table.sort(ids)
			local waves = {}
			for i = 1, #ids do
				local wave = ids[i]
				local t = counts[wave]
				local total = _s.waveTotals[wave]
				table.insert(waves, wave)
				for k = 1, 4 do table.insert(waves, t[k]) end
				for k = 1, 4 do table.insert(waves, total[k]) end
			end
			d.waves = waves
		end
		return d
	end
end
";

	private const float Width = 272f;

	private const float RefreshSeconds = 0.25f;

	// Waves (the raids' crews) are counted every this many refreshes.
	private const int WavesEvery = 4;

	private static readonly Color TimerRed = new Color(1f, 0f, 0.25f, 1f);

	private static readonly Color RowBackground = new Color(0.07f, 0.07f, 0.07f, 0.98f);

	private static ScoreboardView _current;

	private Script _script;

	private bool _invasion;

	private RectTransform _root;

	private float _nextRefresh;

	private int _refreshes;

	private Text _mode;

	private Text _modeTime;

	private Text _names;

	private GameObject _alert;

	private Text _survived;

	private Text _kills;

	private Text _groupKills;

	private Text _minitronKills;

	private Text _corruptronKills;

	private Text _biftronKills;

	private Text _magitronKills;

	private RectTransform _waveList;

	private readonly List<WaveRow> _waveRows = new List<WaveRow>();

	private Text _timer;

	private GameObject _nextButton;

	private string _waveName;

	private string _killLabel;

	private string _survivedOne;

	private string _survivedMany;

	private class WaveRow
	{
		public GameObject Root;

		public Text Name;

		public Text Group;

		public Text[] Kinds = new Text[4];
	}

	private static void Enable()
	{
		if (!LuaUiConfig.CsScoreboard)
		{
			return;
		}
		LuaInjection.AddFunction("CsScoreboard", (ScriptExecutionContext context, CallbackArguments args) =>
		{
			string mode = (args.Count > 0 && args[0].Type == DataType.String) ? args[0].String : string.Empty;
			LuaMenuComponent component = (args.Count > 1 && !args[1].IsNil()) ? args.AsComponent<LuaMenuComponent>(1, "CsScoreboard", allowNil: true) : null;
			return DynValue.NewBoolean(Show(mode, component, context.OwnerScript));
		});
		Patch("Gamemodes/invasion/invasion_ui.lua", "invasion");
		Patch("Gamemodes/tutorial/invasion_ui.lua", "tutorial");
		Patch("Gamemodes/sandbox/ui.lua", "sandbox");
		GameSession.OnLeave(Drop);
	}

	private static void Patch(string file, string mode)
	{
		LuaInjection.AddPatch(Features.CsMenus, file, "GS_Scoreboard:LoadMenuFromClosure(LoadScoreboardMenu)\n", LuaInjection.Mode.Replace, "if not CastleStoryPlus.CsScoreboard(\"" + mode + "\", GS_Scoreboard) then GS_Scoreboard:LoadMenuFromClosure(LoadScoreboardMenu) end\n");
	}

	// Builds the view for the gamemode; false (the Lua panel is built instead) for a gamemode it does not know or
	// when the gamemode script's side cannot be set up.
	private static bool Show(string mode, LuaMenuComponent component, Script script)
	{
		bool invasion = mode == "invasion" || mode == "tutorial";
		if ((!invasion && mode != "sandbox") || script == null)
		{
			return false;
		}
		try
		{
			script.DoString(LuaSide);
		}
		catch (InterpreterException ex)
		{
			LuaBridge.LogOnce("CsScoreboard", ex.DecoratedMessage ?? ex.Message);
			return false;
		}
		Drop();
		Transform parent = (component != null) ? component.transform : LuiCanvas.Instance.Menus;
		GameObject go = new GameObject("CSP_Scoreboard", typeof(RectTransform));
		go.transform.SetParent(parent, false);
		_current = go.AddComponent<ScoreboardView>();
		_current._script = script;
		_current._invasion = invasion;
		_current.Build();
		script.OnScriptKilled += OnScriptKilled;
		RectTransform rect = parent as RectTransform;
		Plugin.Log.LogInfo("CsMenus: scoreboard (" + mode + ") drawn in C#" + ((rect != null) ? (", under " + parent.name + " " + rect.rect.size + " anchors " + rect.anchorMin + "-" + rect.anchorMax) : string.Empty));
		return true;
	}

	private static void OnScriptKilled(Script script)
	{
		if (_current != null && _current._script == script)
		{
			Drop();
		}
	}

	private static void Drop()
	{
		if (_current == null)
		{
			return;
		}
		if (_current._script != null)
		{
			_current._script.OnScriptKilled -= OnScriptKilled;
		}
		Destroy(_current.gameObject);
		_current = null;
	}

	private void Build()
	{
		_waveName = LuaBridge.Localized("##mode_invasion_wave");
		_killLabel = LuaBridge.Localized("##mode_invasion_killcount");
		_survivedOne = LuaBridge.Localized("##mode_invasion_wavesurvived_one");
		_survivedMany = LuaBridge.Localized("##mode_invasion_wavesurvived_many");
		_root = (RectTransform)transform;
		_root.anchorMin = _root.anchorMax = new Vector2(1f, 1f);
		_root.pivot = new Vector2(1f, 1f);
		_root.sizeDelta = new Vector2(Width, 0f);
		_root.anchoredPosition = new Vector2(-24f, 0f);
		VerticalLayoutGroup layout = gameObject.AddComponent<VerticalLayoutGroup>();
		layout.spacing = 1f;
		layout.childControlWidth = true;
		layout.childControlHeight = true;
		layout.childForceExpandWidth = true;
		layout.childForceExpandHeight = false;
		gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

		// The game's name and the time played.
		RectTransform neutral = Block(4);
		Transform header = Row(neutral, 24f);
		_mode = LuiWidgets.Label(header, string.Empty, 14, LuiStyle.Gray, TextAnchor.MiddleLeft, null);
		UiKit.Flexible(_mode.gameObject);
		_modeTime = LuiWidgets.Label(header, string.Empty, 14, LuiStyle.Gray, TextAnchor.MiddleRight, null);

		// The player's team: its players (multiplayer), the minimap and the "build more stockpiles" alert.
		RectTransform own = Block(4);
		_names = LuiWidgets.Label(own, string.Empty, 12, LuiStyle.CastleYellow, TextAnchor.UpperLeft, null);
		_names.gameObject.SetActive(false);
		Minimap(own);
		_alert = Alert(own);

		if (_invasion)
		{
			BuildEnemy();
		}
		Refresh();
	}

	private void BuildEnemy()
	{
		RectTransform enemy = Block(4);
		Transform title = Row(enemy, 18f);
		_survived = LuiWidgets.Label(title, string.Empty, 12, LuiStyle.Gray, TextAnchor.MiddleLeft, null);
		UiKit.Flexible(_survived.gameObject);
		_kills = LuiWidgets.Label(title, string.Empty, 12, LuiStyle.Gray, TextAnchor.MiddleRight, null);

		// Kills by kind: all corruptrons (minitrons, corruptrons, biftrons), then each kind.
		Transform units = Row(enemy, 40f);
		_groupKills = Counter(units, IconKeys._Corruptrons_Group, 48f);
		_minitronKills = Counter(units, IconKeys._Minitron, 32f);
		_corruptronKills = Counter(units, IconKeys._Corruptron, 32f);
		_biftronKills = Counter(units, IconKeys._Biftron, 32f);
		_magitronKills = Counter(units, IconKeys._Warlock, 32f);

		// The waves still on the island: enemies left of each kind, of as many as were seen.
		_waveList = LuiWidgets.Vertical(enemy, 1f, 0);

		LuiWidgets.Divider(enemy, false);
		Transform timer = Row(enemy, 44f);
		if (UIGameMode() == "##mode_invasion")
		{
			RectTransform times = LuiWidgets.Vertical(timer, 0f, 0);
			UiKit.Flexible(times.gameObject);
			LuiWidgets.Label(times, LuaBridge.Localized("##mode_invasion_nextwave"), 12, LuiStyle.Gray, TextAnchor.MiddleLeft, null);
			_timer = LuiWidgets.Label(times, string.Empty, 24, LuiStyle.CastleYellow, TextAnchor.MiddleLeft, null);
			LuiIconButton next = LuiWidgets.IconButton(timer, 44f);
			next.SetIcon(IconKeys.UI_Right.Get64());
			next.SetIconColor(LuiStyle.CastleYellow);
			next.LabelSide = LuiIconButton.Side.Left;
			next.SetLabel(LuaBridge.Localized("##mode_invasion_skip"));
			next.OnClick = (bool shift, bool ctrl) => NextWave();
			_nextButton = next.gameObject;
		}
		else
		{
			Button send = UiKit.CreateButton(timer, LuaBridge.Localized("##mode_tutorial_sendcorruptron"), 220f, NextWave);
			UiKit.Fixed(send.gameObject, 220f, 44f);
			Text label = send.GetComponentInChildren<Text>();
			label.color = LuiStyle.CastleYellow;
			label.fontSize = 15;
			label.font = LuiStyle.Title;
			_nextButton = send.gameObject;
		}
	}

	// The gamemode's name as the game keeps it ("##mode_invasion", "##mode_tutorial", "##mode_sandbox").
	private string UIGameMode()
	{
		Table data = Data(false);
		return (data != null) ? LuaBridge.String(LuaBridge.Get(data, "mode"), string.Empty) : string.Empty;
	}

	private RectTransform Block(int padding)
	{
		RectTransform block = LuiWidgets.Vertical(transform, 2f, padding);
		Image background = block.gameObject.AddComponent<Image>();
		background.color = RowBackground;
		background.raycastTarget = true;
		// The block is laid out by the view, not sized by its own fitter.
		Destroy(block.GetComponent<ContentSizeFitter>());
		return block;
	}

	private static Transform Row(Transform parent, float height)
	{
		Transform row = UiKit.CreateRow(parent, height);
		return row;
	}

	private static Text Counter(Transform parent, IconKey icon, float iconWidth)
	{
		RectTransform cell = LuiWidgets.Horizontal(parent, 2f, 0);
		Destroy(cell.GetComponent<ContentSizeFitter>());
		UiKit.Flexible(cell.gameObject);
		RectTransform image = UiKit.CreateRect("Icon", cell);
		UiKit.Fixed(image.gameObject, iconWidth, 32f);
		Image sprite = image.gameObject.AddComponent<Image>();
		sprite.sprite = icon.Get64();
		sprite.preserveAspect = true;
		sprite.raycastTarget = false;
		return LuiWidgets.Label(cell, "0", 12, LuiStyle.White, TextAnchor.MiddleLeft, null);
	}

	// The game's minimap prefab (what MinimapMenu.lua's Panels.Minimap puts there), 256 x 256.
	private static void Minimap(Transform parent)
	{
		RectTransform holder = UiKit.CreateRect("Minimap", parent);
		UiKit.Fixed(holder.gameObject, 256f, 256f);
		GameObject minimap = Factory.Instantiate(Images.MiniMap);
		if (minimap == null)
		{
			Plugin.Log.LogWarning("CsMenus: the scoreboard found no minimap prefab");
			return;
		}
		minimap.transform.SetParent(holder, false);
		RectTransform rect = minimap.transform as RectTransform;
		if (rect != null)
		{
			UiKit.SetRect(rect, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
			rect.pivot = new Vector2(0.5f, 0.5f);
		}
	}

	// Panel_Storage's alert: no stockpile has room left.
	private static GameObject Alert(Transform parent)
	{
		Transform row = Row(parent, 56f);
		Image background = row.gameObject.AddComponent<Image>();
		background.color = new Color(0.1f, 0.1f, 0.1f, 0.8f);
		background.raycastTarget = false;
		RectTransform image = UiKit.CreateRect("Warning", row);
		UiKit.Fixed(image.gameObject, 32f, 32f);
		Image warning = image.gameObject.AddComponent<Image>();
		warning.sprite = IconKeys._UI_Warning.Get64();
		warning.color = LuiStyle.CastleRed;
		warning.preserveAspect = true;
		warning.raycastTarget = false;
		Text text = LuiWidgets.Label(row, LuaBridge.Localized("##gamemenu_help_popup_buildmorestockpiles"), 12, new Color(0.6f, 0.6f, 0.6f, 1f), TextAnchor.MiddleLeft, null);
		text.horizontalOverflow = HorizontalWrapMode.Wrap;
		UiKit.Flexible(text.gameObject);
		row.gameObject.SetActive(false);
		return row.gameObject;
	}

	private void NextWave()
	{
		if (_script == null)
		{
			return;
		}
		LuaBridge.Call(_script.Globals.Get("CastleStoryPlus_ScoreboardNext"), "CsScoreboard next wave", new DynValue[0]);
		_nextRefresh = 0f;
	}

	private void Update()
	{
		if (Time.unscaledTime < _nextRefresh)
		{
			return;
		}
		_nextRefresh = Time.unscaledTime + RefreshSeconds;
		Refresh();
	}

	private Table Data(bool withWaves)
	{
		if (_script == null)
		{
			return null;
		}
		DynValue result = LuaBridge.Call(_script.Globals.Get("CastleStoryPlus_Scoreboard"), "CsScoreboard", DynValue.NewBoolean(withWaves));
		return (result.Type == DataType.Table) ? result.Table : null;
	}

	private void Refresh()
	{
		bool withWaves = _invasion && (_refreshes++ % WavesEvery == 0);
		Table data = Data(withWaves);
		if (data == null)
		{
			return;
		}
		string mode = LuaBridge.String(LuaBridge.Get(data, "mode"), string.Empty);
		// The 64-pixel spacer the game puts above the panel while a task is selected.
		float top = LuaBridge.Bool(LuaBridge.Get(data, "spacer"), false) ? -64f : 0f;
		if (_root.anchoredPosition.y != top)
		{
			_root.anchoredPosition = new Vector2(-24f, top);
		}
		string modeKey = _invasion ? ((mode == "##mode_invasion") ? "##mode_invasion" : "##mode_tutorial") : "##mode_sandbox";
		Set(_mode, LuaBridge.Localized(modeKey));
		Set(_modeTime, LuaBridge.String(LuaBridge.Get(data, "elapsed"), string.Empty));
		string names = LuaBridge.String(LuaBridge.Get(data, "names"), string.Empty);
		Set(_names, names);
		Active(_names.gameObject, names.Length > 0);
		Active(_alert, LuaBridge.Bool(LuaBridge.Get(data, "noRoom"), false));
		if (!_invasion)
		{
			return;
		}
		int minitrons = (int)LuaBridge.Number(LuaBridge.Get(data, "minitrons"), 0f);
		int corruptrons = (int)LuaBridge.Number(LuaBridge.Get(data, "corruptrons"), 0f);
		int biftrons = (int)LuaBridge.Number(LuaBridge.Get(data, "biftrons"), 0f);
		int magitrons = (int)LuaBridge.Number(LuaBridge.Get(data, "magitrons"), 0f);
		int survived = (int)LuaBridge.Number(LuaBridge.Get(data, "survived"), 0f);
		Set(_survived, ((survived > 1) ? _survivedMany : _survivedOne).Replace("#waves_survived", survived.ToString()));
		Set(_kills, _killLabel + " " + (minitrons + corruptrons + biftrons + magitrons));
		Set(_groupKills, (minitrons + corruptrons + biftrons).ToString());
		Set(_minitronKills, minitrons.ToString());
		Set(_corruptronKills, corruptrons.ToString());
		Set(_biftronKills, biftrons.ToString());
		Set(_magitronKills, magitrons.ToString());
		if (_timer != null)
		{
			Set(_timer, LuaBridge.String(LuaBridge.Get(data, "timer"), string.Empty));
			float seconds = LuaBridge.Number(LuaBridge.Get(data, "timerSeconds"), 100f);
			_timer.color = (seconds < 10f) ? TimerRed : LuiStyle.CastleYellow;
		}
		Active(_nextButton, LuaBridge.Bool(LuaBridge.Get(data, "server"), false));
		if (withWaves)
		{
			Waves(LuaBridge.GetTable(data, "waves"));
		}
	}

	// Rows of nine numbers: wave, then left and seen of minitrons, corruptrons, biftrons and magitrons.
	private void Waves(Table waves)
	{
		int count = (waves != null) ? waves.Length / 9 : 0;
		for (int i = 0; i < count; i++)
		{
			if (i >= _waveRows.Count)
			{
				_waveRows.Add(WaveRowFor(_waveList));
			}
			WaveRow row = _waveRows[i];
			Active(row.Root, true);
			int at = i * 9;
			int wave = (int)LuaBridge.Number(waves.Get(at + 1), 0f);
			int left = 0;
			int seen = 0;
			for (int k = 0; k < 4; k++)
			{
				int kindLeft = (int)LuaBridge.Number(waves.Get(at + 2 + k), 0f);
				int kindSeen = (int)LuaBridge.Number(waves.Get(at + 6 + k), 0f);
				left += kindLeft;
				seen += kindSeen;
				Set(row.Kinds[k], kindLeft + "/" + kindSeen);
			}
			Set(row.Name, _waveName + " " + wave);
			Set(row.Group, left + "/" + seen);
		}
		for (int i = count; i < _waveRows.Count; i++)
		{
			Active(_waveRows[i].Root, false);
		}
	}

	private static WaveRow WaveRowFor(Transform parent)
	{
		WaveRow row = new WaveRow();
		RectTransform root = LuiWidgets.Vertical(parent, 0f, 0);
		Destroy(root.GetComponent<ContentSizeFitter>());
		row.Root = root.gameObject;
		LuiWidgets.Divider(root, false);
		row.Name = LuiWidgets.Label(root, string.Empty, 12, LuiStyle.Gray, TextAnchor.MiddleLeft, null);
		Transform units = Row(root, 36f);
		row.Group = Counter(units, IconKeys._Corruptrons_Group, 48f);
		row.Kinds[0] = Counter(units, IconKeys._Minitron, 32f);
		row.Kinds[1] = Counter(units, IconKeys._Corruptron, 32f);
		row.Kinds[2] = Counter(units, IconKeys._Biftron, 32f);
		row.Kinds[3] = Counter(units, IconKeys._Warlock, 32f);
		return row;
	}

	// Texts and objects are only touched when they change, so the layout is not rebuilt every refresh.
	private static void Set(Text text, string value)
	{
		if (text != null && text.text != value)
		{
			text.text = value;
		}
	}

	private static void Active(GameObject go, bool active)
	{
		if (go != null && go.activeSelf != active)
		{
			go.SetActive(active);
		}
	}
}
