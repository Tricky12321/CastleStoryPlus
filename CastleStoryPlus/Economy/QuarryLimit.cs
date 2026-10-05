using System;
using System.Collections.Generic;
using Brix.Game;
using Brix.Engine;
using Brix.External.Factories;
using Brix.Game.AI;
using Brix.Game.Network;
using Brix.IO.Serialization.Contracts;
using Brix.Lua;
using CastleStoryPlus.Core;
using CastleStoryPlus.Metallurgy;
using HarmonyLib;
using MoonSharp.Interpreter;
using Newtonsoft.Json.Serialization;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.Economy;

// A quarry (mining project) can dig each resource until the team has enough of it: in the quarry menu "-" and "+"
// beside each resource button (stone, iron, brimstone, coal, blue crystal) set a limit for that resource (off, then
// 5 at a time under 50, 10 under 200, then 50), shown as a number under the button. While the team's stockpiles hold
// that much or more (stone counted as bricks, raw stone 20 to a brick, as the stockpiles convert it), the quarry's
// workers leave blocks of that resource alone and dig the rest; they dig them again by themselves once the stock
// drops below the limit. The limits are kept per quarry, saved with it (an extra "resourceLimits" property, ignored
// by the unmodded game) and synced to multiplayer clients; clients send changes to the host.
[Feature(Features.QuarryLimit, Features.QuarryLimitInfo)]
internal static class QuarryLimit
{
	private const int SetLimitHash = 1129595231;

	internal const uint DirtyBit = 0x40000000;

	private const int StonePerBrick = 20;

	internal const int MaxLimit = 5000;

	// The resources, by their flag in the quarry's resource setting (QuarryResources for coal and blue crystal).
	internal const int Stone = 1;

	internal const int Brimstone = 2;

	internal const int Iron = 4;

	internal const int Coal = QuarryResources.CoalOff;

	internal const int Blue = QuarryResources.BlueOff;

	// Per quarry, per resource flag: the limit (missing or 0: none).
	private static readonly Dictionary<QuarryData, Dictionary<int, int>> Limits = new Dictionary<QuarryData, Dictionary<int, int>>();

	private static void Enable()
	{
		GameSession.OnLeave(Limits.Clear);
		NetworkBehaviour.RegisterCommandDelegate(typeof(UNetProjectCmd), SetLimitHash, InvokeSetLimit);
		UI.StepButtons.Register(Features.QuarryLimit);
		LuaInjection.AddFunction("QuarryLimitCount", (ScriptExecutionContext context, CallbackArguments args) =>
		{
			int limit = Get(DataOf(args.AsComponent<MineProject>(0, "QuarryLimitCount", allowNil: true)), (int)args[1].Number);
			return DynValue.NewString((limit > 0) ? limit.ToString() : string.Empty);
		});
		LuaInjection.AddFunction("QuarryLimitLabel", (ScriptExecutionContext context, CallbackArguments args) =>
		{
			return DynValue.NewString(Label(DataOf(args.AsComponent<MineProject>(0, "QuarryLimitLabel", allowNil: true)), (int)args[1].Number));
		});
		LuaInjection.AddFunction("QuarryLimitStep", (ScriptExecutionContext context, CallbackArguments args) =>
		{
			Step(DataOf(args.AsComponent<MineProject>(0, "QuarryLimitStep", allowNil: true)), (int)args[1].Number, (int)args[2].Number);
			return DynValue.Void;
		});
		Buttons(Stone, "--quarry.options.keepStone\ndo\n", "_m.mh.quarryKeepStone = h\nend\n");
		Buttons(Iron, "--quarry.options.KeepIron\ndo\n", "_m.mh.quarryKeepIron = h\nend\n");
		Buttons(Brimstone, "--quarry.options.KeepBrimstone\ndo\n", "_m.mh.quarryKeepBrimstone = h\nend\n");
		// The quarry buttons show only their icon; the tooltip tells the limits.
		foreach (string name in new string[3] { "KeepStone", "KeepIron", "KeepBrimstone" })
		{
			LuaInjection.AddPatch(Features.QuarryLimit, "LUI/Article/Tooltip/Task_Quarry_" + name + ".lua", "Text = ||GetLocalized(\"##task_" + name.ToLowerInvariant() + "_info\"),\n\t},\n", LuaInjection.Mode.InsertAfter,
				"\t{\n\t\ttype = \"Spacer\",\n\t\theight = 4,\n\t},\n"
				+ "\t{\n\t\ttype = \"MiniLabel\",\n\t\tText = ||CastleStoryPlus.QuarryLimitsText(Data.Project:IsSelectedOfTypeQuarry() and Data.Project:GetSelected() or nil),\n\t\tColor = ||CastleYellow,\n\t},\n");
		}
		LuaInjection.AddFunction("QuarryLimitsText", (ScriptExecutionContext context, CallbackArguments args) =>
		{
			// The game's AsComponent throws on nil (no quarry selected) despite allowNil.
			if (args.Count == 0 || args[0].IsNil())
			{
				return DynValue.NewString(string.Empty);
			}
			return DynValue.NewString(LimitsText(DataOf(args.AsComponent<MineProject>(0, "QuarryLimitsText", allowNil: true))));
		});
	}

