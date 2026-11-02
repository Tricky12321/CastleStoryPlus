using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Profiling;
using UnityEditorInternal;
using UnityEngine;

namespace CastleStoryPlus.Player.Editor
{
	// Development: reads a profiler log written by a development player (DevTools POST /api/profiler/file) and writes
	// one thread's markers summed over its frames, by marker and by path (the marker under its parents), as text:
	// -batchmode -quit -executeMethod CastleStoryPlus.Player.Editor.ProfileDump.Run
	// with CSP_PROFILE_IN (the .raw file), CSP_PROFILE_OUT (text to write; default the .raw plus .txt), CSP_PROFILE_THREAD
	// (default "Main Thread"), CSP_PROFILE_DEPTH (paths this deep, default 10) and CSP_PROFILE_TOP (rows, default 200).
	public static class ProfileDump
	{
		private sealed class Agg
		{
			public double Inclusive;

			public double Self;

			public long Calls;

			public double Max;

			public double Frame;
		}

		public static void Run()
		{
			string input = Environment.GetEnvironmentVariable("CSP_PROFILE_IN");
			if (string.IsNullOrEmpty(input) || !File.Exists(input))
			{
				Fail("set CSP_PROFILE_IN to a .raw profiler log");
				return;
			}
			string output = Environment.GetEnvironmentVariable("CSP_PROFILE_OUT");
			if (string.IsNullOrEmpty(output))
			{
				output = input + ".txt";
			}
			string thread = Environment.GetEnvironmentVariable("CSP_PROFILE_THREAD");
			if (string.IsNullOrEmpty(thread))
			{
				thread = "Main Thread";
			}
			int depth = IntVar("CSP_PROFILE_DEPTH", 10);
			int top = IntVar("CSP_PROFILE_TOP", 200);
			Filter = Environment.GetEnvironmentVariable("CSP_PROFILE_FILTER") ?? string.Empty;
			Edges.Clear();
			if (!ProfilerDriver.LoadProfile(input, false))
			{
				Fail("could not load " + input);
				return;
			}
			int first = ProfilerDriver.firstFrameIndex;
			int last = ProfilerDriver.lastFrameIndex;
			Debug.Log("[ProfileDump] frames " + first + ".." + last + " of " + input);
			Dictionary<string, Agg> byName = new Dictionary<string, Agg>();
			Dictionary<string, Agg> byPath = new Dictionary<string, Agg>();
			List<string> threads = new List<string>();
			int frames = 0;
			double frameMs = 0;
			List<string> path = new List<string>();
			for (int frame = first; frame <= last; frame++)
			{
				int threadIndex = FindThread(frame, thread, threads);
				if (threadIndex < 0)
				{
					continue;
				}
				using (RawFrameDataView view = ProfilerDriver.GetRawFrameDataView(frame, threadIndex))
				{
					if (view == null || !view.valid || view.sampleCount == 0)
					{
						continue;
					}
					frames++;
					frameMs += view.frameTimeMs;
					path.Clear();
					Visit(view, 0, path, depth, byName, byPath);
					EndFrame(byName);
					EndFrame(byPath);
					EndFrame(Edges);
				}
			}
			StringBuilder text = new StringBuilder();
			text.Append("profile ").Append(input).Append('\n');
			text.Append("thread ").Append(thread).Append("; frames ").Append(frames).Append("; frame ms ").Append(Ms(frames > 0 ? frameMs / frames : 0)).Append('\n');
			text.Append("threads seen: ").Append(string.Join(", ", threads)).Append('\n');
			text.Append('\n').Append("By marker: inclusive ms/frame, self ms/frame, calls/frame, max inclusive ms in a frame").Append('\n');
			Write(text, byName, frames, top, true);
			text.Append('\n').Append("By path (depth ").Append(depth).Append("): inclusive ms/frame, self ms/frame, calls/frame, max inclusive ms in a frame").Append('\n');
			Write(text, byPath, frames, top, false);
			text.Append('\n').Append("By caller > callee").Append(Filter.Length > 0 ? " (holding \"" + Filter + "\")" : "").Append(": inclusive ms/frame, self ms/frame, calls/frame, max inclusive ms in a frame").Append('\n');
			Write(text, Edges, frames, top, false);
			File.WriteAllText(output, text.ToString());
			Debug.Log("[ProfileDump] wrote " + output);
			if (Application.isBatchMode)
			{
				EditorApplication.Exit(0);
			}
		}

