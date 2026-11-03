using System;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PncEdi;

[HarmonyPatch]
public static class GrabSurvivalHooks
{
	private static bool GodModeActive => Plugin.GameplayTweaksEnabled && Plugin.GodModeEnabled;
	internal static bool PreservePlayerOnDragonGrab => Plugin.GameplayTweaksEnabled;

	private static bool IsActiveDragonGrab(GrabScreen grabScreen)
	{
		return grabScreen != null && grabScreen.IsGrabbed && GrabStruggleHooks.IsDragonGrabEnemy(grabScreen.GrabbingEnemy);
	}

	[HarmonyPatch(typeof(GrabScreen), "StartGrab")]
	[HarmonyPostfix]
	public static void StartGrab_Postfix(GameObject enemy, GrabScreen __instance)
	{
		if (__instance.IsGrabbed && !(enemy == null))
		{
			if (GrabStruggleHooks.IsChaserBossGrabEnemy(enemy))
			{
				DragonGrabGameOverGuard.OnChaserBossGrabStarted(enemy);
				return;
			}
			// The game hides and restores this one itself (0.3.1). Arming the mod's protection
			// on top would block a Destroy vanilla no longer calls and keep a Die from landing
			// that vanilla now handles at EndGrab — two implementations of one mechanic, and the
			// game's is the one that owns the state.
			bool vanillaHandles = VanillaGrabHiding.Handles(enemy);
			VanillaGrabHiding.LogDecisionOnce(enemy, vanillaHandles);
			if (!vanillaHandles)
			{
				GrabEnemyProtection.OnGrabStarted(enemy);
			}
		}
	}

	[HarmonyPatch(typeof(GrabScreen), "EndGrab")]
	[HarmonyPostfix]
	public static void EndGrab_Postfix()
	{
		GrabEnemyProtection.OnGrabEnded();
	}

	[HarmonyPatch(typeof(Object), "Destroy", new Type[] { typeof(Object) })]
	[HarmonyPrefix]
	public static bool BlockDestroy(Object obj)
	{
		if (GrabEnemyProtection.IsProtected(obj))
		{
			return false;
		}
		return true;
	}

	[HarmonyPatch(typeof(Object), "Destroy", new Type[]
	{
		typeof(Object),
		typeof(float)
	})]
	[HarmonyPrefix]
	public static bool BlockDestroyDelayed(Object obj)
	{
		if (GrabEnemyProtection.IsProtected(obj))
		{
			return false;
		}
		return true;
	}

	[HarmonyPatch(typeof(EnemyAI), "RemoveEnemyAfterGrab")]
	[HarmonyPatch(typeof(DragonEnemyAI), "RemoveEnemyAfterGrab")]
	[HarmonyPatch(typeof(ProximityDragonEnemyAI), "RemoveEnemyAfterGrab")]
	[HarmonyPatch(typeof(ProjectileEnemyAI), "RemoveEnemyAfterGrab")]
	[HarmonyPatch(typeof(SpinningEnemyAI), "RemoveEnemyAfterGrab")]
	[HarmonyPatch(typeof(BrawlerEnemyAI), "RemoveEnemyAfterGrab")]
	[HarmonyPrefix]
	public static bool SkipRemoveEnemyAfterGrab(MonoBehaviour __instance)
	{
		if (__instance != null && GrabStruggleHooks.IsChaserBossGrabEnemy(__instance.gameObject))
		{
			EnemyKeepAliveHelper.RestoreEnemyHealth(__instance.gameObject);
			EnemyKeepAliveHelper.ClearDeadState(__instance);
			return false;
		}
		// Let vanilla run: on this prefab RemoveEnemyAfterGrab hides the enemy and registers it
		// with the grab screen instead of destroying it, which is the whole point of skipping it
		// before 0.3.1. Skipping it now would strand the enemy active mid-scene and leave
		// GrabScreen.hiddenEnemyToRestore null, so nothing would call EndGrabHidden at the end.
		if (VanillaGrabHiding.Handles(__instance))
		{
			return true;
		}
		if (GrabEnemyProtection.Enabled)
		{
			if (__instance != null)
			{
				EnemyKeepAliveHelper.RestoreEnemyHealth(__instance.gameObject);
				EnemyKeepAliveHelper.ClearDeadState(__instance);
			}
			return false;
		}
		return true;
	}

	[HarmonyPatch(typeof(EnemyAI), "Die")]
	[HarmonyPatch(typeof(DragonEnemyAI), "Die")]
	[HarmonyPatch(typeof(ProximityDragonEnemyAI), "Die")]
	[HarmonyPatch(typeof(ProjectileEnemyAI), "Die")]
	[HarmonyPatch(typeof(ChargingEnemyAI), "Die")]
	[HarmonyPatch(typeof(SpinningEnemyAI), "Die")]
	[HarmonyPatch(typeof(BrawlerEnemyAI), "Die")]
	[HarmonyPrefix]
	public static bool SkipEnemyDieDuringGrab(MonoBehaviour __instance)
	{
		// Only while the scene is live: an enemy dying mid-scene would break the scene it
		// is playing in.
		if (GrabEnemyProtection.IsProtectedDuringScene((Object)(object)__instance))
		{
			if (__instance != null)
			{
				EnemyKeepAliveHelper.RestoreEnemyHealth(__instance.gameObject);
				EnemyKeepAliveHelper.ClearDeadState(__instance);
			}
			return false;
		}
		// The scene is over, so a kill counts. Release the protection outright, otherwise
		// BlockDestroy would still swallow the Destroy at the end of vanilla's Die and
		// leave a dead enemy standing in the level forever.
		if (GrabEnemyProtection.IsProtected((Object)(object)__instance))
		{
			GrabEnemyProtection.Clear();
			Plugin.DBG("ENEMY-KILL", NameRemap.StripCloneSuffix(__instance.gameObject.name) + " killed after its scene - protection released");
		}
		return true;
	}

