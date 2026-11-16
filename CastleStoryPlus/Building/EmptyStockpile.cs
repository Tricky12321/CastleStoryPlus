using System;
using System.Collections.Generic;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.AI.InstructionType;
using Brix.Game.AI.KnowledgeSpace;
using Brix.Game.AI.Nodes;
using Brix.Game.Components;
using Brix.Game.Network;
using Brix.Game.Semantique;
using Brix.IO.Serialization.Contracts;
using Brix.Legacy;
using Brix.UI.Icons;
using Brix.Utils;
using CastleStoryPlus.Core;
using HarmonyLib;
using Motus.Behavior;
using Newtonsoft.Json.Serialization;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.Building;

// "Empty stockpile": the context wheel of a stockpile (2 x 2, large 3 x 3 or warehouse) has a button that sets it to
// be emptied. While it is set the stockpile takes nothing new (its room is nil, so no worker picks it to store in,
// and a worker on its way finds it full) and idle workers carry what it holds to other storages that take it,
// until it is empty; it then stays empty until the button ("Stop emptying") is pressed again, which gives back its
// room and filters as they were. Handy before deconstructing or moving a stockpile. The stockpile's overhead icon
// is the game's "empty stockpile" icon meanwhile, and its tooltip says "Emptying". Decided by the host: a client
// sends the change, the host syncs it to every client with the stockpile's network state, and it is saved with the
// stockpile ("plusEmptying" on its RecepteurState).
[Feature(Features.EmptyStockpile, Features.EmptyStockpileInfo)]
internal static class EmptyStockpile
{
	private const int ToggleHash = 1162694734;

	// A sync var bit RecepteurState does not use (it uses 0x01 and 0x02); setting it makes UNET send an update.
	internal const uint DirtyBit = 0x40000000u;

	private static readonly HashSet<Recepteur> Emptying = new HashSet<Recepteur>();

	private static void Enable()
	{
		NetworkBehaviour.RegisterCommandDelegate(typeof(UNetRecepteurCmd), ToggleHash, InvokeToggle);
		GameSession.OnLeave(() =>
		{
			Emptying.Clear();
			EmptyStockpileHaul.Clear();
		});
	}

	internal static bool Is(Recepteur recepteur)
	{
		return recepteur != null && Emptying.Count > 0 && Emptying.Contains(recepteur);
	}

	internal static IEnumerable<Recepteur> All => Emptying;

	// The stockpiles the button is for: the game's, the large one and the warehouse (not a worker's hands).
	internal static bool IsStorage(Recepteur recepteur)
	{
		return recepteur != null && recepteur.GetComponent<Labor>() == null && (LargeStockpile.IsStockpileKey(recepteur.AssetKey) || Warehouse.IsWarehouse(recepteur));
	}

	internal static void Set(Recepteur recepteur, bool on)
	{
		if (recepteur == null || on == Is(recepteur))
		{
			return;
		}
		if (on)
		{
			Emptying.Add(recepteur);
		}
		else
		{
			Emptying.Remove(recepteur);
		}
		Apply(recepteur);
		RecepteurState state = recepteur.GetComponent<RecepteurState>();
		if (state != null && NetworkServer.active)
		{
			state.SetDirtyBit(DirtyBit);
		}
	}

	// Room nil while emptying, the normal room otherwise; the workers' knowledge and the overhead icon follow the
	// change signal (the same the game's filters send).
	private static void Apply(Recepteur recepteur)
	{
		if (Is(recepteur))
		{
			Close(recepteur);
		}
		else
		{
			recepteur.ResetDescriptions();
		}
		recepteur.ExclusionChanged.Invoke(null);
	}

	internal static void Close(Recepteur recepteur)
	{
		if (Is(recepteur))
		{
			recepteur.CurrentCapacity.IntersectWith(Description.Empty);
		}
	}

	internal static void Forget(Recepteur recepteur)
	{
		if (recepteur != null)
		{
			Emptying.Remove(recepteur);
		}
	}

	// Context wheel: the selected stockpiles, or the one the wheel was opened over; only the local player's.
	private static List<Recepteur> WheelTargets()
	{
		List<Recepteur> result = new List<Recepteur>();
		if (Instructions.sujets != null)
		{
			foreach (MonoBehaviour subject in Instructions.sujets)
			{
				Add(result, (subject != null) ? subject.GetComponent<Recepteur>() : null);
			}
		}
		if (result.Count == 0 && Instructions.objet is GameObject go && go != null)
		{
			Add(result, go.GetComponentInParent<Recepteur>());
		}
		return result;
	}

