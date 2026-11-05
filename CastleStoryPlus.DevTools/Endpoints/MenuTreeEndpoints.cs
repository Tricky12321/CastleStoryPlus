using System.Collections.Generic;
using Brix.UI.Builder.Menu;
using CastleStoryPlus.DevTools.Api;
using UnityEngine;
using static CastleStoryPlus.DevTools.Endpoints.Json;

namespace CastleStoryPlus.DevTools.Endpoints;

// The game's scene-built menus (title screen and in-game options): the menu components and their containers, the
// containers' items (operation, title, the objects they open or close), and any object's children with their
// components. Inactive objects too, so a page that is not shown can be looked at.
internal static class MenuTreeEndpoints
{
	internal static void Add(List<Route> routes)
	{
		routes.Add(new Route("GET", "/api/ui/menus", "The game's menu components (LegacyBaseMenu) in the loaded scenes, shown or not: path, type, active, and an enum menu's row, list and action containers", Menus)
		{
			Parameters = "q (part of the path or type, case ignored)"
		});
		routes.Add(new Route("GET", "/api/ui/containers", "The game's menu containers whose path holds q, shown or not, with their items: operation, title, group, instance path, and the actions (open, close...) with their targets", Containers)
		{
			Parameters = "q (part of the path, case ignored; required), max (default 20)"
		});
		routes.Add(new Route("GET", "/api/ui/tree", "An object's children (shown or not) with their components and active state, by (part of) its path", Tree)
		{
			Parameters = "q (part of the path, case ignored; required), depth (default 3), index (which match, default 0)"
		});
	}

	internal static string PathOf(Transform t)
	{
		if (t == null)
		{
			return null;
		}
		string path = t.name;
		while (t.parent != null)
		{
			t = t.parent;
			path = t.name + "/" + path;
		}
		return path;
	}

	private static bool InScene(Component c)
	{
		return c != null && c.gameObject.scene.IsValid();
	}

	private static bool Matches(string text, string q)
	{
		return string.IsNullOrEmpty(q) || (text != null && text.IndexOf(q, System.StringComparison.OrdinalIgnoreCase) >= 0);
	}

	private static object Menus(ApiRequest request)
	{
		string q = request.String("q", null);
		List<object> list = new List<object>();
		foreach (LegacyBaseMenu menu in Resources.FindObjectsOfTypeAll<LegacyBaseMenu>())
		{
			if (!InScene(menu))
			{
				continue;
			}
			string path = PathOf(menu.transform);
			if (!Matches(path, q) && !Matches(menu.GetType().Name, q))
			{
				continue;
			}
			Dictionary<string, object> entry = Obj("path", path, "type", menu.GetType().Name, "active", menu.gameObject.activeInHierarchy, "enabled", menu.enabled);
			if (menu is LegacyBaseEnumMenu e)
			{
				entry["rows"] = (e.preModalContainer != null) ? PathOf(e.preModalContainer.transform) : null;
				entry["list"] = (e.enumContainer != null) ? PathOf(e.enumContainer.transform) : null;
				entry["actions"] = (e.actionContainer != null) ? PathOf(e.actionContainer.transform) : null;
				entry["source"] = (e.sourceEnumButton != null) ? PathOf(e.sourceEnumButton.transform) : null;
			}
			if (menu is LegacyOptionCategoriesMenu c)
			{
				entry["tabs"] = (c.TabsContainer != null) ? PathOf(c.TabsContainer.transform) : null;
			}
			list.Add(entry);
		}
		return Obj("count", list.Count, "menus", list);
	}

	private static object Containers(ApiRequest request)
	{
		string q = request.String("q", null);
		if (string.IsNullOrEmpty(q))
		{
			throw new System.ArgumentException("'q' is missing");
		}
		int max = request.Int("max", 20);
		List<object> list = new List<object>();
		foreach (Container container in Resources.FindObjectsOfTypeAll<Container>())
		{
			if (!InScene(container) || list.Count >= max)
			{
				continue;
			}
			string path = PathOf(container.transform);
			if (!Matches(path, q))
			{
				continue;
			}
			List<object> items = new List<object>();
			foreach (Container.Item item in container.items)
			{
				List<object> actions = new List<object>();
				if (item.actionItems != null)
				{
					foreach (Container.Item.ActionItem action in item.actionItems)
					{
						if (action != null)
						{
							actions.Add(Obj("action", action.action.ToString(), "target", (action.target != null) ? PathOf(action.target.transform) : null));
						}
					}
				}
				items.Add(Obj("op", item.containerPopulatorOperation.ToString(), "title", item.titleText, "group", item.group.ToString(), "dynamic", item.isDynamic,
					"instance", (item.instance != null) ? PathOf(item.instance.transform) : null, "original", (item.original != null) ? PathOf(item.original.transform) : null, "actions", actions));
			}
			list.Add(Obj("path", path, "active", container.gameObject.activeInHierarchy, "items", items));
		}
		return Obj("count", list.Count, "containers", list);
	}

	private static object Tree(ApiRequest request)
	{
		string q = request.String("q", null);
		if (string.IsNullOrEmpty(q))
		{
			throw new System.ArgumentException("'q' is missing");
		}
		int depth = request.Int("depth", 3);
		int index = request.Int("index", 0);
		List<Transform> found = new List<Transform>();
		foreach (Transform t in Resources.FindObjectsOfTypeAll<Transform>())
		{
			if (InScene(t) && Matches(PathOf(t), q) && (t.parent == null || !Matches(PathOf(t.parent), q)))
			{
				found.Add(t);
			}
		}
		if (index >= found.Count)
		{
			return Obj("matches", found.Count, "tree", null);
		}
		return Obj("matches", found.Count, "tree", Node(found[index], depth));
	}

	private static object Node(Transform t, int depth)
	{
		List<string> components = new List<string>();
		foreach (Component c in t.GetComponents<Component>())
		{
			if (c != null && !(c is Transform))
			{
				components.Add(c.GetType().Name);
			}
		}
		Dictionary<string, object> node = Obj("name", t.name, "active", t.gameObject.activeSelf, "components", components);
		UnityEngine.UI.Text text = t.GetComponent<UnityEngine.UI.Text>();
		if (text != null)
		{
			node["text"] = text.text;
		}
		RectTransform rect = t as RectTransform;
		if (rect != null)
		{
			node["rect"] = new float[] { Round(rect.anchorMin.x), Round(rect.anchorMin.y), Round(rect.anchorMax.x), Round(rect.anchorMax.y), Round(rect.anchoredPosition.x), Round(rect.anchoredPosition.y), Round(rect.sizeDelta.x), Round(rect.sizeDelta.y) };
		}
		if (depth > 0 && t.childCount > 0)
		{
			List<object> children = new List<object>();
			foreach (Transform child in t)
			{
				children.Add(Node(child, depth - 1));
			}
			node["children"] = children;
		}
		else if (t.childCount > 0)
		{
			node["childCount"] = t.childCount;
		}
		return node;
	}
}
