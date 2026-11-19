using System;
using System.Collections;
using Brix.Audio;
using Brix.Engine;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.AI.Damage;
using Brix.Game.Components;
using CastleStoryPlus.Core;
using CastleStoryPlus.Giant;
using CastleStoryPlus.DevTools.Testing;
using CastleStoryPlus.Treasure;
using CastleStoryPlus.Weather;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.DevTools.Tests;

// The guardians' sounds (GuardianSounds, BeastVoice, ModAudio): every guardian has its sound for every event, the right
// clip plays on the right event, it is a 3D sound, it follows the game's volume and mute, the listener exists only
// while a mod sound plays (and never twice), the voices are capped, and playing a sound allocates nothing.
internal static class GuardianSoundTests
{
	private static readonly string[] Events = { "idle", "roar", "step", "attack", "hit", "death" };

	private static BeastInfo Info(BeastKind kind)
	{
		return BeastKinds.All[(int)kind];
	}

	private static void NeedSounds(TestContext t)
	{
		if (!Plugin.IsEnabled(Features.GuardianSounds) || !Plugin.IsEnabled(Features.Treasure))
		{
			t.Skip("GuardianSounds or Treasure is off");
		}
		// The mod ships no guardian sounds for now: these tests run once WAV files are put in the folder.
		foreach (BeastInfo info in BeastKinds.All)
		{
			if (GuardianSounds.Clips(info, GuardianSound.Roar).Length == 0)
			{
				t.Skip("No guardian sound files in " + GuardianSounds.Folder);
			}
		}
	}

	private static int Count(BeastKind kind, GuardianSound sound)
	{
		return GuardianSounds.Played[(int)kind, (int)sound];
	}

	// The test sets the game's sound settings (they go to Wwise): the player's own are put back afterwards.
	private static void Isolate(TestContext t, Action also = null)
	{
		bool mute = SoundEngine.mute;
		float master = SoundEngine.volumeMaster;
		float sfx = SoundEngine.volumeSFX;
		t.Cleanup(() =>
		{
			ModAudio.StopAll();
			SoundEngine.mute = mute;
			SoundEngine.volumeMaster = master;
			SoundEngine.volumeSFX = sfx;
			also?.Invoke();
		});
		ResetAudio();
	}

	private static void ResetAudio()
	{
		ModAudio.StopAll();
		SoundEngine.mute = false;
		SoundEngine.volumeMaster = 100f;
		SoundEngine.volumeSFX = 100f;
	}

	// A short tone for the tests that need any clip (the mod ships no guardian sounds).
	private static AudioClip Tone()
	{
		const int Rate = 32000;
		float[] data = new float[Rate / 4];
		for (int i = 0; i < data.Length; i++)
		{
			data[i] = 0.3f * Mathf.Sin(2f * Mathf.PI * 440f * i / Rate);
		}
		AudioClip clip = AudioClip.Create("TestTone", data.Length, 1, Rate, false);
		clip.SetData(data, 0);
		return clip;
	}

	private static int Listeners()
	{
		return UnityEngine.Object.FindObjectsOfType<AudioListener>().Length;
	}

	[GameTest("GuardianSounds", "Every guardian has clips for idle, roar, step, attack, hit and death, with the right length and rate", TimeoutSeconds = 60f, NeedsHostGame = false)]
	private static IEnumerator EveryGuardianHasItsSounds(TestContext t)
	{
		NeedSounds(t);
		foreach (BeastInfo info in BeastKinds.All)
		{
			for (int i = 0; i < Events.Length; i++)
			{
				AudioClip[] clips = GuardianSounds.Clips(info, (GuardianSound)i);
				t.True(clips != null && clips.Length > 0, info.Name + " has no " + Events[i] + " sound in " + GuardianSounds.Folder);
				foreach (AudioClip clip in clips)
				{
					t.True(clip.name.StartsWith(info.Name + "_" + Events[i] + "_"), "Clip " + clip.name + " is not a " + info.Name + " " + Events[i] + " sound");
					t.True(clip.length > 0.15f && clip.length < 6f, clip.name + " is " + clip.length + " s long");
					t.True(clip.frequency >= 16000, clip.name + " has the rate " + clip.frequency);
				}
				if (info.Kind == BeastKind.Ironhorn)
				{
					t.Record("Ironhorn " + Events[i], clips.Length + " clips, first " + clips[0].length.ToString("0.00") + " s");
				}
			}
		}
		yield return null;
	}

