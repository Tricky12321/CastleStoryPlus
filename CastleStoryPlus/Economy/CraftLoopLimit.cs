using System;
using System.Collections.Generic;
using Brix.Components;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Game.Network;
using Brix.Game.Semantique;
using Brix.Lifecycle.Pooling;
using Brix.Game.Utils;
using Brix.IO.Serialization.Contracts;
using Brix.Utils;
using CastleStoryPlus.Core;
using HarmonyLib;
using MoonSharp.Interpreter;
using Motus.Behavior;
using Newtonsoft.Json.Serialization;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.Economy;

// Looping a workshop's queue until there is enough in stock: next to the game's "loop" button in a workshop's menu
// (shown whenever the workshop has a queue), a stockpile button turns a stock limit on or off, and "-" and "+"
// buttons around it change the limit (1 at a time under 10, then 5, then 10; 5 at first); turning it on turns looping
// on. The limit shows in the buttons' tooltip. While the queue loops with a limit, a recipe only
// starts when the team's stockpiles (and tool racks) hold less than the limit of what it makes; when every recipe
// in the queue has enough, the workshop waits with its queue, and starts again by itself once the stock drops below
// the limit. Example: a furnace looping iron ingots with limit 5 keeps 5 iron in stock.
// The limit is kept per workshop, saved with it (an extra "loopLimit" property, ignored by the unmodded game) and
// synced to multiplayer clients; clients send changes to the host. Clearing the queue clears the limit.
[Feature(Features.CraftLoopLimit, Features.CraftLoopLimitInfo)]
internal static class CraftLoopLimit
{
	private const int DefaultLimit = 5;

	private const int MaxLimit = 500;

	private const int SetLimitHash = 1129595230;

	// A sync var bit CraftingState does not use (it uses 0x01-0x10); setting it makes UNET send an update.
	internal const uint DirtyBit = 0x40000000u;

	private const float StockCacheSeconds = 1f;

	// Per workshop: above 0 the limit is on, below 0 it is off and the value is kept for turning it on again.
	private static readonly Dictionary<CraftingState, int> Limits = new Dictionary<CraftingState, int>();

	private static readonly Dictionary<KeyValuePair<Faction, Type>, KeyValuePair<float, int>> StockCache = new Dictionary<KeyValuePair<Faction, Type>, KeyValuePair<float, int>>();

