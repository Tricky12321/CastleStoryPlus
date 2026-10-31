using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Brix.Engine;
using Brix.External.Factories;
using CastleStoryPlus.DevTools.Endpoints;
using Newtonsoft.Json;
using UnityEngine;

namespace CastleStoryPlus.DevTools.Frames;

// Runs the benchmark on the game that is loaded (meant: the "CSP Benchmark" save, BenchmarkWorld): normal speed,
// a warm-up, then a few fixed camera views on its platform, each measured for the same time: frame rate,
// 1% low, frame time percentiles, garbage collections and (Unity 6) Unity's own player loop steps. The report is
// written to BepInEx/devtools/benchmarks/benchmark-<date>.json and kept for GET /api/benchmark.
// With CSP_BENCHMARK set the run starts by itself once a game is loaded; CSP_BENCHMARK=quit closes the game after.
internal class BenchmarkRun : MonoBehaviour
{
	private const string Variable = "CSP_BENCHMARK";

	private sealed class View
	{
		internal string Name;

		internal Vector3 Offset;

		internal float Distance;

		internal float Pitch;

		internal float Yaw;
	}

	// Looking at the middle of the benchmark's stockpile field (BenchmarkWorld) plus Offset: all of the platform, the
	// stockpiles, the workshop row and the keep the builders work on.
	private static readonly View[] Views =
	{
		new View { Name = "overview", Offset = new Vector3(37f, 0f, 1f), Distance = 110f, Pitch = 55f, Yaw = 45f },
		new View { Name = "stockpiles", Offset = Vector3.zero, Distance = 25f, Pitch = 45f, Yaw = 200f },
		new View { Name = "workshops", Offset = new Vector3(12f, 0f, -32f), Distance = 25f, Pitch = 40f, Yaw = 20f },
		new View { Name = "keep", Offset = new Vector3(73f, 0f, 0f), Distance = 35f, Pitch = 45f, Yaw = 300f }
	};

	internal static BenchmarkRun Instance;

	internal float WarmupSeconds = 20f;

	internal float ViewSeconds = 15f;

	private bool _running;

	private string _state = "idle";

	private object _report;

	private string _file;

	private readonly List<float> _frames = new List<float>(8192);

	private bool _recording;

	private bool _autoStarted;

	private void Awake()
	{
		Instance = this;
	}

	internal object Status()
	{
		return Json.Obj("state", _state, "running", _running, "file", _file, "report", _report);
	}

	internal void Begin(float warmup, float perView)
	{
		if (_running)
		{
			throw new Api.ApiException(409, "A benchmark is running (GET /api/benchmark)");
		}
		WarmupSeconds = warmup;
		ViewSeconds = perView;
		_running = true;
		StartCoroutine(Run(false));
	}

	private void Update()
	{
		if (_recording)
		{
			_frames.Add(Time.unscaledDeltaTime * 1000f);
		}
		string wanted = System.Environment.GetEnvironmentVariable(Variable);
		if (!_autoStarted && !string.IsNullOrEmpty(wanted) && InGame())
		{
			_autoStarted = true;
			_running = true;
			StartCoroutine(Run(wanted.Equals("quit", StringComparison.OrdinalIgnoreCase)));
		}
	}

	private static bool InGame()
	{
		return GameParam.Map != null && Brix.Engine.Architecte.commence && !Brix.Engine.Architecte.termine;
	}

	private IEnumerator Run(bool quit)
	{
		try
		{
			_report = null;
			_state = "warming up";
			yield return new WaitForSecondsRealtime(5f);
			Call("POST", "/api/pause", "paused", "false");
			Call("POST", "/api/speed", "speed", "1");
			CastleStoryPlus.Saving.AutoSave.ResetTimer();
			yield return new WaitForSecondsRealtime(WarmupSeconds);
			// After the warm-up: right after the load not all of the save's buildings are in yet.
			Vector3 centre = Centre();
			DevToolsPlugin.Log.LogInfo("Benchmark: looking at " + centre);
			// The player's camera holds still (edge scrolling with the mouse, zoom and turning would move it off the view).
			HoldCamera(true);
			List<object> results = new List<object>();
			List<float> all = new List<float>();
			int gcStart = GC.CollectionCount(0);
			foreach (View view in Views)
			{
				_state = "view " + view.Name;
				Vector3 at = centre + view.Offset;
				Call("POST", "/api/camera", "at", F(at.x) + "," + F(at.y) + "," + F(at.z), "distance", F(view.Distance), "pitch", F(view.Pitch), "yaw", F(view.Yaw));
				yield return new WaitForSecondsRealtime(2f);
#if UNITY6
				PlayerLoopTiming.Start();
				PlayerLoopTiming.Reset();
#endif
				int gc = GC.CollectionCount(0);
				_frames.Clear();
				_recording = true;
				yield return new WaitForSecondsRealtime(ViewSeconds);
				_recording = false;
				all.AddRange(_frames);
				results.Add(Json.Obj(
					"view", view.Name,
					"frames", Stats(_frames),
					"gcs", GC.CollectionCount(0) - gc,
					"loop", LoopSteps()));
			}
			CastleStoryPlus.Saving.AutoSave.ResetTimer();
			_report = Json.Obj(
				"time", DateTime.Now.ToString("dd-MM-yyyy HH:mm:ss"),
				"save", GameParam.Map != null ? GameParam.Map.customName : null,
				"unity", Application.unityVersion,
				"mod", CastleStoryPlus.Plugin.Version,
				"screen", Screen.width + "x" + Screen.height,
				"quality", QualitySettings.names[QualitySettings.GetQualityLevel()],
				"targetFrameRate", Application.targetFrameRate,
				"vSync", QualitySettings.vSyncCount,
				"centre", new[] { centre.x, centre.y, centre.z },
				"units", UnityEngine.Object.FindObjectsOfType<Brix.Game.AI.Labor>().Length,
				"warmupSeconds", WarmupSeconds,
				"secondsPerView", ViewSeconds,
				"overall", Stats(all),
				"gcs", GC.CollectionCount(0) - gcStart,
				"views", results);
			_file = Write(_report);
			_state = "done";
			DevToolsPlugin.Log.LogInfo("Benchmark: " + JsonConvert.SerializeObject(((Dictionary<string, object>)_report)["overall"]) + " -> " + _file);
		}
		finally
		{
			_recording = false;
			_running = false;
			HoldCamera(false);
		}
		if (quit)
		{
			yield return new WaitForSecondsRealtime(1f);
			Application.Quit();
		}
	}

