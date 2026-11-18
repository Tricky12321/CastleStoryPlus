namespace CastleStoryPlus.Upgrades;

// What starts a sprint.
internal enum SprintTrigger
{
	// The player pressed the sprint key for the selected bricktrons.
	Key,
	// A melee fighter sent at an enemy or on an attack order.
	Charge,
	// A fighter running to safety (Retreat).
	Flee
}

// The decisions of the Sprint research (tiers, cooldown, speed composition, auto triggers, command checks) as plain
// functions of numbers, with no call into the game or Unity, so they can be tested without the game
// (CastleStoryPlus.Tests). Sprint holds the state and the game side.
internal static class SprintRules
{
	public const int MaxTier = 3;

	// The most units one sprint command (key press of a client) may name; the game's selection holds 500 at most.
	public const int MaxCommandUnits = 500;

	// Tier 1, 2, 3: how long the sprint lasts and how long it must cool down, as a share of the settings' values.
	private static readonly float[] DurationScale = { 0f, 1f, 1.3f, 1.6f };

	private static readonly float[] CooldownScale = { 0f, 1f, 0.75f, 0.5f };

	private static int ClampTier(int tier)
	{
		return (tier < 0) ? 0 : ((tier > MaxTier) ? MaxTier : tier);
	}

	// Seconds the sprint lasts at the tier (0 without the research).
	public static float Duration(int tier, float baseSeconds)
	{
		return baseSeconds * DurationScale[ClampTier(tier)];
	}

	// Seconds from the end of a sprint until the next may start (0 without the research).
	public static float Cooldown(int tier, float baseSeconds)
	{
		return baseSeconds * CooldownScale[ClampTier(tier)];
	}

	// How many times faster a sprinting unit is: a bonus of 50 percent is 1.5.
	public static float Factor(float bonusPercent)
	{
		return 1f + ((bonusPercent < 0f) ? 0f : bonusPercent) / 100f;
	}

	// The speed with every bonus on it. Each bonus is a plain factor and the result is their product, so the order
	// the game's getter patches run in does not matter: light boots (a share, 0.1 is +10%), road and sprint (factors).
	public static float Compose(float speed, float lightBoots, float roadFactor, float sprintFactor)
	{
		return speed * (1f + lightBoots) * roadFactor * sprintFactor;
	}

	// Whether a unit may start a sprint now. Enemies never sprint; a sprint cannot start inside a sprint or before its
	// cooldown is over (readyAt is when the unit may sprint again).
	public static bool CanStart(int tier, bool enemy, bool sprinting, float now, float readyAt)
	{
		return tier > 0 && !enemy && !sprinting && now >= readyAt;
	}

	// The time the unit may sprint again when a sprint starts now: after the sprint, the cooldown.
	public static float ReadyAt(float now, int tier, float durationSeconds, float cooldownSeconds)
	{
		return now + Duration(tier, durationSeconds) + Cooldown(tier, cooldownSeconds);
	}

	// Whether a sprint starts by itself. The key always does; a charge only for a melee fighter with some way to go
	// (a short dash is not worth the cooldown); a flight always.
	public static bool ShouldAutoStart(SprintTrigger trigger, bool meleeFighter, float distance, float minChargeDistance)
	{
		switch (trigger)
		{
			case SprintTrigger.Key:
			case SprintTrigger.Flee:
				return true;
			case SprintTrigger.Charge:
				return meleeFighter && distance >= minChargeDistance;
			default:
				return false;
		}
	}

	// Whether the host carries out a sprint command that a client sent: it must come from a known player and name at
	// least one and at most MaxCommandUnits units.
	public static bool AcceptCommand(bool playerKnown, int units)
	{
		return playerKnown && units > 0 && units <= MaxCommandUnits;
	}

	// Each unit of a command must be a bricktron of the sending player's team.
	public static bool AcceptUnit(bool isBricktron, bool sameTeam)
	{
		return isBricktron && sameTeam;
	}
}
