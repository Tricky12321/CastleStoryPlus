using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace CastleStoryPlus.DevTools.Testing;

// What a running test gets: assertions (a failed one ends the test), its own log lines, waiting over frames, and
// clean-up steps that always run afterwards, also when the test failed (remove placed blocks, ...).
internal class TestContext
{
	internal readonly List<string> Lines = new List<string>();

	internal readonly List<Action> Cleanups = new List<Action>();

	internal readonly Dictionary<string, object> Data = new Dictionary<string, object>();

	internal float Deadline;

	public void Log(string message)
	{
		Lines.Add(Time.realtimeSinceStartup.ToString("0.00", CultureInfo.InvariantCulture) + "  " + message);
	}

	// A value kept with the test's result (route lengths, timings, ...).
	public void Record(string name, object value)
	{
		Data[name] = value;
	}

	public void Cleanup(Action action)
	{
		Cleanups.Add(action);
	}

	public void Fail(string message)
	{
		throw new TestFailure(message);
	}

	public void Skip(string reason)
	{
		throw new TestSkipped(reason);
	}

	public void True(bool condition, string message)
	{
		if (!condition)
		{
			Fail(message);
		}
	}

	public void NotNull(object value, string what)
	{
		if (value == null || (value is UnityEngine.Object unityObject && unityObject == null))
		{
			Fail(what + " is missing");
		}
	}

	public void Equal<T>(T expected, T actual, string what)
	{
		if (!EqualityComparer<T>.Default.Equals(expected, actual))
		{
			Fail(what + ": expected " + expected + ", was " + actual);
		}
	}

	public void AtMost(float limit, float actual, string what)
	{
		if (actual > limit)
		{
			Fail(what + ": " + actual.ToString("0.###", CultureInfo.InvariantCulture) + " is over the limit " + limit.ToString("0.###", CultureInfo.InvariantCulture));
		}
	}

	public void AtLeast(float limit, float actual, string what)
	{
		if (actual < limit)
		{
			Fail(what + ": " + actual.ToString("0.###", CultureInfo.InvariantCulture) + " is under the minimum " + limit.ToString("0.###", CultureInfo.InvariantCulture));
		}
	}

	// yield return t.Seconds(2f): waits in game time.
	public IEnumerator Seconds(float seconds)
	{
		float end = Time.time + seconds;
		while (Time.time < end)
		{
			yield return null;
		}
	}

	public IEnumerator Frames(int frames)
	{
		for (int i = 0; i < frames; i++)
		{
			yield return null;
		}
	}

	// yield return t.Until(() => ..., 10f, "the bricktron to arrive"): fails the test when it takes longer (real
	// seconds).
	public IEnumerator Until(Func<bool> condition, float timeoutSeconds, string waitingFor)
	{
		float end = Time.realtimeSinceStartup + timeoutSeconds;
		while (!condition())
		{
			if (Time.realtimeSinceStartup > end)
			{
				Fail("Waited " + timeoutSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " s for " + waitingFor);
			}
			yield return null;
		}
	}
}

internal class TestFailure : Exception
{
	public TestFailure(string message) : base(message)
	{
	}
}

internal class TestSkipped : Exception
{
	public TestSkipped(string message) : base(message)
	{
	}
}
