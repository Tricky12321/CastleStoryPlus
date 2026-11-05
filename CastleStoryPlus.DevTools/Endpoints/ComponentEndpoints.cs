using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using CastleStoryPlus.DevTools.Api;
using UnityEngine;
using UnityEngine.SceneManagement;
using static CastleStoryPlus.DevTools.Endpoints.Json;
using Object = UnityEngine.Object;

namespace CastleStoryPlus.DevTools.Endpoints;

// Every live component of a type, by its type's name: where it sits (path, scene, active, enabled), to find
// duplicates (two EventSystems, two audio listeners) and leftovers; with fields=true also the values of their serialized
// fields and their local transform, to compare what the game's assets hold on 5.6 and in the Unity 6 port. POST switches
// them off and on, to measure what a kind of component costs a frame.
internal static class ComponentEndpoints
{
	internal static void Add(List<Route> routes)
	{
		routes.Add(new Route("GET", "/api/components", "Live components whose type's name is q (or ends with .q): path, scene, active, enabled and the other components on the object; also inactive ones", Get)
		{
			Parameters = "q (type name, e.g. EventSystem or UnityEngine.EventSystems.EventSystem; required), max (default 50), path (only objects whose path holds this text), fields (true: the serialized fields' values and the local position, rotation and scale)"
		});
		routes.Add(new Route("POST", "/api/components", "Switches the live components (Behaviour, Renderer or Collider) whose type's name is q on or off, for measuring what they cost, or puts back what an earlier call switched; until the game restarts. A game component updated by the game's own clock keeps its update when switched off", Set)
		{
			Parameters = "q (type name; required), path (only objects whose path holds this text), enabled (true, false, or restore: back as they were before the first switch)"
		});
	}

	// What each switched component was before the first switch, for restore.
	private static readonly Dictionary<Component, bool> Before = new Dictionary<Component, bool>();

	private static object Set(ApiRequest request)
	{
		string q = request.Required("q");
		string path = request.String("path", null);
		string enabled = request.Required("enabled");
		bool restore = enabled == "restore";
		bool on = !restore && request.Bool("enabled", true);
		int matched = 0;
		int changed = 0;
		foreach (Component component in Resources.FindObjectsOfTypeAll<Component>())
		{
			if (component == null || component.gameObject.scene.name == null || !Matches(component.GetType(), q))
			{
				continue;
			}
			if (path != null && Path(component.transform).IndexOf(path, StringComparison.OrdinalIgnoreCase) < 0)
			{
				continue;
			}
			bool? current = Enabled(component);
			if (current == null)
			{
				continue;
			}
			matched++;
			bool want;
			if (restore)
			{
				if (!Before.TryGetValue(component, out want))
				{
					continue;
				}
				Before.Remove(component);
			}
			else
			{
				if (!Before.ContainsKey(component))
				{
					Before[component] = current.Value;
				}
				want = on;
			}
			if (current.Value != want)
			{
				SetEnabled(component, want);
				changed++;
			}
		}
		return Obj("q", q, "matched", matched, "changed", changed, "remembered", Before.Count);
	}

	private static bool? Enabled(Component component)
	{
		if (component is Behaviour behaviour)
		{
			return behaviour.enabled;
		}
		if (component is Renderer renderer)
		{
			return renderer.enabled;
		}
		if (component is Collider collider)
		{
			return collider.enabled;
		}
		return null;
	}

	private static void SetEnabled(Component component, bool value)
	{
		if (component is Behaviour behaviour)
		{
			behaviour.enabled = value;
		}
		else if (component is Renderer renderer)
		{
			renderer.enabled = value;
		}
		else if (component is Collider collider)
		{
			collider.enabled = value;
		}
	}

	private static object Get(ApiRequest request)
	{
		string q = request.Required("q");
		int max = request.Int("max", 50);
		string path = request.String("path", null);
		bool fields = request.Bool("fields", false);
		List<object> found = new List<object>();
		int total = 0;
		foreach (Component component in Resources.FindObjectsOfTypeAll<Component>())
		{
			if (component == null || component.gameObject.scene.name == null || !Matches(component.GetType(), q))
			{
				continue;
			}
			if (path != null && Path(component.transform).IndexOf(path, StringComparison.OrdinalIgnoreCase) < 0)
			{
				continue;
			}
			total++;
			if (found.Count >= max)
			{
				continue;
			}
			List<string> others = new List<string>();
			foreach (Component other in component.GetComponents<Component>())
			{
				if (other != null && other != component && !(other is Transform))
				{
					others.Add(other.GetType().Name);
				}
			}
			Scene scene = component.gameObject.scene;
			found.Add(Obj(
				"path", Path(component.transform),
				"type", component.GetType().FullName,
				"scene", scene.IsValid() ? scene.name : null,
				"active", component.gameObject.activeInHierarchy,
				"enabled", Enabled(component),
				"others", others,
				"transform", fields ? LocalTransform(component.transform) : null,
				"fields", fields ? Fields(component) : null));
		}
		return Obj("q", q, "count", total, "components", found);
	}

	private static object LocalTransform(Transform transform)
	{
		return Obj(
			"position", Text(transform.localPosition),
			"rotation", Text(transform.localEulerAngles),
			"scale", Text(transform.localScale));
	}

	// The fields Unity serializes (public, or marked SerializeField, not NonSerialized), of the type and its bases
	// up to MonoBehaviour, as text.
	private static object Fields(Component component)
	{
		Dictionary<string, object> values = new Dictionary<string, object>();
		for (Type t = component.GetType(); t != null && t != typeof(MonoBehaviour) && t != typeof(Component); t = t.BaseType)
		{
			foreach (FieldInfo field in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
			{
				bool serialized = (field.IsPublic || field.IsDefined(typeof(SerializeField), true)) && !field.IsNotSerialized;
				if (!serialized || values.ContainsKey(field.Name))
				{
					continue;
				}
				object value;
				try
				{
					value = Text(field.GetValue(component));
				}
				catch (Exception e)
				{
					value = "(" + e.GetType().Name + ")";
				}
				values[field.Name] = value;
			}
		}
		return values;
	}

	private static string Text(object value)
	{
		if (value == null)
		{
			return null;
		}
		if (value is Object unityObject)
		{
			return unityObject != null ? unityObject.name + " (" + unityObject.GetType().Name + ")" : "null";
		}
		if (value is Matrix4x4 matrix)
		{
			return Text(matrix.GetRow(0)) + " | " + Text(matrix.GetRow(1)) + " | " + Text(matrix.GetRow(2)) + " | " + Text(matrix.GetRow(3));
		}
		if (value is Vector4 v4)
		{
			return F(v4.x) + " " + F(v4.y) + " " + F(v4.z) + " " + F(v4.w);
		}
		if (value is Vector3 v3)
		{
			return F(v3.x) + " " + F(v3.y) + " " + F(v3.z);
		}
		if (value is IFormattable formattable)
		{
			return formattable.ToString(null, CultureInfo.InvariantCulture);
		}
		return value.ToString();
	}

	private static string F(float value)
	{
		return value.ToString("0.###", CultureInfo.InvariantCulture);
	}

	private static bool Matches(Type type, string q)
	{
		for (Type t = type; t != null && t != typeof(Component); t = t.BaseType)
		{
			if (t.Name == q || t.FullName == q)
			{
				return true;
			}
		}
		return false;
	}

	internal static string Path(Transform transform)
	{
		string path = transform.name;
		for (Transform parent = transform.parent; parent != null; parent = parent.parent)
		{
			path = parent.name + "/" + path;
		}
		return path;
	}
}
