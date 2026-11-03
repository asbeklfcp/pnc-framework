using System;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PncEdi;

[HarmonyPatch]
internal static class NunGrabTuning
{
	internal static bool GodModeActive => (Plugin.GameplayTweaksEnabled && !OverkeksMode.Active) && Plugin.GodModeEnabled;

	internal static bool IsNunEnemy(GameObject enemy)
	{
		if (enemy == null)
		{
			return false;
		}
		if (NameRemap.ResolveEnemyKey(enemy.name) == "nun")
		{
			return true;
		}
		EnemyAI enemyAI = enemy.GetComponent<EnemyAI>();
		if (enemyAI != null && !string.IsNullOrEmpty(enemyAI.galleryEnemyID))
		{
			string galleryEnemyID = enemyAI.galleryEnemyID;
			if (galleryEnemyID.IndexOf("nun", StringComparison.OrdinalIgnoreCase) >= 0 || galleryEnemyID.IndexOf("hood", StringComparison.OrdinalIgnoreCase) >= 0 || galleryEnemyID.IndexOf("ghoul", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return true;
			}
		}
		string name = enemy.name;
		return name.IndexOf("hood", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("nun", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("ghoul", StringComparison.OrdinalIgnoreCase) >= 0;
	}

	internal static void ApplyNunGrabPreference(EnemyAI ai)
	{
		if (!(Plugin.GameplayTweaksEnabled && !OverkeksMode.Active) || ai == null || !IsNunEnemy(ai.gameObject) || !ai.canGrab)
		{
			return;
		}
		float multiplier = Mathf.Max(1f, (OverkeksMode.UsesRecipe ? OverkeksConfig.CfgNunGrabPreferenceMultiplier : Plugin.CfgNunGrabPreferenceMultiplier).Value);
		if (!(multiplier <= 1f))
		{
			ai.grabCooldown = Mathf.Max(0.75f, ai.grabCooldown / multiplier);
			ai.attackCooldown = Mathf.Min(12f, ai.attackCooldown * multiplier);
			if (ai.grabRange < ai.attackRange)
			{
				ai.grabRange = ai.attackRange + 0.35f;
			}
		}
	}

	[HarmonyPatch(typeof(EnemyAI), "Start")]
	[HarmonyPostfix]
	private static void EnemyAI_Start_Postfix(EnemyAI __instance)
	{
        OverkeksEnemyRuntime.CaptureForMode(__instance);
		ApplyNunGrabPreference(__instance);
	}

	[HarmonyPatch(typeof(EnemyAI), "TransitionToAttacking")]
	[HarmonyPrefix]
	private static bool TransitionToAttacking_Prefix(EnemyAI __instance)
	{
		if (!(Plugin.GameplayTweaksEnabled && !OverkeksMode.Active) || __instance == null || !IsNunEnemy(__instance.gameObject) || !__instance.canGrab)
		{
			return true;
		}
		if (!GodModeActive && HeatLockSystem.IsFullLockDangerActive() && !EnemyCombatTuning.IsPlayerAtZeroHp())
		{
			return true;
		}
		if (__instance.player == null)
		{
			return true;
		}
		float distance = Vector3.Distance(__instance.transform.position, __instance.player.position);
		if (distance > __instance.grabRange)
		{
			return true;
		}
		if (CanGrabNow(__instance))
		{
			return false;
		}
		float lastGrabTime = Traverse.Create((object)__instance).Field("lastGrabTime").GetValue<float>();
		if (Time.time < lastGrabTime + __instance.grabCooldown)
		{
			return false;
		}
		return true;
	}

	private static bool CanGrabNow(EnemyAI ai)
	{
		if (!ai.canGrab)
		{
			return false;
		}
		Traverse traverse = Traverse.Create((object)ai);
		GrabScreen grabScreen = ai.grabScreen ?? GrabScreen.Instance;
		if (grabScreen == null)
		{
			return false;
		}
		float lastGrabTime = traverse.Field("lastGrabTime").GetValue<float>();
		if (Time.time < lastGrabTime + ai.grabCooldown)
		{
			return false;
		}
		return traverse.Field("grabCoroutine").GetValue<Coroutine>() == null;
	}
}
