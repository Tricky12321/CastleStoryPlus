using System;

namespace CastleStoryPlus.Weather;

// The numbers and decisions of the rain, as pure functions (no Unity, no game): Rain (the MonoBehaviour) reads the
// settings and the game's clock and calls these. Unit tests: CastleStoryPlus.UnitTests/Weather.
internal static class RainRules
{
	// Game hours of rest after a rain before the next may start.
	internal const float RestHours = 6f;

	// Columns looked at to see what the ground of the world is.
	internal const int GroundSamples = 600;

	// Fewer columns with ground than this and the world counts as ordinary (rain).
	internal const int MinGroundSamples = 20;

	// The shortest weather a host can start (a start of 0 hours would end at once).
	internal const float MinBeginHours = 0.05f;

	// A rain lasts 60% to 140% of the setting.
	internal const float MinLengthShare = 0.6f;

	internal const float LengthSpread = 0.8f;

	// Game hours (clock 0 to 24) that went by between two looks at the clock. 0 for the first look, and for a jump
	// of more than 12 hours forward (the clock was set, or a day was skipped); a jump back wraps round midnight, so it
	// reads as a long way forward and gives 0 too (the game's clock only runs forward).
	internal static float HoursPassed(float clock, float lastClock)
	{
		if (float.IsNaN(lastClock) || float.IsNaN(clock))
		{
			return 0f;
		}
		float passed = Repeat(clock - lastClock, 24f);
		return (passed > 12f) ? 0f : passed;
	}

	// Unity's Mathf.Repeat.
	internal static float Repeat(float t, float length)
	{
		float result = t - (float)Math.Floor(t / length) * length;
		if (result < 0f)
		{
			return 0f;
		}
		return (result > length) ? length : result;
	}

	// Game hours between the end of a rest and the start of a rain that make the wait average the days between: the
	// whole cycle (days * 24 h) is the rest, the rain's own hours and this wait. At least 1, so the chance of a roll
	// never goes over 1.
	internal static float WaitHours(float daysBetween, float rainHours)
	{
		return Math.Max(1f, daysBetween * 24f - RestHours - rainHours);
	}

	// One hourly roll: true when the rain starts. random is 0 up to but not including 1.
	internal static bool Starts(double random, float wait)
	{
		return random < 1.0 / wait;
	}

	// How many hours a new rain lasts (random 0 up to 1: 60% to 140% of the setting).
	internal static float RainHoursFor(float averageHours, double random)
	{
		return averageHours * (MinLengthShare + LengthSpread * (float)random);
	}

	// Whether the weather rolls by itself at all: 0 days between switches it off, and so does a world with no weather.
	internal static bool Rolls(float daysBetween, WeatherKind fall)
	{
		return daysBetween > 0f && fall != WeatherKind.None;
	}

	// The hours a host's start gets: None ends the weather (0), any other at least MinBeginHours.
	internal static float BeginHours(WeatherKind kind, float hours)
	{
		return (kind == WeatherKind.None) ? 0f : Math.Max(MinBeginHours, hours);
	}

	// What falls from what the ground is made of: mostly sand (more than half): none (desert), mostly snow: snow, else
	// rain. Too few columns with ground to tell (a sparse map): rain.
	internal static WeatherKind DetectFall(int sand, int snow, int other)
	{
		int total = sand + snow + other;
		if (total < MinGroundSamples)
		{
			return WeatherKind.Rain;
		}
		if (sand * 2 > total)
		{
			return WeatherKind.None;
		}
		return (snow * 2 > total) ? WeatherKind.Snow : WeatherKind.Rain;
	}

	// The columns of a sparse map (islands in the sky) are mostly empty: keep looking until enough have ground, but
	// not for more than ten times the wanted number of looks.
	internal static bool KeepSampling(int looks, int counted)
	{
		return looks < GroundSamples * 10 && counted < GroundSamples;
	}

	// The seed of the column picker, from the world's size: the same world gives the same answer every time.
	internal static int SampleSeed(int sizeX, int sizeZ)
	{
		return unchecked(sizeX * 31 + sizeZ);
	}

	// How much faster things grow while it rains (a boost setting counts only for rain; not set: 1).
	internal static float Boost(WeatherKind current, float? setting)
	{
		return (current == WeatherKind.Rain && setting.HasValue) ? setting.Value : 1f;
	}
}
