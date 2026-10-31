using System.Collections.Generic;
using Brix.External.Factories;
using Brix.Game.AI;
using CastleStoryPlus.Core;
using HarmonyLib;

namespace CastleStoryPlus.Performance;

// How a unit holds what it carries (Interaction.CarriedType: in the hands, on the back, in a bag) was looked up every
// time it was asked: two lookups by name (the factory, then the template), the template's GameObject and a
// GetComponent<Grip> on it, all for a template that does not change. A walking or running unit asks it every frame
// (Locomotion4.CanRun from the Walking, Running and InPlace updates), the AI asks it too (CanClimb, CanStrike). The
// answer is now remembered per unit together with the prop it was worked out for, and worked out again as soon as the
// unit carries another prop (compared by reference, so a new key always looks again). Carrying nothing is answered as
// the game does, without a lookup.
[Feature(Features.FrameSavers, Features.FrameSaversInfo)]
[HarmonyPatch(typeof(Interaction), nameof(Interaction.CarriedType), MethodType.Getter)]
internal static class CarriedTypeCache
{
	private struct Carried
	{
		internal Factory.AssetKey Prop;

		internal TakeType Type;
	}

	private static readonly Dictionary<Interaction, Carried> Known = new Dictionary<Interaction, Carried>();

	private static void Enable()
	{
		GameSession.OnLeave(Known.Clear);
	}

	private static bool Prefix(Interaction __instance, ref TakeType __result)
	{
		Factory.AssetKey prop = __instance.CarriedProp;
		if (prop == null)
		{
			__result = TakeType.None;
			return false;
		}
		if (Known.TryGetValue(__instance, out Carried carried) && ReferenceEquals(carried.Prop, prop))
		{
			__result = carried.Type;
			return false;
		}
		return true;
	}

	private static void Postfix(Interaction __instance, TakeType __result, bool __runOriginal)
	{
		if (!__runOriginal)
		{
			return;
		}
		Factory.AssetKey prop = __instance.CarriedProp;
		if (prop == null)
		{
			return;
		}
		Known[__instance] = new Carried { Prop = prop, Type = __result };
	}
}
