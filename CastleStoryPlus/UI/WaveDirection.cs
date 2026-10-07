using BepInEx.Configuration;
using CastleStoryPlus.Core;
using MoonSharp.Interpreter;
using UnityEngine;
using UnityEngine.UI;

namespace CastleStoryPlus.UI;

// Invasion (survival): where the next wave will come from. The game picked a wave's spawn point (one of the map's
// corruptron spawn markers) at random only when the wave spawned. The host now picks the next one ahead, when the
// previous wave spawns (or when the game starts), and sends it to every player with the wave timer. From 30 seconds
// before the wave an arrow on the edge of the screen points at the spawn point (over the spot itself when it is on
// screen), and it stays a little after the wave arrives. The picked point is kept in the save, so it stays the same
// after loading. Not shown when the preset hides the wave timer.
[Feature(Features.WaveDirection, Features.WaveDirectionInfo)]
internal class WaveDirection : MonoBehaviour
{
	private const float StaleSeconds = 3f;

	private const float EdgeMargin = 70f;

	private const float ArrowSize = 64f;

	private static readonly Color ArrowColor = new Color(1f, 0.25f, 0.2f, 1f);

	private static ConfigEntry<float> ShowSeconds;

	private static ConfigEntry<float> HoldSeconds;

	private static WaveDirection _instance;

	private bool _hasSpawn;

	private Vector3 _spawn;

	private float _reported = -1f;

	private float _reportedAt;

	private float _reportedUnscaled;

	private bool _showing;

	private Vector3 _shown;

	private float _holdUntil;

	private Canvas _canvas;

	private RectTransform _arrow;

	private Text _label;

