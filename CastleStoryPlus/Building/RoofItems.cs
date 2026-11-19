using System.Collections.Generic;
using Brix.Components;
using Brix.Engine;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Game.Components.Volumes;
using Brix.Game.Network;
using Brix.Game.Semantique;
using Brix.Lifecycle.Pooling;
using Brix.Utils;
using CastleStoryPlus.Core;
using CastleStoryPlus.Pathfinding;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.Building;

// Logs (felled trees roll and fall everywhere) and other loose items came to rest on the roofs of the mod's buildings
// (market, research station, smithy, armoury, warehouse), where no worker can stand: they stayed there for ever (#128).
// The host looks at the roof of the mod's large buildings every few seconds; a loose item that has lain still on it for a
// while is put on open ground next to the building (the nearest place workers can reach, else the nearest ground),
// where the cleanup picks it up. Generic: every building made by CustomBuilding that sets RoofCleanup is looked at,
// whatever its model.
[Feature(Features.RoofItems, Features.RoofItemsInfo)]
internal static class RoofItems
{
	private const float ScanSeconds = 2f;

	// An item must lie still on a roof this long before it is moved (a log still rolling off needs no help).
	private const float RestSeconds = 3f;

	// The roof zone: from this far under the building's top up to this far over it, and this far beyond its edges.
	private const float Depth = 0.4f;

	private const float Above = 3f;

	private const float Margin = 0.5f;

	// Same layers as the game's cleanup zones (and AutoCleanup): loose stones, free blocks and accessories.
	private const int Layers = 1073152;

	// How far from the building's edge the ground is looked for, in blocks.
	private const int MaxRing = 6;

	private static readonly Collider[] Hits = new Collider[256];

	private static readonly Dictionary<GameObject, float> Resting = new Dictionary<GameObject, float>();

	private static readonly HashSet<GameObject> Seen = new HashSet<GameObject>();

	private static readonly Dictionary<XYZ, int> Placed = new Dictionary<XYZ, int>();

	private static float _nextScan;

	// How many items were moved off roofs this game (for the tests and the log).
	internal static int Moved { get; private set; }

	private static void Enable()
	{
		Plugin.Root.AddComponent<RoofItemsDriver>();
		GameSession.OnLeave(() =>
		{
			Resting.Clear();
			Seen.Clear();
			Placed.Clear();
			Moved = 0;
		});
	}

	internal static void Update()
	{
		if (!NetworkServer.active || Time.time < _nextScan)
		{
			return;
		}
		_nextScan = Time.time + ScanSeconds;
		AutoList list = BrixSingleton<AutoList>.Instance;
		if (list == null)
		{
			return;
		}
		Seen.Clear();
		Placed.Clear();
		foreach (CustomBuilding building in CustomBuilding.All)
		{
			if (!building.RoofCleanup)
			{
				continue;
			}
			HashSet<GameObject> instances = list.GetInstances(building.BuildingKey);
			if (instances == null)
			{
				continue;
			}
			foreach (GameObject go in instances)
			{
				if (go != null && go.activeInHierarchy)
				{
					ScanRoof(go);
				}
			}
		}
		// Items no longer on a roof (moved, picked up) are forgotten.
		List<GameObject> gone = null;
		foreach (KeyValuePair<GameObject, float> pair in Resting)
		{
			if (pair.Key == null || !Seen.Contains(pair.Key))
			{
				if (gone == null)
				{
					gone = new List<GameObject>();
				}
				gone.Add(pair.Key);
			}
		}
		if (gone != null)
		{
			foreach (GameObject item in gone)
			{
				Resting.Remove(item);
			}
		}
	}

	// The loose items lying on the roof of the building now (the scan's check, also used by the tests).
	internal static List<GameObject> ItemsOnRoof(GameObject building)
	{
		List<GameObject> result = new List<GameObject>();
		BoxCollider box = building.GetComponent<BoxCollider>();
		if (box == null)
		{
			return result;
		}
		Bounds bounds = box.bounds;
		Vector3 center = new Vector3(bounds.center.x, bounds.max.y + (Above - Depth) / 2f, bounds.center.z);
		Vector3 half = new Vector3(bounds.extents.x + Margin, (Above + Depth) / 2f, bounds.extents.z + Margin);
		int count = Physics.OverlapBoxNonAlloc(center, half, Hits, Quaternion.identity, Layers);
		for (int i = 0; i < count; i++)
		{
			GameObject item = SignificantParent.Of(Hits[i].gameObject);
			if (item == null || item == building || result.Contains(item) || !IsLooseItem(item))
			{
				continue;
			}
			Vector3 p = item.transform.position;
			if (RoofRules.OnRoof(bounds.min.x, bounds.max.x, bounds.min.z, bounds.max.z, bounds.max.y, p.x, p.y, p.z, Margin, Depth, Above))
			{
				result.Add(item);
			}
		}
		System.Array.Clear(Hits, 0, count);
		return result;
	}

	private static void ScanRoof(GameObject building)
	{
		foreach (GameObject item in ItemsOnRoof(building))
		{
			Seen.Add(item);
			if (!IsStill(item))
			{
				Resting.Remove(item);
				continue;
			}
			if (!Resting.TryGetValue(item, out float since))
			{
				Resting[item] = Time.time;
			}
			else if (Time.time - since >= RestSeconds)
			{
				MoveToGround(building, item);
				Resting.Remove(item);
			}
		}
	}

