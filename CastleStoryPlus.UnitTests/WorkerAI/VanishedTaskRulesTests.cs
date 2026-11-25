using CastleStoryPlus.WorkerAI;
using NUnit.Framework;

namespace CastleStoryPlus.UnitTests.WorkerAI;

// Issue #199: a bricktron put on a task the moment the task ended froze for good.
[TestFixture]
public class VanishedTaskRulesTests
{
	[Test]
	public void ALiveTaskTakesABricktron()
	{
		Assert.That(VanishedTaskRules.MayJoin(false, false), Is.True);
	}

	[Test]
	public void ATaskThatIsGoneTakesNoOne()
	{
		Assert.That(VanishedTaskRules.MayJoin(true, false), Is.False);
		Assert.That(VanishedTaskRules.MayJoin(false, true), Is.False, "A task whose crew is already gone (the task is being sent to the pool)");
		Assert.That(VanishedTaskRules.MayJoin(true, true), Is.False);
	}

	[Test]
	public void ABricktronInATaskThatIsGoneIsStranded()
	{
		Assert.That(VanishedTaskRules.Stranded(true, false, true), Is.True);
	}

	[Test]
	public void OnlyALiveBricktronInATaskThatIsGoneIsStranded()
	{
		Assert.That(VanishedTaskRules.Stranded(true, false, false), Is.False, "A bricktron in a live task");
		Assert.That(VanishedTaskRules.Stranded(false, false, true), Is.False, "A bricktron that is itself in the pool");
		Assert.That(VanishedTaskRules.Stranded(true, true, true), Is.False, "A phased out bricktron is out of the world on purpose");
	}

	[Test]
	public void AnUnstartedTaskFromATaskThatIsGoneIsLetGoAtTheSecondLook()
	{
		Assert.That(VanishedTaskRules.LetGoUnstarted(false, true, false), Is.False, "The first look only notes it");
		Assert.That(VanishedTaskRules.LetGoUnstarted(false, true, true), Is.True);
	}

	[Test]
	public void AStartedTaskOrALiveAreaIsLeftAlone()
	{
		Assert.That(VanishedTaskRules.LetGoUnstarted(true, true, true), Is.False, "A started task listens for being cancelled");
		Assert.That(VanishedTaskRules.LetGoUnstarted(false, false, true), Is.False, "Its task is still there");
	}

	[Test]
	public void TheCheckRunsEveryFewSeconds()
	{
		Assert.That(VanishedTaskRules.CheckSeconds, Is.GreaterThan(0f).And.LessThanOrEqualTo(5f));
	}
}