	// Whether the feature is switched on, also before its Enable ran (QuarryResources asks while it sets up its buttons).
	internal static bool IsOn()
	{
		return Plugin.Cfg.Bind("Features", Features.QuarryLimit, true, Features.QuarryLimitInfo).Value;
	}

	// The limit as the resource button's count, and small "+" and "-" buttons over the button (inside the button's
	// own block, where h is that button).
	private static void Buttons(int flag, string blockStart, string blockEnd)
	{
		LuaInjection.AddPatch(Features.QuarryLimit, "LUI/Menus/GameMenu.lua", blockEnd, LuaInjection.Mode.InsertBefore, CountAndPlus(flag));
	}

	// Nothing goes before the button any more; kept for QuarryResources' coal and blue crystal buttons.
	internal static string MinusButton(int flag)
	{
		return string.Empty;
	}

	// Goes inside the resource button's block, where h is that button.
	internal static string CountAndPlus(int flag)
	{
		return "h.Count = function() if Data.Project:IsSelectedOfTypeQuarry() then return CastleStoryPlus.QuarryLimitCount(Data.Project:GetSelected(), " + flag + ") end return \"\" end\n"
			+ "h.CountColor = ||CastleYellow\n"
			+ "h.CountFontSize = ||14\n"
			+ "h.ev_refresh = h.ev_refresh or Event.New()\n"
			+ "do local button = h _m.onResourceTypeKeptChanged:AddListener(||button.ev_refresh:Invoke()) _m.onSetSelectedProject:AddListener(||button.ev_refresh:Invoke()) end\n"
			+ "h.Step = function(direction) if not Data.Project:IsSelectedOfTypeQuarry() then return false end CastleStoryPlus.QuarryLimitStep(Data.Project:GetSelected(), " + flag + ", direction) return true end\n"
			+ "h.StepChanged = _m.onResourceTypeKeptChanged\n"
			+ "--quarry.options.limit" + flag + " (Castle Story Plus)\n";
	}

	internal static QuarryData DataOf(MineProject mine)
	{
		return (mine != null && mine.goalProvider != null) ? mine.goalProvider.data : null;
	}

	internal static int Get(QuarryData data, int flag)
	{
		return (data != null && Limits.TryGetValue(data, out Dictionary<int, int> limits) && limits.TryGetValue(flag, out int limit)) ? limit : 0;
	}

	internal static Dictionary<int, int> All(QuarryData data)
	{
		return (data != null && Limits.TryGetValue(data, out Dictionary<int, int> limits)) ? limits : null;
	}

	internal static void Set(QuarryData data, int flag, int limit)
	{
		if (data == null || flag <= 0)
		{
			return;
		}
		limit = Mathf.Clamp(limit, 0, MaxLimit);
		if (Get(data, flag) == limit)
		{
			return;
		}
		if (!Limits.TryGetValue(data, out Dictionary<int, int> limits))
		{
			limits = new Dictionary<int, int>();
			Limits[data] = limits;
		}
		if (limit == 0)
		{
			limits.Remove(flag);
			if (limits.Count == 0)
			{
				Limits.Remove(data);
			}
		}
		else
		{
			limits[flag] = limit;
		}
		if (NetworkServer.active)
		{
			data.SetDirtyBit(DirtyBit);
		}
	}

	// Replaces every limit of the quarry (network and saves).
	internal static void SetAll(QuarryData data, Dictionary<int, int> limits)
	{
		if (data == null)
		{
			return;
		}
		Limits.Remove(data);
		foreach (KeyValuePair<int, int> pair in limits)
		{
			Set(data, pair.Key, pair.Value);
		}
		if (NetworkServer.active)
		{
			data.SetDirtyBit(DirtyBit);
		}
	}

