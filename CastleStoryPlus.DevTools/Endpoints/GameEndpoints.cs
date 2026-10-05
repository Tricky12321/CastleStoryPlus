using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using Brix.Engine;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Bricktron;
using Brix.Game.Components;
using Brix.Game.Network;
using Brix.Input;
using CastleStoryPlus.DevTools.Api;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using static CastleStoryPlus.DevTools.Endpoints.Json;
using Object = UnityEngine.Object;
using Path = System.IO.Path;

namespace CastleStoryPlus.DevTools.Endpoints;

// The game's state: status, log, the mod's features, bricktrons, things in the world, screenshots.
internal static class GameEndpoints
{
	private const int MaxObjects = 500;

	internal static void Add(List<Route> routes)
	{
		routes.Add(new Route("GET", "/api/status", "Game and mod state: in a game, host or client, paused, speed, scene, frame rate", Status));
		routes.Add(new Route("GET", "/api/log", "BepInEx log lines (mod, Harmony, Unity), newest last", Log)
		{
			Parameters = "after (line id, only newer lines), count (default 200), problems (true: warnings and errors only)",
			OffMainThread = true
		});
		routes.Add(new Route("GET", "/api/features", "The mod's features and whether each is on", Features));
		routes.Add(new Route("GET", "/api/bricktrons", "Bricktrons: name, job, activity, task, position, carrying", Bricktrons)
		{
			Parameters = "all (true: every faction, else only the player's), q (name/job filter)"
		});
		routes.Add(new Route("GET", "/api/objects", "Things in the world by type name (buildings, blueprints, resources, units)", Objects)
		{
			Parameters = "q (part of the type name, required), max (default 100)"
		});
		routes.Add(new Route("POST", "/api/screenshot", "Saves a screenshot; answers its file path (written at the end of the frame)", Screenshot));
	}

	private static object Status(ApiRequest request)
	{
		Faction local = (User.LocalUser != null) ? User.LocalUser.faction : null;
		return Obj(
			"modVersion", CastleStoryPlus.Plugin.Version,
			"devToolsVersion", DevToolsPlugin.Version,
			"scene", SceneManager.GetActiveScene().name,
			"inGame", Architecte.commence && !Architecte.termine,
			"loading", !Architecte.commence && Architecte.instance != null,
			"host", NetworkServer.active,
			"client", NetworkClient.active && !NetworkServer.active,
			"multiplayer", Neo.multiplayer,
			"paused", ClockConfig.Paused,
			"inputMode", InputModeController.GetCurrentMode().ToString(),
			"timeScale", Time.timeScale,
			"gameTime", Round(Time.time),
			"realTime", Round(Time.realtimeSinceStartup),
			"fps", Round(1f / Mathf.Max(Time.smoothDeltaTime, 0.0001f)),
			"faction", (local != null) ? local.name : null,
			"testsRunning", Testing.TestRunner.Instance != null && Testing.TestRunner.Instance.Running);
	}

	private static object Log(ApiRequest request)
	{
		List<object> lines = new List<object>();
		long last = request.Int("after", 0);
		foreach (LogBuffer.Line line in LogBuffer.Since(request.Int("after", 0), request.Int("count", 200), request.Bool("problems", false)))
		{
			lines.Add(Obj("id", line.Id, "time", line.Time, "level", line.Level, "source", line.Source, "message", line.Message));
			last = line.Id;
		}
		return Obj("last", last, "lines", lines);
	}

	private static object Features(ApiRequest request)
	{
		List<object> features = new List<object>();
		foreach (KeyValuePair<string, bool> pair in CastleStoryPlus.Plugin.LoadedFeatures)
		{
			features.Add(Obj("name", pair.Key, "on", pair.Value));
		}
		return Obj("features", features);
	}

	private static object Bricktrons(ApiRequest request)
	{
		bool all = request.Bool("all", false);
		string filter = request.String("q", string.Empty);
		Faction local = (User.LocalUser != null) ? User.LocalUser.faction : null;
		List<object> result = new List<object>();
		foreach (Labor labor in Object.FindObjectsOfType<Labor>())
		{
			if (labor == null || (!all && (local == null || !local.IsSame(labor.gameObject))))
			{
				continue;
			}
			Nom nom = labor.GetComponent<Nom>();
			string name = (nom != null) ? nom.GetNom() : labor.name;
			string job = (labor.Occupation != null) ? labor.Occupation.CurrentJob.ToString() : null;
			string kind = (labor.Occupation != null) ? labor.Occupation.CurrentOccupation.ToString() : null;
			if (filter.Length > 0 && (name + " " + job + " " + kind + " " + labor.name).IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
			{
				continue;
			}
			Task task = labor.CurrentTask;
			result.Add(Obj(
				"id", labor.gameObject.GetInstanceID(),
				"name", name,
				"object", labor.gameObject.name,
				"job", job,
				"kind", kind,
				"activity", labor.Activity.ToString(),
				"task", (task != null) ? task.Description : null,
				"taskSeconds", (task != null) ? Round(Time.time - task.TaskStartedTimestamp) : 0f,
				"position", Vec(labor.transform.position),
				"carrying", (labor.recepteur == null || labor.recepteur.IsEmpty()) ? null : labor.recepteur.ContentString(),
				"mine", local != null && local.IsSame(labor.gameObject)));
		}
		return Obj("count", result.Count, "bricktrons", result);
	}

	private static object Objects(ApiRequest request)
	{
		string filter = request.Required("q");
		int max = Mathf.Clamp(request.Int("max", 100), 1, MaxObjects);
		Dictionary<string, int> counts = new Dictionary<string, int>();
		List<object> result = new List<object>();
		foreach (FactoryImprint imprint in Object.FindObjectsOfType<FactoryImprint>())
		{
			if (imprint == null || imprint.AssetKey == null || imprint.AssetKey.Factory == "UI")
			{
				continue;
			}
			string type = imprint.AssetKey.Factory + "." + imprint.AssetKey.Name;
			Blueprint blueprint = imprint.GetComponent<Blueprint>();
			string becomes = (blueprint != null && blueprint.BlueprintResult != null) ? blueprint.BlueprintResult.Name : null;
			if ((type + " " + becomes).IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
			{
				continue;
			}
			counts.TryGetValue(type, out int count);
			counts[type] = count + 1;
			if (result.Count < max)
			{
				result.Add(Obj("id", imprint.gameObject.GetInstanceID(), "type", type, "blueprintOf", becomes, "position", Vec(imprint.transform.position)));
			}
		}
		return Obj("counts", counts, "objects", result);
	}

	private static object Screenshot(ApiRequest request)
	{
		string folder = Path.Combine(Paths.BepInExRootPath, "devtools");
		folder = Path.Combine(folder, "screenshots");
		Directory.CreateDirectory(folder);
		string file = Path.Combine(folder, "screenshot_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".png");
		Application.CaptureScreenshot(file);
		return Obj("path", file);
	}
}
