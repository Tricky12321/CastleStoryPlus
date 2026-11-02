using System.Collections.Generic;
using CastleStoryPlus.DevTools.Api;
using UnityEngine;
using static CastleStoryPlus.DevTools.Endpoints.Json;

namespace CastleStoryPlus.DevTools.Endpoints;

// What the game draws: every active renderer, how many are in the main camera's view (its frustum, tested here on
// the renderer's bounds) and how many Unity drew last frame (Renderer.isVisible: by any camera, the shadow maps
// included), per layer; and the cameras and render settings (shadows, vsync) that decide the cost.
internal static class RenderEndpoints
{
	private sealed class Count
	{
		internal int Total;

		internal int InView;

		internal int Drawn;

		internal long Vertices;

		internal long DrawnVertices;
	}

	// The renderers drawn last frame that share a shader, material or mesh (GET /api/render?by=).
	private sealed class Group
	{
		internal int Renderers;

		internal int Submeshes;

		internal long Vertices;

		internal int ShadowCasters;
	}

	private static string GroupKey(Renderer renderer, string by)
	{
		Material material = renderer.sharedMaterial;
		string shader = (material != null && material.shader != null) ? material.shader.name : "-";
		if (by == "shader")
		{
			return shader;
		}
		string key = shader + " | " + ((material != null) ? material.name : "-");
		if (by == "material")
		{
			return key;
		}
		string mesh = "-";
		MeshFilter filter = renderer.GetComponent<MeshFilter>();
		if (filter != null && filter.sharedMesh != null)
		{
			mesh = filter.sharedMesh.name;
		}
		else if (renderer is SkinnedMeshRenderer skinned && skinned.sharedMesh != null)
		{
			mesh = skinned.sharedMesh.name;
		}
		return key + " | " + mesh + " | " + renderer.GetType().Name;
	}

	internal static void Add(List<Route> routes)
	{
		routes.Add(new Route("GET", "/api/render", "What is drawn: the active renderers, how many are in the main camera's view and how many were drawn last frame (by any camera, shadows included), per layer with their vertices; the lights; the cameras (culling mask, clip planes, per-layer cull distances, occlusion culling) and the render settings (shadow distance and quality, vsync, frame rate cap)", Get)
		{
			Parameters = "top (most layers or groups to list, default 32), by (shader, material or mesh: the renderers drawn last frame grouped by their shader, their shader and material, or those and their mesh and renderer type, most first, with their count, submeshes (about the draw calls per pass), vertices and how many cast shadows)"
		});
		routes.Add(new Route("GET", "/api/render/invalid", "Renderers whose bounds are not finite (NaN or infinite; Unity logs \"Invalid AABB\" for them every frame): how many per renderer type and object name, and the first ones with their path, bounds, transform and mesh", Invalid)
		{
			Parameters = "max (renderers to list, default 50)"
		});
		routes.Add(new Route("POST", "/api/render", "Switches render costs on and off for measuring, until the game restarts: a camera image effect, the shadow quality, the shadows of point and spot lights, or point and spot lights altogether. Answers as GET", Set)
		{
			Parameters = "effect + enabled (an image effect on the cameras by type name, as GET lists them), shadows (Disable, HardOnly or All), lightShadows (false: point and spot lights cast no shadow; true: back as they were), pointLights (false: point and spot lights off; true: back on), linearIntensity (true or false: GraphicsSettings.lightsUseLinearIntensity), ambientIntensity (RenderSettings.ambientIntensity, 0 to 8), instancing (true or false: GPU instancing on the materials of every mesh renderer, to measure what it saves; answers how many materials changed)"
		});
	}

