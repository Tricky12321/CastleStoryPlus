using System;
using System.Collections.Generic;
using BepInEx.Logging;

namespace CastleStoryPlus.DevTools.Api;

// The last log lines of BepInEx (the mod, Harmony, the game's Unity log), numbered so the API can hand out only
// the lines after one already seen.
internal class LogBuffer : ILogListener
{
	internal sealed class Line
	{
		public long Id;

		public string Time;

		public string Level;

		public string Source;

		public string Message;
	}

	private const int MaxLines = 2000;

	private static readonly LinkedList<Line> Lines = new LinkedList<Line>();

	private static long _nextId = 1;

	internal static void Install()
	{
		ReadEarlierLines();
		Logger.Listeners.Add(new LogBuffer());
	}

	// What was logged before this plugin started (BepInEx, the mod's start-up), from BepInEx's log file:
	// "[Level  :Source] message", with following lines of the same message (stack traces) added to it.
	private static void ReadEarlierLines()
	{
		string path = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "LogOutput.log");
		string[] fileLines;
		try
		{
			// The file is open for writing by BepInEx; read it shared.
			using (System.IO.FileStream stream = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite))
			using (System.IO.StreamReader reader = new System.IO.StreamReader(stream))
			{
				fileLines = reader.ReadToEnd().Split('\n');
			}
		}
		catch (Exception)
		{
			return;
		}
		Line current = null;
		foreach (string raw in fileLines)
		{
			string text = raw.TrimEnd('\r');
			int close = text.IndexOf(']');
			int colon = text.IndexOf(':');
			if (text.StartsWith("[") && close > 0 && colon > 0 && colon < close)
			{
				current = new Line
				{
					Id = _nextId++,
					Time = string.Empty,
					Level = text.Substring(1, colon - 1).Trim(),
					Source = text.Substring(colon + 1, close - colon - 1).Trim(),
					Message = text.Substring(close + 1).TrimStart()
				};
				Lines.AddLast(current);
			}
			else if (current != null && text.Length > 0)
			{
				current.Message += "\n" + text;
			}
		}
		while (Lines.Count > MaxLines)
		{
			Lines.RemoveFirst();
		}
	}

	public void LogEvent(object sender, LogEventArgs eventArgs)
	{
		Line line = new Line
		{
			Time = DateTime.Now.ToString("HH:mm:ss.fff"),
			Level = eventArgs.Level.ToString(),
			Source = (eventArgs.Source != null) ? eventArgs.Source.SourceName : string.Empty,
			Message = (eventArgs.Data != null) ? eventArgs.Data.ToString() : string.Empty
		};
		lock (Lines)
		{
			line.Id = _nextId++;
			Lines.AddLast(line);
			while (Lines.Count > MaxLines)
			{
				Lines.RemoveFirst();
			}
		}
	}

	// Lines after the given id (0: from the start), at most count of the newest; warnings and errors only if asked.
	internal static List<Line> Since(long afterId, int count, bool problemsOnly)
	{
		List<Line> result = new List<Line>();
		lock (Lines)
		{
			foreach (Line line in Lines)
			{
				if (line.Id <= afterId)
				{
					continue;
				}
				if (problemsOnly && line.Level != "Warning" && line.Level != "Error" && line.Level != "Fatal")
				{
					continue;
				}
				result.Add(line);
			}
		}
		if (result.Count > count)
		{
			result.RemoveRange(0, result.Count - count);
		}
		return result;
	}

	public void Dispose()
	{
	}
}