	// The middle of the benchmark's stockpiles (the median, so one somewhere else does not move it); the home crystal
	// in another game.
	private static Vector3 Centre()
	{
		List<float> xs = new List<float>();
		List<float> ys = new List<float>();
		List<float> zs = new List<float>();
		foreach (FactoryImprint imprint in UnityEngine.Object.FindObjectsOfType<FactoryImprint>())
		{
			if (imprint == null || imprint.AssetKey == null || imprint.AssetKey.Name != "Palette")
			{
				continue;
			}
			Vector3 at = imprint.transform.position;
			xs.Add(at.x);
			ys.Add(at.y);
			zs.Add(at.z);
		}
		if (xs.Count >= 8)
		{
			xs.Sort();
			ys.Sort();
			zs.Sort();
			return new Vector3(xs[xs.Count / 2], ys[ys.Count / 2], zs[zs.Count / 2]);
		}
		XYZ home = WorldEndpoints.HomeVoxel();
		return new Vector3(home.x, home.y, home.z);
	}

	private static bool[] _cameraWas;

	private static void HoldCamera(bool hold)
	{
		Brix.Input.CameraScript camera = Brix.Input.CameraScript.Instance;
		if (camera == null)
		{
			return;
		}
		if (hold)
		{
			_cameraWas = new[] { camera.CanMove, camera.RotationActive, camera.ClimbActive, camera.ZoomActive };
			camera.CanMove = false;
			camera.RotationActive = false;
			camera.ClimbActive = false;
			camera.ZoomActive = false;
		}
		else if (_cameraWas != null)
		{
			camera.CanMove = _cameraWas[0];
			camera.RotationActive = _cameraWas[1];
			camera.ClimbActive = _cameraWas[2];
			camera.ZoomActive = _cameraWas[3];
			_cameraWas = null;
		}
	}

	private static string F(float value)
	{
		return value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
	}

	// Frame rate and frame times in ms: average, 1% low (the average of the slowest 1% as a rate), percentiles, max.
	private static object Stats(List<float> frames)
	{
		if (frames.Count == 0)
		{
			return null;
		}
		List<float> sorted = new List<float>(frames);
		sorted.Sort();
		double sum = 0;
		foreach (float f in sorted)
		{
			sum += f;
		}
		int worst = Math.Max(1, sorted.Count / 100);
		double worstSum = 0;
		for (int i = sorted.Count - worst; i < sorted.Count; i++)
		{
			worstSum += sorted[i];
		}
		double avg = sum / sorted.Count;
		return Json.Obj(
			"count", sorted.Count,
			"fps", Math.Round(1000.0 / avg, 1),
			"onePercentLowFps", Math.Round(1000.0 / (worstSum / worst), 1),
			"avgMs", Math.Round(avg, 2),
			"p50Ms", Math.Round(sorted[sorted.Count / 2], 2),
			"p95Ms", Math.Round(sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * 0.95))], 2),
			"p99Ms", Math.Round(sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * 0.99))], 2),
			"maxMs", Math.Round(sorted[sorted.Count - 1], 2));
	}

	private static object LoopSteps()
	{
#if UNITY6
		List<PlayerLoopTiming.Step> steps = new List<PlayerLoopTiming.Step>(PlayerLoopTiming.Steps);
		steps.Sort((a, b) => b.Ms.CompareTo(a.Ms));
		long frames = Math.Max(1, PlayerLoopTiming.Frames);
		List<object> rows = new List<object>();
		foreach (PlayerLoopTiming.Step step in steps)
		{
			if (rows.Count >= 12 || step.Calls == 0)
			{
				break;
			}
			rows.Add(Json.Obj("step", step.Name, "msPerFrame", Math.Round(step.Ms / frames, 3), "maxMs", Math.Round(step.MaxMs, 2)));
		}
		return rows;
#else
		return null;
#endif
	}

	private static string Write(object report)
	{
		string folder = Path.Combine(Path.Combine(BepInEx.Paths.BepInExRootPath, "devtools"), "benchmarks");
		Directory.CreateDirectory(folder);
		string file = Path.Combine(folder, "benchmark-" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".json");
		File.WriteAllText(file, JsonConvert.SerializeObject(report, Formatting.Indented));
		return file;
	}

	private List<Api.Route> _routes;

	private object Call(string method, string path, params string[] parameters)
	{
		_routes = _routes ?? Routes.Build();
		Api.Route route = _routes.Find((Api.Route r) => r.Method == method && r.Path == path);
		Api.ApiRequest request = new Api.ApiRequest { Method = method, Path = path };
		for (int i = 0; i + 1 < parameters.Length; i += 2)
		{
			request.Query[parameters[i]] = parameters[i + 1];
		}
		return route.Handler(request);
	}
}
