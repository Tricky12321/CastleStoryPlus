using System;
using System.Collections.Generic;
using System.IO;
using Path = System.IO.Path;
using BepInEx.Configuration;
using Brix.Game.AI;
using CastleStoryPlus.Core;
using UnityEngine;

namespace CastleStoryPlus.Treasure;

internal enum GuardianSound
{
	Idle,
	Roar,
	Step,
	Attack,
	Hit,
	Death
}

// The guardians' sounds (Features.GuardianSounds): a call now and then, a roar when it spots players' units or is
// provoked, footsteps that fit its size and gait, an attack, a hit and a death sound, only the animal's own voice.
//
// The sounds are cut by tools/make_guardian_sounds.py from CC0 animal recordings (listed in
// Assets/Sounds/Guardians/SOURCES.md, nothing from the game) and read from Assets/Sounds/Guardians/<Guardian>_<event>_<n>.wav. The set is swappable: replace the files, or
// add more numbered ones, and the game picks one at random per event. The table below (Settings) is the one place that
// says how each event is played: how loud, how far it carries and how important it is when the voices run out.
// They are played as 3D sounds at the guardian (quieter with distance) through ModAudio, which also follows the game's
// mute and volume settings. The clips load once per guardian, the first time it makes a sound.
[Feature(Features.GuardianSounds, Features.GuardianSoundsInfo)]
internal static class GuardianSounds
{
	internal sealed class Setting
	{
		public string File;

		// Volume 0-1 (before the game's settings), full volume out to MinDistance blocks, silent at MaxDistance.
		public float Volume;

		public float MinDistance;

		public float MaxDistance;

		// Higher cuts lower when all voices are busy.
		public int Priority;

		// Random pitch spread (a fraction), so repeated sounds are not identical.
		public float PitchSpread;
	}

	// How each event is played (index: GuardianSound).
	internal static readonly Setting[] Settings =
	{
		new Setting { File = "idle", Volume = 0.55f, MinDistance = 6f, MaxDistance = 45f, Priority = 1, PitchSpread = 0.06f },
		new Setting { File = "roar", Volume = 1f, MinDistance = 10f, MaxDistance = 90f, Priority = 3, PitchSpread = 0.04f },
		new Setting { File = "step", Volume = 0.5f, MinDistance = 4f, MaxDistance = 32f, Priority = 0, PitchSpread = 0.08f },
		new Setting { File = "attack", Volume = 0.85f, MinDistance = 6f, MaxDistance = 55f, Priority = 2, PitchSpread = 0.05f },
		new Setting { File = "hit", Volume = 0.7f, MinDistance = 6f, MaxDistance = 50f, Priority = 2, PitchSpread = 0.06f },
		new Setting { File = "death", Volume = 1f, MinDistance = 10f, MaxDistance = 80f, Priority = 3, PitchSpread = 0.02f }
	};

	// How heavy each guardian walks (the loudness of its footsteps), by BeastKind: the long-legged Spirestalker paces
	// lightly, the Boulderback is the heaviest.
	private static readonly float[] StepLevel = { 0.7f, 0.6f, 1f, 0.9f };

	private static readonly AudioClip[][][] Bank = new AudioClip[4][][];

	private static readonly int[] LastPick = new int[4 * 6];

	private static ConfigEntry<float> _volume;

	private static readonly List<Labor> Labors = new List<Labor>();

	private static float _nextScan;

	// For the tests and the API: what was played last, and how many sounds of each guardian and event.
	internal static readonly int[,] Played = new int[4, 6];

	internal static AudioClip LastClip;

	internal static float LastVolume;

	internal static float LastPitch;

	internal static float LastMinDistance;

	internal static float LastMaxDistance;

	internal static Vector3 LastAt;

	internal static string Folder => Path.Combine(Path.Combine(Path.Combine(Plugin.Directory, "Assets"), "Sounds"), "Guardians");

	private static bool _hooked;

	private static bool _folderLogged;

	private static void Enable()
	{
		Bind();
		// Once, however often the feature is switched on or the mod reloaded in this session.
		if (!_hooked)
		{
			_hooked = true;
			GameSession.OnLeave(ModAudio.StopAll);
			// The clips are read when a game is ready, not at the first sound, so no sound causes a hitch.
			GameSession.OnLevelReady(Preload);
		}
	}

	// Reads the clips of all guardians (once).
	internal static void Preload()
	{
		foreach (BeastInfo info in BeastKinds.All)
		{
			if (info != null)
			{
				Load(info);
			}
		}
	}

	private static void Bind()
	{
		if (_volume == null)
		{
			_volume = Plugin.Cfg.Bind("Treasure.Sounds", "Volume", 1f, new ConfigDescription("How loud the guardians' sounds are (1: as made, 0: silent). The game's own sound volume and mute apply on top.", new AcceptableValueRange<float>(0f, 1f)));
		}
	}

	internal static bool On => Plugin.IsEnabled(Features.GuardianSounds) && (_volume == null || _volume.Value > 0f);

