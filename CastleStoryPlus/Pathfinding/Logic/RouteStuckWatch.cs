namespace CastleStoryPlus.Pathfinding;

// Whether a unit on a route is stuck: it has a route and has not moved MoveEpsilon blocks for StuckSeconds. One per
// unit, kept by the overlay; the clock comes in as an argument.
internal sealed class RouteStuckWatch
{
	private bool _started;

	private float _x;

	private float _y;

	private float _z;

	private float _since;

	public bool Stuck { get; private set; }

	// The unit's position now; false for a unit without a route (the watch starts again when it has one).
	public bool Update(float now, float x, float y, float z, bool hasRoute)
	{
		if (!hasRoute)
		{
			_started = false;
			Stuck = false;
			return false;
		}
		float dx = x - _x;
		float dy = y - _y;
		float dz = z - _z;
		if (!_started || dx * dx + dy * dy + dz * dz >= RouteOverlayRules.MoveEpsilon * RouteOverlayRules.MoveEpsilon)
		{
			_started = true;
			_x = x;
			_y = y;
			_z = z;
			_since = now;
			Stuck = false;
			return false;
		}
		Stuck = now - _since >= RouteOverlayRules.StuckSeconds;
		return Stuck;
	}
}
