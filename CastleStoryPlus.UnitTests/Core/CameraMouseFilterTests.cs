using CastleStoryPlus.Core;
using NUnit.Framework;

namespace CastleStoryPlus.UnitTests.Core;

[TestFixture]
public class CameraMouseFilterTests
{
	private const float W = 1920f;
	private const float H = 1080f;

	private static CameraMouseFilter Armed()
	{
		CameraMouseFilter f = new CameraMouseFilter();
		f.Tick(true, false, 100f, 100f, W, H);
		f.Tick(true, false, 110f, 100f, W, H);
		return f;
	}

	[Test]
	public void FirstFrameAfterLoadIgnoresDelta()
	{
		CameraMouseFilter f = new CameraMouseFilter();
		f.Tick(true, false, 500f, 500f, W, H);
		Assert.That(f.Armed, Is.False);
		Assert.That(f.Delta(80f), Is.EqualTo(0f));
	}

	[Test]
	public void StationaryCursorStaysIgnored()
	{
		CameraMouseFilter f = new CameraMouseFilter();
		f.Tick(true, false, 500f, 500f, W, H);
		f.Tick(true, false, 500f, 500f, W, H);
		Assert.That(f.Delta(50f), Is.EqualTo(0f));
	}

	[Test]
	public void CursorMovingInsideArms()
	{
		CameraMouseFilter f = Armed();
		Assert.That(f.Armed, Is.True);
		Assert.That(f.Delta(12f), Is.EqualTo(12f));
		Assert.That(f.Delta(-3f), Is.EqualTo(-3f));
	}

	[Test]
	public void NoFocusIgnoresAndDisarms()
	{
		CameraMouseFilter f = Armed();
		f.Tick(false, false, 200f, 200f, W, H);
		Assert.That(f.Armed, Is.False);
		Assert.That(f.Delta(10f), Is.EqualTo(0f));
	}

	[Test]
	public void FocusRegainNeedsNewMovement()
	{
		CameraMouseFilter f = Armed();
		f.Tick(false, false, 200f, 200f, W, H);
		f.Tick(true, false, 800f, 600f, W, H);
		Assert.That(f.Delta(900f), Is.EqualTo(0f));
		f.Tick(true, false, 810f, 600f, W, H);
		Assert.That(f.Delta(5f), Is.EqualTo(5f));
	}

	[TestCase(-1f, 100f)]
	[TestCase(100f, -5f)]
	[TestCase(1921f, 100f)]
	[TestCase(100f, 1081f)]
	public void OutsideWindowIgnored(float x, float y)
	{
		CameraMouseFilter f = Armed();
		f.Tick(true, false, x, y, W, H);
		if (x < 0f || y < 0f || x > W || y > H)
		{
			Assert.That(f.Delta(5f), Is.EqualTo(0f));
		}
	}

	[Test]
	public void CursorReEnteringNeedsNewMovement()
	{
		CameraMouseFilter f = Armed();
		f.Tick(true, false, -20f, 300f, W, H);
		Assert.That(f.Armed, Is.False);
		f.Tick(true, false, 5f, 300f, W, H);
		Assert.That(f.Delta(200f), Is.EqualTo(0f));
		f.Tick(true, false, 15f, 300f, W, H);
		Assert.That(f.Delta(8f), Is.EqualTo(8f));
	}

	[Test]
	public void ResetOnLoadDisarms()
	{
		CameraMouseFilter f = Armed();
		f.Reset();
		f.Tick(true, false, 110f, 100f, W, H);
		Assert.That(f.Delta(30f), Is.EqualTo(0f));
	}

	[TestCase(5000f, 100f)]
	[TestCase(-5000f, -100f)]
	[TestCase(100f, 100f)]
	public void SpikeIsClamped(float raw, float expected)
	{
		Assert.That(Armed().Delta(raw), Is.EqualTo(expected));
	}

	[Test]
	public void NaNIsZero()
	{
		Assert.That(Armed().Delta(float.NaN), Is.EqualTo(0f));
	}

	[Test]
	public void LockedCursorTrustedFromSecondFrame()
	{
		CameraMouseFilter f = new CameraMouseFilter();
		f.Tick(true, true, 960f, 540f, W, H);
		Assert.That(f.Delta(10f), Is.EqualTo(0f));
		f.Tick(true, true, 960f, 540f, W, H);
		Assert.That(f.Delta(10f), Is.EqualTo(10f));
	}

	[Test]
	public void LockedCursorStillNeedsFocus()
	{
		CameraMouseFilter f = new CameraMouseFilter();
		f.Tick(false, true, 960f, 540f, W, H);
		f.Tick(false, true, 960f, 540f, W, H);
		Assert.That(f.Delta(10f), Is.EqualTo(0f));
	}

	[Test]
	public void DragAcrossWindowEdgeStaysArmed()
	{
		CameraMouseFilter f = Armed();
		f.Tick(true, false, -50f, 300f, W, H, true);
		Assert.That(f.Armed, Is.True);
		Assert.That(f.Delta(7f), Is.EqualTo(7f));
	}

	[Test]
	public void DragOutsideDoesNotArmAFreshFilter()
	{
		CameraMouseFilter f = new CameraMouseFilter();
		f.Tick(true, false, -50f, 300f, W, H, true);
		Assert.That(f.Armed, Is.False);
	}

	[Test]
	public void ReleasingOutsideDisarms()
	{
		CameraMouseFilter f = Armed();
		f.Tick(true, false, -50f, 300f, W, H, true);
		f.Tick(true, false, -60f, 300f, W, H, false);
		Assert.That(f.Armed, Is.False);
	}

	// The edge nudge (Armed decides whether the game's nudge runs): cursor at (0,0) right after load must not scroll.
	[Test]
	public void NudgeNotAllowedWithCursorStuckAtOriginAfterLoad()
	{
		CameraMouseFilter f = new CameraMouseFilter();
		for (int i = 0; i < 10; i++)
		{
			f.Tick(true, false, 0f, 0f, W, H);
			Assert.That(f.Armed, Is.False);
		}
	}

	[Test]
	public void NudgeAllowedOnceCursorMovesToTheEdge()
	{
		CameraMouseFilter f = new CameraMouseFilter();
		f.Tick(true, false, 300f, 300f, W, H);
		f.Tick(true, false, 2f, 300f, W, H);
		Assert.That(f.Armed, Is.True);
	}

	[Test]
	public void NudgeNotAllowedWhenUnfocusedEvenAtEdge()
	{
		CameraMouseFilter f = Armed();
		f.Tick(false, false, 2f, 300f, W, H);
		Assert.That(f.Armed, Is.False);
	}
}
