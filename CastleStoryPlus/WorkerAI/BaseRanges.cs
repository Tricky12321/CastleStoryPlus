using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using Brix.Engine;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Legacy;
using CastleStoryPlus.Core;
using CastleStoryPlus.Giant;
using HarmonyLib;
using MoonSharp.Interpreter;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.WorkerAI;

// The ranges of the idle chores around the base (AutoRepair, AutoCleanup), set on the home crystal: its task popup
// (the crystal's section, after the spawn button) gets a button per range showing it in blocks, with small "+" and
// "-" over it (Shift: 5 at a time), and a ring on the ground around the crystal
// shows each range in its colour. The crystal's context wheel has a "Show ranges" / "Hide ranges" button: the rings
// stay around the crystal all the time, or are not drawn at all ([BaseRanges] ShowRings). The ranges are the
// features' [AutoRepair] / [AutoCleanup] Radius config entries, one value for all teams and saves; the chores run on
// the host, so only the host sees the buttons, the wheel button and the rings.
internal static class BaseRanges
{
	internal const int Min = 5;

	internal const int Max = 60;

	private const int ShiftStep = 5;

	internal class Range
	{
		public string Key;

		public ConfigEntry<int> Radius;

		public Color Color;
	}

	internal static readonly List<Range> Ranges = new List<Range>();

	internal static ConfigEntry<bool> AlwaysShow;

	private static bool _registered;

	// Called by each feature's Enable. The two buttons are placed so the repair range comes first either way: one
	// right after the spawn button's block, the other before the next part of the menu (the task's priority).
	public static void Register(string key, ConfigEntry<int> radius, string label, string icon, Color color, bool first)
	{
		Ranges.Add(new Range { Key = key, Radius = radius, Color = color });
		if (!_registered)
		{
			_registered = true;
			AlwaysShow = Plugin.Cfg.Bind("BaseRanges", "ShowRings", true, "Show the auto repair / auto cleanup rings around the home crystal (true: all the time, false: never). Also toggled on the crystal's context wheel.");
			LuaInjection.AddFunction("BaseRangeEditable", (ScriptExecutionContext context, CallbackArguments args) => DynValue.NewBoolean(NetworkServer.active));
			LuaInjection.AddFunction("BaseRange", (ScriptExecutionContext context, CallbackArguments args) =>
			{
				Range range = Find((args.Count > 0) ? args[0].CastToString() : null);
				return DynValue.NewString((range != null) ? range.Radius.Value.ToString(CultureInfo.InvariantCulture) : string.Empty);
			});
			LuaInjection.AddFunction("BaseRangeStep", (ScriptExecutionContext context, CallbackArguments args) =>
			{
				Range range = Find((args.Count > 0) ? args[0].CastToString() : null);
				return DynValue.NewBoolean(Step(range, (args.Count > 1) ? (int)args[1].Number : 0));
			});
			UI.StepButtons.Register(key);
			Plugin.Root.AddComponent<BaseRangeRings>();
		}
		GameUi.GameMenuItems.Extend((GameUi.GameMenuModel menu) => AddToMenu(menu, key, label, icon, color, first));
		if (first)
		{
			LuaInjection.AddPatch(key, "LUI/Menus/GameMenu.lua", "_m.bh.idleTaskActionsSpawnUnit = h--TODO:move to task actions\nend\n", LuaInjection.Mode.InsertAfter, Handle(key, label, icon, color));
		}
		else
		{
			LuaInjection.AddPatch(key, "LUI/Menus/GameMenu.lua", "-------\n--task.priority\n", LuaInjection.Mode.InsertBefore, Handle(key, label, icon, color));
		}
	}

	// The C# in-game menu: a row under the idle group's actions, "Auto cleanup range (blocks)  -  33  +" (Shift: 5 at
	// a time). Not a button with its own - and + like the Lua menu's: in the C# popup those sit above the button,
	// over the row before it (the "Auto spawn bricktrons" toggle).
	private static void AddToMenu(GameUi.GameMenuModel menu, string key, string label, string icon, Color color, bool first)
	{
		GameUi.UiStepper stepper = new GameUi.UiStepper
		{
			Id = "idleTaskActions" + key + "Range",
			Visible = () => NetworkServer.active,
			Label = () => label,
			Value = () =>
			{
				Range range = Find(key);
				return (range != null) ? range.Radius.Value.ToString(CultureInfo.InvariantCulture) : string.Empty;
			},
			// -1 / +1, or -10 / +10 with Shift; Step reads Shift itself.
			Step = (int delta) => Step(Find(key), Math.Sign(delta))
		};
		menu.Popup.Find("idleTaskActions").Add(stepper);
	}