	private static void Add(List<Recepteur> list, Recepteur recepteur)
	{
		if (IsStorage(recepteur) && !list.Contains(recepteur) && Affiliation.BelongsToMe(recepteur.gameObject))
		{
			list.Add(recepteur);
		}
	}

	internal const string WheelButton = "PlusEmptyStockpile";

	private const string WheelCondition = "Condition_PlusEmptyStockpile";

	private const string WheelLabel = "Condition_PlusEmptyStockpileLabel";

	private const string WheelEvent = "Event_PlusEmptyStockpile";

	internal static void RegisterWheel()
	{
		Instructions.conditions[WheelCondition] = () => WheelTargets().Count > 0;
		Instructions.conditions[WheelLabel] = () => AllEmptying(WheelTargets()) ? "Stop emptying" : "Empty stockpile";
		Instructions.commendes[WheelEvent] = () =>
		{
			List<Recepteur> targets = WheelTargets();
			if (targets.Count == 0)
			{
				return;
			}
			// Mixed selection: all start emptying; all emptying: all stop.
			bool on = !AllEmptying(targets);
			foreach (Recepteur recepteur in targets)
			{
				Toggle(recepteur, on);
			}
		};
	}

	private static bool AllEmptying(List<Recepteur> targets)
	{
		if (targets.Count == 0)
		{
			return false;
		}
		foreach (Recepteur recepteur in targets)
		{
			if (!Is(recepteur))
			{
				return false;
			}
		}
		return true;
	}

	// The wheel files as they are read: the button's definition after the game's stockpile buttons, and the button in
	// place of the stockpile wheel's last free slot.
	internal static string AddToWheel(string text)
	{
		RegisterWheel();
		if (text.Contains("BUTTON StockpileAcceptAll") && !text.Contains("BUTTON " + WheelButton))
		{
			return text + "\n\nBUTTON " + WheelButton + "\nName : Empty stockpile\nLabel : " + WheelLabel + "\nCondition : " + WheelCondition + "\nEffect : " + WheelEvent + "\nIcon : _Stockpile_Empty\nSize : Native\nTooltip : Take nothing new and have the workers carry everything to other storages (or again to use it as usual). It stays empty until switched off.\n";
		}
		if (!text.Contains("MENU PaletteAnywhere") || text.Contains("Button: " + WheelButton))
		{
			return text;
		}
		List<string> lines = new List<string>(text.Replace("\r\n", "\n").Split('\n'));
		int start = lines.FindIndex((string l) => l.Trim() == "MENU PaletteAnywhere");
		if (start < 0)
		{
			return text;
		}
		int lastHole = -1;
		for (int i = start + 1; i < lines.Count && !lines[i].Trim().StartsWith("MENU "); i++)
		{
			if (lines[i].Trim() == "Hole")
			{
				lastHole = i;
			}
		}
		if (lastHole < 0)
		{
			Plugin.Log.LogWarning("EmptyStockpile: no free slot in the stockpile wheel");
			return text;
		}
		lines[lastHole] = "\tButton: " + WheelButton;
		return string.Join("\n", lines.ToArray());
	}

	// Shown at once; on a client the host decides and syncs it back.
	private static void Toggle(Recepteur recepteur, bool on)
	{
		Set(recepteur, on);
		Plugin.Log.LogInfo("EmptyStockpile: " + recepteur.name + (on ? " is being emptied" : " is used as usual again"));
		UNetRecepteurCmd cmd = (User.LocalUser != null) ? User.LocalUser.GetComponent<UNetRecepteurCmd>() : null;
		if (cmd == null || cmd.isServer)
		{
			return;
		}
		NetworkWriter writer = new NetworkWriter();
		writer.Write((short)0);
		writer.Write((short)5);
		writer.WritePackedUInt32((uint)ToggleHash);
		writer.Write(cmd.GetComponent<NetworkIdentity>().netId);
		writer.Write(recepteur.gameObject);
		writer.Write(on);
		cmd.SendCommandInternal(writer, 0, "CmdSetEmptyStockpile");
	}

	private static void InvokeToggle(NetworkBehaviour obj, NetworkReader reader)
	{
		if (!NetworkServer.active)
		{
			return;
		}
		GameObject go = reader.ReadGameObject();
		bool on = reader.ReadBoolean();
		if (go == null || !Affiliation.IsOwnedBy(go, obj.gameObject))
		{
			return;
		}
		Recepteur recepteur = go.GetComponent<Recepteur>();
		if (IsStorage(recepteur))
		{
			Set(recepteur, on);
		}
	}

