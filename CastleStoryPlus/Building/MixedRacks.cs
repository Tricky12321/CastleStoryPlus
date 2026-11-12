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

	// A wish older than this is from an order that never reached the stand (a long walk to a far stand took more than
	// the 2 minutes this was before, and the bricktron then took the stand's main class instead).
	private const float WantedSeconds = 600f;

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
			Wanted[labor.InstanceId()] = new KeyValuePair<Occupation.Job, float>(job, Time.time);
		}
	}

	// A new order from the player ends the wish of an earlier call to arms (a later equip order is not held to it).
	public static void Forget(Labor labor)
	{
		if (labor != null && Wanted.Count > 0)
		{
			Wanted.Remove(labor.InstanceId());
		}
	}

	// The wished kit of a bricktron, or null. Read by every GetKit patch of the equip (KitBundles, MixedRacks): it is
	// forgotten when the equip is done (MixedRackEquipPatch), not on the first read, or the second patch saw no wish.
	private static List<Ressource> WantedKit(Labor labor)
	{
		if (labor == null || !Wanted.TryGetValue(labor.InstanceId(), out KeyValuePair<Occupation.Job, float> wish))
		{
			return null;
		}
		if (Time.time - wish.Value > WantedSeconds)
		{
			return null;
		}
		return Kits.KitFor(wish.Key);
	}

	// Class with the most complete kits, then the most items; AllItems when the stand holds no gear.
	public static List<Ressource> Dominant(Recepteur recepteur)
	{
		return Dominant(recepteur, null, null);
	}

	// As above, counting only the kits no one but this bricktron has reserved (KitReservation) when a stand is given.
	private static List<Ressource> Dominant(Recepteur recepteur, GameObject rack, Labor except)
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
			// A kit bundle (KitBundles) is a whole kit.
			int bundles = KitBundles.CountFor(recepteur, kit);
			kits += bundles;
			items += bundles * kit.Count;
			if (rack != null && KitReservation.Enabled)
			{
				Occupation.Job job = JobOf(kit);
				if (job != Occupation.Job.Builder)
				{
					kits -= KitReservation.KitsReserved(rack, (int)job, except);
				}
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

	// Whole kits of the class on the stand: loose sets and kit bundles.
	internal static int Complete(Recepteur recepteur, List<Ressource> kit)
	{
		int kits = int.MaxValue;
		foreach (Ressource item in kit)
		{
			kits = Mathf.Min(kits, recepteur.ContentDescription.Value(item));
		}
		return kits + KitBundles.CountFor(recepteur, kit);
	}

	// Kit to hand out: the wished one (a call to arms sent the bricktron for it) or nothing when the stand no longer
	// holds it whole (strict: it takes no other class, and looks for the kit on another stand); otherwise the kit of
	// the bricktron's call to arms role when the stand holds one, otherwise the class with the most kits no one else
	// has reserved (KitReservation). A bricktron sent for knight gear found its kit taken by one on its way to any
	// kit, and took the stand's archer kit instead.
	public static List<Ressource> KitToTake(ToolRack rack, out bool strict)
	{
		strict = false;
		Labor labor = Equipping;
		List<Ressource> wanted = WantedKit(labor);
		if (wanted != null)
		{
			strict = true;
			return (Complete(rack.recepteur, wanted) > 0) ? wanted : null;
		}
		Occupation.Job role = (labor != null) ? Kits.RoleJob(labor) : Occupation.Job.Builder;
		if (role != Occupation.Job.Builder && Complete(rack.recepteur, Kits.KitFor(role)) > 0)
		{
			return Kits.KitFor(role);
		}
		List<Ressource> dominant = Dominant(rack.recepteur, rack.gameObject, labor);
		return (dominant == ToolRack.AllItems) ? null : dominant;
	}

	public static List<Ressource> KitToTake(ToolRack rack)
	{
		return KitToTake(rack, out _);
	}

	// The soldier class of a kit list, or Builder.
	private static Occupation.Job JobOf(List<Ressource> kit)
	{
		foreach (Occupation.Job job in CallToArms.SoldierJobs)
		{
			if (Kits.KitFor(job) == kit)
			{
				return job;
			}
		}
		return Occupation.Job.Builder;
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
		// A kit bundle (KitBundles) takes the room of a whole kit: one place in every slot.
		int bundles = KitBundles.CountAll(content);
		int bundleLeft = int.MaxValue;
		foreach (List<Ressource> slot in Slots)
		{
			int used = bundles;
			foreach (Ressource item in slot)
			{
				used += content.Value(item);
			}
			int left = Mathf.Max(0, SlotRoom(recepteur, slot) - used);
			bundleLeft = Mathf.Min(bundleLeft, left);
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
		if (KitBundles.CapRoom(capacity, bundleLeft))
		{
			anyRoom = true;
		}
		// A stand with no room for any item is full; leftover bulk alone must not keep it open.
		if (!anyRoom && capacity.Has(Adjectif.encombrement))
		{
			capacity.Set(Adjectif.encombrement, 0);
		}
	}

	// Hard check behind the capacity above: a kit item or bundle fits only while its slot (every slot for a bundle)
	// holds fewer than the slot's room. Counted from what lies on the stand right now (the children of its
	// container): the stand's own count follows an add only a moment later, so two deliveries close together both
	// saw room for one more (stands with 5 kits where 4 fit).
	public static bool HasSlotRoomFor(Recepteur recepteur, Description item)
	{
		if (item == null)
		{
			return true;
		}
		int bundles;
		int[] loose = new int[Slots.Length];
		Transform container = recepteur._container;
		if (container != null)
		{
			bundles = 0;
			foreach (Transform child in container)
			{
				IDescriptor descriptor = (child != null) ? child.GetComponent<IDescriptor>() : null;
				Description held = (descriptor != null) ? descriptor.Description : null;
				if (held == null)
				{
					continue;
				}
				bundles += KitBundles.CountAll(held);
				for (int i = 0; i < Slots.Length; i++)
				{
					foreach (Ressource piece in Slots[i])
					{
						loose[i] += held.Value(piece);
					}
				}
			}
		}
		else
		{
			Description content = recepteur.ContentDescription;
			bundles = KitBundles.CountAll(content);
			for (int i = 0; i < Slots.Length; i++)
			{
				foreach (Ressource piece in Slots[i])
				{
					loose[i] += content.Value(piece);
				}
			}
		}
		bool isBundle = KitBundles.CountAll(item) > 0;
		for (int i = 0; i < Slots.Length; i++)
		{
			bool inSlot = isBundle;
			foreach (Ressource piece in Slots[i])
			{
				if (item.Value(piece) > 0)
				{
					inSlot = true;
				}
			}
			if (inSlot && bundles + loose[i] >= SlotRoom(recepteur, Slots[i]))
			{
				return false;
			}
		}
		return true;
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

// The stand takes a kit item or bundle only while its slots have room (MixedRacks.HasSlotRoomFor): at most as many
// kits as the stand's room for one item (4), all classes together.
[Feature(Features.MixedRacks, Features.MixedRacksInfo)]
[HarmonyPatch]
internal static class MixedRackRoomPatch
{
	[HarmonyPatch(typeof(Recepteur), nameof(Recepteur.HasRoomFor), new Type[] { typeof(IDescriptor) })]
	[HarmonyPostfix]
	private static void DescriptorRoom(Recepteur __instance, IDescriptor d, ref bool __result)
	{
		if (__result && d != null && MixedRacks.IsRack(__instance) && !MixedRacks.HasSlotRoomFor(__instance, d.Description))
		{
			__result = false;
		}
	}

	[HarmonyPatch(typeof(Recepteur), nameof(Recepteur.HasRoomFor), new Type[] { typeof(GameObject) })]
	[HarmonyPostfix]
	private static void ObjectRoom(Recepteur __instance, GameObject o, ref bool __result)
	{
		if (!__result || o == null || !MixedRacks.IsRack(__instance) || __instance.Carries(o))
		{
			return;
		}
		IDescriptor d = o.GetComponent<IDescriptor>();
		if (d != null && !MixedRacks.HasSlotRoomFor(__instance, d.Description))
		{
			__result = false;
		}
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
// cap. Now it takes one whole kit of a single class. Steps aside when a kit bundle was unpacked (KitBundleGetKitPatch).
[Feature(Features.MixedRacks, Features.MixedRacksInfo)]
[HarmonyPatch(typeof(ToolRack), nameof(ToolRack.GetKit))]
internal static class MixedRackGetKitPatch
{
	private static bool Prefix(ToolRack __instance, ref List<GameObject> gos, ref bool __result, bool __runOriginal)
	{
		if (!__runOriginal)
		{
			return false;
		}
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

	private static void Finalizer(Labor labor)
	{
		MixedRacks.Forget(labor);
		MixedRacks.Equipping = null;
	}
}

// A new order from the player starts without the kit wish of an earlier call to arms (MixedRacks.Want).
[Feature(Features.MixedRacks, Features.MixedRacksInfo)]
[HarmonyPatch(typeof(Labor), nameof(Labor.SendOrder))]
internal static class MixedRackForgetWishPatch
{
	private static void Prefix(Labor __instance, Labor.DecisionSource source)
	{
		if (source == Labor.DecisionSource.User)
		{
			MixedRacks.Forget(__instance);
		}
	}
}
