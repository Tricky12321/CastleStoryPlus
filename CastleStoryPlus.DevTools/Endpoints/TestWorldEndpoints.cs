using CastleStoryPlus.Core;
using System.Collections.Generic;
using Brix.Engine;
using Brix.Game;
using Brix.Game.Components;
using Brix.UI.GameSelector;
using CastleStoryPlus.DevTools.Api;
using CastleStoryPlus.DevTools.Worlds;
using UnityEngine;
using UnityEngine.Networking;
using static CastleStoryPlus.DevTools.Endpoints.Json;
using Object = UnityEngine.Object;

namespace CastleStoryPlus.DevTools.Endpoints;

// The test world (one of every building and block, stockpiles of every resource; Worlds/TestWorld) and selecting a
// building, as a click on it does, to look at its menu.
internal static class TestWorldEndpoints
{
	internal static void Add(List<Route> routes)
	{
		routes.Add(new Route("POST", "/api/testworld", "Lays out a test world in the running game (a sandbox is meant) near the home crystal on a flat stone floor: one of every structure in rows by the build menu's groups, every block by group from small to big, and a stockpile for every resource filled with it. Answers at once; GET /api/testworld follows it. Save the game afterwards to keep it", Build)
		{
			Parameters = "width (of the site in voxels, default 120, 32 to 160)"
		});
		routes.Add(new Route("GET", "/api/testworld", "How the test world's building goes: state, site, what was placed or refused, items stored", Status));
		routes.Add(new Route("GET", "/api/testworld/pieces", "The test world's pieces as laid out: name, section, centre (x,y,z on the floor), size, and for a stockpile its resource and Stacking tier, for a stand its kits (e.g. to photograph each one)", Pieces));
#if !ESSENTIALS
		routes.Add(new Route("GET", "/api/studio/plan", "The photo studio's pieces: the test world's plan (every building, block, stand and stockpile with its resource and Stacking tier), by index", StudioPlan));
#endif
#if !ESSENTIALS
		routes.Add(new Route("POST", "/api/studio", "Shows one piece of the test world's plan alone on the photo studio's spot (west of the home crystal): the last one taken away, this one built at once and filled. Answers at once; GET /api/studio follows it and gives the piece's drawn bounds when ready", StudioShow)
		{
			Parameters = "index (from GET /api/studio/plan, required)"
		});
#endif
#if !ESSENTIALS
		routes.Add(new Route("GET", "/api/studio", "The photo studio: state (ready when the piece stands), its spot, the piece and its drawn bounds (centre, size)", StudioStatus));
#endif
		routes.Add(new Route("GET", "/api/tree", "An object's children as a tree: name, active, scale and renderers (mesh, materials); for a stockpile also its display tracks (resources, pieces shown). Find the object by id, or the nearest storage to a point", Tree)
		{
			Parameters = "id (object id), at (x,y,z: the nearest storage to it instead), depth (default 4)"
		});
		routes.Add(new Route("POST", "/api/select", "Selects a building as a click on it does (its menu opens): by object id, or the nearest to the home crystal whose type contains q (e.g. MarketStation, ResearchStation, SmithyStation, Loom)", Select)
		{
			Parameters = "id (object id), q (part of the station's type name)"
		});
	}

	private static object Build(ApiRequest request)
	{
		if (!NetworkServer.active || User.LocalUser == null || User.LocalUser.faction == null || !Architecte.commence || Architecte.termine)
		{
			throw new ApiException(409, "No game in progress on this host (start a sandbox: POST /api/world/new)");
		}
		int width = Mathf.Clamp(request.Int("width", 120), 32, 160);
		TestWorldBuilder.Instance.Begin(width);
		return TestWorldBuilder.Instance.Status();
	}

	private static object Status(ApiRequest request)
	{
		return TestWorldBuilder.Instance.Status();
	}

#if !ESSENTIALS
	private static object StudioPlan(ApiRequest request)
	{
		return TestWorldBuilder.Instance.StudioPlan();
	}

#endif
#if !ESSENTIALS
	private static object StudioShow(ApiRequest request)
	{
		if (!NetworkServer.active || User.LocalUser == null || User.LocalUser.faction == null || !Architecte.commence || Architecte.termine)
		{
			throw new ApiException(409, "No game in progress on this host (start a sandbox: POST /api/world/new)");
		}
		return TestWorldBuilder.Instance.StudioShow(request.Int("index", -1));
	}

#endif
#if !ESSENTIALS
	private static object StudioStatus(ApiRequest request)
	{
		return TestWorldBuilder.Instance.StudioStatus();
	}

#endif
	private static object Pieces(ApiRequest request)
	{
		return TestWorldBuilder.Instance.PieceList();
	}

