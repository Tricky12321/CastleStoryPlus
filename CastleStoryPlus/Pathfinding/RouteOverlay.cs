using System.Collections.Generic;
using BepInEx.Configuration;
using Brix.Engine;
using Brix.Game;
using Brix.Game.AI;
using Brix.Input;
using Brix.UI.GameSelector;
using CastleStoryPlus.Core;
using CastleStoryPlus.WorkerAI;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace CastleStoryPlus.Pathfinding;

// Route overlay (#200): a line along the rest of the route of each of the local player's selected bricktrons, coloured
// by how the unit gets along it: light blue where it walks, amber where it climbs (a ledge up, a step down, a ladder),
// and red while it is stuck (it has a route but has not moved for RouteOverlayRules.StuckSeconds, or it digs its way
// out of a pit, StuckEscape). Toggled with [RouteOverlay] Key (8) or the settings window ([RouteOverlay] Shown, off by
// default), so it is out of the way in normal play.
// The route is the walker's own (Locomotion4.PathIterator: the nodes it walks next, from where it is). The walker runs
// on the host, so the overlay shows routes in single player and for the host; a multiplayer client has none to show.
// Nothing is sent or saved.
// Cheap: the lines are rebuilt only when a unit's route changes (another route, the next node, stuck or not); every
// frame only the line's first point follows the unit. Line renderers are pooled, one per run of one colour.
[Feature(Features.RouteOverlay, Features.RouteOverlayInfo)]
internal sealed class RouteOverlay : MonoBehaviour
{
	private sealed class Unit
	{
		public readonly RouteStuckWatch Watch = new RouteStuckWatch();

		public readonly List<LineRenderer> Lines = new List<LineRenderer>();

		// The state of each drawn run, and the route points drawn (DevTools tests).
		public readonly List<RouteStepState> States = new List<RouteStepState>();

		public int Points;

		// What the lines were built from.
		public IPath Path;

		public int Index = -1;

		public int Count;

		public XYZ First;

		public XYZ Last;

		public bool Stuck;

		public bool Seen;
	}

	private const float Width = 0.12f;

	internal static ConfigEntry<bool> Shown;

	internal static ConfigEntry<KeyCode> Key;

	private static RouteOverlay _instance;

	private static Material _material;

	private readonly Dictionary<Labor, Unit> _units = new Dictionary<Labor, Unit>();

	private readonly Stack<LineRenderer> _pool = new Stack<LineRenderer>();

	private readonly List<Vector3> _points = new List<Vector3>();

	private readonly List<RouteStepState> _steps = new List<RouteStepState>();

	private readonly List<RouteRun> _runs = new List<RouteRun>();

	private readonly List<Labor> _gone = new List<Labor>();

	private List<GameObject> _selected = new List<GameObject>();

	private Transform _lines;

	private static void Enable()
	{
		Shown = Plugin.Cfg.Bind("RouteOverlay", "Shown", false, "Show the route overlay: a line along the rest of the route of each selected bricktron (light blue: walking, amber: climbing, red: stuck). Also switched with [RouteOverlay] Key.");
		Key = Plugin.Cfg.Bind("RouteOverlay", "Key", KeyCode.Alpha8, "Key that shows or hides the route overlay of the selected bricktrons (None: no key). Not while typing or in a menu.");
		_instance = Plugin.Root.AddComponent<RouteOverlay>();
	}

	// ---- DevTools tests

	// The route points drawn for the unit now (0: none).
	internal static int DrawnPoints(Labor labor)
	{
		return (_instance != null && labor != null && _instance._units.TryGetValue(labor, out Unit unit) && unit.Lines.Count > 0) ? unit.Points : 0;
	}

	// Whether a run of the unit's drawn route has the state.
	internal static bool Drawn(Labor labor, RouteStepState state)
	{
		return _instance != null && labor != null && _instance._units.TryGetValue(labor, out Unit unit) && unit.Lines.Count > 0 && unit.States.Contains(state);
	}

	// ---- every frame

	private void Update()
	{
		if (KeyPressed())
		{
			Shown.Value = !Shown.Value;
		}
		if (Shown == null || !Shown.Value || Voxel.Engine == null || UIGameSelector.instance == null)
		{
			Clear();
			return;
		}
		User user = User.LocalUser;
		Faction team = (user != null) ? user.faction : null;
		foreach (Unit unit in _units.Values)
		{
			unit.Seen = false;
		}
		_selected.Clear();
		if (team != null)
		{
			UIGameSelector.GetSelectedOfType(ref _selected, SelectionType.Unit);
		}
		int taken = 0;
		for (int i = 0; i < _selected.Count && taken < RouteOverlayRules.MaxUnits; i++)
		{
			GameObject go = _selected[i];
			Labor labor = (go != null) ? go.GetComponent<Labor>() : null;
			if (labor == null || labor.faction != team || !labor.gameObject.activeInHierarchy)
			{
				continue;
			}
			taken++;
			if (!_units.TryGetValue(labor, out Unit unit))
			{
				unit = new Unit();
				_units[labor] = unit;
			}
			unit.Seen = true;
			try
			{
				Refresh(labor, unit);
			}
			catch (System.Exception ex)
			{
				Plugin.Log.LogWarning("RouteOverlay: " + ex.Message);
				Hide(unit);
			}
		}
		_selected.Clear();
		// Units no longer selected (or gone).
		_gone.Clear();
		foreach (KeyValuePair<Labor, Unit> pair in _units)
		{
			if (!pair.Value.Seen || pair.Key == null)
			{
				_gone.Add(pair.Key);
			}
		}
		foreach (Labor labor in _gone)
		{
			Hide(_units[labor]);
			_units.Remove(labor);
		}
		_gone.Clear();
	}

