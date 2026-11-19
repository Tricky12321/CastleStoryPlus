using System.Collections.Generic;
using CastleStoryPlus.WorkerAI;
using NUnit.Framework;

namespace CastleStoryPlus.UnitTests.WorkerAI;

[TestFixture]
public class ConsolidationRulesTests
{
	private static ConsolidationPile Pile(int id, int held, int room, float x = 0f)
	{
		return new ConsolidationPile
		{
			Id = id,
			Held = held,
			Room = room,
			X = x
		};
	}

	private static bool Plan(List<ConsolidationPile> piles, int hand, out ConsolidationPlan plan, ConsolidationSettings settings = null)
	{
		return ConsolidationRules.Plan(piles, hand, 0f, 0f, 0f, settings ?? new ConsolidationSettings(), out plan);
	}

	[Test]
	public void TheSmallestGoesIntoTheFullest()
	{
		List<ConsolidationPile> piles = new List<ConsolidationPile> { Pile(0, 20, 5), Pile(1, 3, 20), Pile(2, 10, 15) };
		Assert.That(Plan(piles, 4, out ConsolidationPlan plan), Is.True);
		Assert.That(plan.Source, Is.EqualTo(1));
		Assert.That(plan.Target, Is.EqualTo(0));
		Assert.That(plan.Amount, Is.EqualTo(3));
	}

	[Test]
	public void ASourceLargerThanOneLoadIsMovedATripAtATime()
	{
		// The old rule moved a source only when all of it fitted in one carry: 12 stones never moved.
		List<ConsolidationPile> piles = new List<ConsolidationPile> { Pile(0, 12, 40), Pile(1, 30, 40) };
		Assert.That(Plan(piles, 4, out ConsolidationPlan plan), Is.True);
		Assert.That(plan.Source, Is.EqualTo(0));
		Assert.That(plan.Target, Is.EqualTo(1));
		Assert.That(plan.Amount, Is.EqualTo(4));
	}

	[Test]
	public void ASourceIsEmptiedOverSeveralTripsAndWorkers()
	{
		ConsolidationPile source = Pile(0, 12, 40);
		ConsolidationPile target = Pile(1, 30, 40);
		List<ConsolidationPile> piles = new List<ConsolidationPile> { source, target };
		int trips = 0;
		while (Plan(piles, 4, out ConsolidationPlan plan))
		{
			// A second worker plans while the first is on its way: the claims are counted.
			source.ClaimedOut += plan.Amount;
			trips++;
			Assert.That(trips, Is.LessThanOrEqualTo(3));
		}
		Assert.That(trips, Is.EqualTo(3));
		Assert.That(source.Available, Is.EqualTo(0));
	}

	[Test]
	public void AStorageWithLoadsOnTheWayInIsNoSource()
	{
		ConsolidationPile a = Pile(0, 5, 40);
		ConsolidationPile b = Pile(1, 30, 40);
		a.ClaimedIn = 2;
		Assert.That(ConsolidationRules.CanGive(a, new ConsolidationSettings()), Is.False);
		Assert.That(Plan(new List<ConsolidationPile> { a, b }, 4, out _), Is.False);
	}

	[Test]
	public void AStorageBeingEmptiedIsNoTarget()
	{
		ConsolidationPile a = Pile(0, 5, 40);
		ConsolidationPile b = Pile(1, 30, 40);
		b.ClaimedOut = 4;
		Assert.That(ConsolidationRules.CanTake(b), Is.False);
		b.ClaimedOut = 0;
		b.Draining = true;
		Assert.That(ConsolidationRules.CanTake(b), Is.False);
		// The only fuller storage is being emptied: nothing goes into it.
		Assert.That(Plan(new List<ConsolidationPile> { a, b }, 4, out _), Is.False);
		b.Draining = false;
		Assert.That(Plan(new List<ConsolidationPile> { a, b }, 4, out _), Is.True);
	}

	[Test]
	public void ADrainingSourceIsEmptiedFirst()
	{
		ConsolidationPile a = Pile(0, 3, 40);
		ConsolidationPile b = Pile(1, 8, 40);
		ConsolidationPile c = Pile(2, 30, 40);
		b.Draining = true;
		Assert.That(Plan(new List<ConsolidationPile> { a, b, c }, 4, out ConsolidationPlan plan), Is.True);
		Assert.That(plan.Source, Is.EqualTo(1));
		Assert.That(plan.Target, Is.EqualTo(2));
	}

	[TestCase(10, 10, 2, false)]
	[TestCase(11, 10, 2, false)]
	[TestCase(12, 10, 2, true)]
	[TestCase(10, 10, 0, false)]
	[TestCase(11, 10, 0, true)]
	[TestCase(9, 10, 1, false)]
	[TestCase(0, 0, 1, false)]
	[TestCase(int.MaxValue, 0, 100, true)]
	public void ClearlyFuller(int target, int source, int lead, bool fuller)
	{
		Assert.That(ConsolidationRules.IsClearlyFuller(target, source, lead), Is.EqualTo(fuller));
	}

