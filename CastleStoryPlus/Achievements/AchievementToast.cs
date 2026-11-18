using System.Collections.Generic;
using CastleStoryPlus.Core;
using CastleStoryPlus.GameUi;
using CastleStoryPlus.UI;
using UnityEngine;
using UnityEngine.UI;

namespace CastleStoryPlus.Achievements;

// The short message when an achievement is unlocked: a dark plate at the top of the screen with its picture, name and
// text, and a chime made in code (three rising notes, played by ModSound at the game's interface volume, so it is
// heard without a second listener). One at a time; the ones that come together wait their turn.
[Feature(Features.Achievements, Features.AchievementsInfo)]
internal sealed class AchievementToast : MonoBehaviour
{
	private const float FadeIn = 0.25f;

	private const float Hold = 4f;

	private const float FadeOut = 0.6f;

	// From the top of the screen (the weather's message sits lower, the frame rate readout higher).
	private const float TopOffset = -44f;

	private const int SampleRate = 44100;

	private const float ChimeVolume = 0.6f;

	private static readonly Color Gold = new Color(1f, 0.8f, 0.25f, 1f);

	private static AchievementToast _instance;

	private static AudioClip _chime;

	private readonly Queue<AchievementInfo> _waiting = new Queue<AchievementInfo>();

	private GameObject _plate;

	private CanvasGroup _group;

	private RectTransform _rect;

	private Image _icon;

	private Text _name;

	private Text _text;

	private float _start;

	private bool _showing;

	// The achievement on show now, or null (for the tests).
	internal static AchievementInfo Current { get; private set; }

	internal static int Shown { get; private set; }

	internal static bool IsShowing => _instance != null && _instance._showing;

	private static void Enable()
	{
		_instance = Plugin.Root.AddComponent<AchievementToast>();
		AchievementTracker.Unlock += (AchievementInfo achievement) =>
		{
			if (_instance != null && _instance.isActiveAndEnabled)
			{
				_instance._waiting.Enqueue(achievement);
			}
		};
	}

	private void Update()
	{
		if (!_showing)
		{
			if (_waiting.Count > 0)
			{
				Begin(_waiting.Dequeue());
			}
			return;
		}
		float age = Time.unscaledTime - _start;
		if (age >= FadeIn + Hold + FadeOut)
		{
			_plate.SetActive(false);
			_showing = false;
			Current = null;
			return;
		}
		float alpha = Mathf.Clamp01(age / FadeIn) * Mathf.Clamp01((FadeIn + Hold + FadeOut - age) / FadeOut);
		_group.alpha = alpha;
		// Slides down into place while it fades in.
		_rect.anchoredPosition = new Vector2(0f, TopOffset + 24f * (1f - Mathf.Clamp01(age / FadeIn)));
	}

	// For the tests: no message on show or waiting.
	internal static void ResetForTest()
	{
		if (_instance != null)
		{
			_instance._waiting.Clear();
			_instance._showing = false;
			Current = null;
			if (_instance._plate != null)
			{
				_instance._plate.SetActive(false);
			}
		}
	}

	private void OnDisable()
	{
		_waiting.Clear();
		_showing = false;
		Current = null;
		if (_plate != null)
		{
			_plate.SetActive(false);
		}
	}

	private void Begin(AchievementInfo achievement)
	{
		if (_plate == null)
		{
			Build();
		}
		Sprite sprite = GameUiState.Icon(achievement.Icon);
		_icon.sprite = sprite;
		_icon.enabled = sprite != null;
		_name.text = achievement.Name;
		_text.text = achievement.Text;
		_start = Time.unscaledTime;
		_group.alpha = 0f;
		_plate.SetActive(true);
		_showing = true;
		Current = achievement;
		Shown++;
		ModSound.PlayOnce(Chime(), ChimeVolume * ModSound.UiVolume);
	}

	private void Build()
	{
		GameObject canvas = UiKit.CreateCanvas("AchievementToastCanvas", transform, 945);
		canvas.GetComponent<GraphicRaycaster>().enabled = false;
		_rect = UiKit.CreateRect("Toast", canvas.transform);
		UiKit.SetRect(_rect, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, TopOffset), new Vector2(480f, 84f));
		_rect.pivot = new Vector2(0.5f, 1f);
		_plate = _rect.gameObject;
		_group = _plate.AddComponent<CanvasGroup>();
		_group.blocksRaycasts = false;
		_group.interactable = false;
		Image background = _plate.AddComponent<Image>();
		background.color = new Color(0.07f, 0.07f, 0.09f, 0.94f);
		background.raycastTarget = false;
		Outline frame = _plate.AddComponent<Outline>();
		frame.effectColor = Gold;
		frame.effectDistance = new Vector2(2f, -2f);
		RectTransform slot = UiKit.CreateRect("Icon", _rect);
		UiKit.SetRect(slot, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(46f, 0f), new Vector2(60f, 60f));
		_icon = slot.gameObject.AddComponent<Image>();
		_icon.preserveAspect = true;
		_icon.raycastTarget = false;
		RectTransform texts = UiKit.CreateRect("Texts", _rect);
		UiKit.SetRect(texts, Vector2.zero, Vector2.one, new Vector2(40f, 0f), new Vector2(-92f, -12f));
		VerticalLayoutGroup layout = texts.gameObject.AddComponent<VerticalLayoutGroup>();
		layout.childAlignment = TextAnchor.MiddleLeft;
		layout.childControlWidth = true;
		layout.childControlHeight = true;
		layout.childForceExpandWidth = true;
		layout.childForceExpandHeight = false;
		Text caption = UiKit.CreateText(texts, "ACHIEVEMENT UNLOCKED", 12, Gold, TextAnchor.MiddleLeft);
		caption.fontStyle = FontStyle.Bold;
		_name = UiKit.CreateText(texts, string.Empty, 22, Color.white, TextAnchor.MiddleLeft);
		_name.fontStyle = FontStyle.Bold;
		_text = UiKit.CreateText(texts, string.Empty, 14, UiKit.TextColor, TextAnchor.MiddleLeft);
		_plate.SetActive(false);
	}

	// Three rising notes (G, C, E) a tenth of a second apart, each a sine with a touch of its octave that rings out.
	internal static AudioClip Chime()
	{
		if (_chime != null)
		{
			return _chime;
		}
		float[] notes = { 784f, 1046.5f, 1318.5f };
		int length = SampleRate * 12 / 10;
		float[] data = new float[length];
		for (int n = 0; n < notes.Length; n++)
		{
			int start = n * SampleRate / 10;
			for (int i = start; i < length; i++)
			{
				float t = (i - start) / (float)SampleRate;
				float envelope = Mathf.Exp(-t * 5f) * Mathf.Clamp01(t * 400f);
				float tone = Mathf.Sin(2f * Mathf.PI * notes[n] * t) + 0.25f * Mathf.Sin(4f * Mathf.PI * notes[n] * t);
				data[i] += tone * envelope * 0.3f;
			}
		}
		_chime = AudioClip.Create("CSP Achievement", length, 1, SampleRate, false);
		_chime.SetData(data, 0);
		return _chime;
	}
}
