using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Brix.Engine;
using Brix.Transactions;
using CastleStoryPlus.DevTools.Speed;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.DevTools.Testing;

// Runs the in-game tests ([GameTest] methods) one after the other in the running game, each as a coroutine with a
// timeout; its clean-up steps always run. One run at a time; the last run's results stay until the next.
internal class TestRunner : MonoBehaviour
{
	internal enum Outcome
	{
		Pending,
		Running,
		Passed,
		Failed,
		Skipped,
		Error
	}

	internal sealed class TestInfo
	{
		public string Name;

		public string Category;

		public string Description;

		public float TimeoutSeconds;

		public bool NeedsHostGame;

		// May run next to other tests ([ParallelTests] on its class and not Exclusive).
		public bool Parallel;

		// Tests of one group never run at the same time.
		public string Group;

		public MethodInfo Method;
	}

	internal sealed class Result
	{
		public string Name;

		public string Category;

		public Outcome Outcome;

		public string Message;

		public double Seconds;

		public List<string> Log;

		public Dictionary<string, object> Data;

		// Skipped for want of free ground (TestNoRoom): run again once the others are done.
		public bool NoRoom;

		public int Attempts;
	}

	internal static TestRunner Instance;

	internal static readonly List<TestInfo> Tests = new List<TestInfo>();

	internal readonly List<Result> Results = new List<Result>();

	internal bool Running;

	internal int RunNumber;

	internal DateTime StartedAt;

	internal DateTime FinishedAt;

	private bool _stopAsked;

	// Game speed of the run (1: as it is). Set when the run starts and back to 1x when it ends; the tests' waits
	// (t.Seconds) count game time, their timeouts real time, so a faster game finishes them sooner.
	private float _speed = 1f;

	// How many tests may run at the same time (1: one after the other).
	private int _parallel = 1;

	// Take the world's own units away before the run (the player's bricktrons, roaming enemies), so the tests only
	// meet their own.
	private bool _clearUnits;

	private void Awake()
	{
		Instance = this;
		Discover();
	}

