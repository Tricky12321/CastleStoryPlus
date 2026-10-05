using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Brix.External.Factories;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.UI.Icons;
using CastleStoryPlus.Core;
using UnityEngine;

namespace CastleStoryPlus.Market;

// Market prices. Every resource has a base value and a heat (1 or more). A trade of A for B gives
//     out = floor(in * value(A) * (1 - fee) / (value(B) * heat(A) * heat(B)))
// so the base value that comes out is always less than the base value that goes in, whatever the heat, and any
// chain of trades loses value. The base values follow the game's own conversions (3 planks from a log, a rope
// from 10 plant fibres, an iron ingot from 10 raw iron and 2 orange crystals...): a converted resource is never
// worth more than what it is made of, so trading and converting in a circle loses value too.
// Trading a resource heats it up (worse prices for it); the heat falls back to 1 over time (game time).
[Feature(Features.Market, Features.MarketInfo)]
internal class MarketPrices : MonoBehaviour
{
	internal sealed class Good
	{
		public int Index;

		public Ressource Resource;

		public string Name;

		public float Value;

		public IconKey Icon;

		public Factory.AssetKey Asset;

		public float Heat = 1f;
	}

	internal sealed class Trade
	{
		public Good Give;

		public Good Get;

		public int GiveAmount;

		public RecipeInfo Recipe;
	}

	// Traded value (in base value units) that doubles the price of a resource.
	private const float HeatValue = 100f;

	// Base value aimed at for one trade, so cheap resources are traded in bigger lots.
	private const float LotValue = 10f;

	// Most of one resource a market holds; also the biggest lot.
	internal const int Capacity = 60;

	private const int TradeWork = 5;

	internal static ConfigEntry<float> Fee;

	internal static ConfigEntry<float> RecoveryMinutes;

	internal static readonly List<Good> Goods = new List<Good>();

	internal static readonly List<Trade> Trades = new List<Trade>();

	private static readonly Dictionary<int, Trade> TradesByRecipe = new Dictionary<int, Trade>();

	private static void Enable()
	{
		Fee = Plugin.Cfg.Bind("Market", "Fee", 0.15f, new ConfigDescription("Share of the value lost on every trade.", new AcceptableValueRange<float>(0.05f, 0.9f)));
		RecoveryMinutes = Plugin.Cfg.Bind("Market", "RecoveryMinutes", 5f, "Minutes (game time) for the extra price of a traded resource to fall by half.");
		AddGood(Adjectif.woodBlock, "Log", 3f, IconKeys._Stockpiled_Log);
		AddGood(Adjectif.plankBlock, "Plank", 1f, IconKeys._Stockpiled_Plank);
		AddGood(Adjectif.stones, "Raw stone", 1f, IconKeys._Stockpiled_Raw_Stone);
		AddGood(Adjectif.stoneBlock, "Brick", 1f, IconKeys._Stockpiled_Brick);
		AddGood(Adjectif.plant, "Plant fibre", 0.5f, IconKeys._Stockpiled_Plants);
		AddGood(Adjectif.rope, "Rope", 5f, IconKeys._Stockpiled_Rope);
		AddGood(Adjectif.fabric, "Fabric", 5f, IconKeys._Stockpiled_Fabric);
		AddGood(Adjectif.rawIron, "Raw iron", 1f, IconKeys._Stockpiled_Raw_Iron);
		AddGood(Adjectif.iron, "Iron", 14f, IconKeys._Stockpiled_Iron);
		AddGood(Adjectif.cog, "Cog", 14f, IconKeys._Stockpiled_Cog);
		AddGood(Adjectif.orangeCrystal, "Orange crystal", 2f, IconKeys._Stockpiled_Raw_Orange_Crystal);
		AddGood(Adjectif.blueCrystal, "Blue crystal", 4f, IconKeys._Stockpiled_Raw_Blue_Crystal);
		AddGood(Adjectif.glass, "Glass", 20f, IconKeys._Stockpiled_Glass);
		RegisterTrades();
		Plugin.Root.AddComponent<MarketPrices>();
	}