	[GameTest("GuardianSounds", "Each guardian's events play its own clips (idle, roar on spotting, footsteps, attack, hit, death)", TimeoutSeconds = 120f)]
	private static IEnumerator EventsPlayTheRightClips(TestContext t)
	{
		NeedSounds(t);
		Isolate(t);
		XYZ site = Scenario.Site(t, 40, 10);
		Faction enemy = Scenario.EnemyFaction();
		if (enemy == null)
		{
			t.Skip("No enemy faction in this game");
		}
		Labor player = Scenario.Bricktron(t, Occupation.Type.Builder, new XYZ(site.x, site.y, site.z + 4));
		BeastKind[] kinds = { BeastKind.Spirestalker, BeastKind.Cindermane, BeastKind.Boulderback, BeastKind.Ironhorn };
		for (int k = 0; k < kinds.Length; k++)
		{
			BeastKind kind = kinds[k];
			BeastInfo info = Info(kind);
			XYZ at = Scenario.Walkable(new XYZ(site.x + k * 8 - 12, site.y, site.z));
			Labor beast = Beasts.Spawn(kind, enemy, at.ToVector3(), 1);
			t.NotNull(beast, "The spawned " + info.Name);
			Scenario.Adopt(t, beast);
			GameObject unit = beast.gameObject;
			yield return t.Until(() => unit != null && !unit.IsNullOrReleased() && unit.GetComponent<BeastBody>() != null, 10f, info.Name + " to get its model");
			// A voice of our own, so the test decides what the guardian does (the body's own is driven by the camera).
			BeastVoice voice = new BeastVoice(info, beast) { IgnoreGameSpeed = true };
			Vector3 pos = beast.transform.position;
			t.Record(info.Name + " at", pos.ToString());
			// A player's unit 4 blocks away: the guardian spots it at its first look and roars.
			int roars = Count(kind, GuardianSound.Roar);
			voice.Tick(pos, 0.1f, 0f, Locomotion4.State.InPlace);
			t.True(Scenario.Alive(player), "The test's bricktron died");
			t.Equal(roars + 1, Count(kind, GuardianSound.Roar), info.Name + " roar on spotting a bricktron");
			Named(t, info, "roar");
			// It does not roar again at once, also when it goes into combat.
			voice.Tick(pos, 0.1f, 0f, Locomotion4.State.CombatIdle);
			t.Equal(roars + 1, Count(kind, GuardianSound.Roar), info.Name + " roared twice in a row");
			// Footsteps: a half stride walked at 2 blocks a second.
			int steps = Count(kind, GuardianSound.Step);
			voice.Tick(pos, 3.3f, 2f, Locomotion4.State.WalkCycle);
			t.Equal(steps + 1, Count(kind, GuardianSound.Step), info.Name + " footsteps");
			Named(t, info, "step");
			// Standing still: no footsteps.
			voice.Tick(pos, 3.3f, 0f, Locomotion4.State.InPlace);
			t.Equal(steps + 1, Count(kind, GuardianSound.Step), info.Name + " footsteps standing");
			// The blow.
			int attacks = Count(kind, GuardianSound.Attack);
			voice.Tick(pos, 3.3f, 0f, Locomotion4.State.CombatCrank);
			t.Equal(attacks, Count(kind, GuardianSound.Attack), info.Name + " attack sound while cranking");
			voice.Tick(pos, 3.3f, 0f, Locomotion4.State.CombatSwing);
			t.Equal(attacks + 1, Count(kind, GuardianSound.Attack), info.Name + " attack sound at the blow");
			Named(t, info, "attack");
			// A hit: its health goes down.
			int hits = Count(kind, GuardianSound.Hit);
			float max = Scenario.Health(beast).MaxHP;
			Scenario.Health(beast).ApplyDamage(max * 0.02f, null);
			voice.Tick(pos, 3.3f, 0f, Locomotion4.State.InPlace);
			t.Equal(hits + 1, Count(kind, GuardianSound.Hit), info.Name + " hit sound when its health goes down (hp " + Scenario.Hp(beast) + " of " + max + ")");
			Named(t, info, "hit");
			// The idle call.
			int idles = Count(kind, GuardianSound.Idle);
			voice.IdleDue();
			voice.Tick(pos, 3.3f, 0f, Locomotion4.State.InPlace);
			t.Equal(idles + 1, Count(kind, GuardianSound.Idle), info.Name + " idle call");
			Named(t, info, "idle");
			// Death: the corpse appears.
			int deaths = Count(kind, GuardianSound.Death);
			Scenario.Health(beast).ApplyDamage(max * 100f, null);
			yield return t.Until(() => Count(kind, GuardianSound.Death) > deaths, 10f, info.Name + " to play its death sound");
			Named(t, info, "death");
			ModAudio.StopAll();
		}
	}

