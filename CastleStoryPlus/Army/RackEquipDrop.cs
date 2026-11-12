using System;
using System.Collections.Generic;
using Brix.Engine;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Game.Components.Volumes;
using Brix.Game.AI.Nodes;
using Brix.Game.Semantique;
using CastleStoryPlus.Core;
using HarmonyLib;
using Motus.Behavior;
using Brix.Utils;
using UnityEngine;

namespace CastleStoryPlus.Army;

// A builder carrying something (a block, a log) who goes to a tool rack for its gear (call to arms, equip from rack)
// put it down only at the rack. Between racks standing close together there is often no free spot to put it, the
// drop silently did nothing, the "hands not empty" check failed the step and the gear search tried again at once,
// for ever: the worker stood at the rack, carrying the block, and never took its gear.
// Now the worker drops what it carries where it stands (call to arms: drop everything and run for the gear) before it
// walks to the rack; where the game finds no spot the load fits, it is dropped at the nearest free spot anyway. If
// its hands are still not empty at the rack it gives up getting gear instead of trying again for ever.
[Feature(Features.CallToArms, Features.CallToArmsInfo)]
[HarmonyPatch(typeof(ToolNode), nameof(ToolNode.GoEquipRackContent))]
internal static class RackEquipDropFirstPatch
{
	private static void Postfix(Labor labor, ref Node __result)
	{
		if (labor == null || labor.recepteur == null || labor.recepteur.IsEmpty())
		{
			return;
		}
		Node equip = __result;
		__result = (StaticNode)Node.Sequence.Do((Func<Labor, StaticNode>)DropNode.Drop, labor).Do((Labor l) =>
		{
			ForceDrop(l);
		}, labor).Do((Labor l, Node n) => n, labor, equip);
	}

	// As Recepteur.DropAll, but without the check that the load fits the spot (forceDrop).
	internal static void ForceDrop(Labor labor)
	{
		Recepteur recepteur = labor.recepteur;
		if (recepteur == null || recepteur.IsEmpty())
		{
			return;
		}
		List<GameObject> objects = new List<GameObject>();
		foreach (IDescriptor stored in recepteur.StoredItems)
		{
			if (stored != null && stored.GameObject != null)
			{
				objects.Add(stored.GameObject);
			}
		}
		if (objects.Count == 0)
		{
			return;
		}
		Vector3 at = VolumeRegistry.ClosestAvailableDropPosition(labor.transform, objects.Count, objects[0], forceDrop: true);
		if (at == Vector3.zero)
		{
			return;
		}
		ClasseMoi.Move(objects);
		if (objects.Count == 1)
		{
			BaseVoxelTransform voxel = objects[0].GetComponent<BaseVoxelTransform>();
			if (voxel != null)
			{
				voxel.position = XYZ.FromVector3(at);
			}
			else
			{
				objects[0].transform.position = at;
			}
		}
	}
}

[Feature(Features.CallToArms, Features.CallToArmsInfo)]
[HarmonyPatch(typeof(ToolNode), nameof(ToolNode.FailIfNotEmpty))]
internal static class RackEquipGiveUpPatch
{
	private static bool Prefix(Labor labor, ref Node __result)
	{
		if (labor.recepteur.IsEmpty())
		{
			return true;
		}
		// ToolUnfindable ends the gear search (GoEquipBestToolFromRack / GoEquipKitFromRack) instead of looping.
		Plugin.Log.LogWarning(labor.name + " could not put down what it carries at the tool rack and gives up getting its gear");
		__result = Node.Fail(SharedWorkException<Exceptions.ToolUnfindable>.Get);
		return false;
	}
}
