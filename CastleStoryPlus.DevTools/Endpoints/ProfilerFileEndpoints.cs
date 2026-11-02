#if UNITY6
using System;
using System.Collections.Generic;
using System.IO;
using CastleStoryPlus.DevTools.Api;
using UnityEngine;
using UnityEngine.Profiling;
using static CastleStoryPlus.DevTools.Endpoints.Json;

namespace CastleStoryPlus.DevTools.Endpoints;

// Unity's profiler written to a file, in a development player: the whole marker tree of every thread for some seconds
// (Profiler.logFile with the binary log), to be read in the editor by CastleStoryPlus.Player's ProfileDump, which
// writes the main thread's markers with their parents. What the recorders (/api/frames/markers) cannot tell: under
// which step of the player loop a native marker runs, and what each script type's update costs.
internal static class ProfilerFileEndpoints
{
	private static Recording _recording;

	private static string _lastFile;

	internal static void Add(List<Route> routes)
	{
		routes.Add(new Route("POST", "/api/profiler/file", "Writes Unity's profiler log (every marker of every thread, binary) for some seconds, in a development player; the file is read with the editor's ProfileDump (CastleStoryPlus.Player). GET tells whether it still writes", Start)
		{
			Parameters = "seconds (default 3; the editor reads at most 300 frames of a file), path (file to write, without .raw; default BepInEx/profile-<date>-<time> in the player's install)"
		});
		routes.Add(new Route("GET", "/api/profiler/file", "Whether the profiler log is being written, and the last file written", Status));
	}

	private static object Start(ApiRequest request)
	{
		if (!Debug.isDebugBuild)
		{
			throw new ApiException(400, "Not a development player: the profiler writes nothing here");
		}
		if (_recording != null)
		{
			throw new ApiException(409, "Still writing " + _recording.File);
		}
		float seconds = request.Float("seconds", 3f);
		string path = request.String("path", null);
		if (string.IsNullOrEmpty(path))
		{
			path = Path.Combine(BepInEx.Paths.BepInExRootPath, "profile-" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture));
		}
		Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
		GameObject go = new GameObject("CSP_DevToolsProfilerFile");
		UnityEngine.Object.DontDestroyOnLoad(go);
		_recording = go.AddComponent<Recording>();
		_recording.File = path;
		_recording.Until = Time.realtimeSinceStartup + seconds;
		DevToolsPlugin.Log.LogInfo("DevTools: profiler log to " + path + ".raw for " + seconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s");
		return Obj("writing", true, "file", path + ".raw", "seconds", seconds);
	}

	private static object Status(ApiRequest request)
	{
		return Obj("writing", _recording != null, "file", _recording != null ? _recording.File + ".raw" : _lastFile, "developmentPlayer", Debug.isDebugBuild);
	}

	private sealed class Recording : MonoBehaviour
	{
		public string File;

		public float Until;

		private void Start()
		{
			Profiler.maxUsedMemory = 512 * 1024 * 1024;
			Profiler.logFile = File;
			Profiler.enableBinaryLog = true;
			Profiler.enabled = true;
		}

		private void Update()
		{
			if (Time.realtimeSinceStartup < Until)
			{
				return;
			}
			Profiler.enabled = false;
			Profiler.enableBinaryLog = false;
			Profiler.logFile = string.Empty;
			_lastFile = File + ".raw";
			_recording = null;
			DevToolsPlugin.Log.LogInfo("DevTools: profiler log written: " + _lastFile);
			Destroy(gameObject);
		}
	}
}
#endif
