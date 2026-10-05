using System;
using System.Collections.Generic;
using BepInEx.Logging;
using Brix.Game.AI;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.Diagnostics;

// Collects what the F9 system log window (SystemLogPanel) shows: problems (errors and warnings of the game, the mod
// and Harmony, and bricktron tasks that failed) and the mod's own log lines. Kept in memory only, newest last, with
// repeats of the same message counted instead of added again.
[Feature(Features.SystemLog, Features.SystemLogInfo)]
internal class SystemLog : ILogListener
{
	internal enum Severity
	{
		Info,
		Warning,
		Error
	}

	internal sealed class Entry
	{
		public DateTime Time;

		public Severity Severity;

		public string Source;

		public string Message;

		public string Details;

		public int Count = 1;
	}

	private const int MaxEntries = 300;

	private static readonly object Lock = new object();

	private static readonly List<Entry> ProblemList = new List<Entry>();

	private static readonly List<Entry> LogList = new List<Entry>();

	// Raised (on any thread) when a problem is added, so the window can show a count.
	internal static int ProblemVersion;

	private static void Enable()
	{
		Application.logMessageReceivedThreaded += OnUnityLog;
		BepInEx.Logging.Logger.Listeners.Add(new SystemLog());
	}

	internal static List<Entry> Problems()
	{
		lock (Lock)
		{
			return new List<Entry>(ProblemList);
		}
	}

	internal static List<Entry> Log()
	{
		lock (Lock)
		{
			return new List<Entry>(LogList);
		}
	}

	internal static void ClearProblems()
	{
		lock (Lock)
		{
			ProblemList.Clear();
			ProblemVersion++;
		}
	}

	internal static void ClearLog()
	{
		lock (Lock)
		{
			LogList.Clear();
		}
	}

	internal static void AddProblem(Severity severity, string source, string message, string details)
	{
		lock (Lock)
		{
			Add(ProblemList, severity, source, message, details);
			ProblemVersion++;
		}
	}

	private static void OnUnityLog(string condition, string stackTrace, LogType type)
	{
		if (type == LogType.Log)
		{
			return;
		}
		Severity severity = (type == LogType.Warning) ? Severity.Warning : Severity.Error;
		AddProblem(severity, "Unity", condition, stackTrace);
	}

	// BepInEx sources: warnings and errors are problems, the mod's own lines go to the log. Unity's own messages come
	// through the Unity hook (with stack traces).
	public void LogEvent(object sender, LogEventArgs eventArgs)
	{
		string source = eventArgs.Source?.SourceName ?? "?";
		if (source == "Unity Log")
		{
			return;
		}
		string message = Convert.ToString(eventArgs.Data);
		if ((eventArgs.Level & (LogLevel.Fatal | LogLevel.Error)) != 0)
		{
			AddProblem(Severity.Error, source, message, null);
		}
		else if ((eventArgs.Level & LogLevel.Warning) != 0)
		{
			AddProblem(Severity.Warning, source, message, null);
		}
		if (source == Plugin.Name)
		{
			Severity severity = ((eventArgs.Level & (LogLevel.Fatal | LogLevel.Error)) != 0) ? Severity.Error : (((eventArgs.Level & LogLevel.Warning) != 0) ? Severity.Warning : Severity.Info);
			lock (Lock)
			{
				Add(LogList, severity, source, message, null);
			}
		}
	}

	public void Dispose()
	{
	}

	private static void Add(List<Entry> list, Severity severity, string source, string message, string details)
	{
		message = message ?? string.Empty;
		if (list.Count > 0)
		{
			Entry last = list[list.Count - 1];
			if (last.Severity == severity && last.Source == source && last.Message == message)
			{
				last.Count++;
				last.Time = DateTime.Now;
				return;
			}
		}
		list.Add(new Entry
		{
			Time = DateTime.Now,
			Severity = severity,
			Source = source,
			Message = message,
			Details = details
		});
		if (list.Count > MaxEntries)
		{
			list.RemoveAt(0);
		}
	}
}

// Bricktron tasks that end in a real failure (not ones that were switched, cancelled or replaced by a new order).
// Before OnFinally: it ends the work, which clears the task's worker.
[Feature(Features.SystemLog, Features.SystemLogInfo)]
[HarmonyPatch(typeof(Task), "OnFinally")]
internal static class SystemLogTaskPatch
{
	private static void Prefix(Task __instance)
	{
		switch (__instance.result)
		{
			case TaskResult.WorkerFailed:
			case TaskResult.PathFailed:
			case TaskResult.CouldNotReach:
			case TaskResult.InvalidSearch:
			case TaskResult.BlockedByTransferable:
				break;
			default:
				return;
		}
		BaseLabor worker = __instance.Worker;
		string name = (worker != null) ? worker.gameObject.name : "?";
		string reason = __instance.result.ToString();
		if (__instance.Exception != null)
		{
			reason += " (" + __instance.Exception.GetType().Name + ")";
		}
		string task = string.IsNullOrEmpty(__instance.Description) ? "task" : __instance.Description;
		SystemLog.AddProblem(SystemLog.Severity.Warning, "Bricktron", name + ": " + task + " failed: " + reason, null);
	}
}
