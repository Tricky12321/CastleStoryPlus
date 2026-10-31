using System.Collections.Generic;
using CastleStoryPlus.DevTools.Api;
using UnityEngine;
using static CastleStoryPlus.DevTools.Endpoints.Json;

namespace CastleStoryPlus.DevTools.Endpoints;

// What the interface draws at a point of the screen: every canvas element whose rectangle holds it, front first, with
// its canvas, colour, material and texture, to find which element draws something it should not (a dark plate).
internal static class UiEndpoints
{
	internal static void Add(List<Route> routes)
	{
		routes.Add(new Route("GET", "/api/ui/at", "The interface elements drawn at a screen point (every active CanvasRenderer whose rectangle holds it, also those that take no clicks), front first: path, canvas (render mode, sort order), components, colour, alpha, material, texture and rectangle on the screen", At)
		{
			Parameters = "x, y (pixels from the top left, as /api/ui/rect and screenshots count; required), max (default 40)"
		});
		routes.Add(new Route("GET", "/api/ui/canvases", "The canvases that draw (active and enabled), grouped by path (clone numbers left out): how many, how many are root canvases, and their canvas renderers", Canvases)
		{
			Parameters = "depth (path segments a group keeps, default 4)"
		});
		routes.Add(new Route("POST", "/api/ui/click", "Clicks a shown uGUI button (the mod's code-built windows) by its text, as a mouse click does: the first active, interactable button whose label contains the text", Click)
		{
			Parameters = "text (part of the button's label, case ignored; required), index (which match in reading order on the screen, default 0)"
		});
		routes.Add(new Route("GET", "/api/ui/overheads", "The overhead icons by kind (their parent's name): how many, how many show, how many of their canvases are on, how many are close enough to show", Overheads));
	}

	private static object Click(ApiRequest request)
	{
		string text = request.String("text", null);
		if (string.IsNullOrEmpty(text))
		{
			throw new System.ArgumentException("'text' is missing");
		}
		int index = request.Int("index", 0);
		List<string> labels = new List<string>();
		// In reading order on the screen (top to bottom, then left to right), so an index names the same button each time.
		List<UnityEngine.UI.Button> buttons = new List<UnityEngine.UI.Button>(Object.FindObjectsOfType<UnityEngine.UI.Button>());
		buttons.Sort((UnityEngine.UI.Button a, UnityEngine.UI.Button b) =>
		{
			Vector3 pa = a.transform.position;
			Vector3 pb = b.transform.position;
			return (Mathf.Abs(pa.y - pb.y) > 1f) ? pb.y.CompareTo(pa.y) : pa.x.CompareTo(pb.x);
		});
		foreach (UnityEngine.UI.Button button in buttons)
		{
			UnityEngine.UI.Text label = button.GetComponentInChildren<UnityEngine.UI.Text>();
			if (!button.isActiveAndEnabled || !button.IsInteractable() || label == null || label.text.IndexOf(text, System.StringComparison.OrdinalIgnoreCase) < 0)
			{
				continue;
			}
			labels.Add(label.text);
			if (labels.Count - 1 == index)
			{
				button.onClick.Invoke();
				return Obj("clicked", label.text, "path", button.name);
			}
		}
		return Obj("clicked", null, "matches", labels);
	}

	// The canvases that draw (active and enabled), grouped by their path with numbers and names of clones left out, to
	// see which kind of interface element makes Unity batch and draw hundreds of canvases a frame.
	private static object Canvases(ApiRequest request)
	{
		int depth = request.Int("depth", 4);
		Dictionary<string, int[]> groups = new Dictionary<string, int[]>();
		int total = 0;
		int drawing = 0;
		foreach (Canvas canvas in Object.FindObjectsOfType<Canvas>())
		{
			total++;
			if (!canvas.isActiveAndEnabled)
			{
				continue;
			}
			drawing++;
			string key = Group(canvas.transform, depth);
			if (!groups.TryGetValue(key, out int[] counts))
			{
				counts = new int[3];
				groups[key] = counts;
			}
			counts[0]++;
			if (canvas.isRootCanvas)
			{
				counts[1]++;
			}
			counts[2] += canvas.GetComponentsInChildren<CanvasRenderer>(false).Length;
		}
		List<KeyValuePair<string, int[]>> sorted = new List<KeyValuePair<string, int[]>>(groups);
		sorted.Sort((KeyValuePair<string, int[]> a, KeyValuePair<string, int[]> b) => b.Value[0].CompareTo(a.Value[0]));
		List<object> list = new List<object>();
		foreach (KeyValuePair<string, int[]> pair in sorted)
		{
			list.Add(Obj("group", pair.Key, "canvases", pair.Value[0], "root", pair.Value[1], "renderers", pair.Value[2]));
		}
		return Obj("canvases", total, "drawing", drawing, "groups", list);
	}

