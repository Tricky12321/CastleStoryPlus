using UnityEngine;

using Brix.Game.AI;

namespace CastleStoryPlus.Army;

// Call to arms configuration of the local player.
// Quotas persist in PlayerPrefs; rally points belong to the current map and reset when another map is loaded.
internal static class CallToArmsSettings
{
	public const int MaxQuota = 99;

	private const string QuotaPrefix = "CallToArms.Quota.";

	private static object _map;

	private static bool _hasRanged;

	private static Vector3 _ranged;

	private static bool _hasMelee;

	private static Vector3 _melee;

	public static bool HasRanged
	{
		get
		{
			CheckMap();
			return _hasRanged;
		}
	}

	public static bool HasMelee
	{
		get
		{
			CheckMap();
			return _hasMelee;
		}
	}

	public static Vector3 Ranged => _ranged;

	public static Vector3 Melee => _melee;

	public static bool AnyQuota
	{
		get
		{
			foreach (Occupation.Job job in CallToArms.SoldierJobs)
			{
				if (GetQuota(job) > 0)
				{
					return true;
				}
			}
			return false;
		}
	}

	public static int GetQuota(Occupation.Job job)
	{
		return Mathf.Clamp(PlayerPrefs.GetInt(QuotaPrefix + job, 0), 0, MaxQuota);
	}

	public static void SetQuota(Occupation.Job job, int value)
	{
		PlayerPrefs.SetInt(QuotaPrefix + job, Mathf.Clamp(value, 0, MaxQuota));
		PlayerPrefs.Save();
	}

	public static void SetRally(bool ranged, Vector3 position)
	{
		CheckMap();
		if (ranged)
		{
			_hasRanged = true;
			_ranged = position;
		}
		else
		{
			_hasMelee = true;
			_melee = position;
		}
	}

	public static void ClearRally(bool ranged)
	{
		if (ranged)
		{
			_hasRanged = false;
		}
		else
		{
			_hasMelee = false;
		}
	}

	private static void CheckMap()
	{
		object map = GameParam.Map;
		if (!ReferenceEquals(map, _map))
		{
			_map = map;
			_hasRanged = false;
			_hasMelee = false;
		}
	}
}