		private static int FindThread(int frame, string name, List<string> seen)
		{
			for (int t = 0; t < 256; t++)
			{
				using (RawFrameDataView view = ProfilerDriver.GetRawFrameDataView(frame, t))
				{
					if (view == null || !view.valid)
					{
						return -1;
					}
					if (!seen.Contains(view.threadName))
					{
						seen.Add(view.threadName);
					}
					if (view.threadName == name)
					{
						return t;
					}
				}
			}
			return -1;
		}

		// Samples lie depth first; a sample's children follow it. Returns the index after this sample's subtree.
		private static int Visit(RawFrameDataView view, int index, List<string> path, int depth, Dictionary<string, Agg> byName, Dictionary<string, Agg> byPath)
		{
			string name = view.GetSampleName(index) ?? "(unnamed)";
			double time = view.GetSampleTimeMs(index);
			int children = view.GetSampleChildrenCount(index);
			path.Add(name);
			int next = index + 1;
			double childSum = 0;
			for (int i = 0; i < children; i++)
			{
				childSum += view.GetSampleTimeMs(next);
				next = Visit(view, next, path, depth, byName, byPath);
			}
			double self = Math.Max(0, time - childSum);
			Add(byName, name, time, self);
			if (path.Count <= depth)
			{
				Add(byPath, string.Join(" > ", path.ToArray()), time, self);
			}
			if (path.Count >= 2 && (Filter.Length == 0 || name.IndexOf(Filter, StringComparison.Ordinal) >= 0 || path[path.Count - 2].IndexOf(Filter, StringComparison.Ordinal) >= 0))
			{
				Add(Edges, path[path.Count - 2] + " > " + name, time, self);
			}
			path.RemoveAt(path.Count - 1);
			return next;
		}

		// Caller > callee pairs (any depth), for the callers of one marker in a deep profile; CSP_PROFILE_FILTER keeps
		// the pairs whose caller or callee holds the text.
		private static readonly Dictionary<string, Agg> Edges = new Dictionary<string, Agg>();

		private static string Filter = string.Empty;

		private static void Add(Dictionary<string, Agg> aggs, string key, double time, double self)
		{
			if (!aggs.TryGetValue(key, out Agg agg))
			{
				agg = new Agg();
				aggs[key] = agg;
			}
			agg.Inclusive += time;
			agg.Self += self;
			agg.Calls++;
			agg.Frame += time;
		}

		private static void EndFrame(Dictionary<string, Agg> aggs)
		{
			foreach (Agg agg in aggs.Values)
			{
				if (agg.Frame > agg.Max)
				{
					agg.Max = agg.Frame;
				}
				agg.Frame = 0;
			}
		}

		private static void Write(StringBuilder text, Dictionary<string, Agg> aggs, int frames, int top, bool byName)
		{
			List<KeyValuePair<string, Agg>> rows = new List<KeyValuePair<string, Agg>>(aggs);
			rows.Sort((a, b) => b.Value.Inclusive.CompareTo(a.Value.Inclusive));
			int n = 0;
			foreach (KeyValuePair<string, Agg> row in rows)
			{
				if (n++ >= top || frames == 0)
				{
					break;
				}
				Agg a = row.Value;
				text.Append(Ms(a.Inclusive / frames).PadLeft(9)).Append(Ms(a.Self / frames).PadLeft(9))
					.Append(((double)a.Calls / frames).ToString("0.0", CultureInfo.InvariantCulture).PadLeft(9))
					.Append(Ms(a.Max).PadLeft(9)).Append("  ").Append(row.Key).Append('\n');
			}
		}

		private static string Ms(double ms)
		{
			return ms.ToString("0.000", CultureInfo.InvariantCulture);
		}

		private static int IntVar(string name, int fallback)
		{
			string value = Environment.GetEnvironmentVariable(name);
			return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : fallback;
		}

		private static void Fail(string message)
		{
			Debug.LogError("[ProfileDump] " + message);
			if (Application.isBatchMode)
			{
				EditorApplication.Exit(1);
			}
		}
	}
}
