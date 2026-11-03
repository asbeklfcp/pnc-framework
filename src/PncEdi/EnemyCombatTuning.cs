using System;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PncEdi;

[HarmonyPatch]
internal static class EnemyCombatTuning
{
	private static bool PreferDamageAttacks()
	{
		return HeatLockSystem.IsFullLockDangerActive() && !IsPlayerAtZeroHp();
	}

	internal static bool IsPlayerAtZeroHp()
	{
		PlayerStats playerStats = Object.FindAnyObjectByType<PlayerStats>();
		return playerStats != null && playerStats.CurrentHealth <= 0;
	}

	private static bool PreferSexScenes()
	{
		return IsPlayerAtZeroHp() || !HeatLockSystem.IsFullLockDangerActive();
	}

	internal static string ResolveEnemyKey(GameObject enemy, string galleryEnemyId)
	{
		if (enemy == null)
		{
			return "";
		}
		string resolved = NameRemap.ResolveEnemyKey(enemy.name);
		if (!string.IsNullOrEmpty(galleryEnemyId))
		{
			string fromGalleryId = NameRemap.ResolveEnemyKey(galleryEnemyId);
			if (!string.IsNullOrEmpty(fromGalleryId))
			{
				resolved = fromGalleryId;
			}
			else if (galleryEnemyId.IndexOf("zombie", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				resolved = "zombie";
			}
			else if (galleryEnemyId.IndexOf("gargoyle", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				resolved = "gargoyle";
			}
			else if (galleryEnemyId.IndexOf("plantasha", StringComparison.OrdinalIgnoreCase) >= 0 || galleryEnemyId.IndexOf("plant", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				resolved = "plantasha";
			}
		}
		return resolved;
	}

	private static void ApplySpinningEnemyTuning(SpinningEnemyAI ai, float grabRate, float shootRate, float spinRangeScale, float spinCooldownScale)
	{
		if (grabRate > 1.001f)
		{
			ai.grabCooldown = Mathf.Max(2.5f, ai.grabCooldown / grabRate);
		}
		if (shootRate > 1.001f)
		{
			ai.shootCooldown = Mathf.Max(1.25f, ai.shootCooldown / shootRate);
		}
		if (spinRangeScale < 0.999f)
		{
			ai.spinMaxRange = Mathf.Max(ai.grabRange + 1.5f, ai.spinMaxRange * Mathf.Clamp(spinRangeScale, 0.75f, 1f));
		}
		if (spinCooldownScale > 1.001f)
		{
			ai.spinCooldown = Mathf.Min(2.5f, ai.spinCooldown * spinCooldownScale);
		}
	}

	internal static void ApplySpinningEnemyTuningForKey(SpinningEnemyAI ai)
	{
		if ((Plugin.GameplayTweaksEnabled && !OverkeksMode.Active) && !(ai == null))
		{
			string key = ResolveEnemyKey(ai.gameObject, ai.galleryEnemyID);
			if (key == "gargoyle")
			{
				ApplySpinningEnemyTuning(ai, Mathf.Max(1f, (OverkeksMode.UsesRecipe ? OverkeksConfig.CfgGargoyleGrabRate : Plugin.CfgGargoyleGrabRate).Value), Mathf.Max(1f, (OverkeksMode.UsesRecipe ? OverkeksConfig.CfgGargoyleShootRate : Plugin.CfgGargoyleShootRate).Value), (OverkeksMode.UsesRecipe ? OverkeksConfig.CfgGargoyleSpinRangeScale : Plugin.CfgGargoyleSpinRangeScale).Value, Mathf.Max(1f, (OverkeksMode.UsesRecipe ? OverkeksConfig.CfgGargoyleSpinCooldownScale : Plugin.CfgGargoyleSpinCooldownScale).Value));
			}
			else if (key == "plantasha")
			{
				ApplySpinningEnemyTuning(ai, Mathf.Max(1f, (OverkeksMode.UsesRecipe ? OverkeksConfig.CfgPlantashaGrabRate : Plugin.CfgPlantashaGrabRate).Value), Mathf.Max(1f, (OverkeksMode.UsesRecipe ? OverkeksConfig.CfgPlantashaShootRate : Plugin.CfgPlantashaShootRate).Value), 0.84f, 1.15f);
			}
		}
	}

	internal static float ScaleProjectileSpeed(float speed)
	{
		if (!(Plugin.GameplayTweaksEnabled && !OverkeksMode.Active))
		{
			return speed;
		}
		float enemyProjectileSpeedMultiplier = (OverkeksMode.UsesRecipe ? OverkeksConfig.CfgEnemyProjectileSpeedMultiplier : Plugin.CfgEnemyProjectileSpeedMultiplier).Value;
		if (enemyProjectileSpeedMultiplier <= 1.001f)
		{
			return speed;
		}
		return speed * enemyProjectileSpeedMultiplier;
	}

	[HarmonyPatch(typeof(SpinningEnemyAI), "Start")]
	[HarmonyPostfix]
	private static void SpinningEnemyAI_Start_Postfix(SpinningEnemyAI __instance)
	{
        OverkeksEnemyRuntime.CaptureForMode(__instance);
		ApplySpinningEnemyTuningForKey(__instance);
	}

	[HarmonyPatch(typeof(EnemyProjectile), "Initialize")]
	[HarmonyPrefix]
	private static void EnemyProjectile_Initialize_Prefix(ref float projectileSpeed)
	{
		projectileSpeed = ScaleProjectileSpeed(projectileSpeed);
	}

	[HarmonyPatch(typeof(GrabProjectile), "Initialize")]
	[HarmonyPrefix]
	private static void GrabProjectile_Initialize_Prefix(ref float projectileSpeed)
	{
		projectileSpeed = ScaleProjectileSpeed(projectileSpeed);
	}

	[HarmonyPatch(typeof(EnemyAI), "CanGrab")]
	[HarmonyPostfix]
	private static void EnemyAI_CanGrab_Postfix(EnemyAI __instance, ref bool __result)
	{
		if (PreferSexScenes() || (NunGrabTuning.GodModeActive && __instance != null && NunGrabTuning.IsNunEnemy(__instance.gameObject)))
		{
			__result = true;
		}
		else if (PreferDamageAttacks())
		{
			__result = false;
		}
	}

	[HarmonyPatch(typeof(SpinningEnemyAI), "CanGrab")]
	[HarmonyPostfix]
	private static void SpinningEnemyAI_CanGrab_Postfix(ref bool __result)
	{
		if (PreferSexScenes())
		{
			__result = true;
		}
		else if (PreferDamageAttacks())
		{
			__result = false;
		}
	}

	[HarmonyPatch(typeof(ProjectileEnemyAI), "CanGrabShoot")]
	[HarmonyPostfix]
	private static void ProjectileEnemyAI_CanGrabShoot_Postfix(ref bool __result)
	{
		if (PreferSexScenes())
		{
			__result = true;
		}
		else if (PreferDamageAttacks())
		{
			__result = false;
		}
	}
}
