using HarmonyLib;
using PixelCrushers.GridController;

namespace PncEdi;

[HarmonyPatch(typeof(DualWieldingSystem), "OnAttackHit")]
internal static class EnemyReactivationOnAttackHitHook
{
	[HarmonyPostfix]
	public static void Postfix()
	{
		EnemyReactivationAttackHooks.OnPlayerAttackTriggered();
	}
}
