using System.Collections.Generic;
using Brix.Game;
using Brix.Game.Components;
using CastleStoryPlus.Core;
using UnityEngine;
using UnityEngine.UI;

namespace CastleStoryPlus.UI;

// When the firefly of a killed unit (an enemy, or one of your own workers) reaches your home crystal, a hint
// rises above the crystal with the energy it brings: "+40 energy" / "enemy" or "<name> returns". Fireflies of
// killed units are the ones a corpse sends off (isOldUnit); brewed fireflies are not shown. Runs on every peer
// from synced firefly data.
[Feature(Features.EnergyHint, Features.EnergyHintInfo)]
internal class EnergyHint : MonoBehaviour
{
	private class Popup
	{
		public GameObject Root;

		public Text Amount;

		public Text Source;

		public Vector3 Start;

		public float Age;
	}

	private const float ScanSeconds = 0.2f;

	private const float Lifetime = 3f;

	private const float Rise = 3f;

	private static readonly Color EnemyColor = new Color(0.45f, 0.9f, 0.4f, 1f);

	private static readonly Color OwnColor = new Color(1f, 0.8f, 0.25f, 1f);

	private readonly HashSet<Firefly> _inNest = new HashSet<Firefly>();

	// Fireflies seen flying outside a crystal; only those are shown when they arrive (not the ones already in the crystal when a map loads).
	private readonly HashSet<Firefly> _outside = new HashSet<Firefly>();

	private readonly List<Firefly> _left = new List<Firefly>();

	private readonly List<Popup> _popups = new List<Popup>();

	private readonly Dictionary<FireflyNest, int> _stack = new Dictionary<FireflyNest, int>();

	private float _nextScan;

	private static void Enable()
	{
		Plugin.Root.AddComponent<EnergyHint>();
	}

	private void Update()
	{
		if (Time.unscaledTime >= _nextScan)
		{
			_nextScan = Time.unscaledTime + ScanSeconds;
			Scan();
		}
		Animate();
	}

	private void Scan()
	{
		Faction mine = (User.LocalUser != null) ? User.LocalUser.faction : null;
		_left.Clear();
		foreach (Firefly firefly in _inNest)
		{
			if (firefly == null || NestOf(firefly) == null)
			{
				_left.Add(firefly);
			}
		}
		foreach (Firefly firefly in _left)
		{
			_inNest.Remove(firefly);
		}
		_outside.RemoveWhere((Firefly firefly) => firefly == null);
		if (mine == null)
		{
			return;
		}
		_stack.Clear();
		foreach (Firefly firefly in Firefly._allFireflies)
		{
			if (firefly == null || !firefly.isOldUnit || _inNest.Contains(firefly))
			{
				continue;
			}
			FireflyNest nest = NestOf(firefly);
			if (nest == null)
			{
				_outside.Add(firefly);
				continue;
			}
			_inNest.Add(firefly);
			if (!_outside.Remove(firefly) || nest.faction != mine)
			{
				continue;
			}
			int energy = firefly.pureEnergy + firefly.storedEnergy;
			if (energy <= 0)
			{
				continue;
			}
			bool enemy = firefly.faction != nest.faction;
			string source = enemy ? "enemy killed" : (firefly.IsNamed ? (firefly.laborName + " returns") : "unit returns");
			_stack.TryGetValue(nest, out int index);
			_stack[nest] = index + 1;
			Show(nest, "+" + energy + " energy", source, enemy ? EnemyColor : OwnColor, index);
		}
	}

	// The home crystal that holds this firefly, or null.
	private static FireflyNest NestOf(Firefly firefly)
	{
		Transform parent = firefly.transform.parent;
		if (parent == null)
		{
			return null;
		}
		FireflyNest nest = parent.GetComponentInParent<FireflyNest>();
		return (nest != null && nest.isHome) ? nest : null;
	}

	private void Show(FireflyNest nest, string amount, string source, Color color, int index)
	{
		Vector3 top = nest.transform.position + Vector3.up * 4f;
		foreach (Renderer renderer in nest.GetComponentsInChildren<Renderer>())
		{
			top.y = Mathf.Max(top.y, renderer.bounds.max.y + 1f);
		}
		// Several fireflies arriving together are stacked instead of drawn on top of each other.
		top.y += index * 1.2f;

		GameObject root = new GameObject("EnergyHint", typeof(RectTransform));
		root.transform.SetParent(transform, worldPositionStays: false);
		root.transform.position = top;
		root.transform.localScale = Vector3.one * 0.01f;
		Canvas canvas = root.AddComponent<Canvas>();
		canvas.renderMode = RenderMode.WorldSpace;
		root.GetComponent<RectTransform>().sizeDelta = new Vector2(400f, 110f);
		Text amountText = CreateText(root.transform, amount, 48, color, new Vector2(0f, 20f));
		amountText.fontStyle = FontStyle.Bold;
		Text sourceText = CreateText(root.transform, source, 26, Color.white, new Vector2(0f, -28f));
		_popups.Add(new Popup
		{
			Root = root,
			Amount = amountText,
			Source = sourceText,
			Start = top
		});
	}

	private static Text CreateText(Transform parent, string text, int size, Color color, Vector2 position)
	{
		Text label = UiKit.CreateText(parent, text, size, color, TextAnchor.MiddleCenter);
		UiKit.SetRect(label.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), position, new Vector2(400f, 56f));
		Outline outline = label.gameObject.AddComponent<Outline>();
		outline.effectColor = new Color(0f, 0f, 0f, 0.9f);
		outline.effectDistance = new Vector2(2f, -2f);
		return label;
	}

	private void Animate()
	{
		Camera camera = Camera.main;
		for (int i = _popups.Count - 1; i >= 0; i--)
		{
			Popup popup = _popups[i];
			popup.Age += Time.unscaledDeltaTime;
			if (popup.Age >= Lifetime || popup.Root == null)
			{
				if (popup.Root != null)
				{
					Destroy(popup.Root);
				}
				_popups.RemoveAt(i);
				continue;
			}
			float t = popup.Age / Lifetime;
			popup.Root.transform.position = popup.Start + Vector3.up * (Rise * t);
			if (camera != null)
			{
				popup.Root.transform.rotation = camera.transform.rotation;
				// Constant size on screen, whatever the zoom.
				float distance = Vector3.Distance(camera.transform.position, popup.Root.transform.position);
				popup.Root.transform.localScale = Vector3.one * (0.0008f * distance);
			}
			float alpha = (t < 0.7f) ? 1f : (1f - (t - 0.7f) / 0.3f);
			SetAlpha(popup.Amount, alpha);
			SetAlpha(popup.Source, alpha);
		}
	}

	private static void SetAlpha(Text text, float alpha)
	{
		Color color = text.color;
		color.a = alpha;
		text.color = color;
		Outline outline = text.GetComponent<Outline>();
		if (outline != null)
		{
			Color effect = outline.effectColor;
			effect.a = 0.9f * alpha;
			outline.effectColor = effect;
		}
	}
}