	// Network: the flag after RecepteurState's own data, in the initial state and every update.
	internal static void Write(RecepteurState state, NetworkWriter writer)
	{
		writer.Write(Is(state.GetComponent<Recepteur>()));
	}

	internal static void Read(RecepteurState state, NetworkReader reader)
	{
		bool on = reader.ReadBoolean();
		Recepteur recepteur = state.GetComponent<Recepteur>();
		if (recepteur != null && on != Is(recepteur))
		{
			Set(recepteur, on);
		}
	}
}

// Idle chore: carry what an emptying stockpile holds to the best other storage that takes it. Like the game's
// cleanup, only for workers that have nothing else to do (IdleProject), and interruptible until the worker has
// picked something up. A few workers per stockpile at once.
[Feature(Features.EmptyStockpile, Features.EmptyStockpileInfo)]
internal static class EmptyStockpileHaul
{
	private class Haul
	{
		public Recepteur Source;

		public float ClaimedUntil;
	}

	// A claim expires on its own in case a task is dropped without running its Finally.
	private const float ClaimSeconds = 90f;

	private const int MaxPerStockpile = 4;

	private static readonly Dictionary<Labor, Haul> Hauls = new Dictionary<Labor, Haul>();

	private static readonly List<Recepteur> Sources = new List<Recepteur>();

	private static readonly HashSet<Brix.External.Factories.Factory.AssetKey> Tried = new HashSet<Brix.External.Factories.Factory.AssetKey>();

	private static readonly List<Labor> Stale = new List<Labor>();

	public static readonly LaborInstruction<GameObject> Empty = new LaborInstruction<GameObject>("EmptyStockpile", IconKeys.PickUp, (Labor labor, GameObject item) => HaulNode(labor, item));

	internal static void Clear()
	{
		Hauls.Clear();
	}

	// The worker is carrying a load out of an emptying stockpile.
	internal static bool IsCarrying(Labor labor)
	{
		return labor != null && labor.CurrentTask != null && Hauls.ContainsKey(labor);
	}

	private static void Release(Labor labor)
	{
		if (labor != null)
		{
			Hauls.Remove(labor);
		}
	}

	// An item of the nearest emptying stockpile of the worker's team that it can reach, carry and store elsewhere;
	// null when there is none.
	internal static GameObject TryPlan(Labor labor)
	{
		if (!NetworkServer.active || labor == null || !labor.recepteur.IsEmpty())
		{
			return null;
		}
		Expire();
		Sources.Clear();
		foreach (Recepteur pile in EmptyStockpile.All)
		{
			if (pile != null && !pile.IsNullOrReleased() && pile.gameObject.activeInHierarchy && pile.CarriesSomething() && pile.faction == labor.faction && Count(pile) < MaxPerStockpile)
			{
				Sources.Add(pile);
			}
		}
		if (Sources.Count == 0)
		{
			return null;
		}
		Vector3 from = labor.transform.position;
		Sources.Sort((Recepteur a, Recepteur b) => (a.transform.position - from).sqrMagnitude.CompareTo((b.transform.position - from).sqrMagnitude));
		foreach (Recepteur pile in Sources)
		{
			if (!Recepteur.CharactersCouldGrabFrom(pile.gameObject) || !Knowledge.Instance.CanReach(labor, pile))
			{
				continue;
			}
			// One try per kind of item: the destination only depends on the kind.
			Tried.Clear();
			foreach (IDescriptor stored in pile.StoredItems)
			{
				GameObject item = (stored != null) ? stored.GameObject : null;
				GameComponent component = (item != null) ? item.GetComponent<GameComponent>() : null;
				if (component == null || !Tried.Add(component.AssetKey))
				{
					continue;
				}
				if (!labor.recepteur.HasRoomFor(item))
				{
					continue;
				}
				GameObject destination = labor.BestRecepteurToStore(item);
				if (destination == null || destination == pile.gameObject)
				{
					continue;
				}
				Hauls[labor] = new Haul { Source = pile, ClaimedUntil = Time.time + ClaimSeconds };
				return item;
			}
		}
		return null;
	}

	private static Node HaulNode(Labor labor, GameObject item)
	{
		GameComponent itemComponent = (item != null) ? item.GetComponent<GameComponent>() : null;
		if (itemComponent == null || !Hauls.ContainsKey(labor))
		{
			Release(labor);
			return Node.Empty;
		}
		return Node.Sequence.Label("Empty stockpile")
			.Do(labor.GoPickUp(itemComponent, -1))
			.Label("Pick up from emptying stockpile")
			.Do((Labor l) =>
			{
				// Carrying now: stop being interruptible so the load is not dumped again.
				l.Activity = Activity.Idle;
			}, labor)
			.Do(labor.GoStoreInBestStorage())
			.Label("Store elsewhere")
			.Finally((Labor l) =>
			{
				Release(l);
			}, labor);
	}

