#if UNITY6
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Brix.Components;
using Brix.Game;
using Brix.Game.Components;
using Brix.Game.Components.Tracking;
using Brix.Game.Network;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;
using TrackingEntry = Brix.Game.Components.Tracking.TrackingRegistry.TrackingEntry;
using TrackingGroup = Brix.Game.Components.Tracking.TrackingRegistry.TrackingGroup;

namespace CastleStoryPlus.Performance;

// Unity 6 builds only (new performance work goes to the port). The step of the faction vision pass that goes over the
// units and items of the groups a faction sees fully (RefreshActiveCollisionsEnumerator.AssignCollisionsFromFullGroup)
// was most of the pass's cost: 0.4-0.7 ms a frame and up to 17 ms in one frame. A base's stored resources are tracked too (a stockpile group holds some 2300 entries)
// and every pass of every faction asked each of them the same questions, about 0.6 us an entry spent mostly waiting on
// memory: its faction, its vision, and whether it answers for its own visibility (LocalFactionVision.
// IsResponsibleForVision). An item held by a stockpile does not, and then nothing at all happens for it.
// - Such an entry is now remembered per group as held (by which holder), and passed over without being looked at while
//   that stays true: the group's entries are mirrored (TrackingGroup.AddEntry and Remove, which swaps the last entry into
//   the gap), an item whose parent tracker changes is looked at again (GenericParentTracker's currentTracker is only set
//   through NetworkcurrentTracker and on a client in OnDeserialize; releasing the item sets it too), and each holder is
//   checked once per call (alive, not released, still has its tracker), as the game checks it per item. Held does not
//   depend on the faction, so all factions share it. The other entries go through the game's steps in the game's order.
// - The holder's components (its IBrixComponent and its tracker) were looked up with GetComponent for every item; they
//   are kept per holder as long as they live (one gone is looked up again, one not found is never kept). The same check
//   serves VisionScan's partial groups.
// The only difference: the game logs an error for an entry whose CollisionTrackingId is out of range before asking
// whether it is held; a held entry passed over is not logged. CSP_VISION_VERIFY=1 runs the game's own steps for every
// entry passed over and the game's IsResponsibleForVision next to ours, and logs any difference.
[Feature(Features.FrameSavers, Features.FrameSaversInfo)]
internal static class VisionCollisions
{
	private sealed class Holder
	{
		public Object Brix;

		public IBrixComponent BrixComponent;

		// Per TrackingTypes (grip, ressource, character, blueprint).
		public readonly Object[] Trackers = new Object[5];
	}

	// A holder of a group's held entries: the parent tracker's TrackingPair.
	private struct HeldBy
	{
		public GameObject Go;

		public int Type;

		public Holder Holder;
	}

	private sealed class GroupCache
	{
		// Per entry index: -1 to be looked at, otherwise held by Holders[slot].
		public short[] Slots = new short[64];

		public int Count;

		public readonly List<HeldBy> Holders = new List<HeldBy>();

		public bool[] Valid = new bool[8];

		public void Reset(int count)
		{
			if (Slots.Length < count)
			{
				Slots = new short[count * 2];
			}
			for (int i = 0; i < count; i++)
			{
				Slots[i] = -1;
			}
			Count = count;
			Holders.Clear();
		}
	}

	// Compared by reference: Unity's own Equals would ask the engine for each lookup.
	private sealed class ByReference<T> : IEqualityComparer<T> where T : class
	{
		public bool Equals(T a, T b)
		{
			return ReferenceEquals(a, b);
		}

		public int GetHashCode(T o)
		{
			return RuntimeHelpers.GetHashCode(o);
		}
	}

	// Past these the tables are emptied (with every group's held entries, which need the watch on their trackers).
	private const int MaxHolders = 4096;

	private const int MaxWatched = 200000;

	private const int MaxHoldersPerGroup = 256;

	private static readonly Dictionary<GameObject, Holder> HolderTable = new Dictionary<GameObject, Holder>(new ByReference<GameObject>());

