using System;
using System.Collections.Generic;

namespace CastleStoryPlus.WorkerAI;

// A planned trip: Amount items of one resource from the storage Source to the storage Target (ConsolidationPile.Id).
internal struct ConsolidationPlan
{
	public int Source;

	public int Target;

	public int Amount;
}

// Which storage an idle worker empties and where the load goes, as plain functions of numbers (no game, no Unity), so
// they can be tested without the game (CastleStoryPlus.UnitTests). StockpileConsolidation reads the storages and
// carries the plan out.
// - Source: a storage something may be taken out of (not locked, nothing on its way in, not given a load lately), the
//   one being emptied first, else the one holding the least of the resource, then the nearest.
// - Target: one that holds clearly more of the resource than the source (MinLead), is not being emptied itself, is
//   within MaxDistance of the source and has room for a worthwhile trip (MinTripItems, or the source's last items);
//   of those the one with the least room left (the fullest), then the one holding most, then the nearest.
// A source is emptied over as many trips as it takes: the target only gets fuller and the source emptier, so the lead
// only grows and no load is ever carried back.
internal static class ConsolidationRules
{
	// Whether a storage holding targetHeld of a resource is clearly fuller than one holding sourceHeld (a lead below
	// 1 counts as 1: never into a storage holding the same or less).
	public static bool IsClearlyFuller(int targetHeld, int sourceHeld, int minLead)
	{
		int lead = Math.Max(1, minLead);
		return (long)targetHeld - sourceHeld >= lead;
	}

	public static bool CanGive(ConsolidationPile pile, ConsolidationSettings settings)
	{
		return pile != null && !pile.Locked && pile.ClaimedIn <= 0 && pile.ReceivedAgo >= settings.ReceivedSeconds && pile.Available > 0;
	}

	public static bool CanTake(ConsolidationPile pile)
	{
		return pile != null && !pile.Refuses && !pile.Draining && pile.ClaimedOut <= 0 && pile.FreeRoom > 0;
	}

	// Items one trip carries: what may be taken, what the worker's hands hold and what the target still takes.
	public static int TripAmount(int available, int hand, int room)
	{
		return Math.Max(0, Math.Min(available, Math.Min(hand, room)));
	}

	// The smallest trip worth the walk: MinTripItems, or fewer when that is all the source has (its last items) or
	// all the hands hold.
	public static int SmallestTrip(int available, int hand, int minTripItems)
	{
		return Math.Max(1, Math.Min(Math.Max(1, minTripItems), Math.Min(available, hand)));
	}

	public static float Distance(float ax, float ay, float az, float bx, float by, float bz)
	{
		float dx = ax - bx;
		float dy = ay - by;
		float dz = az - bz;
		return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
	}

	public static float Distance(ConsolidationPile a, ConsolidationPile b)
	{
		return Distance(a.X, a.Y, a.Z, b.X, b.Y, b.Z);
	}

	// Below 0 when a is the better source: the one being emptied, then the one holding less, then the nearer.
	public static int CompareSources(ConsolidationPile a, ConsolidationPile b, float workerX, float workerY, float workerZ)
	{
		if (a.Draining != b.Draining)
		{
			return a.Draining ? -1 : 1;
		}
		if (a.Held != b.Held)
		{
			return a.Held.CompareTo(b.Held);
		}
		return Distance(a.X, a.Y, a.Z, workerX, workerY, workerZ).CompareTo(Distance(b.X, b.Y, b.Z, workerX, workerY, workerZ));
	}

	// The best target for the source at piles[source] (an index into piles), or -1; amount: the trip's items.
	public static int BestTarget(IList<ConsolidationPile> piles, int source, int hand, ConsolidationSettings settings, out int amount)
	{
		amount = 0;
		if (piles == null || source < 0 || source >= piles.Count)
		{
			return -1;
		}
		ConsolidationPile from = piles[source];
		int available = from.Available;
		if (available <= 0 || hand <= 0)
		{
			return -1;
		}
		int smallest = SmallestTrip(available, hand, settings.MinTripItems);
		int best = -1;
		float bestDistance = 0f;
		for (int i = 0; i < piles.Count; i++)
		{
			ConsolidationPile to = piles[i];
			if (i == source || !CanTake(to) || !IsClearlyFuller(to.Held, from.Held, settings.MinLead))
			{
				continue;
			}
			float distance = Distance(from, to);
			if (distance > settings.MaxDistance)
			{
				continue;
			}
			int trip = TripAmount(available, hand, to.FreeRoom);
			if (trip < smallest)
			{
				continue;
			}
			if (best >= 0 && !IsBetterTarget(to, distance, piles[best], bestDistance))
			{
				continue;
			}
			best = i;
			bestDistance = distance;
			amount = trip;
		}
		return best;
	}

	// The least room left first (the fullest storage), then the one holding most, then the nearer.
	private static bool IsBetterTarget(ConsolidationPile a, float aDistance, ConsolidationPile b, float bDistance)
	{
		if (a.FreeRoom != b.FreeRoom)
		{
			return a.FreeRoom < b.FreeRoom;
		}
		if (a.Held != b.Held)
		{
			return a.Held > b.Held;
		}
		return aDistance < bDistance;
	}

	// The trip for a worker at (workerX, workerY, workerZ) whose hands take hand items of the resource, among the
	// storages holding it. False when there is nothing worth moving.
	public static bool Plan(IList<ConsolidationPile> piles, int hand, float workerX, float workerY, float workerZ, ConsolidationSettings settings, out ConsolidationPlan plan)
	{
		plan = default(ConsolidationPlan);
		if (piles == null || piles.Count < 2 || hand <= 0 || settings == null)
		{
			return false;
		}
		int bestSource = -1;
		int bestTarget = -1;
		int bestAmount = 0;
		for (int i = 0; i < piles.Count; i++)
		{
			ConsolidationPile from = piles[i];
			if (!CanGive(from, settings))
			{
				continue;
			}
			if (Distance(from.X, from.Y, from.Z, workerX, workerY, workerZ) > settings.MaxWalk)
			{
				continue;
			}
			if (bestSource >= 0 && CompareSources(from, piles[bestSource], workerX, workerY, workerZ) >= 0)
			{
				continue;
			}
			int target = BestTarget(piles, i, hand, settings, out int amount);
			if (target < 0)
			{
				continue;
			}
			bestSource = i;
			bestTarget = target;
			bestAmount = amount;
		}
		if (bestSource < 0)
		{
			return false;
		}
		plan.Source = piles[bestSource].Id;
		plan.Target = piles[bestTarget].Id;
		plan.Amount = bestAmount;
		return true;
	}
}
