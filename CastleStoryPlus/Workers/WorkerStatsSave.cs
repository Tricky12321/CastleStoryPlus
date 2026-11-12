using System;
using Brix.IO.Serialization.Contracts;
using CastleStoryPlus.Core;
using HarmonyLib;
using Newtonsoft.Json.Serialization;

namespace CastleStoryPlus.Workers;

// Saves the stats as extra properties on the existing CharacterState component ("workXp", "combatXp",
// "callToArmsRole", "tier", "variant", "guard", "hasPost", "postX", "postY", "postZ"). The unmodded game ignores unknown properties, so saves stay loadable without the mod.
[Feature]
[HarmonyPatch(typeof(DefaultGameContractResolver), nameof(DefaultGameContractResolver.GenerateDefaultMonoBehaviourContract))]
internal static class WorkerStatsSave
{
	private class StatProvider : IValueProvider
	{
		private readonly Func<WorkerStats, int> _get;

		private readonly Action<WorkerStats, int> _set;

		// Told about the unit once its stat is loaded (the variant's features).
		private readonly Action<CharacterState> _loaded;

		public StatProvider(Func<WorkerStats, int> get, Action<WorkerStats, int> set, Action<CharacterState> loaded)
		{
			_get = get;
			_set = set;
			_loaded = loaded;
		}

		public object GetValue(object target)
		{
			WorkerStats stats = WorkerStats.Peek(target as CharacterState);
			return (stats == null) ? 0 : _get(stats);
		}

		public void SetValue(object target, object value)
		{
			CharacterState state = target as CharacterState;
			WorkerStats stats = WorkerStats.For(state);
			if (stats != null)
			{
				_set(stats, Convert.ToInt32(value));
				_loaded?.Invoke(state);
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
		Add(__result, "variant", (WorkerStats s) => s.Variant, (WorkerStats s, int v) => s.Variant = v, WorkerStats.RaiseVariantChanged);
		Add(__result, "guard", (WorkerStats s) => s.Guard, (WorkerStats s, int v) => s.Guard = v);
		Add(__result, "hasPost", (WorkerStats s) => s.HasPost, (WorkerStats s, int v) => s.HasPost = v);
		Add(__result, "postX", (WorkerStats s) => s.PostX, (WorkerStats s, int v) => s.PostX = v);
		Add(__result, "postY", (WorkerStats s) => s.PostY, (WorkerStats s, int v) => s.PostY = v);
		Add(__result, "postZ", (WorkerStats s) => s.PostZ, (WorkerStats s, int v) => s.PostZ = v);
	}

	private static void Add(JsonObjectContract contract, string name, Func<WorkerStats, int> get, Action<WorkerStats, int> set, Action<CharacterState> loaded = null)
	{
		contract.Properties.AddProperty(new JsonProperty
		{
			PropertyName = name,
			UnderlyingName = name,
			PropertyType = typeof(int),
			DeclaringType = typeof(CharacterState),
			ValueProvider = new StatProvider(get, set, loaded),
			Readable = true,
			Writable = true
		});
	}
}