	internal static string Name(int flag)
	{
		switch (flag)
		{
			case Stone:
				return "bricks";
			case Brimstone:
				return "brimstone";
			case Iron:
				return "raw iron";
			case Coal:
				return "coal";
			case Blue:
				return "blue crystal";
		}
		return "?";
	}

	// The team's stock of a resource; stone as bricks, raw stone counted as bricks.
	internal static int Stock(Faction faction, int flag)
	{
		switch (flag)
		{
			case Stone:
				return CraftLoopLimit.Stock(faction, Adjectif.stoneBlock, cached: true) + CraftLoopLimit.Stock(faction, Adjectif.stones, cached: true) / StonePerBrick;
			case Brimstone:
				return CraftLoopLimit.Stock(faction, Adjectif.orangeCrystal, cached: true);
			case Iron:
				return CraftLoopLimit.Stock(faction, Adjectif.rawIron, cached: true);
			case Coal:
				return CraftLoopLimit.Stock(faction, Adjectif.clay, cached: true);
			case Blue:
				return CraftLoopLimit.Stock(faction, Adjectif.blueCrystal, cached: true);
		}
		return 0;
	}

	// The resource flag of what a block gives, or 0.
	internal static int FlagOf(Factory.AssetKey key)
	{
		if (key == null)
		{
			return 0;
		}
		if (key == RawResources.RawStone)
		{
			return Stone;
		}
		if (key == RawResources.RawIron)
		{
			return Iron;
		}
		if (key == RawResources.RawOrangeCrystal)
		{
			return Brimstone;
		}
		if (key == Metallurgy.Metallurgy.CoalKey)
		{
			return Coal;
		}
		if (key == RawResources.RawBlueCrystal)
		{
			return Blue;
		}
		return 0;
	}

	internal static bool Reached(QuarryData data, int flag)
	{
		int limit = Get(data, flag);
		return limit > 0 && Stock(Faction.GetFaction(data.gameObject), flag) >= limit;
	}

	private static string Label(QuarryData data, int flag)
	{
		int limit = Get(data, flag);
		if (data == null || limit <= 0)
		{
			return "Dig " + Name(flag) + ": no limit";
		}
		int stock = Stock(Faction.GetFaction(data.gameObject), flag);
		return "Dig " + Name(flag) + " until " + limit + " (" + stock + " now" + ((stock >= limit) ? ", paused" : string.Empty) + ")";
	}

	private static string LimitsText(QuarryData data)
	{
		Dictionary<int, int> limits = All(data);
		if (limits == null || limits.Count == 0)
		{
			return "No dig limits (- and + by each resource)";
		}
		Faction faction = Faction.GetFaction(data.gameObject);
		List<string> parts = new List<string>();
		foreach (int flag in new int[5] { Stone, Iron, Brimstone, Coal, Blue })
		{
			if (limits.TryGetValue(flag, out int limit) && limit > 0)
			{
				int stock = Stock(faction, flag);
				parts.Add(Name(flag) + " " + stock + "/" + limit + ((stock >= limit) ? " (paused)" : string.Empty));
			}
		}
		return "Dig until: " + string.Join(", ", parts.ToArray());
	}

	// Off, then 5 at a time under 50, 10 under 200, then 50.
	private static void Step(QuarryData data, int flag, int direction)
	{
		if (data == null)
		{
			return;
		}
		int value = Get(data, flag);
		int step = (direction > 0) ? ((value < 50) ? 5 : ((value < 200) ? 10 : 50)) : ((value <= 50) ? 5 : ((value <= 200) ? 10 : 50));
		Change(data, flag, Mathf.Clamp(value + direction * step, 0, MaxLimit));
	}

	// Shown at once; the host's value follows.
	private static void Change(QuarryData data, int flag, int limit)
	{
		Set(data, flag, limit);
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
		writer.Write(data.gameObject);
		writer.WritePackedUInt32((uint)flag);
		writer.WritePackedUInt32((uint)limit);
		cmd.SendCommandInternal(writer, 0, "CmdSetQuarryLimit");
	}

	private static void InvokeSetLimit(NetworkBehaviour obj, NetworkReader reader)
	{
		if (!NetworkServer.active)
		{
			return;
		}
		GameObject go = reader.ReadGameObject();
		int flag = (int)reader.ReadPackedUInt32();
		int limit = (int)reader.ReadPackedUInt32();
		if (go == null || !Affiliation.IsOwnedBy(go, obj.gameObject))
		{
			return;
		}
		Set(go.GetComponent<QuarryData>(), flag, limit);
	}

