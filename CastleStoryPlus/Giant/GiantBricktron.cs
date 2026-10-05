using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.AI.Damage;
using Brix.Game.Bricktron;
using Brix.Game.Components;
using Brix.Game.Semantique;
using CastleStoryPlus.Core;
using CastleStoryPlus.Workers;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.Giant;

// 3x bricktron: one worker upgraded with 50 dark crystals (DarkCrystals; without that feature the energy of 1.5 new
// bricktrons, which the home crystal hardly ever keeps, as it turns energy into new bricktrons) and a second worker
// that is sacrificed. It is drawn twice as big but still walks in one voxel and counts as one bricktron. Everything it
// does runs 3x as fast (animations, so work, climbing and attacks, plus walking and running speed), and it takes
// a third of the damage (3x health). Carrying is not tripled: at 3x speed it already hauls as much as three workers.
// Allowed: one 3x bricktron per 5 bricktrons. The tier is stored in WorkerStats (saved and synced).
[Feature(Features.GiantBricktron, Features.GiantBricktronInfo)]
internal class GiantBricktron : MonoBehaviour
{
	private class Applied
	{
		public Labor Labor;

		public Transform Scaled;

		public Vector3 OriginalScale;
	}

	private const float ScanSeconds = 0.5f;

	internal static ConfigEntry<float> Speed;

	internal static ConfigEntry<float> Health;

	internal static ConfigEntry<float> Size;

	internal static ConfigEntry<float> CostMultiplier;

	internal static ConfigEntry<int> DarkCrystalCost;

	internal static ConfigEntry<int> BricktronsPerGiant;

	private static bool _enabled;

	private static readonly Dictionary<CharacterState, Applied> AppliedTo = new Dictionary<CharacterState, Applied>();

	private static readonly List<CharacterState> Pending = new List<CharacterState>();

	private float _nextScan;

	private static void Enable()
	{
		Speed = Plugin.Cfg.Bind("GiantBricktron", "Speed", 3f, "Speed of everything a 3x bricktron does (work, walking, climbing, attacks).");
		Health = Plugin.Cfg.Bind("GiantBricktron", "Health", 3f, "Health of a 3x bricktron (it takes 1/Health of the damage).");
		Size = Plugin.Cfg.Bind("GiantBricktron", "Size", 2f, "Drawn size of a 3x bricktron. It still walks in one voxel.");
		CostMultiplier = Plugin.Cfg.Bind("GiantBricktron", "CostMultiplier", 1.5f, "Upgrade cost without the DarkCrystals feature: the energy of this many new bricktrons, paid from the home crystal (plus one sacrificed worker).");
		DarkCrystalCost = Plugin.Cfg.Bind("GiantBricktron", "DarkCrystalCost", 50, "Upgrade cost with the DarkCrystals feature: dark crystals taken from the stockpiles (plus one sacrificed worker).");
		BricktronsPerGiant = Plugin.Cfg.Bind("GiantBricktron", "BricktronsPerGiant", 5, "One 3x bricktron allowed per this many bricktrons.");
		_enabled = true;
		WorkerStats.Changed += (CharacterState state) => Pending.Add(state);
		Plugin.Root.AddComponent<GiantBricktron>();
	}

	public static bool IsGiant(Labor labor)
	{
		if (!_enabled || labor == null)
		{
			return false;
		}
		WorkerStats stats = WorkerStats.Peek(labor.state);
		return stats != null && stats.Tier > 0;
	}

	public static float SpeedFactor(Labor labor)
	{
		return IsGiant(labor) ? Speed.Value : 1f;
	}

	// ---- rules (checked by the window on the client and again by the server)

	public static bool IsAlive(Labor labor)
	{
		if (labor.IsNullOrReleased() || labor.state == null)
		{
			return false;
		}
		Locomotion4 locomotion = (labor.navigation != null) ? labor.navigation.Locomotion : null;
		return locomotion == null || !locomotion.IsCorpse;
	}

	public static Faction FactionOf(Labor labor)
	{
		AffiliationComponent affiliation = labor.GetComponent<AffiliationComponent>();
		return (affiliation != null) ? affiliation.faction : null;
	}

	public static int CountGiants(Faction faction)
	{
		int count = 0;
		foreach (KeyValuePair<CharacterState, WorkerStats> entry in WorkerStats.Entries)
		{
			if (entry.Value.Tier <= 0 || entry.Key == null)
			{
				continue;
			}
			Labor labor = entry.Key.GetComponent<Labor>();
			if (labor != null && IsAlive(labor) && FactionOf(labor) == faction)
			{
				count++;
			}
		}
		return count;
	}