	// Renderers whose bounds are not finite (NaN or infinite): Unity logs "Invalid AABB" for each, every frame, and in
	// the editor every such line carries a stack trace.
	private static object Invalid(ApiRequest request)
	{
		int max = request.Int("max", 50);
		int total = 0;
		Dictionary<string, int> byName = new Dictionary<string, int>();
		List<object> found = new List<object>();
		foreach (Renderer renderer in Object.FindObjectsOfType<Renderer>())
		{
			Bounds bounds = renderer.bounds;
			if (Finite(bounds.center) && Finite(bounds.extents))
			{
				continue;
			}
			total++;
			string name = renderer.GetType().Name + " " + Clean(renderer.name);
			byName.TryGetValue(name, out int count);
			byName[name] = count + 1;
			if (found.Count >= max)
			{
				continue;
			}
			Transform transform = renderer.transform;
			Mesh mesh = renderer is SkinnedMeshRenderer skinned ? skinned.sharedMesh : renderer.GetComponent<MeshFilter>()?.sharedMesh;
			found.Add(Obj(
				"path", Path(transform),
				"type", renderer.GetType().Name,
				"enabled", renderer.enabled && renderer.gameObject.activeInHierarchy,
				"boundsCenter", bounds.center.ToString("F2"),
				"boundsExtents", bounds.extents.ToString("F2"),
				"position", transform.position.ToString("F2"),
				"lossyScale", transform.lossyScale.ToString("F3"),
				"mesh", mesh != null ? mesh.name + " (" + mesh.vertexCount + " vertices, bounds " + mesh.bounds.extents.ToString("F2") + ")" : null));
		}
		List<KeyValuePair<string, int>> sorted = new List<KeyValuePair<string, int>>(byName);
		sorted.Sort((a, b) => b.Value.CompareTo(a.Value));
		List<object> names = new List<object>();
		foreach (KeyValuePair<string, int> pair in sorted)
		{
			names.Add(Obj("name", pair.Key, "count", pair.Value));
		}
		return Obj("total", total, "byName", names, "renderers", found);
	}

	private static bool Finite(Vector3 v)
	{
		return !float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsNaN(v.z) && !float.IsInfinity(v.x) && !float.IsInfinity(v.y) && !float.IsInfinity(v.z);
	}

	private static object Get(ApiRequest request)
	{
		int top = (int)request.Float("top", 32f);
		string by = request.String("by", "");
		Camera main = Camera.main;
		Plane[] planes = (main != null) ? GeometryUtility.CalculateFrustumPlanes(main) : null;
		Dictionary<int, Count> layers = new Dictionary<int, Count>();
		Dictionary<string, Group> groups = new Dictionary<string, Group>();
		Count all = new Count();
		foreach (Renderer renderer in Object.FindObjectsOfType<Renderer>())
		{
			if (!renderer.enabled || !renderer.gameObject.activeInHierarchy)
			{
				continue;
			}
			int layer = renderer.gameObject.layer;
			if (!layers.TryGetValue(layer, out Count count))
			{
				count = new Count();
				layers[layer] = count;
			}
			bool inView = planes != null && GeometryUtility.TestPlanesAABB(planes, renderer.bounds);
			bool drawn = renderer.isVisible;
			long vertices = Vertices(renderer);
			if (by.Length > 0 && drawn)
			{
				string key = GroupKey(renderer, by);
				if (!groups.TryGetValue(key, out Group group))
				{
					group = new Group();
					groups[key] = group;
				}
				group.Renderers++;
				group.Submeshes += renderer.sharedMaterials.Length;
				group.Vertices += vertices;
				if (renderer.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off)
				{
					group.ShadowCasters++;
				}
			}
			foreach (Count c in new Count[2] { count, all })
			{
				c.Total++;
				c.Vertices += vertices;
				if (inView)
				{
					c.InView++;
				}
				if (drawn)
				{
					c.Drawn++;
					c.DrawnVertices += vertices;
				}
			}
		}
		List<KeyValuePair<int, Count>> sorted = new List<KeyValuePair<int, Count>>(layers);
		sorted.Sort((a, b) => b.Value.Total.CompareTo(a.Value.Total));
		List<object> perLayer = new List<object>();
		for (int i = 0; i < sorted.Count && i < top; i++)
		{
			string name = LayerMask.LayerToName(sorted[i].Key);
			perLayer.Add(Obj(
				"layer", sorted[i].Key,
				"name", string.IsNullOrEmpty(name) ? null : name,
				"renderers", sorted[i].Value.Total,
				"inView", sorted[i].Value.InView,
				"drawn", sorted[i].Value.Drawn,
				"vertices", sorted[i].Value.Vertices,
				"drawnVertices", sorted[i].Value.DrawnVertices));
		}
		List<object> grouped = new List<object>();
		if (by.Length > 0)
		{
			List<KeyValuePair<string, Group>> order = new List<KeyValuePair<string, Group>>(groups);
			order.Sort((a, b) => b.Value.Submeshes.CompareTo(a.Value.Submeshes));
			for (int i = 0; i < order.Count && i < top; i++)
			{
				grouped.Add(Obj(
					"group", order[i].Key,
					"renderers", order[i].Value.Renderers,
					"submeshes", order[i].Value.Submeshes,
					"vertices", order[i].Value.Vertices,
					"shadowCasters", order[i].Value.ShadowCasters));
			}
		}
		List<object> cameras = new List<object>();
		foreach (Camera camera in Camera.allCameras)
		{
			cameras.Add(Obj(
				"name", camera.name,
				"main", camera == main,
				"depth", camera.depth,
				"cullingMask", camera.cullingMask,
				"near", Round(camera.nearClipPlane),
				"far", Round(camera.farClipPlane),
				"occlusionCulling", camera.useOcclusionCulling,
				"layerCullDistances", CullDistances(camera),
				"renderingPath", camera.actualRenderingPath.ToString(),
				"targetTexture", camera.targetTexture != null,
				"effects", Effects(camera)));
		}
		return Obj(
			"renderers", all.Total,
			"inView", all.InView,
			"drawn", all.Drawn,
			"vertices", all.Vertices,
			"drawnVertices", all.DrawnVertices,
			"layers", perLayer,
			"groups", (by.Length > 0) ? grouped : null,
			"renderTuning", Obj(
				"shadowCasters", CastleStoryPlus.Rendering.RenderTuning.Managed,
				"castersOff", CastleStoryPlus.Rendering.RenderTuning.CastersOff,
				"sun", (CastleStoryPlus.Rendering.RenderTuning.Sun != null) ? CastleStoryPlus.Rendering.RenderTuning.Sun.name : null),
			"lights", Lights(),
			"animators", Animators(),
			"cameras", cameras,
			"settings", Obj(
				"qualityLevel", QualitySettings.names[QualitySettings.GetQualityLevel()],
				"shadows", QualitySettings.shadows.ToString(),
				"shadowDistance", Round(QualitySettings.shadowDistance),
				"shadowCascades", QualitySettings.shadowCascades,
				"shadowResolution", QualitySettings.shadowResolution.ToString(),
				"vSyncCount", QualitySettings.vSyncCount,
				"targetFrameRate", Application.targetFrameRate,
				"lodBias", Round(QualitySettings.lodBias),
				"antiAliasing", QualitySettings.antiAliasing,
				"screen", Screen.width + "x" + Screen.height),
			"ambient", Ambient());
	}

