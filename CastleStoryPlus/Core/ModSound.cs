using System.Collections.Generic;
using UnityEngine;

namespace CastleStoryPlus.Core;

// Unity audio for the mod's own sounds (the rain, the achievement chime), made in code. The game's sounds go through
// Wwise, which has its own listener: Unity's audio needs one to be heard. This keeps one listener of ours, there only
// while a sound of the mod plays and only when there is no other (looked at every 2 seconds, also after a new
// scene), so there are never two. Volumes follow the game's own sound settings.
internal sealed class ModSound : MonoBehaviour
{
	private const float LookSeconds = 2f;

	private static ModSound _instance;

	// Who needs a listener now (a playing sound's owner).
	private static readonly HashSet<object> Needing = new HashSet<object>();

	private AudioListener _listener;

	private AudioSource _source;

	private float _nextLook;

	private float _onceUntil;

	private static ModSound Instance
	{
		get
		{
			if (_instance == null)
			{
				_instance = Plugin.Root.AddComponent<ModSound>();
			}
			return _instance;
		}
	}

	// The game's own sound settings (Wwise): muted, or the master and ambience volumes (0 to 100) as 0 to 1.
	internal static float AmbienceVolume => Level(Brix.Audio.SoundEngine.volumeAmbience);

	// The same for the interface's sounds.
	internal static float UiVolume => Level(Brix.Audio.SoundEngine.volumeUI);

	// Whether our own listener exists (for the tests).
	internal static bool HasOwnListener => _instance != null && _instance._listener != null;

	private static float Level(float channel)
	{
		if (Brix.Audio.SoundEngine.mute)
		{
			return 0f;
		}
		return Mathf.Clamp01(Brix.Audio.SoundEngine.volumeMaster / 100f) * Mathf.Clamp01(channel / 100f);
	}

	// A sound of the owner plays (or has stopped): a listener is there while any does and no other exists.
	internal static void Listening(object owner, bool playing)
	{
		if (playing)
		{
			if (Needing.Add(owner))
			{
				Instance._nextLook = 0f;
			}
		}
		else
		{
			Needing.Remove(owner);
		}
	}

	// A short sound played once (the chime) at the volume given.
	internal static void PlayOnce(AudioClip clip, float volume)
	{
		if (clip == null || volume <= 0f)
		{
			return;
		}
		ModSound sound = Instance;
		if (sound._source == null)
		{
			sound._source = sound.gameObject.AddComponent<AudioSource>();
			sound._source.spatialBlend = 0f;
			sound._source.playOnAwake = false;
		}
		sound._source.PlayOneShot(clip, volume);
		sound._onceUntil = Time.unscaledTime + clip.length + 0.3f;
		Listening(sound, true);
	}

	private void Update()
	{
		if (_onceUntil > 0f && Time.unscaledTime >= _onceUntil)
		{
			_onceUntil = 0f;
			Listening(this, false);
		}
		if (Time.unscaledTime < _nextLook)
		{
			return;
		}
		_nextLook = Time.unscaledTime + LookSeconds;
		if (Needing.Count == 0)
		{
			if (_listener != null)
			{
				Destroy(_listener);
				_listener = null;
			}
			return;
		}
		int count = FindObjectsOfType<AudioListener>().Length;
		if (_listener == null && count == 0)
		{
			_listener = gameObject.AddComponent<AudioListener>();
		}
		else if (_listener != null && count > 1)
		{
			Destroy(_listener);
			_listener = null;
		}
	}
}