	private static int Count(Recepteur pile)
	{
		int count = 0;
		foreach (Haul haul in Hauls.Values)
		{
			if (haul.Source == pile)
			{
				count++;
			}
		}
		return count;
	}

	private static void Expire()
	{
		Stale.Clear();
		foreach (KeyValuePair<Labor, Haul> pair in Hauls)
		{
			if (pair.Key == null || pair.Value.Source == null || Time.time > pair.Value.ClaimedUntil)
			{
				Stale.Add(pair.Key);
			}
		}
		foreach (Labor labor in Stale)
		{
			Hauls.Remove(labor);
		}
		Stale.Clear();
	}
}

// Idle workers empty stockpiles after picking up loose items (AutoCleanup, higher priority) and before tidying
// stockpiles (StockpileConsolidation). A worker carrying such a load keeps doing it: the idle project would give it a
// "store or drop" order instead.
[Feature(Features.EmptyStockpile, Features.EmptyStockpileInfo)]
[HarmonyPatch(typeof(IdleProject), nameof(IdleProject._GetInstruction))]
[HarmonyPriority(Priority.HigherThanNormal)]
internal static class EmptyStockpileIdlePatch
{
	private static bool Prefix(IdleProject __instance, Labor labor, Var<OrderPackage<Labor>> result, bool __runOriginal)
	{
		if (!__runOriginal || labor == null)
		{
			return __runOriginal;
		}
		if (!labor.recepteur.IsEmpty())
		{
			if (!EmptyStockpileHaul.IsCarrying(labor))
			{
				return true;
			}
			result.Set(OrderPackage<Labor>.Empty);
			return false;
		}
		if (!__instance.GatherAtIdlePoint(labor) || !WorkerAI.AssignedWork.IdleChoresFor(labor))
		{
			return true;
		}
		GameObject item = EmptyStockpileHaul.TryPlan(labor);
		if (item == null)
		{
			return true;
		}
		OrderPackage<Labor> package = Order.Package.OfGameObject(EmptyStockpileHaul.Empty, item, Labor.DecisionSource.Project);
		// Interruptible until the worker picks something up, so real work always wins.
		package.SetPrepare((BaseLabor worker) => __instance.Prepare(worker as Labor, false, Activity.Chilling));
		result.Set(package);
		return false;
	}
}

// No room while emptying: after every recount of the stockpile's room (filters, adding and taking items).
[Feature(Features.EmptyStockpile, Features.EmptyStockpileInfo)]
[HarmonyPatch]
internal static class EmptyStockpileRoomPatch
{
	private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
	{
		yield return AccessTools.Method(typeof(Recepteur), "RegenerateDescriptionsWithExclusions");
		yield return AccessTools.Method(typeof(Recepteur), nameof(Recepteur.ResetDescriptions));
	}

	[HarmonyPriority(Priority.Last)]
	private static void Postfix(Recepteur __instance)
	{
		EmptyStockpile.Close(__instance);
	}
}

[Feature(Features.EmptyStockpile, Features.EmptyStockpileInfo)]
[HarmonyPatch]
internal static class EmptyStockpileAddRemovePatch
{
	private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
	{
		yield return AccessTools.Method(typeof(Recepteur), nameof(Recepteur.AddTo));
		yield return AccessTools.Method(typeof(Recepteur), nameof(Recepteur.RemoveFrom));
	}

	[HarmonyPriority(Priority.Last)]
	private static void Postfix(IRecepteur recept)
	{
		EmptyStockpile.Close(recept as Recepteur);
	}
}

// With no room the stockpile counts as full, and the game converts a full stockpile of stone into bricks.
[Feature(Features.EmptyStockpile, Features.EmptyStockpileInfo)]
[HarmonyPatch]
internal static class EmptyStockpileConvertPatch
{
	private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
	{
		yield return AccessTools.PropertyGetter(typeof(ResourceConverter), nameof(ResourceConverter.CurrentAutoConvertRule));
		yield return AccessTools.PropertyGetter(typeof(ResourceConverter), nameof(ResourceConverter.CurrentConvertRule));
	}