	private static void AddGood(Ressource resource, string name, float value, IconKey icon)
	{
		Factory.AssetKey asset = Description.RepresentativeOf(resource);
		if (asset == null)
		{
			Plugin.Log.LogWarning("Market: no object for " + name + ", left out");
			return;
		}
		Goods.Add(new Good
		{
			Index = Goods.Count,
			Resource = resource,
			Name = name,
			Value = value,
			Icon = icon,
			Asset = asset
		});
	}

	// One crafting recipe per pair. The recipe ids are packed in 8 bits by the crafting queue, so they must stay
	// below 256; they are made in the same order on every machine, after the game's own recipes.
	private static void RegisterTrades()
	{
		RecipeInfo first = CookBook.Sword;
		if (first == null)
		{
			return;
		}
		foreach (Good give in Goods)
		{
			foreach (Good get in Goods)
			{
				if (give == get)
				{
					continue;
				}
				RecipeMaker maker = new RecipeMaker
				{
					Work = TradeWork,
					Heat = 0,
					Creates = get.Asset,
					QteCreated = 1,
					DropOnCompletion = false,
					Icon = get.Icon,
					Drawing = give.Icon
				};
				int amount = LotSize(give, get);
				RecipeInfo recipe = maker.With(give.Resource, amount).Make();
				if (recipe.id > 255)
				{
					Plugin.Log.LogError("Market: recipe id " + recipe.id + " is too high for the crafting queue, trades left out");
					return;
				}
				Trade trade = new Trade { Give = give, Get = get, GiveAmount = amount, Recipe = recipe };
				Trades.Add(trade);
				TradesByRecipe[recipe.id] = trade;
			}
		}
		Plugin.Log.LogInfo("Market: " + Trades.Count + " trades registered");
	}

	// Enough to get at least one of the other resource at base prices, and about LotValue worth.
	private static int LotSize(Good give, Good get)
	{
		int byValue = Mathf.CeilToInt(LotValue / give.Value);
		int forOne = Mathf.CeilToInt(get.Value / (give.Value * (1f - Fee.Value)) - 0.0001f);
		return Mathf.Clamp(Math.Max(byValue, forOne), 1, Capacity);
	}

	internal static Trade TradeFor(RecipeInfo recipe)
	{
		if (recipe == null)
		{
			return null;
		}
		TradesByRecipe.TryGetValue(recipe.id, out Trade trade);
		return trade;
	}

	internal static Trade TradeFor(Good give, Good get)
	{
		foreach (Trade trade in Trades)
		{
			if (trade.Give == give && trade.Get == get)
			{
				return trade;
			}
		}
		return null;
	}

	// What a trade gives right now. 0 means the prices are too high; the market then gives the goods back.
	internal static int Output(Trade trade)
	{
		double value = (double)trade.GiveAmount * trade.Give.Value * (1.0 - Fee.Value);
		double price = (double)trade.Get.Value * trade.Give.Heat * trade.Get.Heat;
		return Math.Max(0, (int)Math.Floor(value / price + 1e-9));
	}

	// Server: a trade finished, the traded resources heat up.
	internal static void Traded(Trade trade, int output)
	{
		trade.Give.Heat += trade.GiveAmount * trade.Give.Value / HeatValue;
		trade.Get.Heat += output * trade.Get.Value / HeatValue;
	}

	// Extra price in percent, e.g. 25 for a heat of 1.25.
	internal static int Markup(Good good)
	{
		return Mathf.RoundToInt((good.Heat - 1f) * 100f);
	}

	private void Update()
	{
		float halfLife = Mathf.Max(0.1f, RecoveryMinutes.Value) * 60f;
		float keep = Mathf.Pow(0.5f, Time.deltaTime / halfLife);
		foreach (Good good in Goods)
		{
			if (good.Heat > 1f)
			{
				good.Heat = 1f + (good.Heat - 1f) * keep;
			}
		}
	}
}