	[HarmonyPatch(typeof(DragonEnemyAI), "ModifyGrabForDragon")]
	[HarmonyPatch(typeof(ProximityDragonEnemyAI), "ModifyGrabForDragon")]
	[HarmonyPrefix]
	[HarmonyPriority(500)]
	public static bool SkipModifyGrabForDragon(MonoBehaviour __instance)
	{
		if (!PreservePlayerOnDragonGrab)
		{
			return true;
		}
		if (GodModeActive)
		{
			GrabScreen grabScreen = GrabScreen.Instance;
			if (grabScreen != null)
			{
				GrabStruggleHooks.ApplyGodModeGrabHint(grabScreen);
			}
		}
		return false;
	}

	[HarmonyPatch(typeof(DragonEnemyAI), "ModifyGrabForDragon")]
	[HarmonyPatch(typeof(ProximityDragonEnemyAI), "ModifyGrabForDragon")]
	[HarmonyPostfix]
	[HarmonyPriority(500)]
	public static void ModifyGrabForDragon_Postfix()
	{
		if (!PreservePlayerOnDragonGrab)
		{
			return;
		}
		DragonGrabGameOverGuard.CancelPendingGameOver();
		if (GodModeActive)
		{
			GrabScreen grabScreen = GrabScreen.Instance;
			if (IsActiveDragonGrab(grabScreen))
			{
				GrabStruggleHooks.HandleBlockedGrabGameOver(grabScreen);
			}
		}
	}

	[HarmonyPatch(typeof(GameOverScreen), "ShowGameOverSequence")]
	[HarmonyPrefix]
	public static bool BlockShowGameOverSequence()
	{
		if (DragonGrabGameOverGuard.ShouldBlockGameOver())
		{
			DragonGrabGameOverGuard.CancelPendingGameOver();
			return false;
		}
		return true;
	}

	[HarmonyPatch(typeof(GameOverScreen), "OnPlayerDied")]
	[HarmonyPrefix]
	public static bool OnPlayerDied_Prefix()
	{
		if (!DragonGrabGameOverGuard.ShouldBlockGameOver())
		{
			return true;
		}
		DragonGrabGameOverGuard.CancelPendingGameOver();
		GrabScreen grabScreen = GrabScreen.Instance;
		if (grabScreen != null && grabScreen.IsGrabbed)
		{
			GrabStruggleHooks.HandleBlockedGrabGameOver(grabScreen);
		}
		return false;
	}

	[HarmonyPatch(typeof(GameOverScreen), "TriggerGameOver")]
	[HarmonyPrefix]
	public static bool SkipTriggerGameOver()
	{
		if (!DragonGrabGameOverGuard.ShouldBlockGameOver())
		{
			return true;
		}
		DragonGrabGameOverGuard.CancelPendingGameOver();
		GrabScreen grabScreen = GrabScreen.Instance;
		if (grabScreen != null && grabScreen.IsGrabbed)
		{
			GrabStruggleHooks.HandleBlockedGrabGameOver(grabScreen);
		}
		return false;
	}

	private static bool _promptGeometryLogged;

	[HarmonyPatch(typeof(GameOverScreen), "Update")]
	[HarmonyPostfix]
	public static void GameOverScreen_Update_Postfix()
	{
		LogPromptGeometryOnce();
		if (!DragonGrabGameOverGuard.ShouldBlockGameOver())
		{
			return;
		}
		GameOverScreen gameOverScreen = GameOverScreen.Instance;
		if (!(gameOverScreen == null))
		{
			Traverse traverse = Traverse.Create((object)gameOverScreen);
			if (traverse.Field("isWaitingForInput").GetValue<bool>() || traverse.Field("hasShownGameOver").GetValue<bool>())
			{
				DragonGrabGameOverGuard.CancelPendingGameOver("GameOverScreen.Update");
			}
		}
	}

	// The prompt has been answered: hasShownGameOver is set and the panel is fading in, so
	// nothing is left to protect and the latch must not survive into a restarted run.
	[HarmonyPatch(typeof(GameOverScreen), "ShowGameOverPanel")]
	[HarmonyPostfix]
	public static void ShowGameOverPanel_Postfix()
	{
		DragonGrabGameOverGuard.ReleaseGameOverPresentation("ShowGameOverPanel");
	}

	[HarmonyPatch(typeof(RunRestartController), "RestartRun")]
	[HarmonyPrefix]
	public static void RestartRun_Prefix()
	{
		DragonGrabGameOverGuard.ReleaseGameOverPresentation("RestartRun");
	}

	// Once per run, the first frame the prompt is actually up. It is the game's own UI, so its
	// geometry is the reference the mod's overlay is judged against when either looks misplaced
	// - see ScreenDiag (§76).
	private static void LogPromptGeometryOnce()
	{
		if (_promptGeometryLogged)
		{
			return;
		}
		GameOverScreen screen = GameOverScreen.Instance;
		if (screen == null)
		{
			return;
		}
		Traverse t = Traverse.Create((object)screen);
		if (!t.Field("isWaitingForInput").GetValue<bool>())
		{
			return;
		}
		GameObject panel = t.Field("promptPanel").GetValue<GameObject>();
		_promptGeometryLogged = true;
		Plugin.DBG("GAMEOVER", "prompt up: " + ScreenDiag.Describe());
		Plugin.DBG("GAMEOVER", "prompt " + ScreenDiag.DescribeRect((panel != null)
			? panel.GetComponent<RectTransform>() : null));
	}
}
