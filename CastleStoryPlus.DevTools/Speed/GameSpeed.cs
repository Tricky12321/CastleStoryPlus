using System.Globalization;
using Brix.Engine;
using CastleStoryPlus.Diagnostics;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CastleStoryPlus.DevTools.Speed;

// Fast-forward for testing (/api/speed, the MCP tool game_speed): runs the game at up to 20x.
// The speed goes through the game's own clock (ClockConfig) as an extra clock speed of its own, so pausing keeps it
// and unpausing returns to it, like the mod's speed keys. Time.timeScale alone is not enough: Unity advances game
// time by at most Time.maximumDeltaTime per frame (1/3 s by default), so at 20x and 30 fps (0.66 s of game time per
// frame) the game silently ran at about 10x. While fast, maximumDeltaTime is raised to allow the speed down to
// MinFps, and put back at normal speed. The fixed timestep is left alone (the game's AI counts in fixed steps).
// Back to 1x when the auto-timer runs out, when a map loads or the player leaves to the menu, in multiplayer, and
// when the game or the player picks another speed (speed keys, the game's own speed buttons): then the chosen speed
// is dropped and the game's speed kept. The measured speed is game seconds per real second over the last seconds.
internal class GameSpeed : MonoBehaviour
{
	internal const float MinSpeed = 0.25f;

	internal const float MaxSpeed = 20f;

	// The lowest frame rate at which the chosen speed is still reached; below it Unity's cap slows the game down.
	private const float MinFps = 10f;

	// The game's clock speeds are an enum keyed into ClockConfig.ClockSpeedMap; this one is the DevTools speed's.
	private const ClockConfig.ClockSpeed DevSpeed = (ClockConfig.ClockSpeed)100;

	// One sample of game and real time every SampleSeconds, the last Samples kept.
	private const float SampleSeconds = 0.1f;

	private const int Samples = 200;

	internal static GameSpeed Instance;

	// The speed DevTools set, 1 when it set none (or it was reset).
	internal float Chosen = 1f;

	internal bool Active;

	// Real time (realtimeSinceStartup) when the speed goes back to 1x, 0 for no timer.
	internal float ResetAt;

	internal string LastReset;

	private float _savedMaxDelta;

	// Set when the speed was reset while the game was paused or frozen (loading, saving): whoever froze it puts the
	// old time scale back afterwards, so that is turned into 1x once it runs again.
	private float _pendingSpeed;

	private int _quietSpikesAtStart;

	private readonly float[] _gameTime = new float[Samples];

	private readonly float[] _realTime = new float[Samples];

	private readonly int[] _frames = new int[Samples];

	private readonly int[] _fixedSteps = new int[Samples];

	private readonly int[] _cappedFrames = new int[Samples];

	private int _sampled;

	private float _nextSample;

	private int _fixedStepCount;

	private int _cappedFrameCount;

	private Architecte _architecte;

	private bool _inGame;

	private void Awake()
	{
		Instance = this;
		SceneManager.sceneLoaded += OnSceneLoaded;
	}

	private void OnDestroy()
	{
		SceneManager.sceneLoaded -= OnSceneLoaded;
	}

	private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
	{
		Reset("scene " + scene.name + " loaded");
	}

	internal static bool InGame()
	{
		return Architecte.commence && !Architecte.termine && Architecte.instance != null;
	}

	// Main thread. speed 1 is the same as Reset. maxFrameSeconds, when given, is the most game time one frame may
	// advance instead (the game then runs slower than asked when the frame rate drops, but its per-frame logic never
	// sees a bigger step than that).
	internal void Set(float speed, float forSeconds, float maxFrameSeconds = 0f)
	{
		speed = Mathf.Clamp(speed, MinSpeed, MaxSpeed);
		if (Mathf.Approximately(speed, 1f))
		{
			Reset("asked for 1x");
			return;
		}
		if (!Active)
		{
			_savedMaxDelta = Time.maximumDeltaTime;
			_quietSpikesAtStart = FrameMonitor.QuietSpikes;
		}
		_pendingSpeed = 0f;
		ClockConfig.ClockSpeedMap[DevSpeed] = speed;
		ClockConfig._unpausedClockSpeed = DevSpeed;
		if (!ClockConfig.Paused)
		{
			ClockConfig.SetTimeScale(DevSpeed);
		}
		Time.maximumDeltaTime = (maxFrameSeconds > 0f) ? maxFrameSeconds : Mathf.Max(_savedMaxDelta, speed / MinFps);
		Active = true;
		Chosen = speed;
		ResetAt = (forSeconds > 0f) ? Time.realtimeSinceStartup + forSeconds : 0f;
		DevToolsPlugin.Log.LogInfo("Speed: " + Text(speed) + "x" + (ClockConfig.Paused ? " (paused, applies when unpaused)" : string.Empty)
			+ ((ResetAt > 0f) ? (", back to 1x after " + Text(forSeconds) + " real seconds") : string.Empty)
			+ ", maximumDeltaTime " + Text(Time.maximumDeltaTime) + " s, fixed step " + Text(Time.fixedDeltaTime) + " s");
	}

