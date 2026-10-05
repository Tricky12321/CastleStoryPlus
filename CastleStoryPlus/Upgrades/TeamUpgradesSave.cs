using System;
using Brix.Game;
using Brix.IO.Serialization.Contracts;
using CastleStoryPlus.Core;
using HarmonyLib;
using Newtonsoft.Json.Serialization;

namespace CastleStoryPlus.Upgrades;

// Saves the researched tiers as an extra property on the existing Faction component ("upgrades": "2,1,0,...").
// The unmodded game ignores unknown properties, so saves stay loadable without the mod.
[Feature(Features.Upgrades, Features.UpgradesInfo)]
[HarmonyPatch(typeof(DefaultGameContractResolver), nameof(DefaultGameContractResolver.GenerateDefaultMonoBehaviourContract))]
internal static class TeamUpgradesSave
{
	private const string Property = "upgrades";

	private class TiersProvider : IValueProvider
	{
		public object GetValue(object target)
		{
			return TeamUpgrades.Encode(target as Faction);
		}

		public void SetValue(object target, object value)
		{
			TeamUpgrades.Decode(target as Faction, Convert.ToString(value));
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
			ValueProvider = new TiersProvider(),
			Readable = true,
			Writable = true
		});
	}
}
