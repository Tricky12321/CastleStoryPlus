using System;
using System.Collections.Generic;
using Brix.Game.AI;
using Brix.Game.AI.Nodes;
using Brix.Game.Semantique;
using CastleStoryPlus.Army;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.Building;

// The game locked a weapon stand to the soldier class of the first item put on it (ToolRack.RefreshExclusivity:
// a sword made it a knight rack, so bows, quivers and caps were refused). Now every stand takes the gear of all
// classes at once. Room is counted per slot instead of per item: a stand holds as many weapons, as many shields
// and quivers (props) and as many helmets and caps (hats) as it held of one item before, so it still holds the
// same number of kits, of any classes mixed. Equipping takes one complete kit of a single class: the one a call to
// arms asked for, otherwise the class with the most complete kits on the stand (also the icon of its equip button).
[Feature(Features.MixedRacks, Features.MixedRacksInfo)]
internal static class MixedRacks
{
	// Room per slot; 0 = the stand's own room for one item (the game's kits per stand).
	private static int _perSlot;

	private static bool _logged;

	// Kit a bricktron was sent for by a call to arms, by labor instance id, with the time it was asked.
	private static readonly Dictionary<int, KeyValuePair<Occupation.Job, float>> Wanted = new Dictionary<int, KeyValuePair<Occupation.Job, float>>();

	// A wish older than this is from an order that never reached the stand.
	private const float WantedSeconds = 120f;

	// The bricktron taking gear from a stand right now (set around ToolNode.EquipRackContent).
	internal static Labor Equipping;

	private static readonly List<Ressource>[] Slots = new List<Ressource>[3]
	{
		ToolRack.Weapons,
		ToolRack.Props,
		ToolRack.Hats
	};

	private static void Enable()
	{
		_perSlot = Plugin.Cfg.Bind("MixedRacks", "PerSlot", 0, "Weapons, props (shields, quivers...) and hats a weapon stand holds of each, all classes together; 0 = the stand's own room for one item (as many kits as before).").Value;
	}

	public static bool IsRack(Recepteur recepteur)
	{
		return recepteur != null && (bool)recepteur._rack;
	}

	// Called by the call to arms when it sends a bricktron for the kit of one class.
	public static void Want(Labor labor, Occupation.Job job)
	{
		if (labor != null)
		{
			Wanted[labor.GetInstanceID()] = new KeyValuePair<Occupation.Job, float>(job, Time.time);
		}
	}

	// Takes the wished kit of a bricktron (once), or null.
	private static List<Ressource> TakeWanted(Labor labor)
	{
		if (labor == null || !Wanted.TryGetValue(labor.GetInstanceID(), out KeyValuePair<Occupation.Job, float> wish))
		{
			return null;
		}
		Wanted.Remove(labor.GetInstanceID());
		if (Time.time - wish.Value > WantedSeconds)
		{
			return null;
		}
		return Kits.KitFor(wish.Key);
	}

	// Class with the most complete kits, then the most items; AllItems when the stand holds no gear.
	public static List<Ressource> Dominant(Recepteur recepteur)
	{
		List<Ressource> best = ToolRack.AllItems;
		int bestKits = 0;
		int bestItems = 0;
		foreach (List<Ressource> kit in ToolRack.AllClasses)
		{
			int kits = int.MaxValue;
			int items = 0;
			foreach (Ressource item in kit)
			{
				int count = recepteur.ContentDescription.Value(item);
				kits = Mathf.Min(kits, count);
				items += count;
			}
			if (kits > bestKits || (kits == bestKits && items > bestItems))
			{
				best = kit;
				bestKits = kits;
				bestItems = items;
			}
		}
		return best;
	}

	private static int Complete(Recepteur recepteur, List<Ressource> kit)
	{
		int kits = int.MaxValue;
		foreach (Ressource item in kit)
		{
			kits = Mathf.Min(kits, recepteur.ContentDescription.Value(item));
		}
		return kits;
	}

	// Kit to hand out: the wished one when the stand holds it whole, otherwise the dominant class.
	public static List<Ressource> KitToTake(ToolRack rack)
	{
		List<Ressource> wanted = TakeWanted(Equipping);
		if (wanted != null && Complete(rack.recepteur, wanted) > 0)
		{
			return wanted;
		}
		List<Ressource> dominant = Dominant(rack.recepteur);
		return (dominant == ToolRack.AllItems) ? null : dominant;
	}

	// One stored item of every type of the kit; true when the kit is whole.
	public static bool Collect(ToolRack rack, List<Ressource> kit, List<GameObject> gos)
	{
		if (kit == null)
		{
			return false;
		}
		int found = 0;
		foreach (Ressource item in kit)
		{
			foreach (IDescriptor stored in rack.recepteur.StoredItems)
			{
				GameObject go = (stored != null) ? stored.GameObject : null;
				if (go != null && !gos.Contains(go) && Apparence.Is(go, item))
				{
					gos.Add(go);
					found++;
					break;
				}
			}
		}
		return found == kit.Count;
	}

