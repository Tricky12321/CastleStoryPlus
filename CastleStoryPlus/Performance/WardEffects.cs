#if UNITY6
using System.Collections.Generic;
using BepInEx.Configuration;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace CastleStoryPlus.Performance;

// Several dozen sentinel, healing and lantern wards made the frame time jump (#78). Measured on a test island with 40
// sentinel and 40 healing wards in view: the wards' scripts cost next to nothing (all their updates together under
// 0.05 ms a frame, 1 ms with 25 enemies in range), but each ward is 5 to 8 renderers, a point light and a particle
// system, and 80 of them took the draw calls from 375 to 1,157 a frame and the shadow casters from 167 to 477. Most of
// that is decoration: the healing ward's aura sphere (a 515 vertex transparent sphere), its fireflies, the gems and the
// lights.
// - The gems, the aura spheres and the particles cast no shadow (a glow does not shade the ground; the aura sphere was
//   drawn into the sun's shadow maps as a solid ball).
// - A ward further than EffectDistance blocks from the camera (the game camera zoomed out over the base) has its aura
//   sphere, particles and light switched off: they are a few pixels there. Checked every QuarterSecond for all wards
//   at once, with a margin so a ward on the border does not flicker. Its base and plates cast no shadow there either.
// - Only the MaxLights nearest wards' point lights are lit; a point light costs a pass over the pixels it reaches.
// The wards' own scripts, glow and rotation animation, sounds and attacks are not touched.
[Feature(Features.FrameSavers, Features.FrameSaversInfo)]
internal static class WardEffects
{
	private const float TickSeconds = 0.25f;

	// A ward that is out must come this much closer than EffectDistance to be switched on again.
	private const float Hysteresis = 0.9f;

	internal static ConfigEntry<bool> Enabled;

	internal static ConfigEntry<float> EffectDistance;

	internal static ConfigEntry<int> MaxLights;

	internal sealed class Entry
	{
		internal WardController Ward;

		internal Renderer[] Effects;

		// The ward's solid parts (base, plates) and the shadow casting they had.
		internal MeshRenderer[] Solids;

		internal ShadowCastingMode[] SolidModes;

		internal Light Light;

		internal bool EffectsOn = true;

		internal bool LightOn = true;

		// Whether the light was switched off by this and not by the game.
		internal bool PutOut;

		internal float Distance;
	}

	private static readonly Dictionary<WardController, Entry> Entries = new Dictionary<WardController, Entry>();

	private static readonly List<Entry> Near = new List<Entry>();

	// For the developer API and the test: wards looked after, with effects on, with a lit light.
	internal static int Count => Entries.Count;

	internal static IEnumerable<WardController> Wards => Entries.Keys;

	// Whether the ward's aura, particles and light are shown (or the ward is not looked after).
	internal static bool EffectsShown(WardController ward)
	{
		return !Entries.TryGetValue(ward, out Entry entry) || entry.EffectsOn;
	}

	// Whether the ward's point light is lit; false for a ward without a light.
	internal static bool LightShown(WardController ward)
	{
		return Entries.TryGetValue(ward, out Entry entry) && entry.Light != null && entry.Light.enabled;
	}

	internal static int EffectsOnCount { get; private set; }

	internal static int LightsOnCount { get; private set; }

	private static void Enable()
	{
		Enabled = Plugin.Cfg.Bind("WardEffects", "Enabled", true, "Wards cast no shadow from their gems, auras and particles, and switch off aura, particles and light far from the camera (see EffectDistance and MaxLights). Off: the game's own wards.");
		EffectDistance = Plugin.Cfg.Bind("WardEffects", "EffectDistance", 70f, new ConfigDescription("Blocks from the camera beyond which a ward's aura, particles and light are switched off (a few pixels there). 0 keeps them on at any distance.", new AcceptableValueRange<float>(0f, 1000f)));
		MaxLights = Plugin.Cfg.Bind("WardEffects", "MaxLights", 12, new ConfigDescription("How many wards' point lights are lit at once, the nearest to the camera first. 0 lights all of them.", new AcceptableValueRange<int>(0, 200)));
		GameSession.OnLeave(Entries.Clear);
		Plugin.Root.AddComponent<WardEffectsTick>();
	}

	internal static void Register(WardController ward)
	{
		if (!Enabled.Value || ward == null || Entries.ContainsKey(ward))
		{
			return;
		}
		List<Renderer> effects = new List<Renderer>();
		List<MeshRenderer> solids = new List<MeshRenderer>();
		List<ShadowCastingMode> modes = new List<ShadowCastingMode>();
		foreach (ParticleSystemRenderer particles in ward.GetComponentsInChildren<ParticleSystemRenderer>(true))
		{
			particles.shadowCastingMode = ShadowCastingMode.Off;
			effects.Add(particles);
		}
		foreach (MeshRenderer mesh in ward.GetComponentsInChildren<MeshRenderer>(true))
		{
			// The healing ward's aura ("Healing Aura/Sphere") and every ward's gem: lit shapes that cast no shadow.
			if (mesh.name == "Gem" || (mesh.name == "Sphere" && mesh.transform.parent != null && mesh.transform.parent.name.Contains("Aura")))
			{
				mesh.shadowCastingMode = ShadowCastingMode.Off;
				if (mesh.name == "Sphere")
				{
					effects.Add(mesh);
				}
			}
			else if (mesh.shadowCastingMode != ShadowCastingMode.Off)
			{
				solids.Add(mesh);
				modes.Add(mesh.shadowCastingMode);
			}
		}
		Entries[ward] = new Entry
		{
			Ward = ward,
			Effects = effects.ToArray(),
			Solids = solids.ToArray(),
			SolidModes = modes.ToArray(),
			Light = ward.GetComponentInChildren<Light>(true)
		};
	}

