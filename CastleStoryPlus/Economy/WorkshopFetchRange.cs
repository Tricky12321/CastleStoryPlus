using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.AI.KnowledgeSpace;
using Brix.Game.Components;
using Brix.Game.Network;
using Brix.Game.Semantique;
using Brix.IO.Serialization.Contracts;
using CastleStoryPlus.Core;
using HarmonyLib;
using Newtonsoft.Json.Serialization;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.Economy;

// How far a workshop's workers may go for its ingredients. A worker fetching for a workshop (chopping block, furnace,
// market...) took the nearest thing holding the ingredient anywhere on the map: with no logs in the stockpiles, the
// chopping block's workers on loop ran hundreds of blocks out to logs lying where enemies were. Now only stockpiles
// and loose items within the workshop's range (counted on the ground, from the workshop) are taken; with none in
// range the worker finds nothing and the workshop waits. The range is set per workshop under its recipes ("-" and
// "+", Shift 25 at a time, past the largest step "No limit"); a workshop not set has [WorkshopFetchRange] Default.
// While a workshop is selected, a ring on the ground around it shows its range.
// Saved with the workshop (an extra "fetchRange" property) and synced to multiplayer clients; clients send changes to
// the host.
[Feature(Features.WorkshopFetchRange, Features.WorkshopFetchRangeInfo)]
internal static class WorkshopFetchRange
{
	private const int SetRangeHash = 1129595237;

	// A sync var bit CraftingState does not use (WideRecipeIds, CraftLoopLimit and WorkshopPriority use 0x20000000,
	// 0x40000000 and 0x10000000).
	internal const uint DirtyBit = 0x08000000u;

	internal const int NoLimit = 0;

	internal const int Min = 10;

	internal const int Max = 250;

	private const int Step = 5;

	private const int ShiftStep = 25;

	// A workshop's own range; a workshop not in here has the default.
	private static readonly Dictionary<CraftingState, int> Ranges = new Dictionary<CraftingState, int>();

	internal static ConfigEntry<int> Default;

	// The workshop the running search fetches for, and its range (NoLimit: none).
	private static Vector3 _origin;

	private static int _range;

	private static void Enable()
	{
		Default = Plugin.Cfg.Bind("WorkshopFetchRange", "Default", 40, new ConfigDescription("How far, in blocks from the workshop, its workers go for its ingredients (stockpiles and loose items), for every workshop not set in its own menu. 0: no limit.", new AcceptableValueRange<int>(0, 1000)));
		GameSession.OnLeave(Ranges.Clear);
		NetworkBehaviour.RegisterCommandDelegate(typeof(UNetProjectCmd), SetRangeHash, InvokeSetRange);
		GameUi.GameMenuItems.Extend(AddToMenu);
		Plugin.Root.AddComponent<WorkshopFetchRangeRing>();
	}

	// The C# in-game menu: a "Fetch within" row under the recipes, "-  40  +".
	private static void AddToMenu(GameUi.GameMenuModel menu)
	{
		GameUi.GamePopupMenu.WorkshopSettings(menu.Popup).Add(new GameUi.UiStepper
		{
			Id = "workshopFetchRange",
			Label = () => "Fetch within",
			Value = () => Shown(Get(Selected())),
			Tooltip = () => "How far from this workshop, in blocks, its workers go for its ingredients, to stockpiles and loose items (Shift: " + ShiftStep + " at a time). Past " + Max + ": no limit.",
			// -1 / +1, or -10 / +10 with Shift.
			Step = (int delta) => StepSelected(Math.Sign(delta), Math.Abs(delta) > 1)
		});
	}

	private static string Shown(int range)
	{
		return (range == NoLimit) ? "No limit" : range.ToString();
	}

	internal static int Get(CraftingState state)
	{
		if (state != null && Ranges.TryGetValue(state, out int range))
		{
			return range;
		}
		return (Default != null) ? Mathf.Max(0, Default.Value) : NoLimit;
	}

	// -1: back to the default.
	internal static void Set(CraftingState state, int range)
	{
		if (state == null)
		{
			return;
		}
		bool had = Ranges.TryGetValue(state, out int current);
		if (range < 0)
		{
			if (!had)
			{
				return;
			}
			Ranges.Remove(state);
		}
		else
		{
			if (had && current == range)
			{
				return;
			}
			Ranges[state] = range;
		}
		if (NetworkServer.active)
		{
			state.SetDirtyBit(DirtyBit);
		}
	}

	// The value saved and synced: the workshop's own range, or -1 for the default.
	internal static int Raw(CraftingState state)
	{
		return (state != null && Ranges.TryGetValue(state, out int range)) ? range : -1;
	}

	internal static int FromRaw(int raw)
	{
		return (raw < 0) ? -1 : Mathf.Clamp(raw, 0, 1000);
	}