	private static void Discover()
	{
		Tests.Clear();
		foreach (Type type in typeof(TestRunner).Assembly.GetTypes())
		{
			foreach (MethodInfo method in type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
			{
				GameTestAttribute attribute = (GameTestAttribute)Attribute.GetCustomAttribute(method, typeof(GameTestAttribute));
				if (attribute == null)
				{
					continue;
				}
				ParameterInfo[] parameters = method.GetParameters();
				if (method.ReturnType != typeof(IEnumerator) || parameters.Length != 1 || parameters[0].ParameterType != typeof(TestContext))
				{
					DevToolsPlugin.Log.LogError("Test " + type.Name + "." + method.Name + " must be: static IEnumerator " + method.Name + "(TestContext t)");
					continue;
				}
				ParallelTestsAttribute parallel = (ParallelTestsAttribute)Attribute.GetCustomAttribute(type, typeof(ParallelTestsAttribute));
				Tests.Add(new TestInfo
				{
					Parallel = parallel != null && !attribute.Exclusive,
					Group = (parallel != null) ? parallel.Group : null,
					Name = type.Name + "." + method.Name,
					Category = attribute.Category,
					Description = attribute.Description,
					TimeoutSeconds = attribute.TimeoutSeconds,
					NeedsHostGame = attribute.NeedsHostGame,
					Method = method
				});
			}
		}
		Tests.Sort((TestInfo a, TestInfo b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
		DevToolsPlugin.Log.LogInfo("Tests: " + Tests.Count + " in-game tests found");
	}

	// Tests whose name or category contains the filter (all for an empty one, case does not matter).
	internal static List<TestInfo> Matching(string filter)
	{
		List<TestInfo> result = new List<TestInfo>();
		foreach (TestInfo test in Tests)
		{
			if (string.IsNullOrEmpty(filter)
				|| test.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
				|| test.Category.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
			{
				result.Add(test);
			}
		}
		return result;
	}

	internal static bool InHostGame()
	{
		return Architecte.commence && !Architecte.termine && NetworkServer.active;
	}

	// Main thread. False when a run is already going.
	internal bool StartRun(List<TestInfo> tests, float speed, int parallel = 1, bool clearUnits = false)
	{
		if (Running)
		{
			return false;
		}
		Running = true;
		_stopAsked = false;
		_speed = speed;
		_parallel = Math.Max(1, parallel);
		_clearUnits = clearUnits;
		RunNumber++;
		StartedAt = DateTime.Now;
		Results.Clear();
		foreach (TestInfo test in tests)
		{
			Results.Add(new Result { Name = test.Name, Category = test.Category, Outcome = Outcome.Pending });
		}
		StartCoroutine(RunAll(tests));
		return true;
	}

	internal void Stop()
	{
		_stopAsked = true;
	}

	private IEnumerator RunAll(List<TestInfo> tests)
	{
		DevToolsPlugin.Log.LogInfo("Tests: run " + RunNumber + " started, " + tests.Count + " tests" + ((_speed != 1f) ? (" at " + _speed + "x") : string.Empty));
		bool fast = _speed != 1f && GameSpeed.InGame() && !Neo.multiplayer;
		if (fast)
		{
			GameSpeed.Instance.Set(_speed, 0f);
		}
		bool quieted = false;
		GameObject runBuilder = null;
		if (_clearUnits && InHostGame())
		{
			ClearUnits();
			Scenario.QuietWorld();
			quieted = true;
			// One builder of the run's own for the older tests that use "the player's builder" (Units.Find); the
			// newer ones bring their own. Given a few seconds to settle into the world.
			XYZ home = Scenario.Home();
			runBuilder = Transactor.SpawnUNet(Brix.External.Factories.Test.Bricktron, Scenario.Player, Scenario.Walkable(Scenario.Ground(home.x + 4, home.z + 2)).ToVector3(), Quaternion.identity);
			if (runBuilder != null)
			{
				runBuilder.GetComponent<Brix.Game.AI.Occupation>().SetOccupation(Brix.Game.AI.Occupation.Type.Builder, true);
				// Only what a test orders it to do: an idle builder takes a free workshop or a chore on its own.
				runBuilder.GetComponent<Brix.Game.AI.Labor>().Autonomy = Brix.Game.AI.AutonomyStatus.WaitForOrders;
			}
			yield return new WaitForSeconds(3f);
		}
		// Tests start in their order as soon as they may: no more than _parallel at once, a test that must run alone
		// only when nothing else runs (and nothing starts next to it), never two of one group at once.
		List<int> queue = new List<int>();
		for (int i = 0; i < tests.Count; i++)
		{
			queue.Add(i);
		}
		HashSet<int> running = new HashSet<int>();
		List<int> deferred = new List<int>();
		HashSet<string> busyGroups = new HashSet<string>();
		bool aloneRunning = false;
		while (queue.Count > 0 || running.Count > 0 || deferred.Count > 0)
		{
			if (running.Count == 0 && deferred.Count > 0)
			{
				queue.AddRange(deferred);
				deferred.Clear();
			}
			if (_stopAsked)
			{
				queue.AddRange(deferred);
				deferred.Clear();
				foreach (int i in queue)
				{
					Results[i].Outcome = Outcome.Skipped;
					Results[i].Message = "The run was stopped";
				}
				queue.Clear();
			}
			for (int q = 0; q < queue.Count && running.Count < _parallel && !aloneRunning; q++)
			{
				int index = queue[q];
				TestInfo test = tests[index];
				bool alone = !test.Parallel || _parallel == 1;
				if (alone && running.Count > 0)
				{
					// Waits for the others; nothing after it starts first, so the order holds.
					break;
				}
				if (test.Group != null && busyGroups.Contains(test.Group))
				{
					continue;
				}
				queue.RemoveAt(q--);
				running.Add(index);
				if (test.Group != null)
				{
					busyGroups.Add(test.Group);
				}
				aloneRunning = alone;
				StartCoroutine(RunTracked(test, Results[index], () =>
				{
					running.Remove(index);
					if (test.Group != null)
					{
						busyGroups.Remove(test.Group);
					}
					if (alone)
					{
						aloneRunning = false;
					}
					Result result = Results[index];
					// Out of free ground while others held it: again once nothing else runs.
					if (result.NoRoom && result.Attempts < 3 && (running.Count > 0 || queue.Count > 0))
					{
						result.Outcome = Outcome.Pending;
						result.Message = null;
						result.NoRoom = false;
						deferred.Add(index);
					}
				}));
			}
			yield return null;
		}
		if (quieted)
		{
			Scenario.UnquietWorld();
		}
		if (runBuilder != null)
		{
			Scenario.Release(runBuilder);
		}
		if (fast && GameSpeed.Instance.Active)
		{
			GameSpeed.Instance.Reset("the test run ended");
		}
		Running = false;
		FinishedAt = DateTime.Now;
		int passed = Results.FindAll((Result r) => r.Outcome == Outcome.Passed).Count;
		DevToolsPlugin.Log.LogInfo("Tests: run " + RunNumber + " done, " + passed + "/" + Results.Count + " passed");
	}

	private IEnumerator RunTracked(TestInfo test, Result result, Action done)
	{
		result.Attempts++;
		yield return StartCoroutine(RunOne(test, result));
		done();
	}

	// Takes every unit the world has now out of it: the player's bricktrons and the map's roaming enemies and beasts.
	private static void ClearUnits()
	{
		int removed = 0;
		foreach (Brix.Game.AI.Labor labor in UnityEngine.Object.FindObjectsOfType<Brix.Game.AI.Labor>())
		{
			if (labor != null && labor.gameObject.activeInHierarchy)
			{
				Scenario.Release(labor.gameObject);
				removed++;
			}
		}
		DevToolsPlugin.Log.LogInfo("Tests: took the world's " + removed + " units away");
	}

	private IEnumerator RunOne(TestInfo test, Result result)
	{
		TestContext context = new TestContext();
		result.Outcome = Outcome.Running;
		result.Log = context.Lines;
		result.Data = context.Data;
		float start = Time.realtimeSinceStartup;
		context.Deadline = start + test.TimeoutSeconds;
		if (test.NeedsHostGame && !InHostGame())
		{
			result.Outcome = Outcome.Skipped;
			result.Message = "Needs a game in progress on the host (single player or hosting)";
			yield break;
		}
		// The test's coroutine is stepped by hand (nested IEnumerators too), so an exception or a timeout ends
		// only this test, and the clean-up still runs.
		Stack<IEnumerator> stack = new Stack<IEnumerator>();
		try
		{
			stack.Push((IEnumerator)test.Method.Invoke(null, new object[1] { context }));
		}
		catch (TargetInvocationException ex)
		{
			Finish(result, ex.InnerException ?? ex);
			stack.Clear();
		}
		while (stack.Count > 0)
		{
			object current;
			try
			{
				if (_stopAsked)
				{
					throw new TestFailure("The run was stopped");
				}
				if (Time.realtimeSinceStartup > context.Deadline)
				{
					throw new TestFailure("Timed out after " + test.TimeoutSeconds + " s");
				}
				IEnumerator top = stack.Peek();
				if (!top.MoveNext())
				{
					stack.Pop();
					continue;
				}
				current = top.Current;
			}
			catch (Exception ex)
			{
				Finish(result, ex);
				break;
			}
			if (current is IEnumerator nested)
			{
				stack.Push(nested);
				continue;
			}
			yield return current;
		}
		if (result.Outcome == Outcome.Running)
		{
			result.Outcome = Outcome.Passed;
		}
		for (int i = context.Cleanups.Count - 1; i >= 0; i--)
		{
			try
			{
				context.Cleanups[i]();
			}
			catch (Exception ex)
			{
				context.Log("Clean-up failed: " + ex.Message);
				if (result.Outcome == Outcome.Passed)
				{
					result.Outcome = Outcome.Error;
					result.Message = "Clean-up failed: " + ex.Message;
				}
			}
		}
		// Blocks taken away by a clean-up (DeBlock) leave in the next FixedUpdate, falling blocks a frame or two later:
		// a few frames, so the next test does not find (or lose) them on the same ground.
		for (int i = 0; i < 10; i++)
		{
			yield return new WaitForFixedUpdate();
		}
		result.Seconds = Math.Round(Time.realtimeSinceStartup - start, 2);
		DevToolsPlugin.Log.LogInfo("Test " + test.Name + ": " + result.Outcome + ((result.Message != null) ? (" - " + result.Message) : string.Empty));
	}

	private static void Finish(Result result, Exception ex)
	{
		if (ex is TestFailure)
		{
			result.Outcome = Outcome.Failed;
			result.Message = ex.Message;
		}
		else if (ex is TestSkipped)
		{
			result.NoRoom = ex is TestNoRoom;
			result.Outcome = Outcome.Skipped;
			result.Message = ex.Message;
		}
		else
		{
			result.Outcome = Outcome.Error;
			result.Message = ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace;
		}
	}
}