	// "1:50,4:10" for saves.
	internal static string Encode(QuarryData data)
	{
		Dictionary<int, int> limits = All(data);
		if (limits == null)
		{
			return string.Empty;
		}
		List<string> parts = new List<string>();
		foreach (KeyValuePair<int, int> pair in limits)
		{
			parts.Add(pair.Key + ":" + pair.Value);
		}
		return string.Join(",", parts.ToArray());
	}

	internal static Dictionary<int, int> Decode(string text)
	{
		Dictionary<int, int> limits = new Dictionary<int, int>();
		if (string.IsNullOrEmpty(text))
		{
			return limits;
		}
		foreach (string part in text.Split(','))
		{
			string[] pair = part.Split(':');
			if (pair.Length == 2 && int.TryParse(pair[0], out int flag) && int.TryParse(pair[1], out int limit))
			{
				limits[flag] = limit;
			}
		}
		return limits;
	}
}

// Server: a quarry's workers leave blocks of a resource at its limit alone.
[Feature(Features.QuarryLimit, Features.QuarryLimitInfo)]
[HarmonyPatch(typeof(DigGoal), nameof(DigGoal.IsMatchFor))]
internal static class QuarryLimitDigPatch
{
	private static void Postfix(DigGoal __instance, ref bool __result)
	{
		if (!__result || !(__instance.ResourceSetting is QuarryData data) || QuarryLimit.All(data) == null)
		{
			return;
		}
		int flag = QuarryLimit.FlagOf(Voxel.GetBlockInfo(__instance.voxel.position).ResourceKey);
		if (flag != 0 && QuarryLimit.Reached(data, flag))
		{
			__result = false;
		}
	}
}

// Network: the limit is appended to the quarry's own data, in the initial state and every update.
[Feature(Features.QuarryLimit, Features.QuarryLimitInfo)]
[HarmonyPatch(typeof(QuarryData), nameof(QuarryData.OnSerialize))]
internal static class QuarryLimitWritePatch
{
	private static void Postfix(QuarryData __instance, NetworkWriter writer, ref bool __result)
	{
		Dictionary<int, int> limits = QuarryLimit.All(__instance);
		writer.WritePackedUInt32((uint)((limits != null) ? limits.Count : 0));
		if (limits != null)
		{
			foreach (KeyValuePair<int, int> pair in limits)
			{
				writer.WritePackedUInt32((uint)pair.Key);
				writer.WritePackedUInt32((uint)pair.Value);
			}
		}
		__result = true;
	}
}

[Feature(Features.QuarryLimit, Features.QuarryLimitInfo)]
[HarmonyPatch(typeof(QuarryData), nameof(QuarryData.OnDeserialize))]
internal static class QuarryLimitReadPatch
{
	private static void Postfix(QuarryData __instance, NetworkReader reader)
	{
		Dictionary<int, int> limits = new Dictionary<int, int>();
		int count = (int)reader.ReadPackedUInt32();
		for (int i = 0; i < count; i++)
		{
			int flag = (int)reader.ReadPackedUInt32();
			limits[flag] = (int)reader.ReadPackedUInt32();
		}
		QuarryLimit.SetAll(__instance, limits);
	}
}

// Saves: an extra "resourceLimits" property ("1:50,4:10": resource flag and limit) on the quarry's data.
[Feature(Features.QuarryLimit, Features.QuarryLimitInfo)]
[HarmonyPatch(typeof(DefaultGameContractResolver), nameof(DefaultGameContractResolver.GenerateDefaultMonoBehaviourContract))]
internal static class QuarryLimitSave
{
	private const string Property = "resourceLimits";

	private class LimitProvider : IValueProvider
	{
		public object GetValue(object target)
		{
			return QuarryLimit.Encode(target as QuarryData);
		}

		public void SetValue(object target, object value)
		{
			QuarryLimit.SetAll(target as QuarryData, QuarryLimit.Decode(value as string));
		}
	}

	private static void Postfix(Type objectType, JsonObjectContract __result)
	{
		if (objectType != typeof(QuarryData) || __result == null || __result.Properties.Contains(Property))
		{
			return;
		}
		__result.Properties.AddProperty(new JsonProperty
		{
			PropertyName = Property,
			UnderlyingName = Property,
			PropertyType = typeof(string),
			DeclaringType = typeof(QuarryData),
			ValueProvider = new LimitProvider(),
			Readable = true,
			Writable = true
		});
	}
}