	private static string Handle(string key, string label, string icon, Color color)
	{
		string colour = "Color.New(" + F(color.r) + ", " + F(color.g) + ", " + F(color.b) + ", 1)";
		return "\n---idleTask.actions." + key + "Range (Castle Story Plus)\n"
			+ "do\n"
			+ "local h = MenuHandle.New()\n"
			+ "h.Visible = ||CastleStoryPlus.BaseRangeEditable()\n"
			+ "h.Label = ||\"" + label + "\"\n"
			+ "h.Icon = ||IconKeys." + icon + ":Get64()\n"
			+ "h.IconSize = ||32\n"
			+ "h.IconColor = ||" + colour + "\n"
			+ "h.OnAction = function() end\n"
			+ "h.Count = ||CastleStoryPlus.BaseRange(\"" + key + "\")\n"
			+ "h.CountColor = ||CastleYellow\n"
			+ "h.CountFontSize = ||14\n"
			+ "h.ev_refresh = Event.New()\n"
			+ "h.Step = function(direction) return CastleStoryPlus.BaseRangeStep(\"" + key + "\", direction) end\n"
			+ "h.StepChanged = h.ev_refresh\n"
			+ "\n"
			+ "_m.onSetSelectedProject:AddListener(||h.ev_refresh:Invoke())\n"
			+ "_m.sh.idleTaskActions.ev_onLoad:AddListener(function(m, sh) m.AddMenuHandleToggle(h, sh) end)\n"
			+ "\n"
			+ "_m.mg.projectContext:AddChild(h)\n"
			+ "end\n";
	}

	internal const string WheelButton = "PlusBaseRanges";

	private const string WheelCondition = "Condition_PlusBaseRanges";

	private const string WheelLabel = "Condition_PlusBaseRangesLabel";

	private const string WheelEvent = "Event_PlusBaseRanges";

	// Host only, like the rings.
	internal static void RegisterWheel()
	{
		Instructions.conditions[WheelCondition] = () => AlwaysShow != null && NetworkServer.active;
		Instructions.conditions[WheelLabel] = () => (AlwaysShow != null && AlwaysShow.Value) ? "Hide ranges" : "Show ranges";
		Instructions.commendes[WheelEvent] = () =>
		{
			if (AlwaysShow != null && NetworkServer.active)
			{
				AlwaysShow.Value = !AlwaysShow.Value;
			}
		};
	}

	// The wheel files as they are read: the button's definition after the game's buttons, and the button in place of
	// the crystal wheel's last free slot (between "Call to arms" and "Back to work").
	internal static string AddToWheel(string text)
	{
		if (AlwaysShow == null)
		{
			return text;
		}
		RegisterWheel();
		if (text.Contains("BUTTON Back To Work") && !text.Contains("BUTTON " + WheelButton))
		{
			return text + "\n\nBUTTON " + WheelButton + "\nName : Show ranges\nLabel : " + WheelLabel + "\nCondition : " + WheelCondition + "\nEffect : " + WheelEvent + "\nIcon : _Defend_Area\nSize : Native\nTooltip : Show the auto repair (green) and auto cleanup (blue) ranges around the crystal all the time, or again only while its task is selected.\n";
		}
		if (!text.Contains("MENU Crystal") || text.Contains("Button: " + WheelButton))
		{
			return text;
		}
		List<string> lines = new List<string>(text.Replace("\r\n", "\n").Split('\n'));
		int start = lines.FindIndex((string l) => l.Trim() == "MENU Crystal");
		if (start < 0)
		{
			return text;
		}
		int lastHole = -1;
		for (int i = start + 1; i < lines.Count && !lines[i].Trim().StartsWith("MENU "); i++)
		{
			if (lines[i].Trim() == "Hole")
			{
				lastHole = i;
			}
		}
		if (lastHole < 0)
		{
			Plugin.Log.LogWarning("BaseRanges: no free slot in the crystal wheel");
			return text;
		}
		lines[lastHole] = "\tButton: " + WheelButton;
		return string.Join("\n", lines.ToArray());
	}

	private static string F(float value)
	{
		return value.ToString("0.###", CultureInfo.InvariantCulture);
	}

	private static Range Find(string key)
	{
		foreach (Range range in Ranges)
		{
			if (range.Key == key)
			{
				return range;
			}
		}
		return null;
	}

	// Host only (the chores run there). Saved to the config file at once; the ring follows the next frame.
	private static bool Step(Range range, int direction)
	{
		if (range == null || direction == 0 || !NetworkServer.active)
		{
			return false;
		}
		bool shift = UnityEngine.Input.GetKey(KeyCode.LeftShift) || UnityEngine.Input.GetKey(KeyCode.RightShift);
		int next = Mathf.Clamp(range.Radius.Value + direction * (shift ? ShiftStep : 1), Min, Max);
		if (next == range.Radius.Value)
		{
			return false;
		}
		range.Radius.Value = next;
		return true;
	}

	// The radius as the chores use it, kept within the buttons' range (the config file may say anything).
	internal static int Clamped(ConfigEntry<int> radius)
	{
		return Mathf.Clamp(radius.Value, Min, Max);
	}

