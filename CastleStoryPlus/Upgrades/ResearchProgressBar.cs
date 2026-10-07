using System.Collections.Generic;
using Brix.Game.AI;
using Brix.Game.Components;
using CastleStoryPlus.Core;
using CastleStoryPlus.UI;
using UnityEngine;
using UnityEngine.UI;

namespace CastleStoryPlus.Upgrades;

// A progress bar over the head of the worker researching at a research station: how far the current tier is, shown
// while the worker works on it (a stroke in the last few seconds) and gone when it stops or the tier is done. Drawn
// on a screen overlay at the worker's head, the same size at any distance. The research time is counted by the host
// (ResearchTimePatch), so only the host sees the bar.
[Feature(Features.Upgrades, Features.UpgradesInfo)]
internal class ResearchProgressBar : MonoBehaviour
{
	private static readonly Color BarBackground = new Color(0f, 0f, 0f, 0.7f);

	private const float Width = 64f;

	private const float Height = 8f;

	private const float Border = 1.5f;

	// Above the top of the worker's model.
	private const float AboveHead = 0.35f;

	private RectTransform _canvas;

	private readonly Dictionary<CraftingStation, RectTransform> _bars = new Dictionary<CraftingStation, RectTransform>();

	private readonly Dictionary<Labor, float> _heads = new Dictionary<Labor, float>();

	private readonly List<CraftingStation> _gone = new List<CraftingStation>();

	private static void Enable()
	{
		Plugin.Root.AddComponent<ResearchProgressBar>();
		GameSession.OnLeave(() => ResearchTimePatch.Working.Clear());
	}

	private void LateUpdate()
	{
		Camera camera = Camera.main;
		_gone.Clear();
		foreach (CraftingStation station in ResearchTimePatch.Working)
		{
			float progress = (station != null) ? ResearchTimePatch.Progress(station) : -1f;
			Labor worker = (progress >= 0f) ? station.Operator : null;
			if (worker == null)
			{
				_gone.Add(station);
				continue;
			}
			RectTransform bar = BarFor(station);
			Vector3 head = worker.transform.position + Vector3.up * (HeadHeight(worker) + AboveHead);
			Vector3 screen = (camera != null) ? camera.WorldToScreenPoint(head) : Vector3.back;
			bool visible = screen.z > 0f;
			bar.gameObject.SetActive(visible);
			if (visible)
			{
				bar.position = new Vector3(screen.x, screen.y, 0f);
				RectTransform fill = (RectTransform)bar.GetChild(0).GetChild(0);
				fill.anchorMax = new Vector2(progress, 1f);
				fill.offsetMax = Vector2.zero;
			}
		}
		foreach (CraftingStation station in _gone)
		{
			ResearchTimePatch.Working.Remove(station);
			if (_bars.TryGetValue(station, out RectTransform bar))
			{
				_bars.Remove(station);
				if (bar != null)
				{
					Destroy(bar.gameObject);
				}
			}
		}
		if (_bars.Count == 0)
		{
			_heads.Clear();
		}
	}

	private RectTransform BarFor(CraftingStation station)
	{
		if (_bars.TryGetValue(station, out RectTransform bar) && bar != null)
		{
			return bar;
		}
		if (_canvas == null)
		{
			_canvas = UiKit.CreateCanvas("CastleStoryPlusResearchBars", transform, 0).GetComponent<RectTransform>();
			_canvas.GetComponent<GraphicRaycaster>().enabled = false;
		}
		bar = UiKit.CreateRect("ResearchBar", _canvas);
		bar.sizeDelta = new Vector2(Width, Height);
		Image background = bar.gameObject.AddComponent<Image>();
		background.color = BarBackground;
		background.raycastTarget = false;
		// The fill grows inside a track inset by the border, so the border is even all round.
		RectTransform track = UiKit.CreateRect("Track", bar);
		track.anchorMin = Vector2.zero;
		track.anchorMax = Vector2.one;
		track.offsetMin = new Vector2(Border, Border);
		track.offsetMax = new Vector2(-Border, -Border);
		RectTransform fill = UiKit.CreateRect("Fill", track);
		fill.anchorMin = Vector2.zero;
		fill.anchorMax = new Vector2(0f, 1f);
		fill.pivot = new Vector2(0f, 0.5f);
		fill.offsetMin = Vector2.zero;
		fill.offsetMax = Vector2.zero;
		Image fillImage = fill.gameObject.AddComponent<Image>();
		fillImage.color = UiKit.Yellow;
		fillImage.raycastTarget = false;
		_bars[station] = bar;
		return bar;
	}

	// The top of the worker's model above its feet, measured once (a giant bricktron is taller).
	private float HeadHeight(Labor worker)
	{
		if (_heads.TryGetValue(worker, out float height))
		{
			return height;
		}
		height = 1f;
		bool any = false;
		foreach (Renderer renderer in worker.GetComponentsInChildren<Renderer>())
		{
			if (renderer is ParticleSystemRenderer || !renderer.enabled)
			{
				continue;
			}
			float top = renderer.bounds.max.y - worker.transform.position.y;
			height = any ? Mathf.Max(height, top) : top;
			any = true;
		}
		height = Mathf.Clamp(height, 0.5f, 4f);
		_heads[worker] = height;
		return height;
	}
}
