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
//
// Queues of up to MaxOrders orders (the game: 8, all its number holds). The 9th order on is kept here per workshop
// as whole recipe ids (Tail), read back after the first 8, synced after the high bytes and saved as "recipesMore".
// A save without the mod keeps the first 8.
[Feature]
[HarmonyPatch]
internal static class WideRecipeIds
{
	// The most a recipe id may be.
	internal const int MaxId = 0xFFFF;

	// The most orders a workshop's queue holds.
	internal const int MaxOrders = 20;

	private const int Slots = 8;

	// Set when the high bytes or the orders after the 8th change, so the next update is synced.
	private const uint DirtyBit = 0x20000000u;

	private static readonly int[] NoTail = new int[0];

	private static readonly Dictionary<CraftingState, ulong> High = new Dictionary<CraftingState, ulong>();

	private static readonly Dictionary<CraftingState, int[]> Tail = new Dictionary<CraftingState, int[]>();

	private static void Enable()
	{
		GameSession.OnLeave(() =>
		{
			High.Clear();
			Tail.Clear();
		});
	}

	// The recipe ids of the orders after the 8th, in order; empty when there are none.
	internal static int[] TailOf(CraftingState state)
	{
		return (state != null && Tail.TryGetValue(state, out int[] tail)) ? tail : NoTail;
	}

	internal static void SetTail(CraftingState state, int[] tail)
	{
		if (state == null)
		{
			return;
		}
		tail = tail ?? NoTail;
		if (tail.Length > MaxOrders - Slots)
		{
			int[] cut = new int[MaxOrders - Slots];
			Array.Copy(tail, cut, cut.Length);
			tail = cut;
		}
		if (SameIds(TailOf(state), tail))
		{
			return;
		}
		if (tail.Length == 0)
		{
			Tail.Remove(state);
		}
		else
		{
			Tail[state] = tail;
		}
		Changed(state);
	}

	private static bool SameIds(int[] a, int[] b)
	{
		if (a.Length != b.Length)
		{
			return false;
		}
		for (int i = 0; i < a.Length; i++)
		{
			if (a[i] != b[i])
			{
				return false;
			}
		}
		return true;
	}

	// The workshop's queue is cached by the low bytes only: read it again.
	private static void Changed(CraftingState state)
	{
		CraftingLabor labor = state.GetComponent<CraftingLabor>();
		if (labor != null)
		{
			labor._recipesHash = ~state.recipes;
		}
		state.queueChanged = true;
		state.enabled = true;
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
		Changed(state);
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
		int[] tail = NoTail;
		if (recipes != null && recipes.Count > Slots)
		{
			tail = new int[Math.Min(recipes.Count, MaxOrders) - Slots];
			for (int i = 0; i < tail.Length; i++)
			{
				tail[i] = recipes[Slots + i].id;
			}
		}
		bool dirty = false;
		if (HighOf(__instance) != high)
		{
			SetHigh(__instance, high);
			dirty = true;
		}
		if (!SameIds(TailOf(__instance), tail))
		{
			SetTail(__instance, tail);
			dirty = true;
		}
		if (dirty && NetworkServer.active)
		{
			__instance.SetDirtyBit(DirtyBit);
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
		foreach (int id in TailOf(__instance))
		{
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
		int[] tail = TailOf(__instance);
		writer.WritePackedUInt32((uint)tail.Length);
		foreach (int id in tail)
		{
			writer.WritePackedUInt32((uint)id);
		}
		__result = true;
	}

	[HarmonyPatch(typeof(CraftingState), nameof(CraftingState.OnDeserialize))]
	[HarmonyPostfix]
	[HarmonyPriority(Priority.First)]
	private static void Read(CraftingState __instance, NetworkReader reader)
	{
		SetHigh(__instance, reader.ReadPackedUInt64());
		int count = (int)reader.ReadPackedUInt32();
		int[] tail = (count > 0) ? new int[count] : NoTail;
		for (int i = 0; i < count; i++)
		{
			tail[i] = (int)reader.ReadPackedUInt32();
		}
		SetTail(__instance, tail);
	}

	// Saves: an extra "recipesHigh" property on the workshop's CraftingState.
	[HarmonyPatch(typeof(DefaultGameContractResolver), nameof(DefaultGameContractResolver.GenerateDefaultMonoBehaviourContract))]
	[HarmonyPostfix]
	private static void Save(Type objectType, JsonObjectContract __result)
	{
		const string property = "recipesHigh";
		const string more = "recipesMore";
		if (objectType != typeof(CraftingState) || __result == null)
		{
			return;
		}
		if (!__result.Properties.Contains(more))
		{
			__result.Properties.AddProperty(new JsonProperty
			{
				PropertyName = more,
				UnderlyingName = more,
				PropertyType = typeof(int[]),
				DeclaringType = typeof(CraftingState),
				ValueProvider = new TailProvider(),
				Readable = true,
				Writable = true
			});
		}
		if (__result.Properties.Contains(property))
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

	private class TailProvider : IValueProvider
	{
		public object GetValue(object target)
		{
			return TailOf(target as CraftingState);
		}

		public void SetValue(object target, object value)
		{
			SetTail(target as CraftingState, value as int[]);
		}
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

// Queuing at a workshop: up to WideRecipeIds.MaxOrders orders (the game stopped at 8). Runs after the prefixes that
// refuse an order (a research whose line is busy).
[Feature]
[HarmonyPatch(typeof(CraftingLabor), nameof(CraftingLabor.QueueRecipe))]
internal static class WorkshopQueueLength
{
	[HarmonyPriority(Priority.Low)]
	private static bool Prefix(CraftingLabor __instance, RecipeInfo recipe, int qty, bool __runOriginal)
	{
		if (!__runOriginal)
		{
			return false;
		}
		List<RecipeInfo> queue = __instance.QueuedRecipes;
		for (int i = 0; i < qty; i++)
		{
			if (queue.Count >= WideRecipeIds.MaxOrders)
			{
				break;
			}
			queue.Add(recipe);
		}
		__instance.state.SetRecipes(queue);
		return false;
	}
}
