using CastleStoryPlus.Core;
using System;
using System.Collections.Generic;
using Brix.External.Factories;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Game.Semantique;
using CastleStoryPlus.DevTools.Api;
using UnityEngine;
using static CastleStoryPlus.DevTools.Endpoints.Json;
using Object = UnityEngine.Object;

namespace CastleStoryPlus.DevTools.Endpoints;

// What storages (stockpiles, warehouses, racks) hold: the content description by resource type, with the names
// the game's UI shows for them and the room for each, and the stored objects by type.
internal static class StorageEndpoints
{
	internal static void Add(List<Route> routes)
	{
		routes.Add(new Route("GET", "/api/storage", "Storages and what they hold: amounts per resource (type and shown name) with the room for it, the top of what is drawn (world height), and the stored objects by type", Storage)
		{
			Parameters = "q (part of the storage's type name, e.g. Warehouse, Palette), empty (true: also empty ones), max (default 50)"
		});
		routes.Add(new Route("GET", "/api/storage/trackers", "Items near a place and what holds them, as this player sees it: the item's parent, the holder its network tracker names (object, kind, network id) and whether that holder took it in; for comparing host and client", Trackers)
		{
			Parameters = "at (x,y,z, required), radius (default 6), max (default 200)"
		});
	}

	private static object Trackers(ApiRequest request)
	{
		Vector3 at = request.Vector("at");
		float radius = request.Float("radius", 6f);
		int max = Mathf.Clamp(request.Int("max", 200), 1, 2000);
		List<object> result = new List<object>();
		Dictionary<string, int> summary = new Dictionary<string, int>();
		// Also the hidden ones: what a storage or blueprint holds is switched off.
		foreach (Brix.Game.Network.GenericParentTracker tracker in Resources.FindObjectsOfTypeAll<Brix.Game.Network.GenericParentTracker>())
		{
			if (tracker == null || !tracker.gameObject.scene.IsValid() || tracker.GetComponent<Labor>() != null || (tracker.transform.position - at).sqrMagnitude > radius * radius)
			{
				continue;
			}
			GameObject holder = tracker.TrackingParent();
			UnityEngine.Networking.NetworkIdentity identity = (holder != null) ? holder.GetComponent<UnityEngine.Networking.NetworkIdentity>() : null;
			Recepteur recepteur = (holder != null) ? holder.GetComponent<Recepteur>() : null;
			IDescriptor descriptor = tracker.GetComponent<IDescriptor>();
			bool stored = recepteur != null && descriptor != null && recepteur.StoredItems.Contains(descriptor);
			FactoryImprint imprint = tracker.GetComponent<FactoryImprint>();
			string type = (imprint != null && imprint.AssetKey != null) ? (imprint.AssetKey.Factory + "." + imprint.AssetKey.Name) : tracker.name;
			string key = type + " in " + ((holder != null) ? holder.name : "nothing") + (stored ? " (stored)" : string.Empty) + (tracker.gameObject.activeSelf ? string.Empty : " (hidden)") + ", under " + Path(tracker.transform.parent, 3);
			summary.TryGetValue(key, out int count);
			summary[key] = count + 1;
			if (result.Count < max)
			{
				UnityEngine.Networking.NetworkIdentity own = tracker.GetComponent<UnityEngine.Networking.NetworkIdentity>();
				List<int> observers = new List<int>();
				if (own != null && own.observers != null)
				{
					foreach (UnityEngine.Networking.NetworkConnection connection in own.observers)
					{
						observers.Add(connection.connectionId);
					}
				}
				List<object> visibleFor = new List<object>();
				LocalFactionVision vision = tracker.GetComponent<LocalFactionVision>();
				if (UnityEngine.Networking.NetworkServer.active && vision != null && Brix.Network.UNetManager.instance != null)
				{
					foreach (Brix.Game.User user in Brix.Network.UNetManager.instance.UserList)
					{
						if (user != null && user.faction != null)
						{
							visibleFor.Add(Obj("connection", (user.connectionToClient != null) ? (object)user.connectionToClient.connectionId : null, "faction", user.faction.name, "visible", vision.IsVisibleForFaction(user.faction)));
						}
					}
				}
				result.Add(Obj(
					"netId", (own != null) ? (object)own.netId.Value : null,
					"visibleFor", visibleFor,
					"faction", (Brix.Game.Faction.GetFaction(tracker.gameObject) != null) ? Brix.Game.Faction.GetFaction(tracker.gameObject).name : null,
					"observers", UnityEngine.Networking.NetworkServer.active ? observers : null,
					"id", tracker.gameObject.InstanceId(),
					"type", type,
					"tracker", tracker.GetType().Name,
					"position", Vec(tracker.transform.position),
					"parent", Path(tracker.transform.parent, 3),
					"holder", (holder != null) ? holder.name : null,
					"holderNetId", (identity != null) ? (object)identity.netId.Value : null,
					"stored", stored,
					"active", tracker.gameObject.activeSelf));
			}
		}
		return Obj("count", result.Count, "summary", summary, "items", result);
	}

