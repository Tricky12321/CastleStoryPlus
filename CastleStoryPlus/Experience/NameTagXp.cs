using System.Collections;
using Brix.NewUI.OverheadDisplay;
using UnityEngine;
using UnityEngine.UI;

namespace CastleStoryPlus.Experience;

// Added to a bricktron's name tag: two thin XP bars under the name (work on top, combat below) and the
// "LEVEL UP!" effect. Built in code because the name tag prefab has no slot for them.
internal class NameTagXp : MonoBehaviour
{
	private static readonly Color XpBarBackground = new Color(0f, 0f, 0f, 0.6f);

	private static readonly Color WorkXpColor = new Color(0.25f, 0.55f, 0.95f, 1f);

	private static readonly Color CombatXpColor = new Color(0.85f, 0.25f, 0.2f, 1f);

	private static readonly Color LevelUpColor = new Color(1f, 0.85f, 0.3f, 1f);

	private const float XpBarHeight = 3f;

	private NameTag _tag;

	private RectTransform _xpBars;

	private RectTransform _workXpFill;

	private RectTransform _combatXpFill;

	private Coroutine _levelUpRoutine;

	private GameObject _levelUpPopup;

	private Transform _levelUpPulseTarget;

	private Vector3 _levelUpBaseScale;

	private Color _levelUpNameColor;

	public static NameTagXp On(NameTag tag)
	{
		NameTagXp xp = tag.GetComponent<NameTagXp>();
		if (xp == null)
		{
			xp = tag.gameObject.AddComponent<NameTagXp>();
			xp._tag = tag;
		}
		return xp;
	}

	public void SetXpProgress(float work, float combat)
	{
		EnsureXpBars();
		_xpBars.gameObject.SetActive(value: true);
		SetFill(_workXpFill, work);
		SetFill(_combatXpFill, combat);
	}

	public void ResetTag()
	{
		StopLevelUp();
		if (_xpBars != null)
		{
			_xpBars.gameObject.SetActive(value: false);
		}
	}

	public void PlayLevelUp(string text)
	{
		if (!isActiveAndEnabled)
		{
			return;
		}
		StopLevelUp();
		_levelUpRoutine = StartCoroutine(LevelUpRoutine(text));
	}

	// Puts the tag back to normal if an animation is cut short (new level-up, tag returned to the pool).
	private void StopLevelUp()
	{
		if (_levelUpRoutine != null)
		{
			StopCoroutine(_levelUpRoutine);
			_levelUpRoutine = null;
		}
		if (_levelUpPulseTarget != null)
		{
			_levelUpPulseTarget.localScale = _levelUpBaseScale;
			_tag.Name.color = _levelUpNameColor;
			_levelUpPulseTarget = null;
		}
		if (_levelUpPopup != null)
		{
			Destroy(_levelUpPopup);
			_levelUpPopup = null;
		}
	}

	private void OnDisable()
	{
		StopLevelUp();
	}

	private void EnsureXpBars()
	{
		if (_xpBars != null)
		{
			return;
		}
		GameObject root = new GameObject("XpBars", typeof(RectTransform));
		_xpBars = root.GetComponent<RectTransform>();
		_xpBars.SetParent(_tag.Name.transform, worldPositionStays: false);
		root.AddComponent<LayoutElement>().ignoreLayout = true;
		_xpBars.anchorMin = new Vector2(0f, 0f);
		_xpBars.anchorMax = new Vector2(1f, 0f);
		_xpBars.pivot = new Vector2(0.5f, 1f);
		_xpBars.anchoredPosition = new Vector2(0f, -1f);
		_xpBars.sizeDelta = new Vector2(0f, XpBarHeight * 2f + 1f);
		_workXpFill = CreateBar(_xpBars, 0f, WorkXpColor);
		_combatXpFill = CreateBar(_xpBars, -(XpBarHeight + 1f), CombatXpColor);
	}

