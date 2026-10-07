using System;
using System.Collections.Generic;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Game.Network;
using Brix.IO.Serialization.Contracts;
using CastleStoryPlus.Core;
using HarmonyLib;
using MoonSharp.Interpreter;
using Newtonsoft.Json.Serialization;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.Economy;

// A workshop's priority can be set like a task's: "-" and "+" buttons in its queue menu step it through locked, low,
// medium and high (the game keeps every workshop at high and ignores changes). Workers weigh it the same way as a
// task's priority, so a research station on low is only staffed when the higher work is covered. Kept per workshop,
// saved with it (an extra "priority" property) and synced to multiplayer clients; clients send changes to the host.
[Feature(Features.WorkshopPriority, Features.WorkshopPriorityInfo)]
internal static class WorkshopPriority
{
	private const int SetPriorityHash = 1129595234;

	// A sync var bit CraftingState does not use (WideRecipeIds and CraftLoopLimit use 0x20000000 and 0x40000000).
	internal const uint DirtyBit = 0x10000000u;

	private static readonly ProjectPriority.Bias[] Steps = { ProjectPriority.Bias.Locked, ProjectPriority.Bias.Low, ProjectPriority.Bias.Medium, ProjectPriority.Bias.High };

	// Per workshop; a workshop not in here has the game's high.
	private static readonly Dictionary<CraftingState, ProjectPriority.Bias> Biases = new Dictionary<CraftingState, ProjectPriority.Bias>();

