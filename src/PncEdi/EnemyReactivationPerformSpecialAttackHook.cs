using HarmonyLib;
using PixelCrushers.GridController;
using UnityEngine;

namespace PncEdi;

[HarmonyPatch(typeof(DualWieldingSystem), "PerformSpecialAttack")]
internal static class EnemyReactivationPerformSpecialAttackHook
{
	[HarmonyPostfix]
	public static void Postfix(DualWieldingSystem __instance)
	{
		if (__instance != null && __instance.IsAttacking)
		{
			EnemyReactivationAttackHooks.OnPlayerAttackTriggered();
		}
	}
}
