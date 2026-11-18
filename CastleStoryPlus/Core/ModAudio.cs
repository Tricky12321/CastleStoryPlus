using UnityEngine;

namespace CastleStoryPlus.Core;

// Sound the mod plays itself (the rain, the guardians), next to the game's own, which goes through Wwise. Shared by
// every mod sound:
// - the game's sound settings (muted, master volume and the volume of a channel) as one factor, GameVolume;
// - the Unity audio listener: the game has none (Wwise has its own), and Unity's audio is silent without one. Whoever
//   plays a sound says so with Need(owner, true); while any owner needs it, one listener follows the camera (so 3D
//   sounds come from where they are), and it is removed again when none does, or when another listener turns up in the
//   scene (never two);
// - Play: a one-shot at a place in the world from a pool of voices (a fixed number, no allocation per sound).
internal static class ModAudio
{
	internal enum Channel
	{
		Sfx,
		Ambience,
		Ui
	}

	private const int Voices = 16;

	internal sealed class Voice
	{
		public AudioSource Source;

		public float Volume;

		public int Priority;

		public Channel Channel;

		public float Started;

		public bool Busy;

		public bool Spatial;

		// Volume after the fall off from the listener (the camera): what is least audible is cut first.
		public float Audibility()
		{
			if (!Spatial)
			{
				return Volume;
			}
			Camera camera = Camera.main;
			if (camera == null)
			{
				return Volume;
			}
			float distance = Vector3.Distance(camera.transform.position, Source.transform.position);
			float span = Mathf.Max(1f, Source.maxDistance - Source.minDistance);
			return Volume * Mathf.Clamp01(1f - (distance - Source.minDistance) / span);
		}
	}

	private static ModAudioDriver _driver;

	private static readonly Voice[] Pool = new Voice[Voices];

	private static readonly System.Collections.Generic.List<object> Owners = new System.Collections.Generic.List<object>();

	// Sounds started and not started (no free voice) since the game began, for the tests.
	internal static int Started;

	internal static int Dropped;

	internal static int Stolen;

	internal static bool ListenerPresent => _driver != null && _driver.HasListener;

	internal static int VoicesPlaying
	{
		get
		{
			int count = 0;
			for (int i = 0; i < Voices; i++)
			{
				if (Pool[i] != null && Pool[i].Busy)
				{
					count++;
				}
			}
			return count;
		}
	}

	internal static int VoiceLimit => Voices;

	// For the tests: a voice of the pool (null until it was first needed).
	internal static Voice VoiceAt(int index)
	{
		return Pool[index];
	}

	// The game's own sound settings (Wwise): 0 when muted, else master times the channel (both 0-100).
	internal static float GameVolume(Channel channel)
	{
		if (Brix.Audio.SoundEngine.mute)
		{
			return 0f;
		}
		float volume = (channel == Channel.Ambience) ? Brix.Audio.SoundEngine.volumeAmbience : (channel == Channel.Ui) ? Brix.Audio.SoundEngine.volumeUI : Brix.Audio.SoundEngine.volumeSFX;
		return Mathf.Clamp01(Brix.Audio.SoundEngine.volumeMaster / 100f) * Mathf.Clamp01(volume / 100f);
	}

	private static ModAudioDriver Driver()
	{
		if (_driver == null && Plugin.Root != null)
		{
			_driver = Plugin.Root.AddComponent<ModAudioDriver>();
		}
		return _driver;
	}

	// The owner needs the listener while it plays something (any number of owners).
	internal static void Need(object owner, bool needed)
	{
		ModAudioDriver driver = Driver();
		if (driver == null)
		{
			return;
		}
		bool has = Owners.Contains(owner);
		if (needed && !has)
		{
			Owners.Add(owner);
		}
		else if (!needed && has)
		{
			Owners.Remove(owner);
		}
		driver.Wake();
	}

	internal static int OwnerCount => Owners.Count;

	internal static bool AnyOwner()
	{
		return Owners.Count > 0;
	}

	// A user interface sound (2D, the game's UI volume): a click, a jingle.
	internal static bool PlayUi(AudioClip clip, float volume)
	{
		return Start(clip, Vector3.zero, false, Channel.Ui, volume, 1f, 1f, 2f, 2);
	}

	// A one-shot at a place in the world (3D): volume (0-1, before the game's settings), pitch, linear fall off between
	// min and max distance (blocks). When all voices are busy the least audible voice (by volume and distance to the
	// listener) of a lower or equal priority is cut, else the sound is not played. Returns whether it plays.
	internal static bool Play(AudioClip clip, Vector3 at, Channel channel, float volume, float pitch, float minDistance, float maxDistance, int priority)
	{
		return Start(clip, at, true, channel, volume, pitch, minDistance, maxDistance, priority);
	}

	// A one-shot without a place (2D, the same everywhere): a message, a jingle.
	internal static bool Play2D(AudioClip clip, Channel channel, float volume, float pitch, int priority)
	{
		return Start(clip, Vector3.zero, false, channel, volume, pitch, 1f, 2f, priority);
	}

