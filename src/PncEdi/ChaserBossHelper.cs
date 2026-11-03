using System;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PncEdi;

internal static class ChaserBossHelper
{
	internal static bool IsChaserBossPrefab(GameObject prefab)
	{
		if (prefab == null)
		{
			return false;
		}
		if (prefab.GetComponent<DragonEnemyAI>() != null || prefab.GetComponent<ProximityDragonEnemyAI>() != null)
		{
			return true;
		}
		string name = NameRemap.StripCloneSuffix(prefab.name);
		return name.IndexOf("wendigo", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("dragon", StringComparison.OrdinalIgnoreCase) >= 0;
	}
}