	private static CraftingState Selected()
	{
		CraftingStation station = SelectedStation();
		return (station != null) ? station.state : null;
	}

	// The next range: steps of 5 (Shift 25) between Min and Max, and past Max no limit.
	internal static int Next(int range, int direction, bool shift)
	{
		int step = shift ? ShiftStep : Step;
		if (range == NoLimit)
		{
			return (direction < 0) ? Max : NoLimit;
		}
		int next = range + direction * step;
		if (next > Max)
		{
			return NoLimit;
		}
		return Mathf.Max(Min, next);
	}

	// Shown at once; the host's value follows.
	private static bool StepSelected(int direction, bool shift)
	{
		CraftingState state = Selected();
		if (state == null || direction == 0)
		{
			return false;
		}
		int current = Get(state);
		int range = Next(current, direction, shift);
		if (range == current)
		{
			return false;
		}
		Set(state, range);
		UNetProjectCmd cmd = (User.LocalUser != null) ? User.LocalUser.GetComponent<UNetProjectCmd>() : null;
		if (cmd == null || cmd.isServer)
		{
			return true;
		}
		NetworkWriter writer = new NetworkWriter();
		writer.Write((short)0);
		writer.Write((short)5);
		writer.WritePackedUInt32((uint)SetRangeHash);
		writer.Write(cmd.GetComponent<NetworkIdentity>().netId);
		writer.Write(state.gameObject);
		writer.WritePackedUInt32((uint)range);
		cmd.SendCommandInternal(writer, 0, "CmdSetWorkshopFetchRange");
		return true;
	}

	private static void InvokeSetRange(NetworkBehaviour obj, NetworkReader reader)
	{
		if (!NetworkServer.active)
		{
			return;
		}
		GameObject station = reader.ReadGameObject();
		int range = FromRaw((int)reader.ReadPackedUInt32());
		if (station == null || !Affiliation.IsOwnedBy(station, obj.gameObject))
		{
			return;
		}
		Set(station.GetComponent<CraftingState>(), range);
	}

	// A worker starts looking for ingredients: when it fetches for a workshop with a range, the search is limited.
	internal static bool Begin(Labor labor)
	{
		GameObject giver = (labor != null && labor.CurrentTask != null) ? labor.CurrentTask.Giver : null;
		CraftingStation station = (giver != null) ? giver.GetComponent<CraftingStation>() : null;
		int range = (station != null) ? Get(station.state) : NoLimit;
		if (range == NoLimit)
		{
			return false;
		}
		_origin = station.transform.position;
		_range = range;
		return true;
	}

	internal static void End()
	{
		_range = NoLimit;
	}

	// The game's selected workshop; its crafting selection exists only in a game (null in the menus and while a world
	// loads, where the game's own lookup throws).
	private static CraftingStation SelectedStation()
	{
		return (UIGameObserver.crafting != null) ? CraftingStation.SelectedStation() : null;
	}

	// The selected workshop and its range, for the ring; null when none is selected or it has no limit.
	internal static CraftingStation SelectedWithRange(out int range)
	{
		CraftingStation station = SelectedStation();
		range = (station != null) ? Get(station.state) : NoLimit;
		return (range == NoLimit) ? null : station;
	}

	// Whether the running search may take this (always outside a workshop's search).
	internal static bool InRange(IGameComponent gc)
	{
		if (_range == NoLimit || gc == null || gc.GameObject == null)
		{
			return true;
		}
		Vector3 at = gc.GameObject.transform.position;
		float dx = at.x - _origin.x;
		float dz = at.z - _origin.z;
		return dx * dx + dz * dz <= (float)_range * _range;
	}
}

// The search for ingredients: Labor.ClosestResource is where a workshop's fetch (the game's and the market's, through
// Labor.Find) looks for them. Not Labor.Find itself: it is a one-line method the runtime inlines into its callers, so
// a patch on it is skipped. First, before TaskFlow's prefix, which runs the search itself.
[Feature(Features.WorkshopFetchRange, Features.WorkshopFetchRangeInfo)]
[HarmonyPatch(typeof(Labor), "ClosestResource", new Type[] { typeof(GameObject), typeof(int), typeof(Description) }, new ArgumentType[] { ArgumentType.Out, ArgumentType.Out, ArgumentType.Normal })]
internal static class WorkshopFetchRangeFindPatch
{
	[HarmonyPriority(Priority.First)]
	private static void Prefix(Labor __instance, out bool __state)
	{
		__state = WorkshopFetchRange.Begin(__instance);
	}

	private static void Finalizer(bool __state)
	{
		if (__state)
		{
			WorkshopFetchRange.End();
		}
	}
}

