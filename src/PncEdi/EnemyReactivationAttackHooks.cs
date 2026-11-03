namespace PncEdi;

internal static class EnemyReactivationAttackHooks
{
	internal static void OnPlayerAttackTriggered()
	{
		if (Plugin.GameplayTweaksEnabled && !GrabStruggleHooks.ShouldBlockGrabAttacks())
		{
			EnemyReactivationHelper.TryWakeOnPlayerAttack();
		}
	}
}