	private static readonly Dictionary<TrackingGroup, GroupCache> Groups = new Dictionary<TrackingGroup, GroupCache>(new ByReference<TrackingGroup>());

	// The parent tracker of each entry remembered as held: when its currentTracker is set, the entry is looked at again.
	private static readonly Dictionary<GenericParentTracker, TrackingEntry> Watched = new Dictionary<GenericParentTracker, TrackingEntry>(new ByReference<GenericParentTracker>());

	private static readonly bool Verify = System.Environment.GetEnvironmentVariable("CSP_VISION_VERIFY") == "1";

	private static int _mainThread = -1;

	// Set when a group changed off the main thread: everything remembered is dropped at the next call.
	private static volatile bool _dropAll;

	private static long _checks;

	private static long _mismatches;

	private static long _skipped;

	private static long _skipMismatches;

	private static long _entries;

	private static void Enable()
	{
		GameSession.OnLeave(DropAll);
	}

	private static void Disable()
	{
		DropAll();
	}

	private static void DropAll()
	{
		HolderTable.Clear();
		Groups.Clear();
		Watched.Clear();
		_dropAll = false;
	}

	// RefreshActiveCollisionsEnumerator.AssignCollisionsFromFullGroup: the game's steps for every entry not held.
	internal static void AssignFromFullGroup(SharedFactionVision shared, TrackingGroup fullGroup)
	{
		if (_mainThread < 0)
		{
			_mainThread = Thread.CurrentThread.ManagedThreadId;
		}
		if (_dropAll)
		{
			DropAll();
		}
		int enemies = shared.EnemyOrNeutralFactions;
		if ((fullGroup.trackedFactions & enemies) == 0)
		{
			return;
		}
		List<LocalFactionVision> whoSeesMe = VisionScan.WhoSeesMe(shared);
		List<LocalFactionVision> collisions = VisionScan.TrackingCollisions(shared);
		List<TrackingEntry> entries = fullGroup.Entries;
		int count = entries.Count;
		if (!Groups.TryGetValue(fullGroup, out GroupCache cache))
		{
			cache = new GroupCache();
			cache.Reset(count);
			Groups[fullGroup] = cache;
		}
		else if (cache.Count != count || cache.Holders.Count > MaxHoldersPerGroup)
		{
			cache.Reset(count);
		}
		CheckHolders(cache);
		short[] slots = cache.Slots;
		bool[] valid = cache.Valid;
		for (int i = 0; i < count; i++)
		{
			short slot = slots[i];
			if (slot >= 0)
			{
				if (valid[slot])
				{
					if (Verify)
					{
						VerifySkipped(shared, entries[i], whoSeesMe);
					}
					continue;
				}
				slots[i] = -1;
			}
			TrackingEntry entry = entries[i];
			if (((1 << entry.faction.ID) & enemies) == 0)
			{
				// Not this faction's business, but held or not is the same for all of them.
				LocalFactionVision other = entry.Vision;
				if (!other.IsNullOrReleased())
				{
					Classify(other, cache, i, entry, false);
				}
				continue;
			}
			LocalFactionVision vision = entry.Vision;
			if (vision.IsNullOrReleased() || vision.CollisionTrackingId == -1)
			{
				continue;
			}
			if (vision.CollisionTrackingId >= 1 + whoSeesMe.Count)
			{
				Plugin.Log.LogWarning("VisionCollisions: CollisionTrackingId=" + vision.CollisionTrackingId + ", _whoSeesMe.Count=" + whoSeesMe.Count + ", collisions=" + collisions.Count);
			}
			else if (Classify(vision, cache, i, entry, true))
			{
				if (vision.CollisionTrackingId >= 1)
				{
					whoSeesMe[vision.CollisionTrackingId - 1] = null;
					vision.CollisionTrackingId = -1;
				}
				else
				{
					whoSeesMe.Add(null);
					collisions.Add(vision);
				}
			}
		}
		if (Verify)
		{
			_entries += count;
		}
	}