	// A stand still locked to one class (from before this feature, kept in the save).
	public static bool ExcludesKitItem(Recepteur recepteur)
	{
		foreach (Ressource item in ToolRack.AllItems)
		{
			if (recepteur.Exclusions.Contains(item))
			{
				return true;
			}
		}
		return false;
	}

	private static int SlotRoom(Recepteur recepteur, List<Ressource> slot)
	{
		if (_perSlot > 0)
		{
			return _perSlot;
		}
		int room = 0;
		foreach (Ressource item in slot)
		{
			room = Mathf.Max(room, recepteur.BaseCapacity.Value(item));
		}
		return room;
	}

	// Caps the room for every item at what is left of its slot (all classes together).
	public static void ApplySlotCapacity(Recepteur recepteur)
	{
		LogCapacityOnce(recepteur);
		Description capacity = recepteur.CurrentCapacity;
		Description content = recepteur.ContentDescription;
		bool anyRoom = false;
		foreach (List<Ressource> slot in Slots)
		{
			int used = 0;
			foreach (Ressource item in slot)
			{
				used += content.Value(item);
			}
			int left = Mathf.Max(0, SlotRoom(recepteur, slot) - used);
			foreach (Ressource item in slot)
			{
				int room = capacity.Value(item);
				if (room > left)
				{
					capacity.Set(item, left);
					room = left;
				}
				if (room > 0)
				{
					anyRoom = true;
				}
			}
		}
		// A stand with no room for any item is full; leftover bulk alone must not keep it open.
		if (!anyRoom && capacity.Has(Adjectif.encombrement))
		{
			capacity.Set(Adjectif.encombrement, 0);
		}
	}

	private static void LogCapacityOnce(Recepteur recepteur)
	{
		if (_logged)
		{
			return;
		}
		_logged = true;
		string text = string.Empty;
		foreach (KeyValuePair<Type, Adjectif> pair in recepteur.BaseCapacity.DicoAdjectif)
		{
			text += pair.Key.Name + "=" + pair.Value.quantifiable.valeur + " ";
		}
		Plugin.Log.LogInfo("MixedRacks: weapon stand capacity " + text);
	}
}

// The stand no longer locks to the class of its first item. _exclusivity now only names the dominant class, which
// picks the icon of the equip button in the stand's action wheel (IsForKnight, IsForArcher...).
[Feature(Features.MixedRacks, Features.MixedRacksInfo)]
[HarmonyPatch(typeof(ToolRack), nameof(ToolRack.RefreshExclusivity))]
internal static class MixedRackExclusivityPatch
{
	private static bool Prefix(ToolRack __instance)
	{
		Recepteur recepteur = __instance.recepteur;
		if (recepteur == null)
		{
			return true;
		}
		// The game's own "empty stand" state: every kit item allowed.
		if (MixedRacks.ExcludesKitItem(recepteur))
		{
			recepteur.MakeExclusive(ToolRack.AllItems);
		}
		__instance._exclusivity = MixedRacks.Dominant(recepteur);
		return false;
	}
}

// Every add, remove and exclusion change ends here (DealSingleResourceRecepteur, ResetDescriptions).
[Feature(Features.MixedRacks, Features.MixedRacksInfo)]
[HarmonyPatch(typeof(Recepteur), "RegenerateDescriptionsWithExclusions")]
internal static class MixedRackCapacityPatch
{
	private static void Postfix(Recepteur __instance)
	{
		if (MixedRacks.IsRack(__instance))
		{
			MixedRacks.ApplySlotCapacity(__instance);
		}
	}
}

// The game took the first weapon, prop and hat on the stand, which on a mixed stand can be a sword, a quiver and a
// cap. Now it takes one whole kit of a single class.
[Feature(Features.MixedRacks, Features.MixedRacksInfo)]
[HarmonyPatch(typeof(ToolRack), nameof(ToolRack.GetKit))]
internal static class MixedRackGetKitPatch
{
	private static bool Prefix(ToolRack __instance, ref List<GameObject> gos, ref bool __result)
	{
		if (__instance.recepteur == null)
		{
			return true;
		}
		__result = MixedRacks.Collect(__instance, MixedRacks.KitToTake(__instance), gos);
		return false;
	}
}

// Tells GetKit which bricktron is equipping, so a call to arms gets the class it asked for.
[Feature(Features.MixedRacks, Features.MixedRacksInfo)]
[HarmonyPatch(typeof(ToolNode), "EquipRackContent")]
internal static class MixedRackEquipPatch
{
	private static void Prefix(Labor labor)
	{
		MixedRacks.Equipping = labor;
	}

	private static void Finalizer()
	{
		MixedRacks.Equipping = null;
	}
}
