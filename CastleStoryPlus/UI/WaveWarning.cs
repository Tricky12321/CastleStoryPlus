using CastleStoryPlus.Core;
using MoonSharp.Interpreter;
using UnityEngine;
using UnityEngine.UI;

namespace CastleStoryPlus.UI;

// Invasion (survival): a big warning before the next wave, at 30 and 15 seconds, and a live countdown for the
// last 5 seconds ending in "THE WAVE IS HERE!". The invasion client script reports the synced wave timer every
// slow update; the countdown runs on from it every frame (game time, so it stops while paused and speeds up
// with the game speed). Not shown when the preset hides the wave timer.
[Feature(Features.WaveWarning, Features.WaveWarningInfo)]
internal class WaveWarning : MonoBehaviour
{
	private const float BannerSeconds = 3f;

	private const float ArrivedSeconds = 2.5f;

	private const float StaleSeconds = 3f;

	private static readonly float[] Warnings = new float[2] { 30f, 15f };

	private const float CountdownFrom = 5f;

	private static readonly Color WarningColor = new Color(1f, 0.75f, 0.2f, 1f);

	private static readonly Color DangerColor = new Color(1f, 0.25f, 0.2f, 1f);

	private static WaveWarning _instance;

	private float _reported = -1f;

	private float _reportedAt;

	private float _reportedUnscaled;

	private float _previous = -1f;

	private GameObject _banner;

	private Text _text;

	private float _bannerUntil;

	private float _bannerStart;

	private static void Enable()
	{
		LuaInjection.AddFunction("WaveTimer", (ScriptExecutionContext context, CallbackArguments args) =>
		{
			DynValue value = args[0];
			Report((value.Type == DataType.Number) ? (float)value.Number : -1f);
			return DynValue.Void;
		});
		LuaInjection.AddPatch(Features.WaveWarning, "Gamemodes/invasion/clientside.lua", "Hooks.Connect(hk_SlowUpdate, clSlowUpdate)\n", LuaInjection.Mode.InsertAfter, @"
---Castle Story Plus: big warnings before the next wave (30 s, 15 s, countdown from 5 s)
Hooks.Connect(hk_SlowUpdate, function()
	if Registry.waveTimer ~= nil and Registry.waveTimer.running and sv_Settings.displayTimeBeforeWave == true then
		CastleStoryPlus.WaveTimer(Registry.waveTimer:RemainingSeconds())
	else
		CastleStoryPlus.WaveTimer(-1)
	end
end)
");
		_instance = Plugin.Root.AddComponent<WaveWarning>();
	}

	private static void Report(float remaining)
	{
		if (_instance == null)
		{
			return;
		}
		_instance._reported = remaining;
		_instance._reportedAt = Time.time;
		_instance._reportedUnscaled = Time.unscaledTime;
	}

	private void Update()
	{
		bool stale = Time.unscaledTime - _reportedUnscaled > StaleSeconds && Time.timeScale > 0f;
		if (_reported < 0f || stale)
		{
			_previous = -1f;
			UpdateBanner(-1f);
			return;
		}
		float remaining = _reported - (Time.time - _reportedAt);
		// A new wave restarts the timer: no crossing between the old and the new value.
		if (_previous >= 0f && remaining <= _previous + 1f)
		{
			foreach (float warning in Warnings)
			{
				if (_previous > warning && remaining <= warning)
				{
					Show("NEXT WAVE IN " + warning + " SECONDS", WarningColor, BannerSeconds);
					if (warning < 30f)
					{
						// At 30 s the game itself plays its warning music.
						MenuSound.PlaySound(MenuSound.Prefix.Other, MenuSound.Suffix.Negative);
					}
				}
			}
			if (_previous > CountdownFrom && remaining <= CountdownFrom)
			{
				MenuSound.PlaySound(MenuSound.Prefix.Other, MenuSound.Suffix.Negative);
			}
			if (_previous > 0f && remaining <= 0f)
			{
				Show("THE WAVE IS HERE!", DangerColor, ArrivedSeconds);
			}
		}
		else if (_previous >= 0f && _previous <= CountdownFrom && remaining > _previous + 1f)
		{
			// The new wave's timer arrived before the countdown reached 0.
			Show("THE WAVE IS HERE!", DangerColor, ArrivedSeconds);
		}
		_previous = remaining;
		UpdateBanner(remaining);
	}

	private void UpdateBanner(float remaining)
	{
		if (remaining > 0f && remaining <= CountdownFrom)
		{
			EnsureBanner();
			_text.text = "NEXT WAVE IN " + Mathf.CeilToInt(remaining);
			_text.color = DangerColor;
			_banner.SetActive(true);
			// A beat on every second.
			float beat = remaining - Mathf.Floor(remaining);
			_banner.transform.localScale = Vector3.one * (1f + 0.15f * beat * beat);
			return;
		}
		if (_banner == null || !_banner.activeSelf)
		{
			return;
		}
		float left = _bannerUntil - Time.unscaledTime;
		if (left <= 0f)
		{
			_banner.SetActive(false);
			return;
		}
		float age = Time.unscaledTime - _bannerStart;
		_banner.transform.localScale = Vector3.one * (1f + 0.2f * Mathf.Max(0f, 1f - age * 4f));
		Color color = _text.color;
		color.a = Mathf.Clamp01(left / 0.5f);
		_text.color = color;
	}

	private void Show(string message, Color color, float seconds)
	{
		EnsureBanner();
		_text.text = message;
		_text.color = color;
		_bannerStart = Time.unscaledTime;
		_bannerUntil = Time.unscaledTime + seconds;
		_banner.SetActive(true);
	}

	private void EnsureBanner()
	{
		if (_banner != null)
		{
			return;
		}
		GameObject canvas = UiKit.CreateCanvas("WaveWarningCanvas", transform, 950);
		RectTransform rect = UiKit.CreateRect("Banner", canvas.transform);
		UiKit.SetRect(rect, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -190f), new Vector2(1200f, 90f));
		_banner = rect.gameObject;
		_text = UiKit.CreateText(rect, string.Empty, 56, WarningColor, TextAnchor.MiddleCenter);
		_text.fontStyle = FontStyle.Bold;
		UiKit.SetRect(_text.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
		Outline outline = _text.gameObject.AddComponent<Outline>();
		outline.effectColor = new Color(0f, 0f, 0f, 0.9f);
		outline.effectDistance = new Vector2(3f, -3f);
		_banner.SetActive(false);
	}
}
