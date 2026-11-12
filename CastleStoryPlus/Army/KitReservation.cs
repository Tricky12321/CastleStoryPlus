using System.Collections.Generic;
using Brix.Game.AI;
using Brix.Game.AI.KnowledgeSpace;
using Brix.Game.AI.Nodes;
using Brix.Game.Components;
using Brix.Game.Semantique;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.Army;

// Server: a bricktron reserves the kit (or the room for its kit) on the weapon stand it sets out for, before it walks
// there. The game chose a stand by what lay on it, not by who was already on the way: at a call to arms several
// bricktrons ran to the same last kit, all but the first found the stand empty (or took the kit of another class) and
// searched again from there; putting the gear back, several ran to the last free place. Now a stand is only chosen
// while it still holds a kit (of the class asked for) that no one else has reserved, or room for one more kit that no
// one else is bringing. A reservation ends when the bricktron has taken or stored its kit, starts another task, is
// gone, or after 90 seconds.
[Feature(Features.KitReservation, Features.KitReservationInfo)]
internal static class KitReservation
{
	private class Reservation
	{
		public Labor Labor;

		public Task Task;

		public GameObject Rack;

		// The class of the kit to take (CallToArms.AnyKit for whatever the stand hands out); unused when storing.
		public int Job;

		public bool Store;

		public float Until;
	}

	private const float TimeoutSeconds = 90f;

	private static readonly Dictionary<Labor, Reservation> ByLabor = new Dictionary<Labor, Reservation>();

	private static readonly Dictionary<GameObject, List<Reservation>> ByRack = new Dictionary<GameObject, List<Reservation>>();

	private static void Enable()
	{
		GameSession.OnLeave(() =>
		{
			ByLabor.Clear();
			ByRack.Clear();
		});
	}

	internal static bool Enabled => Plugin.IsEnabled(Features.KitReservation);

	public static void ReserveKit(Labor labor, GameObject rack, int job)
	{
		Reserve(labor, rack, job, store: false);
	}

	public static void ReserveRoom(Labor labor, GameObject rack)
	{
		Reserve(labor, rack, CallToArms.AnyKit, store: true);
	}

	private static void Reserve(Labor labor, GameObject rack, int job, bool store)
	{
		if (!Enabled || labor.IsNullOrReleased() || rack == null)
		{
			return;
		}
		Release(labor);
		Reservation reservation = new Reservation
		{
			Labor = labor,
			Task = labor.CurrentTask,
			Rack = rack,
			Job = job,
			Store = store,
			Until = Time.time + TimeoutSeconds
		};
		ByLabor[labor] = reservation;
		if (!ByRack.TryGetValue(rack, out List<Reservation> list))
		{
			list = new List<Reservation>();
			ByRack[rack] = list;
		}
		list.Add(reservation);
	}

	public static void Release(Labor labor)
	{
		if (labor == null || !ByLabor.TryGetValue(labor, out Reservation reservation))
		{
			return;
		}
		ByLabor.Remove(labor);
		if (reservation.Rack != null && ByRack.TryGetValue(reservation.Rack, out List<Reservation> list))
		{
			list.Remove(reservation);
			if (list.Count == 0)
			{
				ByRack.Remove(reservation.Rack);
			}
		}
	}

	// Kits of the class (or of any class, AnyKit) that others are on their way to take from the stand.
	public static int KitsReserved(GameObject rack, int job, Labor except)
	{
		int count = 0;
		foreach (Reservation reservation in Others(rack, except))
		{
			if (!reservation.Store && (job == CallToArms.AnyKit || reservation.Job == job))
			{
				count++;
			}
		}
		return count;
	}

	// Kits others are on their way to put on the stand.
	public static int RoomReserved(GameObject rack, Labor except)
	{
		int count = 0;
		foreach (Reservation reservation in Others(rack, except))
		{
			if (reservation.Store)
			{
				count++;
			}
		}
		return count;
	}

	private static List<Reservation> Others(GameObject rack, Labor except)
	{
		List<Reservation> result = new List<Reservation>();
		if (!Enabled || rack == null || !ByRack.TryGetValue(rack, out List<Reservation> list))
		{
			return result;
		}
		for (int i = list.Count - 1; i >= 0; i--)
		{
			if (!IsValid(list[i]))
			{
				Release(list[i].Labor);
			}
		}
		foreach (Reservation reservation in list)
		{
			if (reservation.Labor != except)
			{
				result.Add(reservation);
			}
		}
		return result;
	}

	// A reservation lasts while its bricktron is on the task it was made for.
	private static bool IsValid(Reservation reservation)
	{
		if (Time.time > reservation.Until || reservation.Labor.IsNullOrReleased() || reservation.Rack.IsNullOrReleased())
		{
			return false;
		}
		return reservation.Labor.CurrentTask == reservation.Task;
	}

