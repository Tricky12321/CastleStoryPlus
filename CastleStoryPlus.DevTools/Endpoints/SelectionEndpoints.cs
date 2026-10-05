using System.Collections.Generic;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Game.Components.Volumes;
using Brix.Game.Network;
using CastleStoryPlus.Building;
using CastleStoryPlus.DevTools.Api;
using UnityEngine;
using static CastleStoryPlus.DevTools.Endpoints.Json;
using Object = UnityEngine.Object;

namespace CastleStoryPlus.DevTools.Endpoints;

// What the player has selected, and moving a building (the mod's MoveStructure: a blueprint at the new place
// and the old one demolished by the workers).
internal static class SelectionEndpoints
{
	internal static void Add(List<Route> routes)
	{
		routes.Add(new Route("GET", "/api/selection", "What the player has selected: bricktrons, wards, objects, workshop, project; with ids, positions and whether a thing can be moved", Selection));
		routes.Add(new Route("POST", "/api/move", "Moves a building (as the mod's move key does): its blueprint at the new position, the old one demolished by the workers of a build project", Move)
		{
			Parameters = "id (object id, required), to (x,y,z world position of the building, required), turn (degrees round the vertical, default: as it is), project (build project id; default: the last selected build project)"
		});
	}

	private static object Selection(ApiRequest request)
	{
		if (UIGameObserver.wards == null)
		{
			throw new ApiException(409, "No game in progress");
		}
		List<object> selected = new List<object>();
		foreach (WardController ward in UIGameObserver.wards.wardsSelected)
		{
			if (ward != null)
			{
				selected.Add(Describe("ward", ward.gameObject));
			}
		}
		foreach (Labor labor in UIGameObserver.bricktrons.CheckSelection())
		{
			if (labor != null)
			{
				selected.Add(Describe("bricktron", labor.gameObject));
			}
		}
		AddAll(selected, "object", UIGameObserver.dynamicObjects.AllSelected);
		AddAll(selected, "block", UIGameObserver.placedBlocks.AllSelected);
		AddAll(selected, "storage", UIGameObserver.storage.AllSelected);
		if (UIGameObserver.crafting.CurrentSelected != null)
		{
			selected.Add(Describe("workshop", UIGameObserver.crafting.CurrentSelected.gameObject));
		}
		if (UIGameObserver.projects.CurrentSelected != null)
		{
			selected.Add(Describe("project", UIGameObserver.projects.CurrentSelected.gameObject));
		}
		Project build = BuildProject(0);
		return Obj("selected", selected, "buildProject", (build != null) ? Describe("project", build.gameObject) : null);
	}

	private static void AddAll(List<object> list, string kind, IEnumerable<Transform> transforms)
	{
		foreach (Transform t in transforms)
		{
			if (t != null)
			{
				list.Add(Describe(kind, t.gameObject));
			}
		}
	}

	private static Dictionary<string, object> Describe(string kind, GameObject go)
	{
		FactoryImprint imprint = go.GetComponent<FactoryImprint>();
		MovableVolume volume = go.GetComponent<MovableVolume>();
		return Obj(
			"kind", kind,
			"id", go.GetInstanceID(),
			"name", go.name,
			"type", (imprint != null && imprint.AssetKey != null) ? (imprint.AssetKey.Factory + "." + imprint.AssetKey.Name) : null,
			"position", Vec(go.transform.position),
			"turn", Round(go.transform.eulerAngles.y),
			"movable", volume != null && CopyPaste.IsPlacedBuilding(volume) && go.GetComponent<Blueprint>() == null);
	}

	private static object Move(ApiRequest request)
	{
		int id = request.Int("id", 0);
		GameObject building = FindById(id);
		if (building == null)
		{
			throw new ApiException(404, "No object with id " + id);
		}
		Vector3 to = request.Vector("to");
		float turn = request.Float("turn", building.transform.eulerAngles.y);
		Project project = BuildProject(request.Int("project", 0));
		if (project == null)
		{
			throw new ApiException(409, "No build project to move it with (make one, or select one)");
		}
		string problem = MoveStructure.MoveTo(building, to, Quaternion.Euler(0f, turn, 0f), project.gameObject);
		if (problem != null)
		{
			throw new ApiException(409, problem);
		}
		return Obj("ordered", true, "building", Describe("building", building), "to", Vec(to), "turn", turn, "project", project.name);
	}

	private static GameObject FindById(int id)
	{
		foreach (FactoryImprint imprint in Object.FindObjectsOfType<FactoryImprint>())
		{
			if (imprint != null && imprint.gameObject.GetInstanceID() == id)
			{
				return imprint.gameObject;
			}
		}
		return null;
	}

	// The build project by id, else the last selected one, else any of the player's.
	private static Project BuildProject(int id)
	{
		Faction local = (User.LocalUser != null) ? User.LocalUser.faction : null;
		if (UIGameObserver.projects == null || local == null)
		{
			return null;
		}
		Project last = UIGameObserver.projects.LastSelectedBuild;
		if (id == 0 && IsBuild(last, local))
		{
			return last;
		}
		foreach (Project project in UIGameObserver.projects.all)
		{
			if (IsBuild(project, local) && (id == 0 || project.gameObject.GetInstanceID() == id))
			{
				return project;
			}
		}
		return null;
	}

	private static bool IsBuild(Project project, Faction local)
	{
		return project != null && project.GetComponent<BuildGoalProvider>() != null && local.IsSame(project.gameObject);
	}
}
