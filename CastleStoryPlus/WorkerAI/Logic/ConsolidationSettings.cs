namespace CastleStoryPlus.WorkerAI;

// The numbers stockpile consolidation goes by ([Storage.Consolidation] in the config; the defaults here are the
// config's defaults).
internal sealed class ConsolidationSettings
{
	public const int DefaultMinLead = 2;

	public const float DefaultMaxDistance = 40f;

	public const float DefaultMaxWalk = 64f;

	public const int DefaultMinTripItems = 2;

	public const float DefaultReceivedSeconds = 300f;

	public const float DefaultClaimSeconds = 60f;

	public const float DefaultScanSeconds = 5f;

	public const float DefaultDrainSeconds = 120f;

	// A resource only goes into a storage holding at least this many more of it than the one it is taken from, so two
	// storages of about the same fill never swap loads back and forth.
	public int MinLead = DefaultMinLead;

	// Farthest (blocks, straight line) a load is carried from one storage to the other.
	public float MaxDistance = DefaultMaxDistance;

	// Farthest (blocks, straight line) a worker walks to the storage it empties.
	public float MaxWalk = DefaultMaxWalk;

	// Fewest items a trip carries, unless it takes the last ones out of the storage (no walk for a single stone
	// while more stays behind).
	public int MinTripItems = DefaultMinTripItems;

	// A storage given a consolidation load is not emptied for this long.
	public float ReceivedSeconds = DefaultReceivedSeconds;

	// A planned move holds its items and room this long at most (in case its task ends without letting go).
	public float ClaimSeconds = DefaultClaimSeconds;

	// After a look that found nothing to move, the team's storages are not looked at again for this long.
	public float ScanSeconds = DefaultScanSeconds;

	// A storage that was being emptied stays first in line this long after its last trip.
	public float DrainSeconds = DefaultDrainSeconds;
}
