using System;
using System.Collections;
using System.Threading;

namespace CastleStoryPlus.Loading;

// Runs pure data work (file IO, JSON parsing) on a worker thread so the main thread keeps rendering.
// The work must not touch UnityEngine.Object APIs. Wait from a coroutine with "yield return task.Wait();".
internal class BackgroundTask<T>
{
	private readonly Func<T> _work;

	private volatile bool _done;

	private T _result;

	private Exception _error;

	public bool IsDone => _done;

	private BackgroundTask(Func<T> work)
	{
		_work = work;
	}

	public static BackgroundTask<T> Start(Func<T> work)
	{
		BackgroundTask<T> task = new BackgroundTask<T>(work);
		Thread thread = new Thread(task.Run);
		thread.IsBackground = true;
		thread.Start();
		return task;
	}

	private void Run()
	{
		try
		{
			_result = _work();
		}
		catch (Exception ex)
		{
			_error = ex;
		}
		finally
		{
			_done = true;
		}
	}

	public IEnumerator Wait()
	{
		while (!_done)
		{
			yield return null;
		}
	}

	// Rethrows on the calling (main) thread if the work failed, so errors surface where they did before.
	public T Result
	{
		get
		{
			if (!_done)
			{
				throw new InvalidOperationException("Background task has not finished.");
			}
			if (_error != null)
			{
				throw new Exception("Background task failed: " + _error.Message, _error);
			}
			return _result;
		}
	}
}
