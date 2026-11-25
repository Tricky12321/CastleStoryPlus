using CastleStoryPlus.Core;
using System;
using System.Collections.Generic;
using Brix.Engine;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Game.Components.Volumes;
using Brix.Game.Factories;
using Brix.Game.Network;
using CastleStoryPlus.Building;
using CastleStoryPlus.DevTools.Api;
using UnityEngine;
using UnityEngine.Networking;
using static CastleStoryPlus.DevTools.Endpoints.Json;
#if !ESSENTIALS
using DemolishTask = CastleStoryPlus.Building.Demolish;
#endif
using Object = UnityEngine.Object;

namespace CastleStoryPlus.DevTools.Endpoints;

// Taking down placed buildings (stockpiles, workshops, doors, ...) as the remove tool does (UNetBlueprint.CmdPlaceAnti):
// a demolition blueprint the workers of a build project carry out, or at once with instant.
internal static class DemolishEndpoints
{
	internal static void Add(List<Route> routes)
	{
		routes.Add(new Route("POST", "/api/demolish", "Takes down the player's placed buildings (stockpiles, workshops, doors, ...) in a box or by id, as the remove tool does: the workers of a build project demolish them, or at once with instant", Demolish)
		{
			Parameters = "from, to (x,y,z: the box; a building counts when its position is inside), ids (comma separated object ids instead), project (build project id; default as for placing), instant (true: cheat, gone at once)"
		});
#if !ESSENTIALS
		routes.Add(new Route("POST", "/api/demolish/mark", "Marks a building for demolition as the demolish key and a click do (the mod's Demolish task: a builder takes it down and carries its materials to storage), or cancels the mark. Works on the host and on a client (sent to the host, which decides; a client's answer is only 'sent')", Mark)
		{
			Parameters = "at (x,y,z: the building nearest to it within 2 blocks, required), mark (false: cancel; default true). No check that it is the player's own: the host refuses another player's building"
		});
		routes.Add(new Route("GET", "/api/demolish/marks", "The buildings with a demolition blueprint over them as this player sees them (and on the host whether the Demolish task holds them, with what each gives back), and the demolition leftovers still to carry", Marks)
		{
			Parameters = "own (true: also list the player's own buildings that can be marked)"
		});
#endif
	}

#if !ESSENTIALS
	// The placed building (any player's) nearest to the point, within 2 blocks.
	private static GameObject BuildingNear(Vector3 at)
	{
		GameObject best = null;
		float bestDistance = 4f;
		foreach (MovableVolume volume in Object.FindObjectsOfType<MovableVolume>())
		{
			if (volume == null || DemolishTask.DemolishKey(volume.gameObject) == null)
			{
				continue;
			}
			float distance = (volume.transform.position - at).sqrMagnitude;
			if (distance < bestDistance)
			{
				best = volume.gameObject;
				bestDistance = distance;
			}
		}
		return best;
	}

	private static void NeedGame()
	{
		if (!NetworkServer.active && !NetworkClient.active)
		{
			throw new ApiException(409, "No game in progress");
		}
		if (User.LocalUser == null || User.LocalUser.faction == null || !Architecte.commence || Architecte.termine)
		{
			throw new ApiException(409, "No game in progress (or still loading)");
		}
	}

	private static object Mark(ApiRequest request)
	{
		NeedGame();
		Vector3 at = request.Vector("at");
		bool mark = request.Bool("mark", true);
		GameObject building = BuildingNear(at);
		if (building == null)
		{
			throw new ApiException(404, "No building that can be demolished within 2 blocks of " + at);
		}
		string refused = DemolishTask.Request(building, mark);
		return Obj("building", Describe(building), "mark", mark, "host", NetworkServer.active, "done", refused == null, "refused", refused);
	}