	// The sacrificed worker is gone before the new 3x bricktron is counted.
	public static int Limit(Faction faction)
	{
		return Mathf.Max(0, faction.UnitCount - 1) / Mathf.Max(1, BricktronsPerGiant.Value);
	}

	public static FireflyNest HomeNest(Faction faction)
	{
		foreach (FireflyNest nest in UnityEngine.Object.FindObjectsOfType<FireflyNest>())
		{
			if (nest != null && nest.isHome && nest.faction == faction && nest.mainRecepteur != null)
			{
				return nest;
			}
		}
		return null;
	}

	public static bool UsesDarkCrystals => Economy.DarkCrystals.IsOn();

	// The cost line of the window.
	public static string CostText(Faction faction)
	{
		if (UsesDarkCrystals)
		{
			return "Cost: " + DarkCrystalCost.Value + " dark crystals (" + Economy.DarkCrystals.Stock(faction) + " in the stockpiles) + the sacrificed worker";
		}
		FireflyNest nest = HomeNest(faction);
		return (nest != null) ? ("Cost: " + Cost(nest) + " energy (" + AvailableEnergy(nest) + " in the crystal) + the sacrificed worker") : "Cost: no home crystal";
	}

	public static int Cost(FireflyNest nest)
	{
		return Mathf.CeilToInt(nest.NewFireflyRequiredEnergy * CostMultiplier.Value);
	}

	// Energy of the fireflies waiting in the crystal that are not a dead worker on its way back.
	public static int AvailableEnergy(FireflyNest nest)
	{
		int energy = 0;
		foreach (IDescriptor item in nest.mainRecepteur.StoredItems)
		{
			GameObject go = item.GameObject;
			if (go == null || !go.CompareTag("Firefly"))
			{
				continue;
			}
			Firefly firefly = go.GetComponent<Firefly>();
			if (firefly != null && !firefly.IsNamed)
			{
				energy += firefly.pureEnergy;
			}
		}
		return energy;
	}

	// The worker that keeps the most experience is upgraded; the other one is sacrificed.
	public static void Order(Labor a, Labor b, out Labor upgrade, out Labor sacrifice)
	{
		WorkerStats sa = WorkerStats.Peek(a.state);
		WorkerStats sb = WorkerStats.Peek(b.state);
		int xpA = (sa != null) ? sa.WorkXp + sa.CombatXp : 0;
		int xpB = (sb != null) ? sb.WorkXp + sb.CombatXp : 0;
		upgrade = (xpB > xpA) ? b : a;
		sacrifice = (upgrade == a) ? b : a;
	}

	public static string NameOf(Labor labor)
	{
		Nom nom = labor.GetComponent<Nom>();
		return (nom != null) ? nom.GetNom() : "worker";
	}

	// Null when the upgrade is allowed, else the reason.
	public static string Check(Labor upgrade, Labor sacrifice, out FireflyNest nest, out int cost)
	{
		nest = null;
		cost = 0;
		if (upgrade == null || sacrifice == null || upgrade == sacrifice)
		{
			return "Select exactly 2 workers.";
		}
		if (!IsAlive(upgrade) || !IsAlive(sacrifice))
		{
			return "Both workers must be alive.";
		}
		if (IsGiant(upgrade) || IsGiant(sacrifice))
		{
			return "A 3x bricktron cannot be upgraded or sacrificed.";
		}
		Faction faction = FactionOf(upgrade);
		if (faction == null || FactionOf(sacrifice) != faction)
		{
			return "Both workers must be yours.";
		}
		int giants = CountGiants(faction);
		int limit = Limit(faction);
		if (giants >= limit)
		{
			return "Limit reached: " + giants + "/" + limit + " (one per " + BricktronsPerGiant.Value + " bricktrons).";
		}
		if (UsesDarkCrystals)
		{
			cost = DarkCrystalCost.Value;
			int stock = Economy.DarkCrystals.Stock(faction);
			return (stock < cost) ? ("Not enough dark crystals in the stockpiles: " + stock + "/" + cost + ".") : null;
		}
		nest = HomeNest(faction);
		if (nest == null)
		{
			return "No home crystal found.";
		}
		cost = Cost(nest);
		int available = AvailableEnergy(nest);
		if (available < cost)
		{
			return "Not enough energy in the crystal: " + available + "/" + cost + ".";
		}
		return null;
	}

