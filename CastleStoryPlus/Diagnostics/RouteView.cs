using System.Collections.Generic;
using Brix.Engine;
using Brix.Game.AI;
using Brix.Pathfinding;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.Diagnostics;

// Debug menu "Show routes": draws the route searches of the selected bricktrons in the world. The game first finds
// a route over coarse terrain blocks (orange: the blocks' representative voxels), then straightens it in fine detail
// from the start (yellow), and the walker unrolls the rest voxel by voxel (green: the route that is walked). Only
// searches that end while the bricktron is selected are drawn; each bricktron keeps its last one.
internal class RouteView : MonoBehaviour
{
	private sealed class Route
	{
		public readonly List<Vector3> Coarse = new List<Vector3>();

		public readonly List<Vector3> Improved = new List<Vector3>();

		public readonly List<Vector3> Final = new List<Vector3>();

		public LineRenderer CoarseLine;

		public LineRenderer ImprovedLine;

		public LineRenderer FinalLine;
	}

	internal static readonly Color CoarseColor = new Color(1f, 0.5f, 0.1f, 1f);

	internal static readonly Color ImprovedColor = new Color(1f, 0.9f, 0.2f, 1f);

	internal static readonly Color FinalColor = new Color(0.2f, 1f, 0.4f, 1f);

	// Feet height over a walkable voxel, a little lifted so the line is not hidden in the floor.
	private const float Lift = -0.3f;

	internal static bool Enabled;

	private static RouteView _instance;

	private static Material _material;

	private static readonly HashSet<Labor> Selected = new HashSet<Labor>();

	private static readonly Dictionary<Labor, Route> Routes = new Dictionary<Labor, Route>();

	internal static string Summary = string.Empty;

	internal static void Toggle()
	{
		Enabled = !Enabled;
		if (_instance == null)
		{
			_instance = Plugin.Root.AddComponent<RouteView>();
		}
		if (!Enabled)
		{
			Clear();
		}
	}

	private static void Clear()
	{
		foreach (Route route in Routes.Values)
		{
			Destroy(route.CoarseLine);
			Destroy(route.ImprovedLine);
			Destroy(route.FinalLine);
		}
		Routes.Clear();
		Selected.Clear();
		Summary = string.Empty;
	}

	private static void Destroy(LineRenderer line)
	{
		if (line != null)
		{
			Object.Destroy(line.gameObject);
		}
	}

	internal static bool Watched(SearchPathRequest request)
	{
		return Enabled && request != null && request.Navigation != null && request.Navigation.Labor != null && Selected.Contains(request.Navigation.Labor);
	}

	internal static void Coarse(SearchPathRequest request)
	{
		Route route = For(request.Navigation.Labor);
		route.Coarse.Clear();
		route.Improved.Clear();
		route.Final.Clear();
		Points(request.SuccessLink, route.Coarse);
	}

	internal static void Concluded(SearchPathRequest request)
	{
		Route route = For(request.Navigation.Labor);
		if (!request.Succeeded)
		{
			route.Improved.Clear();
			route.Final.Clear();
			return;
		}
		route.Improved.Clear();
		Points(request.SuccessLink, route.Improved);
		route.Final.Clear();
		if (request.PathUnroller != null)
		{
			// Unrolls the whole route now, as the walker would on its way.
			List<PathNode> nodes = new List<PathNode>();
			request.PathUnroller.GetResultNodes(nodes);
			foreach (PathNode node in nodes)
			{
				route.Final.Add(Point(node.RepresentativePosition));
			}
		}
		Summary = request.Navigation.Labor.name + ": coarse " + route.Coarse.Count + " nodes, straightened " + route.Improved.Count + ", walked " + route.Final.Count + " voxels";
	}

	private static void Points(PathLink link, List<Vector3> result)
	{
		for (PathLink at = link; at != null; at = at.tail)
		{
			if (at.head.Node != null)
			{
				result.Add(Point(at.head.Node.RepresentativePosition));
			}
		}
	}

	private static Vector3 Point(XYZ voxel)
	{
		return voxel.ToVector3() + new Vector3(0f, Lift, 0f);
	}

	private static Route For(Labor labor)
	{
		if (!Routes.TryGetValue(labor, out Route route))
		{
			route = new Route();
			Routes[labor] = route;
		}
		return route;
	}

	private void Update()
	{
		if (!Enabled)
		{
			return;
		}
		Selected.Clear();
		if (UIGameObserver.bricktrons != null)
		{
			foreach (Labor labor in UIGameObserver.bricktrons.CheckSelection())
			{
				if (labor != null)
				{
					Selected.Add(labor);
				}
			}
		}
		List<Labor> gone = null;
		foreach (KeyValuePair<Labor, Route> pair in Routes)
		{
			if (pair.Key == null)
			{
				if (gone == null)
				{
					gone = new List<Labor>();
				}
				gone.Add(pair.Key);
				continue;
			}
			bool shown = Selected.Contains(pair.Key);
			Draw(ref pair.Value.CoarseLine, pair.Value.Coarse, CoarseColor, 0.08f, shown, 0.1f);
			Draw(ref pair.Value.ImprovedLine, pair.Value.Improved, ImprovedColor, 0.12f, shown, 0.2f);
			Draw(ref pair.Value.FinalLine, pair.Value.Final, FinalColor, 0.16f, shown, 0.3f);
		}
		if (gone != null)
		{
			foreach (Labor labor in gone)
			{
				Destroy(Routes[labor].CoarseLine);
				Destroy(Routes[labor].ImprovedLine);
				Destroy(Routes[labor].FinalLine);
				Routes.Remove(labor);
			}
		}
	}

	// The three lines at slightly different heights, so they can be told apart where they run together.
	private static void Draw(ref LineRenderer line, List<Vector3> points, Color color, float width, bool shown, float offset)
	{
		if (!shown || points.Count < 2)
		{
			if (line != null)
			{
				line.enabled = false;
			}
			return;
		}
		if (line == null)
		{
			GameObject go = new GameObject("CastleStoryPlusRoute");
			line = go.AddComponent<LineRenderer>();
			if (_material == null)
			{
				Shader shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
				_material = new Material(shader);
			}
			line.sharedMaterial = _material;
			line.useWorldSpace = true;
			line.startWidth = width;
			line.endWidth = width;
			line.startColor = color;
			line.endColor = color;
		}
		line.enabled = true;
		line.positionCount = points.Count;
		for (int i = 0; i < points.Count; i++)
		{
			line.SetPosition(i, points[i] + new Vector3(0f, offset, 0f));
		}
	}
}

// The coarse route, before it is straightened.
[Feature(Features.DebugMenu, Features.DebugMenuInfo)]
[HarmonyPatch(typeof(SearchPathRequest), "InitImprove")]
internal static class RouteViewCoarsePatch
{
	private static void Postfix(SearchPathRequest __instance)
	{
		if (RouteView.Watched(__instance) && __instance.SuccessLink != null)
		{
			RouteView.Coarse(__instance);
		}
	}
}

[Feature(Features.DebugMenu, Features.DebugMenuInfo)]
[HarmonyPatch(typeof(SearchPathRequest), "ConcludeSearch")]
internal static class RouteViewConcludePatch
{
	private static void Postfix(SearchPathRequest __instance)
	{
		if (RouteView.Watched(__instance))
		{
			RouteView.Concluded(__instance);
		}
	}
}