	private static void Named(TestContext t, BeastInfo info, string ev)
	{
		t.True(GuardianSounds.LastClip.name.StartsWith(info.Name + "_" + ev + "_"), "The last sound was " + GuardianSounds.LastClip.name + ", not a " + info.Name + " " + ev);
	}

	[GameTest("GuardianSounds", "A guardian's sound is a 3D sound at the guardian that fades out with distance", TimeoutSeconds = 60f, NeedsHostGame = false)]
	private static IEnumerator SoundIsPositioned(TestContext t)
	{
		NeedSounds(t);
		Isolate(t);
		BeastInfo info = Info(BeastKind.Boulderback);
		Vector3 at = new Vector3(1000f, 50f, 1000f);
		t.True(GuardianSounds.Play(info, GuardianSound.Roar, at), "The roar did not play");
		AudioSource source = null;
		for (int i = 0; i < ModAudio.VoiceLimit; i++)
		{
			ModAudio.Voice voice = ModAudio.VoiceAt(i);
			if (voice != null && voice.Busy)
			{
				source = voice.Source;
			}
		}
		t.NotNull(source, "The voice playing the roar");
		t.Equal(1f, source.spatialBlend, "spatialBlend (3D)");
		t.True(source.rolloffMode == AudioRolloffMode.Linear, "The fall off is " + source.rolloffMode);
		t.True(source.maxDistance > source.minDistance && source.maxDistance <= 120f, "Distance " + source.minDistance + " to " + source.maxDistance);
		t.True((source.transform.position - at).sqrMagnitude < 0.01f, "The voice is at " + source.transform.position + ", not at the guardian");
		t.True(source.clip != null && source.clip.name.StartsWith("Boulderback_roar_"), "The clip is " + source.clip);
		t.Record("roar", "min " + source.minDistance + ", max " + source.maxDistance + ", volume " + source.volume.ToString("0.00"));
		// Steps carry less far than a roar.
		GuardianSounds.Play(info, GuardianSound.Step, at);
		t.True(GuardianSounds.LastMaxDistance < GuardianSounds.Settings[(int)GuardianSound.Roar].MaxDistance, "Steps carry as far as a roar");
		// Heard near the listener, not heard far away (when this machine plays sound at all).
		ModAudio.StopAll();
		Camera camera = Camera.main;
		t.NotNull(camera, "The main camera");
		Vector3 eye = camera.transform.position;
		GuardianSounds.Play(info, GuardianSound.Roar, eye + camera.transform.forward * 3f);
		yield return t.Until(() => ModAudio.ListenerPresent || Listeners() > 0, 5f, "a listener");
		yield return t.Seconds(0.4f);
		float near = Level();
		ModAudio.StopAll();
		GuardianSounds.Play(info, GuardianSound.Roar, eye + camera.transform.forward * 300f);
		yield return t.Seconds(0.4f);
		float far = Level();
		t.Record("output level near / far", near.ToString("0.0000") + " / " + far.ToString("0.0000"));
		if (near < 0.0005f)
		{
			t.Log("No sound reaches the output here (no audio device?): the fall off is checked by its settings only");
		}
		else
		{
			t.True(far < near * 0.1f, "A roar 300 blocks away is as loud as one 3 blocks away: " + far + " against " + near);
		}
	}