	// The ambient light as set (the game's sky sets ambientLight every frame) and the probe the shaders get from it
	// (its constant term per colour, which is the flat colour times 1/(2 sqrt(pi)) = 0.2821 in Flat mode).
	private static object Ambient()
	{
		UnityEngine.Rendering.SphericalHarmonicsL2 probe = RenderSettings.ambientProbe;
		return Obj(
			"mode", RenderSettings.ambientMode.ToString(),
			"intensity", Round(RenderSettings.ambientIntensity),
			"light", new float[3] { Round(RenderSettings.ambientLight.r), Round(RenderSettings.ambientLight.g), Round(RenderSettings.ambientLight.b) },
			"probeL0", new float[3] { (float)System.Math.Round(probe[0, 0], 4), (float)System.Math.Round(probe[1, 0], 4), (float)System.Math.Round(probe[2, 0], 4) },
			"reflectionMode", RenderSettings.defaultReflectionMode.ToString(),
			"reflectionIntensity", Round(RenderSettings.reflectionIntensity),
			"fog", RenderSettings.fog,
			"lightsUseLinearIntensity", UnityEngine.Rendering.GraphicsSettings.lightsUseLinearIntensity,
			"colorSpace", QualitySettings.activeColorSpace.ToString());
	}

	// Point and spot lights are counted; directional lights and lights with shadows are listed.
	private static object Lights()
	{
		List<object> listed = new List<object>();
		int points = 0;
		int lit = 0;
		foreach (Light light in Object.FindObjectsOfType<Light>())
		{
			bool point = light.type == LightType.Point || light.type == LightType.Spot;
			if (point && light.isActiveAndEnabled)
			{
				points++;
				if (light.intensity > 0f)
				{
					lit++;
				}
			}
			if (point && light.shadows == LightShadows.None)
			{
				continue;
			}
			listed.Add(Obj(
				"name", light.name,
				"path", Path(light.transform),
				"type", light.type.ToString(),
				"shadows", light.shadows.ToString(),
				"intensity", Round(light.intensity),
				"color", new float[3] { Round(light.color.r), Round(light.color.g), Round(light.color.b) },
				"bounceIntensity", Round(light.bounceIntensity),
				"range", Round(light.range),
				"enabled", light.isActiveAndEnabled,
				"forward", Vec(light.transform.forward)));
		}
		return Obj(
			"pointAndSpotOn", points,
			"pointAndSpotLit", lit,
			"listed", listed);
	}

