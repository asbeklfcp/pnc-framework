using HarmonyLib;
using UnityEngine;

namespace PncEdi;

/// <summary>
/// Wakes post-scene / stun-scheduled enemies when the player actually deals damage to them.
/// </summary>
[HarmonyPatch]
internal static class EnemyReactivationOnDamageHook
{
	[HarmonyPatch(typeof(EnemyAI), "TakeDamage")]
	[HarmonyPrefix]
	[HarmonyPriority(Priority.First)]
	private static void EnemyAI_TakeDamage_Prefix(EnemyAI __instance, int damage)
	{
		TryWake(__instance, damage);
	}

	[HarmonyPatch(typeof(ChargingEnemyAI), "TakeDamage")]
	[HarmonyPrefix]
	[HarmonyPriority(Priority.First)]
	private static void Charging_TakeDamage_Prefix(ChargingEnemyAI __instance, int damage)
	{
		TryWake(__instance, damage);
	}

	[HarmonyPatch(typeof(SpinningEnemyAI), "TakeDamage")]
	[HarmonyPrefix]
	[HarmonyPriority(Priority.First)]
	private static void Spinning_TakeDamage_Prefix(SpinningEnemyAI __instance, int damage)
	{
		TryWake(__instance, damage);
	}

	[HarmonyPatch(typeof(ProjectileEnemyAI), "TakeDamage")]
	[HarmonyPrefix]
	[HarmonyPriority(Priority.First)]
	private static void Projectile_TakeDamage_Prefix(ProjectileEnemyAI __instance, int damage)
	{
		TryWake(__instance, damage);
	}

	[HarmonyPatch(typeof(BrawlerEnemyAI), "TakeDamage")]
	[HarmonyPrefix]
	[HarmonyPriority(Priority.First)]
	private static void Brawler_TakeDamage_Prefix(BrawlerEnemyAI __instance, int damage)
	{
		TryWake(__instance, damage);
	}

	[HarmonyPatch(typeof(DragonEnemyAI), "TakeDamage")]
	[HarmonyPrefix]
	[HarmonyPriority(Priority.First)]
	private static void Dragon_TakeDamage_Prefix(DragonEnemyAI __instance, int damage)
	{
		TryWake(__instance, damage);
	}

	[HarmonyPatch(typeof(ProximityDragonEnemyAI), "TakeDamage")]
	[HarmonyPrefix]
	[HarmonyPriority(Priority.First)]
	private static void Proximity_TakeDamage_Prefix(ProximityDragonEnemyAI __instance, int damage)
	{
		TryWake(__instance, damage);
	}

	private static void TryWake(MonoBehaviour ai, int damage)
	{
		if (OverkeksMode.Active && damage > 0 && ai != null)
		{
			EnemyReactivationHelper.TryWakeOnDamage(ai.gameObject);
		}
	}
}
