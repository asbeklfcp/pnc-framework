using HarmonyLib;
using PixelCrushers.GridController;

namespace PncEdi;

[HarmonyPatch(typeof(DualWieldingSystem), "OnAttackStart")]
internal static class EnemyReactivationOnAttackStartHook
{
	[HarmonyPostfix]
	public static void Postfix()
	{
		EnemyReactivationAttackHooks.OnPlayerAttackTriggered();
	}
}
