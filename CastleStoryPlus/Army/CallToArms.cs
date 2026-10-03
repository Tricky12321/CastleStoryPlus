using System.Collections.Generic;
using Brix.Audio;
using Brix.Game;
using Brix.Game.AI;
using Brix.Components;
using Brix.External.Factories;
using Brix.Game.AI.InstructionType;
using Brix.Game.AI.Nodes;
using Brix.Game.Network;
using Brix.Game.Semantique;
using Brix.Lifecycle.Pooling;
using Brix.UI.Icons;
using Motus.Behavior;
using CastleStoryPlus.Workers;
using UnityEngine;

namespace CastleStoryPlus.Army;

// Call to arms with per-class quotas and separate rally points for ranged and melee soldiers.
// The client plans who goes (CallToArmsSettings holds the quotas and rally points), the server runs the orders.
internal static class CallToArms
{
	public class Orders
	{
		public GameObject Crystal;

		// Soldier job to equip, or -1 for the vanilla "best kit on any rack".
		public int Job;

		public bool HasRanged;

		public Vector3 Ranged;

		public bool HasMelee;

		public Vector3 Melee;
	}

	public const int AnyKit = -1;

	// Per-worker roles (CharacterState.CallToArmsRole): Auto, a soldier job value, or stay at work.
	public const int RoleAuto = 0;

	public const int RoleStayAtWork = 7;

	public const int FlagRanged = 1;

	public const int FlagMelee = 2;

	// Melee first, so the front line fills before the archers.
	public static readonly Occupation.Job[] SoldierJobs = new Occupation.Job[6]
	{
		Occupation.Job.Knight,
		Occupation.Job.Halberdier,
		Occupation.Job.Archer,
		Occupation.Job.Arbalist,
		Occupation.Job.Alchemist,
		Occupation.Job.Artificer
	};

	public static readonly LaborInstruction<Orders> EquipAndRally = new LaborInstruction<Orders>("CallToArmsRally", IconKeys.DropThere, (Labor labor, Orders orders) => EquipAndRallyNode(labor, orders));

	public static bool IsRanged(Occupation.Job job)
	{
		return job == Occupation.Job.Archer || job == Occupation.Job.Arbalist || job == Occupation.Job.Alchemist || job == Occupation.Job.Artificer;
	}

	// Client side: replaces the vanilla call to arms (all builders grab any kit and run to the crystal).
	// Workers with a fixed role go first, then the quotas are filled with workers on Auto.
	public static void Execute(GameObject crystal)
	{
		SoundEngine.Play("Call_To_Arm");
		if (UIGameObserver.bricktrons == null)
		{
			return;
		}
		List<Labor> builders = new List<Labor>();
		List<Labor> soldiers = new List<Labor>();
		foreach (Labor labor in UIGameObserver.bricktrons.CheckCreation())
		{
			if (labor == null)
			{
				continue;
			}
			if (labor.Profession.OccupationType != Occupation.Type.Builder)
			{
				soldiers.Add(labor);
			}
			else if (RoleOf(labor) != RoleStayAtWork)
			{
				builders.Add(labor);
			}
		}
		Dictionary<Occupation.Job, int> called = new Dictionary<Occupation.Job, int>();
		foreach (Occupation.Job job in SoldierJobs)
		{
			called[job] = 0;
		}
		foreach (Labor soldier in soldiers)
		{
			Occupation.Job job = soldier.Occupation.CurrentJob;
			if (called.ContainsKey(job))
			{
				called[job]++;
			}
		}
		Dictionary<Occupation.Job, int> kits = CountKits((builders.Count > 0) ? builders[0].faction : null);
		foreach (Labor builder in builders.ToArray())
		{
			int role = RoleOf(builder);
			if (role == RoleAuto)
			{
				continue;
			}
			// A fixed role without a free kit keeps the worker at work.
			builders.Remove(builder);
			Occupation.Job job = (Occupation.Job)role;
			if (kits.ContainsKey(job) && kits[job] > 0)
			{
				kits[job]--;
				called[job]++;
				Send(builder, crystal, role);
			}
		}
		if (CallToArmsSettings.AnyQuota)
		{
			foreach (Occupation.Job job in SoldierJobs)
			{
				int toCall = Mathf.Min(CallToArmsSettings.GetQuota(job) - called[job], kits[job]);
				for (int i = 0; i < toCall && builders.Count > 0; i++)
				{
					Labor builder = ClosestToKit(builders, job);
					builders.Remove(builder);
					Send(builder, crystal, (int)job);
				}
			}
		}
		else
		{
			bool hasRally = CallToArmsSettings.HasRanged || CallToArmsSettings.HasMelee;
			foreach (Labor builder in builders)
			{
				if (hasRally)
				{
					Send(builder, crystal, AnyKit);
				}
				else
				{
					DirectOrderProject.AddCallToArmsGoal(builder, crystal);
				}
			}
		}
		SendSoldiersToRally(soldiers);
	}

