namespace CastleStoryPlus.Pathfinding;

// A run of route steps of one state: the points from First to Last (both included) are drawn as one line.
internal sealed class RouteRun
{
	public int First;

	public int Last;

	public RouteStepState State;
}
