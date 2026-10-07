using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using CastleStoryPlus.Core;
using UnityEngine;
using UnityEngine.UI;
using static CastleStoryPlus.UI.UiKit;

namespace CastleStoryPlus.Diagnostics;

// The performance window, toggled with F7 ([Debug] PerformanceKey): frame rate and frame times of the last 10 s
// (now, average, 1% low, p95, p99, slowest), garbage collections, a graph of the last 300 frame times (lines at 16.7,
// 33.3 and 100 ms, spikes in red) and of the frame rate, what takes the time per frame (FrameMonitor's systems,
// measured from the first time the window opens), the managed heap and what allocates the most (each allocation
// brings the next garbage collection closer, and each collection stops the game) and the last spikes with what took
// their time. The graphs are drawn
// into small textures every frame while the window is open; the numbers refresh four times a second.
[Feature(Features.Performance, Features.PerformanceInfo)]
internal class PerformancePanel : MonoBehaviour
{
	private const int GraphFrames = 300;

	private const int GraphHeight = 120;

	private const int FpsHeight = 60;

	// Frame time at the top of the graph.
	private const float GraphMaxMs = 120f;

	private const float StatsSeconds = 10f;

	private const float RefreshInterval = 0.25f;

	private const int BucketsShown = 14;

	private const int AllocatorsShown = 8;

	private const int SpikesShown = 8;

	private static readonly Color32 GraphBack = new Color32(15, 15, 20, 230);

	private static readonly Color32 Bar = new Color32(110, 200, 120, 255);

	private static readonly Color32 SlowBar = new Color32(240, 190, 70, 255);

	private static readonly Color32 SpikeBar = new Color32(235, 70, 60, 255);

	private static readonly Color32 GcMark = new Color32(120, 160, 255, 255);

	private static readonly Color32 Line = new Color32(255, 255, 255, 70);

	private static readonly Color32 FpsLine = new Color32(120, 200, 255, 255);

	private static ConfigEntry<KeyCode> _key;

	private GameObject _window;

	private Text _stats;

	private Text _buckets;

	private Text _allocators;

	private Text _spikes;

	private Text _measureLabel;

	private Text _fpsScale;

	private Texture2D _frameGraph;

	private Texture2D _fpsGraph;

	private Color32[] _framePixels;

	private Color32[] _fpsPixels;

	private readonly float[] _recent = new float[FrameMonitor.History];

	private readonly float[] _sorted = new float[FrameMonitor.History];

	private float _nextRefresh;

	private int _drawnFrame = -1;

	private static void Enable()
	{
		_key = Plugin.Cfg.Bind("Debug", "PerformanceKey", KeyCode.F7, "Key that opens the performance window (frame rate, frame times, garbage collections, what takes the time, spikes).");
		Plugin.Root.AddComponent<PerformancePanel>();
	}

	private void Awake()
	{
		BuildWindow();
		_window.SetActive(false);
	}

	private void Update()
	{
		if (UnityEngine.Input.GetKeyDown(_key.Value))
		{
			SetVisible(!_window.activeSelf);
		}
		if (!_window.activeSelf)
		{
			return;
		}
		if (UnityEngine.Input.GetKeyDown(KeyCode.Escape))
		{
			SetVisible(false);
			return;
		}
		DrawGraphs();
		if (Time.unscaledTime >= _nextRefresh)
		{
			Refresh();
		}
	}

	private void SetVisible(bool visible)
	{
		_window.SetActive(visible);
		if (visible)
		{
			// Systems are measured from the first time anyone looks.
			FrameMonitor.StartDetailed();
			Refresh();
		}
	}

	private void BuildWindow()
	{
		GameObject canvas = CreateCanvas("PerformanceCanvas", transform, 951);
		_window = canvas;
		GameObject window = CreatePanel("PerformanceWindow", canvas.transform);
		RectTransform rect = window.GetComponent<RectTransform>();
		rect.pivot = new Vector2(1f, 1f);
		SetRect(rect, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-16f, -16f), new Vector2(640f, 1140f));
		VerticalLayoutGroup layout = window.AddComponent<VerticalLayoutGroup>();
		layout.padding = new RectOffset(14, 14, 10, 14);
		layout.spacing = 6f;
		layout.childControlWidth = true;
		layout.childControlHeight = true;
		layout.childForceExpandWidth = true;
		layout.childForceExpandHeight = false;

