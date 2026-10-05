using CastleStoryPlus.Updates;
using NUnit.Framework;

namespace CastleStoryPlus.Tests;

[TestFixture]
public class UpdateCheckTests
{
	[TestCase("v0.3.0", "0.2.0", true)]
	[TestCase("v0.2.1", "0.2.0", true)]
	[TestCase("v0.2.0", "0.2.0", false)]
	[TestCase("v0.1.9", "0.2.0", false)]
	[TestCase("not-a-version", "0.2.0", false)]
	public void IsNewerComparesVersions(string tag, string current, bool expected)
	{
		Assert.That(UpdateCheck.IsNewer(tag, current), Is.EqualTo(expected));
	}
}
