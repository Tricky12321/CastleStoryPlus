#if UNITY6
using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace CastleStoryPlus.Multiplayer;

// The chat box in a game (ChatBoxDriver) kept its last 10 messages on the screen for good. Each message now goes away
// [Multiplayer] ChatFadeSeconds after it came (0: they stay); while the chat is open for typing, the recent messages
// show again, and with no message left the box is hidden until the next one or until the chat is opened.
// [Multiplayer] Chat = false switches the chat box off: no box, no messages, no sound, and the chat key does nothing.
// The lobby's chat is not changed.
[Feature(Features.GameChat, Features.GameChatInfo)]
[HarmonyPatch(typeof(ChatBoxDriver), "ReceivedChatMessage")]
internal static class GameChat
{
	// The game's own limit of lines in the box.
	private const int MaxLines = 10;

	private struct Line
	{
		public string Text;

		public float Time;
	}

	private static readonly List<Line> Lines = new List<Line>();

	private static readonly StringBuilder Builder = new StringBuilder();

	private static ConfigEntry<bool> _enabled;

	private static ConfigEntry<int> _fadeSeconds;

	private static Text _output;

	private static CanvasGroup _group;

	// When the box has to be drawn again: the next message to go, or the chat opened or closed.
	private static float _nextChange = float.MaxValue;

	private static bool _wasActive;

	private static bool _shownEmpty = true;

	internal static bool Enabled => _enabled == null || _enabled.Value;

	private static float FadeSeconds => (_fadeSeconds != null) ? Math.Max(0, _fadeSeconds.Value) : 0f;

	private static void Enable()
	{
		_enabled = Plugin.Cfg.Bind("Multiplayer", "Chat", true, "Show the chat box in a game. Off: no chat box, no chat messages or their sound, and the chat key does nothing (the lobby's chat stays).");
		_fadeSeconds = Plugin.Cfg.Bind("Multiplayer", "ChatFadeSeconds", 12, new ConfigDescription("Seconds a chat message stays in the chat box of a game; while the chat is open for typing the recent messages show again (0: messages stay, as in the game).", new AcceptableValueRange<int>(0, 600)));
		GameSession.OnLeave(Clear);
	}

	private static void Clear()
	{
		Lines.Clear();
		_output = null;
		_group = null;
		_nextChange = float.MaxValue;
		_wasActive = false;
		_shownEmpty = true;
	}

	// Replaces the game's: the line is kept with its time and the box drawn from the kept lines.
	private static bool Prefix(ChatBoxDriver __instance, string msg)
	{
		if (!Enabled)
		{
			return false;
		}
		MenuSound.PlaySound(MenuSound.Prefix.Main, MenuSound.Suffix.ChatReceive);
		Add(msg);
		Draw(__instance);
		return false;
	}

	private static void Add(string text)
	{
		Lines.Add(new Line { Text = text, Time = Time.unscaledTime });
		while (Lines.Count > MaxLines)
		{
			Lines.RemoveAt(0);
		}
	}

	private static Text Output(ChatBoxDriver driver)
	{
		if (_output == null && driver.chatPanel != null)
		{
			_output = driver.chatPanel.GetPlugComponent<Text>("Output Text");
			if (_output != null)
			{
				_output.supportRichText = true;
			}
		}
		return _output;
	}

	private static CanvasGroup Group(ChatBoxDriver driver)
	{
		if (_group == null && driver.chatPanel != null)
		{
			GameObject panel = driver.chatPanel.gameObject;
			_group = panel.GetComponent<CanvasGroup>();
			if (_group == null)
			{
				_group = panel.AddComponent<CanvasGroup>();
			}
		}
		return _group;
	}

	// Writes the lines still to be shown into the box and hides the box when there are none.
	private static void Draw(ChatBoxDriver driver)
	{
		Text output = Output(driver);
		if (output == null)
		{
			return;
		}
		bool active = driver._isActive;
		float fade = FadeSeconds;
		float now = Time.unscaledTime;
		_nextChange = float.MaxValue;
		Builder.Length = 0;
		int shown = 0;
		foreach (Line line in Lines)
		{
			float ends = line.Time + fade;
			if (fade > 0f && !active && ends <= now)
			{
				continue;
			}
			if (fade > 0f && ends > now && ends < _nextChange)
			{
				_nextChange = ends;
			}
			Builder.Append(line.Text).Append(Environment.NewLine);
			shown++;
		}
		output.text = Builder.ToString();
		Builder.Length = 0;
		_wasActive = active;
		ShowBox(driver, active || shown > 0 || fade <= 0f);
	}

	private static void ShowBox(ChatBoxDriver driver, bool show)
	{
		CanvasGroup group = Group(driver);
		if (group == null)
		{
			return;
		}
		_shownEmpty = !show;
		group.alpha = show ? 1f : 0f;
		group.blocksRaycasts = show;
		group.interactable = show;
	}

	// The game's start of the box: its intro line (multiplayer) becomes the first kept line.
	[Feature(Features.GameChat, Features.GameChatInfo)]
	[HarmonyPatch(typeof(ChatBoxDriver), "Initialize")]
	internal static class InitializePatch
	{
		private static void Postfix(ChatBoxDriver __instance)
		{
			Clear();
			Text output = Output(__instance);
			string intro = (output != null) ? output.text : null;
			if (!string.IsNullOrEmpty(intro))
			{
				Add(intro.TrimEnd('\r', '\n'));
			}
			if (!Enabled)
			{
				ShowBox(__instance, false);
				return;
			}
			Draw(__instance);
		}
	}

	// Switched off: the chat cannot be opened. On: the box is drawn again when a message goes or the chat opens or
	// closes.
	[Feature(Features.GameChat, Features.GameChatInfo)]
	[HarmonyPatch(typeof(ChatBoxDriver), "Update")]
	internal static class UpdatePatch
	{
		private static bool Prefix(ChatBoxDriver __instance)
		{
			if (!__instance.initialized)
			{
				return true;
			}
			if (Enabled)
			{
				return true;
			}
			if (__instance._isActive)
			{
				__instance.isActive = false;
				__instance.chatInputField.DeactivateInputField();
			}
			if (!_shownEmpty)
			{
				ShowBox(__instance, false);
			}
			return false;
		}

		private static void Postfix(ChatBoxDriver __instance)
		{
			if (!__instance.initialized || !Enabled)
			{
				return;
			}
			if (__instance._isActive != _wasActive || Time.unscaledTime >= _nextChange || (_shownEmpty && FadeSeconds <= 0f))
			{
				Draw(__instance);
			}
		}
	}
}
#endif
