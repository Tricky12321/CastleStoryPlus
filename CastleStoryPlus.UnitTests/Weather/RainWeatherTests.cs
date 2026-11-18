using System;
using System.Collections.Generic;
using CastleStoryPlus.Weather;
using NUnit.Framework;

namespace CastleStoryPlus.UnitTests.Weather;

[TestFixture]
public class RainWeatherTests
{
	private static Func<double> Fixed(double value)
	{
		return () => value;
	}

	private static Func<double> Sequence(params double[] values)
	{
		Queue<double> queue = new Queue<double>(values);
		return () => queue.Dequeue();
	}

	// A weather that is past its rest.
	private static RainWeather Rested()
	{
		return new RainWeather { Rest = 0f };
	}

	[Test]
	public void StartsWithARestAndNoWeather()
	{
		RainWeather weather = new RainWeather();
		Assert.That(weather.Current, Is.EqualTo(WeatherKind.None));
		Assert.That(weather.Rest, Is.EqualTo(RainRules.RestHours));
		Assert.That(float.IsNaN(weather.LastClock), Is.True);
	}

	[Test]
	public void AdvanceCountsFromTheSecondLook()
	{
		RainWeather weather = new RainWeather();
		Assert.That(weather.Advance(8f), Is.EqualTo(0f));
		Assert.That(weather.Advance(8.5f), Is.EqualTo(0.5f).Within(1e-4f));
		Assert.That(weather.Advance(8.5f), Is.EqualTo(0f));
	}

	[Test]
	public void AdvanceAcrossMidnightAndAClockSet()
	{
		RainWeather weather = new RainWeather();
		weather.Advance(23.75f);
		Assert.That(weather.Advance(0.25f), Is.EqualTo(0.5f).Within(1e-4f));
		// The clock set to 18: a jump of 17.75 hours is not counted, and the next look counts from there.
		Assert.That(weather.Advance(18f), Is.EqualTo(0f));
		Assert.That(weather.Advance(19f), Is.EqualTo(1f).Within(1e-4f));
	}

	[Test]
	public void AdvanceAClockSetBackwardsIsNotCounted()
	{
		RainWeather weather = new RainWeather();
		weather.Advance(12f);
		Assert.That(weather.Advance(6f), Is.EqualTo(0f));
		Assert.That(weather.Advance(7f), Is.EqualTo(1f).Within(1e-4f));
	}

	[Test]
	public void RestCountsDownBeforeAnyRoll()
	{
		RainWeather weather = new RainWeather();
		weather.Roll(2f, 3f, 3f, WeatherKind.Rain, Fixed(0.0));
		Assert.That(weather.Rest, Is.EqualTo(4f));
		Assert.That(weather.Current, Is.EqualTo(WeatherKind.None));
		weather.Roll(4f, 3f, 3f, WeatherKind.Rain, Fixed(0.0));
		Assert.That(weather.Rest, Is.EqualTo(0f));
		Assert.That(weather.Current, Is.EqualTo(WeatherKind.None));
		Assert.That(weather.HourCarry, Is.EqualTo(0f));
	}

	[Test]
	public void NoRollWhileResting()
	{
		RainWeather weather = new RainWeather();
		// 6 hours exactly use up the rest; the roll comes the next time.
		weather.Roll(6f, 3f, 3f, WeatherKind.Rain, () => throw new InvalidOperationException("rolled"));
		Assert.That(weather.Current, Is.EqualTo(WeatherKind.None));
	}

	[Test]
	public void RollsOncePerWholeHour()
	{
		RainWeather weather = Rested();
		int rolls = 0;
		Func<double> never = () =>
		{
			rolls++;
			return 0.99;
		};
		weather.Roll(0.4f, 3f, 3f, WeatherKind.Rain, never);
		Assert.That(rolls, Is.EqualTo(0));
		weather.Roll(0.4f, 3f, 3f, WeatherKind.Rain, never);
		Assert.That(rolls, Is.EqualTo(0));
		weather.Roll(0.4f, 3f, 3f, WeatherKind.Rain, never);
		Assert.That(rolls, Is.EqualTo(1));
		Assert.That(weather.HourCarry, Is.EqualTo(0.2f).Within(1e-4f));
		weather.Roll(5f, 3f, 3f, WeatherKind.Rain, never);
		Assert.That(rolls, Is.EqualTo(6));
	}

	[Test]
	public void ARollThatHitsStartsTheWeatherAndStopsRolling()
	{
		RainWeather weather = Rested();
		// 3 days, 3 h rain: wait 63 h, chance 1/63; the second number sets the length.
		weather.Roll(3f, 3f, 3f, WeatherKind.Rain, Sequence(0.5, 0.0, 0.5, 0.0));
		Assert.That(weather.Current, Is.EqualTo(WeatherKind.Rain));
		// Rolled twice (0.5 no, 0.0 yes with length roll 0.5 = 3 h); the third hour is not rolled.
		Assert.That(weather.HoursLeft, Is.EqualTo(3f).Within(1e-4f));
		Assert.That(weather.HourCarry, Is.EqualTo(1f).Within(1e-4f));
	}