	private static void Enable()
	{
		NetworkBehaviour.RegisterCommandDelegate(typeof(UNetProjectCmd), SetLimitHash, InvokeSetLimit);
		LuaInjection.AddFunction("LoopLimit", (ScriptExecutionContext context, CallbackArguments args) => DynValue.NewNumber(Get(Selected())));
		LuaInjection.AddFunction("LoopLimitOn", (ScriptExecutionContext context, CallbackArguments args) => DynValue.NewBoolean(Get(Selected()) > 0));
		LuaInjection.AddFunction("LoopLimitLabel", (ScriptExecutionContext context, CallbackArguments args) => DynValue.NewString(Label(Selected())));
		LuaInjection.AddAction("ToggleLoopLimit", Toggle);
		LuaInjection.AddAction("LoopLimitDown", () => Step(-1));
		LuaInjection.AddAction("LoopLimitUp", () => Step(1));
		// The menu's buttons show only their icon; the loop tooltip (shared by these buttons) tells the limit.
		LuaInjection.AddPatch(Features.CraftLoopLimit, "LUI/Article/Tooltip/Operable_Queue_ToggleLooping.lua", "Text = ||GetLocalized(\"##operable_queue_toggle_looping_info\"),\n\t},\n", LuaInjection.Mode.InsertAfter,
			"\t{\n\t\ttype = \"Spacer\",\n\t\theight = 4,\n\t},\n"
			+ "\t{\n\t\ttype = \"MiniLabel\",\n\t\tText = ||CastleStoryPlus.LoopLimitLabel(),\n\t\tColor = ||CastleYellow,\n\t},\n");
		LuaInjection.AddPatch(Features.CraftLoopLimit, "LUI/Menus/GameMenu.lua", "_m.mh.toggleLoop = h\nend\n", LuaInjection.Mode.InsertAfter, @"
--operable: queue: loop until stock: - / on-off / + (Castle Story Plus)
do
local handles = {}
local RefreshAll = function() for i = 1, #handles do handles[i].ev_refresh:Invoke() end end
local Queue = ||Data.Operable:HasSelected() and Data.Operable:HasQueue()
local On = ||Queue() and CastleStoryPlus.LoopLimitOn()
local Make = function(icon, visible, color, highlight, action)
	local h = MenuHandle.New()
	h.Visible = visible
	h.TooltipPath = ||{""Operable"", ""Queue"", ""ToggleLooping""}
	h.Label = ||CastleStoryPlus.LoopLimitLabel()
	h.LabelColor = ||CastleYellow
	h.Icon = icon
	h.IconColor = color
	h.IconSize = ||32
	h.Highlight = highlight
	h.ev_refresh = Event.New()
	h.OnAction = function() action() RefreshAll() end
	_m.sh.operableQueue.ev_onLoad:AddListener(function(m, sh) m.AddMenuHandleToggle(h, sh) end)
	_m.mg.projectContext:AddChild(h)
	handles[#handles + 1] = h
end
Make(||IconKeys._UI_Minus:Get64(), On, ||CastleYellow, ||HighlightMode.None, function() CastleStoryPlus.LoopLimitDown() end)
Make(||IconKeys._UI_Stockpile:Get64(), Queue,
	function() if CastleStoryPlus.LoopLimitOn() then return CastleYellow end return Gray end,
	function() if Queue() and CastleStoryPlus.LoopLimitOn() then return HighlightMode.All end return HighlightMode.None end,
	function()
		CastleStoryPlus.ToggleLoopLimit()
		if CastleStoryPlus.LoopLimitOn() and not Data.Operable:GetIsQueueLooping() then Data.Operable:SetIsQueueLooping(true) end
	end)
Make(||IconKeys._UI_Plus:Get64(), On, ||CastleYellow, ||HighlightMode.None, function() CastleStoryPlus.LoopLimitUp() end)
Data.Operable.ev_onSetSelected:AddListener(RefreshAll)
Data.Operable.ev_onSetQueue:AddListener(RefreshAll)
Data.Operable.ev_onSetIsQueueLooping:AddListener(RefreshAll)
end
");
	}

	// The limit while it is on, else 0.
	internal static int Get(CraftingState state)
	{
		int raw = Raw(state);
		return (raw > 0) ? raw : 0;
	}

	// The stored value: above 0 on, below 0 off with the value kept, 0 never set.
	internal static int Raw(CraftingState state)
	{
		return (state != null && Limits.TryGetValue(state, out int limit)) ? limit : 0;
	}

	internal static void Set(CraftingState state, int limit)
	{
		if (state == null)
		{
			return;
		}
		limit = Mathf.Clamp(limit, -MaxLimit, MaxLimit);
		if (Raw(state) == limit)
		{
			return;
		}
		if (limit == 0)
		{
			Limits.Remove(state);
		}
		else
		{
			Limits[state] = limit;
		}
		if (NetworkServer.active)
		{
			state.SetDirtyBit(DirtyBit);
		}
	}

	private static CraftingState Selected()
	{
		CraftingStation station = CraftingStation.SelectedStation();
		return (station != null) ? station.state : null;
	}

	private static int Value(int raw)
	{
		return (raw != 0) ? Mathf.Abs(raw) : DefaultLimit;
	}

	private static string Label(CraftingState state)
	{
		int raw = Raw(state);
		if (raw <= 0)
		{
			return "Loop until stock: off (" + Value(raw) + ")";
		}
		CraftingLabor labor = (state != null) ? state.GetComponent<CraftingLabor>() : null;
		Ressource product = (labor != null) ? Product(labor.FirstRecipe) : null;
		if (product == null)
		{
			return "Loop until stock: " + raw;
		}
		return "Loop until stock: " + raw + " (" + Stock(Faction.GetFaction(state.gameObject), product, cached: false) + " now)";
	}

	private static void Toggle()
	{
		CraftingState state = Selected();
		if (state == null)
		{
			return;
		}
		int raw = Raw(state);
		Change(state, (raw > 0) ? -raw : Value(raw));
	}

	// 1 at a time under 10, 5 under 50, then 10.
	private static void Step(int direction)
	{
		CraftingState state = Selected();
		if (state == null)
		{
			return;
		}
		int raw = Raw(state);
		int value = Value(raw);
		int step = (direction > 0) ? ((value < 10) ? 1 : ((value < 50) ? 5 : 10)) : ((value <= 10) ? 1 : ((value <= 50) ? 5 : 10));
		value = Mathf.Clamp(value + direction * step, 1, MaxLimit);
		Change(state, (raw > 0) ? value : -value);
	}

	// Shown at once; the host's value follows.
	private static void Change(CraftingState state, int raw)
	{
		Set(state, raw);
		UNetProjectCmd cmd = (User.LocalUser != null) ? User.LocalUser.GetComponent<UNetProjectCmd>() : null;
		if (cmd == null || cmd.isServer)
		{
			return;
		}
		NetworkWriter writer = new NetworkWriter();
		writer.Write((short)0);
		writer.Write((short)5);
		writer.WritePackedUInt32((uint)SetLimitHash);
		writer.Write(cmd.GetComponent<NetworkIdentity>().netId);
		writer.Write(state.gameObject);
		writer.WritePackedUInt32(unchecked((uint)raw));
		cmd.SendCommandInternal(writer, 0, "CmdSetCraftLoopLimit");
	}

	private static void InvokeSetLimit(NetworkBehaviour obj, NetworkReader reader)
	{
		if (!NetworkServer.active)
		{
			return;
		}
		GameObject station = reader.ReadGameObject();
		int limit = unchecked((int)reader.ReadPackedUInt32());
		if (station == null || !Affiliation.IsOwnedBy(station, obj.gameObject))
		{
			return;
		}
		Set(station.GetComponent<CraftingState>(), limit);
	}

	// Server: whether the queue may start its first recipe. With a limit, the first recipe whose product is below the
	// limit is moved to the front; if none is, the workshop waits.
	internal static bool MayStart(CraftingLabor labor)
	{
		int limit = Get(labor.state);
		if (limit <= 0 || !labor.LoopRecipe)
		{
			return true;
		}
		List<RecipeInfo> queue = labor.QueuedRecipes;
		Faction faction = Faction.GetFaction(labor.gameObject);
		for (int i = 0; i < queue.Count; i++)
		{
			Ressource product = Product(queue[i]);
			if (product != null && Stock(faction, product, cached: true) >= limit)
			{
				continue;
			}
			if (i > 0)
			{
				List<RecipeInfo> waiting = queue.GetRange(0, i);
				queue.RemoveRange(0, i);
				queue.AddRange(waiting);
				labor.state.SetRecipes(queue);
			}
			return true;
		}
		return false;
	}

	private static Ressource Product(RecipeInfo recipe)
	{
		if (recipe == null || recipe.Creates == null || recipe.Creates.IsEmpty())
		{
			return null;
		}
		GameObject template = Factory.Peek(recipe.Creates);
		return (template != null) ? Apparence.PrincipaleOf(template) : null;
	}

	// What the team's stockpiles and tool racks hold of the resource.
	internal static int Stock(Faction faction, Ressource resource, bool cached)
	{
		KeyValuePair<Faction, Type> key = new KeyValuePair<Faction, Type>(faction, resource.GetType());
		if (cached && StockCache.TryGetValue(key, out KeyValuePair<float, int> entry) && Time.time - entry.Key < StockCacheSeconds)
		{
			return entry.Value;
		}
		int count = 0;
		AutoList list = BrixSingleton<AutoList>.Instance;
		if (list != null)
		{
			HashSet<GameObject> seen = new HashSet<GameObject>();
			foreach (Factory.AssetKey storage in new[] { ObjetsDynamiques.Palette, ObjetsDynamiques.Toolrack, ObjetsDynamiques.SingleToolrack })
			{
				HashSet<GameObject> instances = list.GetInstances(storage);
				if (instances == null)
				{
					continue;
				}
				foreach (GameObject go in instances)
				{
					if (go == null || !go.activeInHierarchy || !seen.Add(go) || Faction.GetFaction(go) != faction)
					{
						continue;
					}
					Recepteur recepteur = go.GetComponent<Recepteur>();
					if (recepteur != null)
					{
						count += recepteur.ContentDescription.Value(resource);
					}
				}
			}
		}
		StockCache[key] = new KeyValuePair<float, int>(Time.time, count);
		return count;
	}
}

// The queue only starts a recipe that MayStart allows (the game's node, with that condition added).
[Feature(Features.CraftLoopLimit, Features.CraftLoopLimitInfo)]
[HarmonyPatch(typeof(CraftingLabor), "ProcessQueue")]
internal static class CraftLoopLimitQueuePatch
{
	private static bool Prefix(CraftingLabor __instance, ref StaticNode __result)
	{
		__result = Node.Selector.If((CraftingLabor aCraftingLabor) => aCraftingLabor.CurrentWork == null && aCraftingLabor.FirstRecipe != null && CraftLoopLimit.MayStart(aCraftingLabor), __instance).Do((CraftingLabor aCraftingLabor) =>
		{
			aCraftingLabor.PopRecipe();
		}, __instance).Do((CraftingLabor aCraftingLabor) =>
		{
			aCraftingLabor.instructions.CookPackage(aCraftingLabor.CurrentRecipe).Send(aCraftingLabor);
		}, __instance);
		return false;
	}
}

// Clearing the queue (which also stops looping) clears the limit.
[Feature(Features.CraftLoopLimit, Features.CraftLoopLimitInfo)]
[HarmonyPatch(typeof(CraftingLabor), nameof(CraftingLabor.ClearOrderQueue))]
internal static class CraftLoopLimitClearPatch
{
	private static void Postfix(CraftingLabor __instance)
	{
		CraftLoopLimit.Set(__instance.state, 0);
	}
}

// Network: the limit is appended to the workshop's own state data, in the initial state and every update.
[Feature(Features.CraftLoopLimit, Features.CraftLoopLimitInfo)]
[HarmonyPatch(typeof(CraftingState), nameof(CraftingState.OnSerialize))]
internal static class CraftLoopLimitWritePatch
{
	private static void Postfix(CraftingState __instance, NetworkWriter writer, ref bool __result)
	{
		writer.WritePackedUInt32(unchecked((uint)CraftLoopLimit.Raw(__instance)));
		__result = true;
	}
}

[Feature(Features.CraftLoopLimit, Features.CraftLoopLimitInfo)]
[HarmonyPatch(typeof(CraftingState), nameof(CraftingState.OnDeserialize))]
internal static class CraftLoopLimitReadPatch
{
	private static void Postfix(CraftingState __instance, NetworkReader reader)
	{
		CraftLoopLimit.Set(__instance, unchecked((int)reader.ReadPackedUInt32()));
	}
}

// Saves: an extra "loopLimit" property on the workshop's CraftingState.
[Feature(Features.CraftLoopLimit, Features.CraftLoopLimitInfo)]
[HarmonyPatch(typeof(DefaultGameContractResolver), nameof(DefaultGameContractResolver.GenerateDefaultMonoBehaviourContract))]
internal static class CraftLoopLimitSave
{
	private const string Property = "loopLimit";

	private class LimitProvider : IValueProvider
	{
		public object GetValue(object target)
		{
			return CraftLoopLimit.Raw(target as CraftingState);
		}

		public void SetValue(object target, object value)
		{
			CraftLoopLimit.Set(target as CraftingState, Convert.ToInt32(value));
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
			ValueProvider = new LimitProvider(),
			Readable = true,
			Writable = true
		});
	}
}
