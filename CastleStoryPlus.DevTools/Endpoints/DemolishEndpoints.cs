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
	}

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
