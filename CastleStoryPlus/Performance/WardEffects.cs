#if UNITY6
using System.Collections.Generic;
using BepInEx.Configuration;
using Brix.Game.Components.Wards;
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
//   sphere, its looping particles and its light hidden, and its base and plates cast no shadow: they are a few pixels
//   there. Checked every quarter second for all wards at once, with a margin so a ward on the border does not flicker.
// - Only the MaxLights nearest wards' lights are visible. A light is hidden by its culling mask, not by switching it
//   off: the game switches the lights itself (ActivateAtNight, once a second, lights a ward at night and not by day),
//   and only the lights the game has on count toward MaxLights.
// The wards' own scripts, glow and rotation animation, sounds, attacks and their fire and overheat particles are not
// touched (those only lose their shadow).
// Setting [Performance] WardEffects off puts everything back as the game has it; setting it on looks up the wards again.
[Feature(Features.FrameSavers, Features.FrameSaversInfo)]
internal static class WardEffects
{
	// A ward that is out must come this much closer than EffectDistance to be switched on again.
	private const float Hysteresis = 0.9f;

	internal static ConfigEntry<bool> Enabled;

	internal static ConfigEntry<float> EffectDistance;

	internal static ConfigEntry<int> MaxLights;

	internal sealed class Entry
	{
		internal WardController Ward;

		// Looping particles and the aura sphere: hidden far from the camera.
		internal Renderer[] Effects;

		// Everything this changed the shadow casting of, and what it was.
		internal Renderer[] Shaded;

		internal ShadowCastingMode[] ShadedModes;

		// Of Shaded, whether the renderer is a solid part (base, plates) that casts a shadow near the camera only.
		internal bool[] Solid;

		internal Light Light;

		internal int LightMask;

		internal bool Near = true;

		internal bool LightHidden;

		internal float Distance;
	}

	private static readonly Dictionary<WardController, Entry> Entries = new Dictionary<WardController, Entry>();

	private static readonly List<Entry> NearLit = new List<Entry>();

	private static bool _wasEnabled = true;

	internal static IEnumerable<WardController> Wards => Entries.Keys;

	// Whether the ward's aura and particles are shown (also for a ward not looked after).
	internal static bool EffectsShown(WardController ward)
	{
		return !Entries.TryGetValue(ward, out Entry entry) || entry.Near;
	}

	// Whether the ward's light lights anything: on in the game's eyes and not hidden.
	internal static bool LightShown(WardController ward)
	{
		return Entries.TryGetValue(ward, out Entry entry) && entry.Light != null && entry.Light.enabled && !entry.LightHidden;
	}

	private static void Enable()
	{
		Enabled = Plugin.Cfg.Bind("Performance", "WardEffects", true, "Wards cast no shadow from their gems, auras and particles, and hide aura, particles, light and shadow far from the camera (see WardEffectsDistance and WardMaxLights). Off: the game's own wards.");
		EffectDistance = Plugin.Cfg.Bind("Performance", "WardEffectsDistance", 70f, new ConfigDescription("Blocks from the camera beyond which a ward's aura, particles, light and shadow are hidden (a few pixels there). 0 keeps them at any distance.", new AcceptableValueRange<float>(0f, 1000f)));
		MaxLights = Plugin.Cfg.Bind("Performance", "WardMaxLights", 12, new ConfigDescription("How many wards' lights are visible at once, the nearest to the camera first. 0 shows all of them.", new AcceptableValueRange<int>(0, 200)));
		GameSession.OnLeave(Entries.Clear);
		Plugin.Root.AddComponent<WardEffectsTick>();
	}

	internal static void Register(WardController ward)
	{
		if (!Enabled.Value || ward == null || Entries.ContainsKey(ward))
		{
			return;
		}
		// The particles the ward's attack plays (fire, overheat) stay visible: only their shadow goes.
		HashSet<ParticleSystem> played = new HashSet<ParticleSystem>();
		WardAttack attack = ward.GetComponent<WardAttack>();
		if (attack != null)
		{
			played.Add(attack.fireParticles);
			played.Add(attack.overheatParticles);
		}
		List<Renderer> effects = new List<Renderer>();
		List<Renderer> shaded = new List<Renderer>();
		List<ShadowCastingMode> modes = new List<ShadowCastingMode>();
		List<bool> solid = new List<bool>();
		foreach (ParticleSystemRenderer particles in ward.GetComponentsInChildren<ParticleSystemRenderer>(true))
		{
			Shade(particles, false, shaded, modes, solid);
			if (!played.Contains(particles.GetComponent<ParticleSystem>()))
			{
				effects.Add(particles);
			}
		}
		foreach (MeshRenderer mesh in ward.GetComponentsInChildren<MeshRenderer>(true))
		{
			bool aura = mesh.name == "Sphere" && mesh.transform.parent != null && mesh.transform.parent.name.Contains("Aura");
			if (mesh.name == "Gem" || aura)
			{
				Shade(mesh, false, shaded, modes, solid);
				if (aura)
				{
					effects.Add(mesh);
				}
			}
			else if (mesh.shadowCastingMode != ShadowCastingMode.Off)
			{
				Shade(mesh, true, shaded, modes, solid);
			}
		}
		Light light = ward.GetComponentInChildren<Light>(true);
		Entries[ward] = new Entry
		{
			Ward = ward,
			Effects = effects.ToArray(),
			Shaded = shaded.ToArray(),
			ShadedModes = modes.ToArray(),
			Solid = solid.ToArray(),
			Light = light,
			LightMask = light != null ? light.cullingMask : 0
		};
	}

