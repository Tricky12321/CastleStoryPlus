using System.Collections.Generic;
using CastleStoryPlus.DevTools.Api;
using UnityEngine;
using static CastleStoryPlus.DevTools.Endpoints.Json;

namespace CastleStoryPlus.DevTools.Endpoints;

// Measuring experiments on the engine side, switched on and off while the game runs (until it restarts), to see what
// a change is worth before it is made in the mod: the two scene cameras drawn as one, the skinned mesh renderers'
// bounds fixed instead of worked out from the skin every frame.
internal static class ExperimentEndpoints
{
	internal static void Add(List<Route> routes)
	{
		routes.Add(new Route("POST", "/api/experiment", "Switches a measuring experiment on or off, until the game restarts. cameras: the background camera (depth -1, the far islands and sky) is switched off and the main camera draws its layers too, with its clear flags, so the scene is culled, lit and shadowed once a frame instead of twice. skinned: every skinned mesh renderer keeps a fixed, generous bounding box (updateWhenOffscreen off) instead of its bounds being worked out from the skinned vertices every frame. Answers as GET", Set)
		{
			Parameters = "name (cameras or skinned; required), on (true or false; required)"
		});
		routes.Add(new Route("GET", "/api/experiment", "The scene cameras (enabled, depth, culling mask, clear flags) and the skinned mesh renderers (how many work their bounds out every frame), and which experiments are on", Get));
	}

	private sealed class CamerasBefore
	{
		public Camera Background;

		public Camera Main;

		public int MainMask;

		public CameraClearFlags MainClear;

		public Color MainColor;

		public Skybox AddedSkybox;
	}

	private sealed class SkinnedBefore
	{
		public bool UpdateWhenOffscreen;

		public Bounds LocalBounds;
	}

	private static CamerasBefore _cameras;

	private static readonly Dictionary<SkinnedMeshRenderer, SkinnedBefore> Skinned = new Dictionary<SkinnedMeshRenderer, SkinnedBefore>();

	private static object Set(ApiRequest request)
	{
		string name = request.Required("name");
		bool on = request.Bool("on", true);
		switch (name)
		{
			case "cameras":
				Cameras(on);
				break;
			case "skinned":
				SkinnedBounds(on);
				break;
			case "bgforward":
				BackgroundForward(on);
				break;
			default:
				throw new ApiException(400, "name: cameras, skinned or bgforward");
		}
		return Get(request);
	}

	private static Camera _forwardCamera;

	private static RenderingPath _forwardBefore;

	// The background camera keeps drawing its layers, but with the forward path (no G-buffer and lighting pass for ten
	// far renderers).
	private static void BackgroundForward(bool on)
	{
		if (on)
		{
			if (_forwardCamera != null)
			{
				return;
			}
			Camera main = Camera.main;
			Camera background = null;
			foreach (Camera camera in Camera.allCameras)
			{
				if (camera != main && camera.enabled && camera.targetTexture == null && (background == null || camera.depth < background.depth))
				{
					background = camera;
				}
			}
			if (background == null)
			{
				throw new ApiException(409, "No second scene camera");
			}
			_forwardCamera = background;
			_forwardBefore = background.renderingPath;
			background.renderingPath = RenderingPath.Forward;
			DevToolsPlugin.Log.LogInfo("DevTools: experiment bgforward on");
			return;
		}
		if (_forwardCamera != null)
		{
			_forwardCamera.renderingPath = _forwardBefore;
			_forwardCamera = null;
		}
	}