	// A physical item lying free: not inside a storage, not carried or worn, not a building or a unit.
	private static bool IsLooseItem(GameObject go)
	{
		if (go.IsNullOrReleased() || !go.activeInHierarchy || go.GetComponent<Labor>() != null || go.GetComponent<Blueprint>() != null)
		{
			return false;
		}
		Rigidbody body = go.GetComponent<Rigidbody>();
		if (body == null || body.isKinematic)
		{
			return false;
		}
		Transform parent = go.transform.parent;
		return parent == null || (parent.GetComponentInParent<Recepteur>() == null && parent.GetComponentInParent<Labor>() == null);
	}

	private static bool IsStill(GameObject item)
	{
		Rigidbody body = item.GetComponent<Rigidbody>();
		return body != null && (body.IsSleeping() || body.velocity.sqrMagnitude < 0.04f);
	}

	// Puts the item on the nearest open ground beside the building.
	internal static bool MoveToGround(GameObject building, GameObject item)
	{
		BoxCollider box = building.GetComponent<BoxCollider>();
		if (box == null || item.IsNullOrReleased())
		{
			return false;
		}
		Bounds bounds = box.bounds;
		int minX = Mathf.FloorToInt(bounds.min.x + 0.01f);
		int maxX = Mathf.FloorToInt(bounds.max.x - 0.01f);
		int minZ = Mathf.FloorToInt(bounds.min.z + 0.01f);
		int maxZ = Mathf.FloorToInt(bounds.max.z - 0.01f);
		int baseY = Mathf.FloorToInt(bounds.min.y + 0.5f);
		Faction faction = Faction.GetFaction(building);
		bool mapped = faction != null && ReachMap.Has(faction);
		Vector3 from = item.transform.position;
		bool found = false;
		bool foundReachable = false;
		XYZ bestColumn = XYZ.zero;
		float bestSurface = 0f;
		float bestDistance = float.MaxValue;
		for (int ring = 1; ring <= MaxRing && !foundReachable; ring++)
		{
			for (int x = minX - ring; x <= maxX + ring; x++)
			{
				for (int z = minZ - ring; z <= maxZ + ring; z++)
				{
					if (RoofRules.Ring(minX, maxX, minZ, maxZ, x, z) != ring || !Ground(x, z, baseY, out float surface))
					{
						continue;
					}
					XYZ stand = new XYZ(x, Mathf.FloorToInt(surface) + 1, z);
					bool reachable = !mapped || ReachMap.Near(faction, stand, 1, 2, 1);
					float distance = (new Vector3(x + 0.5f, 0f, z + 0.5f) - new Vector3(from.x, 0f, from.z)).sqrMagnitude;
					// A reachable place beats any unreachable one; then the nearest to the item.
					if (!found || (reachable && !foundReachable) || (reachable == foundReachable && distance < bestDistance))
					{
						found = true;
						foundReachable = reachable;
						bestColumn = new XYZ(x, 0, z);
						bestSurface = surface;
						bestDistance = distance;
					}
				}
			}
		}
		if (!found)
		{
			Plugin.Log.LogWarning("RoofItems: no open ground next to " + building.name + " for " + item.name);
			return false;
		}
		Placed.TryGetValue(bestColumn, out int slot);
		Placed[bestColumn] = slot + 1;
		float ox = ((slot & 1) == 0) ? -0.25f : 0.25f;
		float oz = ((slot & 2) == 0) ? -0.25f : 0.25f;
		Vector3 at = new Vector3(bestColumn.x + 0.5f + ox, bestSurface + 0.3f + (slot / 4) * 0.35f, bestColumn.z + 0.5f + oz);
		item.transform.position = at;
		item.transform.rotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
		item.Deposer();
		item.SetActive(true);
		// The item's synced position follows at once: its transform snaps back to the last synced one when it is more
		// than 10 away from it (UNetTransform.BrixUpdate).
		UNetTransform synced = item.GetComponent<UNetTransform>();
		if (synced != null)
		{
			synced.NetworkLastServerPosition = at;
			synced.NetworkLastServerRotation = item.transform.rotation;
		}
		Rigidbody body = item.GetComponent<Rigidbody>();
		if (body != null && !body.isKinematic)
		{
			body.velocity = Vector3.zero;
			body.angularVelocity = Vector3.zero;
		}
		Moved++;
		Plugin.Log.LogInfo("RoofItems: " + item.name + " moved from the roof of " + building.name + " to the ground at " + at);
		return true;
	}

	// The top of the ground in the column near the building's foot (not the top of a wall), with room over it.
	private static bool Ground(int x, int z, int y, out float surface)
	{
		surface = 0f;
		if (!Architecte.VoxelDessus(new XZ(x, z), out XYZ top, y - 3, y + 2))
		{
			return false;
		}
		XYZ above = new XYZ(x, top.y + 1, z);
		if (top.y > y + 1 || !Voxel.IsEmpty(above) || !Voxel.IsEmpty(above + XYZ.up))
		{
			return false;
		}
		surface = top.y + 0.5f;
		return true;
	}
}

internal class RoofItemsDriver : MonoBehaviour
{
	private void Update()
	{
		try
		{
			RoofItems.Update();
		}
		catch (System.Exception ex)
		{
			Plugin.Log.LogWarning("RoofItems: " + ex.Message);
			enabled = false;
		}
	}
}
