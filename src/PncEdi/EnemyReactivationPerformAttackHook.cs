using HarmonyLib;
using PixelCrushers.GridController;
using UnityEngine;

namespace PncEdi;

[HarmonyPatch(typeof(DualWieldingSystem), "PerformAttack")]
internal static class EnemyReactivationPerformAttackHook
{
	[HarmonyPostfix]
	public static void Postfix(DualWieldingSystem __instance, HandSlot handSlot)
	{
		if (!(__instance == null) && IsHandSwinging(__instance, handSlot))
		{
			EnemyReactivationAttackHooks.OnPlayerAttackTriggered();
		}
	}

	private static bool IsHandSwinging(DualWieldingSystem system, HandSlot handSlot)
	{
		if (1 == 0)
		{
		}
		bool result = (((int)handSlot == 0) ? system.IsLeftHandAttacking : (((int)handSlot != 1) ? system.IsAttacking : system.IsRightHandAttacking));
		if (1 == 0)
		{
		}
		return result;
	}
}