	public static int RoleOf(Labor labor)
	{
		WorkerStats stats = (labor != null) ? WorkerStats.Peek(labor.state) : null;
		return (stats != null) ? stats.CallToArmsRole : RoleAuto;
	}

	// Sets of a soldier job the player owns: complete kits on racks plus soldiers already wearing one.
	public static Dictionary<Occupation.Job, int> CountSets()
	{
		Dictionary<Occupation.Job, int> sets = CountKits(LocalFaction());
		if (UIGameObserver.bricktrons == null)
		{
			return sets;
		}
		foreach (Labor labor in UIGameObserver.bricktrons.CheckCreation())
		{
			if (labor != null && labor.Profession.OccupationType != Occupation.Type.Builder && sets.ContainsKey(labor.Occupation.CurrentJob))
			{
				sets[labor.Occupation.CurrentJob]++;
			}
		}
		return sets;
	}

	// Workers of the local player with each fixed role.
	public static Dictionary<int, int> CountRoles()
	{
		Dictionary<int, int> roles = new Dictionary<int, int>();
		if (UIGameObserver.bricktrons == null)
		{
			return roles;
		}
		foreach (Labor labor in UIGameObserver.bricktrons.CheckCreation())
		{
			if (labor == null)
			{
				continue;
			}
			int role = RoleOf(labor);
			roles[role] = (roles.TryGetValue(role, out int count) ? count : 0) + 1;
		}
		return roles;
	}

	// Gives the role to as many of the workers as there are sets left for it. Returns how many got it.
	public static int AssignRole(ICollection<Labor> workers, int role)
	{
		int available = int.MaxValue;
		if (role != RoleAuto && role != RoleStayAtWork)
		{
			Dictionary<Occupation.Job, int> sets = CountSets();
			Dictionary<int, int> roles = CountRoles();
			available = (sets.TryGetValue((Occupation.Job)role, out int total) ? total : 0) - (roles.TryGetValue(role, out int taken) ? taken : 0);
		}
		int assigned = 0;
		foreach (Labor worker in workers)
		{
			if (worker == null)
			{
				continue;
			}
			if (RoleOf(worker) == role)
			{
				assigned++;
				continue;
			}
			if (available <= 0)
			{
				continue;
			}
			available--;
			assigned++;
			CallToArmsNetwork.SendSetRole(worker.gameObject, role);
		}
		return assigned;
	}

	// Complete kits per soldier job on the faction's tool racks.
	public static Dictionary<Occupation.Job, int> CountKits(Faction faction)
	{
		Dictionary<Occupation.Job, int> kits = new Dictionary<Occupation.Job, int>();
		foreach (Occupation.Job job in SoldierJobs)
		{
			kits[job] = 0;
		}
		HashSet<GameObject> racks = BrixSingleton<AutoList>.Instance?.GetInstances(ObjetsDynamiques.Toolrack);
		if (faction == null || racks == null)
		{
			return kits;
		}
		foreach (GameObject go in racks)
		{
			if (go == null || !go.activeInHierarchy || !faction.IsSame(go))
			{
				continue;
			}
			ToolRack rack = go.GetComponent<ToolRack>();
			if (rack == null)
			{
				continue;
			}
			foreach (Occupation.Job job in SoldierJobs)
			{
				kits[job] += Kits.KitsFor(rack, job);
			}
		}
		return kits;
	}