	// Whole kits of the class on the stand that no one else has reserved.
	public static int FreeKits(ToolRack rack, Occupation.Job job, Labor except)
	{
		int kits = Kits.KitsFor(rack, job);
		if (kits <= 0)
		{
			return 0;
		}
		int free = kits - KitsReserved(rack.gameObject, (int)job, except);
		// Kits of any class someone takes (a call to arms without a class) may be this class too.
		return Mathf.Min(free, AllKits(rack) - KitsReserved(rack.gameObject, CallToArms.AnyKit, except));
	}

	// Whole kits of any class on the stand.
	public static int AllKits(ToolRack rack)
	{
		int kits = 0;
		foreach (Occupation.Job job in CallToArms.SoldierJobs)
		{
			kits += Kits.KitsFor(rack, job);
		}
		return kits;
	}

	// Kits the stand still takes of the bricktron's class: the room left for each of its pieces.
	public static int RoomFor(ToolRack rack, Labor labor)
	{
		List<Ressource> kit = (labor.Occupation != null) ? Kits.KitFor(labor.Occupation.CurrentJob) : null;
		if (kit == null || rack.recepteur == null)
		{
			return 1;
		}
		int room = int.MaxValue;
		foreach (Ressource item in kit)
		{
			room = Mathf.Min(room, rack.recepteur.CurrentCapacity.Value(item));
		}
		return room;
	}
}

// The game's choice of a stand to take gear from (the call to arms without a class, the equip orders): skip stands
// whose kits others have reserved, and reserve a kit on the one chosen.
[Feature(Features.KitReservation, Features.KitReservationInfo)]
[HarmonyPatch(typeof(Labor), nameof(Labor.BestToolToPickup))]
internal static class KitReservationPickupPatch
{
	private static void Postfix(Labor __instance, GameObject __result)
	{
		if (__result != null && __result.GetComponent<ToolRack>() != null)
		{
			KitReservation.ReserveKit(__instance, __result, CallToArms.AnyKit);
		}
	}
}

[Feature(Features.KitReservation, Features.KitReservationInfo)]
[HarmonyPatch(typeof(Knowledge), "_ClosestToolFrom_Test")]
internal static class KitReservationPickupTestPatch
{
	private static void Postfix(IGameComponent gc, Labor futureUser, ref bool __result)
	{
		if (!__result || gc == null || gc.GameObject == null)
		{
			return;
		}
		ToolRack rack = gc.GameObject.GetComponent<ToolRack>();
		if (rack != null && KitReservation.AllKits(rack) - KitReservation.KitsReserved(gc.GameObject, CallToArms.AnyKit, futureUser) <= 0)
		{
			__result = false;
		}
	}
}

// The game's choice of a stand to put the gear back on: skip stands whose last places others are bringing a kit to,
// and reserve a place on the one chosen.
[Feature(Features.KitReservation, Features.KitReservationInfo)]
[HarmonyPatch(typeof(Labor), nameof(Labor.BestToolRackToStore))]
internal static class KitReservationStorePatch
{
	private static void Postfix(Labor __instance, GameObject __result)
	{
		if (__result != null && __result.GetComponent<ToolRack>() != null)
		{
			KitReservation.ReserveRoom(__instance, __result);
		}
	}
}

[Feature(Features.KitReservation, Features.KitReservationInfo)]
[HarmonyPatch(typeof(Knowledge), "_CanStoreToolOnRack_Test")]
internal static class KitReservationStoreTestPatch
{
	private static void Postfix(IGameComponent gc, Labor futureUser, ref bool __result)
	{
		if (!__result || gc == null || gc.GameObject == null || futureUser.IsNullOrReleased())
		{
			return;
		}
		ToolRack rack = gc.GameObject.GetComponent<ToolRack>();
		if (rack != null && KitReservation.RoomFor(rack, futureUser) - KitReservation.RoomReserved(gc.GameObject, futureUser) <= 0)
		{
			__result = false;
		}
	}
}

// Took its kit (or found none) at the stand: the reservation has done its job.
[Feature(Features.KitReservation, Features.KitReservationInfo)]
[HarmonyPatch(typeof(ToolNode), "EquipRackContent")]
internal static class KitReservationEquipDonePatch
{
	private static void Finalizer(Labor labor)
	{
		KitReservation.Release(labor);
	}
}

[Feature(Features.KitReservation, Features.KitReservationInfo)]
[HarmonyPatch(typeof(ToolNode), "StoreToolsIn")]
internal static class KitReservationStoreDonePatch
{
	private static void Finalizer(Labor labor)
	{
		KitReservation.Release(labor);
	}
}

// A pooled bricktron that is reused has no reservation.
[Feature(Features.KitReservation, Features.KitReservationInfo)]
[HarmonyPatch(typeof(Labor), nameof(Labor.OnRequested))]
internal static class KitReservationResetPatch
{
	private static void Prefix(Labor __instance)
	{
		KitReservation.Release(__instance);
	}
}
