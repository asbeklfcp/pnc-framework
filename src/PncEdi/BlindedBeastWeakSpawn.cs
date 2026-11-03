using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace PncEdi;

/// <summary>
/// Random-pool / rare Blinded Beasts (not arena bosses) get reduced HP and slower move speed,
/// lose the MiniBoss health bar, and are treated as ordinary enemies for Q-escape delay / miniboss heat locks.
/// </summary>
internal static class BlindedBeastWeakSpawn
{
	/// <summary>
	/// Blinded Beast prefab sits around ~700 HP (miniboss). Global EnemyHealthMultiplier
	/// leaves random-pool beasts near 400 even at 0.5 — still feels unkillable as fodder.
	/// </summary>
	private const float HealthScale = 0.25f;

	private const int MaxHealthCap = 150;

	private const float SpeedScale = 0.7f;


	private static readonly HashSet<int> WeakInstanceIds = new HashSet<int>();

	internal static void ClearCache()
	{
		WeakInstanceIds.Clear();
	}


	internal static bool IsBlindedBeastKey(string key)
	{
		return !string.IsNullOrEmpty(key) && key.Equals("blinded_beast", StringComparison.OrdinalIgnoreCase);
	}

	internal static bool IsBlindedBeast(GameObject enemy)
	{
		if (enemy == null)
		{
			return false;
		}
		return IsBlindedBeastKey(NameRemap.ResolveEnemyKey(enemy.name));
	}

	internal static bool IsWeakRandomSpawn(GameObject enemy)
	{
		if (enemy == null)
		{
			return false;
		}
		return WeakInstanceIds.Contains(enemy.GetInstanceID());
	}

	internal static void TryConsumePendingAndWeaken(GameObject enemy)
	{
		if (enemy == null || enemy.GetComponent<OverkeksSpawnVariant>()?.Weak != true || WeakInstanceIds.Contains(enemy.GetInstanceID()) || !IsBlindedBeast(enemy))
		{
			return;
		}
		OverkeksEnemyRuntime.CaptureForMode(EnemyAiTypes.First(enemy));
        OverkeksMode.Own(enemy);
		ApplyWeakStats(enemy);
		WeakInstanceIds.Add(enemy.GetInstanceID());
		Plugin.DBG("SPAWN", "blinded_beast random-spawn weakened (hp x" + HealthScale.ToString("0.##") + ", speed x" + SpeedScale.ToString("0.##") + ")");
	}

	private static void ApplyWeakStats(GameObject enemy)
	{
		EnemyAI ai = enemy.GetComponent<EnemyAI>();
		if (ai == null)
		{
			return;
		}
		// Random-pool beasts must not own the MiniBoss health bar / stage UI.
		if (ai.isMiniBoss)
		{
			ai.isMiniBoss = false;
			MiniBossHealthBarUI.Unregister(ai);
		}
		Traverse t = Traverse.Create((object)ai);
		int maxHealth = Mathf.Max(1, Mathf.Min(MaxHealthCap, Mathf.CeilToInt(ai.maxHealth * HealthScale)));
		ai.maxHealth = maxHealth;
		ai.currentHealth = maxHealth;
		if (ai.secondStageMaxHealth > 0)
		{
			ai.secondStageMaxHealth = Mathf.Max(1, Mathf.Min(MaxHealthCap, Mathf.CeilToInt(ai.secondStageMaxHealth * HealthScale)));
		}
		ai.secondStageMoveSpeed *= SpeedScale;
		Plugin.DBG("SPAWN", "blinded_beast weak HP now " + maxHealth + " (stage2=" + ai.secondStageMaxHealth + ")");
		Traverse follower = t.Field("followerEntity");
		if (follower.FieldExists())
		{
			object value = follower.GetValue();
			if (value != null)
			{
				Traverse ft = Traverse.Create(value);
				if (ft.Field("maxSpeed").FieldExists())
				{
					float speed = ft.Field("maxSpeed").GetValue<float>();
					ft.Field("maxSpeed").SetValue(speed * SpeedScale);
				}
			}
		}
	}

	private static void EnemyAI_Start_Postfix(EnemyAI __instance)
	{
 if(!OverkeksMode.Active) return;
		if (__instance != null)
		{
			TryConsumePendingAndWeaken(__instance.gameObject);
		}
	}

	[HarmonyPatch(typeof(EnemyAI), "OnDestroy")]
	[HarmonyPostfix]
	private static void EnemyAI_OnDestroy_Postfix(EnemyAI __instance)
	{
 if(!OverkeksMode.HooksAllowed) return;
		if (__instance != null)
		{
			WeakInstanceIds.Remove(__instance.gameObject.GetInstanceID());
		}
	}
}