	// A transform's name with up to levels of its parents, outermost first.
	private static string Path(Transform transform, int levels)
	{
		string path = null;
		for (int i = 0; i < levels && transform != null; i++)
		{
			path = (path == null) ? transform.name : (transform.name + "/" + path);
			transform = transform.parent;
		}
		return path ?? "nothing";
	}

	private static object Storage(ApiRequest request)
	{
		string filter = request.String("q", string.Empty);
		bool withEmpty = request.Bool("empty", false);
		int max = Mathf.Clamp(request.Int("max", 50), 1, 500);
		List<object> result = new List<object>();
		foreach (Recepteur recepteur in Object.FindObjectsOfType<Recepteur>())
		{
			if (recepteur == null || recepteur.GetComponent<Labor>() != null)
			{
				continue;
			}
			FactoryImprint imprint = recepteur.GetComponent<FactoryImprint>();
			string type = (imprint != null && imprint.AssetKey != null) ? (imprint.AssetKey.Factory + "." + imprint.AssetKey.Name) : recepteur.name;
			if (filter.Length > 0 && type.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
			{
				continue;
			}
			if (!withEmpty && recepteur.IsEmpty())
			{
				continue;
			}
			List<object> contents = new List<object>();
			foreach (KeyValuePair<Type, Adjectif> pair in recepteur.ContentDescription.DicoAdjectif)
			{
				Adjectif adjectif = pair.Value;
				if (adjectif == null || adjectif.quantifiable == null || adjectif.quantifiable.valeur == 0)
				{
					continue;
				}
				string shown = null;
				Ressource resource = adjectif as Ressource;
				if (resource != null)
				{
					AdjectiveInfo info = resource.GetKey().GetInfo();
					shown = (info != null) ? info.descriptiveName : null;
				}
				contents.Add(Obj(
					"key", pair.Key.Name,
					"class", adjectif.GetType().FullName,
					"resource", resource != null,
					"amount", adjectif.quantifiable.valeur,
					"room", recepteur.BaseCapacity.Value(adjectif),
					"shownName", shown));
			}
			Dictionary<string, int> objects = new Dictionary<string, int>();
			foreach (IDescriptor item in recepteur.StoredItems)
			{
				FactoryImprint itemImprint = (item != null && item.GameObject != null) ? item.GameObject.GetComponent<FactoryImprint>() : null;
				string itemType = (itemImprint != null && itemImprint.AssetKey != null) ? (itemImprint.AssetKey.Factory + "." + itemImprint.AssetKey.Name) : ((item != null && item.GameObject != null) ? item.GameObject.name : "?");
				objects.TryGetValue(itemType, out int count);
				objects[itemType] = count + 1;
			}
			float top = float.MinValue;
			foreach (Renderer renderer in recepteur.GetComponentsInChildren<Renderer>())
			{
				if (renderer.enabled && renderer.gameObject.activeInHierarchy && !(renderer is ParticleSystemRenderer))
				{
					top = Mathf.Max(top, renderer.bounds.max.y);
				}
			}
			result.Add(Obj(
				"id", recepteur.gameObject.InstanceId(),
				"type", type,
				"position", Vec(recepteur.transform.position),
				"drawnTop", (top > float.MinValue) ? (object)Round(top) : null,
				"contents", contents,
				"objects", objects));
			if (result.Count >= max)
			{
				break;
			}
		}
		return Obj("count", result.Count, "storages", result);
	}
}