	private static void Enable()
	{
		ShowSeconds = Plugin.Cfg.Bind("WaveDirection", "ShowSeconds", 30f, new ConfigDescription("Seconds before the next wave the arrow to its spawn point shows.", new AcceptableValueRange<float>(5f, 600f)));
		HoldSeconds = Plugin.Cfg.Bind("WaveDirection", "HoldSeconds", 15f, new ConfigDescription("Seconds the arrow stays after the wave has arrived.", new AcceptableValueRange<float>(0f, 120f)));
		LuaInjection.AddFunction("WaveSpawn", (ScriptExecutionContext context, CallbackArguments args) =>
		{
			if (_instance != null)
			{
				_instance._hasSpawn = args.Count >= 3 && args[0].Type == DataType.Number;
				if (_instance._hasSpawn)
				{
					_instance._spawn = new Vector3((float)args[0].Number, (float)args[1].Number, (float)args[2].Number);
				}
			}
			return DynValue.Void;
		});
		LuaInjection.AddFunction("WaveDirectionTimer", (ScriptExecutionContext context, CallbackArguments args) =>
		{
			if (_instance != null)
			{
				DynValue value = args[0];
				_instance._reported = (value.Type == DataType.Number) ? (float)value.Number : -1f;
				_instance._reportedAt = Time.time;
				_instance._reportedUnscaled = Time.unscaledTime;
			}
			return DynValue.Void;
		});
		// Host: the next spawn point, picked ahead and kept in the saved Registry.
		LuaInjection.AddPatch(Features.WaveDirection, "Gamemodes/invasion/message.lua", "function _SendWaveTime(startsTimer)\n", LuaInjection.Mode.InsertBefore, @"---Castle Story Plus (WaveDirection): the next wave's spawn point, picked ahead so the players can be warned
function CastleStoryPlusSpawnIndex(spawns)
	if spawns == nil or #spawns == 0 then return nil end
	local index = Registry.cspNextSpawn
	if index == nil or spawns[index] == nil then
		index = math.random(#spawns)
		Registry.cspNextSpawn = index
	end
	return index
end

");
		LuaInjection.AddPatch(Features.WaveDirection, "Gamemodes/invasion/invasion.lua", "\t\tlocal spawnPosition = spawns[math.random(#spawns)].position\n", LuaInjection.Mode.Replace, @"		---Castle Story Plus (WaveDirection): the spawn point picked ahead, then the next wave's
		local spawnPosition = spawns[CastleStoryPlusSpawnIndex(spawns) or math.random(#spawns)].position
		Registry.cspNextSpawn = math.random(#spawns)
");
		// Host: the spawn point goes out with the wave timer (x, y, z as numbers).
		LuaInjection.AddPatch(Features.WaveDirection, "Gamemodes/invasion/message.lua", "\tt[NetworkMessage.WaveTime.Starts] = startsTimer\n", LuaInjection.Mode.InsertAfter, @"	---Castle Story Plus (WaveDirection)
	if IsServer() then
		local cspSpawns = Markers.GetGroup(""Corruptron Spawn Point"")
		local cspIndex = CastleStoryPlusSpawnIndex(cspSpawns)
		if cspIndex ~= nil then
			local cspPosition = cspSpawns[cspIndex].position
			t[4] = cspPosition.x
			t[5] = cspPosition.y
			t[6] = cspPosition.z
		end
	end
");
		// Every player: the spawn point from the host.
		LuaInjection.AddPatch(Features.WaveDirection, "Gamemodes/invasion/message.lua", "\tNetworkEvent.onSetWaveTime.Trigger()\n", LuaInjection.Mode.InsertBefore, @"	---Castle Story Plus (WaveDirection)
	if t[4] ~= nil then
		CastleStoryPlus.WaveSpawn(t[4], t[5], t[6])
	else
		CastleStoryPlus.WaveSpawn()
	end
");
		LuaInjection.AddPatch(Features.WaveDirection, "Gamemodes/invasion/clientside.lua", "Hooks.Connect(hk_SlowUpdate, clSlowUpdate)\n", LuaInjection.Mode.InsertAfter, @"
---Castle Story Plus (WaveDirection): the wave timer, for the arrow to the next wave's spawn point
Hooks.Connect(hk_SlowUpdate, function()
	if Registry.waveTimer ~= nil and Registry.waveTimer.running and sv_Settings.displayTimeBeforeWave == true then
		CastleStoryPlus.WaveDirectionTimer(Registry.waveTimer:RemainingSeconds())
	else
		CastleStoryPlus.WaveDirectionTimer(-1)
	end
end)
");
		_instance = Plugin.Root.AddComponent<WaveDirection>();
	}

	private void Update()
	{
		bool stale = Time.unscaledTime - _reportedUnscaled > StaleSeconds && Time.timeScale > 0f;
		float remaining = (_reported < 0f || stale) ? -1f : _reported - (Time.time - _reportedAt);
		if (remaining >= 0f && remaining <= ShowSeconds.Value && _hasSpawn)
		{
			_showing = true;
			_shown = _spawn;
			_holdUntil = 0f;
		}
		else if (_showing)
		{
			// The wave came (the timer started again) or the game ended: keep pointing at where it came from.
			_showing = false;
			_holdUntil = (remaining >= 0f) ? Time.unscaledTime + HoldSeconds.Value : 0f;
		}
		bool holding = Time.unscaledTime < _holdUntil;
		Camera camera = Camera.main;
		if ((!_showing && !holding) || camera == null)
		{
			SetVisible(false);
			return;
		}
		EnsureArrow();
		SetVisible(true);
		_label.text = _showing ? "NEXT WAVE " + Mathf.CeilToInt(Mathf.Max(0f, remaining)) + "s" : "WAVE";
		Place(camera, _shown);
	}

	// The arrow and its label are separate objects (the label stays upright): shown and hidden together.
	private void SetVisible(bool visible)
	{
		if (_arrow != null)
		{
			_arrow.gameObject.SetActive(visible);
		}
		if (_label != null)
		{
			_label.gameObject.SetActive(visible);
		}
	}

	// Over the spawn point when it is on screen, else on the screen's edge in its direction.
	private void Place(Camera camera, Vector3 target)
	{
		Vector3 screen = camera.WorldToScreenPoint(target);
		bool behind = screen.z < 0f;
		if (behind)
		{
			screen.x = Screen.width - screen.x;
			screen.y = Screen.height - screen.y;
		}
		float scale = (_canvas.scaleFactor > 0f) ? _canvas.scaleFactor : 1f;
		float pulse = 1f + 0.12f * Mathf.Abs(Mathf.Sin(Time.unscaledTime * 4f));
		_arrow.localScale = Vector3.one * pulse;
		bool onScreen = !behind && screen.x > EdgeMargin && screen.x < Screen.width - EdgeMargin && screen.y > EdgeMargin && screen.y < Screen.height - EdgeMargin;
		Vector2 position;
		Vector2 direction;
		if (onScreen)
		{
			direction = Vector2.down;
			position = new Vector2(screen.x, screen.y + ArrowSize * scale);
		}
		else
		{
			Vector2 center = new Vector2(Screen.width / 2f, Screen.height / 2f);
			direction = new Vector2(screen.x, screen.y) - center;
			if (direction.sqrMagnitude < 1f)
			{
				direction = Vector2.up;
			}
			direction.Normalize();
			float halfWidth = Screen.width / 2f - EdgeMargin;
			float halfHeight = Screen.height / 2f - EdgeMargin;
			float reach = Mathf.Min((Mathf.Abs(direction.x) > 0.001f) ? halfWidth / Mathf.Abs(direction.x) : float.MaxValue, (Mathf.Abs(direction.y) > 0.001f) ? halfHeight / Mathf.Abs(direction.y) : float.MaxValue);
			position = center + direction * reach;
		}
		_arrow.anchoredPosition = position / scale;
		_arrow.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg - 90f);
		// The label stays upright, on the inner side of the arrow.
		_label.rectTransform.anchoredPosition = (position - direction * ArrowSize * 1.1f * scale) / scale;
	}

	private void EnsureArrow()
	{
		if (_arrow != null)
		{
			return;
		}
		GameObject canvas = UiKit.CreateCanvas("WaveDirectionCanvas", transform, 940);
		_canvas = canvas.GetComponent<Canvas>();
		_arrow = UiKit.CreateRect("Arrow", canvas.transform);
		UiKit.SetRect(_arrow, Vector2.zero, Vector2.zero, Vector2.zero, new Vector2(ArrowSize, ArrowSize));
		Image image = _arrow.gameObject.AddComponent<Image>();
		image.sprite = ArrowSprite();
		image.color = ArrowColor;
		image.raycastTarget = false;
		Outline outline = _arrow.gameObject.AddComponent<Outline>();
		outline.effectColor = new Color(0f, 0f, 0f, 0.9f);
		outline.effectDistance = new Vector2(2f, -2f);
		RectTransform labelRect = UiKit.CreateRect("Label", canvas.transform);
		UiKit.SetRect(labelRect, Vector2.zero, Vector2.zero, Vector2.zero, new Vector2(260f, 40f));
		_label = UiKit.CreateText(labelRect, string.Empty, 26, ArrowColor, TextAnchor.MiddleCenter);
		_label.fontStyle = FontStyle.Bold;
		_label.raycastTarget = false;
		UiKit.SetRect(_label.rectTransform, Vector2.zero, Vector2.zero, Vector2.zero, new Vector2(260f, 40f));
		Outline labelOutline = _label.gameObject.AddComponent<Outline>();
		labelOutline.effectColor = new Color(0f, 0f, 0f, 0.9f);
		labelOutline.effectDistance = new Vector2(2f, -2f);
		SetVisible(false);
	}

	// A white triangle pointing up, tinted by the image colour.
	private static Sprite ArrowSprite()
	{
		const int size = 64;
		Texture2D texture = new Texture2D(size, size, TextureFormat.ARGB32, mipmap: false);
		Color32 clear = new Color32(255, 255, 255, 0);
		Color32 white = new Color32(255, 255, 255, 255);
		for (int y = 0; y < size; y++)
		{
			for (int x = 0; x < size; x++)
			{
				float dx = Mathf.Abs(x + 0.5f - size / 2f);
				bool inside = y >= 4 && y < size - 4 && dx <= (size / 2f - 4f) * (1f - (y - 4f) / (size - 8f));
				texture.SetPixel(x, y, inside ? white : clear);
			}
		}
		texture.Apply();
		return Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f));
	}
}