	private static RectTransform CreateBar(RectTransform parent, float y, Color fillColor)
	{
		GameObject background = new GameObject("XpBar", typeof(RectTransform));
		RectTransform backgroundRect = background.GetComponent<RectTransform>();
		backgroundRect.SetParent(parent, worldPositionStays: false);
		backgroundRect.anchorMin = new Vector2(0f, 1f);
		backgroundRect.anchorMax = new Vector2(1f, 1f);
		backgroundRect.pivot = new Vector2(0.5f, 1f);
		backgroundRect.anchoredPosition = new Vector2(0f, y);
		backgroundRect.sizeDelta = new Vector2(0f, XpBarHeight);
		Image backgroundImage = background.AddComponent<Image>();
		backgroundImage.color = XpBarBackground;
		backgroundImage.raycastTarget = false;
		GameObject fill = new GameObject("Fill", typeof(RectTransform));
		RectTransform fillRect = fill.GetComponent<RectTransform>();
		fillRect.SetParent(backgroundRect, worldPositionStays: false);
		fillRect.anchorMin = Vector2.zero;
		fillRect.anchorMax = new Vector2(0f, 1f);
		fillRect.pivot = new Vector2(0f, 0.5f);
		fillRect.offsetMin = Vector2.zero;
		fillRect.offsetMax = Vector2.zero;
		Image fillImage = fill.AddComponent<Image>();
		fillImage.color = fillColor;
		fillImage.raycastTarget = false;
		return fillRect;
	}

	private static void SetFill(RectTransform fill, float progress)
	{
		fill.anchorMax = new Vector2(Mathf.Clamp01(progress), 1f);
		fill.offsetMax = Vector2.zero;
	}

	// "LEVEL UP!" text rising out of the name tag while the tag pulses and flashes gold.
	private IEnumerator LevelUpRoutine(string text)
	{
		Text name = _tag.Name;
		GameObject popup = new GameObject("LevelUp", typeof(RectTransform));
		_levelUpPopup = popup;
		RectTransform popupRect = popup.GetComponent<RectTransform>();
		popupRect.SetParent(name.transform, worldPositionStays: false);
		popup.AddComponent<LayoutElement>().ignoreLayout = true;
		popupRect.anchorMin = new Vector2(0.5f, 1f);
		popupRect.anchorMax = new Vector2(0.5f, 1f);
		popupRect.pivot = new Vector2(0.5f, 0f);
		popupRect.sizeDelta = new Vector2(200f, 24f);
		Text label = popup.AddComponent<Text>();
		label.font = name.font;
		label.fontSize = name.fontSize + 4;
		label.fontStyle = FontStyle.Bold;
		label.alignment = TextAnchor.MiddleCenter;
		label.horizontalOverflow = HorizontalWrapMode.Overflow;
		label.raycastTarget = false;
		label.text = text;
		Outline outline = popup.AddComponent<Outline>();
		outline.effectColor = new Color(0f, 0f, 0f, 0.8f);
		Color nameColor = name.color;
		Transform pulseTarget = (_tag.content != null) ? _tag.content.transform : _tag.transform;
		Vector3 baseScale = pulseTarget.localScale;
		_levelUpPulseTarget = pulseTarget;
		_levelUpBaseScale = baseScale;
		_levelUpNameColor = nameColor;
		const float duration = 2.2f;
		float elapsed = 0f;
		while (elapsed < duration)
		{
			float t = elapsed / duration;
			popupRect.anchoredPosition = new Vector2(0f, 4f + 36f * t);
			label.color = new Color(LevelUpColor.r, LevelUpColor.g, LevelUpColor.b, (t < 0.7f) ? 1f : (1f - (t - 0.7f) / 0.3f));
			float pulse = (t < 0.25f) ? Mathf.Sin(t / 0.25f * Mathf.PI) : 0f;
			pulseTarget.localScale = baseScale * (1f + 0.35f * pulse);
			name.color = Color.Lerp(nameColor, LevelUpColor, (t < 0.5f) ? (1f - t / 0.5f) : 0f);
			elapsed += Time.unscaledDeltaTime;
			yield return null;
		}
		_levelUpRoutine = null;
		StopLevelUp();
	}
}
