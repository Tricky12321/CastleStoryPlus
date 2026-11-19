namespace CastleStoryPlus.WorkerAI;

// One storage (a stockpile or a weapon stand) as stockpile consolidation sees it for one resource: plain numbers read
// from the game by StockpileConsolidation, so the choice of a move (ConsolidationRules) can be tested without the game.
internal sealed class ConsolidationPile
{
	// The caller's handle for the storage (an index into its own list).
	public int Id;

	// Items of the resource the storage holds.
	public int Held;

	// Of those, items other workers are on their way to take (resource reservations of tasks, blueprints, workshops).
	public int Reserved;

	// Items consolidation workers are already on their way to take out of it.
	public int ClaimedOut;

	// Items consolidation workers are already on their way to bring to it.
	public int ClaimedIn;

	// Room the storage has for one more item of the resource (its real room, filters and full columns counted).
	public int Room;

	// Nothing may be taken out of it (others reserve its kits, the game reserves it, workers cannot grab from it).
	public bool Locked;

	// Nothing may be brought to it (a warehouse, a stand reserved by soldiers).
	public bool Refuses;

	// It is being emptied (a move out of it was planned a moment ago): it is emptied first and is no target.
	public bool Draining;

	// Seconds since a consolidation load was last brought to it (float.MaxValue: never).
	public float ReceivedAgo = float.MaxValue;

	public float X;

	public float Y;

	public float Z;

	// Items that may be taken out now.
	public int Available
	{
		get
		{
			int available = Held - Reserved - ClaimedOut;
			return (available > 0) ? available : 0;
		}
	}

	// Room left once the loads on their way have arrived.
	public int FreeRoom
	{
		get
		{
			int room = Room - ClaimedIn;
			return (room > 0) ? room : 0;
		}
	}
}