	// The overhead icons (over units, stockpiles, buildings) by kind: how many show, and which of their canvases draw.
	private static object Overheads(ApiRequest request)
	{
		Dictionary<string, int[]> groups = new Dictionary<string, int[]>();
		foreach (Brix.NewUI.OverheadDisplay.OverheadDisplay display in Object.FindObjectsOfType<Brix.NewUI.OverheadDisplay.OverheadDisplay>())
		{
			string key = display.transform.parent != null ? display.transform.parent.name : "-";
			if (!groups.TryGetValue(key, out int[] counts))
			{
				counts = new int[8];
				groups[key] = counts;
			}
			counts[0]++;
			CanvasGroup content = display.Content != null ? display.Content.GetComponent<CanvasGroup>() : null;
			if (content != null && content.alpha > 0f)
			{
				counts[1]++;
			}
			if (display.MainCanvas != null && display.MainCanvas.enabled)
			{
				counts[2]++;
			}
			if (display.ArrowCanvas != null && display.ArrowCanvas.enabled)
			{
				counts[3]++;
			}
			foreach (Canvas canvas in display.GetComponentsInChildren<Canvas>(false))
			{
				if (canvas.enabled)
				{
					counts[4]++;
				}
			}
			if (display.IsCloseEnough)
			{
				counts[5]++;
			}
			if (display.MainCanvas == display.GetComponent<Canvas>())
			{
				counts[6]++;
			}
			if (display.Content == display.ArrowCanvas?.transform || (display.ArrowCanvas != null && display.ArrowCanvas.transform.IsChildOf(display.Content)))
			{
				counts[7]++;
			}
		}
		List<object> list = new List<object>();
		foreach (KeyValuePair<string, int[]> pair in groups)
		{
			int[] c = pair.Value;
			list.Add(Obj("group", pair.Key, "displays", c[0], "showing", c[1], "mainCanvasOn", c[2], "arrowCanvasOn", c[3], "canvasesOn", c[4], "closeEnough", c[5], "mainIsOwn", c[6], "arrowInContent", c[7]));
		}
		return Obj("groups", list);
	}

	private static string Group(Transform transform, int depth)
	{
		List<string> names = new List<string>();
		for (Transform t = transform; t != null; t = t.parent)
		{
			string name = t.name;
			int cut = name.IndexOf(" (", System.StringComparison.Ordinal);
			names.Insert(0, cut > 0 ? name.Substring(0, cut) : name);
		}
		if (names.Count > depth)
		{
			names.RemoveRange(depth, names.Count - depth);
		}
		return string.Join("/", names.ToArray());
	}

	private static object At(ApiRequest request)
	{
		float x = request.Float("x", -1f);
		float y = request.Float("y", -1f);
		if (x < 0f || y < 0f)
		{
			throw new System.ArgumentException("x and y are required");
		}
		int max = request.Int("max", 40);
		Vector2 point = new Vector2(x, Screen.height - y);
		List<KeyValuePair<long, object>> found = new List<KeyValuePair<long, object>>();
		foreach (CanvasRenderer renderer in Object.FindObjectsOfType<CanvasRenderer>())
		{
			if (!renderer.gameObject.activeInHierarchy || renderer.cull)
			{
				continue;
			}
			RectTransform rect = renderer.transform as RectTransform;
			Canvas canvas = renderer.GetComponentInParent<Canvas>();
			if (rect == null || canvas == null || !canvas.isActiveAndEnabled)
			{
				continue;
			}
			Camera camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
			if (!RectTransformUtility.RectangleContainsScreenPoint(rect, point, camera))
			{
				continue;
			}
			Canvas root = canvas.rootCanvas;
			Material material = renderer.materialCount > 0 ? renderer.GetMaterial() : null;
			Texture texture = material != null && material.HasProperty("_MainTex") ? material.mainTexture : null;
			List<string> components = new List<string>();
			foreach (Component component in renderer.GetComponents<Component>())
			{
				if (component != null && !(component is Transform) && !(component is CanvasRenderer))
				{
					components.Add(component.GetType().Name);
				}
			}
			Vector3[] corners = new Vector3[4];
			rect.GetWorldCorners(corners);
			Vector2 min = RectTransformUtility.WorldToScreenPoint(camera, corners[0]);
			Vector2 maxCorner = RectTransformUtility.WorldToScreenPoint(camera, corners[2]);
			// Front first: the root canvas's sort order, then the element's depth in its canvas.
			long order = ((long)root.sortingOrder << 32) + renderer.absoluteDepth;
			found.Add(new KeyValuePair<long, object>(order, Obj(
				"path", Path(rect),
				"canvas", root.name + " (" + root.renderMode + ", order " + root.sortingOrder + ")",
				"depth", renderer.absoluteDepth,
				"components", components,
				"color", renderer.GetColor().ToString(),
#if UNITY6
				"alpha", Round(renderer.GetInheritedAlpha()),
#else
				// 5.6 has no inherited alpha (the CanvasGroups above are not counted).
				"alpha", Round(renderer.GetAlpha()),
#endif
				"material", material != null ? material.name + " (" + material.shader.name + ")" : null,
				"texture", texture != null ? texture.name + " " + texture.width + "x" + texture.height : null,
				"screenRect", Mathf.RoundToInt(min.x) + "," + Mathf.RoundToInt(Screen.height - maxCorner.y) + " " + Mathf.RoundToInt(maxCorner.x - min.x) + "x" + Mathf.RoundToInt(maxCorner.y - min.y))));
		}
		found.Sort((a, b) => b.Key.CompareTo(a.Key));
		List<object> elements = new List<object>();
		for (int i = 0; i < found.Count && i < max; i++)
		{
			elements.Add(found[i].Value);
		}
		return Obj("x", x, "y", y, "count", found.Count, "elements", elements);
	}

	private static string Path(Transform transform)
	{
		string path = transform.name;
		for (Transform parent = transform.parent; parent != null; parent = parent.parent)
		{
			path = parent.name + "/" + path;
		}
		return path;
	}
}
