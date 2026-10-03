using System.Diagnostics;

namespace CastleStoryPlus.Loading;

// Time slice for loading coroutines: do work until the budget is spent, then yield a frame.
// Keeps the loading screen drawing and the window responsive regardless of how heavy each item is.
internal class FrameBudget
{
	public const long DefaultMilliseconds = 30;

	private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

	private readonly long _milliseconds;

	private int _calls;

	public FrameBudget(long milliseconds = DefaultMilliseconds)
	{
		_milliseconds = milliseconds;
	}

	// Checks the clock every 64 calls only, so it is cheap enough for tight loops.
	public bool Exceeded
	{
		get
		{
			_calls++;
			if ((_calls & 63) != 0)
			{
				return false;
			}
			return _stopwatch.ElapsedMilliseconds >= _milliseconds;
		}
	}

	public bool ExceededNow => _stopwatch.ElapsedMilliseconds >= _milliseconds;

	public void Reset()
	{
		_stopwatch.Reset();
		_stopwatch.Start();
		_calls = 0;
	}
}
