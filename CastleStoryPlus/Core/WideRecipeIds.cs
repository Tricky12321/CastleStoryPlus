using System;
using System.Collections.Generic;
using Brix.Game.Components;
using Brix.IO.Serialization.Contracts;
using HarmonyLib;
using Newtonsoft.Json.Serialization;
using UnityEngine.Networking;

namespace CastleStoryPlus.Core;

// Recipe ids above 255. A workshop's queue is one 64-bit number with 8 bits per order, so the game can only queue
// recipes 1 to 255; the mod's market, upgrades and metallurgy need more. The game's number now holds the low byte of
// each order and a second number, kept here per workshop, the high byte: synced after the game's own data and saved
// as "recipesHigh". Old saves have no high bytes, so their queues load as before. No recipe gets an id whose low
// byte is 0, the game's mark for an empty slot.
[Feature]
[HarmonyPatch]
internal static class WideRecipeIds
{
	// The most a recipe id may be.
	internal const int MaxId = 0xFFFF;

	private const int Slots = 8;

	// Set when the high bytes change, so the next update is synced.
	private const uint DirtyBit = 0x20000000u;

	private static readonly Dictionary<CraftingState, ulong> High = new Dictionary<CraftingState, ulong>();

	private static void Enable()
	{
		GameSession.OnLeave(() => High.Clear());
	}

	internal static ulong HighOf(CraftingState state)
	{
		return (state != null && High.TryGetValue(state, out ulong high)) ? high : 0uL;
	}

	internal static void SetHigh(CraftingState state, ulong high)
	{
		if (state == null || HighOf(state) == high)
		{
			return;
		}
		if (high == 0uL)
		{
			High.Remove(state);
		}
		else
		{
			High[state] = high;
		}
		// The workshop's queue is cached by the low bytes only.
		CraftingLabor labor = state.GetComponent<CraftingLabor>();
		if (labor != null)
		{
			labor._recipesHash = ~state.recipes;
		}
		state.queueChanged = true;
		state.enabled = true;
	}

	// No recipe id with a low byte of 0.
	[HarmonyPatch(typeof(RecipeMaker), nameof(RecipeMaker.Make))]
	[HarmonyPrefix]
	private static void SkipEmptyLowByte()
	{
		if ((CookBook.nextId & 0xFF) == 0)
		{
			CookBook.nextId++;
		}
	}

	[HarmonyPatch(typeof(CraftingState), nameof(CraftingState.SetRecipes))]
	[HarmonyPrefix]
	private static bool SetRecipes(CraftingState __instance, List<RecipeInfo> recipes)
	{
		ulong low = 0uL;
		ulong high = 0uL;
		for (int i = 0; i < Slots && recipes != null && i < recipes.Count; i++)
		{
			int id = recipes[i].id;
			low |= (ulong)(id & 0xFF) << (8 * i);
			high |= (ulong)((id >> 8) & 0xFF) << (8 * i);
		}
		if (HighOf(__instance) != high)
		{
			SetHigh(__instance, high);
			if (NetworkServer.active)
			{
				__instance.SetDirtyBit(DirtyBit);
			}
		}
		__instance.Networkrecipes = low;
		return false;
	}

	[HarmonyPatch(typeof(CraftingState), nameof(CraftingState.GetRecipe))]
	[HarmonyPrefix]
	private static bool GetRecipe(CraftingState __instance, List<RecipeInfo> results)
	{
		results.Clear();
		ulong low = __instance.recipes;
		ulong high = HighOf(__instance);
		for (int i = 0; i < Slots; i++)
		{
			int id = (int)((low >> (8 * i)) & 0xFF);
			if (id == 0)
			{
				continue;
			}
			id |= (int)((high >> (8 * i)) & 0xFF) << 8;
			if (CookBook.RecipesById.TryGetValue(id, out RecipeInfo recipe))
			{
				results.Add(recipe);
			}
		}
		return false;
	}

	// Network: the high bytes after the game's data, in the initial state and every update. First, so they always
	// come before the data other features append.
	[HarmonyPatch(typeof(CraftingState), nameof(CraftingState.OnSerialize))]
	[HarmonyPostfix]
	[HarmonyPriority(Priority.First)]
	private static void Write(CraftingState __instance, NetworkWriter writer, ref bool __result)
	{
		writer.WritePackedUInt64(HighOf(__instance));
		__result = true;
	}

	[HarmonyPatch(typeof(CraftingState), nameof(CraftingState.OnDeserialize))]
	[HarmonyPostfix]
	[HarmonyPriority(Priority.First)]
	private static void Read(CraftingState __instance, NetworkReader reader)
	{
		SetHigh(__instance, reader.ReadPackedUInt64());
	}

	// Saves: an extra "recipesHigh" property on the workshop's CraftingState.
	[HarmonyPatch(typeof(DefaultGameContractResolver), nameof(DefaultGameContractResolver.GenerateDefaultMonoBehaviourContract))]
	[HarmonyPostfix]
	private static void Save(Type objectType, JsonObjectContract __result)
	{
		const string property = "recipesHigh";
		if (objectType != typeof(CraftingState) || __result == null || __result.Properties.Contains(property))
		{
			return;
		}
		__result.Properties.AddProperty(new JsonProperty
		{
			PropertyName = property,
			UnderlyingName = property,
			PropertyType = typeof(ulong),
			DeclaringType = typeof(CraftingState),
			ValueProvider = new HighProvider(),
			Readable = true,
			Writable = true
		});
	}

	private class HighProvider : IValueProvider
	{
		public object GetValue(object target)
		{
			return HighOf(target as CraftingState);
		}

		public void SetValue(object target, object value)
		{
			SetHigh(target as CraftingState, Convert.ToUInt64(value));
		}
	}
}