	// Each holder of the group checked once: TrackingPair.IsValid for its go and type.
	private static void CheckHolders(GroupCache cache)
	{
		List<HeldBy> holders = cache.Holders;
		if (cache.Valid.Length < holders.Count)
		{
			cache.Valid = new bool[holders.Count * 2];
		}
		for (int h = 0; h < holders.Count; h++)
		{
			HeldBy held = holders[h];
			cache.Valid[h] = Tracks(held.Go, held.Type, held.Holder);
		}
	}

	// IsResponsibleForVision (as Responsible); held entries are remembered in the group's cache.
	// asked: the game asks too (an entry of another faction is only classified, and must not throw where the game does).
	private static bool Classify(LocalFactionVision vision, GroupCache cache, int index, TrackingEntry entry, bool asked)
	{
		bool responsible = Responsible(vision, asked, out GenericParentTracker parentTracker, out GameObject go, out int type, out Holder holder);
		if (responsible || go == null || cache.Holders.Count >= MaxHoldersPerGroup)
		{
			return responsible;
		}
		int slot = -1;
		List<HeldBy> holders = cache.Holders;
		for (int h = 0; h < holders.Count; h++)
		{
			if (ReferenceEquals(holders[h].Go, go) && holders[h].Type == type)
			{
				slot = h;
				break;
			}
		}
		if (slot < 0)
		{
			slot = holders.Count;
			holders.Add(new HeldBy { Go = go, Type = type, Holder = holder });
			if (cache.Valid.Length <= slot)
			{
				bool[] grown = new bool[cache.Valid.Length * 2];
				System.Array.Copy(cache.Valid, grown, cache.Valid.Length);
				cache.Valid = grown;
			}
			// Just checked by Responsible.
			cache.Valid[slot] = true;
		}
		if (Watched.Count >= MaxWatched)
		{
			DropAll();
			return responsible;
		}
		Watched[parentTracker] = entry;
		cache.Slots[index] = (short)slot;
		return responsible;
	}

	// The game's steps for an entry passed over as held, which must do nothing.
	private static void VerifySkipped(SharedFactionVision shared, TrackingEntry entry, List<LocalFactionVision> whoSeesMe)
	{
		_skipped++;
		bool acts = false;
		if (((1 << entry.faction.ID) & shared.EnemyOrNeutralFactions) != 0)
		{
			LocalFactionVision vision = entry.Vision;
			if (!vision.IsNullOrReleased() && vision.CollisionTrackingId != -1 && vision.CollisionTrackingId < 1 + whoSeesMe.Count)
			{
				acts = LocalFactionVision.IsResponsibleForVision(vision);
			}
		}
		if (acts)
		{
			_skipMismatches++;
			Plugin.Log.LogWarning("VisionCollisions: passed over " + entry.Vision.name + ", which the game would have counted");
		}
		if (_skipped % 500000 == 0)
		{
			Plugin.Log.LogInfo("VisionCollisions: " + _skipped + " entries passed over of " + _entries + " in full groups, " + _skipMismatches + " wrongly; " + _checks + " checks of IsResponsibleForVision, " + _mismatches + " differed; " + Groups.Count + " groups, " + Watched.Count + " watched");
		}
	}

	// LocalFactionVision.IsResponsibleForVision with the holder's components kept (see above).
	internal static bool Responsible(LocalFactionVision vision)
	{
		return Responsible(vision, true, out _, out _, out _, out _);
	}

	private static bool Responsible(LocalFactionVision vision, bool asked, out GenericParentTracker parentTracker, out GameObject go, out int type, out Holder holder)
	{
		bool fast = IsResponsible(vision, asked, out parentTracker, out go, out type, out holder);
		if (Verify && asked)
		{
			_checks++;
			bool game = LocalFactionVision.IsResponsibleForVision(vision);
			if (fast != game)
			{
				_mismatches++;
				Plugin.Log.LogWarning("VisionCollisions: " + vision.name + " responsible " + fast + ", the game says " + game);
				go = null;
				return game;
			}
		}
		return fast;
	}

