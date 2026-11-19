using System;

namespace CastleStoryPlus.Core;

// Decides when the camera may use the mouse's movement (drag-pan, drag-rotate, the mouse look), as pure logic (no
// Unity, no game): CameraMouseGuard reads the window and the mouse and calls this. Unit tests:
// CastleStoryPlus.UnitTests/Core.
//
// Right after a level loads, when the window regains focus and when the cursor comes in from outside the window, the
// mouse's frame delta can be garbage (a whole jump from the last known position, or from (0,0)), and the camera
// scrolls far off by itself. So the mouse is ignored until the window has focus, the cursor is inside it and the
// cursor has really moved since the last reset; after that the game's delta is used, clamped to a sane size.
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
	internal void Tick(bool focused, bool cursorLocked, float x, float y, float width, float height)
	{
		if (!focused || (!cursorLocked && !IsInside(x, y, width, height)))
		{
			Reset();
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