	// ---- server

	public static string Upgrade(Labor upgrade, Labor sacrifice)
	{
		if (!NetworkServer.active)
		{
			return "Only the host can upgrade.";
		}
		string problem = Check(upgrade, sacrifice, out FireflyNest nest, out int cost);
		if (problem != null)
		{
			return problem;
		}
		if (UsesDarkCrystals)
		{
			if (!Economy.DarkCrystals.Consume(FactionOf(upgrade), cost))
			{
				return "Not enough dark crystals in the stockpiles.";
			}
		}
		else
		{
			nest.ConsumeUnnamedFirefliesUpTo(cost);
		}
		GiantSacrifice.Mark(sacrifice.gameObject);
		BricktronDamageReceiver receiver = sacrifice.GetComponent<BricktronDamageReceiver>();
		if (receiver != null)
		{
			receiver.Kill();
		}
		WorkerStats.Modify(upgrade.state, (WorkerStats s) => s.Tier = 1);
		Plugin.Log.LogInfo("3x bricktron: " + NameOf(upgrade) + " upgraded for " + cost + (UsesDarkCrystals ? " dark crystals, " : " energy, ") + NameOf(sacrifice) + " sacrificed");
		return null;
	}

	// ---- looks (every peer)

	private void Update()
	{
		if (Pending.Count > 0)
		{
			foreach (CharacterState state in Pending.ToArray())
			{
				Refresh(state);
			}
			Pending.Clear();
		}
		if (Time.unscaledTime < _nextScan)
		{
			return;
		}
		_nextScan = Time.unscaledTime + ScanSeconds;
		// Loaded saves set the tier without an event, and pooled bricktrons lose it when reused.
		foreach (KeyValuePair<CharacterState, WorkerStats> entry in WorkerStats.Entries)
		{
			if (entry.Value.Tier > 0 && entry.Key != null && !AppliedTo.ContainsKey(entry.Key))
			{
				Pending.Add(entry.Key);
			}
		}
		foreach (CharacterState state in AppliedTo.Keys)
		{
			Pending.Add(state);
		}
	}

	private static void Refresh(CharacterState state)
	{
		Labor labor = (state != null) ? state.GetComponent<Labor>() : null;
		bool giant = labor != null && IsAlive(labor) && IsGiant(labor);
		bool applied = AppliedTo.TryGetValue(state, out Applied current);
		if (giant && !applied)
		{
			Apply(state, labor);
		}
		else if (!giant && applied)
		{
			Restore(state, current);
		}
	}

	private static void Apply(CharacterState state, Labor labor)
	{
		Transform scaled = ModelRoot(labor);
		Applied applied = new Applied
		{
			Labor = labor,
			Scaled = scaled,
			OriginalScale = (scaled != null) ? scaled.localScale : Vector3.one
		};
		if (scaled != null)
		{
			scaled.localScale = applied.OriginalScale * Size.Value;
		}
		foreach (SkinnedMeshRenderer renderer in labor.GetComponentsInChildren<SkinnedMeshRenderer>(includeInactive: true))
		{
			renderer.updateWhenOffscreen = true;
		}
		SetAnimatorSpeed(labor, Speed.Value);
		AppliedTo[state] = applied;
	}

	private static void Restore(CharacterState state, Applied applied)
	{
		AppliedTo.Remove(state);
		if (applied.Scaled != null)
		{
			applied.Scaled.localScale = applied.OriginalScale;
		}
		if (applied.Labor != null)
		{
			SetAnimatorSpeed(applied.Labor, 1f);
		}
	}

	private static void SetAnimatorSpeed(Labor labor, float speed)
	{
		Locomotion4 locomotion = (labor.navigation != null) ? labor.navigation.Locomotion : null;
		if (locomotion != null && locomotion.animator != null)
		{
			locomotion.animator.speed = speed;
		}
	}

	// The top of the skeleton (the child of the bricktron that holds the skinned mesh's bones). Scaling it
	// scales the drawn body and what it holds, but not the bricktron's own colliders.
	private static Transform ModelRoot(Labor labor)
	{
		SkinnedMeshRenderer renderer = labor.GetComponentInChildren<SkinnedMeshRenderer>(includeInactive: true);
		Transform bone = (renderer != null) ? renderer.rootBone : null;
		if (bone == null)
		{
			return null;
		}
		while (bone.parent != null && bone.parent != labor.transform)
		{
			bone = bone.parent;
		}
		return (bone.parent == labor.transform) ? bone : null;
	}
}