	[Test]
	public void SnowWorldGivesSnow()
	{
		RainWeather weather = Rested();
		weather.Roll(1f, 3f, 3f, WeatherKind.Snow, Sequence(0.0, 1.0));
		Assert.That(weather.Current, Is.EqualTo(WeatherKind.Snow));
		Assert.That(weather.HoursLeft, Is.EqualTo(4.2f).Within(1e-4f));
	}

	[Test]
	public void DesertAndZeroDaysNeverRoll()
	{
		RainWeather desert = Rested();
		desert.Roll(100f, 3f, 3f, WeatherKind.None, () => throw new InvalidOperationException("rolled"));
		Assert.That(desert.Current, Is.EqualTo(WeatherKind.None));
		RainWeather off = Rested();
		off.Roll(100f, 0f, 3f, WeatherKind.Rain, () => throw new InvalidOperationException("rolled"));
		Assert.That(off.Current, Is.EqualTo(WeatherKind.None));
		Assert.That(off.HourCarry, Is.EqualTo(0f));
	}

	[Test]
	public void RainCountsDownAndEndsWithARest()
	{
		RainWeather weather = Rested();
		weather.Set(WeatherKind.Rain, 2f);
		weather.Roll(1.5f, 3f, 3f, WeatherKind.Rain, Fixed(0.0));
		Assert.That(weather.Current, Is.EqualTo(WeatherKind.Rain));
		Assert.That(weather.HoursLeft, Is.EqualTo(0.5f).Within(1e-4f));
		weather.Roll(0.5f, 3f, 3f, WeatherKind.Rain, Fixed(0.0));
		Assert.That(weather.Current, Is.EqualTo(WeatherKind.None));
		Assert.That(weather.HoursLeft, Is.EqualTo(0f));
		Assert.That(weather.Rest, Is.EqualTo(RainRules.RestHours));
	}

	[Test]
	public void RainDoesNotRollForANewRainWhileItFalls()
	{
		RainWeather weather = Rested();
		weather.Set(WeatherKind.Rain, 10f);
		weather.Roll(3f, 3f, 3f, WeatherKind.Rain, () => throw new InvalidOperationException("rolled"));
		Assert.That(weather.HoursLeft, Is.EqualTo(7f));
	}

	[Test]
	public void ASetClockThatGivesNoHoursChangesNothing()
	{
		RainWeather weather = Rested();
		weather.Set(WeatherKind.Rain, 2f);
		weather.Roll(0f, 3f, 3f, WeatherKind.Rain, Fixed(0.0));
		Assert.That(weather.HoursLeft, Is.EqualTo(2f));
	}

	[Test]
	public void BeginGivesAtLeastTheShortestWeather()
	{
		RainWeather weather = Rested();
		WeatherKind from = weather.Begin(WeatherKind.Rain, 0f);
		Assert.That(from, Is.EqualTo(WeatherKind.None));
		Assert.That(weather.HoursLeft, Is.EqualTo(RainRules.MinBeginHours));
	}

	[Test]
	public void BeginNoneEndsItAndRests()
	{
		RainWeather weather = Rested();
		weather.Begin(WeatherKind.Snow, 5f);
		WeatherKind from = weather.Begin(WeatherKind.None, 5f);
		Assert.That(from, Is.EqualTo(WeatherKind.Snow));
		Assert.That(weather.Current, Is.EqualTo(WeatherKind.None));
		Assert.That(weather.HoursLeft, Is.EqualTo(0f));
		Assert.That(weather.Rest, Is.EqualTo(RainRules.RestHours));
	}

	[Test]
	public void ResetClearsEverything()
	{
		RainWeather weather = Rested();
		weather.Begin(WeatherKind.Rain, 3f);
		weather.Advance(4f);
		weather.HourCarry = 0.7f;
		weather.Reset();
		Assert.That(weather.Current, Is.EqualTo(WeatherKind.None));
		Assert.That(weather.HoursLeft, Is.EqualTo(0f));
		Assert.That(weather.Rest, Is.EqualTo(RainRules.RestHours));
		Assert.That(weather.HourCarry, Is.EqualTo(0f));
		Assert.That(float.IsNaN(weather.LastClock), Is.True);
	}

	[Test]
	public void LongRunAveragesTheDaysBetween()
	{
		// Three days between rains on average, an hour at a time, with a seeded generator: the mean wait from the end of
		// one rain to the end of the next is the setting (the cycle is rest + wait + rain).
		Random random = new Random(12345);
		RainWeather weather = new RainWeather();
		float days = 3f;
		float rainHours = 3f;
		int cycles = 0;
		int hoursRun = 0;
		bool wasRaining = false;
		for (int hour = 0; hour < 24 * 3000; hour++)
		{
			weather.Roll(1f, days, rainHours, WeatherKind.Rain, random.NextDouble);
			hoursRun++;
			bool raining = weather.Current != WeatherKind.None;
			if (wasRaining && !raining)
			{
				cycles++;
			}
			wasRaining = raining;
		}
		double averageDays = hoursRun / 24.0 / cycles;
		Assert.That(averageDays, Is.EqualTo(days).Within(0.2));
	}
}
