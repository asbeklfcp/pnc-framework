using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace PncEdi;

[HarmonyPatch]
internal static class OverkeksEnemyCombatTuning
{
	private static readonly HashSet<int> HealthScaledIds = new HashSet<int>();

	private static readonly HashSet<int> PaceTunedIds = new HashSet<int>();

	private const float MaxFollowerSpeed = 28f;

	private const float MinAttackCooldown = 0.35f;

	private const float MinChargeCooldown = 0.85f;

	internal static void Forget(int id) {HealthScaledIds.Remove(id);PaceTunedIds.Remove(id);}
	internal static void ResetForScene()
	{
		HealthScaledIds.Clear();
		PaceTunedIds.Clear();
	}

	private static bool PreferDamageAttacks()
	{
		if (HeatLockSystem.IsFullLockDangerActive())
		{
			return !IsPlayerAtZeroHp();
		}
		return false;
	}

	internal static bool IsPlayerAtZeroHp()
	{
		PlayerStats playerStats = UnityEngine.Object.FindAnyObjectByType<PlayerStats>();
		if (playerStats != null)
		{
			return playerStats.CurrentHealth <= 0;
		}
		return false;
	}

	private static bool PreferSexScenes()
	{
		if (!IsPlayerAtZeroHp())
		{
			return !HeatLockSystem.IsFullLockDangerActive();
		}
		return true;
	}

