using System;

namespace CastleStoryPlus.Weather;

// The weather's state and its hourly roll (the host's). Pure: the clock, the settings and the random numbers come in
// as arguments, so the whole cycle (rest, wait, rain, rest) can be run in a unit test.
internal sealed class RainWeather
{
	internal WeatherKind Current;

	// Game hours left of what falls now.
	internal float HoursLeft;

	// Game hours of rest left before the next may start.
	internal float Rest = RainRules.RestHours;

	// Game hours counted towards the next whole-hour roll.
	internal float HourCarry;

	// The clock at the last look; NaN before the first.
	internal float LastClock = float.NaN;

	// A new game (or leaving one): nothing falls, a fresh rest, no clock seen.
	internal void Reset()
	{
		Current = WeatherKind.None;
		HoursLeft = 0f;
		LastClock = float.NaN;
		HourCarry = 0f;
		Rest = RainRules.RestHours;
	}

	// The game hours since the last look at the clock (see RainRules.HoursPassed).
	internal float Advance(float clock)
	{
		float passed = RainRules.HoursPassed(clock, LastClock);
		LastClock = clock;
		return passed;
	}

	// Sets what falls and for how long; returns what fell before.
	internal WeatherKind Set(WeatherKind kind, float hours)
	{
		WeatherKind from = Current;
		Current = kind;
		HoursLeft = hours;
		return from;
	}

	// The host starts the weather (None ends it, with a rest after); returns what fell before.
	internal WeatherKind Begin(WeatherKind kind, float hours)
	{
		WeatherKind from = Set(kind, RainRules.BeginHours(kind, hours));
		if (kind == WeatherKind.None)
		{
			Rest = RainRules.RestHours;
		}
		return from;
	}

	// Counts the weather down (and ends it with a rest), or the rest down, or rolls once for each whole hour gone by
	// whether a new one starts. next gives random numbers from 0 up to 1.
	internal void Roll(float hours, float daysBetween, float rainHours, WeatherKind fall, Func<double> next)
	{
		if (Current != WeatherKind.None)
		{
			HoursLeft -= hours;
			if (HoursLeft <= 0f)
			{
				Set(WeatherKind.None, 0f);
				Rest = RainRules.RestHours;
			}
			return;
		}
		if (Rest > 0f)
		{
			Rest -= hours;
			return;
		}
		if (!RainRules.Rolls(daysBetween, fall))
		{
			return;
		}
		HourCarry += hours;
		while (HourCarry >= 1f)
		{
			HourCarry -= 1f;
			float wait = RainRules.WaitHours(daysBetween, rainHours);
			if (RainRules.Starts(next(), wait))
			{
				Begin(fall, RainRules.RainHoursFor(rainHours, next()));
				return;
			}
		}
	}
}
