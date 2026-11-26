using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.Combat;

// The rings under units, crystals and buildings (SelectionCircle) draw health, stamina and the side's colour through
// their animator, which writes those values into the ring's renderer. A unit's ring is a child of the unit. When the
// unit is destroyed rather than given back to its pool (Object.Destroy: as a multiplayer client does with what the host
// takes away, and as tests do), Unity switches the components of the whole hierarchy off first; the unit's own clean-up
// then hands its ring back to the ring pool, so the ring outlives the unit with its animator switched off. Every later
// unit, crystal or building that got that ring showed the material's own values (a yellow ring at about 80 %, the
// stamina ring on) and never followed its health (#196). A ring now switches its animator back on when it goes back to
// the pool and when it is taken from it.
[Feature]
internal static class HealthRingAnimator
{
	private static readonly AccessTools.FieldRef<SelectionCircle, Animator> AnimatorOf = AccessTools.FieldRefAccess<SelectionCircle, Animator>("animator");

	internal static void Wake(SelectionCircle circle)
	{
		if (circle == null)
		{
			return;
		}
		Animator animator = AnimatorOf(circle);
		if (animator == null)
		{
			animator = circle.GetComponent<Animator>();
		}
		if (animator != null && !animator.enabled)
		{
			animator.enabled = true;
		}
	}
}

[Feature]
[HarmonyPatch(typeof(SelectionCircle), nameof(SelectionCircle.OnRequested))]
internal static class HealthRingRequestedPatch
{
	private static void Postfix(SelectionCircle __instance)
	{
		HealthRingAnimator.Wake(__instance);
	}
}

[Feature]
[HarmonyPatch(typeof(SelectionCircle), nameof(SelectionCircle.ResetComponent))]
internal static class HealthRingResetPatch
{
	private static void Postfix(SelectionCircle __instance)
	{
		HealthRingAnimator.Wake(__instance);
	}
}
