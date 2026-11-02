#if UNITY6
using System.Collections.Generic;
using System.Diagnostics;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEngine.LowLevel;

namespace CastleStoryPlus.DevTools.Frames;

// Unity's own profiler markers (the names the Profiler window shows: culling, drawing, skinning, each script type's
// Update and so on), read with a ProfilerRecorder each. A release player has only a few of them; a development player
// (PlayerBuild.ProfilingLinux) has them all, so this splits up the native time the player loop steps cannot. Started by
// the first call of /api/frames/markers: one recorder per marker of the chosen categories, read once a frame by a step
// put first in the player loop (a recorder's last value is the whole previous frame). A recorder makes its marker cost
// a little more, so Stop takes them all away again.
internal static class ProfilerMarkers
{
	internal sealed class Marker
	{
		internal string Name;

		internal string Category;

		internal ProfilerRecorder Recorder;

		internal double Ms;

		internal double MaxMs;

		internal long Count;
	}

	// A render statistic (draw calls, batches, SetPass calls, triangles, vertices and so on): a count per frame.
	internal sealed class Counter
	{
		internal string Name;

		internal string Category;

		internal ProfilerRecorder Recorder;

		internal double Sum;

		internal long Max;
	}

	internal static bool Running { get; private set; }

	internal static readonly List<Marker> Markers = new List<Marker>();

	internal static readonly List<Counter> Counters = new List<Counter>();

	internal static long Frames;

	internal static readonly Stopwatch Since = new Stopwatch();

	private static PlayerLoopSystem _original;

	private static bool _first;

	internal static void Start(ICollection<string> categories)
	{
		if (Running)
		{
			return;
		}
		List<ProfilerRecorderHandle> handles = new List<ProfilerRecorderHandle>();
		ProfilerRecorderHandle.GetAvailable(handles);
		Markers.Clear();
		Counters.Clear();
		foreach (ProfilerRecorderHandle handle in handles)
		{
			ProfilerRecorderDescription description = ProfilerRecorderHandle.GetDescription(handle);
			string category = description.Category.Name;
			// The render statistics (counts) come along whatever categories were asked for; they exist in a release
			// player too.
			if (description.UnitType == ProfilerMarkerDataUnit.Count && category == "Render")
			{
				ProfilerRecorder counting = new ProfilerRecorder(description.Category, description.Name, 1);
				if (counting.Valid)
				{
					counting.Start();
					Counters.Add(new Counter { Name = description.Name, Category = category, Recorder = counting });
				}
				else
				{
					counting.Dispose();
				}
				continue;
			}
			if (description.UnitType != ProfilerMarkerDataUnit.TimeNanoseconds)
			{
				continue;
			}
			if (categories != null && categories.Count > 0 && !categories.Contains(category))
			{
				continue;
			}
			ProfilerRecorder recorder = new ProfilerRecorder(description.Category, description.Name, 1, ProfilerRecorderOptions.SumAllSamplesInFrame | ProfilerRecorderOptions.WrapAroundWhenCapacityReached);
			if (!recorder.Valid)
			{
				recorder.Dispose();
				continue;
			}
			recorder.Start();
			Markers.Add(new Marker { Name = description.Name, Category = category, Recorder = recorder });
		}
		_original = PlayerLoop.GetCurrentPlayerLoop();
		PlayerLoopSystem loop = _original;
		PlayerLoopSystem[] phases = loop.subSystemList;
		PlayerLoopSystem first = phases[0];
		List<PlayerLoopSystem> steps = new List<PlayerLoopSystem>();
		steps.Add(new PlayerLoopSystem { type = typeof(ProfilerMarkers), updateDelegate = Sample });
		if (first.subSystemList != null)
		{
			steps.AddRange(first.subSystemList);
		}
		first.subSystemList = steps.ToArray();
		PlayerLoopSystem[] copy = (PlayerLoopSystem[])phases.Clone();
		copy[0] = first;
		loop.subSystemList = copy;
		PlayerLoop.SetPlayerLoop(loop);
		Reset();
		Running = true;
	}

	internal static void Stop()
	{
		if (!Running)
		{
			return;
		}
		PlayerLoop.SetPlayerLoop(_original);
		foreach (Marker marker in Markers)
		{
			marker.Recorder.Dispose();
		}
		foreach (Counter counter in Counters)
		{
			counter.Recorder.Dispose();
		}
		Markers.Clear();
		Counters.Clear();
		Running = false;
	}

	internal static void Reset()
	{
		foreach (Marker marker in Markers)
		{
			marker.Ms = 0;
			marker.MaxMs = 0;
			marker.Count = 0;
		}
		foreach (Counter counter in Counters)
		{
			counter.Sum = 0;
			counter.Max = 0;
		}
		Frames = 0;
		_first = true;
		Since.Restart();
	}

	private static void Sample()
	{
		// The first frame after a (re)start holds the time before it.
		if (_first)
		{
			_first = false;
			return;
		}
		Frames++;
		foreach (Marker marker in Markers)
		{
			ProfilerRecorder recorder = marker.Recorder;
			long value = recorder.LastValue;
			if (value <= 0)
			{
				continue;
			}
			double ms = value / 1000000.0;
			marker.Ms += ms;
			marker.Count++;
			if (ms > marker.MaxMs)
			{
				marker.MaxMs = ms;
			}
		}
		foreach (Counter counter in Counters)
		{
			long value = counter.Recorder.LastValue;
			counter.Sum += value;
			if (value > counter.Max)
			{
				counter.Max = value;
			}
		}
	}
}
#endif
