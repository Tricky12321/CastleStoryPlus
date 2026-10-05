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
// the game's UI shows for them, and the stored objects by type.
internal static class StorageEndpoints
{
	internal static void Add(List<Route> routes)
	{
		routes.Add(new Route("GET", "/api/storage", "Storages and what they hold: amounts per resource (type and shown name) and the stored objects by type", Storage)
		{
			Parameters = "q (part of the storage's type name, e.g. Warehouse, Palette), empty (true: also empty ones), max (default 50)"
		});
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
			result.Add(Obj(
				"id", recepteur.gameObject.GetInstanceID(),
				"type", type,
				"position", Vec(recepteur.transform.position),
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