	// Back to normal speed (when DevTools set the speed). Main thread.
	internal void Reset(string reason)
	{
		if (!Active)
		{
			return;
		}
		ClockConfig.ClockSpeedMap[DevSpeed] = 1f;
		if (ClockConfig._unpausedClockSpeed == DevSpeed)
		{
			ClockConfig._unpausedClockSpeed = ClockConfig.ClockSpeed.Default;
		}
		if (Mathf.Approximately(Time.timeScale, Chosen))
		{
			ClockConfig.SetTimeScale(ClockConfig.ClockSpeed.Default);
		}
		else if (Time.timeScale == 0f)
		{
			_pendingSpeed = Chosen;
		}
		Time.maximumDeltaTime = _savedMaxDelta;
		Active = false;
		ResetAt = 0f;
		int quiet = FrameMonitor.QuietSpikes - _quietSpikesAtStart;
		LastReset = reason;
		DevToolsPlugin.Log.LogInfo("Speed: back to 1x from " + Text(Chosen) + "x (" + reason + ")"
			+ ((quiet > 0) ? (", " + quiet + " frame spike warnings left out meanwhile") : string.Empty));
		Chosen = 1f;
	}

	// Another speed was picked by the game or the player: keep it, only drop DevTools' own state.
	private void Release(string reason)
	{
		ClockConfig.ClockSpeedMap[DevSpeed] = 1f;
		Time.maximumDeltaTime = _savedMaxDelta;
		Active = false;
		ResetAt = 0f;
		LastReset = reason;
		DevToolsPlugin.Log.LogInfo("Speed: " + Text(Chosen) + "x dropped, " + reason + " (now " + Text(Time.timeScale) + "x)");
		Chosen = 1f;
	}

	private void FixedUpdate()
	{
		_fixedStepCount++;
	}

	private void Update()
	{
		if (Time.deltaTime > 0f && Time.deltaTime >= Time.maximumDeltaTime * 0.999f)
		{
			_cappedFrameCount++;
		}
		Sample();
		WatchGame();
		if (_pendingSpeed > 0f && Time.timeScale != 0f)
		{
			if (Mathf.Approximately(Time.timeScale, _pendingSpeed))
			{
				ClockConfig.SetTimeScale(ClockConfig.ClockSpeed.Default);
				DevToolsPlugin.Log.LogInfo("Speed: 1x after the game unfroze at " + Text(_pendingSpeed) + "x");
			}
			_pendingSpeed = 0f;
		}
		if (!Active)
		{
			return;
		}
		if (ResetAt > 0f && Time.realtimeSinceStartup >= ResetAt)
		{
			Reset("the timer ran out");
		}
		else if (Neo.multiplayer)
		{
			Reset("multiplayer");
		}
		else if (Time.timeScale != 0f && !Mathf.Approximately(Time.timeScale, Chosen))
		{
			Release("the game or the speed keys set another speed");
		}
	}

	// A map that starts or ends (loading, back to the menu, another map) resets the speed.
	private void WatchGame()
	{
		bool inGame = InGame();
		Architecte architecte = Architecte.instance;
		if (inGame != _inGame || architecte != _architecte)
		{
			string reason = (architecte != _architecte && architecte != null) ? "a map loads" : (inGame ? "the map started" : "left the map");
			_inGame = inGame;
			_architecte = architecte;
			Reset(reason);
		}
	}

	private void Sample()
	{
		float now = Time.realtimeSinceStartup;
		if (now < _nextSample)
		{
			return;
		}
		_nextSample = now + SampleSeconds;
		int slot = _sampled % Samples;
		_gameTime[slot] = Time.time;
		_realTime[slot] = now;
		_frames[slot] = Time.frameCount;
		_fixedSteps[slot] = _fixedStepCount;
		_cappedFrames[slot] = _cappedFrameCount;
		_sampled++;
	}

	// Game seconds per real second over about the last seconds, and frames and fixed steps per real second. False
	// when there are not two samples yet.
	internal bool Measure(float seconds, out float speed, out float window, out float fps, out float fixedPerFrame, out float cappedShare)
	{
		speed = window = fps = fixedPerFrame = cappedShare = 0f;
		if (_sampled < 2)
		{
			return false;
		}
		int newest = (_sampled - 1) % Samples;
		int oldestIndex = Mathf.Max(0, _sampled - Samples);
		int back = _sampled - 2;
		while (back > oldestIndex && _realTime[newest] - _realTime[(back - 1) % Samples] <= seconds)
		{
			back--;
		}
		int old = back % Samples;
		window = _realTime[newest] - _realTime[old];
		if (window <= 0f)
		{
			return false;
		}
		int frames = _frames[newest] - _frames[old];
		speed = (_gameTime[newest] - _gameTime[old]) / window;
		fps = frames / window;
		fixedPerFrame = (frames > 0) ? (float)(_fixedSteps[newest] - _fixedSteps[old]) / frames : 0f;
		cappedShare = (frames > 0) ? (float)(_cappedFrames[newest] - _cappedFrames[old]) / frames : 0f;
		return true;
	}

	private static string Text(float value)
	{
		return value.ToString("0.##", CultureInfo.InvariantCulture);
	}
}
