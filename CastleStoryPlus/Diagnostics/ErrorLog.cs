using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Logging;
using CastleStoryPlus.Core;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CastleStoryPlus.Diagnostics;

// Modding aid, off by default ([Debug] ErrorLog): writes every error, exception and assert of the game and of all
// BepInEx sources (plus their warnings) with timestamps, the active scene and full stack traces to
// BepInEx/errors.log. The previous session is kept as errors.prev.log. Repeats of the same error are counted
// instead of written again, so a per-frame error does not drown the rest.
[Feature]
internal class ErrorLog : ILogListener
{
	private const int FullRepeats = 3;

	private static readonly object Lock = new object();

	private static readonly Dictionary<string, int> Seen = new Dictionary<string, int>();

	private static StreamWriter _writer;

	private static bool _includeWarnings;

	private static string _scene = "?";

	private static void Enable()
	{
		if (!Plugin.Cfg.Bind("Debug", "ErrorLog", false, "Write all errors and exceptions with stack traces to BepInEx/errors.log (modding aid).").Value)
		{
			return;
		}
		_includeWarnings = Plugin.Cfg.Bind("Debug", "ErrorLogWarnings", false, "Also write Unity warnings to BepInEx/errors.log (BepInEx warnings are always written).").Value;
		string path = Path.Combine(BepInEx.Paths.BepInExRootPath, "errors.log");
		string previous = Path.Combine(BepInEx.Paths.BepInExRootPath, "errors.prev.log");
		try
		{
			if (File.Exists(path))
			{
				if (File.Exists(previous))
				{
					File.Delete(previous);
				}
				File.Move(path, previous);
			}
			_writer = new StreamWriter(path, append: false) { AutoFlush = true };
		}
		catch (Exception ex)
		{
			// Still held by a game that has not finished closing: write this session to its own file.
			string fallback = Path.Combine(BepInEx.Paths.BepInExRootPath, "errors." + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".log");
			Plugin.Log.LogWarning("ErrorLog: could not open " + path + " (" + ex.Message + "), writing to " + fallback);
			try
			{
				_writer = new StreamWriter(fallback, append: false) { AutoFlush = true };
			}
			catch (Exception fallbackEx)
			{
				Plugin.Log.LogWarning("ErrorLog: could not open " + fallback + ": " + fallbackEx.Message);
				return;
			}
		}
		Write("Session started " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + ", " + Plugin.Name + " " + Plugin.Version + ", Unity " + Application.unityVersion);
		Application.logMessageReceivedThreaded += OnUnityLog;
		BepInEx.Logging.Logger.Listeners.Add(new ErrorLog());
		SceneManager.activeSceneChanged += (Scene from, Scene to) =>
		{
			_scene = to.name;
			Write("---- scene " + to.name);
		};
	}

	private static void OnUnityLog(string condition, string stackTrace, LogType type)
	{
		if (type == LogType.Log || (type == LogType.Warning && !_includeWarnings))
		{
			return;
		}
		Record(type.ToString().ToUpperInvariant(), "Unity", condition, stackTrace);
	}

	// BepInEx sources: the plugin's own log and Harmony. Unity messages arrive through the Unity hook with stack traces.
	public void LogEvent(object sender, LogEventArgs eventArgs)
	{
		if ((eventArgs.Level & (LogLevel.Fatal | LogLevel.Error | LogLevel.Warning)) == 0)
		{
			return;
		}
		string source = eventArgs.Source?.SourceName ?? "?";
		if (source == "Unity Log")
		{
			return;
		}
		Record(eventArgs.Level.ToString().ToUpperInvariant(), source, Convert.ToString(eventArgs.Data), null);
	}

	public void Dispose()
	{
	}

	private static void Record(string level, string source, string message, string stackTrace)
	{
		string key = level + "|" + message + "|" + FirstLine(stackTrace);
		lock (Lock)
		{
			int count = Seen.TryGetValue(key, out int seen) ? seen + 1 : 1;
			Seen[key] = count;
			if (count <= FullRepeats)
			{
				string text = level + " [" + source + "] (scene " + _scene + ", frame " + FrameCount() + ")" + ((count > 1) ? " repeat " + count : string.Empty) + "\n" + message;
				if (!string.IsNullOrEmpty(stackTrace))
				{
					text += "\n" + stackTrace.TrimEnd();
				}
				Write(text);
			}
			else if (IsPowerOfTen(count))
			{
				Write(level + " [" + source + "] repeated " + count + " times: " + FirstLine(message));
			}
		}
	}

	private static void Write(string text)
	{
		lock (Lock)
		{
			_writer?.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " " + text);
		}
	}

	// Time.frameCount may only be read on the main thread.
	private static string FrameCount()
	{
		try
		{
			return Time.frameCount.ToString();
		}
		catch (Exception)
		{
			return "?";
		}
	}

	private static string FirstLine(string text)
	{
		if (string.IsNullOrEmpty(text))
		{
			return string.Empty;
		}
		int end = text.IndexOf('\n');
		return (end < 0) ? text : text.Substring(0, end);
	}

	private static bool IsPowerOfTen(int count)
	{
		while (count >= 10 && count % 10 == 0)
		{
			count /= 10;
		}
		return count == 1;
	}
}