	private static float Level()
	{
		float[] data = new float[1024];
		AudioListener.GetOutputData(data, 0);
		float sum = 0f;
		foreach (float sample in data)
		{
			sum += sample * sample;
		}
		return Mathf.Sqrt(sum / data.Length);
	}

	[GameTest("GuardianSounds", "The sounds follow the game's master and effects volume and mute, also while they play", TimeoutSeconds = 60f, NeedsHostGame = false)]
	private static IEnumerator FollowsGameVolume(TestContext t)
	{
		NeedSounds(t);
		Isolate(t);
		BeastInfo info = Info(BeastKind.Ironhorn);
		Vector3 at = new Vector3(1000f, 50f, 1000f);
		GuardianSounds.Play(info, GuardianSound.Death, at);
		AudioSource source = Busy();
		t.NotNull(source, "The voice");
		float full = source.volume;
		t.True(full > 0.3f, "Volume at full game volume: " + full);
		SoundEngine.volumeMaster = 50f;
		yield return t.Frames(3);
		t.True(Mathf.Abs(source.volume - full * 0.5f) < 0.02f, "Master 50: " + source.volume + " of " + full);
		SoundEngine.volumeSFX = 50f;
		yield return t.Frames(3);
		t.True(Mathf.Abs(source.volume - full * 0.25f) < 0.02f, "Master 50, effects 50: " + source.volume + " of " + full);
		SoundEngine.mute = true;
		yield return t.Frames(3);
		t.Equal(0f, source.volume, "Volume while muted");
		int before = ModAudio.Started;
		t.False(GuardianSounds.Play(info, GuardianSound.Roar, at), "A sound started while the game is muted");
		t.Equal(before, ModAudio.Started, "Sounds started while muted");
		SoundEngine.mute = false;
		SoundEngine.volumeMaster = 100f;
		SoundEngine.volumeSFX = 100f;
		yield return t.Frames(3);
		t.True(Mathf.Abs(source.volume - full) < 0.02f, "Back at full: " + source.volume + " of " + full);
	}

	private static AudioSource Busy()
	{
		for (int i = 0; i < ModAudio.VoiceLimit; i++)
		{
			ModAudio.Voice voice = ModAudio.VoiceAt(i);
			if (voice != null && voice.Busy)
			{
				return voice.Source;
			}
		}
		return null;
	}