	private static void Enable()
	{
		GameSession.OnLeave(Biases.Clear);
		NetworkBehaviour.RegisterCommandDelegate(typeof(UNetProjectCmd), SetPriorityHash, InvokeSetPriority);
		LuaInjection.AddFunction("WorkshopPriorityLabel", (ScriptExecutionContext context, CallbackArguments args) => DynValue.NewString("Priority: " + Name(Get(Selected()))));
		LuaInjection.AddFunction("WorkshopPriorityCan", (ScriptExecutionContext context, CallbackArguments args) =>
		{
			CraftingState state = Selected();
			int step = Array.IndexOf(Steps, Get(state)) + ((args.Count > 0) ? (int)args[0].Number : 0);
			return DynValue.NewBoolean(state != null && step >= 0 && step < Steps.Length);
		});
		LuaInjection.AddAction("WorkshopPriorityDown", () => Step(-1));
		LuaInjection.AddAction("WorkshopPriorityUp", () => Step(1));
		LuaInjection.AddPatch(Features.WorkshopPriority, "LUI/Menus/GameMenu.lua", "_m.mh.toggleLoop = h\nend\n", LuaInjection.Mode.InsertAfter, @"
--operable: queue: priority - / + (Castle Story Plus)
do
local handles = {}
local RefreshAll = function() for i = 1, #handles do handles[i].ev_refresh:Invoke() end end
local Make = function(icon, direction)
	local h = MenuHandle.New()
	h.Visible = ||Data.Operable:HasSelected() and Data.Operable:HasQueue()
	h.Label = ||CastleStoryPlus.WorkshopPriorityLabel()
	h.LabelColor = ||CastleYellow
	h.Icon = icon
	h.IconColor = function() if CastleStoryPlus.WorkshopPriorityCan(direction) then return CastleYellow end return Gray end
	h.IconSize = ||32
	h.ev_refresh = Event.New()
	h.OnAction = function()
		if not UIGame.IsPaused() then
			if direction < 0 then CastleStoryPlus.WorkshopPriorityDown() else CastleStoryPlus.WorkshopPriorityUp() end
		end
		RefreshAll()
	end
	_m.sh.operableQueue.ev_onLoad:AddListener(function(m, sh) m.AddMenuHandleToggle(h, sh) end)
	_m.mg.projectContext:AddChild(h)
	handles[#handles + 1] = h
end
Make(||IconKeys.UI_Minus:Get64(), -1)
Make(||IconKeys.UI_Plus:Get64(), 1)
Data.Operable.ev_onSetSelected:AddListener(RefreshAll)
Data.Operable.ev_onSetQueue:AddListener(RefreshAll)
end
");
	}

	internal static ProjectPriority.Bias Get(CraftingState state)
	{
		return (state != null && Biases.TryGetValue(state, out ProjectPriority.Bias bias)) ? bias : ProjectPriority.Bias.High;
	}

	internal static void Set(CraftingState state, ProjectPriority.Bias bias)
	{
		if (state == null || Get(state) == bias)
		{
			return;
		}
		if (bias == ProjectPriority.Bias.High)
		{
			Biases.Remove(state);
		}
		else
		{
			Biases[state] = bias;
		}
		if (NetworkServer.active)
		{
			state.SetDirtyBit(DirtyBit);
		}
	}

	internal static ProjectPriority.Bias FromRaw(int raw)
	{
		return Array.IndexOf(Steps, (ProjectPriority.Bias)raw) >= 0 ? (ProjectPriority.Bias)raw : ProjectPriority.Bias.High;
	}

	private static string Name(ProjectPriority.Bias bias)
	{
		switch (bias)
		{
		case ProjectPriority.Bias.Locked:
			return "locked";
		case ProjectPriority.Bias.Low:
			return "low";
		case ProjectPriority.Bias.Medium:
			return "medium";
		default:
			return "high";
		}
	}

	private static CraftingState Selected()
	{
		CraftingStation station = CraftingStation.SelectedStation();
		return (station != null) ? station.state : null;
	}

	// Shown at once; the host's value follows.
	private static void Step(int direction)
	{
		CraftingState state = Selected();
		int step = Array.IndexOf(Steps, Get(state)) + direction;
		if (state == null || step < 0 || step >= Steps.Length)
		{
			return;
		}
		ProjectPriority.Bias bias = Steps[step];
		Set(state, bias);
		UNetProjectCmd cmd = (User.LocalUser != null) ? User.LocalUser.GetComponent<UNetProjectCmd>() : null;
		if (cmd == null || cmd.isServer)
		{
			return;
		}
		NetworkWriter writer = new NetworkWriter();
		writer.Write((short)0);
		writer.Write((short)5);
		writer.WritePackedUInt32((uint)SetPriorityHash);
		writer.Write(cmd.GetComponent<NetworkIdentity>().netId);
		writer.Write(state.gameObject);
		writer.WritePackedUInt32(unchecked((uint)(int)bias));
		cmd.SendCommandInternal(writer, 0, "CmdSetWorkshopPriority");
	}

	private static void InvokeSetPriority(NetworkBehaviour obj, NetworkReader reader)
	{
		if (!NetworkServer.active)
		{
			return;
		}
		GameObject station = reader.ReadGameObject();
		int raw = unchecked((int)reader.ReadPackedUInt32());
		if (station == null || !Affiliation.IsOwnedBy(station, obj.gameObject))
		{
			return;
		}
		Set(station.GetComponent<CraftingState>(), FromRaw(raw));
	}
}

// The workshop's priority instead of the game's fixed high, both where workers weigh it and where it is read.
[Feature(Features.WorkshopPriority, Features.WorkshopPriorityInfo)]
[HarmonyPatch(typeof(CraftingStation), nameof(CraftingStation.PriorityInput))]
internal static class WorkshopPriorityInputPatch
{
	private static void Postfix(CraftingStation __instance, ref ProjectPriority __result)
	{
		__result.manualBias = (float)WorkshopPriority.Get(__instance.state);
	}
}

[Feature(Features.WorkshopPriority, Features.WorkshopPriorityInfo)]
[HarmonyPatch(typeof(CraftingStation), nameof(CraftingStation.PriorityBias), MethodType.Getter)]
internal static class WorkshopPriorityGetPatch
{
	private static void Postfix(CraftingStation __instance, ref ProjectPriority.Bias __result)
	{
		__result = WorkshopPriority.Get(__instance.state);
	}
}

// Network: the priority after the game's and the other features' data (last, on both sides), in the initial state
// and every update.
[Feature(Features.WorkshopPriority, Features.WorkshopPriorityInfo)]
[HarmonyPatch(typeof(CraftingState), nameof(CraftingState.OnSerialize))]
internal static class WorkshopPriorityWritePatch
{
	[HarmonyPriority(Priority.Last)]
	private static void Postfix(CraftingState __instance, NetworkWriter writer, ref bool __result)
	{
		writer.WritePackedUInt32(unchecked((uint)(int)WorkshopPriority.Get(__instance)));
		__result = true;
	}
}

[Feature(Features.WorkshopPriority, Features.WorkshopPriorityInfo)]
[HarmonyPatch(typeof(CraftingState), nameof(CraftingState.OnDeserialize))]
internal static class WorkshopPriorityReadPatch
{
	[HarmonyPriority(Priority.Last)]
	private static void Postfix(CraftingState __instance, NetworkReader reader)
	{
		WorkshopPriority.Set(__instance, WorkshopPriority.FromRaw(unchecked((int)reader.ReadPackedUInt32())));
	}
}

// Saves: an extra "priority" property on the workshop's CraftingState.
[Feature(Features.WorkshopPriority, Features.WorkshopPriorityInfo)]
[HarmonyPatch(typeof(DefaultGameContractResolver), nameof(DefaultGameContractResolver.GenerateDefaultMonoBehaviourContract))]
internal static class WorkshopPrioritySave
{
	private const string Property = "priority";

	private class PriorityProvider : IValueProvider
	{
		public object GetValue(object target)
		{
			return (int)WorkshopPriority.Get(target as CraftingState);
		}

		public void SetValue(object target, object value)
		{
			WorkshopPriority.Set(target as CraftingState, WorkshopPriority.FromRaw(Convert.ToInt32(value)));
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
			ValueProvider = new PriorityProvider(),
			Readable = true,
			Writable = true
		});
	}
}
