using System;
using CastleStoryPlus.Weather;
using NUnit.Framework;

namespace CastleStoryPlus.UnitTests.Weather;

[TestFixture]
public class RainRulesTests
{
	[TestCase(10f, 9f, 1f)]
	[TestCase(0f, 0f, 0f)]
	[TestCase(12f, 0f, 12f)]
	[TestCase(12.5f, 0.5f, 12f)]
	public void HoursPassedForwardCounts(float clock, float last, float expected)
	{
		Assert.That(RainRules.HoursPassed(clock, last), Is.EqualTo(expected).Within(1e-4f));
	}

	[Test]
	public void HoursPassedFirstLookIsZero()
	{
		Assert.That(RainRules.HoursPassed(13f, float.NaN), Is.EqualTo(0f));
	}

	[Test]
	public void HoursPassedWrapsRoundMidnight()
	{
		Assert.That(RainRules.HoursPassed(0.5f, 23.5f), Is.EqualTo(1f).Within(1e-4f));
		Assert.That(RainRules.HoursPassed(1f, 22f), Is.EqualTo(3f).Within(1e-4f));
	}

	[TestCase(12.01f, 0f)]
	[TestCase(18f, 0f)]
	[TestCase(23.9f, 0f)]
	public void HoursPassedOverTwelveIsDropped(float clock, float last)
	{
		Assert.That(RainRules.HoursPassed(clock, last), Is.EqualTo(0f));
	}

	[TestCase(9.9f, 10f)]
	[TestCase(0f, 6f)]
	public void HoursPassedBackwardsJumpIsDropped(float clock, float last)
	{
		// Wraps to 24 - x, which is over 12 for a small step back.
		Assert.That(RainRules.HoursPassed(clock, last), Is.EqualTo(0f));
	}

	[Test]
	public void HoursPassedBackwardsOverTwelveReadsAsForward()
	{
		// A jump back of 10 hours looks like 14 forward: dropped; a jump back of 13 looks like 11 forward: counted (the
		// game's clock never runs backwards, so only a set clock does this).
		Assert.That(RainRules.HoursPassed(1f, 11f), Is.EqualTo(0f));
		Assert.That(RainRules.HoursPassed(1f, 14f), Is.EqualTo(11f).Within(1e-4f));
	}

	[Test]
	public void HoursPassedNaNClockIsZero()
	{
		Assert.That(RainRules.HoursPassed(float.NaN, 5f), Is.EqualTo(0f));
	}

	[TestCase(5f, 24f, 5f)]
	[TestCase(-1f, 24f, 23f)]
	[TestCase(24f, 24f, 0f)]
	[TestCase(49f, 24f, 1f)]
	public void RepeatIsUnitysRepeat(float t, float length, float expected)
	{
		Assert.That(RainRules.Repeat(t, length), Is.EqualTo(expected).Within(1e-4f));
	}

	[TestCase(3f, 3f, 63f)]
	[TestCase(1f, 3f, 15f)]
	[TestCase(0.5f, 3f, 3f)]
	public void WaitMakesTheCycleAverageTheDays(float days, float rainHours, float expected)
	{
		Assert.That(RainRules.WaitHours(days, rainHours), Is.EqualTo(expected).Within(1e-4f));
	}

	[Test]
	public void WaitIsAtLeastOneHour()
	{
		Assert.That(RainRules.WaitHours(0.1f, 3f), Is.EqualTo(1f));
		Assert.That(RainRules.WaitHours(0.25f, 24f), Is.EqualTo(1f));
	}

	[Test]
	public void StartsIsTheChanceOfOneOverTheWait()
	{
		Assert.That(RainRules.Starts(0.0, 10f), Is.True);
		Assert.That(RainRules.Starts(0.0999, 10f), Is.True);
		Assert.That(RainRules.Starts(0.1, 10f), Is.False);
		Assert.That(RainRules.Starts(0.99, 10f), Is.False);
	}

	[Test]
	public void StartsWithWaitOneAlwaysStarts()
	{
		Assert.That(RainRules.Starts(0.9999999, 1f), Is.True);
	}

	[TestCase(3f, 0.0, 1.8f)]
	[TestCase(3f, 0.5, 3f)]
	[TestCase(3f, 1.0, 4.2f)]
	[TestCase(10f, 0.25, 8f)]
	public void RainLastsSixtyToOneHundredFortyPercent(float average, double random, float expected)
	{
		Assert.That(RainRules.RainHoursFor(average, random), Is.EqualTo(expected).Within(1e-4f));
	}