	// Plays the guardian's sound at the place. scale: relative loudness (footsteps by speed); returns whether it plays.
	internal static bool Play(BeastInfo info, GuardianSound sound, Vector3 at, float scale = 1f)
	{
		if (info == null || !On)
		{
			return false;
		}
		AudioClip clip = Pick(info, sound);
		if (clip == null)
		{
			return false;
		}
		Setting setting = Settings[(int)sound];
		float volume = setting.Volume * scale * ((_volume != null) ? _volume.Value : 1f) * ((sound == GuardianSound.Step) ? StepLevel[(int)info.Kind] : 1f);
		float pitch = 1f + UnityEngine.Random.Range(-setting.PitchSpread, setting.PitchSpread);
		if (!ModAudio.Play(clip, at, ModAudio.Channel.Sfx, Mathf.Clamp01(volume), pitch, setting.MinDistance, setting.MaxDistance, setting.Priority))
		{
			return false;
		}
		Played[(int)info.Kind, (int)sound]++;
		LastClip = clip;
		LastVolume = volume;
		LastPitch = pitch;
		LastMinDistance = setting.MinDistance;
		LastMaxDistance = setting.MaxDistance;
		LastAt = at;
		return true;
	}

	internal static AudioClip[] Clips(BeastInfo info, GuardianSound sound)
	{
		Load(info);
		return Bank[(int)info.Kind][(int)sound];
	}

	// A random clip of the event, never the one played last when there are more.
	private static AudioClip Pick(BeastInfo info, GuardianSound sound)
	{
		AudioClip[] clips = Clips(info, sound);
		if (clips == null || clips.Length == 0)
		{
			return null;
		}
		int slot = (int)info.Kind * 6 + (int)sound;
		int pick = UnityEngine.Random.Range(0, clips.Length);
		if (clips.Length > 1 && pick == LastPick[slot])
		{
			pick = (pick + 1 + UnityEngine.Random.Range(0, clips.Length - 1)) % clips.Length;
		}
		LastPick[slot] = pick;
		return clips[pick];
	}

	private static void Load(BeastInfo info)
	{
		if (Bank[(int)info.Kind] != null)
		{
			return;
		}
		AudioClip[][] kind = new AudioClip[6][];
		Bank[(int)info.Kind] = kind;
		if (!Directory.Exists(Folder) && !_folderLogged)
		{
			_folderLogged = true;
			Plugin.Log.LogWarning("Guardian sounds: the folder " + Folder + " is missing, the guardians stay silent");
		}
		for (int i = 0; i < kind.Length; i++)
		{
			List<AudioClip> clips = new List<AudioClip>();
			try
			{
				string[] files = Directory.Exists(Folder) ? Directory.GetFiles(Folder, info.Name + "_" + Settings[i].File + "_*.wav") : new string[0];
				Array.Sort(files, StringComparer.Ordinal);
				foreach (string file in files)
				{
					AudioClip clip = ReadWav(file);
					if (clip != null)
					{
						clips.Add(clip);
					}
				}
			}
			catch (Exception ex)
			{
				Plugin.Log.LogError("Guardian sounds: could not read the " + info.Name + " " + Settings[i].File + " sounds: " + ex.Message);
			}
			kind[i] = clips.ToArray();
		}
	}

	// PCM 16-bit or 32-bit float WAV, any channel count and rate.
	internal static AudioClip ReadWav(string file)
	{
		byte[] data = File.ReadAllBytes(file);
		if (data.Length < 44 || data[0] != 'R' || data[1] != 'I' || data[2] != 'F' || data[3] != 'F')
		{
			Plugin.Log.LogWarning("Guardian sounds: " + file + " is not a WAV file");
			return null;
		}
		int format = 0;
		int channels = 0;
		int rate = 0;
		int bits = 0;
		int offset = 12;
		while (offset + 8 <= data.Length)
		{
			string id = System.Text.Encoding.ASCII.GetString(data, offset, 4);
			int size = BitConverter.ToInt32(data, offset + 4);
			int body = offset + 8;
			if (id == "fmt ")
			{
				format = BitConverter.ToInt16(data, body);
				channels = BitConverter.ToInt16(data, body + 2);
				rate = BitConverter.ToInt32(data, body + 4);
				bits = BitConverter.ToInt16(data, body + 14);
			}
			else if (id == "data")
			{
				size = Mathf.Min(size, data.Length - body);
				int bytes = bits / 8;
				if (channels < 1 || bytes < 2 || !((format == 1 && bits == 16) || (format == 3 && bits == 32)))
				{
					Plugin.Log.LogWarning("Guardian sounds: " + file + " must be 16-bit PCM or 32-bit float");
					return null;
				}
				int count = size / bytes;
				float[] samples = new float[count];
				if (bits == 32)
				{
					Buffer.BlockCopy(data, body, samples, 0, count * 4);
				}
				else
				{
					short[] pcm = new short[count];
					Buffer.BlockCopy(data, body, pcm, 0, count * 2);
					for (int i = 0; i < count; i++)
					{
						samples[i] = pcm[i] / 32768f;
					}
				}
				AudioClip clip = AudioClip.Create(Path.GetFileNameWithoutExtension(file), count / channels, channels, rate, false);
				clip.SetData(samples, 0);
				return clip;
			}
			offset = body + size + (size & 1);
		}
		Plugin.Log.LogWarning("Guardian sounds: " + file + " has no sound data");
		return null;
	}

	// Whether a living unit of a player is within the radius of the place (looked at from a list taken twice a second).
	internal static bool PlayerNear(Vector3 at, float radius)
	{
		if (Time.time >= _nextScan)
		{
			_nextScan = Time.time + 0.5f;
			Live<Labor>.Active(Labors);
		}
		float limit = radius * radius;
		foreach (Labor labor in Labors)
		{
			if (labor != null && BeastTemper.IsPlayers(labor) && labor.gameObject.activeInHierarchy && Giant.GiantBricktron.IsAlive(labor) && Beasts.KindOf(labor) == null && (labor.transform.position - at).sqrMagnitude <= limit)
			{
				return true;
			}
		}
		return false;
	}
}
