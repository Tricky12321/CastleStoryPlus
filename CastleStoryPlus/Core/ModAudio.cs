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
		Ambience
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
	}

	private static ModAudioDriver _driver;

	private static readonly Voice[] Pool = new Voice[Voices];

	private static readonly object[] Owners = new object[8];

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
		float volume = (channel == Channel.Ambience) ? Brix.Audio.SoundEngine.volumeAmbience : Brix.Audio.SoundEngine.volumeSFX;
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

	// The owner needs the listener while it plays something (it says so every frame or when it starts and stops).
	internal static void Need(object owner, bool needed)
	{
		ModAudioDriver driver = Driver();
		if (driver == null)
		{
			return;
		}
		int free = -1;
		for (int i = 0; i < Owners.Length; i++)
		{
			if (Owners[i] == owner)
			{
				if (!needed)
				{
					Owners[i] = null;
				}
				driver.Wake();
				return;
			}
			if (Owners[i] == null && free < 0)
			{
				free = i;
			}
		}
		if (needed && free >= 0)
		{
			Owners[free] = owner;
			driver.Wake();
		}
	}

	internal static bool AnyOwner()
	{
		for (int i = 0; i < Owners.Length; i++)
		{
			if (Owners[i] != null)
			{
				return true;
			}
		}
		return false;
	}

	// A one-shot at a place: volume (0-1, before the game's settings), pitch, linear fall off between min and max
	// distance (blocks). When all voices are busy the quietest of a lower or equal priority is cut, else the sound is
	// not played. Returns whether it plays.
	internal static bool Play(AudioClip clip, Vector3 at, Channel channel, float volume, float pitch, float minDistance, float maxDistance, int priority)
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
			// Cut the quietest voice that is not more important than this sound.
			float quietest = float.MaxValue;
			for (int i = 0; i < Voices; i++)
			{
				Voice candidate = Pool[i];
				if (candidate.Priority <= priority && candidate.Volume < quietest)
				{
					quietest = candidate.Volume;
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
		source.spatialBlend = 1f;
		source.rolloffMode = AudioRolloffMode.Linear;
		source.minDistance = minDistance;
		source.maxDistance = Mathf.Max(maxDistance, minDistance + 1f);
		source.dopplerLevel = 0f;
		voice.Volume = volume;
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

	internal bool HasListener => _listener != null;

	internal void Wake()
	{
		enabled = true;
	}

	private void Update()
	{
		bool voices = ModAudio.Tick();
		bool need = voices || ModAudio.AnyOwner();
		UpdateListener(need);
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
		UpdateListener(false);
	}

	// The game's sounds go through Wwise, which has its own listener: Unity's audio needs one to be heard. Ours is
	// there only while a mod sound plays and only when there is no other (looked at every 2 seconds, also after a new
	// scene).
	private void UpdateListener(bool playing)
	{
		if (!playing)
		{
			if (_listenerObject != null)
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