	// Animators by culling mode, and how many of them are on bricktrons (and enemies).
	private static object Animators()
	{
		Dictionary<string, object> byCulling = new Dictionary<string, object>();
		int total = 0;
		int onUnits = 0;
		Dictionary<string, int> counts = new Dictionary<string, int>();
		// Animators not on units, by name, parent's name, controller and culling.
		Dictionary<string, int> kinds = new Dictionary<string, int>();
		// The same for those that animate off the screen too.
		Dictionary<string, int> always = new Dictionary<string, int>();
		foreach (Animator animator in Object.FindObjectsOfType<Animator>())
		{
			if (!animator.isActiveAndEnabled)
			{
				continue;
			}
			total++;
			bool unit = animator.GetComponentInParent<Brix.Game.AI.Labor>() != null;
			if (unit)
			{
				onUnits++;
			}
			string key = animator.cullingMode + (unit ? " (units)" : " (other)");
			counts.TryGetValue(key, out int n);
			counts[key] = n + 1;
			if (!unit)
			{
				string controller = (animator.runtimeAnimatorController != null) ? animator.runtimeAnimatorController.name : "none";
				Transform parent = animator.transform.parent;
				string kind = Clean(animator.name) + " < " + ((parent != null) ? Clean(parent.name) : "-") + " [" + controller + ", " + animator.cullingMode
					+ (animator.GetComponentInChildren<Renderer>() != null ? ", renderer" : ", no renderer") + "]";
				kinds.TryGetValue(kind, out int k);
				kinds[kind] = k + 1;
				if (animator.cullingMode == AnimatorCullingMode.AlwaysAnimate)
				{
					always.TryGetValue(kind, out int a);
					always[kind] = a + 1;
				}
			}
		}
		foreach (KeyValuePair<string, int> count in counts)
		{
			byCulling[count.Key] = count.Value;
		}
		return Obj("active", total, "onUnits", onUnits, "byCulling", byCulling, "others", Kinds(kinds), "alwaysAnimate", Kinds(always));
	}

	private static List<object> Kinds(Dictionary<string, int> kinds)
	{
		List<KeyValuePair<string, int>> sorted = new List<KeyValuePair<string, int>>(kinds);
		sorted.Sort((KeyValuePair<string, int> a, KeyValuePair<string, int> b) => b.Value.CompareTo(a.Value));
		List<object> list = new List<object>();
		for (int i = 0; i < sorted.Count && i < 25; i++)
		{
			list.Add(Obj("kind", sorted[i].Key, "count", sorted[i].Value));
		}
		return list;
	}

	// A name without its "(Clone)" and " (12)" suffixes.
	private static string Clean(string name)
	{
		name = name.Replace("(Clone)", string.Empty).Trim();
		int open = name.LastIndexOf(" (", System.StringComparison.Ordinal);
		if (open > 0 && name.EndsWith(")", System.StringComparison.Ordinal))
		{
			name = name.Substring(0, open);
		}
		return name;
	}

	private static string Path(Transform t)
	{
		string path = t.name;
		for (Transform p = t.parent; p != null; p = p.parent)
		{
			path = p.name + "/" + path;
		}
		return path;
	}

	private static List<object> Effects(Camera camera)
	{
		List<object> effects = new List<object>();
		foreach (Behaviour behaviour in camera.GetComponents<Behaviour>())
		{
			if (behaviour == null || behaviour is Camera)
			{
				continue;
			}
			effects.Add(Obj("type", behaviour.GetType().Name, "enabled", behaviour.enabled));
		}
		return effects;
	}

	// Lights switched by POST, to put them back as they were.
	private static readonly Dictionary<Light, LightShadows> ShadowsBefore = new Dictionary<Light, LightShadows>();

	private static readonly List<Light> SwitchedOff = new List<Light>();

