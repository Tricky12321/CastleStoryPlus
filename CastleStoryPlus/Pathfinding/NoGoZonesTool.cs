using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Brix.Audio;
using Brix.Engine;
using Brix.Game;
using Brix.Game.AI;
using Brix.Input;
using Brix.UI.Icons;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace CastleStoryPlus.Pathfinding;

// Drawing the no-go zones (NoGoZones). The "No-go zones" button on the in-game menu's right-hand bar, or Ctrl with
// [NoGoZones] ToolKey (N), starts the tool: a left-button drag on the ground marks a rectangle of columns as a zone of
// the player's faction, Shift held when the drag starts erases instead; right-click, Esc, the button or the key again
// end it. The drag shows its rectangle as a box (yellow: a zone, red: erasing), as Ctrl+C's area does (CopyPaste), and
// while the tool runs the game's pickers stand by (no unit selection, no placing). The zones of the player's team are
// shown on the ground as red cells (their edge brighter): faint all the time ([NoGoZones] AlwaysShow), clearer while
// the tool runs. A drawing goes through NoGoZones.Request: at once on the host, to the host from a client.
[Feature(Features.NoGoZones, Features.NoGoZonesInfo)]
internal static class NoGoZonesTool
{
	private static ConfigEntry<KeyCode> _key;

	internal static ConfigEntry<bool> AlwaysShow;

	private static bool _dragging;

	private static bool _erasing;

	private static XYZ _start;

	private static XYZ _end;

	private static LineRenderer _outline;

	private static Material _outlineMaterial;

	// While true, the game's pickers are paused so a drag does not select units or place anything.
	internal static bool Drawing;

	private static void Enable()
	{
		_key = Plugin.Cfg.Bind("NoGoZones", "ToolKey", KeyCode.N, "With Ctrl held: start or end drawing no-go zones (drag on the ground; Shift+drag erases).");
		AlwaysShow = Plugin.Cfg.Bind("NoGoZones", "AlwaysShow", true, "Show your team's no-go zones on the ground all the time (faint), not only while drawing them.");
		SceneManager.activeSceneChanged += (Scene from, Scene to) => Stop();
		GameUi.GameMenuItems.Extend((GameUi.GameMenuModel menu) =>
		{
			int at = menu.RightBar.FindIndex((GameUi.UiItem item) => item == null);
			menu.RightBar.Insert((at < 0) ? menu.RightBar.Count : at, new GameUi.UiButton
			{
				Id = "noGoZones",
				Label = () => "No-go zones",
				Icon = () => GameUi.GameUiState.Icon(IconKeys._UI_Cancel),
				Hotkey = () => "Ctrl+" + _key.Value,
				Highlight = () => Drawing,
				Tooltip = () => "No-go zones: drag on the ground to mark an area your team's units route round (no wall is built; a unit whose goal is inside still goes in). Shift+drag erases, right-click or Esc ends." + (string.IsNullOrEmpty(NoGoZones.LastRefusal) ? string.Empty : "\n" + NoGoZones.LastRefusal),
				OnClick = (bool shift, bool ctrl) => Toggle()
			});
		});
		Plugin.Root.AddComponent<NoGoZonesView>();
	}

	// Ctrl with the tool's key: the game's own key actions on that key stand by.
	internal static bool IsShortcutHeld()
	{
		return _key != null && (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) && Input.GetKey(_key.Value);
	}

	internal static void Toggle()
	{
		if (Drawing)
		{
			Stop();
			return;
		}
		if (Picking.Instance == null || User.LocalUser == null || User.LocalUser.faction == null || !NoGoZones.Active)
		{
			return;
		}
		if (InputModeController._mode == InputMode.blueprint)
		{
			Picking.Instance.ClearPick();
		}
		Drawing = true;
		_dragging = false;
	}

	internal static void Stop()
	{
		Drawing = false;
		_dragging = false;
		HideBox();
	}

