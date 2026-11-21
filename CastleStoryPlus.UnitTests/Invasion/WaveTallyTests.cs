using CastleStoryPlus.Invasion;
using NUnit.Framework;

namespace CastleStoryPlus.UnitTests.Invasion;

[TestFixture]
public class WaveTallyTests
{
	// Kinds in the scoreboard's order.
	private const int Minitron = 0;

	private const int Corruptron = 1;

	[Test]
	public void AWaveWithoutSummonsHasHadTheMostEverLeft()
	{
		WaveTally tally = new WaveTally();
		Assert.That(tally.Seen(3, Corruptron, 5), Is.EqualTo(5));
		Assert.That(tally.Seen(3, Corruptron, 2), Is.EqualTo(5));
		Assert.That(tally.Seen(3, Corruptron, 0), Is.EqualTo(5));
	}

	[Test]
	public void SummonsComeOnTopOfTheWavesOwn()
	{
		// Three corruptrons, two of them killed, then a warlock summons one: four came, two are left.
		WaveTally tally = new WaveTally();
		Assert.That(tally.Seen(4, Corruptron, 3), Is.EqualTo(3));
		Assert.That(tally.Seen(4, Corruptron, 1), Is.EqualTo(3));
		tally.Summoned(4, Corruptron);
		Assert.That(tally.Seen(4, Corruptron, 2), Is.EqualTo(4));
	}

	[Test]
	public void SummonsWhileTheWaveIsWholeAreNotCountedTwice()
	{
		// Two minitrons of its own and two summoned, all alive: four, not six.
		WaveTally tally = new WaveTally();
		tally.Seen(2, Minitron, 2);
		tally.Summoned(2, Minitron);
		tally.Summoned(2, Minitron);
		Assert.That(tally.Seen(2, Minitron, 4), Is.EqualTo(4));
	}

	[Test]
	public void DeadSummonsStayCounted()
	{
		WaveTally tally = new WaveTally();
		tally.Seen(5, Minitron, 1);
		tally.Summoned(5, Minitron);
		tally.Summoned(5, Minitron);
		Assert.That(tally.Seen(5, Minitron, 3), Is.EqualTo(3));
		// The wave's own minitron and one summon dead: one left of three.
		Assert.That(tally.Seen(5, Minitron, 1), Is.EqualTo(3));
		// Only summons left, the wave's own all dead.
		Assert.That(tally.Seen(5, Minitron, 0), Is.EqualTo(3));
	}

	[Test]
	public void WavesAndKindsAreCountedApart()
	{
		WaveTally tally = new WaveTally();
		tally.Summoned(1, Corruptron);
		Assert.That(tally.SummonedOf(1, Corruptron), Is.EqualTo(1));
		Assert.That(tally.SummonedOf(1, Minitron), Is.EqualTo(0));
		Assert.That(tally.SummonedOf(2, Corruptron), Is.EqualTo(0));
		Assert.That(tally.Seen(2, Corruptron, 2), Is.EqualTo(2));
	}

	[Test]
	public void SummonsOfNoWaveOrUnknownKindAreIgnored()
	{
		WaveTally tally = new WaveTally();
		tally.Summoned(0, Corruptron);
		tally.Summoned(3, -1);
		tally.Summoned(3, WaveTally.KindCount);
		Assert.That(tally.SummonedOf(0, Corruptron), Is.EqualTo(0));
		Assert.That(tally.Seen(3, -1, 2), Is.EqualTo(2));
		Assert.That(tally.Seen(3, WaveTally.KindCount, 0), Is.EqualTo(0));
	}

	[Test]
	public void ANewGameStartsEmpty()
	{
		WaveTally tally = new WaveTally();
		tally.Seen(1, Corruptron, 6);
		tally.Summoned(1, Corruptron);
		tally.Clear();
		Assert.That(tally.SummonedOf(1, Corruptron), Is.EqualTo(0));
		Assert.That(tally.Seen(1, Corruptron, 2), Is.EqualTo(2));
	}
}
