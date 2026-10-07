using System;
using System.Collections.Generic;
using Brix.Game;
using Brix.IO.Serialization.Contracts;
using Brix.Lua;
using CastleStoryPlus.Core;
using HarmonyLib;
using MoonSharp.Interpreter;
using Newtonsoft.Json.Serialization;

namespace CastleStoryPlus.UI;

// The resources a team has had in its stockpiles. Once a resource has been stocked it stays in the resource list for
// good, also at 0 and after the world is saved and loaded again. Kept per team and saved as an extra property on
// the Faction component ("seenResources": "Wood,Stone,..."); the unmodded game ignores it.
[Feature(Features.ResourceList, Features.ResourceListInfo)]
internal static class ResourceSeen
{
	private static readonly Dictionary<Faction, HashSet<string>> Seen = new Dictionary<Faction, HashSet<string>>();

	private static void Enable()
	{
		LuaInjection.AddFunction("ResourceSeen", (ScriptExecutionContext context, CallbackArguments args) =>
		{
			HashSet<string> seen = Of(LocalFaction(), create: false);
			return DynValue.NewBoolean(seen != null && seen.Contains(Key(args)));
		});
		LuaInjection.AddFunction("MarkResourceSeen", (ScriptExecutionContext context, CallbackArguments args) =>
		{
			HashSet<string> seen = Of(LocalFaction(), create: true);
			if (seen != null)
			{
				seen.Add(Key(args));
			}
			return DynValue.Void;
		});
	}

	private static Faction LocalFaction()
	{
		return (User.LocalUser != null) ? User.LocalUser.faction : null;
	}

	private static string Key(CallbackArguments args)
	{
		return args.AsEnum<LuaCrafting.LuaResource>(0, "ResourceSeen").ToString();
	}

	private static HashSet<string> Of(Faction faction, bool create)
	{
		if (faction == null)
		{
			return null;
		}
		if (!Seen.TryGetValue(faction, out HashSet<string> seen) && create)
		{
			// Teams of a game that was left.
			List<Faction> gone = new List<Faction>();
			foreach (Faction key in Seen.Keys)
			{
				if (key == null)
				{
					gone.Add(key);
				}
			}
			foreach (Faction key in gone)
			{
				Seen.Remove(key);
			}
			seen = new HashSet<string>();
			Seen[faction] = seen;
		}
		return seen;
	}

	internal static string Encode(Faction faction)
	{
		HashSet<string> seen = Of(faction, create: false);
		return (seen != null) ? string.Join(",", new List<string>(seen).ToArray()) : string.Empty;
	}

	internal static void Decode(Faction faction, string text)
	{
		HashSet<string> seen = Of(faction, create: true);
		if (seen == null || string.IsNullOrEmpty(text))
		{
			return;
		}
		foreach (string name in text.Split(','))
		{
			if (name.Length > 0)
			{
				seen.Add(name);
			}
		}
	}
}

// Saves: an extra "seenResources" property on the Faction component.
[Feature(Features.ResourceList, Features.ResourceListInfo)]
[HarmonyPatch(typeof(DefaultGameContractResolver), nameof(DefaultGameContractResolver.GenerateDefaultMonoBehaviourContract))]
internal static class ResourceSeenSave
{
	private const string Property = "seenResources";

	private class SeenProvider : IValueProvider
	{
		public object GetValue(object target)
		{
			return ResourceSeen.Encode(target as Faction);
		}

		public void SetValue(object target, object value)
		{
			ResourceSeen.Decode(target as Faction, Convert.ToString(value));
		}
	}

	private static void Postfix(Type objectType, JsonObjectContract __result)
	{
		if (objectType != typeof(Faction) || __result == null || __result.Properties.Contains(Property))
		{
			return;
		}
		__result.Properties.AddProperty(new JsonProperty
		{
			PropertyName = Property,
			UnderlyingName = Property,
			PropertyType = typeof(string),
			DeclaringType = typeof(Faction),
			ValueProvider = new SeenProvider(),
			Readable = true,
			Writable = true
		});
	}
}
