using System.Collections.Generic;
using Brix.Engine;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.AI.Damage;
using Brix.Game.Components;
using Brix.Game.Semantique;
using Brix.Legacy;
using Brix.Lifecycle.Pooling;
using Brix.Utils;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.Building;

// A stockpile (or warehouse) that is demolished, by its owner's workers or by enemies, drops what it holds on the
// ground. The game destroyed the contents with it: its own drop looks for a free spot in only 4 places next to the
// stockpile (none in a row of stockpiles or against a wall), and even then left the items to be put down in a later
// frame, after the stockpile and its contents were gone.
[Feature(Features.StockpileSpill, Features.StockpileSpillInfo)]
[HarmonyPatch(typeof(ObjectDamageReceiver), nameof(ObjectDamageReceiver.Kill))]
internal static class StockpileSpill
{
	private static void Prefix(ObjectDamageReceiver __instance)
	{
		if (!NetworkServer.active || __instance == null)
		{
			return;
		}
		GameComponent component = __instance.GetComponent<GameComponent>();
		Recepteur recepteur = (component != null) ? component.recepteur : null;
		if (recepteur == null || !recepteur.CarriesSomething() || !IsStockpile(__instance.gameObject))
		{
			return;
		}
		List<GameObject> items = new List<GameObject>();
		foreach (IDescriptor item in recepteur.StoredItems)
		{
			if (item != null && item.GameObject != null)
			{
				items.Add(item.GameObject);
			}
		}
		if (items.Count == 0)
		{
			return;
		}
		// Out of the stockpile first (it lets go of each item as it is moved), then laid on the ground round it.
		ClasseMoi.Move(items);
		foreach (GameObject item in items)
		{
			Kept.Add(item);
		}
		ForgetInMergeback();
		LayOnGround(items, __instance.transform.position);
		Plugin.Log.LogInfo("StockpileSpill: " + __instance.name + " dropped " + items.Count + " items");
	}

	// Items dropped from a stockpile: the game sinks loose stones, ore and crystals back into the ground after half a
	// minute (meant for the gravel of digging); these are the player's resources and stay until picked up. An item
	// leaves the set when it goes back to the pool.
	internal static readonly HashSet<GameObject> Kept = new HashSet<GameObject>();

	private static void Enable()
	{
		GameSession.OnLeave(Kept.Clear);
	}

	// The merge-back keeps a list of the loose gravel it will sink or remove; kept items are taken off it (they
	// were put on it while stored), and StockpileSpillMergebackPatch keeps them off.
	private static void ForgetInMergeback()
	{
		Mergeback mergeback = Object.FindObjectOfType<Mergeback>();
		if (mergeback != null)
		{
			mergeback._garnottes.RemoveAll((Mergeback.GarnotteRegion region) => region.Value.garnotte != null && Kept.Contains(region.Value.garnotte.gameObject));
		}
	}

	// Most items laid in one block of ground, side by side and in layers.
	private const int PerCell = 8;

	// How far round the stockpile items are laid, in blocks.
	private const int MaxRadius = 6;

	// The game's scatter put many items on one spot (every carriable item exactly in the middle), partly inside
	// the ground on uneven terrain; the physics then pushed them through the ground, where they fell out of the
	// world after a while. Each item gets a place of its own instead: on the open ground round the stockpile,
	// nearest first, at most PerCell to a block, just above the surface.
	private static void LayOnGround(List<GameObject> items, Vector3 center)
	{
		XYZ middle = XYZ.FromVector3(center);
		int placed = 0;
		for (int radius = 0; radius <= MaxRadius && placed < items.Count; radius++)
		{
			for (int dx = -radius; dx <= radius && placed < items.Count; dx++)
			{
				for (int dz = -radius; dz <= radius && placed < items.Count; dz++)
				{
					if (Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dz)) != radius || !Ground(middle.x + dx, middle.z + dz, middle.y, out float surface))
					{
						continue;
					}
					for (int slot = 0; slot < PerCell && placed < items.Count; slot++)
					{
						// Four spots a quarter block from the middle, then a second layer over them.
						float ox = ((slot & 1) == 0) ? -0.25f : 0.25f;
						float oz = ((slot & 2) == 0) ? -0.25f : 0.25f;
						float oy = 0.3f + (slot / 4) * 0.35f;
						Put(items[placed], new Vector3(middle.x + dx + ox, surface + oy, middle.z + dz + oz));
						placed++;
					}
				}
			}
		}
		// No open ground near (a stockpile on a tower, walled in): what is left goes where the game would drop it.
		if (placed < items.Count)
		{
			List<GameObject> rest = items.GetRange(placed, items.Count - placed);
			Recepteur.ScatterObjects(rest, center + Vector3.up * 0.5f);
		}
	}

	// The top of the ground in the column x, z near the stockpile's height (not the top of a wall), and room over it.
	private static bool Ground(int x, int z, int y, out float surface)
	{
		surface = 0f;
		if (!Architecte.VoxelDessus(new XZ(x, z), out XYZ top, y - 4, y + 1))
		{
			return false;
		}
		XYZ above = new XYZ(x, top.y + 1, z);
		if (top.y > y || !Voxel.IsEmpty(above) || !Voxel.IsEmpty(above + XYZ.up))
		{
			return false;
		}
		surface = top.y + 0.5f;
		return true;
	}

	private static void Put(GameObject item, Vector3 position)
	{
		if (item.IsNullOrReleased())
		{
			return;
		}
		item.transform.position = position;
		item.transform.rotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
		item.Deposer();
		item.SetActive(true);
		Rigidbody body = item.GetComponent<Rigidbody>();
		if (body != null && !body.isKinematic)
		{
			body.velocity = Vector3.zero;
			body.angularVelocity = Vector3.zero;
		}
	}

	private static bool IsStockpile(GameObject go)
	{
		FactoryImprint imprint = go.GetComponent<FactoryImprint>();
		return imprint != null && (imprint.AssetKey == ObjetsDynamiques.Palette || Warehouse.IsWarehouse(imprint));
	}
}

// A dropped stockpile's stones, ore and crystals do not sink into the ground.
[Feature(Features.StockpileSpill, Features.StockpileSpillInfo)]
[HarmonyPatch(typeof(Garnotte), nameof(Garnotte.SinkIfPossible))]
internal static class StockpileSpillKeepPatch
{
	private static bool Prefix(Garnotte __instance, ref bool __result)
	{
		if (__instance == null || !StockpileSpill.Kept.Contains(__instance.gameObject))
		{
			return true;
		}
		__result = false;
		return false;
	}
}

[Feature(Features.StockpileSpill, Features.StockpileSpillInfo)]
[HarmonyPatch(typeof(Mergeback), "OnGarnotteEnabled")]
internal static class StockpileSpillMergebackPatch
{
	private static bool Prefix(Garnotte g)
	{
		return g == null || !StockpileSpill.Kept.Contains(g.gameObject);
	}
}

// A pooled item is used again for anything (also the gravel of digging): it may sink again.
[Feature(Features.StockpileSpill, Features.StockpileSpillInfo)]
[HarmonyPatch(typeof(Garnotte), nameof(Garnotte.ResetComponent))]
internal static class StockpileSpillResetPatch
{
	private static void Postfix(Garnotte __instance)
	{
		if (__instance != null)
		{
			StockpileSpill.Kept.Remove(__instance.gameObject);
		}
	}
}
