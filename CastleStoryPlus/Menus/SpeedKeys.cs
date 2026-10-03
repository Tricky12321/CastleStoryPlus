using BepInEx.Configuration;
using Brix.Engine;
using Brix.Input;
using Brix.Network;
using CastleStoryPlus.Core;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace CastleStoryPlus.Menus;

// Game speed keys: 1 = normal speed, 2 = [GameSpeed] Speed2 (2x), 3 = [GameSpeed] Speed3 (3x).
// Uses the game's own clock speeds (ClockConfig): Double gets Speed2 and the debug-only VeryFast gets Speed3,
// so pausing and resuming keep the chosen speed. Single player only; the game forces normal speed in multiplayer.
// The keys 1-3 are only bound in the world editor, where this does nothing.
[Feature(Features.SpeedKeys, Features.SpeedKeysInfo)]
internal class SpeedKeys : MonoBehaviour
{
	internal static ConfigEntry<float> Speed2;

	internal static ConfigEntry<float> Speed3;

	private static ConfigEntry<KeyCode> _normalKey;

	private static ConfigEntry<KeyCode> _speed2Key;

	private static ConfigEntry<KeyCode> _speed3Key;

	private static void Enable()
	{
		Speed2 = Plugin.Cfg.Bind("GameSpeed", "Speed2", 2f, "Game speed of the second speed key.");
		Speed3 = Plugin.Cfg.Bind("GameSpeed", "Speed3", 3f, "Game speed of the third speed key.");
		_normalKey = Plugin.Cfg.Bind("GameSpeed", "NormalSpeedKey", KeyCode.Alpha1, "Key for normal game speed.");
		_speed2Key = Plugin.Cfg.Bind("GameSpeed", "Speed2Key", KeyCode.Alpha2, "Key for game speed Speed2.");
		_speed3Key = Plugin.Cfg.Bind("GameSpeed", "Speed3Key", KeyCode.Alpha3, "Key for game speed Speed3.");
		Plugin.Root.AddComponent<SpeedKeys>();
	}

	private void Update()
	{
		ClockConfig.ClockSpeed speed;
		if (Input.GetKeyDown(_normalKey.Value))
		{
			speed = ClockConfig.ClockSpeed.Default;
		}
		else if (Input.GetKeyDown(_speed2Key.Value))
		{
			speed = ClockConfig.ClockSpeed.Double;
		}
		else if (Input.GetKeyDown(_speed3Key.Value))
		{
			speed = ClockConfig.ClockSpeed.VeryFast;
		}
		else
		{
			return;
		}
		if (!CanChangeSpeed())
		{
			return;
		}
		ClockConfig.ClockSpeedMap[ClockConfig.ClockSpeed.Double] = Speed2.Value;
		ClockConfig.ClockSpeedMap[ClockConfig.ClockSpeed.VeryFast] = Speed3.Value;
		if (ClockConfig.Paused)
		{
			// Resumes at this speed when the game is unpaused.
			ClockConfig._unpausedClockSpeed = speed;
		}
		else
		{
			ClockConfig._unpausedClockSpeed = speed;
			ClockConfig.SetTimeScale(speed);
		}
		float value = ClockConfig.GetClockSpeedTimeScale(speed);
		GameSignals.Invoke(GameSignals.OnAutoFadeDialog, "Game speed " + value.ToString("0.#") + "x" + (ClockConfig.Paused ? " (paused)" : string.Empty), 1f);
	}

	// In a single player game (not the world editor), and not while typing in a text field.
	private static bool CanChangeSpeed()
	{
		if (Neo.multiplayer || !Architecte.commence || Architecte.termine || InputModeController.DefaultMode == InputMode.worldEditor)
		{
			return false;
		}
		GameObject focused = (EventSystem.current != null) ? EventSystem.current.currentSelectedGameObject : null;
		return focused == null || focused.GetComponent<InputField>() == null;
	}
}