	// Remembers the renderer's shadow casting; the glow parts lose it for good, the solid parts when far.
	private static void Shade(Renderer renderer, bool isSolid, List<Renderer> shaded, List<ShadowCastingMode> modes, List<bool> solid)
	{
		shaded.Add(renderer);
		modes.Add(renderer.shadowCastingMode);
		solid.Add(isSolid);
		if (!isSolid)
		{
			renderer.shadowCastingMode = ShadowCastingMode.Off;
		}
	}

	internal static void Unregister(WardController ward)
	{
		if (ward != null && Entries.TryGetValue(ward, out Entry entry))
		{
			Restore(entry);
			Entries.Remove(ward);
		}
	}

	// Everything as the game has it.
	private static void Restore(Entry entry)
	{
		foreach (Renderer renderer in entry.Effects)
		{
			if (renderer != null)
			{
				renderer.enabled = true;
			}
		}
		for (int i = 0; i < entry.Shaded.Length; i++)
		{
			if (entry.Shaded[i] != null)
			{
				entry.Shaded[i].shadowCastingMode = entry.ShadedModes[i];
			}
		}
		if (entry.Light != null)
		{
			entry.Light.cullingMask = entry.LightMask;
		}
	}

	private static void SetNear(Entry entry, bool near)
	{
		if (entry.Near == near)
		{
			return;
		}
		entry.Near = near;
		foreach (Renderer renderer in entry.Effects)
		{
			if (renderer != null)
			{
				renderer.enabled = near;
			}
		}
		// Far from the camera the ward's shadow is a few pixels too, and a ward's parts are drawn into the sun's
		// shadow maps once per cascade.
		for (int i = 0; i < entry.Shaded.Length; i++)
		{
			if (entry.Solid[i] && entry.Shaded[i] != null)
			{
				entry.Shaded[i].shadowCastingMode = near ? entry.ShadedModes[i] : ShadowCastingMode.Off;
			}
		}
	}

	private static void HideLight(Entry entry, bool hide)
	{
		entry.LightHidden = hide;
		if (entry.Light != null)
		{
			int mask = hide ? 0 : entry.LightMask;
			if (entry.Light.cullingMask != mask)
			{
				entry.Light.cullingMask = mask;
			}
		}
	}

	internal static void Tick()
	{
		if (!Enabled.Value)
		{
			if (_wasEnabled)
			{
				_wasEnabled = false;
				foreach (Entry entry in Entries.Values)
				{
					Restore(entry);
				}
				Entries.Clear();
			}
			return;
		}
		if (!_wasEnabled)
		{
			_wasEnabled = true;
			foreach (WardController ward in Object.FindObjectsOfType<WardController>())
			{
				Register(ward);
			}
		}
		Camera camera = Camera.main;
		if (camera == null || Entries.Count == 0)
		{
			return;
		}
		Vector3 eye = camera.transform.position;
		float far = EffectDistance.Value;
		int maxLights = MaxLights.Value;
		NearLit.Clear();
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
			bool near = far <= 0f || entry.Distance < (entry.Near ? far : far * Hysteresis);
			SetNear(entry, near);
			if (near && entry.Light != null && entry.Light.enabled)
			{
				NearLit.Add(entry);
			}
			else
			{
				HideLight(entry, !near);
			}
		}
		if (gone != null)
		{
			foreach (WardController ward in gone)
			{
				Entries.Remove(ward);
			}
		}
		NearLit.Sort((a, b) => a.Distance.CompareTo(b.Distance));
		for (int i = 0; i < NearLit.Count; i++)
		{
			HideLight(NearLit[i], maxLights > 0 && i >= maxLights);
		}
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
