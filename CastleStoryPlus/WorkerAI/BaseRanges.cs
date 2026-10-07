using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using Brix.Engine;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Components;
using CastleStoryPlus.Core;
using CastleStoryPlus.Giant;
using MoonSharp.Interpreter;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.WorkerAI;

// The ranges of the idle chores around the base (AutoRepair, AutoCleanup), set on the home crystal: its task popup
// (the crystal's section, after the spawn button) gets a button per range showing it in blocks, with small "+" and
// "-" over it (Shift: 5 at a time), and while the crystal's task is selected a ring on the ground around the crystal
// shows each range in its colour. The ranges are the features' [AutoRepair] / [AutoCleanup] Radius config entries,
// one value for all teams and saves; the chores run on the host, so only the host sees the buttons and rings.
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

	private static bool _registered;

	// Called by each feature's Enable. The two buttons are placed so the repair range comes first either way: one
	// right after the spawn button's block, the other before the next part of the menu (the task's priority).
	public static void Register(string key, ConfigEntry<int> radius, string label, string icon, Color color, bool first)
	{
		Ranges.Add(new Range { Key = key, Radius = radius, Color = color });
		if (!_registered)
		{
			_registered = true;
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
		if (first)
		{
			LuaInjection.AddPatch(key, "LUI/Menus/GameMenu.lua", "_m.bh.idleTaskActionsSpawnUnit = h--TODO:move to task actions\nend\n", LuaInjection.Mode.InsertAfter, Handle(key, label, icon, color));
		}
		else
		{
			LuaInjection.AddPatch(key, "LUI/Menus/GameMenu.lua", "-------\n--task.priority\n", LuaInjection.Mode.InsertBefore, Handle(key, label, icon, color));
		}
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

	// The local player's home crystal while its task (the idle task) is selected in the task list.
	internal static FireflyNest SelectedHome()
	{
		if (!NetworkServer.active || User.LocalUser == null || UIGameObserver.projects == null || UIGameObserver.projects.all == null)
		{
			return null;
		}
		foreach (Project project in UIGameObserver.projects.all)
		{
			if (project != null && project is IdleProject && project.selection && project.faction == User.LocalUser.faction)
			{
				return GiantBricktron.HomeNest(project.faction);
			}
		}
		return null;
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
		FireflyNest nest = BaseRanges.SelectedHome();
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
	private static void Draw(LineRenderer line, Vector3 center, int radius)
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
