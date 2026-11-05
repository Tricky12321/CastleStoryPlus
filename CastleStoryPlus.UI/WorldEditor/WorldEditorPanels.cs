using System;
using Brix.Engine;
using Brix.Input;
using Brix.Input.WorldEditor;
using Brix.UI.Builder.Menu;
using CastleStoryPlus.GameUi;
using CastleStoryPlus.LuaUi;
using UnityEngine;

namespace CastleStoryPlus.WorldEditor;

// What the world editor's menus read and change in the game, where the Lua menus called UIGame.
internal static class WorldEditorGame
{
	internal static WorldGridController Grid
	{
		get { return (Picking.Instance != null) ? Picking.Instance.WorldEditorGrid : null; }
	}

	// Every tab's action and close: no terrain brush and no marker tool in the hand.
	internal static void DropTools()
	{
		WeTerrain.SendBrush(WeTerrain.Brush.None);
		WeMarkers.SendTool(WeMarkers.Tool.None);
	}

	// UIGame.ToTimeString: 12.5 as 12:30.
	internal static string TimeText(float time)
	{
		float hours = Mathf.Floor(time);
		int minutes = Mathf.RoundToInt((time - hours) * 60f);
		return hours + ":" + minutes.ToString().PadLeft(2, '0');
	}
}

// The world editor's panels, one per tab, as the Lua menus in LUI/Menus/WorldEditor drew them.
internal static class WorldEditorPanels
{
	// The map preview, drawn again each time the map tab opens (drawing it reads the whole terrain).
	private static Texture2D _map;

	private static Rect _mapUv = new Rect(0f, 0f, 1f, 1f);

	// WeTimeMenu.lua: pause the day, and the time of day.
	internal static UiPanel Time()
	{
		UiPanel panel = new UiPanel();
		UiSection section = panel.Section("actions", () => GameUiState.Text("##mode_worldeditor_actions"));
		section.Add(new UiToggle
		{
			Id = "we_time_pause",
			Label = () => GameUiState.Text("##mode_worldeditor_time_pause"),
			Value = () => Journee.PausedByWorldEditor,
			Set = (bool on) => Journee.PausedByWorldEditor = on
		});
		section.Add(new UiSlider
		{
			Id = "we_time_now",
			Label = () => GameUiState.Text("##mode_worldeditor_time_now"),
			Min = () => 0f,
			Max = () => 24f,
			Value = () => Journee.CurrentTime,
			Set = (float time) => Journee.CurrentTime = time,
			Format = WorldEditorGame.TimeText
		});
		return panel;
	}

	// WeGridMenu.lua: the grid shown, its opacity, locked to the camera's level or at a height of its own.
	internal static UiPanel Grid()
	{
		UiPanel panel = new UiPanel();
		UiSection section = panel.Section("options", () => GameUiState.Text("##mode_worldeditor_options"));
		section.Add(new UiToggle
		{
			Id = "we_grid_visible",
			Label = () => GameUiState.Text("##mode_worldeditor_grid_visible"),
			Value = () => WorldEditorGame.Grid != null && WorldEditorGame.Grid.ActiveByUser,
			Set = (bool on) => WorldEditorGame.Grid.ActiveByUser = on
		});
		section.Add(new UiSlider
		{
			Id = "we_grid_opacity",
			Label = () => GameUiState.Text("##mode_worldeditor_grid_opacity"),
			Min = () => 0f,
			Max = () => 100f,
			Value = () => (WorldEditorGame.Grid != null) ? WorldEditorGame.Grid.OpacityByUser * 100f : 0f,
			Set = (float percent) => WorldEditorGame.Grid.OpacityByUser = percent / 100f,
			Format = (float percent) => Mathf.RoundToInt(percent) + "%"
		});
		section.Add(new UiToggle
		{
			Id = "we_grid_camera",
			Label = () => GameUiState.Text("##mode_worldeditor_grid_heightlocks"),
			Value = () => WorldEditorGame.Grid != null && !WorldEditorGame.Grid.UseHeightOverride,
			Set = (bool locked) => WorldEditorGame.Grid.UseHeightOverride = !locked
		});
		section.Add(new UiSlider
		{
			Id = "we_grid_height",
			Visible = () => WorldEditorGame.Grid != null && WorldEditorGame.Grid.UseHeightOverride,
			Label = () => GameUiState.Text("##mode_worldeditor_grid_height"),
			WholeNumbers = true,
			Min = () => WorldEditorGame.Grid.HeightMin,
			Max = () => WorldEditorGame.Grid.HeightMax,
			Value = () => WorldEditorGame.Grid.HeightOverride,
			Set = (float height) => WorldEditorGame.Grid.HeightOverride = Mathf.RoundToInt(height),
			Format = (float height) => Mathf.RoundToInt(height) + "vx"
		});
		return panel;
	}