	[Test]
	public void AboutAsFullAreLeftAlone()
	{
		List<ConsolidationPile> piles = new List<ConsolidationPile> { Pile(0, 10, 40), Pile(1, 11, 40) };
		Assert.That(Plan(piles, 4, out _), Is.False);
		Assert.That(Plan(piles, 4, out _, new ConsolidationSettings { MinLead = 1 }), Is.True);
	}

	[Test]
	public void NothingIsEverCarriedBack()
	{
		// Run a full consolidation of three storages trip by trip, then no move from the fuller back to the emptier.
		ConsolidationPile a = Pile(0, 7, 40);
		ConsolidationPile b = Pile(1, 9, 40);
		ConsolidationPile c = Pile(2, 15, 20);
		List<ConsolidationPile> piles = new List<ConsolidationPile> { a, b, c };
		int total = a.Held + b.Held + c.Held;
		ConsolidationSettings settings = new ConsolidationSettings { ReceivedSeconds = 0f };
		List<int> sources = new List<int>();
		for (int round = 0; round < 50 && Plan(piles, 4, out ConsolidationPlan plan, settings); round++)
		{
			ConsolidationPile from = piles[plan.Source];
			ConsolidationPile to = piles[plan.Target];
			Assert.That(to.Held, Is.GreaterThan(from.Held), "Round " + round);
			from.Held -= plan.Amount;
			to.Held += plan.Amount;
			to.Room -= plan.Amount;
			sources.Add(plan.Source);
		}
		Assert.That(a.Held + b.Held + c.Held, Is.EqualTo(total));
		Assert.That(Plan(piles, 4, out _, settings), Is.False, "Still moving after 50 trips");
		// a (the smallest) was emptied first, then b, both into c (the least room left).
		Assert.That(sources[0], Is.EqualTo(0));
		Assert.That(a.Held, Is.EqualTo(0));
		Assert.That(b.Held, Is.EqualTo(0));
		Assert.That(c.Held, Is.EqualTo(total));
		Assert.That(c.Room, Is.GreaterThanOrEqualTo(0));
	}

	[Test]
	public void ATargetTooFarIsNotUsed()
	{
		List<ConsolidationPile> piles = new List<ConsolidationPile> { Pile(0, 3, 40, 0f), Pile(1, 30, 40, 41f) };
		Assert.That(Plan(piles, 4, out _, new ConsolidationSettings { MaxDistance = 40f, MaxWalk = 100f }), Is.False);
		Assert.That(Plan(piles, 4, out _, new ConsolidationSettings { MaxDistance = 41f, MaxWalk = 100f }), Is.True);
	}

	[Test]
	public void ASourceTooFarFromTheWorkerIsNotUsed()
	{
		List<ConsolidationPile> piles = new List<ConsolidationPile> { Pile(0, 3, 40, 70f), Pile(1, 30, 40, 75f) };
		Assert.That(Plan(piles, 4, out _, new ConsolidationSettings { MaxWalk = 64f }), Is.False);
		Assert.That(Plan(piles, 4, out _, new ConsolidationSettings { MaxWalk = 70f }), Is.True);
	}

	[Test]
	public void ANearerTargetWinsAmongEquallyFullOnes()
	{
		List<ConsolidationPile> piles = new List<ConsolidationPile> { Pile(0, 3, 40, 0f), Pile(1, 30, 10, 30f), Pile(2, 30, 10, 10f) };
		Assert.That(Plan(piles, 4, out ConsolidationPlan plan), Is.True);
		Assert.That(plan.Target, Is.EqualTo(2));
	}

	[Test]
	public void ATripOfASingleItemIsNotWorthTheWalk()
	{
		// The fullest target only takes 1 more while the source has 10: the next fullest with room for a trip.
		List<ConsolidationPile> piles = new List<ConsolidationPile> { Pile(0, 10, 40), Pile(1, 40, 1), Pile(2, 30, 10) };
		Assert.That(Plan(piles, 4, out ConsolidationPlan plan), Is.True);
		Assert.That(plan.Target, Is.EqualTo(2));
		Assert.That(plan.Amount, Is.EqualTo(4));
		// Only that one: nothing.
		piles.RemoveAt(2);
		Assert.That(Plan(piles, 4, out _), Is.False);
	}

	[Test]
	public void TheLastItemOfASourceIsMovedAlone()
	{
		List<ConsolidationPile> piles = new List<ConsolidationPile> { Pile(0, 1, 40), Pile(1, 30, 1) };
		Assert.That(Plan(piles, 4, out ConsolidationPlan plan), Is.True);
		Assert.That(plan.Amount, Is.EqualTo(1));
	}