	// The local player's home crystal while its rings are shown ([BaseRanges] ShowRings). Not "while the crystal's
	// task is selected": that is the idle task, selected nearly all the time, so the rings would never hide.
	internal static FireflyNest ShownHome()
	{
		if (!NetworkServer.active || User.LocalUser == null || AlwaysShow == null || !AlwaysShow.Value)
		{
			return null;
		}
		return GiantBricktron.HomeNest(User.LocalUser.faction);
	}
}

// The rings: one line per range around the selected home crystal, laid on the ground (the top of each column of
// blocks or terrain under it), made again when the range or the crystal changes and every second (the ground may
// have changed). The ranges are spheres around the crystal; a ring shows them at the ground.
internal class BaseRangeRings : MonoBehaviour
{
	private const float RebuildSeconds = 1f;

	// How far above and below the crystal the ground is looked for.
	private const int Above = 16;

	private const int Below = 32;

	private const float Lift = 0.2f;

	private const float Width = 0.25f;

	private static Material _material;

	private readonly List<LineRenderer> _lines = new List<LineRenderer>();

	private readonly List<int> _drawn = new List<int>();

	private FireflyNest _nest;

	private Vector3 _at;

	private float _builtAt;

	private void Update()
	{
		FireflyNest nest = BaseRanges.ShownHome();
		if (nest == null)
		{
			Hide();
			return;
		}
		Vector3 center = nest.transform.position;
		bool stale = nest != _nest || center != _at || Time.unscaledTime - _builtAt >= RebuildSeconds;
		for (int i = 0; i < BaseRanges.Ranges.Count; i++)
		{
			BaseRanges.Range range = BaseRanges.Ranges[i];
			LineRenderer line = Line(i, range.Color);
			int radius = BaseRanges.Clamped(range.Radius);
			if (stale || !line.enabled || _drawn[i] != radius)
			{
				Draw(line, center, radius);
				_drawn[i] = radius;
			}
			line.enabled = true;
		}
		if (stale)
		{
			_nest = nest;
			_at = center;
			_builtAt = Time.unscaledTime;
		}
	}

	private void Hide()
	{
		_nest = null;
		foreach (LineRenderer line in _lines)
		{
			if (line != null)
			{
				line.enabled = false;
			}
		}
	}

	// The lines live in the game scene and are made again after a scene change.
	private LineRenderer Line(int index, Color color)
	{
		while (_lines.Count <= index)
		{
			_lines.Add(null);
			_drawn.Add(-1);
		}
		LineRenderer line = _lines[index];
		if (line != null)
		{
			return line;
		}
		GameObject go = new GameObject("CastleStoryPlusBaseRange");
		line = go.AddComponent<LineRenderer>();
		if (_material == null)
		{
			Shader shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
			_material = new Material(shader);
		}
		line.sharedMaterial = _material;
		line.useWorldSpace = true;
		line.startWidth = Width;
		line.endWidth = Width;
		line.startColor = color;
		line.endColor = color;
		line.enabled = false;
		_lines[index] = line;
		_drawn[index] = -1;
		return line;
	}

	// About one point per block of the circle, each on the ground under it; the last point closes the ring.
	internal static void Draw(LineRenderer line, Vector3 center, int radius)
	{
		int segments = Mathf.Clamp(Mathf.CeilToInt(2f * Mathf.PI * radius), 24, 256);
		line.positionCount = segments + 1;
		for (int i = 0; i <= segments; i++)
		{
			float angle = (i % segments) * 2f * Mathf.PI / segments;
			float x = center.x + Mathf.Cos(angle) * radius;
			float z = center.z + Mathf.Sin(angle) * radius;
			line.SetPosition(i, new Vector3(x, Ground(x, z, center.y) + Lift, z));
		}
	}

	// The top of the highest non-empty voxel of the column near the crystal's height; the crystal's height if none.
	private static float Ground(float x, float z, float height)
	{
		XYZ top = XYZ.FromVector3(new Vector3(x, height, z));
		for (int y = top.y + Above; y >= top.y - Below; y--)
		{
			if (Voxel.IsNonEmpty(new XYZ(top.x, y, top.z)))
			{
				return y + 0.5f;
			}
		}
		return height;
	}

	private void OnDestroy()
	{
		foreach (LineRenderer line in _lines)
		{
			if (line != null)
			{
				Destroy(line.gameObject);
			}
		}
		_lines.Clear();
		_drawn.Clear();
	}
}

// The crystal wheel's command and conditions, with the game's own.
[Feature]
[HarmonyPatch(typeof(Instructions), nameof(Instructions.Batire))]
internal static class BaseRangesWheelCommandsPatch
{
	private static void Postfix()
	{
		if (BaseRanges.AlwaysShow != null)
		{
			BaseRanges.RegisterWheel();
		}
	}
}

// The crystal wheel's button, added to the wheel files as they are read (only with AutoRepair or AutoCleanup on).
[Feature]
[HarmonyPatch(typeof(Livre), nameof(Livre.Split))]
internal static class BaseRangesWheelFilesPatch
{
	private static void Prefix(ref string contenu)
	{
		if (contenu != null)
		{
			contenu = BaseRanges.AddToWheel(contenu);
		}
	}
}
