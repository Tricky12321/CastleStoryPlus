using System.Collections.Generic;
using Brix.Game.Components;
using Brix.Game.Rules;
using CastleStoryPlus.Core;
using UnityEngine;

namespace CastleStoryPlus.Respawn;

// Shows under a dead bricktron's firefly what is left before it respawns:
// "flying home, ~N s", "respawn in N s" (the absorption in the crystal), "needs N energy" or "respawning".
[Feature(Features.RespawnStatus, Features.RespawnStatusInfo)]
internal class RespawnStatus : MonoBehaviour
{
	private const float RefreshSeconds = 0.25f;

	private readonly Dictionary<Firefly, string> _shown = new Dictionary<Firefly, string>();

	private readonly List<Firefly> _gone = new List<Firefly>();

	private float _nextRefresh;

	private static void Enable()
	{
		Plugin.Root.AddComponent<RespawnStatus>();
	}

	private void Update()
	{
		if (Time.unscaledTime < _nextRefresh)
		{
			return;
		}
		_nextRefresh = Time.unscaledTime + RefreshSeconds;
		foreach (Firefly firefly in Firefly._allFireflies)
		{
			if (firefly == null)
			{
				continue;
			}
			string status = StatusOf(firefly);
			_shown.TryGetValue(firefly, out string shown);
			if (status == shown)
			{
				continue;
			}
			FireflyOverheadDisplayDriver driver = firefly.GetComponent<FireflyOverheadDisplayDriver>();
			if (driver == null || driver.overheadDisplay == null || !driver.overheadDisplay.NameTag.IsSpawned)
			{
				continue;
			}
			_shown[firefly] = status;
			if (string.IsNullOrEmpty(status))
			{
				driver.overheadDisplay.NameTag.Get.NoContext();
			}
			else
			{
				driver.overheadDisplay.NameTag.Get.SetContext(status);
			}
		}
		// Pooled fireflies leave the list when released; forget them so a reused one is shown again.
		foreach (Firefly firefly in _shown.Keys)
		{
			if (firefly == null || !Firefly._allFireflies.Contains(firefly))
			{
				_gone.Add(firefly);
			}
		}
		foreach (Firefly firefly in _gone)
		{
			_shown.Remove(firefly);
		}
		_gone.Clear();
	}

	// Null when the firefly is not waiting to respawn.
	private static string StatusOf(Firefly firefly)
	{
		if (!firefly.IsNamed || !firefly.LaborHasOccupation)
		{
			return null;
		}
		if (firefly.IsSpawning)
		{
			return "respawning";
		}
		Transform parent = firefly.transform.parent;
		FireflyNest nest = (parent != null) ? parent.GetComponentInParent<FireflyNest>() : null;
		if (nest == null || nest.faction != firefly.faction)
		{
			if (firefly.movementMode == FireflyMovementMode.Travelling)
			{
				int seconds = Mathf.CeilToInt(Vector3.Distance(firefly.transform.position, firefly.movementTarget) / Firefly._travelTopSpeed);
				return "flying home, ~" + seconds + " s";
			}
			return "flying home";
		}
		// Clients never see the capture on the server, so they start their own timer when it arrives home.
		if (!firefly.captureTimer.running && !firefly.captureTimer.Check())
		{
			firefly.captureTimer.Start();
		}
		if (!firefly.captureTimer.Check())
		{
			return "respawn in " + Mathf.CeilToInt(firefly.captureTimer.RemainingSeconds()) + " s";
		}
		int required = RespawnCost.Enabled ? RespawnCost.Energy : GeneralRules.Main.GetHomeCrystalSpawnRequiredEnergy(nest.GetActiveOrSpawnableCharacterCount());
		int missing = required - nest.GetAvailablePurifiedEnergy();
		return (missing > 0) ? ("needs " + missing + " energy") : "respawning";
	}
}