	[TestCase(0f, 1, false)]
	[TestCase(-1f, 1, false)]
	[TestCase(3f, 0, false)]
	[TestCase(3f, 1, true)]
	[TestCase(3f, 2, true)]
	[TestCase(0.01f, 2, true)]
	public void RollsOnlyWithDaysAndAWorldWithWeather(float days, int fall, bool expected)
	{
		Assert.That(RainRules.Rolls(days, (WeatherKind)fall), Is.EqualTo(expected));
	}

	[TestCase(1, 0f, 0.05f)]
	[TestCase(1, -5f, 0.05f)]
	[TestCase(1, 0.04f, 0.05f)]
	[TestCase(2, 4f, 4f)]
	[TestCase(0, 4f, 0f)]
	[TestCase(0, -1f, 0f)]
	public void BeginHours(int kind, float hours, float expected)
	{
		Assert.That(RainRules.BeginHours((WeatherKind)kind, hours), Is.EqualTo(expected));
	}

	[TestCase(0, 0, 0, 1)]
	[TestCase(19, 0, 0, 1)]
	[TestCase(20, 0, 0, 0)]
	[TestCase(600, 0, 0, 0)]
	[TestCase(0, 600, 0, 2)]
	[TestCase(301, 0, 299, 0)]
	[TestCase(300, 0, 300, 1)]
	[TestCase(300, 301, 0, 2)]
	[TestCase(300, 300, 0, 1)]
	[TestCase(100, 100, 100, 1)]
	[TestCase(20, 0, 0, 0)]
	public void DetectFallFromTheCounts(int sand, int snow, int other, int expected)
	{
		Assert.That((int)RainRules.DetectFall(sand, snow, other), Is.EqualTo(expected));
	}

	[Test]
	public void DetectFallTooFewColumnsIsRainEvenInADesert()
	{
		Assert.That(RainRules.DetectFall(19, 0, 0), Is.EqualTo(WeatherKind.Rain));
		Assert.That(RainRules.DetectFall(0, 19, 0), Is.EqualTo(WeatherKind.Rain));
		Assert.That(RainRules.DetectFall(20, 0, 0), Is.EqualTo(WeatherKind.None));
	}

	[Test]
	public void DetectFallSandThatIsHalfIsNotADesert()
	{
		Assert.That(RainRules.DetectFall(50, 0, 50), Is.EqualTo(WeatherKind.Rain));
		Assert.That(RainRules.DetectFall(51, 0, 49), Is.EqualTo(WeatherKind.None));
		Assert.That(RainRules.DetectFall(0, 50, 50), Is.EqualTo(WeatherKind.Rain));
		Assert.That(RainRules.DetectFall(0, 51, 49), Is.EqualTo(WeatherKind.Snow));
	}

	[Test]
	public void SamplingStopsWhenEnoughColumnsHaveGroundOrAfterTenTimesTheLooks()
	{
		Assert.That(RainRules.KeepSampling(0, 0), Is.True);
		Assert.That(RainRules.KeepSampling(5999, 599), Is.True);
		Assert.That(RainRules.KeepSampling(100, 600), Is.False);
		Assert.That(RainRules.KeepSampling(6000, 10), Is.False);
	}

	[Test]
	public void SampleSeedIsStableAndMatchesTheOriginalFormula()
	{
		Assert.That(RainRules.SampleSeed(256, 128), Is.EqualTo(256 * 31 + 128));
		Assert.That(RainRules.SampleSeed(0, 0), Is.EqualTo(0));
		Assert.DoesNotThrow(() => RainRules.SampleSeed(int.MaxValue, int.MaxValue));
	}

	[Test]
	public void BoostOnlyWhileItRains()
	{
		Assert.That(RainRules.Boost(WeatherKind.Rain, 3f), Is.EqualTo(3f));
		Assert.That(RainRules.Boost(WeatherKind.Snow, 3f), Is.EqualTo(1f));
		Assert.That(RainRules.Boost(WeatherKind.None, 3f), Is.EqualTo(1f));
		Assert.That(RainRules.Boost(WeatherKind.Rain, null), Is.EqualTo(1f));
	}
}
