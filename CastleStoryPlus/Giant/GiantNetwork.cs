using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Network;
using CastleStoryPlus.Core;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.Giant;

// UNET command on the player's UNetProjectCmd that asks the server for an upgrade to a giant bricktron.
// Registered by hand, like the game's weaved commands (see CallToArmsNetwork).
[Feature(Features.GiantBricktron, Features.GiantBricktronInfo)]
internal static class GiantNetwork
{
	private const int UpgradeHash = 1129595221;

	private static void Enable()
	{
		NetworkBehaviour.RegisterCommandDelegate(typeof(UNetProjectCmd), UpgradeHash, InvokeUpgrade);
	}

	// On the host the result is returned at once; a client gets null and sees the result through the synced tier.
	public static string SendUpgrade(Labor upgrade, Labor sacrifice)
	{
		UNetProjectCmd cmd = User.LocalUser.GetComponent<UNetProjectCmd>();
		if (cmd.isServer)
		{
			return Upgrade(cmd, upgrade.gameObject, sacrifice.gameObject);
		}
		NetworkWriter writer = new NetworkWriter();
		writer.Write((short)0);
		writer.Write((short)5);
		writer.WritePackedUInt32((uint)UpgradeHash);
		writer.Write(cmd.GetComponent<NetworkIdentity>().netId);
		writer.Write(upgrade.gameObject);
		writer.Write(sacrifice.gameObject);
		cmd.SendCommandInternal(writer, 0, "CmdGiantBricktronUpgrade");
		return null;
	}

	private static void InvokeUpgrade(NetworkBehaviour obj, NetworkReader reader)
	{
		if (!NetworkServer.active)
		{
			return;
		}
		Upgrade((UNetProjectCmd)obj, reader.ReadGameObject(), reader.ReadGameObject());
	}

	private static string Upgrade(UNetProjectCmd cmd, GameObject upgradeGO, GameObject sacrificeGO)
	{
		if (upgradeGO == null || sacrificeGO == null || !Affiliation.IsOwnedBy(upgradeGO, cmd.gameObject) || !Affiliation.IsOwnedBy(sacrificeGO, cmd.gameObject))
		{
			return "Both workers must be yours.";
		}
		string problem = GiantBricktron.Upgrade(upgradeGO.GetComponent<Labor>(), sacrificeGO.GetComponent<Labor>());
		if (problem != null)
		{
			Plugin.Log.LogInfo("giant bricktron upgrade refused: " + problem);
		}
		return problem;
	}
}