	private static bool Prefix(ResourceConverter __instance, ref ResourceConverter.ConversionRule __result)
	{
		if (!EmptyStockpile.Is(__instance.GetComponent<Recepteur>()))
		{
			return true;
		}
		__result = null;
		return false;
	}
}

// A stockpile that goes back to the pool is used as usual when it is placed again.
[Feature(Features.EmptyStockpile, Features.EmptyStockpileInfo)]
[HarmonyPatch(typeof(Recepteur), nameof(Recepteur.ResetComponent))]
internal static class EmptyStockpileResetPatch
{
	private static void Prefix(Recepteur __instance)
	{
		EmptyStockpile.Forget(__instance);
	}
}

// The overhead icon of an emptying stockpile: the game's "empty stockpile" icon.
[Feature(Features.EmptyStockpile, Features.EmptyStockpileInfo)]
[HarmonyPatch(typeof(RecepteurOverheadDisplayDriver), "SetBigIcon")]
internal static class EmptyStockpileIconPatch
{
	private static void Postfix(RecepteurOverheadDisplayDriver __instance)
	{
		if (__instance.overheadDisplay == null || !EmptyStockpile.Is(__instance.recepteur))
		{
			return;
		}
		__instance.overheadDisplay.BigIcon.Get.SetIcon(IconKeys._Stockpile_Empty.Get64());
		__instance.overheadDisplay.ShowBigIcon(true);
		__instance.overheadDisplay.BigIcon.Get.Show = true;
	}
}

[Feature(Features.EmptyStockpile, Features.EmptyStockpileInfo)]
[HarmonyPatch(typeof(Instructions), nameof(Instructions.Batire))]
internal static class EmptyStockpileWheelCommandsPatch
{
	private static void Postfix()
	{
		EmptyStockpile.RegisterWheel();
	}
}

[Feature(Features.EmptyStockpile, Features.EmptyStockpileInfo)]
[HarmonyPatch(typeof(Livre), nameof(Livre.Split))]
internal static class EmptyStockpileWheelFilesPatch
{
	private static void Prefix(ref string contenu)
	{
		if (contenu != null)
		{
			contenu = EmptyStockpile.AddToWheel(contenu);
		}
	}
}

// Multiplayer: the flag after RecepteurState's own sync vars. Both ends run the mod.
[Feature(Features.EmptyStockpile, Features.EmptyStockpileInfo)]
[HarmonyPatch(typeof(RecepteurState), nameof(RecepteurState.OnSerialize))]
internal static class EmptyStockpileNetworkWrite
{
	private static void Postfix(RecepteurState __instance, NetworkWriter writer, ref bool __result)
	{
		EmptyStockpile.Write(__instance, writer);
		__result = true;
	}
}

[Feature(Features.EmptyStockpile, Features.EmptyStockpileInfo)]
[HarmonyPatch(typeof(RecepteurState), nameof(RecepteurState.OnDeserialize))]
internal static class EmptyStockpileNetworkRead
{
	private static void Postfix(RecepteurState __instance, NetworkReader reader)
	{
		EmptyStockpile.Read(__instance, reader);
	}
}

// Saves: an extra "plusEmptying" property on the stockpile's RecepteurState. The unmodded game ignores it.
[Feature(Features.EmptyStockpile, Features.EmptyStockpileInfo)]
[HarmonyPatch(typeof(DefaultGameContractResolver), nameof(DefaultGameContractResolver.GenerateDefaultMonoBehaviourContract))]
internal static class EmptyStockpileSave
{
	private const string Property = "plusEmptying";

	private class EmptyingProvider : IValueProvider
	{
		public object GetValue(object target)
		{
			RecepteurState state = target as RecepteurState;
			return state != null && EmptyStockpile.Is(state.GetComponent<Recepteur>());
		}

		public void SetValue(object target, object value)
		{
			RecepteurState state = target as RecepteurState;
			Recepteur recepteur = (state != null) ? state.GetComponent<Recepteur>() : null;
			if (EmptyStockpile.IsStorage(recepteur))
			{
				EmptyStockpile.Set(recepteur, Convert.ToBoolean(value));
			}
		}
	}

	private static void Postfix(Type objectType, JsonObjectContract __result)
	{
		if (objectType != typeof(RecepteurState) || __result == null || __result.Properties.Contains(Property))
		{
			return;
		}
		__result.Properties.AddProperty(new JsonProperty
		{
			PropertyName = Property,
			UnderlyingName = Property,
			PropertyType = typeof(bool),
			DeclaringType = typeof(RecepteurState),
			ValueProvider = new EmptyingProvider(),
			Readable = true,
			Writable = true
		});
	}
}