	[GameTest("GuardianSounds", "The audio listener exists only while a mod sound plays, never twice, shared with the rain", TimeoutSeconds = 90f, NeedsHostGame = false)]
	private static IEnumerator ListenerLifecycle(TestContext t)
	{
		NeedSounds(t);
		Isolate(t, () => Rain.Begin(WeatherKind.None, 0f));
		yield return t.Until(() => !ModAudio.ListenerPresent, 8f, "the mod's listener to be gone");
		int others = Listeners();
		t.Record("listeners before", others);
		t.True(others <= 1, "More than one audio listener: " + others);
		// A long clip: the listener comes while it plays.
		BeastInfo info = Info(BeastKind.Cindermane);
		GuardianSounds.Play(info, GuardianSound.Death, new Vector3(1000f, 50f, 1000f));
		yield return t.Until(() => Listeners() >= 1, 5f, "an audio listener while the sound plays");
		for (int i = 0; i < 20; i++)
		{
			yield return t.Frames(5);
			t.True(Listeners() <= 1, "Two audio listeners while a sound plays: " + Listeners());
		}
		// The rain needs it too: still one.
		if (Plugin.IsEnabled(Features.Rain) && Rain.Active)
		{
			Rain.Begin(WeatherKind.Rain, 2f);
			yield return t.Until(() => RainVisuals.Instance != null && RainVisuals.Instance.SoundPlaying, 15f, "the rain to play");
			yield return t.Seconds(2.5f);
			t.True(Listeners() <= 1, "Two audio listeners with rain and a guardian sound: " + Listeners());
			Rain.Begin(WeatherKind.None, 0f);
			yield return t.Until(() => !RainVisuals.Instance.SoundPlaying, 15f, "the rain to fade out");
		}
		else
		{
			t.Log("No rain in this game: listener shared with the rain not checked");
		}
		// Everything has stopped: the listener stays a few seconds, then goes.
		ModAudio.StopAll();
		yield return t.Until(() => Listeners() == 0 && !ModAudio.ListenerPresent, 8f, "the listener to be removed");
		t.Equal(0, ModAudio.VoicesPlaying, "Voices still playing");
		// A sound right after the listener went finds a listener at once (not up to two seconds later).
		GuardianSounds.Play(info, GuardianSound.Hit, new Vector3(1000f, 50f, 1000f));
		yield return t.Frames(3);
		t.True(ModAudio.ListenerPresent || Listeners() >= 1, "No listener three frames after a sound that followed a pause");
		// And a sound that follows within the linger time keeps the same listener (no churn).
		ModAudio.StopAll();
		yield return t.Seconds(1.5f);
		t.True(ModAudio.ListenerPresent, "The listener went at once when the sound ended");
	}

	[GameTest("GuardianSounds", "At most 16 sounds play at once; a flood cuts the less important ones and allocates nothing per sound", TimeoutSeconds = 60f, NeedsHostGame = false)]
	private static IEnumerator VoiceCapAndAllocations(TestContext t)
	{
		NeedSounds(t);
		Isolate(t);
		BeastInfo info = Info(BeastKind.Spirestalker);
		Vector3 at = new Vector3(1000f, 50f, 1000f);
		// Warm up: the clips load and the voices are made once.
		for (int i = 0; i < ModAudio.VoiceLimit + 2; i++)
		{
			GuardianSounds.Play(info, GuardianSound.Step, at);
		}
		ModAudio.StopAll();
		yield return t.Frames(2);
		int dropped = ModAudio.Dropped;
		int stolen = ModAudio.Stolen;
		long before = GC.GetAllocatedBytesForCurrentThread();
		for (int i = 0; i < 200; i++)
		{
			GuardianSounds.Play(info, (GuardianSound)(i % 6), at + new Vector3(i, 0f, 0f));
			t.True(ModAudio.VoicesPlaying <= ModAudio.VoiceLimit, "More voices than the limit: " + ModAudio.VoicesPlaying);
		}
		long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
		t.Record("voices playing", ModAudio.VoicesPlaying);
		t.Record("dropped / stolen", (ModAudio.Dropped - dropped) + " / " + (ModAudio.Stolen - stolen));
		t.Record("bytes allocated by 200 sounds", allocated);
		t.True(ModAudio.VoicesPlaying <= 16, "Voices playing: " + ModAudio.VoicesPlaying);
		t.True(ModAudio.Dropped - dropped + ModAudio.Stolen - stolen > 100, "The flood neither cut nor dropped sounds");
		t.True(allocated < 20000, "200 sounds allocated " + allocated + " bytes");
		// The guardian's own per-frame tick allocates nothing.
		Labor none = null;
		BeastVoice voice = new BeastVoice(info, none);
		Vector3 pos = at;
		voice.Tick(pos, 0f, 0f, Locomotion4.State.InPlace);
		before = GC.GetAllocatedBytesForCurrentThread();
		for (int i = 0; i < 1000; i++)
		{
			voice.Tick(pos, 0f, 0f, Locomotion4.State.InPlace);
		}
		allocated = GC.GetAllocatedBytesForCurrentThread() - before;
		t.Record("bytes allocated by 1000 ticks", allocated);
		t.True(allocated < 2000, "1000 ticks allocated " + allocated + " bytes");
	}

