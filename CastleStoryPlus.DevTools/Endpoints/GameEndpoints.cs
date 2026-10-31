using CastleStoryPlus.Core;
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
using Brix.Legacy;
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
		routes.Add(new Route("GET", "/api/bricktrons", "Bricktrons: name, job, activity, task (its target, whether it can be interrupted), pending task, autonomy, moving, position, carrying (enemies with all=true; healtron: true marks a Healtron)", Bricktrons)
		{
			Parameters = "all (true: every faction, else only the player's), q (name/job filter)"
		});
		routes.Add(new Route("GET", "/api/objects", "Things in the world by type name (buildings, blueprints, resources, units; trees as Nature.Tree, nearest first)", Objects)
		{
			Parameters = "q (part of the type name, required; tree or arbre finds the trees), max (default 100), x, z (trees: nearest this column first; default the home crystal)"
		});
		routes.Add(new Route("GET", "/api/ui/rect", "Where a shown menu part is on the screen (pixels from the top left), by its object's name, e.g. a station's window \"CS market\"", UiRect)
		{
			Parameters = "q (part of the name, required)"
		});
		routes.Add(new Route("POST", "/api/screenshot", "Saves a screenshot; answers its file path (written at the end of the frame). ui=false draws the world's cameras only, without the menus over them (written at once)", Screenshot)
		{
			Parameters = "ui (default true), name (file name without .png, letters, digits, - and _), maxWidth (also write a copy at most this many pixels wide, <name>_small.png, answered as smallPath; for tools that send the picture on, e.g. MCP with its message limit); with ui=false also at (x,y,z), yaw, pitch, distance: the world's cameras drawn from there looking at 'at', put back after (the game's camera script cannot move them in between)"
		});
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
			"testsRunning", Testing.TestRunner.Instance != null && Testing.TestRunner.Instance.Running,
			"worldStart", (Worlds.WorldStarter.Instance != null) ? Worlds.WorldStarter.Instance.Phase : null,
			"worldStartResult", (Worlds.WorldStarter.Instance != null) ? Worlds.WorldStarter.Instance.LastResult : null);
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
				"id", labor.gameObject.InstanceId(),
				"name", name,
				"object", labor.gameObject.name,
				"job", job,
				"kind", kind,
				"activity", labor.Activity.ToString(),
				"task", (task != null) ? task.Description : null,
				"taskSeconds", (task != null) ? Round(Time.time - task.TaskStartedTimestamp) : 0f,
				"position", Vec(labor.transform.position),
				"carrying", (labor.recepteur == null || labor.recepteur.IsEmpty()) ? null : labor.recepteur.ContentString(),
				"mine", local != null && local.IsSame(labor.gameObject),
				"healtron", CastleStoryPlus.Combat.Healtron.IsHealtron(labor),
				"taskTarget", (task != null) ? Target(task.Target) : null,
				"taskCanInterrupt", (task != null) ? (object)task.CanInterrupt : null,
				"pending", (labor.PendingTask != null) ? labor.PendingTask.Description : null,
				"autonomy", (labor.Autonomy != null) ? labor.Autonomy.Id.ToString() : null,
				"moving", Moving(labor)));
		}
		return Obj("count", result.Count, "bricktrons", result);
	}

	// What a task works on: its object's name, type, id and position.
	private static object Target(GameObject target)
	{
		if (target == null)
		{
			return null;
		}
		FactoryImprint imprint = target.GetComponent<FactoryImprint>();
		return Obj("id", target.InstanceId(), "name", target.name, "type", (imprint != null) ? imprint.AssetKey.ToString() : null, "position", Vec(target.transform.position));
	}

	// Whether the bricktron walks or runs, else stands still (a task that never ends while it stands still is stuck).
	private static object Moving(Labor labor)
	{
		Locomotion4 locomotion = labor.GetComponent<Locomotion4>();
		if (locomotion == null)
		{
			return null;
		}
		return Obj("walking", locomotion.Walking, "running", locomotion.Running, "inPlace", locomotion.InPlace, "ladder", locomotion.IsUsingLadder, "airborne", locomotion.IsAirborne, "grabbed", locomotion.IsGrabbed);
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
				result.Add(Obj("id", imprint.gameObject.InstanceId(), "type", type, "blueprintOf", becomes, "position", Vec(imprint.transform.position)));
			}
		}
		Trees(request, filter, max, counts, result);
		return Obj("counts", counts, "objects", result);
	}

	// Trees are no game objects: the game keeps them by their root voxel in TreeRegistry (and draws them itself), so
	// they are listed from there as Nature.Tree (also found by "arbre"), nearest x, z first (default the home crystal).
	private static void Trees(ApiRequest request, string filter, int max, Dictionary<string, int> counts, List<object> result)
	{
		TreeRegistry registry = TreeRegistry.Instance;
		if (registry == null || ("Nature.Tree Arbre").IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
		{
			return;
		}
		List<XYZ> trees = new List<XYZ>(registry._treeData.Keys);
		if (trees.Count == 0)
		{
			return;
		}
		counts["Nature.Tree"] = trees.Count;
		XYZ? center = null;
		if (request.Get("x") != null || request.Get("z") != null)
		{
			center = new XYZ(request.Int("x", 0), 0, request.Int("z", 0));
		}
		else if (User.LocalUser != null && User.LocalUser.faction != null)
		{
			try
			{
				center = WorldEndpoints.HomeVoxel();
			}
			catch (ApiException)
			{
				center = null;
			}
		}
		if (center.HasValue)
		{
			XYZ from = center.Value;
			trees.Sort((a, b) => WorldEndpoints.FlatDistance(a, from).CompareTo(WorldEndpoints.FlatDistance(b, from)));
		}
		for (int i = 0; i < trees.Count && result.Count < max; i++)
		{
			XYZ tree = trees[i];
			result.Add(Obj("id", null, "type", "Nature.Tree", "blueprintOf", null, "position", PathEndpoints.Voxel(tree), "size", (registry._treeData[tree] >> 4) & 0xF, "distance", center.HasValue ? Round(Mathf.Sqrt(WorldEndpoints.FlatDistance(tree, center.Value))) : (float?)null));
		}
	}

	// Where a shown menu part is on the screen, in pixels from the top left (e.g. a station's window, "CS market").
	private static object UiRect(ApiRequest request)
	{
		string q = request.Required("q");
		foreach (RectTransform rect in Object.FindObjectsOfType<RectTransform>())
		{
			if (rect == null || !rect.gameObject.activeInHierarchy || rect.name.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0)
			{
				continue;
			}
			// A station window hides by switching its background image off.
			bool hidden = false;
			foreach (Behaviour behaviour in rect.GetComponents<Behaviour>())
			{
				if (behaviour != null && behaviour.GetType().Name == "Image" && !behaviour.enabled)
				{
					hidden = true;
				}
			}
			if (hidden)
			{
				continue;
			}
			Canvas canvas = rect.GetComponentInParent<Canvas>();
			Camera camera = (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay) ? canvas.worldCamera : null;
			Vector3[] corners = new Vector3[4];
			rect.GetWorldCorners(corners);
			Vector2 low = RectTransformUtility.WorldToScreenPoint(camera, corners[0]);
			Vector2 high = RectTransformUtility.WorldToScreenPoint(camera, corners[2]);
			return Obj("name", rect.name, "x", Mathf.RoundToInt(low.x), "y", Mathf.RoundToInt(Screen.height - high.y), "width", Mathf.RoundToInt(high.x - low.x), "height", Mathf.RoundToInt(high.y - low.y), "screen", new int[2] { Screen.width, Screen.height });
		}
		throw new ApiException(404, "No shown menu part named like " + q);
	}

	// The cameras that draw the world (not the menu's overlay), in their order, into one picture of the screen's size.
	private static byte[] WorldOnly(bool posed, Vector3 eye, Quaternion rotation)
	{
		int width = Screen.width;
		int height = Screen.height;
		RenderTexture target = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
		List<Camera> cameras = new List<Camera>();
		foreach (Camera camera in Camera.allCameras)
		{
			if (camera != null && camera.enabled && camera.targetTexture == null && camera.cullingMask != (1 << LayerMask.NameToLayer("UI")))
			{
				cameras.Add(camera);
			}
		}
		cameras.Sort((Camera a, Camera b) => a.depth.CompareTo(b.depth));
		// The marks over the world stay out of the picture: the projects' flags (the build job's pole) and the
		// icons over things (world canvases).
		List<Renderer> marks = new List<Renderer>();
		foreach (Project project in Object.FindObjectsOfType<Project>())
		{
			foreach (Renderer renderer in project.GetComponentsInChildren<Renderer>())
			{
				if (renderer.enabled)
				{
					marks.Add(renderer);
				}
			}
		}
		List<Canvas> icons = new List<Canvas>();
		foreach (Canvas canvas in Object.FindObjectsOfType<Canvas>())
		{
			if (canvas.enabled && canvas.renderMode == RenderMode.WorldSpace)
			{
				icons.Add(canvas);
			}
		}
		marks.ForEach((Renderer renderer) => renderer.enabled = false);
		icons.ForEach((Canvas canvas) => canvas.enabled = false);
		RenderTexture active = RenderTexture.active;
		Texture2D picture = new Texture2D(width, height, TextureFormat.RGB24, false);
		try
		{
			foreach (Camera camera in cameras)
			{
				Vector3 position = camera.transform.position;
				Quaternion turn = camera.transform.rotation;
				if (posed)
				{
					camera.transform.position = eye;
					camera.transform.rotation = rotation;
				}
				camera.targetTexture = target;
				camera.Render();
				camera.targetTexture = null;
				camera.transform.position = position;
				camera.transform.rotation = turn;
			}
			RenderTexture.active = target;
			picture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
			picture.Apply();
			return picture.EncodeToPNG();
		}
		finally
		{
			foreach (Renderer renderer in marks)
			{
				if (renderer != null)
				{
					renderer.enabled = true;
				}
			}
			foreach (Canvas canvas in icons)
			{
				if (canvas != null)
				{
					canvas.enabled = true;
				}
			}
			RenderTexture.active = active;
			RenderTexture.ReleaseTemporary(target);
			Object.Destroy(picture);
		}
	}

	private static object Screenshot(ApiRequest request)
	{
		string folder = Path.Combine(Paths.BepInExRootPath, "devtools");
		folder = Path.Combine(folder, "screenshots");
		Directory.CreateDirectory(folder);
		string name = request.Get("name");
		if (string.IsNullOrEmpty(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains(".."))
		{
			name = "screenshot_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
		}
		string file = Path.Combine(folder, name + ".png");
		int maxWidth = request.Int("maxWidth", 0);
		string small = maxWidth > 0 ? Path.Combine(folder, name + "_small.png") : null;
		if (small != null && File.Exists(small))
		{
			File.Delete(small);
		}
		if (request.Bool("ui", true))
		{
#if UNITY6
			ScreenCapture.CaptureScreenshot(file);
#else
			Application.CaptureScreenshot(file);
#endif
			if (small != null)
			{
				// The capture is written at the end of the frame: the copy is made once it is there.
				ScreenshotCopy.After(file, small, maxWidth);
			}
		}
		else
		{
			bool posed = request.Get("at") != null;
			Vector3 at = posed ? request.Vector("at") : Vector3.zero;
			Quaternion rotation = Quaternion.Euler(Mathf.Clamp(request.Float("pitch", 30f), 5f, 89f), request.Float("yaw", 0f), 0f);
			Vector3 eye = at - rotation * Vector3.forward * Mathf.Clamp(request.Float("distance", 14f), 1f, 300f);
			byte[] png = WorldOnly(posed, eye, rotation);
			File.WriteAllBytes(file, png);
			if (small != null)
			{
				File.WriteAllBytes(small, ScreenshotCopy.Scaled(png, maxWidth));
			}
		}
		return small != null ? Obj("path", file, "smallPath", small) : Obj("path", file);
	}
}
