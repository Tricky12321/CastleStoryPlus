using System;

namespace CastleStoryPlus.Core;

// Decides when the camera may react to the mouse, as pure logic (no Unity, no game): CameraMouseGuard reads the window
// and the mouse and calls this. Unit tests: CastleStoryPlus.UnitTests/Core.
//
// Two uses: (1) the game's edge nudge (Curseur.CheckToNudgeScreen scrolls the camera while the cursor is within 5 px
// of a window edge): right after a level load the cursor position can still be (0,0) or outside the window, and the
// camera scrolled off by itself; (2) the mouse's frame delta (drag-pan, drag-rotate), which can be a huge jump then.
// Both are ignored until the window has focus, the cursor is inside it and the cursor has really moved since the
// last reset (level load, focus loss, cursor left the window). The delta is also clamped to a sane size.
internal class CameraMouseFilter
{
	// The largest per-frame delta (per axis) let through; anything larger is a spike.
	internal const float MaxDelta = 100f;

	private bool _hasBaseline;
	private bool _armed;
	private float _baseX;
	private float _baseY;

	// True when the camera may use the mouse's movement this frame.
	internal bool Armed => _armed;

	// Forget the mouse: after a level load. The next Tick takes the cursor's position as the new baseline.
	internal void Reset()
	{
		_hasBaseline = false;
		_armed = false;
	}

	// Once per frame, before the camera reads the mouse. With a locked cursor the position does not move, so the
	// movement is trusted from the second frame on.
	// A mouse button held keeps an armed filter armed when the cursor leaves the window (a drag across monitors).
	internal void Tick(bool focused, bool cursorLocked, float x, float y, float width, float height, bool buttonHeld = false)
	{
		if (!focused)
		{
			Reset();
			return;
		}
		if (!cursorLocked && !IsInside(x, y, width, height))
		{
			if (!(_armed && buttonHeld))
			{
				Reset();
			}
			return;
		}
		if (!_hasBaseline)
		{
			_hasBaseline = true;
			_baseX = x;
			_baseY = y;
			return;
		}
		if (!_armed && (cursorLocked || Math.Abs(x - _baseX) > 0.5f || Math.Abs(y - _baseY) > 0.5f))
		{
			_armed = true;
		}
	}

	// The delta the camera gets for one axis: 0 while not armed, otherwise the raw delta clamped to MaxDelta.
	internal float Delta(float raw)
	{
		if (!_armed || float.IsNaN(raw))
		{
			return 0f;
		}
		return Math.Max(-MaxDelta, Math.Min(MaxDelta, raw));
	}

	internal static bool IsInside(float x, float y, float width, float height)
	{
		return width > 0f && height > 0f && x >= 0f && y >= 0f && x <= width && y <= height;
	}
}
