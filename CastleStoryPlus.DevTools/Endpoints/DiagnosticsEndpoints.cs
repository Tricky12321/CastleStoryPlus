using CastleStoryPlus.Core;
using System.Collections.Generic;
using Brix.External.Factories;
using Brix.Game.AI;
using CastleStoryPlus.DevTools.Api;
using CastleStoryPlus.DevTools.Pathing;
using CastleStoryPlus.Diagnostics;
using CastleStoryPlus.WorkerAI;
using UnityEngine;
using UnityEngine.Networking;
using static CastleStoryPlus.DevTools.Endpoints.Json;

namespace CastleStoryPlus.DevTools.Endpoints;

// Why workers do or do not do things: the problems the mod's system log collected (failed tasks with their
// reason, errors), and what the idle workers' automatic cleanup sees.
internal static class DiagnosticsEndpoints
{
	internal static void Add(List<Route> routes)
	{
		routes.Add(new Route("GET", "/api/problems", "The system log's problems (F9): bricktron tasks that failed and why, errors and warnings of the game and the mod", Problems)
		{
			Parameters = "count (default 100), q (filter on source or message)"
		});
#if !ESSENTIALS
		routes.Add(new Route("GET", "/api/cleanup", "What a worker's automatic cleanup would do: each loose item near the base or the worker and why it would not be picked up", Cleanup)
		{
			Parameters = "unit (bricktron id; default: the player's first builder), max (default 50)"
		});
#endif
		routes.Add(new Route("GET", "/api/workertrace", "Whether the worker trace (every worker state change and why workers reject or fail tasks, written to BepInEx/workertrace.log) is on", WorkerTraceState));
		routes.Add(new Route("POST", "/api/workertrace", "Switches the worker trace on or off (off by default; it costs frame time while on)", SetWorkerTrace)
		{
			Parameters = "on (true or false, required)"
		});
		routes.Add(new Route("GET", "/api/xray", "The debug menu's x-ray view: mode (off, semi: the walls of mines, tunnels and caves, full: only the ore) and what the last read found", XRayState));
		routes.Add(new Route("POST", "/api/xray", "Sets the debug menu's x-ray view (only this machine's view)", SetXRay)
		{
			Parameters = "mode (off, semi or full, required)"
		});
	}

	private static object XRayState(ApiRequest request)
	{
		return Obj("mode", XRayView.Current.ToString().ToLower(), "radius", XRayView.Radius, "summary", XRayView.Summary, "state", XRayView.State());
	}

	private static object SetXRay(ApiRequest request)
	{
		string mode = request.String("mode", "off").ToLower();
		XRayView.Set(mode == "semi" ? XRayView.Mode.Semi : mode == "full" ? XRayView.Mode.Full : XRayView.Mode.Off);
		return XRayState(request);
	}

	private static object WorkerTraceState(ApiRequest request)
	{
		return Obj("on", WorkerTrace.Active, "file", System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "workertrace.log"));
	}

	private static object SetWorkerTrace(ApiRequest request)
	{
		if (request.Bool("on", false))
		{
			WorkerTrace.Start();
		}
		else
		{
			WorkerTrace.Stop();
		}
		return WorkerTraceState(request);
	}

	private static object Problems(ApiRequest request)
	{
		int count = request.Int("count", 100);
		string filter = request.String("q", string.Empty);
		List<object> result = new List<object>();
		List<SystemLog.Entry> entries = SystemLog.Problems();
		for (int i = entries.Count - 1; i >= 0 && result.Count < count; i--)
		{
			SystemLog.Entry entry = entries[i];
			if (filter.Length > 0 && (entry.Source + " " + entry.Message).IndexOf(filter, System.StringComparison.OrdinalIgnoreCase) < 0)
			{
				continue;
			}
			result.Add(Obj(
				"time", entry.Time.ToString("HH:mm:ss"),
				"severity", entry.Severity.ToString(),
				"source", entry.Source,
				"message", entry.Message,
				"details", entry.Details,
				"count", entry.Count));
		}
		return Obj("count", result.Count, "problems", result);
	}

#if !ESSENTIALS
	private static object Cleanup(ApiRequest request)
	{
		if (!NetworkServer.active)
		{
			throw new ApiException(409, "Only on the host");
		}
		if (AutoCleanup.Radius == null)
		{
			throw new ApiException(409, "The AutoCleanup feature is switched off");
		}
		Labor labor = Units.Find(request.Int("unit", 0), false);
		if (labor == null)
		{
			throw new ApiException(404, "No bricktron");
		}
		int max = request.Int("max", 50);
		Dictionary<string, int> reasons = new Dictionary<string, int>();
		List<object> items = new List<object>();
		foreach (KeyValuePair<GameObject, string> pair in AutoCleanup.Explain(labor))
		{
			string why = pair.Value ?? "would pick it up";
			reasons.TryGetValue(why, out int n);
			reasons[why] = n + 1;
			if (items.Count < max)
			{
				FactoryImprint imprint = pair.Key.GetComponent<FactoryImprint>();
				items.Add(Obj(
					"id", pair.Key.InstanceId(),
					"type", (imprint != null && imprint.AssetKey != null) ? (imprint.AssetKey.Factory + "." + imprint.AssetKey.Name) : pair.Key.name,
					"position", Vec(pair.Key.transform.position),
					"why", why));
			}
		}
		return Obj(
			"unit", labor.name,
			"radius", AutoCleanup.Radius.Value,
			"carrying", !labor.recepteur.IsEmpty(),
			"reasons", reasons,
			"items", items);
	}
#endif
}
