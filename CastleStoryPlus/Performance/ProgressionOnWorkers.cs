#if UNITY6_PLAYER
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using Brix.Game.Components;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.Performance;

// The Unity 6 port: a storage's AnimatedProgression (the pallet's animator that drops the goods into place and raises
// the heap, for a second after its content changed) stepped its animator by hand, Animator.Update every frame, which
// works out the whole animator there and then on the main thread (about 0.43 ms a call in the bench save, one call a
// frame). Instead the animator is switched on for that second: Unity's own animation pass, later in the same frame,
// works it out with the same time step on its worker threads, together with every other animator. When the game
// stops stepping it, it is switched off again before the next pass, so it takes the same steps and ends in the same
// state. The mod's patches that look at the pallet after the step (StackingVisual, the blue crystal crate) see it one
// frame later while it plays, and the same at the end. [Performance] ProgressionOnWorkers; off: stepped as before
// (with ProgressionOffscreen).
// A vertex shader cannot take this over: the clips also switch the content's objects (crate, per-resource heaps) on and
// off, the pieces are the game's own meshes with the game's converted materials, and the mod's stacking and large
// stockpile code copy and read the pieces' transforms on the CPU.
[Feature(Features.FrameSavers, Features.FrameSaversInfo)]
[HarmonyPatch(typeof(AnimatedProgression), "Update")]
internal static class ProgressionOnWorkers
{
	private sealed class Driven
	{
		public AnimatorCullingMode Culling;

		public AnimatorUpdateMode UpdateMode;

		public bool KeepState;
	}

	private static ConfigEntry<bool> _entry;

	private static readonly Dictionary<Animator, Driven> Active = new Dictionary<Animator, Driven>();

	private static readonly AccessTools.FieldRef<AnimatedProgression, Animator> AnimatorOf = AccessTools.FieldRefAccess<AnimatedProgression, Animator>("animator");

	private static readonly MethodInfo AnimatorUpdate = AccessTools.Method(typeof(Animator), nameof(Animator.Update), new[] { typeof(float) });

	internal static bool On => Entry.Value;

	private static ConfigEntry<bool> Entry
	{
		get
		{
			if (_entry == null)
			{
				_entry = Plugin.Cfg.Bind("Performance", "ProgressionOnWorkers", true, "Unity 6 port: a storage's pallet animation (the second after its content changed) is worked out by Unity's animation pass on its worker threads instead of on the main thread. Off: stepped on the main thread as the game does.");
			}
			return _entry;
		}
	}

	private static void Enable()
	{
		_ = Entry;
		GameSession.OnLeave(Active.Clear);
	}

	private static void Disable()
	{
		foreach (KeyValuePair<Animator, Driven> pair in Active)
		{
			if (pair.Key != null)
			{
				Release(pair.Key, pair.Value);
			}
		}
		Active.Clear();
	}

	// The game's animator.Update(Time.deltaTime) goes through Step.
	private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
	{
		MethodInfo step = AccessTools.Method(typeof(ProgressionOnWorkers), nameof(Step));
		foreach (CodeInstruction instruction in instructions)
		{
			if (instruction.Calls(AnimatorUpdate))
			{
				yield return new CodeInstruction(OpCodes.Call, step).MoveLabelsFrom(instruction);
				continue;
			}
			yield return instruction;
		}
	}

	private static void Step(Animator animator, float deltaTime)
	{
		if (!On)
		{
			animator.Update(deltaTime);
			return;
		}
		if (Active.ContainsKey(animator))
		{
			return;
		}
		Driven driven = new Driven { Culling = animator.cullingMode, UpdateMode = animator.updateMode, KeepState = animator.keepAnimatorStateOnDisable };
		Active[animator] = driven;
		// Unity's pass steps it by Time.deltaTime, as the game did, also off the screen; switched off it keeps its
		// state and pose (the game leaves it off between changes and steps it by hand).
		animator.keepAnimatorStateOnDisable = true;
		animator.updateMode = AnimatorUpdateMode.Normal;
		animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
		animator.enabled = true;
	}

	// For checking the two ways against each other (CSP_PROGRESSION_LOG=1): each run's frames and the state it ends in.
	private static readonly bool LogRuns = System.Environment.GetEnvironmentVariable("CSP_PROGRESSION_LOG") == "1";

	private static readonly Dictionary<AnimatedProgression, int> Frames = new Dictionary<AnimatedProgression, int>();

	// The game stopped stepping it (its Update switched itself off): off again before the next animation pass.
	private static void Postfix(AnimatedProgression __instance)
	{
		if (LogRuns)
		{
			LogRun(__instance);
		}
		if (__instance.enabled || Active.Count == 0)
		{
			return;
		}
		Animator animator = AnimatorOf(__instance);
		if (animator != null && Active.TryGetValue(animator, out Driven driven))
		{
			Active.Remove(animator);
			Release(animator, driven);
		}
	}

	private static void LogRun(AnimatedProgression progression)
	{
		Frames.TryGetValue(progression, out int frames);
		if (progression.enabled)
		{
			Frames[progression] = frames + 1;
			return;
		}
		Frames.Remove(progression);
		Animator animator = AnimatorOf(progression);
		if (animator == null || frames == 0)
		{
			return;
		}
		AnimatorStateInfo state = animator.GetCurrentAnimatorStateInfo(0);
		Plugin.Log.LogInfo("ProgressionOnWorkers: " + (On ? "workers" : "main thread") + " frames=" + frames + " state=" + state.shortNameHash + " t=" + state.normalizedTime.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " transition=" + animator.IsInTransition(0) + " container=" + animator.GetFloat("Container").ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
	}

	private static void Release(Animator animator, Driven driven)
	{
		animator.enabled = false;
		animator.cullingMode = driven.Culling;
		animator.updateMode = driven.UpdateMode;
		animator.keepAnimatorStateOnDisable = driven.KeepState;
	}
}
#endif