	private static bool Start(AudioClip clip, Vector3 at, bool spatial, Channel channel, float volume, float pitch, float minDistance, float maxDistance, int priority)
	{
		ModAudioDriver driver = Driver();
		if (clip == null || driver == null || GameVolume(channel) <= 0f)
		{
			return false;
		}
		Voice voice = null;
		for (int i = 0; i < Voices; i++)
		{
			if (Pool[i] == null)
			{
				Pool[i] = MakeVoice(driver, i);
			}
			if (!Pool[i].Busy)
			{
				voice = Pool[i];
				break;
			}
		}
		if (voice == null)
		{
			// Cut the least audible voice that is not more important than this sound.
			float quietest = float.MaxValue;
			for (int i = 0; i < Voices; i++)
			{
				Voice candidate = Pool[i];
				float level = candidate.Audibility();
				if (candidate.Priority <= priority && level < quietest)
				{
					quietest = level;
					voice = candidate;
				}
			}
			if (voice == null)
			{
				Dropped++;
				return false;
			}
			voice.Source.Stop();
			Stolen++;
		}
		AudioSource source = voice.Source;
		source.transform.position = at;
		source.clip = clip;
		source.pitch = pitch;
		source.spatialBlend = spatial ? 1f : 0f;
		source.rolloffMode = AudioRolloffMode.Linear;
		source.minDistance = minDistance;
		source.maxDistance = Mathf.Max(maxDistance, minDistance + 1f);
		source.dopplerLevel = 0f;
		voice.Volume = volume;
		voice.Spatial = spatial;
		voice.Priority = priority;
		voice.Started = Time.unscaledTime;
		voice.Busy = true;
		voice.Channel = channel;
		source.volume = volume * GameVolume(channel);
		source.Play();
		Started++;
		driver.Wake();
		return true;
	}

	private static Voice MakeVoice(ModAudioDriver driver, int index)
	{
		GameObject go = new GameObject("CSP Voice " + index);
		go.transform.SetParent(driver.transform, false);
		AudioSource source = go.AddComponent<AudioSource>();
		source.playOnAwake = false;
		source.loop = false;
		return new Voice { Source = source };
	}

	// Each frame while something is going: volume follows the game's settings, finished voices are freed.
	internal static bool Tick()
	{
		bool any = false;
		for (int i = 0; i < Voices; i++)
		{
			Voice voice = Pool[i];
			if (voice == null || !voice.Busy)
			{
				continue;
			}
			if (!voice.Source.isPlaying)
			{
				voice.Busy = false;
				continue;
			}
			any = true;
			voice.Source.volume = voice.Volume * GameVolume(voice.Channel);
		}
		return any;
	}

	// The driver is gone (the mod was reloaded): its voices go with it.
	internal static void Release(ModAudioDriver driver)
	{
		if (_driver != driver)
		{
			return;
		}
		for (int i = 0; i < Voices; i++)
		{
			if (Pool[i] != null && Pool[i].Source != null)
			{
				Object.Destroy(Pool[i].Source.gameObject);
			}
			Pool[i] = null;
		}
		_driver = null;
	}

	// Stops everything (a feature switched off, a game left).
	internal static void StopAll()
	{
		for (int i = 0; i < Voices; i++)
		{
			if (Pool[i] != null && Pool[i].Busy)
			{
				Pool[i].Source.Stop();
				Pool[i].Busy = false;
			}
		}
	}
}

// Keeps the listener while a sound is going and frees finished voices. Asleep (disabled) when there is nothing to do.
internal sealed class ModAudioDriver : MonoBehaviour
{
	private AudioListener _listener;

	private GameObject _listenerObject;

	private float _nextLook;

	private float _lastLook;

	private float _lastNeed;

	// Seconds the listener stays after the last sound, so sounds that follow find it.
	private const float Linger = 4f;

	internal bool HasListener => _listener != null;

	internal void Wake()
	{
		enabled = true;
		_lastNeed = Time.unscaledTime;
		// A sound after a pause: look for a listener now, not at the next two second look.
		if (_listener == null && Time.unscaledTime - _lastLook > 0.25f)
		{
			_nextLook = 0f;
		}
	}

	private void OnDestroy()
	{
		ModAudio.Release(this);
	}

	private void Update()
	{
		bool voices = ModAudio.Tick();
		bool need = voices || ModAudio.AnyOwner();
		UpdateListener(need);
		if (need)
		{
			_lastNeed = Time.unscaledTime;
		}
		if (!need && _listener == null)
		{
			enabled = false;
		}
	}

	private void LateUpdate()
	{
		// The listener hears from the camera, so a sound's distance is the distance from where the player looks.
		if (_listenerObject != null)
		{
			Camera camera = Camera.main;
			if (camera != null)
			{
				_listenerObject.transform.SetPositionAndRotation(camera.transform.position, camera.transform.rotation);
			}
		}
	}

	private void OnDisable()
	{
		if (_listenerObject != null)
		{
			Destroy(_listenerObject);
			_listenerObject = null;
			_listener = null;
		}
	}

	// The game's sounds go through Wwise, which has its own listener: Unity's audio needs one to be heard. Ours is
	// there only while a mod sound plays and only when there is no other (looked at every 2 seconds, also after a new
	// scene).
	private void UpdateListener(bool playing)
	{
		if (!playing)
		{
			if (_listenerObject != null && (Time.unscaledTime - _lastNeed > Linger || !enabled))
			{
				Destroy(_listenerObject);
				_listenerObject = null;
				_listener = null;
			}
			return;
		}
		if (Time.unscaledTime < _nextLook)
		{
			return;
		}
		_nextLook = Time.unscaledTime + 2f;
		_lastLook = Time.unscaledTime;
		int count = FindObjectsOfType<AudioListener>().Length;
		if (_listener == null && count == 0)
		{
			_listenerObject = new GameObject("CSP Audio Listener");
			_listenerObject.transform.SetParent(transform, false);
			_listener = _listenerObject.AddComponent<AudioListener>();
			Camera camera = Camera.main;
			if (camera != null)
			{
				_listenerObject.transform.SetPositionAndRotation(camera.transform.position, camera.transform.rotation);
			}
		}
		else if (_listener != null && count > 1)
		{
			Destroy(_listenerObject);
			_listenerObject = null;
			_listener = null;
		}
	}
}