	// Every frame from InputModeController.Update.
	internal static void Update()
	{
		if (Picking.Instance == null || User.LocalUser == null)
		{
			if (Drawing)
			{
				Stop();
			}
			return;
		}
		if ((Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) && Input.GetKeyDown(_key.Value) && !InputModeController.IsPaused() && !InputModeController.IsInMenu())
		{
			InputMode mode = InputModeController._mode;
			if (Drawing || mode == InputMode.command || mode == InputMode.blueprint)
			{
				Toggle();
				return;
			}
		}
		if (!Drawing)
		{
			return;
		}
		if (Input.GetKeyDown(KeyCode.Escape) || (Input.GetMouseButtonDown(1) && !_dragging) || InputModeController.IsPaused() || InputModeController.IsInMenu() || !NoGoZones.Active)
		{
			Stop();
			return;
		}
		bool hit = VoxelUnderCursor(out XYZ voxel);
		if (!_dragging)
		{
			if (Input.GetMouseButtonDown(0) && hit && !OverUI())
			{
				_dragging = true;
				_erasing = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
				_start = voxel;
				_end = voxel;
			}
			else
			{
				if (hit && !OverUI())
				{
					bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
					DrawBox(voxel, voxel, shift);
				}
				else
				{
					HideBox();
				}
				return;
			}
		}
		if (hit)
		{
			NoGoRect kept = NoGoRules.Drawn(_start.x, _start.z, voxel.x, voxel.z);
			_end = new XYZ((voxel.x >= _start.x) ? kept.MaxX : kept.MinX, voxel.y, (voxel.z >= _start.z) ? kept.MaxZ : kept.MinZ);
		}
		DrawBox(_start, _end, _erasing);
		if (Input.GetMouseButtonUp(0) || !Input.GetMouseButton(0))
		{
			_dragging = false;
			HideBox();
			NoGoRect rect = NoGoRules.Drawn(_start.x, _start.z, _end.x, _end.z);
			NoGoZones.Request(_erasing ? NoGoZones.OpErase : NoGoZones.OpAdd, rect);
			if (NoGoZones.LastRefusal != null)
			{
				Plugin.Log.LogInfo("NoGoZones: " + NoGoZones.LastRefusal);
				SoundEngine.Play("Brick_Cancel");
			}
			else
			{
				SoundEngine.Play("Brick_Turn");
			}
		}
	}

	private static bool OverUI()
	{
		return EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
	}

	private static bool VoxelUnderCursor(out XYZ voxel)
	{
		voxel = XYZ.zero;
		VoxelRaycastHit hit = new VoxelRaycastHit();
		LayerBundle layers = new LayerBundle((int)UnityLayer.Terrain, (int)UnityLayer.Blueprints, (int)UnityLayer.FreeBlocks, (int)UnityLayer.DynamicObjects);
		if (Camera.main == null || !hit.PlaceAtRaycast(Camera.main.ScreenPointToRay(Input.mousePosition), Picking.Instance.MaxDistance, layers))
		{
			return false;
		}
		voxel = hit.VoxelPosition;
		return true;
	}

	private static void DrawBox(XYZ a, XYZ b, bool erasing)
	{
		if (_outline == null)
		{
			GameObject go = new GameObject("CastleStoryPlusNoGoArea");
			_outline = go.AddComponent<LineRenderer>();
			// Part of the game scene and made again every game; the material is kept for the session.
			if (_outlineMaterial == null)
			{
				Shader shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
				_outlineMaterial = new Material(shader);
			}
			_outline.sharedMaterial = _outlineMaterial;
			_outline.useWorldSpace = true;
			_outline.startWidth = 0.1f;
			_outline.endWidth = 0.1f;
			_outline.positionCount = 16;
		}
		Color color = erasing ? new Color(1f, 0.25f, 0.2f, 1f) : new Color(1f, 0.82f, 0.1f, 1f);
		_outline.startColor = color;
		_outline.endColor = color;
		float x0 = Mathf.Min(a.x, b.x) - 0.5f;
		float x1 = Mathf.Max(a.x, b.x) + 0.5f;
		float z0 = Mathf.Min(a.z, b.z) - 0.5f;
		float z1 = Mathf.Max(a.z, b.z) + 0.5f;
		float y0 = Mathf.Min(a.y, b.y) - 0.5f;
		float y1 = Mathf.Max(a.y, b.y) + 1.5f;
		Vector3 b0 = new Vector3(x0, y0, z0);
		Vector3 b1 = new Vector3(x1, y0, z0);
		Vector3 b2 = new Vector3(x1, y0, z1);
		Vector3 b3 = new Vector3(x0, y0, z1);
		Vector3 t0 = new Vector3(x0, y1, z0);
		Vector3 t1 = new Vector3(x1, y1, z0);
		Vector3 t2 = new Vector3(x1, y1, z1);
		Vector3 t3 = new Vector3(x0, y1, z1);
		_outline.SetPositions(new[] { b0, b1, b2, b3, b0, t0, t1, b1, t1, t2, b2, t2, t3, b3, t3, t0 });
		_outline.enabled = true;
	}