	public static Faction LocalFaction()
	{
		if (UIGameObserver.bricktrons == null)
		{
			return null;
		}
		foreach (Labor labor in UIGameObserver.bricktrons.CheckCreation())
		{
			if (labor != null)
			{
				return labor.faction;
			}
		}
		return null;
	}

	// Picks the builder standing closest to a rack holding this job's kit.
	private static Labor ClosestToKit(List<Labor> builders, Occupation.Job job)
	{
		List<Vector3> rackPositions = new List<Vector3>();
		HashSet<GameObject> racks = BrixSingleton<AutoList>.Instance?.GetInstances(ObjetsDynamiques.Toolrack);
		if (racks != null)
		{
			foreach (GameObject go in racks)
			{
				if (go == null || !builders[0].faction.IsSame(go))
				{
					continue;
				}
				ToolRack rack = go.GetComponent<ToolRack>();
				if (rack != null && Kits.KitsFor(rack, job) > 0)
				{
					rackPositions.Add(go.transform.position);
				}
			}
		}
		Labor best = builders[0];
		float bestDistance = float.MaxValue;
		foreach (Labor builder in builders)
		{
			foreach (Vector3 position in rackPositions)
			{
				float distance = (position - builder.transform.position).sqrMagnitude;
				if (distance < bestDistance)
				{
					best = builder;
					bestDistance = distance;
				}
			}
		}
		return best;
	}

	private static void Send(Labor labor, GameObject crystal, int job)
	{
		labor.AssignToIdle();
		int flags = (CallToArmsSettings.HasRanged ? FlagRanged : 0) | (CallToArmsSettings.HasMelee ? FlagMelee : 0);
		CallToArmsNetwork.SendRally(labor.gameObject, crystal, job, CallToArmsSettings.Ranged, CallToArmsSettings.Melee, flags);
	}

	private static void SendSoldiersToRally(List<Labor> soldiers)
	{
		foreach (Labor soldier in soldiers)
		{
			bool ranged = soldier.Occupation.CurrentOccupation == Occupation.Type.Archer;
			if (ranged ? !CallToArmsSettings.HasRanged : !CallToArmsSettings.HasMelee)
			{
				continue;
			}
			soldier.AssignToIdle();
			User.LocalUser.GetComponent<UNetProjectCmd>().CallCmdAddMoveToGoal(soldier.gameObject, ranged ? CallToArmsSettings.Ranged : CallToArmsSettings.Melee, drop: false, queued: false);
		}
	}

	// Server side: equip the requested kit, then run to the rally point for the class it ended up with.
	private static Node EquipAndRallyNode(Labor labor, Orders orders)
	{
		StaticNode equip = (orders.Job == AnyKit) ? labor.GoEquipBestToolFromRack(Adjectif.accessoire) : Kits.GoEquipKitFromRack(labor, (Occupation.Job)orders.Job);
		return (StaticNode)Node.Sequence.Do(equip).Do((Labor l) => RallyNode(l, orders), labor);
	}

	private static Node RallyNode(Labor labor, Orders orders)
	{
		Occupation.Type type = labor.Occupation.CurrentOccupation;
		if (type == Occupation.Type.Archer && orders.HasRanged)
		{
			return labor.RunToward(Location.Of(orders.Ranged), precise: true, aggressive: false);
		}
		if (type == Occupation.Type.Knight && orders.HasMelee)
		{
			return labor.RunToward(Location.Of(orders.Melee), precise: true, aggressive: false);
		}
		if (orders.Crystal != null)
		{
			return labor.RunToExactly(Location.Of(orders.Crystal));
		}
		return Node.Empty;
	}
}