	private static object Marks(ApiRequest request)
	{
		NeedGame();
		List<object> marks = new List<object>();
		foreach (Blueprint blueprint in Object.FindObjectsOfType<Blueprint>())
		{
			if (blueprint == null || blueprint.Released || !blueprint.IsAnti)
			{
				continue;
			}
			GameObject building = DemolishTask.BuildingOf(blueprint);
			if (building == null)
			{
				continue;
			}
			List<object> refund = new List<object>();
			foreach (KeyValuePair<Factory.AssetKey, int> entry in DemolishTask.RefundOf(building))
			{
				refund.Add(Obj("item", entry.Key.Name, "count", entry.Value));
			}
			Dictionary<string, object> described = Describe(building);
			described["task"] = DemolishTask.IsMarked(building);
			described["refund"] = refund;
			marks.Add(described);
		}
		List<object> own = null;
		if (request.Bool("own", false))
		{
			own = new List<object>();
			foreach (MovableVolume volume in Object.FindObjectsOfType<MovableVolume>())
			{
				if (volume != null && DemolishTask.DemolishKey(volume.gameObject) != null && User.LocalUser.faction.IsSame(volume.gameObject))
				{
					own.Add(Describe(volume.gameObject));
				}
			}
		}
		return Obj("count", marks.Count, "marks", marks, "task", NetworkServer.active ? (int?)DemolishTask.Count : null, "leftovers", NetworkServer.active ? (int?)DemolishHaul.Count : null, "own", own);
	}

	private static Dictionary<string, object> Describe(GameObject building)
	{
		FactoryImprint imprint = building.GetComponent<FactoryImprint>();
		return Obj("id", building.InstanceId(), "type", (imprint != null && imprint.AssetKey != null) ? imprint.AssetKey.Name : building.name, "position", Vec(building.transform.position), "own", User.LocalUser.faction.IsSame(building));
	}
#endif

	private static object Demolish(ApiRequest request)
	{
		if (!NetworkServer.active)
		{
			throw new ApiException(409, "Only on the host (single player or hosting a game)");
		}
		if (User.LocalUser == null || User.LocalUser.faction == null || !Architecte.commence || Architecte.termine)
		{
			throw new ApiException(409, "No game in progress (or still loading)");
		}
		HashSet<int> ids = new HashSet<int>();
		foreach (string part in request.String("ids", string.Empty).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
		{
			ids.Add(int.Parse(part.Trim()));
		}
		bool byBox = ids.Count == 0;
		XYZ min = new XYZ(0, 0, 0);
		XYZ max = new XYZ(0, 0, 0);
		if (byBox)
		{
			XYZ from = XYZ.FromVector3(request.Vector("from"));
			XYZ to = XYZ.FromVector3(request.Vector("to"));
			min = new XYZ(Mathf.Min(from.x, to.x), Mathf.Min(from.y, to.y), Mathf.Min(from.z, to.z));
			max = new XYZ(Mathf.Max(from.x, to.x), Mathf.Max(from.y, to.y), Mathf.Max(from.z, to.z));
		}
		bool instant = request.Bool("instant", false);
		Project project = instant ? null : ProjectEndpoints.BuildProject(request.Int("project", 0), out bool _);
		UNetBlueprint net = User.LocalUser.GetComponent<UNetBlueprint>();
		List<object> done = new List<object>();
		foreach (MovableVolume volume in Object.FindObjectsOfType<MovableVolume>())
		{
			GameObject building = volume.gameObject;
#if !ESSENTIALS
			if (!CopyPaste.IsPlacedBuilding(volume) || building.GetComponent<Blueprint>() != null || !User.LocalUser.faction.IsSame(building))
#else
			if (building.GetComponent<Blueprint>() != null || !User.LocalUser.faction.IsSame(building))
#endif
			{
				continue;
			}
			if (byBox)
			{
				XYZ at = XYZ.FromVector3(building.transform.position);
				if (at.x < min.x || at.y < min.y || at.z < min.z || at.x > max.x || at.y > max.y || at.z > max.z)
				{
					continue;
				}
			}
			else if (!ids.Contains(building.InstanceId()))
			{
				continue;
			}
			FactoryImprint imprint = building.GetComponent<FactoryImprint>();
			Factory.AssetKey key = (imprint != null) ? BlueprintAssetKeyResolver.Resolve(imprint.AssetKey) : null;
			if (key.IsNullOrInvalid())
			{
				continue;
			}
			net.CmdPlaceAnti(key.Factory, key.Name, building.transform.position, building.transform.rotation, instant, (project != null) ? project.gameObject : null);
			done.Add(Obj("id", building.InstanceId(), "type", key.Name, "position", Vec(building.transform.position)));
		}
		if (project != null && done.Count > 0)
		{
			ProjectEndpoints.Commit(project);
		}
		return Obj("demolished", done.Count, "instant", instant, "buildings", done);
	}
}
