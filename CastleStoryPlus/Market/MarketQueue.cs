using System;
using System.Collections.Generic;
using Brix.Game.Components;
using Brix.IO.Serialization.Contracts;
using CastleStoryPlus.Core;
using HarmonyLib;
using Newtonsoft.Json.Serialization;

namespace CastleStoryPlus.Market;

// A market queues as many trades as every workshop (WideRecipeIds.MaxOrders). Saves made when only the market
// queued more than 8 kept its 9th to 12th trade as "plusExtraRecipes" on the crafting state (8 recipe ids of a byte
// each in one number, as the game packs the first 8): such a save loads them into the queue, and that property is
// no longer written.
[Feature(Features.Market, Features.MarketInfo)]
internal static class MarketQueue
{
	internal const int Slots = WideRecipeIds.MaxOrders;

	internal const string Property = "plusExtraRecipes";

	// The old property's trades, after the first 8, as whole recipe ids.
	internal static int[] Unpack(ulong value)
	{
		int[] ids = new int[8];
		CraftingState.FromLong(value, ids);
		List<int> result = new List<int>();
		foreach (int id in ids)
		{
			if (id != 0)
			{
				result.Add(id);
			}
		}
		return result.ToArray();
	}
}

// Old saves: "plusExtraRecipes" read into the queue, never written.
[Feature(Features.Market, Features.MarketInfo)]
[HarmonyPatch(typeof(DefaultGameContractResolver), nameof(DefaultGameContractResolver.GenerateDefaultMonoBehaviourContract))]
internal static class MarketQueueSave
{
	private class ExtraProvider : IValueProvider
	{
		public object GetValue(object target)
		{
			return 0uL;
		}

		public void SetValue(object target, object value)
		{
			CraftingState state = target as CraftingState;
			ulong extra = Convert.ToUInt64(value);
			if (state != null && extra != 0uL && WideRecipeIds.TailOf(state).Length == 0)
			{
				WideRecipeIds.SetTail(state, MarketQueue.Unpack(extra));
			}
		}
	}

	private static void Postfix(Type objectType, JsonObjectContract __result)
	{
		if (objectType != typeof(CraftingState) || __result == null || __result.Properties.Contains(MarketQueue.Property))
		{
			return;
		}
		__result.Properties.AddProperty(new JsonProperty
		{
			PropertyName = MarketQueue.Property,
			UnderlyingName = MarketQueue.Property,
			PropertyType = typeof(ulong),
			DeclaringType = typeof(CraftingState),
			ValueProvider = new ExtraProvider(),
			Readable = true,
			Writable = true,
			ShouldSerialize = (object target) => false
		});
	}
}
