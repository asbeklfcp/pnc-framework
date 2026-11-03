using System;
using HarmonyLib;
using UnityEngine;

namespace PncEdi;

[HarmonyPatch]
internal static class OverkeksNunGrabTuning
{
	internal static bool GodModeActive
	{
		get
		{
			if (OverkeksMode.Active)
			{
				return OverkeksMode.Active;
			}
			return false;
		}
	}

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
		EnemyAI component = enemy.GetComponent<EnemyAI>();
		if (component != null && !string.IsNullOrEmpty(component.galleryEnemyID))
		{
			string galleryEnemyID = component.galleryEnemyID;
			if (galleryEnemyID.IndexOf("nun", StringComparison.OrdinalIgnoreCase) >= 0 || galleryEnemyID.IndexOf("hood", StringComparison.OrdinalIgnoreCase) >= 0 || galleryEnemyID.IndexOf("ghoul", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return true;
			}
		}
		string name = enemy.name;
		if (name.IndexOf("hood", StringComparison.OrdinalIgnoreCase) < 0 && name.IndexOf("nun", StringComparison.OrdinalIgnoreCase) < 0)
		{
			return name.IndexOf("ghoul", StringComparison.OrdinalIgnoreCase) >= 0;
		}
		return true;
	}

	internal static void ApplyNunGrabPreference(EnemyAI ai)
	{
		if (!OverkeksMode.Active || ai == null || !IsNunEnemy(ai.gameObject) || !ai.canGrab)
		{
			return;
		}
		float num = Mathf.Max(1f, OverkeksConfig.CfgNunGrabPreferenceMultiplier.Value);
		if (!(num <= 1f))
		{
			ai.grabCooldown = Mathf.Max(0.75f, ai.grabCooldown / num);
			ai.attackCooldown = Mathf.Min(12f, ai.attackCooldown * num);
			if (ai.grabRange < ai.attackRange)
			{
				ai.grabRange = ai.attackRange + 0.35f;
			}
		}
	}

	private static void EnemyAI_Start_Postfix(EnemyAI __instance)
	{
 OverkeksEnemyRuntime.CaptureForMode(__instance);
 if(!OverkeksMode.Active) return;
		ApplyNunGrabPreference(__instance);
	}

	[HarmonyPatch(typeof(EnemyAI), "TransitionToAttacking")]
	[HarmonyPrefix]
	private static bool TransitionToAttacking_Prefix(EnemyAI __instance)
	{
 if(!OverkeksMode.Active) return true;
		if (!OverkeksMode.Active || __instance == null || !IsNunEnemy(__instance.gameObject) || !__instance.canGrab)
		{
			return true;
		}
		if (!GodModeActive && HeatLockSystem.IsFullLockDangerActive() && !OverkeksEnemyCombatTuning.IsPlayerAtZeroHp())
		{
			return true;
		}
		if (__instance.player == null)
		{
			return true;
		}
		if (Vector3.Distance(__instance.transform.position, __instance.player.position) > __instance.grabRange)
		{
			return true;
		}
		if (CanGrabNow(__instance))
		{
			return false;
		}
		float value = Traverse.Create((object)__instance).Field("lastGrabTime").GetValue<float>();
		if (Time.time < value + __instance.grabCooldown)
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
		Traverse val = Traverse.Create((object)ai);
		if ((ai.grabScreen ?? GrabScreen.Instance) == null)
		{
			return false;
		}
		float value = val.Field("lastGrabTime").GetValue<float>();
		if (Time.time < value + ai.grabCooldown)
		{
			return false;
		}
		return val.Field("grabCoroutine").GetValue<Coroutine>() == null;
	}
}
