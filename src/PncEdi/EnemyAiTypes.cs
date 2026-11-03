using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PncEdi;

// The one list of enemy AI component classes, and the only place a new one has to be added.
//
// Every helper that has to touch "whatever AI this enemy runs" used to spell the set out for
// itself - keep-alive, reactivation, the near-player hider, the inactive autofix, the vanilla
// grab-hiding probe. Six copies of a six-item list agreed only by hand, and 0.3.1 shipped a
// seventh class: BrawlerEnemyAI, the Black Serpent. It dropped out of every one of those lists
// silently, so a serpent that survived its own grab was never reactivated and stood inert for
// the rest of the run (§76). EnemyAiAudit had been printing the GAP line for it since the port.
//
// MimicEnemy is deliberately NOT here: it is a chest with no state machine and no pathfinding,
// and every caller that wants it handles it separately.
internal static class EnemyAiTypes
{
	internal static readonly Type[] All = new Type[7]
	{
		typeof(EnemyAI),
		typeof(ChargingEnemyAI),
		typeof(SpinningEnemyAI),
		typeof(ProjectileEnemyAI),
		typeof(DragonEnemyAI),
		typeof(ProximityDragonEnemyAI),
		typeof(BrawlerEnemyAI)
	};

	// Every AI component on this object. A prefab carries one in practice, but nothing enforces
	// that and the callers were all written to tolerate several.
	internal static List<MonoBehaviour> On(GameObject go)
	{
		List<MonoBehaviour> found = new List<MonoBehaviour>();
		if (go == null)
		{
			return found;
		}
		for (int i = 0; i < All.Length; i++)
		{
			MonoBehaviour ai = (MonoBehaviour)(object)go.GetComponent(All[i]);
			if (!(ai == null))
			{
				found.Add(ai);
			}
		}
		return found;
	}

	internal static MonoBehaviour First(GameObject go)
	{
		if (go == null)
		{
			return null;
		}
		for (int i = 0; i < All.Length; i++)
		{
			MonoBehaviour ai = (MonoBehaviour)(object)go.GetComponent(All[i]);
			if (!(ai == null))
			{
				return ai;
			}
		}
		return null;
	}

	internal static MonoBehaviour FirstInParent(GameObject go)
	{
		if (go == null)
		{
			return null;
		}
		for (int i = 0; i < All.Length; i++)
		{
			MonoBehaviour ai = (MonoBehaviour)(object)go.GetComponentInParent(All[i]);
			if (!(ai == null))
			{
				return ai;
			}
		}
		return null;
	}

	internal static bool Any(GameObject go)
	{
		return First(go) != null;
	}
}
