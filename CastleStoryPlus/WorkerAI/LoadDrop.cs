using System.Collections.Generic;
using Brix.Engine;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Game.Components.Volumes;
using Brix.Game.Semantique;
using Brix.Utils;
using UnityEngine;

namespace CastleStoryPlus.WorkerAI;

// Puts down what a bricktron carries where it stands, also where the game finds no spot the load fits. Used by
// LoadRelief (a load nothing can store) and, in the Full edition, by the call to arms' gear fetch (RackEquipDrop).
internal static class LoadDrop
{
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