	private static object Set(ApiRequest request)
	{
		if (!string.IsNullOrEmpty(request.Get("linearIntensity")))
		{
			UnityEngine.Rendering.GraphicsSettings.lightsUseLinearIntensity = request.Bool("linearIntensity", false);
		}
		if (!string.IsNullOrEmpty(request.Get("ambientIntensity")))
		{
			RenderSettings.ambientIntensity = Mathf.Clamp(request.Float("ambientIntensity", 1f), 0f, 8f);
		}
		string effect = request.Get("effect");
		if (!string.IsNullOrEmpty(effect))
		{
			bool enabled = request.Bool("enabled", false);
			int found = 0;
			foreach (Camera camera in Camera.allCameras)
			{
				foreach (Behaviour behaviour in camera.GetComponents<Behaviour>())
				{
					if (behaviour != null && !(behaviour is Camera) && behaviour.GetType().Name == effect)
					{
						behaviour.enabled = enabled;
						found++;
					}
				}
			}
			if (found == 0)
			{
				throw new ApiException(404, "No image effect '" + effect + "' on the cameras");
			}
		}
		string shadows = request.Get("shadows");
		if (!string.IsNullOrEmpty(shadows))
		{
			try
			{
				QualitySettings.shadows = (ShadowQuality)System.Enum.Parse(typeof(ShadowQuality), shadows, true);
			}
			catch (System.ArgumentException)
			{
				throw new ApiException(400, "'shadows' must be Disable, HardOnly or All, was " + shadows);
			}
		}
		string lightShadows = request.Get("lightShadows");
		if (!string.IsNullOrEmpty(lightShadows))
		{
			if (request.Bool("lightShadows", true))
			{
				foreach (KeyValuePair<Light, LightShadows> before in ShadowsBefore)
				{
					if (before.Key != null)
					{
						before.Key.shadows = before.Value;
					}
				}
				ShadowsBefore.Clear();
			}
			else
			{
				foreach (Light light in Object.FindObjectsOfType<Light>())
				{
					if (light.type != LightType.Directional && light.shadows != LightShadows.None)
					{
						ShadowsBefore[light] = light.shadows;
						light.shadows = LightShadows.None;
					}
				}
			}
		}
		string pointLights = request.Get("pointLights");
		if (!string.IsNullOrEmpty(pointLights))
		{
			if (request.Bool("pointLights", true))
			{
				foreach (Light light in SwitchedOff)
				{
					if (light != null)
					{
						light.enabled = true;
					}
				}
				SwitchedOff.Clear();
			}
			else
			{
				foreach (Light light in Object.FindObjectsOfType<Light>())
				{
					if ((light.type == LightType.Point || light.type == LightType.Spot) && light.enabled)
					{
						light.enabled = false;
						SwitchedOff.Add(light);
					}
				}
			}
		}
		string instancing = request.Get("instancing");
		if (!string.IsNullOrEmpty(instancing))
		{
			bool on = request.Bool("instancing", false);
			HashSet<Material> seen = new HashSet<Material>();
			int changed = 0;
			foreach (MeshRenderer renderer in Object.FindObjectsOfType<MeshRenderer>())
			{
				foreach (Material material in renderer.sharedMaterials)
				{
					if (material != null && seen.Add(material) && material.enableInstancing != on)
					{
						material.enableInstancing = on;
						changed++;
					}
				}
			}
			return Obj("instancing", on, "materials", seen.Count, "changed", changed, "render", Get(request));
		}
		return Get(request);
	}

	private static long Vertices(Renderer renderer)
	{
		if (renderer is SkinnedMeshRenderer skinned)
		{
			return (skinned.sharedMesh != null) ? skinned.sharedMesh.vertexCount : 0;
		}
		MeshFilter filter = renderer.GetComponent<MeshFilter>();
		return (filter != null && filter.sharedMesh != null) ? filter.sharedMesh.vertexCount : 0;
	}

	// Only the layers with a distance of their own (0 means the camera's far plane).
	private static Dictionary<string, object> CullDistances(Camera camera)
	{
		float[] distances = camera.layerCullDistances;
		Dictionary<string, object> result = new Dictionary<string, object>();
		for (int i = 0; i < distances.Length; i++)
		{
			if (distances[i] > 0f)
			{
				result[LayerMask.LayerToName(i) is string name && name.Length > 0 ? name : i.ToString()] = Round(distances[i]);
			}
		}
		return result;
	}
}