	// WeMapMenu.lua: a preview of the map's terrain (grass, earth, trees), cut to the part of the world in use.
	internal static UiPanel Map()
	{
		UiPanel panel = new UiPanel();
		UiSection section = panel.Section("preview", () => GameUiState.Text("##mode_worldeditor_map_preview"));
		section.Add(new UiPicture
		{
			Id = "we_map_preview",
			Width = 288f,
			Height = 288f,
			Texture = () => _map,
			Uv = () => _mapUv
		});
		panel.OnOpen = DrawMap;
		return panel;
	}

	// LUI2.GetMapTexture with WeMapMenu's colours, and GetMapTextureUvs.
	private static void DrawMap()
	{
		if (!GameSignals.Received(GameSignals.TerrainReady))
		{
			return;
		}
		Color grass = Minimap.grassColor;
		Color earth = Minimap.earthColor;
		try
		{
			Minimap.grassColor = new Color(42f / 255f, 148f / 255f, 26f / 255f, 1f);
			Minimap.earthColor = new Color(112f / 255f, 92f / 255f, 56f / 255f, 1f);
			_map = Minimap.GetTerrainTypeMinimap(_map);
			if (_map != null)
			{
				_map.filterMode = FilterMode.Point;
			}
		}
		catch (Exception ex)
		{
			_map = null;
			Plugin.Log.LogWarning("CsWorldEditor: the map preview: " + ex.Message);
		}
		finally
		{
			Minimap.grassColor = grass;
			Minimap.earthColor = earth;
		}
		_mapUv = MapUv();
	}

	// The square round the occupied part of the 1024 x 1024 world.
	private static Rect MapUv()
	{
		Bounds bounds;
		try
		{
			bounds = Voxel.Engine.ComputeOccupiedBounds();
		}
		catch (Exception)
		{
			return new Rect(0f, 0f, 1f, 1f);
		}
		float x = bounds.size.x + 2f;
		float z = bounds.size.z + 2f;
		if (x == 0f && z == 0f)
		{
			return new Rect(0f, 0f, 1f, 1f);
		}
		float side = Mathf.Max(x, z);
		Vector3 min = bounds.center - new Vector3(side, 0f, side) / 2f;
		return new Rect(min.x / 1024f, min.z / 1024f, side / 1024f, side / 1024f);
	}

	internal static UiPanel Terrain()
	{
		return NotYet();
	}

	internal static UiPanel Markers()
	{
		return NotYet();
	}

	internal static UiPanel Modes()
	{
		return NotYet();
	}

	internal static UiPanel Save()
	{
		return NotYet();
	}

	internal static UiPanel Workshop()
	{
		return NotYet();
	}

	// A tab whose panel is not drawn in C# yet.
	private static UiPanel NotYet()
	{
		UiPanel panel = new UiPanel();
		UiSection section = panel.Section("todo", null);
		section.Add(new UiText
		{
			Id = "we_not_yet",
			Text = () => "Not drawn in C# yet. Switch [LuaUi] CsWorldEditor off to use this tab.",
			Color = () => LuiStyle.White
		});
		return panel;
	}

	// The world editor closed: the preview goes with it.
	internal static void Forget()
	{
		if (_map != null)
		{
			UnityEngine.Object.Destroy(_map);
			_map = null;
		}
	}
}
