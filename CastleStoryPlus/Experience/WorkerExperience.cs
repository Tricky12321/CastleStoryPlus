using Brix.Game;
using Brix.Game.AI;
using CastleStoryPlus.Giant;
using CastleStoryPlus.Workers;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.Experience;

// Level curve, bonuses and the server-side award rules for work and combat experience.
internal static class WorkerExperience
{
	public const int MaxLevel = 10;

	public const float BonusPerLevel = 0.05f;

	public const int WorkXpPerTask = 1;

	public const int CombatXpPerHit = 1;

	public const int CombatXpPerKill = 5;

	// XP needed to go from level 1 to 2; every following level needs this much more than the previous one.
	private const int FirstLevelXp = 20;

	private const int ExtraXpPerLevel = 10;

	// Total XP required to reach a level: L2 = 20, L3 = 50, L4 = 90 ... L10 = 540.
	public static int XpForLevel(int level)
	{
		int steps = Mathf.Clamp(level, 1, MaxLevel) - 1;
		return steps * FirstLevelXp + ExtraXpPerLevel * steps * (steps - 1) / 2;
	}

	public static int LevelFor(int xp)
	{
		int level = 1;
		while (level < MaxLevel && xp >= XpForLevel(level + 1))
		{
			level++;
		}
		return level;
	}

	// 0..1 progress from the current level towards the next; full at max level.
	public static float Progress(int xp)
	{
		int level = LevelFor(xp);
		if (level >= MaxLevel)
		{
			return 1f;
		}
		int from = XpForLevel(level);
		int to = XpForLevel(level + 1);
		return Mathf.Clamp01((float)(xp - from) / (float)(to - from));
	}

	public static float Multiplier(int level)
	{
		return 1f + BonusPerLevel * (Mathf.Clamp(level, 1, MaxLevel) - 1);
	}

	public static WorkerStats StatsOf(Labor labor)
	{
		return (labor != null) ? WorkerStats.Peek(labor.state) : null;
	}

	public static int WorkLevel(Labor labor)
	{
		WorkerStats stats = StatsOf(labor);
		return LevelFor((stats != null) ? stats.WorkXp : 0);
	}

	public static int CombatLevel(Labor labor)
	{
		WorkerStats stats = StatsOf(labor);
		return LevelFor((stats != null) ? stats.CombatXp : 0);
	}

	public static string LevelSuffix(Labor labor)
	{
		if (labor == null || labor.state == null)
		{
			return string.Empty;
		}
		return " [Lv " + WorkLevel(labor) + "/" + CombatLevel(labor) + "]";
	}

	public static void AwardTaskCompleted(Task work)
	{
		if (!NetworkServer.active || work == null || work.result != TaskResult.Succes || work.Status != TaskProgression.Finished)
		{
			return;
		}
		Labor labor = work.Worker as Labor;
		if (labor != null && labor.state != null)
		{
			WorkerStats.Modify(labor.state, (WorkerStats s) => s.WorkXp += WorkXpPerTask);
		}
	}

	public static void AwardCombat(GameObject attacker, GameObject victim, int amount)
	{
		if (!NetworkServer.active)
		{
			return;
		}
		Labor labor = EnemyAttackerOf(attacker, victim);
		if (labor != null)
		{
			WorkerStats.Modify(labor.state, (WorkerStats s) => s.CombatXp += amount);
		}
	}

	public static void SetWorkAnimationSpeed(Labor labor, bool working)
	{
		if (labor == null || labor.navigation == null)
		{
			return;
		}
		Locomotion4 locomotion = labor.navigation.Locomotion;
		Animator animator = (locomotion != null) ? locomotion.animator : null;
		if (animator != null)
		{
			animator.speed = GiantBricktron.SpeedFactor(labor) * (working ? Multiplier(WorkLevel(labor)) : 1f);
		}
	}

	// Only fighting the enemy counts; friendly fire and hitting neutral objects give nothing.
	private static Labor EnemyAttackerOf(GameObject attacker, GameObject victim)
	{
		if (attacker == null || victim == null || attacker == victim || !Affiliation.IsEnemy(attacker, victim))
		{
			return null;
		}
		Labor labor = attacker.GetComponent<Labor>();
		return (labor != null && labor.state != null) ? labor : null;
	}
}
