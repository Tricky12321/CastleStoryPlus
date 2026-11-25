using System;

namespace CastleStoryPlus.Rendering;

// The values WardAnimations hands the wards' animators, particle systems and link lines, as pure logic: each switch
// off gives back the game's own value. Unit tests: CastleStoryPlus.UnitTests/Rendering.
internal static class WardAnimationRules
{
	// Of a healing ward's particles, the share left with FewerHealParticles.
	internal const float HealShare = 0.25f;

	// A healing ward particle system's emission rate.
	internal static float HealRate(float original, bool fewer)
	{
		return fewer ? original * HealShare : original;
	}

	// A healing ward particle system's particle cap: at least one, so a system with any particles keeps showing.
	internal static int HealMax(int original, bool fewer)
	{
		if (!fewer || original <= 0)
		{
			return original;
		}
		return Math.Max(1, (int)Math.Ceiling(original * HealShare));
	}

	// The sentinel ward's gem rotation speed: without the defense animation it never turns faster than on standby
	// (the game speeds it up while it aims and shoots).
	internal static float DefenseSpeed(float speed, float standby, bool still)
	{
		return still ? Math.Min(speed, standby) : speed;
	}

	// The sway of a link line (the beam shader's _Amplitude): none with StillLinks.
	internal static float LinkAmplitude(float original, bool still)
	{
		return still ? 0f : original;
	}
}
