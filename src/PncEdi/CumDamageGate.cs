using HarmonyLib;

namespace PncEdi;

// GrabScreen.TriggerMaxHeatAnimation - the player cumming in an H-scene - deliberately
// drops invulnerability, deals maxHeatDamage plus MaxHealth*grabDamagePercent, and puts
// invulnerability back:
//
//     playerStats.SetInvulnerable(false);
//     playerStats.TakeDamage(Mathf.RoundToInt(maxHeatDamage));
//     if (!isDragonGrab && grabDamagePercent > 0f) playerStats.TakeDamage(...);
//     playerStats.SetInvulnerable(true);
//
// That is the only damage source that can kill the player *during* a scene, which is also
// how a run ends up softlocked: a death mid-scene never re-fires the game over, because
// the deferred-game-over machinery only arms for players who were already dead when the
// scene began (see TODO "Dying while already grappled").
//
// Suppressing at the TakeDamage level rather than zeroing the serialized fields keeps the
// cum animation, heat block and cooldown behaviour exactly as vanilla - only the HP loss
// is gone - and avoids handing GameplayHooks.TakeDamage_Prefix a 0-damage call that would
// still register with the damage-filler tracker.
[HarmonyPatch]
public static class CumDamageGate
{
	private static bool _suppressing;
	internal static bool Enabled => Plugin.GameplayTweaksEnabled && (OverkeksMode.UsesRecipe ? OverkeksConfig.CfgDisableCumDamage : Plugin.CfgDisableCumDamage).Value;
	internal static bool Suppressing => _suppressing;

	[HarmonyPatch(typeof(GrabScreen), "TriggerMaxHeatAnimation")]
	[HarmonyPrefix]
	public static void TriggerMaxHeatAnimation_Prefix()
	{
		if (Enabled)
		{
			_suppressing = true;
		}
	}

	// Finalizer, not postfix: a postfix is skipped when the original throws, which would
	// leave the flag stuck on and silently suppress *all* player damage for the rest of
	// the run. A void finalizer that ignores __exception runs either way and rethrows.
	[HarmonyPatch(typeof(GrabScreen), "TriggerMaxHeatAnimation")]
	[HarmonyFinalizer]
	public static void TriggerMaxHeatAnimation_Finalizer()
	{
		_suppressing = false;
	}
}