	private void Refresh(Labor labor, Unit unit)
	{
		Locomotion4 legs = (labor.navigation != null) ? labor.navigation.Locomotion : null;
		DestinationIterator route = (legs != null) ? legs.PathIterator : null;
		WalkNode next = (route != null && route.Path != null) ? route.GetNth(route.CurrentIndex) : null;
		bool hasRoute = next != null && next.PathNode != null;
		Vector3 position = labor.transform.position;
		bool stuck = unit.Watch.Update(Time.time, position.x, position.y, position.z, hasRoute);
		if (!hasRoute)
		{
			Hide(unit);
			return;
		}
		stuck = stuck || StuckEscape.IsEscaping(labor);
		int count = route.Nodes.Count;
		WalkNode last = route.GetNth(count - 1);
		XYZ first = next.Voxel;
		XYZ end = (last != null && last.PathNode != null) ? last.Voxel : first;
		if (unit.Lines.Count > 0 && unit.Path == route.Path && unit.Index == route.CurrentIndex && unit.Count == count && unit.First == first && unit.Last == end && unit.Stuck == stuck)
		{
			// The same route: only the start follows the unit.
			unit.Lines[0].SetPosition(0, UnitPoint(position));
			return;
		}
		unit.Path = route.Path;
		unit.Index = route.CurrentIndex;
		unit.Count = count;
		unit.First = first;
		unit.Last = end;
		unit.Stuck = stuck;
		Build(unit, route, position, stuck);
	}

	private void Build(Unit unit, DestinationIterator route, Vector3 position, bool stuck)
	{
		_points.Clear();
		_steps.Clear();
		_points.Add(UnitPoint(position));
		for (int i = route.CurrentIndex; _points.Count < RouteOverlayRules.MaxPoints; i++)
		{
			WalkNode node = route.GetNth(i);
			if (node == null || node.PathNode == null)
			{
				break;
			}
			_points.Add(node.Voxel.ToVector3() + new Vector3(0f, RouteOverlayRules.Lift, 0f));
			_steps.Add(RouteOverlayRules.StepState(node.MustClimbUp(), node.MustStepDown(), node.IsLadder(), stuck));
		}
		RouteOverlayRules.Runs(_steps, _runs);
		while (unit.Lines.Count > _runs.Count)
		{
			Release(unit.Lines[unit.Lines.Count - 1]);
			unit.Lines.RemoveAt(unit.Lines.Count - 1);
		}
		while (unit.Lines.Count < _runs.Count)
		{
			unit.Lines.Add(Take());
		}
		unit.States.Clear();
		for (int r = 0; r < _runs.Count; r++)
		{
			RouteRun run = _runs[r];
			LineRenderer line = unit.Lines[r];
			RouteOverlayRules.Colour(run.State, out float red, out float green, out float blue);
			Color colour = new Color(red, green, blue, 1f);
			line.startColor = colour;
			line.endColor = colour;
			line.positionCount = run.Last - run.First + 1;
			for (int p = run.First; p <= run.Last; p++)
			{
				line.SetPosition(p - run.First, _points[p]);
			}
			line.enabled = true;
			unit.States.Add(run.State);
		}
		unit.Points = _points.Count;
		_points.Clear();
		_steps.Clear();
	}

	private static Vector3 UnitPoint(Vector3 position)
	{
		return new Vector3(position.x, RouteOverlayRules.VoxelLevel(position.y) + RouteOverlayRules.Lift, position.z);
	}

	// ---- lines

	private LineRenderer Take()
	{
		if (_pool.Count > 0)
		{
			return _pool.Pop();
		}
		if (_lines == null)
		{
			_lines = new GameObject("CastleStoryPlusRouteOverlay").transform;
			_lines.SetParent(transform, false);
		}
		if (_material == null)
		{
			Shader shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
			_material = new Material(shader);
		}
		GameObject go = new GameObject("Route");
		go.transform.SetParent(_lines, false);
		LineRenderer line = go.AddComponent<LineRenderer>();
		line.sharedMaterial = _material;
		line.useWorldSpace = true;
		line.startWidth = Width;
		line.endWidth = Width;
		line.numCornerVertices = 2;
		line.shadowCastingMode = ShadowCastingMode.Off;
		line.receiveShadows = false;
		line.enabled = false;
		return line;
	}

	private void Release(LineRenderer line)
	{
		if (line == null)
		{
			return;
		}
		line.enabled = false;
		line.positionCount = 0;
		_pool.Push(line);
	}

	private void Hide(Unit unit)
	{
		foreach (LineRenderer line in unit.Lines)
		{
			Release(line);
		}
		unit.Lines.Clear();
		unit.States.Clear();
		unit.Points = 0;
		unit.Path = null;
		unit.Index = -1;
	}

	private void Clear()
	{
		if (_units.Count == 0)
		{
			return;
		}
		foreach (Unit unit in _units.Values)
		{
			Hide(unit);
		}
		_units.Clear();
	}

	// Switched off (the feature's switch): nothing stays drawn.
	private void OnDisable()
	{
		Clear();
	}

	private void OnDestroy()
	{
		if (_lines != null)
		{
			Destroy(_lines.gameObject);
		}
		if (_instance == this)
		{
			_instance = null;
		}
	}

	// ---- the key

	private static bool KeyPressed()
	{
		if (Key == null || Shown == null || Key.Value == KeyCode.None || !UnityEngine.Input.GetKeyDown(Key.Value))
		{
			return false;
		}
		if (Voxel.Engine == null || InputModeController._mode == InputMode.blueprint || InputModeController.IsInMenu())
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
}
