using CastleStoryPlus.UI;
using NUnit.Framework;

namespace CastleStoryPlus.Tests;

[TestFixture]
public class WorldSearchQueryTests
{
	[Test]
	public void SplitsAtCommasTrimsAndLowerCases()
	{
		Assert.That(WorldSearchQuery.Terms(" Wood , ARCHER,,  "), Is.EqualTo(new[] { "wood", "archer" }));
	}

	[TestCase(null)]
	[TestCase("")]
	[TestCase(" , ,")]
	public void EmptyTextHasNoTerms(string text)
	{
		Assert.That(WorldSearchQuery.Terms(text), Is.Empty);
	}

	[Test]
	public void MatchesWhenAnyTermIsInTheText()
	{
		string[] terms = WorldSearchQuery.Terms("stone, archer");
		Assert.That(WorldSearchQuery.Matches("bob archer knight bricktron", terms), Is.True);
		Assert.That(WorldSearchQuery.Matches("wood log", terms), Is.False);
	}
}
