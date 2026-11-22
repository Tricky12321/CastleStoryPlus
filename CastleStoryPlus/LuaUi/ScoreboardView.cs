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

// [LuaUi] CsScoreboard: the gamemode's top-right panel (GS_Scoreboard) drawn in C# for every gamemode: invasion,
// the tutorial (which uses the invasion panel), sandbox, conquest, credit island and grab the gems. The game's Lua
// panel (InvasionMenu.lua, SandboxMenu.lua and their copies per gamemode) took seconds to build when a map loads.
// Each gamemode's ui.lua asks CastleStoryPlus.CsScoreboard first; the game's Lua panel is only built when the view
// cannot be set up. Conquest and credit island show what sandbox shows (their Lua panels are copies of it with
// their own name); grab the gems adds the time before the next gem and each team's score. The values are read through a small Lua function run in the gamemode's own script (the same Registry,
// Data.Storage and raids the Lua panel read), a few times a second; only the drawing is C#. The view is a child of
// GS_Scoreboard, so GameMenu hiding the scoreboard while a left sidebar is open (GS_Scoreboard:SetVisible, its
// CanvasGroup) hides it too. GS_Scoreboard ("Menu: Scoreboard (Lua)", 320 wide, right of the left bar) has a
// VerticalLayoutGroup of its own for the Lua panel; the view keeps out of it (ignoreLayout) and places itself, or
// the group and the view's spacer offset fought over its place and it jumped off the left edge and back.
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
	-- The host counts the Healtrons killed (the game counts them as corruptrons) in the saved Registry; clients get
	-- the host's count with the wave rows (WaveCountsSync).
	if fy_Corruptron ~= nil and fy_Corruptron.hk_OnKilled ~= nil then
		Hooks.Connect(fy_Corruptron.hk_OnKilled, function(ld)
			if type(ld) == ""table"" then ld = ld[1] end
			if ld ~= nil and Registry ~= nil and IsServer() and CastleStoryPlus.IsHealtron(ld) then
				Registry.cspHealtronKillCount = (Registry.cspHealtronKillCount or 0) + 1
			end
		end)
	end

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
		d.healtrons = Registry.cspHealtronKillCount or 0
		d.survived = Registry.waveSurvived or 0
		if Registry.waveTimer ~= nil then
			d.timer = Registry.waveTimer:FormatedRemainingSeconds()
			d.timerSeconds = Registry.waveTimer:RemainingSeconds()
		end
		if withWaves then
			local counts = {}
			local raids = fy_RaidProject:GetAllOfFaction(Faction.defaultEnemy)
			for r = 1, #raids do
				-- The wave number is kept on the raid (saved and sent to clients); a unit's own is lost on load and
				-- never reaches clients. Raids of no wave (0: the treasure chests' guards) are not counted.
				local crew = {}
				local raidWave = raids[r].waveNumber or 0
				if raidWave > 0 and not CastleStoryPlus.IsGuardRaid(raids[r]) then crew = Project.GetCrew(raids[r]) end
				for c = 1, #crew do
					local labor = crew[c]
					local wave = labor.waveNumber or raidWave
					local t = counts[wave]
					if t == nil then t = { 0, 0, 0, 0, 0 } counts[wave] = t end
					local oc = labor:GetOccupation()
					if oc == Occupations.Minitron then t[1] = t[1] + 1
					elseif oc == Occupations.Corruptron then
						-- A Healtron is a corruptron with a mod flag, counted as its own kind.
						if CastleStoryPlus.IsHealtron(labor) then t[3] = t[3] + 1 else t[2] = t[2] + 1 end
					elseif oc == Occupations.Biftron then t[4] = t[4] + 1
					elseif oc == Occupations.Magitron then t[5] = t[5] + 1 end
				end
			end
			local ids = {}
			for wave, t in pairs(counts) do
				if t[1] + t[2] + t[3] + t[4] + t[5] > 0 then
					table.insert(ids, wave)
					-- As many as the wave has had: the most of its own left at once, and the units its warlocks
					-- summoned into it (counted by the host, WarlockSummons).
					local total = {}
					for i = 1, 5 do total[i] = CastleStoryPlus.WaveSeen(wave, i, t[i]) end
					_s.waveTotals[wave] = total
				end
			end
			table.sort(ids)
			local waves = {}
			for i = 1, #ids do
				local wave = ids[i]
				local t = counts[wave]
				local total = _s.waveTotals[wave]
				table.insert(waves, wave)
				for k = 1, 5 do table.insert(waves, t[k]) end
				for k = 1, 5 do table.insert(waves, total[k]) end
			end
			d.waves = waves
		end
		return d
	end
end
";

	// Grab the gems: the time before the next gem and each team's score, from the gamemode's own data scripts.
	private const string GemsLuaSide = @"
if CastleStoryPlus_ScoreboardGems == nil then
	local clientside = require(""Data/Data_Clientside.lua"")
	local factions = require(""Data/Data_Faction.lua"")
	function CastleStoryPlus_ScoreboardGems()
		local g = { clientside:GetPumpkinSpawnRemainingTime() }
		for i, faction in ipairs(factions:GetAllOwned()) do
			table.insert(g, faction.name)
			table.insert(g, clientside:GetScore(faction))
		end
		return g
	end
end
";

	private const float Width = 272f;

	// The enemy kinds a wave row counts: minitrons, corruptrons, healtrons (the mod's), biftrons, magitrons. A wave row
	// from the Lua side is its number, then what is left and what was seen of each kind.
	private const int KindCount = 5;

	private const int RowLength = Invasion.WaveCountsSync.RowLength;

	// Three counters side by side in a row of the block (its width less its padding), as wide as the largest "27/41".
	private const float CellWidth = 86f;

	private const float CellHeight = 28f;

	// A Healtron is drawn as a corruptron in this green with a green plus on it.
	private static readonly Color HealtronGreen = new Color(0.45f, 1f, 0.4f, 1f);

	private static readonly Color HealtronBadge = new Color(0.35f, 1f, 0.3f, 1f);

	private const float BadgeSize = 13f;

	// Each kind's name, shown when the pointer is over its counter (the wave rows' order).
	private static readonly string[] KindNames = { "Minitrons", "Corruptrons", "Healtrons (corruptrons that heal their allies)", "Biftrons", "Warlocks" };

	private const float RefreshSeconds = 0.25f;

	// Waves (the raids' crews) are counted every this many refreshes.
	private const int WavesEvery = 4;

	private static readonly Color TimerRed = new Color(1f, 0f, 0.25f, 1f);

	private static readonly Color RowBackground = new Color(0.07f, 0.07f, 0.07f, 0.98f);

	private static ScoreboardView _current;

	private Script _script;

	private bool _invasion;

	// The gamemode's name key ("##mode_sandbox", "##mode_conquest"...); for invasion it is read from the game.
	private string _modeKey;

	private bool _gems;

	private bool _tutorial;

	private Text _gemTimer;

	private readonly List<Text> _gemScores = new List<Text>();

	private readonly List<Image> _gemFlags = new List<Image>();

	private Transform _gemRows;

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

	private Text _healtronKills;

	private RectTransform _waveList;

	private readonly List<WaveRow> _waveRows = new List<WaveRow>();

	private Text _timer;

	private Text _waveTotal;

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

		public Text[] Kinds = new Text[KindCount];

		public GameObject[] Cells = new GameObject[KindCount];

		// The second line of counters (the last two kinds): hidden when none of them was seen.
		public GameObject SecondLine;

		// The first line (the first three kinds): hidden when none of them was seen.
		public GameObject FirstLine;
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
		// The treasure chests' guards are hired in raids of the enemy faction, but they are no wave.
		LuaInjection.AddFunction("IsGuardRaid", (ScriptExecutionContext context, CallbackArguments args) =>
		{
			LuaDriver raid = (args.Count > 0 && !args[0].IsNil()) ? args.AsComponent<LuaDriver>(0, "IsGuardRaid", allowNil: true) : null;
#if !ESSENTIALS
			return DynValue.NewBoolean(raid != null && Treasure.TreasureChests.IsGuardRaid(raid.gameObject));
#else
			return DynValue.NewBoolean(false);
#endif
		});
		// What a wave has had of a kind (1 to 5, the wave rows' order), given what is left of it.
		LuaInjection.AddFunction("WaveSeen", (ScriptExecutionContext context, CallbackArguments args) =>
		{
			int wave = (args.Count > 0 && args[0].Type == DataType.Number) ? (int)args[0].Number : 0;
			int kind = (args.Count > 1 && args[1].Type == DataType.Number) ? (int)args[1].Number - 1 : -1;
			int left = (args.Count > 2 && args[2].Type == DataType.Number) ? (int)args[2].Number : 0;
			return DynValue.NewNumber(Invasion.WarlockSummons.Tally.Seen(wave, kind, left));
		});
		LuaInjection.AddFunction("IsHealtron", (ScriptExecutionContext context, CallbackArguments args) =>
		{
			LuaDriver labor = (args.Count > 0 && !args[0].IsNil()) ? args.AsComponent<LuaDriver>(0, "IsHealtron", allowNil: true) : null;
#if !ESSENTIALS
			return DynValue.NewBoolean(labor != null && Combat.Healtron.IsHealtron(labor.gameObject));
#else
			return DynValue.NewBoolean(false);
#endif
		});
		Patch("Gamemodes/invasion/invasion_ui.lua", "invasion");
		Patch("Gamemodes/tutorial/invasion_ui.lua", "tutorial");
		Patch("Gamemodes/sandbox/ui.lua", "sandbox");
		Patch("Gamemodes/conquest/ui.lua", "conquest");
		Patch("Gamemodes/creditisland/ui.lua", "creditisland");
		LuaInjection.AddPatch(Features.CsMenus, "Gamemodes/grabthegems/Data/Data_Menu.lua", "GS_Scoreboard:LoadMenuFromClosure(self._DoLoadScoreboard)\n", LuaInjection.Mode.Replace, "if not CastleStoryPlus.CsScoreboard(\"grabthegems\", GS_Scoreboard) then GS_Scoreboard:LoadMenuFromClosure(self._DoLoadScoreboard) end\n");
		GameSession.OnLeave(Drop);
		GameSession.OnLeave(Invasion.WaveCountsSync.Forget);
		GameSession.OnLeave(Invasion.WarlockSummons.Tally.Clear);
	}

	private static string ModeKey(string mode)
	{
		switch (mode)
		{
			case "sandbox":
				return "##mode_sandbox";
			case "conquest":
				return "##mode_conquest";
			case "creditisland":
				return "##mode_creditisland";
			case "grabthegems":
				return "##mode_gtg";
			default:
				return null;
		}
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
		string modeKey = ModeKey(mode);
		if ((!invasion && modeKey == null) || script == null)
		{
			return false;
		}
		bool gems = mode == "grabthegems";
		try
		{
			script.DoString(LuaSide);
			if (gems)
			{
				script.DoString(GemsLuaSide);
			}
		}
		catch (InterpreterException ex)
		{
			LuaBridge.LogOnce("CsScoreboard", ex.DecoratedMessage ?? ex.Message);
			return false;
		}
		catch (System.Exception ex)
		{
			LuaBridge.LogOnce("CsScoreboard", ex.Message);
			return false;
		}
		Drop();
		Transform parent = (component != null) ? component.transform : LuiCanvas.Instance.Menus;
		GameObject go = new GameObject("CSP_Scoreboard", typeof(RectTransform));
		go.transform.SetParent(parent, false);
		_current = go.AddComponent<ScoreboardView>();
		_current._script = script;
		_current._invasion = invasion;
		_current._modeKey = modeKey;
		_current._gems = gems;
		// The sandbox tutorial (LTD.lua) was connected by SandboxMenu.lua, which is not loaded with this view.
		_current._tutorial = mode == "sandbox" && GameUi.GameTutorial.Connect(script);
		_current.Build();
		script.OnScriptKilled += OnScriptKilled;
		RectTransform rect = parent as RectTransform;
		Plugin.Log.LogInfo("CsMenus: scoreboard (" + mode + ") drawn in C#" + ((rect != null) ? (", under " + parent.name + " " + rect.rect.size + " anchors " + rect.anchorMin + "-" + rect.anchorMax) : string.Empty));
		return true;
	}

	private void OnDestroy()
	{
		if (_tutorial)
		{
			_tutorial = false;
			GameUi.GameTutorial.Disconnect(_script);
		}
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
		_root.anchorMin = _root.anchorMax = new Vector2(0f, 1f);
		_root.pivot = new Vector2(0f, 1f);
		_root.sizeDelta = new Vector2(Width, 0f);
		_root.anchoredPosition = Vector2.zero;
		gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
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
		if (_gems)
		{
			BuildGems();
		}
		Refresh();
	}

	// PumpkinFactionUnitsPanel: the time before the next gem, then each team's flag and score.
	private void BuildGems()
	{
		RectTransform gems = Block(4);
		Transform timer = Row(gems, 20f);
		Text label = LuiWidgets.Label(timer, LuaBridge.Localized("##mode_gtg_timebeforenextgem"), 13, LuiStyle.Gray, TextAnchor.MiddleLeft, null);
		UiKit.Flexible(label.gameObject);
		_gemTimer = LuiWidgets.Label(timer, string.Empty, 13, LuiStyle.Gray, TextAnchor.MiddleRight, null);
		_gemRows = Row(gems, 36f);
	}

	private static IconKey FlagOf(string faction)
	{
		switch (faction)
		{
			case "Blue":
				return IconKeys.FlagBlue;
			case "Red":
				return IconKeys.FlagRed;
			case "Green":
				return IconKeys.FlagGreen;
			default:
				return IconKeys.FlagOrange;
		}
	}

	private void RefreshGems()
	{
		DynValue result = LuaBridge.Call(_script.Globals.Get("CastleStoryPlus_ScoreboardGems"), "CsScoreboard gems", new DynValue[0]);
		if (result.Type != DataType.Table)
		{
			return;
		}
		Table g = result.Table;
		Set(_gemTimer, LuaBridge.String(g.Get(1), string.Empty));
		int teams = (g.Length - 1) / 2;
		for (int i = 0; i < teams; i++)
		{
			string faction = LuaBridge.String(g.Get(2 + i * 2), string.Empty);
			int score = (int)LuaBridge.Number(g.Get(3 + i * 2), 0f);
			if (i >= _gemScores.Count)
			{
				RectTransform cell = LuiWidgets.Horizontal(_gemRows, 2f, 0);
				Destroy(cell.GetComponent<ContentSizeFitter>());
				UiKit.Flexible(cell.gameObject);
				RectTransform image = UiKit.CreateRect("Flag", cell);
				UiKit.Fixed(image.gameObject, 32f, 32f);
				Image flag = image.gameObject.AddComponent<Image>();
				flag.preserveAspect = true;
				flag.raycastTarget = false;
				_gemFlags.Add(flag);
				_gemScores.Add(LuiWidgets.Label(cell, "0", 12, LuiStyle.White, TextAnchor.MiddleLeft, null));
			}
			Sprite sprite = FlagOf(faction).Get64();
			if (_gemFlags[i].sprite != sprite)
			{
				_gemFlags[i].sprite = sprite;
			}
			Set(_gemScores[i], score.ToString());
			Active(_gemScores[i].transform.parent.gameObject, true);
		}
		for (int i = teams; i < _gemScores.Count; i++)
		{
			Active(_gemScores[i].transform.parent.gameObject, false);
		}
	}

	private void BuildEnemy()
	{
		RectTransform enemy = Block(4);
		Transform title = Row(enemy, 18f);
		_survived = LuiWidgets.Label(title, string.Empty, 12, LuiStyle.Gray, TextAnchor.MiddleLeft, null);
		UiKit.Flexible(_survived.gameObject);
		_kills = LuiWidgets.Label(title, string.Empty, 12, LuiStyle.Gray, TextAnchor.MiddleRight, null);

		// Kills by kind: all corruptrons (minitrons, corruptrons, healtrons, biftrons), then each kind, on two lines.
		RectTransform units = LuiWidgets.Vertical(enemy, 0f, 0);
		Destroy(units.GetComponent<ContentSizeFitter>());
		units.GetComponent<VerticalLayoutGroup>().childForceExpandWidth = true;
		Transform first = KindLine(units);
		Transform second = KindLine(units);
		_groupKills = Cell(first, IconKeys._Corruptrons_Group, 36f, Color.white, false, "Corrupted units killed (minitrons, corruptrons, healtrons and biftrons)", out _);
		_minitronKills = KindCell(first, 0, "Minitrons killed", out _);
		_corruptronKills = KindCell(first, 1, "Corruptrons killed", out _);
		_biftronKills = KindCell(second, 3, "Biftrons killed", out _);
		_magitronKills = KindCell(second, 4, "Warlocks killed", out _);
		_healtronKills = KindCell(second, 2, "Healtrons killed", out _);

		// The waves still on the island: enemies left of each kind, of as many as were seen.
		// As wide as the block (not as its rows want: "41/41" counters made it wider than the scoreboard).
		_waveList = LuiWidgets.Vertical(enemy, 1f, 0);
		Destroy(_waveList.GetComponent<ContentSizeFitter>());
		_waveList.GetComponent<VerticalLayoutGroup>().childForceExpandWidth = true;
		UiKit.Flexible(_waveList.gameObject);

		LuiWidgets.Divider(enemy, false);
		Transform timer = Row(enemy, 44f);
		if (UIGameMode() == "##mode_invasion")
		{
			RectTransform times = LuiWidgets.Vertical(timer, 0f, 0);
			UiKit.Flexible(times.gameObject);
			LuiWidgets.Label(times, LuaBridge.Localized("##mode_invasion_nextwave"), 12, LuiStyle.Gray, TextAnchor.MiddleLeft, null);
			_timer = LuiWidgets.Label(times, string.Empty, 24, LuiStyle.CastleYellow, TextAnchor.MiddleLeft, null);
			// The enemies left of all the waves on the island, of as many as were seen, beside the timer.
			RectTransform totals = LuiWidgets.Vertical(timer, 0f, 0);
			Destroy(totals.GetComponent<ContentSizeFitter>());
			totals.GetComponent<VerticalLayoutGroup>().childForceExpandWidth = true;
			UiKit.Fixed(totals.gameObject, 84f, 44f);
			Text totalName = LuiWidgets.Label(totals, "Enemies left", 12, LuiStyle.Gray, TextAnchor.MiddleRight, null);
			totalName.horizontalOverflow = HorizontalWrapMode.Overflow;
			_waveTotal = LuiWidgets.Label(totals, "0/0", 20, LuiStyle.White, TextAnchor.MiddleRight, null);
			_waveTotal.horizontalOverflow = HorizontalWrapMode.Overflow;
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

	// A line of counters, side by side.
	private static Transform KindLine(Transform parent)
	{
		RectTransform line = LuiWidgets.Horizontal(parent, 2f, 0);
		Destroy(line.GetComponent<ContentSizeFitter>());
		UiKit.Fixed(line.gameObject, CellWidth * 3f + 4f, CellHeight);
		line.GetComponent<LayoutElement>().flexibleWidth = 0f;
		return line;
	}

	// The icon of an enemy kind (the wave rows' order: minitron, corruptron, healtron, biftron, warlock).
	internal static IconKey KindIcon(int kind)
	{
		switch (kind)
		{
			case 0:
				return IconKeys._Minitron;
			case 1:
			case 2:
				return IconKeys._Corruptron;
			case 3:
				return IconKeys._Biftron;
			default:
				return IconKeys._Warlock;
		}
	}

	// The icon's 64-pixel sprite, or its 32-pixel one where the game has no larger one.
	internal static Sprite IconSprite(IconKey icon)
	{
		if (icon == null)
		{
			return null;
		}
		Sprite sprite = icon.Get64();
		return (sprite != null) ? sprite : icon.Get32();
	}

	// A counter of an enemy kind: its icon (a Healtron's green, with a plus) and its name when pointed at.
	private static Text KindCell(Transform parent, int kind, string tooltip, out GameObject root)
	{
		bool healtron = kind == 2;
		return Cell(parent, KindIcon(kind), 24f, healtron ? HealtronGreen : Color.white, healtron, tooltip, out root);
	}

	// An icon with its count right beside it, right-aligned within a width of its own and on one line, so a "27/41" is
	// never squeezed into a column of single digits.
	private static Text Cell(Transform parent, IconKey icon, float iconWidth, Color tint, bool healBadge, string tooltip, out GameObject root)
	{
		RectTransform cell = LuiWidgets.Horizontal(parent, 4f, 0);
		Destroy(cell.GetComponent<ContentSizeFitter>());
		UiKit.Fixed(cell.gameObject, CellWidth, CellHeight);
		root = cell.gameObject;
		root.AddComponent<CanvasGroup>();
		RectTransform image = UiKit.CreateRect("Icon", cell);
		UiKit.Fixed(image.gameObject, iconWidth, 28f);
		Image sprite = image.gameObject.AddComponent<Image>();
		sprite.sprite = IconSprite(icon);
		sprite.color = tint;
		sprite.preserveAspect = true;
		sprite.raycastTarget = tooltip != null;
		if (healBadge)
		{
			// A green plus on the icon's lower right corner, so a Healtron is not just a corruptron of another colour.
			RectTransform badge = UiKit.CreateRect("Heal", image);
			badge.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
			badge.anchorMin = badge.anchorMax = new Vector2(1f, 0f);
			badge.pivot = new Vector2(1f, 0f);
			badge.sizeDelta = new Vector2(BadgeSize, BadgeSize);
			badge.anchoredPosition = new Vector2(2f, 0f);
			Image plus = badge.gameObject.AddComponent<Image>();
			plus.sprite = IconSprite(IconKeys.UI_Plus);
			plus.color = HealtronBadge;
			plus.preserveAspect = true;
			plus.raycastTarget = false;
		}
		if (tooltip != null)
		{
			LuiHoverTip.Add(image.gameObject, tooltip);
		}
		Text text = LuiWidgets.Label(cell, "0", 12, LuiStyle.White, TextAnchor.MiddleRight, null);
		text.horizontalOverflow = HorizontalWrapMode.Overflow;
		UiKit.Fixed(text.gameObject, CellWidth - iconWidth - 8f, CellHeight);
		return text;
	}

	// The game's minimap prefab (what MinimapMenu.lua's Panels.Minimap puts there), 256 x 256. The holder has its
	// size before the prefab is made: MiniMap reads its own size once when it starts and places the markers (units,
	// crystals, the camera) by it, and a new rect is 100 x 100 until the layout runs.
	private static void Minimap(Transform parent)
	{
		RectTransform holder = UiKit.CreateRect("Minimap", parent);
		UiKit.Fixed(holder.gameObject, 256f, 256f);
		holder.sizeDelta = new Vector2(256f, 256f);
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
			_root.anchoredPosition = new Vector2(0f, top);
		}
		string modeKey = _invasion ? ((mode == "##mode_invasion") ? "##mode_invasion" : "##mode_tutorial") : _modeKey;
		Set(_mode, LuaBridge.Localized(modeKey));
		Set(_modeTime, LuaBridge.String(LuaBridge.Get(data, "elapsed"), string.Empty));
		string names = LuaBridge.String(LuaBridge.Get(data, "names"), string.Empty);
		Set(_names, names);
		Active(_names.gameObject, names.Length > 0);
		Active(_alert, LuaBridge.Bool(LuaBridge.Get(data, "noRoom"), false));
		if (_gems)
		{
			RefreshGems();
		}
		if (!_invasion)
		{
			return;
		}
		int minitrons = (int)LuaBridge.Number(LuaBridge.Get(data, "minitrons"), 0f);
		int corruptrons = (int)LuaBridge.Number(LuaBridge.Get(data, "corruptrons"), 0f);
		int biftrons = (int)LuaBridge.Number(LuaBridge.Get(data, "biftrons"), 0f);
		int magitrons = (int)LuaBridge.Number(LuaBridge.Get(data, "magitrons"), 0f);
		// The game counts a Healtron as a corruptron; a client has the host's count of them.
		int healtrons = (int)LuaBridge.Number(LuaBridge.Get(data, "healtrons"), 0f);
		if (!UnityEngine.Networking.NetworkServer.active && Invasion.WaveCountsSync.HealtronKills >= 0)
		{
			healtrons = Invasion.WaveCountsSync.HealtronKills;
		}
		healtrons = Mathf.Clamp(healtrons, 0, corruptrons);
		int survived = (int)LuaBridge.Number(LuaBridge.Get(data, "survived"), 0f);
		Set(_survived, ((survived > 1) ? _survivedMany : _survivedOne).Replace("#waves_survived", survived.ToString()));
		Set(_kills, _killLabel + " " + (minitrons + corruptrons + biftrons + magitrons));
		Set(_groupKills, (minitrons + corruptrons + biftrons).ToString());
		Set(_minitronKills, minitrons.ToString());
		Set(_corruptronKills, (corruptrons - healtrons).ToString());
		Set(_healtronKills, healtrons.ToString());
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
			// The raids are the host's alone: a client shows the host's rows (WaveCountsSync).
			int[] rows = Rows(LuaBridge.GetTable(data, "waves"));
			Invasion.WaveCountsSync.Listen();
			if (UnityEngine.Networking.NetworkServer.active)
			{
				Invasion.WaveCountsSync.Send(rows, healtrons);
			}
			else if (Invasion.WaveCountsSync.Rows != null)
			{
				rows = Invasion.WaveCountsSync.Rows;
			}
			Waves(rows);
			SetTotal(rows);
		}
	}

	// The Lua side's rows as numbers.
	private static int[] Rows(Table waves)
	{
		int count = (waves != null) ? waves.Length / RowLength * RowLength : 0;
		int[] rows = new int[count];
		for (int i = 0; i < count; i++)
		{
			rows[i] = (int)LuaBridge.Number(waves.Get(i + 1), 0f);
		}
		return rows;
	}

	// Rows of eleven numbers: wave, then left and seen of each kind (minitrons, corruptrons, healtrons, biftrons,
	// magitrons); a kind that was never seen in the wave has no counter.
	private void Waves(int[] waves)
	{
		int count = waves.Length / RowLength;
		for (int i = 0; i < count; i++)
		{
			if (i >= _waveRows.Count)
			{
				_waveRows.Add(WaveRowFor(_waveList));
			}
			WaveRow row = _waveRows[i];
			Active(row.Root, true);
			int at = i * RowLength;
			int wave = waves[at];
			int left = 0;
			int seen = 0;
			for (int k = 0; k < KindCount; k++)
			{
				int kindLeft = waves[at + 1 + k];
				int kindSeen = waves[at + 1 + KindCount + k];
				left += kindLeft;
				seen += kindSeen;
				Set(row.Kinds[k], kindLeft + "/" + kindSeen);
				// An unseen kind keeps its place, so the columns of the waves line up.
				CanvasGroup group = row.Cells[k].GetComponent<CanvasGroup>();
				group.alpha = (kindSeen > 0) ? 1f : 0f;
				group.blocksRaycasts = kindSeen > 0;
			}
			Active(row.FirstLine, waves[at + 1 + KindCount] > 0 || waves[at + 2 + KindCount] > 0 || waves[at + 3 + KindCount] > 0);
			Active(row.SecondLine, waves[at + 1 + KindCount + 3] > 0 || waves[at + 1 + KindCount + 4] > 0);
			Set(row.Name, _waveName + " " + wave);
			Set(row.Group, left + "/" + seen);
		}
		for (int i = count; i < _waveRows.Count; i++)
		{
			Active(_waveRows[i].Root, false);
		}
	}

	// What is left of all the waves, of as many as were seen, beside the timer.
	private void SetTotal(int[] waves)
	{
		int left = 0;
		int seen = 0;
		for (int at = 0; at + RowLength <= waves.Length; at += RowLength)
		{
			for (int k = 0; k < KindCount; k++)
			{
				left += waves[at + 1 + k];
				seen += waves[at + 1 + KindCount + k];
			}
		}
		Set(_waveTotal, left + "/" + seen);
	}

	private static WaveRow WaveRowFor(Transform parent)
	{
		WaveRow row = new WaveRow();
		RectTransform root = LuiWidgets.Vertical(parent, 0f, 0);
		Destroy(root.GetComponent<ContentSizeFitter>());
		root.GetComponent<VerticalLayoutGroup>().childForceExpandWidth = true;
		UiKit.Flexible(root.gameObject);
		row.Root = root.gameObject;
		LuiWidgets.Divider(root, false);
		// The wave's name on the left, what is left of it and what was seen on the right.
		Transform title = Row(root, 18f);
		row.Name = LuiWidgets.Label(title, string.Empty, 12, LuiStyle.Gray, TextAnchor.MiddleLeft, null);
		UiKit.Flexible(row.Name.gameObject);
		row.Group = LuiWidgets.Label(title, "0/0", 12, LuiStyle.White, TextAnchor.MiddleRight, null);
		row.Group.horizontalOverflow = HorizontalWrapMode.Overflow;
		Transform first = KindLine(root);
		Transform second = KindLine(root);
		row.FirstLine = first.gameObject;
		row.SecondLine = second.gameObject;
		for (int k = 0; k < KindCount; k++)
		{
			row.Kinds[k] = KindCell((k < 3) ? first : second, k, KindNames[k] + " left in the wave, of all that came", out row.Cells[k]);
		}
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