	[TestCase(10, 4, 2, 2)]
	[TestCase(1, 4, 2, 1)]
	[TestCase(10, 1, 2, 1)]
	[TestCase(10, 4, 0, 1)]
	[TestCase(0, 4, 2, 1)]
	[TestCase(10, 4, 6, 4)]
	public void SmallestTrip(int available, int hand, int minTrip, int smallest)
	{
		Assert.That(ConsolidationRules.SmallestTrip(available, hand, minTrip), Is.EqualTo(smallest));
	}

	[TestCase(10, 4, 20, 4)]
	[TestCase(3, 4, 20, 3)]
	[TestCase(10, 4, 2, 2)]
	[TestCase(-1, 4, 2, 0)]
	[TestCase(10, 0, 2, 0)]
	public void TripAmount(int available, int hand, int room, int amount)
	{
		Assert.That(ConsolidationRules.TripAmount(available, hand, room), Is.EqualTo(amount));
	}

	[Test]
	public void ReservedItemsAreNeverMoved()
	{
		ConsolidationPile source = Pile(0, 5, 40);
		source.Reserved = 5;
		List<ConsolidationPile> piles = new List<ConsolidationPile> { source, Pile(1, 30, 40) };
		Assert.That(Plan(piles, 4, out _), Is.False);
		source.Reserved = 3;
		Assert.That(Plan(piles, 4, out ConsolidationPlan plan), Is.True);
		Assert.That(plan.Amount, Is.EqualTo(2));
		Assert.That(source.Available, Is.EqualTo(2));
	}

	[Test]
	public void LockedAndRefusingStoragesAreSkipped()
	{
		ConsolidationPile source = Pile(0, 5, 40);
		ConsolidationPile target = Pile(1, 30, 40);
		List<ConsolidationPile> piles = new List<ConsolidationPile> { source, target };
		source.Locked = true;
		Assert.That(Plan(piles, 4, out _), Is.False);
		source.Locked = false;
		target.Refuses = true;
		Assert.That(Plan(piles, 4, out _), Is.False);
		target.Refuses = false;
		Assert.That(Plan(piles, 4, out _), Is.True);
	}

	[Test]
	public void AStorageJustGivenALoadIsNotEmptied()
	{
		ConsolidationPile source = Pile(0, 5, 40);
		List<ConsolidationPile> piles = new List<ConsolidationPile> { source, Pile(1, 30, 40) };
		source.ReceivedAgo = 10f;
		Assert.That(Plan(piles, 4, out _, new ConsolidationSettings { ReceivedSeconds = 300f }), Is.False);
		source.ReceivedAgo = 300f;
		Assert.That(Plan(piles, 4, out _, new ConsolidationSettings { ReceivedSeconds = 300f }), Is.True);
	}

	[Test]
	public void ClaimedRoomCounts()
	{
		ConsolidationPile target = Pile(1, 30, 4);
		target.ClaimedIn = 3;
		List<ConsolidationPile> piles = new List<ConsolidationPile> { Pile(0, 5, 40), target };
		Assert.That(target.FreeRoom, Is.EqualTo(1));
		Assert.That(Plan(piles, 4, out _), Is.False);
		target.ClaimedIn = 4;
		Assert.That(ConsolidationRules.CanTake(target), Is.False);
	}

	[Test]
	public void EmptyOrBrokenInput()
	{
		Assert.That(ConsolidationRules.Plan(null, 4, 0f, 0f, 0f, new ConsolidationSettings(), out _), Is.False);
		Assert.That(Plan(new List<ConsolidationPile>(), 4, out _), Is.False);
		Assert.That(Plan(new List<ConsolidationPile> { Pile(0, 5, 40) }, 4, out _), Is.False);
		Assert.That(Plan(new List<ConsolidationPile> { Pile(0, 5, 40), Pile(1, 30, 40) }, 0, out _), Is.False);
		Assert.That(ConsolidationRules.Plan(new List<ConsolidationPile> { Pile(0, 5, 40), Pile(1, 30, 40) }, 4, 0f, 0f, 0f, null, out _), Is.False);
		Assert.That(ConsolidationRules.BestTarget(new List<ConsolidationPile> { Pile(0, 5, 40) }, 3, 4, new ConsolidationSettings(), out int amount), Is.EqualTo(-1));
		Assert.That(amount, Is.EqualTo(0));
	}

	[Test]
	public void DistanceIsStraightLine()
	{
		Assert.That(ConsolidationRules.Distance(0f, 0f, 0f, 3f, 4f, 0f), Is.EqualTo(5f).Within(1e-5f));
		ConsolidationPile low = Pile(0, 3, 40);
		ConsolidationPile high = Pile(1, 30, 40);
		high.Y = 30f;
		high.Z = 40f;
		Assert.That(ConsolidationRules.Distance(low, high), Is.EqualTo(50f).Within(1e-4f));
		Assert.That(Plan(new List<ConsolidationPile> { low, high }, 4, out _, new ConsolidationSettings { MaxDistance = 49f }), Is.False);
	}
}