		Transform header = CreateRow(window.transform, 28f);
		Flexible(CreateText(header, "Performance", 18, Yellow, TextAnchor.MiddleLeft).gameObject);
		Button measure = CreateButton(header, string.Empty, 150f, () =>
		{
			FrameMonitor.StartDetailed();
			Refresh();
		});
		_measureLabel = measure.GetComponentInChildren<Text>();
		CreateButton(header, "Reset", 70f, () =>
		{
			FrameMonitor.Reset();
			Refresh();
		});
		CreateButton(header, "Close", 70f, () => SetVisible(false));

		_stats = Block(window.transform, 146f, 13);

		CreateText(Row(window.transform, 18f), "Frame time (ms), last " + GraphFrames + " frames: lines at 16.7 / 33.3 / 100 ms; yellow over 33 ms, red a spike, blue mark a garbage collection", 11, Grey, TextAnchor.MiddleLeft);
		_frameGraph = Graph(window.transform, GraphHeight, out _framePixels);

		Transform fpsRow = Row(window.transform, 18f);
		Flexible(CreateText(fpsRow, "Frames per second", 11, Grey, TextAnchor.MiddleLeft).gameObject);
		_fpsScale = CreateText(fpsRow, string.Empty, 11, Grey, TextAnchor.MiddleRight);
		_fpsGraph = Graph(window.transform, FpsHeight, out _fpsPixels);

		CreateText(Row(window.transform, 18f), "What takes the time (average per frame since measuring or the last reset)", 12, Yellow, TextAnchor.MiddleLeft);
		_buckets = Block(window.transform, 236f, 12);

		CreateText(Row(window.transform, 18f), "What allocates the most (average per frame since measuring or the last reset)", 12, Yellow, TextAnchor.MiddleLeft);
		_allocators = Block(window.transform, 140f, 12);