	internal static string ResolveEnemyKey(GameObject enemy, string galleryEnemyId) => enemy == null ? "" : IdentityPolicy.Resolve(NameRemap.ResolveEnemyKey(enemy.name),NameRemap.ResolveEnemyKey(galleryEnemyId ?? ""));

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
		if (OverkeksMode.Active && !(ai == null))
		{
			string text = ResolveEnemyKey(ai.gameObject, ai.galleryEnemyID);
			if (text == "gargoyle")
			{
				ApplySpinningEnemyTuning(ai, Mathf.Max(1f, OverkeksConfig.CfgGargoyleGrabRate.Value), Mathf.Max(1f, OverkeksConfig.CfgGargoyleShootRate.Value), OverkeksConfig.CfgGargoyleSpinRangeScale.Value, Mathf.Max(1f, OverkeksConfig.CfgGargoyleSpinCooldownScale.Value));
			}
			else if (text == "plantasha")
			{
				ApplySpinningEnemyTuning(ai, Mathf.Max(1f, OverkeksConfig.CfgPlantashaGrabRate.Value), Mathf.Max(1f, OverkeksConfig.CfgPlantashaShootRate.Value), 0.84f, 1.15f);
			}
		}
	}

	internal static float ScaleProjectileSpeed(float speed)
	{
		if (!OverkeksMode.Active)
		{
			return speed;
		}
		float value = OverkeksConfig.CfgEnemyProjectileSpeedMultiplier.Value;
		if (value <= 1.001f)
		{
			return speed;
		}
		return speed * value;
	}

	internal static void ApplyEnemyHealthMultiplier(MonoBehaviour ai)
	{
		if (!OverkeksMode.Active || ai == null)
		{
			return;
		}
		float mult = OverkeksConfig.CfgEnemyHealthMultiplier?.Value ?? 1f;
		if (mult < 0.05f)
		{
			mult = 0.05f;
		}
		if (Mathf.Approximately(mult, 1f))
		{
			return;
		}
		int id = ai.GetInstanceID();
		if (!HealthScaledIds.Add(id))
		{
			return;
		}
		Traverse t = Traverse.Create((object)ai);
		if (!t.Field("maxHealth").FieldExists())
		{
			return;
		}
		int maxHealth = t.Field("maxHealth").GetValue<int>();
		maxHealth = Mathf.Max(1, Mathf.CeilToInt(maxHealth * mult));
		t.Field("maxHealth").SetValue(maxHealth);
		if (t.Field("currentHealth").FieldExists())
		{
			t.Field("currentHealth").SetValue(maxHealth);
		}
		if (t.Field("secondStageMaxHealth").FieldExists())
		{
			int stage2 = t.Field("secondStageMaxHealth").GetValue<int>();
			if (stage2 > 0)
			{
				t.Field("secondStageMaxHealth").SetValue(Mathf.Max(1, Mathf.CeilToInt(stage2 * mult)));
			}
		}
	}

	internal static void ApplyEnemyPace(MonoBehaviour ai, bool melee)
	{
		if (!OverkeksMode.Active || ai == null)
		{
			return;
		}
		int id = ai.GetInstanceID();
		if (!PaceTunedIds.Add(id))
		{
			return;
		}
		ApplyDamageToPlayerMultiplier(ai);
		float moveMult = ResolveMoveSpeedMultiplier(ai, melee);
		if (moveMult > 1.001f)
		{
			ScaleFollowerMaxSpeed(ai, moveMult);
			if (melee)
			{
				ScaleFloatField(ai, "secondStageMoveSpeed", moveMult);
				ScaleFloatField(ai, "chargeSpeed", moveMult);
			}
		}
		if (!melee)
		{
			return;
		}
		float attackRate = Mathf.Max(1f, OverkeksConfig.CfgMeleeEnemyAttackRate?.Value ?? 1f);
		if (attackRate <= 1.001f)
		{
			return;
		}
		if (ai is EnemyAI enemyAi && OverkeksNunGrabTuning.IsNunEnemy(enemyAi.gameObject))
		{
			return;
		}
		ScaleCooldownField(ai, "attackCooldown", attackRate, MinAttackCooldown);
		ScaleCooldownField(ai, "chargeCooldown", attackRate, MinChargeCooldown);
	}

	private static void ApplyDamageToPlayerMultiplier(MonoBehaviour ai)
	{
		float mult = OverkeksConfig.CfgEnemyDamageToPlayerMultiplier?.Value ?? 1f;
		if (mult < 0.05f)
		{
			mult = 0.05f;
		}
		if (Mathf.Approximately(mult, 1f))
		{
			return;
		}
		Traverse t = Traverse.Create((object)ai);
		ScaleIntDamageField(ai, "attackDamage", mult);
		ScaleIntDamageField(ai, "projectileDamage", mult);
	}

	private static void ScaleIntDamageField(MonoBehaviour ai, string fieldName, float mult)
	{
if(OptionalMembers.Read(ai,fieldName) is int value && value>0) OptionalMembers.Write(ai,fieldName,Mathf.Max(1,Mathf.CeilToInt(value*mult)));
    }

	private static float ResolveMoveSpeedMultiplier(MonoBehaviour ai, bool melee)
	{
		float general = Mathf.Max(1f, OverkeksConfig.CfgEnemyMoveSpeedMultiplier?.Value ?? 1f);
		float meleeMult = Mathf.Max(general, OverkeksConfig.CfgMeleeEnemyMoveSpeedMultiplier?.Value ?? general);
		if (melee)
		{
			return meleeMult;
		}
		if (ai is ProjectileEnemyAI projectile && GooperTuning.IsGooper(projectile.gameObject))
		{
			float gooper = Mathf.Max(1f, OverkeksConfig.CfgGooperMoveSpeedMultiplier?.Value ?? 1f);
			return Mathf.Max(general, gooper);
		}
		return general;
	}

	private static void ScaleFollowerMaxSpeed(MonoBehaviour ai, float mult)
	{
var follower=OptionalMembers.Read(ai,"followerEntity");
        if(OptionalMembers.Read(follower,"maxSpeed") is float speed && speed>0)OptionalMembers.Write(follower,"maxSpeed",Mathf.Min(MaxFollowerSpeed,speed*mult));
    }

	private static void ScaleFloatField(MonoBehaviour ai, string fieldName, float mult)
	{
if(OptionalMembers.Read(ai,fieldName) is float value && value>0)OptionalMembers.Write(ai,fieldName,value*mult);
    }

	private static void ScaleCooldownField(MonoBehaviour ai, string fieldName, float rate, float minValue)
	{
if(OptionalMembers.Read(ai,fieldName) is float value && value>0)OptionalMembers.Write(ai,fieldName,Mathf.Max(minValue,value/rate));
    }

	private static void SpinningEnemyAI_Start_Postfix(SpinningEnemyAI __instance)
	{
 OverkeksEnemyRuntime.CaptureForMode(__instance);
 if(!OverkeksMode.Active) return;
		ApplyEnemyHealthMultiplier(__instance);
		ApplyEnemyPace(__instance, melee: false);
		ApplySpinningEnemyTuningForKey(__instance);
		EnemyIdentityTuning.Apply(__instance != null ? __instance.gameObject : null);
	}

	private static void EnemyAI_Start_Postfix(EnemyAI __instance)
	{
 OverkeksEnemyRuntime.CaptureForMode(__instance);
 if(!OverkeksMode.Active) return;
		ApplyEnemyHealthMultiplier(__instance);
		ApplyEnemyPace(__instance, melee: true);
		EnemyIdentityTuning.Apply(__instance != null ? __instance.gameObject : null);
	}

	private static void ProjectileEnemyAI_Start_Postfix(ProjectileEnemyAI __instance)
	{
 OverkeksEnemyRuntime.CaptureForMode(__instance);
 if(!OverkeksMode.Active) return;
		ApplyEnemyHealthMultiplier(__instance);
		ApplyEnemyPace(__instance, melee: false);
		EnemyIdentityTuning.Apply(__instance != null ? __instance.gameObject : null);
	}

	private static void BrawlerEnemyAI_Start_Postfix(BrawlerEnemyAI __instance)
	{
 OverkeksEnemyRuntime.CaptureForMode(__instance);
 if(!OverkeksMode.Active) return;
		ApplyEnemyHealthMultiplier(__instance);
		ApplyEnemyPace(__instance, melee: true);
		EnemyIdentityTuning.Apply(__instance != null ? __instance.gameObject : null);
	}

	private static void ChargingEnemyAI_Start_Postfix(ChargingEnemyAI __instance)
	{
 OverkeksEnemyRuntime.CaptureForMode(__instance);
 if(!OverkeksMode.Active) return;
		ApplyEnemyHealthMultiplier(__instance);
		ApplyEnemyPace(__instance, melee: true);
		EnemyIdentityTuning.Apply(__instance != null ? __instance.gameObject : null);
	}

	[HarmonyPatch(typeof(EnemyProjectile), "Initialize")]
	[HarmonyPrefix]
	private static void EnemyProjectile_Initialize_Prefix(ref float projectileSpeed)
	{
 if(!OverkeksMode.Active) return;
		projectileSpeed = ScaleProjectileSpeed(projectileSpeed);
	}

	[HarmonyPatch(typeof(GrabProjectile), "Initialize")]
	[HarmonyPrefix]
	private static void GrabProjectile_Initialize_Prefix(ref float projectileSpeed)
	{
 if(!OverkeksMode.Active) return;
		projectileSpeed = ScaleProjectileSpeed(projectileSpeed);
	}

	[HarmonyPatch(typeof(EnemyAI), "CanGrab")]
	[HarmonyPostfix]
	private static void EnemyAI_CanGrab_Postfix(EnemyAI __instance, ref bool __result)
	{
 if(!OverkeksMode.Active) return;
		if (PreferSexScenes() || (OverkeksNunGrabTuning.GodModeActive && __instance != null && OverkeksNunGrabTuning.IsNunEnemy(__instance.gameObject)))
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
 if(!OverkeksMode.Active) return;
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
 if(!OverkeksMode.Active) return;
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
