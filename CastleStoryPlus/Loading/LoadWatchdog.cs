using System;
using System.Collections;
using System.Diagnostics;
using System.Text;
using System.Threading;
using Brix.Engine;
using Brix.Game;
using Brix.Network;
using Brix.UI.Builder.Menu.Component;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.Loading;

// Writes the loader's state to the BepInEx log when one step of a map load runs for a long time, so a hung loading screen
// shows what it waits for: the step and its resume point, what it yielded, and the flags the loader's wait
// loops check. Also notices when the load coroutine is no longer resumed at all (its host stopped it), and when
// one frame never ends (an endless loop on the main thread): then the main thread is aborted, so Unity logs the
// exact stack of the loop. The game is hung at that point anyway.
[Feature(Features.FasterLoading, Features.FasterLoadingInfo)]
internal class LoadWatchdog : MonoBehaviour
{
	private const double StuckSeconds = 20;

	private const int FrozenSeconds = 30;

	private static Thread _mainThread;

	private static volatile int _frame;

	// What the load is doing, written by the main thread and saved to BepInEx/loadwatch.txt by the watcher, so it
	// can be read even when the main thread hangs.
	private static volatile string _status = "not loading";

	private static readonly Stopwatch SinceStatus = new Stopwatch();

	private static readonly Stopwatch SinceResume = new Stopwatch();

	private static readonly Stopwatch SinceStepChange = new Stopwatch();

	private static volatile bool _loading;

	private static string _step;

	private static object _yielded;

	private static string _stack;

	private static double _nextReport;

	private static void Enable()
	{
		Plugin.Root.AddComponent<LoadWatchdog>();
		_mainThread = Thread.CurrentThread;
		Thread watcher = new Thread(WatchFrames) { IsBackground = true, Name = "LoadWatchdog" };
		watcher.Start();
	}

	private static void WatchFrames()
	{
		int lastFrame = -1;
		int frozen = 0;
		string file = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "loadwatch.txt");
		while (true)
		{
			Thread.Sleep(1000);
			int frame = _frame;
			try
			{
				System.IO.File.WriteAllText(file, DateTime.Now.ToString("HH:mm:ss") + " frame " + frame + ", loading " + _loading + ", frozen " + frozen + " s\n" + _status + "\n");
			}
			catch (Exception)
			{
			}
			if (!_loading || frame != lastFrame)
			{
				lastFrame = frame;
				frozen = 0;
				continue;
			}
			frozen++;
			if (frozen == FrozenSeconds)
			{
				Plugin.Log.LogError("Load watchdog: frame " + frame + " has not ended for " + FrozenSeconds + " s (endless loop on the main thread while loading, step " + _step + "); aborting the main thread to log its stack");
				_loading = false;
				_mainThread.Abort();
			}
		}
	}

	public static void Started()
	{
		_loading = true;
		_status = "load started";
		_step = null;
		_nextReport = StuckSeconds;
		SinceResume.Reset();
		SinceResume.Start();
		SinceStepChange.Reset();
		SinceStepChange.Start();
	}

	public static void Finished()
	{
		_loading = false;
		_status = "load finished";
	}

	// Called by LoadTiming each time the load coroutine yields.
	public static void Resumed(string step, object yielded, Func<string> stack)
	{
		SinceResume.Reset();
		SinceResume.Start();
		_yielded = yielded;
		if (!SinceStatus.IsRunning || SinceStatus.ElapsedMilliseconds >= 1000)
		{
			SinceStatus.Reset();
			SinceStatus.Start();
			_status = "step " + step + " for " + SinceStepChange.Elapsed.TotalSeconds.ToString("0") + " s, yielded " + ((yielded == null) ? "null" : yielded.GetType().Name) + "\nstack " + stack();
		}
		if (step != _step)
		{
			_step = step;
			_nextReport = StuckSeconds;
			SinceStepChange.Reset();
			SinceStepChange.Start();
			return;
		}
		if (SinceStepChange.Elapsed.TotalSeconds >= _nextReport)
		{
			_stack = stack();
			Report("Load watchdog: the same step has run for " + SinceStepChange.Elapsed.TotalSeconds.ToString("0") + " s");
			_nextReport += StuckSeconds;
		}
	}

	private void Update()
	{
		_frame = Time.frameCount;
		if (!_loading || SinceResume.Elapsed.TotalSeconds < StuckSeconds)
		{
			return;
		}
		Report("Load watchdog: the load coroutine has not been resumed for " + SinceResume.Elapsed.TotalSeconds.ToString("0") + " s, it was stopped");
		_loading = false;
	}

	private static void Report(string title)
	{
		StringBuilder text = new StringBuilder(title);
		text.Append("\n  step: ").Append(_step);
		text.Append("\n  stack: ").Append(_stack);
		text.Append("\n  yielded: ").Append((_yielded == null) ? "null" : _yielded.GetType().Name);
		try
		{
			AppendState(text);
		}
		catch (Exception e)
		{
			text.Append("\n  state failed: ").Append(e.Message);
		}
		Plugin.Log.LogWarning(text.ToString());
	}

	private static void AppendState(StringBuilder text)
	{
		text.Append("\n  lua menu coroutines: ").Append(LuaMenuComponent.CoroutineCount);
		text.Append("\n  server active: ").Append(NetworkServer.active);
		Traverse loader = Traverse.Create(typeof(GameLoader)).Field("_instance");
		if (loader.GetValue() != null)
		{
			text.Append("\n  loading progress: ").Append(loader.Field("loadingProgress").GetValue()).Append(" of ").Append(loader.Field("expectedLoadingProgress").GetValue());
		}
		if (GameParam.instance != null)
		{
			text.Append("\n  host loaded: ").Append(GameParam.instance.HostLoaded).Append(", host ready: ").Append(GameParam.instance.HostReady);
		}
		if (UNetManager.instance != null && UNetManager.instance.UserList != null)
		{
			foreach (User user in UNetManager.instance.UserList)
			{
				if (user == null)
				{
					text.Append("\n  user: null");
					continue;
				}
				bool ready = user.connectionToClient != null && user.connectionToClient.isReady;
				text.Append("\n  user ").Append(user.name).Append(": connection ready ").Append(ready).Append(", faction ready ").Append(user.FactionReady).Append(", terrain loaded ").Append(user.TerrainLoaded).Append(", level ready ").Append(user.LevelReady);
			}
		}
	}
}
