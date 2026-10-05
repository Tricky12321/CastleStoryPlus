using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Brix.Engine;
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
	}

	internal static TestRunner Instance;

	internal static readonly List<TestInfo> Tests = new List<TestInfo>();

	internal readonly List<Result> Results = new List<Result>();

	internal bool Running;

	internal int RunNumber;

	internal DateTime StartedAt;

	internal DateTime FinishedAt;

	private bool _stopAsked;

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
				Tests.Add(new TestInfo
				{
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
	internal bool Start(List<TestInfo> tests)
	{
		if (Running)
		{
			return false;
		}
		Running = true;
		_stopAsked = false;
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
		DevToolsPlugin.Log.LogInfo("Tests: run " + RunNumber + " started, " + tests.Count + " tests");
		for (int i = 0; i < tests.Count; i++)
		{
			Result result = Results[i];
			if (_stopAsked)
			{
				result.Outcome = Outcome.Skipped;
				result.Message = "The run was stopped";
				continue;
			}
			yield return StartCoroutine(RunOne(tests[i], result));
		}
		Running = false;
		FinishedAt = DateTime.Now;
		int passed = Results.FindAll((Result r) => r.Outcome == Outcome.Passed).Count;
		DevToolsPlugin.Log.LogInfo("Tests: run " + RunNumber + " done, " + passed + "/" + Results.Count + " passed");
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
