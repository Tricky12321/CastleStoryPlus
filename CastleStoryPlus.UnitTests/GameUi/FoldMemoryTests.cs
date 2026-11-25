using CastleStoryPlus.GameUi;
using NUnit.Framework;

namespace CastleStoryPlus.UnitTests.GameUi;

// The task popup's minimized state, kept per task (#198).
[TestFixture]
public class FoldMemoryTests
{
	private sealed class Task
	{
	}

	[Test]
	public void EverythingStartsOpen()
	{
		FoldMemory<object> memory = new FoldMemory<object>();
		Assert.That(memory.IsFolded(new Task()), Is.False);
		Assert.That(memory.IsFolded(null), Is.False);
	}

	[Test]
	public void FoldingOneTaskLeavesTheOthersOpen()
	{
		FoldMemory<object> memory = new FoldMemory<object>();
		Task crystal = new Task();
		Task build = new Task();
		memory.Set(crystal, true);
		Assert.That(memory.IsFolded(crystal), Is.True);
		Assert.That(memory.IsFolded(build), Is.False);
		// Back to the crystal after another task: still folded.
		Assert.That(memory.IsFolded(crystal), Is.True);
	}

	[Test]
	public void ToggleFlipsAndReturnsTheNewState()
	{
		FoldMemory<object> memory = new FoldMemory<object>();
		Task task = new Task();
		Assert.That(memory.Toggle(task), Is.True);
		Assert.That(memory.IsFolded(task), Is.True);
		Assert.That(memory.Toggle(task), Is.False);
		Assert.That(memory.IsFolded(task), Is.False);
		Assert.That(memory.Count, Is.EqualTo(0));
	}

	[Test]
	public void NothingSelectedIsNeverFolded()
	{
		FoldMemory<object> memory = new FoldMemory<object>();
		memory.Set(null, true);
		Assert.That(memory.Toggle(null), Is.False);
		Assert.That(memory.Count, Is.EqualTo(0));
	}

	[Test]
	public void ClearOpensEverything()
	{
		FoldMemory<object> memory = new FoldMemory<object>();
		Task a = new Task();
		Task b = new Task();
		memory.Set(a, true);
		memory.Set(b, true);
		memory.Clear();
		Assert.That(memory.IsFolded(a), Is.False);
		Assert.That(memory.IsFolded(b), Is.False);
	}
}