	private static void Cameras(bool on)
	{
		if (on)
		{
			if (_cameras != null)
			{
				return;
			}
			Camera main = Camera.main;
			Camera background = null;
			foreach (Camera camera in Camera.allCameras)
			{
				if (camera != main && camera.enabled && camera.targetTexture == null && (background == null || camera.depth < background.depth))
				{
					background = camera;
				}
			}
			if (main == null || background == null)
			{
				throw new ApiException(409, "No main camera or no second scene camera");
			}
			_cameras = new CamerasBefore { Background = background, Main = main, MainMask = main.cullingMask, MainClear = main.clearFlags, MainColor = main.backgroundColor };
			main.cullingMask |= background.cullingMask;
			main.clearFlags = background.clearFlags;
			main.backgroundColor = background.backgroundColor;
			Skybox skybox = background.GetComponent<Skybox>();
			if (skybox != null && skybox.enabled && main.GetComponent<Skybox>() == null)
			{
				_cameras.AddedSkybox = main.gameObject.AddComponent<Skybox>();
				_cameras.AddedSkybox.material = skybox.material;
			}
			background.enabled = false;
			DevToolsPlugin.Log.LogInfo("DevTools: experiment cameras on (background camera off, its layers on the main camera)");
			return;
		}
		if (_cameras == null)
		{
			return;
		}
		if (_cameras.Main != null)
		{
			_cameras.Main.cullingMask = _cameras.MainMask;
			_cameras.Main.clearFlags = _cameras.MainClear;
			_cameras.Main.backgroundColor = _cameras.MainColor;
		}
		if (_cameras.AddedSkybox != null)
		{
			Object.Destroy(_cameras.AddedSkybox);
		}
		if (_cameras.Background != null)
		{
			_cameras.Background.enabled = true;
		}
		_cameras = null;
		DevToolsPlugin.Log.LogInfo("DevTools: experiment cameras off");
	}

	private static void SkinnedBounds(bool on)
	{
		if (on)
		{
			foreach (SkinnedMeshRenderer renderer in Resources.FindObjectsOfTypeAll<SkinnedMeshRenderer>())
			{
				if (renderer == null || renderer.gameObject.scene.name == null || Skinned.ContainsKey(renderer))
				{
					continue;
				}
				Skinned[renderer] = new SkinnedBefore { UpdateWhenOffscreen = renderer.updateWhenOffscreen, LocalBounds = renderer.localBounds };
				renderer.updateWhenOffscreen = false;
				renderer.localBounds = new Bounds(Vector3.zero, new Vector3(6f, 6f, 6f));
			}
			DevToolsPlugin.Log.LogInfo("DevTools: experiment skinned on (" + Skinned.Count + " renderers with fixed bounds)");
			return;
		}
		foreach (KeyValuePair<SkinnedMeshRenderer, SkinnedBefore> pair in Skinned)
		{
			if (pair.Key != null)
			{
				pair.Key.updateWhenOffscreen = pair.Value.UpdateWhenOffscreen;
				pair.Key.localBounds = pair.Value.LocalBounds;
			}
		}
		Skinned.Clear();
		DevToolsPlugin.Log.LogInfo("DevTools: experiment skinned off");
	}

	private static object Get(ApiRequest request)
	{
		List<object> cameras = new List<object>();
		foreach (Camera camera in Resources.FindObjectsOfTypeAll<Camera>())
		{
			if (camera == null || camera.gameObject.scene.name == null || camera.targetTexture != null || !camera.gameObject.activeInHierarchy)
			{
				continue;
			}
			cameras.Add(Obj(
				"name", camera.name,
				"main", camera == Camera.main,
				"enabled", camera.enabled,
				"depth", camera.depth,
				"cullingMask", camera.cullingMask,
				"clearFlags", camera.clearFlags.ToString(),
				"renderingPath", camera.renderingPath.ToString(),
				"position", camera.transform.position.ToString(),
				"parent", camera.transform.parent != null ? camera.transform.parent.name : null,
				"skybox", camera.GetComponent<Skybox>() != null,
				"near", camera.nearClipPlane,
				"far", camera.farClipPlane));
		}
		int skinned = 0;
		int offscreen = 0;
		int active = 0;
		foreach (SkinnedMeshRenderer renderer in Resources.FindObjectsOfTypeAll<SkinnedMeshRenderer>())
		{
			if (renderer == null || renderer.gameObject.scene.name == null)
			{
				continue;
			}
			skinned++;
			if (renderer.updateWhenOffscreen)
			{
				offscreen++;
			}
			if (renderer.isVisible)
			{
				active++;
			}
		}
		return Obj(
			"experiments", Obj("cameras", _cameras != null, "skinned", Skinned.Count > 0, "bgforward", _forwardCamera != null),
			"cameras", cameras,
			"skinnedMeshRenderers", Obj("total", skinned, "updateWhenOffscreen", offscreen, "visible", active));
	}
}