	internal static void Unregister(WardController ward)
	{
		if (ward != null && Entries.TryGetValue(ward, out Entry entry))
		{
			Switch(entry, true, true);
			Entries.Remove(ward);
		}
	}

	private static void Switch(Entry entry, bool effects, bool light)
	{
		if (entry.EffectsOn != effects)
		{
			entry.EffectsOn = effects;
			foreach (Renderer renderer in entry.Effects)
			{
				if (renderer != null)
				{
					renderer.enabled = effects;
				}
			}
			// Far from the camera the ward's shadow is a few pixels too, and a ward's parts are drawn into the sun's
			// shadow maps once per cascade.
			for (int i = 0; i < entry.Solids.Length; i++)
			{
				if (entry.Solids[i] != null)
				{
					entry.Solids[i].shadowCastingMode = effects ? entry.SolidModes[i] : ShadowCastingMode.Off;
				}
			}
		}
		entry.LightOn = light;
		if (entry.Light == null)
		{
			return;
		}
		// The game switches a ward's light itself (a healing ward lights up when it heals): a light that is wanted
		// off is switched off again whenever it is found on, and one is only switched back on when this put it out.
		if (!light)
		{
			if (entry.Light.enabled)
			{
				entry.Light.enabled = false;
				entry.PutOut = true;
			}
		}
		else if (entry.PutOut)
		{
			entry.PutOut = false;
			entry.Light.enabled = true;
		}
	}

	internal static void Tick()
	{
		Camera camera = Camera.main;
		if (!Enabled.Value)
		{
			foreach (Entry entry in Entries.Values)
			{
				Switch(entry, true, true);
			}
			Entries.Clear();
		}
		if (camera == null || Entries.Count == 0)
		{
			EffectsOnCount = Entries.Count;
			LightsOnCount = Entries.Count;
			return;
		}
		Vector3 eye = camera.transform.position;
		float far = EffectDistance.Value;
		int maxLights = MaxLights.Value;
		Near.Clear();
		List<WardController> gone = null;
		foreach (KeyValuePair<WardController, Entry> pair in Entries)
		{
			if (pair.Key == null)
			{
				gone = gone ?? new List<WardController>();
				gone.Add(pair.Key);
				continue;
			}
			Entry entry = pair.Value;
			entry.Distance = Vector3.Distance(eye, pair.Key.transform.position);
			bool effects = far <= 0f || entry.Distance < (entry.EffectsOn ? far : far * Hysteresis);
			Switch(entry, effects, entry.LightOn);
			if (effects)
			{
				Near.Add(entry);
			}
		}
		if (gone != null)
		{
			foreach (WardController ward in gone)
			{
				Entries.Remove(ward);
			}
		}
		Near.Sort((a, b) => a.Distance.CompareTo(b.Distance));
		int effectsOn = 0;
		int lightsOn = 0;
		foreach (KeyValuePair<WardController, Entry> pair in Entries)
		{
			if (!pair.Value.EffectsOn)
			{
				Switch(pair.Value, false, false);
			}
			else
			{
				effectsOn++;
			}
		}
		for (int i = 0; i < Near.Count; i++)
		{
			bool lit = maxLights <= 0 || i < maxLights;
			Switch(Near[i], true, lit);
			if (lit)
			{
				lightsOn++;
			}
		}
		EffectsOnCount = effectsOn;
		LightsOnCount = lightsOn;
	}
}

internal sealed class WardEffectsTick : MonoBehaviour
{
	private float _next;

	private void Update()
	{
		float now = Time.unscaledTime;
		if (now < _next)
		{
			return;
		}
		_next = now + 0.25f;
		WardEffects.Tick();
	}
}

[Feature(Features.FrameSavers, Features.FrameSaversInfo)]
[HarmonyPatch(typeof(WardController), nameof(WardController.OnRequested))]
internal static class WardEffectsRegisterPatch
{
	private static void Postfix(WardController __instance)
	{
		WardEffects.Register(__instance);
	}
}

[Feature(Features.FrameSavers, Features.FrameSaversInfo)]
[HarmonyPatch(typeof(WardController), nameof(WardController.ResetComponent))]
internal static class WardEffectsUnregisterPatch
{
	private static void Prefix(WardController __instance)
	{
		WardEffects.Unregister(__instance);
	}
}
#endif
