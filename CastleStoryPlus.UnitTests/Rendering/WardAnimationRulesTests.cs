using CastleStoryPlus.Rendering;
using NUnit.Framework;

namespace CastleStoryPlus.UnitTests.Rendering;

// The ward animation switches (#200, point 3): each switch off gives the game's own value.
[TestFixture]
public class WardAnimationRulesTests
{
	[Test]
	public void HealRateIsAQuarterWhenFewer()
	{
		Assert.That(WardAnimationRules.HealRate(20f, true), Is.EqualTo(5f).Within(0.0001f));
		Assert.That(WardAnimationRules.HealRate(20f, false), Is.EqualTo(20f));
	}

	[Test]
	public void HealMaxKeepsAtLeastOneParticle()
	{
		Assert.That(WardAnimationRules.HealMax(100, true), Is.EqualTo(25));
		Assert.That(WardAnimationRules.HealMax(3, true), Is.EqualTo(1));
		Assert.That(WardAnimationRules.HealMax(1, true), Is.EqualTo(1));
		Assert.That(WardAnimationRules.HealMax(0, true), Is.EqualTo(0));
		Assert.That(WardAnimationRules.HealMax(100, false), Is.EqualTo(100));
	}

	[Test]
	public void DefenseSpeedNeverAboveStandbyWhenStill()
	{
		Assert.That(WardAnimationRules.DefenseSpeed(0.5f, 0.3f, true), Is.EqualTo(0.3f));
		Assert.That(WardAnimationRules.DefenseSpeed(0f, 0.3f, true), Is.EqualTo(0f));
		Assert.That(WardAnimationRules.DefenseSpeed(0.5f, 0.3f, false), Is.EqualTo(0.5f));
	}

	[Test]
	public void LinkAmplitudeIsZeroWhenStill()
	{
		Assert.That(WardAnimationRules.LinkAmplitude(0.2f, true), Is.EqualTo(0f));
		Assert.That(WardAnimationRules.LinkAmplitude(0.2f, false), Is.EqualTo(0.2f));
	}
}