	// go: the holder when held (not responsible because the parent tracker is tracked), else null.
	private static bool IsResponsible(LocalFactionVision other, bool asked, out GenericParentTracker parentTracker, out GameObject go, out int type, out Holder holder)
	{
		go = null;
		type = 0;
		holder = null;
		parentTracker = null;
		if (other.IgnoreParentVision)
		{
			return true;
		}
		parentTracker = other.ParentTracker;
		if (parentTracker.IsNullOrReleased())
		{
			return true;
		}
		if (parentTracker is CharacterParentTracker)
		{
			return true;
		}
		// GenericParentTracker.IsTracked: TrackingPair.IsValid.
		TrackingPair pair = parentTracker.NetworkcurrentTracker;
		GameObject at = pair.go;
		if (at == null)
		{
			return true;
		}
		int kind = pair.type;
		if (kind < 1 || kind > 4)
		{
			// Not a type the game has a tracker for: its own answer (it throws as the game would).
			return !asked || !parentTracker.IsTracked();
		}
		if (!HolderTable.TryGetValue(at, out Holder found))
		{
			if (HolderTable.Count >= MaxHolders)
			{
				HolderTable.Clear();
			}
			found = new Holder();
			HolderTable[at] = found;
		}
		if (!Tracks(at, kind, found))
		{
			return true;
		}
		go = at;
		type = kind;
		holder = found;
		return false;
	}

	// TrackingPair.IsValid: !go.IsNullOrReleased() && the tracker of its type on go.
	private static bool Tracks(GameObject go, int type, Holder holder)
	{
		if (go == null)
		{
			return false;
		}
		// GameObject.IsNullOrReleased: the first IBrixComponent of go. A component is added after the others, so the one
		// found stays the first as long as it lives. None found: go counts as not released, as in the game.
		if (holder.Brix == null)
		{
			holder.BrixComponent = go.GetComponent<IBrixComponent>();
			holder.Brix = holder.BrixComponent as Object;
		}
		if (holder.Brix != null && (holder.BrixComponent.Released || holder.BrixComponent.Destroyed))
		{
			return false;
		}
		if (holder.Trackers[type] == null)
		{
			holder.Trackers[type] = Tracker(go, type) as Object;
		}
		return holder.Trackers[type] != null;
	}

	private static IObjectTracker Tracker(GameObject go, int type)
	{
		switch (type)
		{
		case 1:
			return go.GetComponent<IGripTracker>();
		case 2:
			return go.GetComponent<IResourceTracker>();
		case 3:
			return go.GetComponent<ICharacterTracker>();
		default:
			return go.GetComponent<IBlueprintTracker>();
		}
	}

	private static bool OnMainThread()
	{
		if (_mainThread >= 0 && Thread.CurrentThread.ManagedThreadId != _mainThread)
		{
			_dropAll = true;
			return false;
		}
		return true;
	}

	// TrackingGroup.AddEntry appends: the new entry is looked at.
	internal static void Added(TrackingGroup group)
	{
		if (Groups.Count == 0 || !OnMainThread() || !Groups.TryGetValue(group, out GroupCache cache))
		{
			return;
		}
		int count = group.Entries.Count;
		if (count != cache.Count + 1)
		{
			Groups.Remove(group);
			return;
		}
		if (cache.Slots.Length < count)
		{
			short[] grown = new short[count * 2];
			System.Array.Copy(cache.Slots, grown, cache.Count);
			cache.Slots = grown;
		}
		cache.Slots[count - 1] = -1;
		cache.Count = count;
	}