		CreateText(Row(window.transform, 18f), "Last spikes", 12, Yellow, TextAnchor.MiddleLeft);
		_spikes = Block(window.transform, 210f, 12);
	}

	private static Transform Row(Transform parent, float height)
	{
		return CreateRow(parent, height);
	}

	private static Text Block(Transform parent, float height, int size)
	{
		GameObject box = CreatePanel("Block", parent);
		box.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.25f);
		LayoutElement element = box.AddComponent<LayoutElement>();
		element.minHeight = height;
		element.preferredHeight = height;
		Text text = CreateText(box.transform, string.Empty, size, TextColor, TextAnchor.UpperLeft);
		text.horizontalOverflow = HorizontalWrapMode.Overflow;
		text.verticalOverflow = VerticalWrapMode.Truncate;
		SetRect(text.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, new Vector2(-12f, -8f));
		return text;
	}

	private static Texture2D Graph(Transform parent, int height, out Color32[] pixels)
	{
		Texture2D texture = new Texture2D(GraphFrames, height, TextureFormat.RGBA32, false)
		{
			filterMode = FilterMode.Point,
			wrapMode = TextureWrapMode.Clamp
		};
		pixels = new Color32[GraphFrames * height];
		RectTransform rect = CreateRect("Graph", parent);
		RawImage image = rect.gameObject.AddComponent<RawImage>();
		image.texture = texture;
		image.raycastTarget = false;
		LayoutElement element = rect.gameObject.AddComponent<LayoutElement>();
		element.minHeight = height;
		element.preferredHeight = height;
		return texture;
	}

	private void DrawGraphs()
	{
		if (_drawnFrame == FrameMonitor.Recorded)
		{
			return;
		}
		_drawnFrame = FrameMonitor.Recorded;
		int frames = Mathf.Min(FrameMonitor.Recorded, GraphFrames);
		for (int i = 0; i < _framePixels.Length; i++)
		{
			_framePixels[i] = GraphBack;
		}
		for (int i = 0; i < _fpsPixels.Length; i++)
		{
			_fpsPixels[i] = GraphBack;
		}
		float fpsTop = 60f;
		for (int i = 0; i < frames; i++)
		{
			int slot = (FrameMonitor.Recorded - frames + i) % FrameMonitor.History;
			float ms = FrameMonitor.FrameMs[slot];
			if (ms > 0f)
			{
				fpsTop = Mathf.Max(fpsTop, 1000f / ms);
			}
		}
		fpsTop = Mathf.Ceil(fpsTop / 30f) * 30f;
		int x0 = GraphFrames - frames;
		for (int i = 0; i < frames; i++)
		{
			int slot = (FrameMonitor.Recorded - frames + i) % FrameMonitor.History;
			float ms = FrameMonitor.FrameMs[slot];
			int x = x0 + i;
			Color32 color = FrameMonitor.FrameSpike[slot] ? SpikeBar : ((ms > 33.4f) ? SlowBar : Bar);
			int top = Mathf.Clamp(Mathf.RoundToInt(ms / GraphMaxMs * GraphHeight), 1, GraphHeight);
			for (int y = 0; y < top; y++)
			{
				_framePixels[y * GraphFrames + x] = color;
			}
			if (FrameMonitor.FrameGcs[slot] > 0)
			{
				for (int y = GraphHeight - 4; y < GraphHeight; y++)
				{
					_framePixels[y * GraphFrames + x] = GcMark;
				}
			}
			float fps = (ms > 0f) ? (1000f / ms) : 0f;
			int fy = Mathf.Clamp(Mathf.RoundToInt(fps / fpsTop * (FpsHeight - 1)), 0, FpsHeight - 1);
			_fpsPixels[fy * GraphFrames + x] = FpsLine;
			if (fy > 0)
			{
				_fpsPixels[(fy - 1) * GraphFrames + x] = FpsLine;
			}
		}
		HorizontalLine(_framePixels, GraphHeight, 16.7f / GraphMaxMs);
		HorizontalLine(_framePixels, GraphHeight, 33.3f / GraphMaxMs);
		HorizontalLine(_framePixels, GraphHeight, 100f / GraphMaxMs);
		HorizontalLine(_fpsPixels, FpsHeight, 30f / fpsTop);
		HorizontalLine(_fpsPixels, FpsHeight, 60f / fpsTop);
		_frameGraph.SetPixels32(_framePixels);
		_frameGraph.Apply(false);
		_fpsGraph.SetPixels32(_fpsPixels);
		_fpsGraph.Apply(false);
		_fpsScale.text = "top " + fpsTop.ToString("0") + " fps, lines at 30 and 60";
	}

	private static void HorizontalLine(Color32[] pixels, int height, float share)
	{
		int y = Mathf.RoundToInt(share * height);
		if (y < 0 || y >= height)
		{
			return;
		}
		for (int x = 0; x < GraphFrames; x++)
		{
			Color32 under = pixels[y * GraphFrames + x];
			pixels[y * GraphFrames + x] = new Color32((byte)((under.r + Line.r) / 2), (byte)((under.g + Line.g) / 2), (byte)((under.b + Line.b) / 2), 255);
		}
	}

	private void Refresh()
	{
		_nextRefresh = Time.unscaledTime + RefreshInterval;
		_measureLabel.text = FrameMonitor.Detailed ? ("Measuring " + FrameMonitor.PatchedMethods) : "Measure systems";
		ShowStats();
		ShowBuckets();
		ShowAllocators();
		ShowSpikes();
	}

	private void ShowStats()
	{
		int count = FrameMonitor.Recent(StatsSeconds, _recent, out int gcs, out int spikes);
		if (count == 0)
		{
			_stats.text = "No frames measured yet.";
			return;
		}
		float last = _recent[count - 1];
		double sum = 0.0;
		for (int i = 0; i < count; i++)
		{
			sum += _recent[i];
			_sorted[i] = _recent[i];
		}
		Array.Sort(_sorted, 0, count);
		float avg = (float)(sum / count);
		float p95 = _sorted[Mathf.Min(count - 1, count * 95 / 100)];
		float p99 = _sorted[Mathf.Min(count - 1, count * 99 / 100)];
		float max = _sorted[count - 1];
		// 1% low: the frame rate of the slowest 1% of the frames.
		int slowest = Mathf.Max(1, count / 100);
		double slowSum = 0.0;
		for (int i = count - slowest; i < count; i++)
		{
			slowSum += _sorted[i];
		}
		float low = (float)(1000.0 / (slowSum / slowest));
		float seconds = Mathf.Max(0.01f, (float)(sum / 1000.0));
		StringBuilder text = new StringBuilder();
		text.Append("FPS  now ").Append(Fps(last)).Append("   avg ").Append(Fps(avg)).Append("   1% low ").Append(low.ToString("0.0")).Append("   (last ").Append(StatsSeconds.ToString("0")).Append(" s, ").Append(count).Append(" frames)\n");
		text.Append("Frame ms  now ").Append(last.ToString("0.0")).Append("   avg ").Append(avg.ToString("0.0")).Append("   p95 ").Append(p95.ToString("0.0")).Append("   p99 ").Append(p99.ToString("0.0")).Append("   max ").Append(max.ToString("0.0")).Append('\n');
		text.Append("Garbage collections  ").Append(gcs).Append(" in ").Append(seconds.ToString("0")).Append(" s (").Append((gcs * 60f / seconds).ToString("0.0")).Append(" per minute), ").Append(FrameMonitor.GcTotal).Append(" since reset\n");
		text.Append("Spikes (over ").Append(FrameMonitor.SpikeMs.Value.ToString("0")).Append(" ms)  ").Append(spikes).Append(" in ").Append(seconds.ToString("0")).Append(" s, ").Append(FrameMonitor.SpikeCount).Append(" since reset\n");
		if (FrameMonitor.Detailed && FrameMonitor.MeasuredFrames > 0)
		{
			double frameAvg = FrameMonitor.MeasuredFrameMs / FrameMonitor.MeasuredFrames;
			double unmeasured = FrameMonitor.UnmeasuredTotalMs / FrameMonitor.MeasuredFrames;
			text.Append("Measured since reset  ").Append(FrameMonitor.MeasuredFrames).Append(" frames, ").Append(frameAvg.ToString("0.0")).Append(" ms avg, of which unmeasured (rendering, Unity, coroutines, Lua) ").Append(unmeasured.ToString("0.0")).Append(" ms\n");
		}
		else
		{
			text.Append("Systems are not measured yet.\n");
		}
		float kbPerFrame = FrameMonitor.RecentKbPerFrame(StatsSeconds, out float peakKb);
		text.Append("Heap  ").Append((FrameMonitor.HeapUsed / 1048576f).ToString("0")).Append(" MB used");
		if (FrameMonitor.HeapReserved > 0)
		{
			text.Append(" of ").Append((FrameMonitor.HeapReserved / 1048576f).ToString("0")).Append(" MB");
		}
		text.Append("   allocating ").Append(kbPerFrame.ToString("0")).Append(" KB/frame (peak ").Append(peakKb.ToString("0")).Append("), ").Append(FrameMonitor.AllocMbPerSecond.ToString("0.0")).Append(" MB/s since reset\n");
		if (FrameMonitor.GcFrames > 0)
		{
			text.Append("Last collection  ").Append((FrameMonitor.UsedBeforeLastGc / 1048576f).ToString("0")).Append(" -> ").Append((FrameMonitor.UsedAfterLastGc / 1048576f).ToString("0")).Append(" MB, frame ").Append(FrameMonitor.LastGcFrameMs.ToString("0")).Append(" ms (avg ").Append(FrameMonitor.GcFrameMsAverage.ToString("0")).Append(" ms over ").Append(FrameMonitor.GcFrames).Append(")\n");
		}
		text.Append("Missing-script warnings  ").Append(FrameMonitor.MissingScriptTotal).Append(" (where they come from is in the log)");
		_stats.text = text.ToString();
	}

	private static string Fps(float ms)
	{
		return (ms > 0f) ? (1000f / ms).ToString("0.0") : "-";
	}

	private void ShowBuckets()
	{
		if (!FrameMonitor.Detailed || FrameMonitor.MeasuredFrames == 0)
		{
			_buckets.text = "Measuring starts when this window opens (or with [Performance] MeasureAtStart).";
			return;
		}
		StringBuilder text = new StringBuilder();
		text.Append("  ms/frame   max ms   calls/frame   system\n");
		foreach (FrameMonitor.Bucket bucket in FrameMonitor.Ranked(BucketsShown))
		{
			double avg = FrameMonitor.Milliseconds(bucket.TotalTicks) / FrameMonitor.MeasuredFrames;
			text.Append(Pad(avg.ToString("0.00"), 10)).Append(Pad(FrameMonitor.Milliseconds(bucket.MaxTicks).ToString("0.0"), 9)).Append(Pad(((double)bucket.TotalCalls / FrameMonitor.MeasuredFrames).ToString("0.0"), 14)).Append("   ").Append(bucket.Name).Append('\n');
		}
		_buckets.text = text.ToString();
	}

	private void ShowAllocators()
	{
		if (!FrameMonitor.Detailed || FrameMonitor.MeasuredFrames == 0 || !FrameMonitor.Allocations)
		{
			_allocators.text = FrameMonitor.Allocations ? "Measuring starts when this window opens." : "Off ([Performance] MeasureAllocations).";
			return;
		}
		StringBuilder text = new StringBuilder();
		text.Append("  KB/frame   KB/call   system\n");
		foreach (FrameMonitor.Bucket bucket in FrameMonitor.RankedByBytes(AllocatorsShown))
		{
			double perFrame = bucket.TotalBytes / 1024.0 / FrameMonitor.MeasuredFrames;
			double perCall = (bucket.TotalCalls > 0) ? (bucket.TotalBytes / 1024.0 / bucket.TotalCalls) : 0.0;
			text.Append(Pad(perFrame.ToString("0.0"), 10)).Append(Pad(perCall.ToString("0.00"), 10)).Append("   ").Append(bucket.Name).Append('\n');
		}
		_allocators.text = text.ToString();
	}

	private void ShowSpikes()
	{
		List<FrameMonitor.Spike> spikes = FrameMonitor.Spikes;
		if (spikes.Count == 0)
		{
			_spikes.text = "No spikes since the last reset.";
			return;
		}
		StringBuilder text = new StringBuilder();
		for (int i = spikes.Count - 1; i >= 0 && i >= spikes.Count - SpikesShown; i--)
		{
			FrameMonitor.Spike spike = spikes[i];
			text.Append(spike.Clock.ToString("HH:mm:ss")).Append("  ").Append(spike.Milliseconds.ToString("0")).Append(" ms");
			if (spike.Gcs > 0)
			{
				text.Append(", GC x").Append(spike.Gcs);
			}
			if (spike.MissingScripts > 0)
			{
				text.Append(", missing scripts x").Append(spike.MissingScripts);
			}
			for (int j = 0; j < spike.Names.Length && j < 3; j++)
			{
				text.Append(", ").Append(spike.Names[j]).Append(' ').Append(spike.BucketMs[j].ToString("0")).Append(" ms");
			}
			if (spike.Names.Length > 0)
			{
				text.Append(", unmeasured ").Append(spike.UnmeasuredMs.ToString("0")).Append(" ms");
			}
			text.Append('\n');
		}
		_spikes.text = text.ToString();
	}

	private static string Pad(string value, int width)
	{
		return (value.Length >= width) ? value : (new string(' ', width - value.Length) + value);
	}
}