	private static void HideBox()
	{
		if (_outline != null)
		{
			_outline.enabled = false;
		}
	}
}

// The zones of the local player's team on the ground: one quad per column, on the column's ground (the map's own top,
// not a building or block on it), red, the zone's edge brighter. Made again when the zones change and every few
// seconds (the ground under them is dug or built up).
[Feature(Features.NoGoZones, Features.NoGoZonesInfo)]
internal sealed class NoGoZonesView : MonoBehaviour
{
	private const float RefreshSeconds = 10f;

	// The most columns drawn (a quad each).
	private const int MaxCells = 60000;

	private const float Lift = 0.06f;

	private static readonly Color Inside = new Color(0.95f, 0.15f, 0.1f, 1f);

	private static readonly Color Edge = new Color(1f, 0.3f, 0.15f, 1f);

	private GameObject _go;

	private Mesh _mesh;

	private Material _material;

	private int _version = -1;

	private float _built;

	private bool _drawing;

	private bool _always;

	private Faction _faction;

	private readonly List<Vector3> _vertices = new List<Vector3>();

	private readonly List<Color> _colors = new List<Color>();

	private readonly List<int> _triangles = new List<int>();

	private void LateUpdate()
	{
		try
		{
			Refresh();
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning("NoGoZones view: " + ex.Message);
			Hide();
			enabled = false;
		}
	}

	private void OnDisable()
	{
		Hide();
	}

	private void Hide()
	{
		if (_go != null)
		{
			_go.SetActive(false);
		}
	}

	private void Refresh()
	{
		Faction faction = (User.LocalUser != null) ? User.LocalUser.faction : null;
		bool always = NoGoZonesTool.AlwaysShow == null || NoGoZonesTool.AlwaysShow.Value;
		bool drawing = NoGoZonesTool.Drawing;
		if (faction == null || !VoxelEngine.Ready || !NoGoZones.Any || (!always && !drawing))
		{
			Hide();
			return;
		}
		if (_go == null)
		{
			// Part of the game scene, made again every game; the mesh and material are kept for the session.
			_go = new GameObject("CastleStoryPlusNoGoZones");
			if (_mesh == null)
			{
				_mesh = new Mesh();
				_mesh.indexFormat = IndexFormat.UInt32;
			}
			_go.AddComponent<MeshFilter>().sharedMesh = _mesh;
			MeshRenderer renderer = _go.AddComponent<MeshRenderer>();
			renderer.shadowCastingMode = ShadowCastingMode.Off;
			renderer.receiveShadows = false;
			if (_material == null)
			{
				Shader shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
				_material = new Material(shader);
			}
			renderer.sharedMaterial = _material;
			_version = -1;
		}
		if (_version != NoGoZones.Version || faction != _faction || drawing != _drawing || always != _always || Time.unscaledTime - _built > RefreshSeconds)
		{
			Build(faction, drawing);
		}
		_go.SetActive(_vertices.Count > 0);
	}

