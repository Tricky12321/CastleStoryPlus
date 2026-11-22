using Brix.Game;
using UnityEngine.Networking;

namespace CastleStoryPlus.Multiplayer;

// The team (faction) of the player on a network connection, for commands the host carries out for that player
// (Sprint, the reset of all bricktrons); null before the player has a faction.
internal static class ConnectionTeams
{
	internal static Faction TeamOf(NetworkConnection connection)
	{
		if (connection == null || connection.playerControllers == null)
		{
			return null;
		}
		foreach (PlayerController controller in connection.playerControllers)
		{
			User user = (controller != null && controller.gameObject != null) ? controller.gameObject.GetComponent<User>() : null;
			if (user != null && user.faction != null)
			{
				return user.faction;
			}
		}
		return null;
	}
}