// ... where everything holding the ingredient is tested: what lies out of range does not count.
[Feature(Features.WorkshopFetchRange, Features.WorkshopFetchRangeInfo)]
[HarmonyPatch(typeof(Knowledge), "_ClosestObjectToPickup_Test")]
internal static class WorkshopFetchRangeTestPatch
{
	private static void Postfix(IGameComponent gc, ref bool __result)
	{
		if (__result && !WorkshopFetchRange.InRange(gc))
		{
			__result = false;
		}
	}
}

// Network: the range after the game's and the other features' data, in the initial state and every update.
[Feature(Features.WorkshopFetchRange, Features.WorkshopFetchRangeInfo)]
[HarmonyPatch(typeof(CraftingState), nameof(CraftingState.OnSerialize))]
internal static class WorkshopFetchRangeWritePatch
{
	[HarmonyPriority(Priority.Low)]
	private static void Postfix(CraftingState __instance, NetworkWriter writer, ref bool __result)
	{
		writer.WritePackedUInt32((uint)(WorkshopFetchRange.Raw(__instance) + 1));
		__result = true;
	}
}

[Feature(Features.WorkshopFetchRange, Features.WorkshopFetchRangeInfo)]
[HarmonyPatch(typeof(CraftingState), nameof(CraftingState.OnDeserialize))]
internal static class WorkshopFetchRangeReadPatch
{
	[HarmonyPriority(Priority.Low)]
	private static void Postfix(CraftingState __instance, NetworkReader reader)
	{
		WorkshopFetchRange.Set(__instance, WorkshopFetchRange.FromRaw((int)reader.ReadPackedUInt32() - 1));
	}
}

// Saves: an extra "fetchRange" property on the workshop's CraftingState (-1: the default).
[Feature(Features.WorkshopFetchRange, Features.WorkshopFetchRangeInfo)]
[HarmonyPatch(typeof(DefaultGameContractResolver), nameof(DefaultGameContractResolver.GenerateDefaultMonoBehaviourContract))]
internal static class WorkshopFetchRangeSave
{
	private const string Property = "fetchRange";

	private class RangeProvider : IValueProvider
	{
		public object GetValue(object target)
		{
			return WorkshopFetchRange.Raw(target as CraftingState);
		}

		public void SetValue(object target, object value)
		{
			WorkshopFetchRange.Set(target as CraftingState, WorkshopFetchRange.FromRaw(Convert.ToInt32(value)));
		}
	}

	private static void Postfix(Type objectType, JsonObjectContract __result)
	{
		if (objectType != typeof(CraftingState) || __result == null || __result.Properties.Contains(Property))
		{
			return;
		}
		__result.Properties.AddProperty(new JsonProperty
		{
			PropertyName = Property,
			UnderlyingName = Property,
			PropertyType = typeof(int),
			DeclaringType = typeof(CraftingState),
			ValueProvider = new RangeProvider(),
			Readable = true,
			Writable = true
		});
	}
}

// The ring: one line on the ground around the selected workshop at its range (BaseRangeRings' drawing), made again
// when the workshop or the range changes and every second (the ground may have changed).
internal class WorkshopFetchRangeRing : MonoBehaviour
{
	private const float RebuildSeconds = 1f;

	private const float Width = 0.25f;

	private static readonly Color RingColor = new Color(1f, 0.75f, 0.2f, 1f);

	private static Material _material;

	private LineRenderer _line;

	private CraftingStation _station;

	private int _drawn = -1;

	private float _builtAt;

	private void Update()
	{
		CraftingStation station = WorkshopFetchRange.SelectedWithRange(out int range);
		if (station == null)
		{
			if (_line != null)
			{
				_line.enabled = false;
			}
			_station = null;
			return;
		}
		LineRenderer line = Line();
		if (station != _station || range != _drawn || !line.enabled || Time.unscaledTime - _builtAt >= RebuildSeconds)
		{
			WorkerAI.BaseRangeRings.Draw(line, station.transform.position, range);
			_station = station;
			_drawn = range;
			_builtAt = Time.unscaledTime;
		}
		line.enabled = true;
	}

	// The line lives in the game scene and is made again after a scene change.
	private LineRenderer Line()
	{
		if (_line != null)
		{
			return _line;
		}
		GameObject go = new GameObject("CastleStoryPlusWorkshopFetchRange");
		_line = go.AddComponent<LineRenderer>();
		if (_material == null)
		{
			Shader shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
			_material = new Material(shader);
		}
		_line.sharedMaterial = _material;
		_line.useWorldSpace = true;
		_line.startWidth = Width;
		_line.endWidth = Width;
		_line.startColor = RingColor;
		_line.endColor = RingColor;
		_line.enabled = false;
		_drawn = -1;
		return _line;
	}

	private void OnDestroy()
	{
		if (_line != null)
		{
			Destroy(_line.gameObject);
		}
	}
}