	private void Build(Faction faction, bool drawing)
	{
		_version = NoGoZones.Version;
		_faction = faction;
		_drawing = drawing;
		_always = NoGoZonesTool.AlwaysShow == null || NoGoZonesTool.AlwaysShow.Value;
		_built = Time.unscaledTime;
		_vertices.Clear();
		_colors.Clear();
		_triangles.Clear();
		NoGoMap map = NoGoZones.MapFor(faction);
		float alpha = drawing ? 0.4f : 0.18f;
		Color inside = new Color(Inside.r, Inside.g, Inside.b, alpha);
		Color edge = new Color(Edge.r, Edge.g, Edge.b, Mathf.Min(1f, alpha * 2f));
		int cells = 0;
		if (map != null)
		{
			foreach (KeyValuePair<Faction, List<NoGoRect>> entry in NoGoZones.All)
			{
				if (!NoGoZones.Heeds(faction, entry.Key))
				{
					continue;
				}
				foreach (NoGoRect rect in entry.Value)
				{
					for (int x = rect.MinX; x <= rect.MaxX && cells < MaxCells; x++)
					{
						for (int z = rect.MinZ; z <= rect.MaxZ && cells < MaxCells; z++)
						{
							int top = VoxelEngine.GetColumnTop(x, z);
							if (top <= 0)
							{
								continue;
							}
							bool border = !map.Contains(x - 1, z) || !map.Contains(x + 1, z) || !map.Contains(x, z - 1) || !map.Contains(x, z + 1);
							Quad(x, top - 0.5f + Lift, z, border ? edge : inside);
							cells++;
						}
					}
				}
			}
		}
		_mesh.Clear();
		_mesh.SetVertices(_vertices);
		_mesh.SetColors(_colors);
		_mesh.SetTriangles(_triangles, 0);
		_mesh.RecalculateBounds();
	}

	private void Quad(int x, float y, int z, Color color)
	{
		int i = _vertices.Count;
		_vertices.Add(new Vector3(x - 0.5f, y, z - 0.5f));
		_vertices.Add(new Vector3(x - 0.5f, y, z + 0.5f));
		_vertices.Add(new Vector3(x + 0.5f, y, z + 0.5f));
		_vertices.Add(new Vector3(x + 0.5f, y, z - 0.5f));
		for (int k = 0; k < 4; k++)
		{
			_colors.Add(color);
		}
		_triangles.Add(i);
		_triangles.Add(i + 1);
		_triangles.Add(i + 2);
		_triangles.Add(i);
		_triangles.Add(i + 2);
		_triangles.Add(i + 3);
	}
}

[Feature(Features.NoGoZones, Features.NoGoZonesInfo)]
[HarmonyPatch(typeof(InputModeController), nameof(InputModeController.Update))]
internal static class NoGoZonesToolInputPatch
{
	private static void Prefix()
	{
		try
		{
			NoGoZonesTool.Update();
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning("NoGoZones tool: " + ex.Message);
			NoGoZonesTool.Stop();
		}
	}
}

// While zones are drawn, the game's pickers stand by (no unit selection, no placing).
[Feature(Features.NoGoZones, Features.NoGoZonesInfo)]
[HarmonyPatch(typeof(Picking), "OnUpdate")]
internal static class NoGoZonesToolPickingPatch
{
	private static void Prefix()
	{
		if (NoGoZonesTool.Drawing)
		{
			Picking.actif = false;
		}
	}
}

// Key actions on the tool's key do not fire for Ctrl+N.
[Feature(Features.NoGoZones, Features.NoGoZonesInfo)]
[HarmonyPatch(typeof(Brix.Utils.UI.KeyBindingsUtility), nameof(Brix.Utils.UI.KeyBindingsUtility.RegisterAction))]
internal static class NoGoZonesToolKeyActionPatch
{
	private static void Prefix(ref Action<Rewired.InputActionEventData> action)
	{
		Action<Rewired.InputActionEventData> inner = action;
		action = (Rewired.InputActionEventData data) =>
		{
			if (!NoGoZonesTool.IsShortcutHeld())
			{
				inner(data);
			}
		};
	}
}