	private static object Tree(ApiRequest request)
	{
		GameObject target = null;
		int id = request.Int("id", 0);
		if (id != 0)
		{
			foreach (GameObject go in Object.FindObjectsOfType<GameObject>())
			{
				if (go.InstanceId() == id)
				{
					target = go;
					break;
				}
			}
		}
		else
		{
			Vector3 at = request.Vector("at");
			float best = 9f;
			foreach (Brix.Game.Semantique.Recepteur storage in Object.FindObjectsOfType<Brix.Game.Semantique.Recepteur>())
			{
				float distance = (storage.transform.position - at).sqrMagnitude;
				if (distance < best && storage.GetComponent<RecepteurTracksDisplay>() != null || distance < best && storage.name.Contains("Palette"))
				{
					best = distance;
					target = storage.gameObject;
				}
			}
		}
		if (target == null)
		{
			throw new ApiException(404, "Nothing found");
		}
		List<object> tracks = new List<object>();
		foreach (RecepteurTracksDisplay display in target.GetComponentsInChildren<RecepteurTracksDisplay>(true))
		{
			foreach (RecepteurTracksDisplay.DisplayTrack track in display.DisplayTracks)
			{
				int shown = 0;
				List<string> names = new List<string>();
				foreach (GameObject go in track.gos)
				{
					if (go != null)
					{
						names.Add(go.name);
						if (go.activeSelf)
						{
							shown++;
						}
					}
				}
				tracks.Add(Obj("resources", track.adjectif, "valid", track.Valid, "pieces", track.gos.Count, "shown", shown, "first", (names.Count > 0) ? names[0] : null));
			}
		}
		return Obj("name", target.name, "id", target.InstanceId(), "tracks", tracks, "tree", Node(target.transform, request.Int("depth", 4)));
	}

	private static object Node(Transform t, int depth)
	{
		List<object> children = new List<object>();
		if (depth > 0)
		{
			foreach (Transform child in t)
			{
				children.Add(Node(child, depth - 1));
			}
		}
		Renderer renderer = t.GetComponent<Renderer>();
		MeshFilter filter = t.GetComponent<MeshFilter>();
		List<string> materials = new List<string>();
		if (renderer != null)
		{
			foreach (Material material in renderer.sharedMaterials)
			{
				materials.Add((material != null) ? material.name : "null");
			}
		}
		return Obj(
			"name", t.name,
			"active", t.gameObject.activeSelf,
			"scale", (t.localScale != Vector3.one) ? new float[3] { t.localScale.x, t.localScale.y, t.localScale.z } : null,
			"mesh", (filter != null && filter.sharedMesh != null) ? filter.sharedMesh.name : null,
			"renderer", (renderer != null) ? (object)Obj("enabled", renderer.enabled, "materials", materials) : null,
			"children", (children.Count > 0) ? children : null,
			"more", (depth <= 0 && t.childCount > 0) ? (int?)t.childCount : null);
	}

	private static object Select(ApiRequest request)
	{
		if (User.LocalUser == null || User.LocalUser.faction == null)
		{
			throw new ApiException(409, "No game in progress");
		}
		GameObject target = null;
		int id = request.Int("id", 0);
		string q = request.String("q", null);
		if (id != 0)
		{
			foreach (GameObject go in Object.FindObjectsOfType<GameObject>())
			{
				if (go.InstanceId() == id)
				{
					target = go;
					break;
				}
			}
		}
		else if (!string.IsNullOrEmpty(q))
		{
			Vector3 home = WorldEndpoints.HomeVoxel().ToVector3();
			float best = float.MaxValue;
			foreach (CraftingStation station in Object.FindObjectsOfType<CraftingStation>())
			{
				if (station == null || station.GetType().Name.IndexOf(q, System.StringComparison.OrdinalIgnoreCase) < 0 || Faction.GetFaction(station.gameObject) != User.LocalUser.faction)
				{
					continue;
				}
				float distance = (station.transform.position - home).sqrMagnitude;
				if (distance < best)
				{
					best = distance;
					target = station.gameObject;
				}
			}
		}
		else
		{
			throw new ApiException(400, "Give id or q");
		}
		if (target == null)
		{
			throw new ApiException(404, "Nothing found to select");
		}
		UIGameSelector.Select(target);
		return Obj("selected", target.name, "id", target.InstanceId(), "position", target.transform.position);
	}
}
