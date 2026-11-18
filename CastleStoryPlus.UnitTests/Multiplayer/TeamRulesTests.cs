using CastleStoryPlus.Multiplayer;
using NUnit.Framework;

namespace CastleStoryPlus.UnitTests.Multiplayer;

public class TeamRulesTests
{
	// Conquest Versus: two to four teams of any size, four team slots, the game wants one player per team.
	private static bool Versus(params int[] teams)
	{
		return TeamRules.IsValid(teams, 4, 2, 4, 1, -1);
	}

	[Test]
	public void SavedThreePlayerTeamsAreValidInVersus()
	{
		Assert.That(Versus(0, 0, 1), Is.True);
	}

	[Test]
	public void OneSharedTeamIsNotValidInVersus()
	{
		Assert.That(Versus(0, 0), Is.False);
	}

	[Test]
	public void EmptyTeamSlotsDoNotCount()
	{
		Assert.That(Versus(1, 2), Is.True);
	}

	[Test]
	public void TwoPlayersInOneTeamAreValidWhenTheModeAllowsOneTeam()
	{
		// Co-op: one to four teams (the reporter's two-player save).
		Assert.That(TeamRules.IsValid(new[] { 0, 0 }, 4, 1, 4, 1, -1), Is.True);
	}

	[Test]
	public void TeamOutOfRangeIsNotValid()
	{
		Assert.That(Versus(0, 4), Is.False);
		Assert.That(Versus(0, -1), Is.False);
	}

	[Test]
	public void TooManyTeamsAreNotValid()
	{
		Assert.That(TeamRules.IsValid(new[] { 0, 1, 2 }, 4, 1, 2, 1, -1), Is.False);
	}

	[Test]
	public void TeamSizeLimitsApply()
	{
		Assert.That(TeamRules.IsValid(new[] { 0, 0, 1 }, 4, 2, 4, 1, 1), Is.False);
		Assert.That(TeamRules.IsValid(new[] { 0, 0, 1, 1 }, 4, 2, 4, 2, 2), Is.True);
		Assert.That(TeamRules.IsValid(new[] { 0, 1, 1 }, 4, 2, 4, 2, -1), Is.False);
	}

	[Test]
	public void NoLimitsAcceptsAnyAssignedTeams()
	{
		Assert.That(TeamRules.IsValid(new[] { 3, 3, 3 }, 4, -1, -1, -1, -1), Is.True);
	}
}