	// TrackingGroup.Remove (before it): the last entry moves into the removed one's place.
	internal static void Removing(TrackingGroup group, TrackingEntry entry)
	{
		if (Groups.Count == 0 || !OnMainThread() || !Groups.TryGetValue(group, out GroupCache cache))
		{
			return;
		}
		int count = group.Entries.Count;
		int index = entry.TrackingGroupIndex;
		if (count != cache.Count || count == 0 || index < 0 || index >= count || !ReferenceEquals(group.Entries[index], entry))
		{
			Groups.Remove(group);
			return;
		}
		cache.Slots[index] = cache.Slots[count - 1];
		cache.Count = count - 1;
	}

	// TrackingGroup.ResetObject: the group goes back to the pool.
	internal static void Reset(TrackingGroup group)
	{
		if (Groups.Count == 0 || !OnMainThread())
		{
			return;
		}
		Groups.Remove(group);
	}

	// A parent tracker's currentTracker was set: its entry is looked at again.
	internal static void TrackerSet(GenericParentTracker tracker)
	{
		if (Watched.Count == 0 || !OnMainThread() || !Watched.TryGetValue(tracker, out TrackingEntry entry))
		{
			return;
		}
		Watched.Remove(tracker);
		TrackingGroup group = entry.TrackingGroup;
		if (group == null || !Groups.TryGetValue(group, out GroupCache cache))
		{
			return;
		}
		int index = entry.TrackingGroupIndex;
		if (index >= 0 && index < cache.Count)
		{
			cache.Slots[index] = -1;
		}
	}
}

[Feature(Features.FrameSavers, Features.FrameSaversInfo)]
[HarmonyPatch]
internal static class VisionCollisionsFullPatch
{
	private static MethodBase TargetMethod()
	{
		return VisionScan.Nested("RefreshActiveCollisionsEnumerator", "AssignCollisionsFromFullGroup");
	}

	// After the frame monitor's timer, so the vision: row still times this step.
	[HarmonyPriority(Priority.Low)]
	private static bool Prefix(SharedFactionVision ____sharedVision, TrackingGroup fullGroup)
	{
		if (____sharedVision == null || fullGroup == null)
		{
			return true;
		}
		VisionCollisions.AssignFromFullGroup(____sharedVision, fullGroup);
		return false;
	}
}

[Feature(Features.FrameSavers, Features.FrameSaversInfo)]
[HarmonyPatch(typeof(TrackingGroup), nameof(TrackingGroup.AddEntry))]
internal static class VisionCollisionsAddedPatch
{
	private static void Postfix(TrackingGroup __instance)
	{
		VisionCollisions.Added(__instance);
	}
}

[Feature(Features.FrameSavers, Features.FrameSaversInfo)]
[HarmonyPatch(typeof(TrackingGroup), nameof(TrackingGroup.Remove))]
internal static class VisionCollisionsRemovePatch
{
	private static void Prefix(TrackingGroup __instance, TrackingEntry entry)
	{
		VisionCollisions.Removing(__instance, entry);
	}
}

[Feature(Features.FrameSavers, Features.FrameSaversInfo)]
[HarmonyPatch(typeof(TrackingGroup), nameof(TrackingGroup.ResetObject))]
internal static class VisionCollisionsResetPatch
{
	private static void Prefix(TrackingGroup __instance)
	{
		VisionCollisions.Reset(__instance);
	}
}

[Feature(Features.FrameSavers, Features.FrameSaversInfo)]
[HarmonyPatch(typeof(GenericParentTracker), nameof(GenericParentTracker.NetworkcurrentTracker), MethodType.Setter)]
internal static class VisionCollisionsTrackerPatch
{
	private static void Postfix(GenericParentTracker __instance)
	{
		VisionCollisions.TrackerSet(__instance);
	}
}

// On a client the tracker arrives with the object's state.
[Feature(Features.FrameSavers, Features.FrameSaversInfo)]
[HarmonyPatch(typeof(GenericParentTracker), nameof(GenericParentTracker.OnDeserialize))]
internal static class VisionCollisionsTrackerReadPatch
{
	private static void Postfix(GenericParentTracker __instance)
	{
		VisionCollisions.TrackerSet(__instance);
	}
}
#endif