	[GameTest("GuardianSounds", "Switching the feature off silences the guardians", TimeoutSeconds = 60f, NeedsHostGame = false)]
	private static IEnumerator FeatureOffIsSilent(TestContext t)
	{
		NeedSounds(t);
		Isolate(t, () => FeatureToggle.Set(Features.GuardianSounds, true));
		FeatureToggle.Set(Features.GuardianSounds, false);
		BeastInfo info = Info(BeastKind.Cindermane);
		int before = ModAudio.Started;
		t.False(GuardianSounds.Play(info, GuardianSound.Roar, Vector3.zero), "A sound played with the feature off");
		t.Equal(before, ModAudio.Started, "Sounds started with the feature off");
		FeatureToggle.Set(Features.GuardianSounds, true);
		t.True(GuardianSounds.Play(info, GuardianSound.Roar, Vector3.zero), "No sound with the feature back on");
		yield return null;
	}

	[GameTest("GuardianSounds", "PlayUi plays a 2D sound at the game's UI volume; any number of owners can need the listener", TimeoutSeconds = 60f, NeedsHostGame = false)]
	private static IEnumerator UiSoundAndOwners(TestContext t)
	{
		Isolate(t);
		AudioClip clip = Tone();
		t.Cleanup(() => UnityEngine.Object.Destroy(clip));
		float uiBefore = SoundEngine.volumeUI;
		t.Cleanup(() => SoundEngine.volumeUI = uiBefore);
		SoundEngine.volumeUI = 100f;
		// A 3D sound first, so the voice that PlayUi gets was left at spatialBlend 1.
		ModAudio.Play(clip, new Vector3(1000f, 50f, 1000f), ModAudio.Channel.Sfx, 1f, 1f, 10f, 60f, 0);
		ModAudio.StopAll();
		t.True(ModAudio.PlayUi(clip, 0.8f), "PlayUi did not play");
		AudioSource source = Busy();
		t.NotNull(source, "The voice");
		t.Equal(0f, source.spatialBlend, "spatialBlend of a UI sound (2D)");
		t.True(Mathf.Abs(source.volume - 0.8f) < 0.01f, "UI volume at 100: " + source.volume);
		SoundEngine.volumeUI = 50f;
		yield return t.Frames(3);
		t.True(Mathf.Abs(source.volume - 0.4f) < 0.02f, "UI volume 50: " + source.volume);
		SoundEngine.volumeUI = 100f;
		SoundEngine.volumeSFX = 0f;
		yield return t.Frames(3);
		t.True(source.volume > 0.7f, "The effects volume changed a UI sound: " + source.volume);
		SoundEngine.mute = true;
		yield return t.Frames(3);
		t.Equal(0f, source.volume, "UI volume while muted");
		// More owners than any fixed table.
		int before = ModAudio.OwnerCount;
		object[] owners = new object[20];
		for (int i = 0; i < owners.Length; i++)
		{
			owners[i] = new object();
			ModAudio.Need(owners[i], true);
		}
		t.Equal(before + 20, ModAudio.OwnerCount, "Owners after 20 more");
		foreach (object owner in owners)
		{
			ModAudio.Need(owner, false);
		}
		t.Equal(before, ModAudio.OwnerCount, "Owners after they let go");
	}
}
