using System;
using HarmonyLib;
using UnityEngine;

namespace PncEdi;

/// <summary>
/// Gooper (ProjectileEnemyAI slime) combat buff: faster move/attacks, lower panic-teleport
/// range, and prefer shooting over retreat when an attack is ready.
/// </summary>
[HarmonyPatch]
internal static class GooperTuning
{
	private const float MinShootCooldown = 0.75f;

	private const float MinGrabCooldown = 2.5f;

	private const float MinMinRange = 1.25f;

	internal static bool IsGooper(GameObject enemy)
	{
		if (enemy == null)
		{
			return false;
		}
		ProjectileEnemyAI ai = enemy.GetComponent<ProjectileEnemyAI>();
		if (ai == null)
		{
			return false;
		}
		return OverkeksEnemyCombatTuning.ResolveEnemyKey(enemy, ai.galleryEnemyID) == "gooper";
	}

	internal static void Apply(ProjectileEnemyAI ai)
	{
		if (!OverkeksMode.Active || ai == null || !IsGooper(ai.gameObject))
		{
			return;
		}
		float attackRate = Mathf.Max(1f, OverkeksConfig.CfgGooperAttackRate?.Value ?? 1.5f);
		if (attackRate > 1.001f)
		{
			ai.shootCooldown = Mathf.Max(MinShootCooldown, ai.shootCooldown / attackRate);
			ai.grabCooldown = Mathf.Max(MinGrabCooldown, ai.grabCooldown / attackRate);
		}
		float minRangeScale = Mathf.Clamp(OverkeksConfig.CfgGooperMinRangeScale?.Value ?? 0.55f, 0.35f, 1f);
		if (minRangeScale < 0.999f)
		{
			ai.minRange = Mathf.Max(MinMinRange, ai.minRange * minRangeScale);
		}
		// Move speed is applied once in EnemyCombatTuning (max of EnemyMoveSpeedMultiplier / GooperMoveSpeedMultiplier).
		Plugin.DBG("GOOPER-TUNE", NameRemap.StripCloneSuffix(ai.gameObject.name)
			+ " attack x" + attackRate.ToString("0.##")
			+ " minRange=" + ai.minRange.ToString("0.##")
			+ " shootCd=" + ai.shootCooldown.ToString("0.##"));
	}

	
	
	private static void ProjectileEnemyAI_Start_Postfix(ProjectileEnemyAI __instance)
	{
		Apply(__instance);
	}

	/// <summary>
	/// Vanilla retreats as soon as the player is inside minRange, even when a shot is ready —
	/// that is why goopers often teleport away without attacking. Prefer the shot.
	/// </summary>
	[HarmonyPatch(typeof(ProjectileEnemyAI), "TransitionToRetreating")]
	[HarmonyPrefix]
	private static bool TransitionToRetreating_Prefix(ProjectileEnemyAI __instance)
	{
		if (!OverkeksMode.Active || __instance == null || !IsGooper(__instance.gameObject))
		{
			return true;
		}
		Traverse t = Traverse.Create((object)__instance);
		bool canGrab = t.Method("CanGrabShoot").GetValue<bool>();
		bool canShoot = t.Method("CanShoot").GetValue<bool>();
		if (!canGrab && !canShoot)
		{
			return true;
		}
		t.Method("TransitionToShooting").GetValue();
		return false;
	}
}
