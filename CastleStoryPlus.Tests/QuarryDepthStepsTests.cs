using CastleStoryPlus.Economy;
using NUnit.Framework;

namespace CastleStoryPlus.Tests;

[TestFixture]
public class QuarryDepthStepsTests
{
	[TestCase(1, 1, 2)]
	[TestCase(9, 1, 10)]
	[TestCase(10, 1, 15)]
	[TestCase(15, 1, 20)]
	[TestCase(15, -1, 10)]
	[TestCase(10, -1, 9)]
	[TestCase(2, -1, 1)]
	public void StepsByOneUpToTenThenByFive(int depth, int direction, int expected)
	{
		Assert.That(QuarryDepthSteps.Next(depth, direction, 48), Is.EqualTo(expected));
	}

	[Test]
	public void NeverUnderOne()
	{
		Assert.That(QuarryDepthSteps.Next(1, -1, 48), Is.EqualTo(1));
	}

	[TestCase(45, 48)]
	[TestCase(48, 48)]
	public void NeverOverTheMaximum(int depth, int expected)
	{
		Assert.That(QuarryDepthSteps.Next(depth, 1, 48), Is.EqualTo(expected));
	}
}
