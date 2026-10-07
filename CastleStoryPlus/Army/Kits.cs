using System;
using System.Collections.Generic;
using Brix.Components;
using Brix.External.Factories;
using Brix.Game.AI;
using Brix.Game.AI.KnowledgeSpace;
using Brix.Game.AI.Nodes;
using Brix.Game.Semantique;
using Brix.Lifecycle.Pooling;
using CastleStoryPlus.Building;
using Motus.Behavior;
using UnityEngine;

namespace CastleStoryPlus.Army;

// Weapon and armour sets per soldier job on tool racks, and the AI node to equip one specific kit.
internal static class Kits
{
	// Kit items for a soldier job, or null for Builder.
	public static List<Ressource> KitFor(Occupation.Job job)
	{
		switch (job)
		{
		case Occupation.Job.Knight:
			return ToolRack.Knight;
		case Occupation.Job.Halberdier:
			return ToolRack.Halbardier;
		case Occupation.Job.Archer:
			return ToolRack.Archer;
		case Occupation.Job.Arbalist:
			return ToolRack.Arbalist;
		case Occupation.Job.Alchemist:
			return ToolRack.Alchemist;
		case Occupation.Job.Artificer:
			return ToolRack.Artificer;
		default:
			return null;
		}
	}

	// Number of complete kits of this job stored on the rack.
	public static int KitsFor(ToolRack rack, Occupation.Job job)
	{
		List<Ressource> kit = KitFor(job);
		if (kit == null || rack == null || rack.recepteur == null)
		{
			return 0;
		}
		int count = int.MaxValue;
		foreach (Ressource item in kit)
		{
			count = Mathf.Min(count, rack.recepteur.ContentDescription.Value(item));
		}
		return count;
	}

	// Like GoEquipBestToolFromRack, but only takes the kit of one soldier job.
	public static StaticNode GoEquipKitFromRack(Labor labor, Occupation.Job job)
	{
		Func<Labor, bool> armed = (Labor l) => l.Occupation.CurrentOccupation != Occupation.Type.Builder;
		if (armed(labor))
		{
			return Node.Empty;
		}
		Sequencer.Builder n = Node.Sequence.Do((Labor aLabor, Occupation.Job aJob) => ClosestRackWithKit(aLabor, aJob), labor, job).Process((GameObject rackGo, Labor aLabor) => (!(rackGo == null)) ? aLabor.GoEquipRackContent(rackGo) : Node.FailWork<Exceptions.ToolUnfindable>(), labor);
		Catcher.Builder b = Node.Catcher.Do(n).Catch((Func<Exceptions.ToolUnfindable, Node>)Node.Fail).Catch((Exceptions.WorkFailedException e) => Node.Continue());
		return b.Loop().Until(armed, labor).ToNode;
	}

	public static GameObject ClosestRackWithKit(Labor labor, Occupation.Job job)
	{
		HashSet<GameObject> racks = BrixSingleton<AutoList>.Instance.GetInstances(ObjetsDynamiques.Toolrack);
		if (racks == null)
		{
			return null;
		}
		GameObject best = null;
		float bestDistance = float.MaxValue;
		foreach (GameObject go in racks)
		{
			if (go == null || !go.activeInHierarchy || !labor.faction.IsSame(go))
			{
				continue;
			}
			ToolRack rack = go.GetComponent<ToolRack>();
			if (rack == null || KitsFor(rack, job) <= 0 || !labor.CanEquipRackContent(go))
			{
				continue;
			}
			float distance = (go.transform.position - labor.transform.position).sqrMagnitude;
			if (distance >= bestDistance || !Knowledge.Instance.CanReach(labor, rack))
			{
				continue;
			}
			best = go;
			bestDistance = distance;
		}
		// A stand may hold kits of several classes (MixedRacks): take this job's kit there, not its main one.
		if (best != null)
		{
			MixedRacks.Want(labor, job);
		}
		return best;
	}
}
