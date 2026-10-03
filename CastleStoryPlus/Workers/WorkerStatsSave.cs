using System;
using Brix.IO.Serialization.Contracts;
using CastleStoryPlus.Core;
using HarmonyLib;
using Newtonsoft.Json.Serialization;

namespace CastleStoryPlus.Workers;

// Saves the stats as extra properties on the existing CharacterState component ("workXp", "combatXp",
// "callToArmsRole", "tier"). The unmodded game ignores unknown properties, so saves stay loadable without the mod.
[Feature]
[HarmonyPatch(typeof(DefaultGameContractResolver), nameof(DefaultGameContractResolver.GenerateDefaultMonoBehaviourContract))]
internal static class WorkerStatsSave
{
	private class StatProvider : IValueProvider
	{
		private readonly Func<WorkerStats, int> _get;

		private readonly Action<WorkerStats, int> _set;

		public StatProvider(Func<WorkerStats, int> get, Action<WorkerStats, int> set)
		{
			_get = get;
			_set = set;
		}

		public object GetValue(object target)
		{
			WorkerStats stats = WorkerStats.Peek(target as CharacterState);
			return (stats == null) ? 0 : _get(stats);
		}

		public void SetValue(object target, object value)
		{
			WorkerStats stats = WorkerStats.For(target as CharacterState);
			if (stats != null)
			{
				_set(stats, Convert.ToInt32(value));
			}
		}
	}

	private static void Postfix(Type objectType, JsonObjectContract __result)
	{
		if (objectType != typeof(CharacterState) || __result == null || __result.Properties.Contains("workXp"))
		{
			return;
		}
		Add(__result, "workXp", (WorkerStats s) => s.WorkXp, (WorkerStats s, int v) => s.WorkXp = v);
		Add(__result, "combatXp", (WorkerStats s) => s.CombatXp, (WorkerStats s, int v) => s.CombatXp = v);
		Add(__result, "callToArmsRole", (WorkerStats s) => s.CallToArmsRole, (WorkerStats s, int v) => s.CallToArmsRole = v);
		Add(__result, "tier", (WorkerStats s) => s.Tier, (WorkerStats s, int v) => s.Tier = v);
	}

	private static void Add(JsonObjectContract contract, string name, Func<WorkerStats, int> get, Action<WorkerStats, int> set)
	{
		contract.Properties.AddProperty(new JsonProperty
		{
			PropertyName = name,
			UnderlyingName = name,
			PropertyType = typeof(int),
			DeclaringType = typeof(CharacterState),
			ValueProvider = new StatProvider(get, set),
			Readable = true,
			Writable = true
		});
	}
}
