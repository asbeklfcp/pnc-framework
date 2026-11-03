using System;
using System.Collections;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace PncEdi;

internal static class GrabEndHelper
{
	private static bool _grabSessionActive;
	private static GameObject _grabSessionEnemy;
	private static bool _endingChaserBossGrab;
	private static bool _skipGrabAnimatorReenable;

	internal static void OnGrabSessionStarted(GameObject enemy)
	{
		if (!(enemy == null))
		{
			_grabSessionActive = true;
			_grabSessionEnemy = enemy;
		}
	}

	internal static void OnGrabSessionEnded()
	{
		_grabSessionActive = false;
		_grabSessionEnemy = null;
	}

	internal static void EnsureGrabSessionClosed()
	{
		OnGrabSessionEnded();
		SceneEscapeGate.EndScene();
	}

	internal static bool IsGrabSessionLive(GrabScreen grabScreen)
	{
		if (grabScreen == null)
		{
			return false;
		}
		return grabScreen.IsGrabbed || _grabSessionActive;
	}

	internal static bool IsEscapeSceneActive()
	{
		GrabScreen grabScreen = GrabScreen.Instance;
		if (grabScreen != null && IsGrabSessionLive(grabScreen))
		{
			return true;
		}
		GrappleScreenobject grappleScreenobject = GrappleScreenobject.Instance;
		if (grappleScreenobject != null && grappleScreenobject.IsGrappling)
		{
			return true;
		}
		return CameraSwapHooks.IsCinematicLive();
	}

	internal static void PrepareGrabEnd(GrabScreen grabScreen)
	{
		if (grabScreen == null)
		{
			return;
		}
		Plugin.Instance?.ResetGrabTracking();
		CancelGameOverPresentation("PrepareGrabEnd");
		Traverse traverse = Traverse.Create((object)grabScreen);
		if (!grabScreen.IsGrabbed && _grabSessionActive)
		{
			traverse.Field("isGrabbed").SetValue((object)true);
			if (_grabSessionEnemy != null)
			{
				traverse.Field("grabbingEnemy").SetValue((object)_grabSessionEnemy);
			}
		}
		Animator grabScreenAnimator = traverse.Field("grabScreenAnimator").GetValue<Animator>();
		if (grabScreenAnimator != null)
		{
			grabScreenAnimator.SetBool("MaxHeat", false);
			GrabScreenHeatParam.Set(grabScreenAnimator, value: false);
			grabScreenAnimator.enabled = false;
		}
		traverse.Field("maxHeatAnimationPlaying").SetValue((object)false);
		traverse.Field("heatIncreaseBlocked").SetValue((object)false);
		traverse.Field("isCoolingDown").SetValue((object)false);
		traverse.Field("heatFullyCooled").SetValue((object)false);
		_endingChaserBossGrab = traverse.Field("isDragonGrab").GetValue<bool>();
		if (!_endingChaserBossGrab && _grabSessionEnemy != null)
		{
			_endingChaserBossGrab = GrabStruggleHooks.IsChaserBossGrabEnemy(_grabSessionEnemy);
		}
		_skipGrabAnimatorReenable = _endingChaserBossGrab;
	}

	internal static void FinishGrabEnd(GrabScreen grabScreen)
	{
		if (_endingChaserBossGrab)
		{
			DragonGrabGameOverGuard.OnChaserBossGrabEnded();
			_endingChaserBossGrab = false;
		}
		DragonGrabHooks.ClearChaserBossPresentation();
		OnGrabSessionEnded();
		HeatLockSystem.CompleteSceneEntrySurvival("grab");
	}

	internal static void FinalizeGrabEndVisuals(GrabScreen grabScreen)
	{
		if (grabScreen == null)
		{
			return;
		}
		CleanupGrabPresentation(grabScreen);
		DragonGrabHooks.ForceRestorePlayerView(grabScreen);
		if (Plugin.Instance == null || _skipGrabAnimatorReenable)
		{
			_skipGrabAnimatorReenable = false;
			return;
		}
		Animator grabScreenAnimator = Traverse.Create((object)grabScreen).Field("grabScreenAnimator").GetValue<Animator>();
		if (grabScreenAnimator != null)
		{
			Plugin.Instance.StartCoroutine(ReenableGrabAnimator(grabScreenAnimator));
		}
	}

	internal static void CleanupGrabPresentation(GrabScreen grabScreen)
	{
		if (grabScreen == null)
		{
			return;
		}
		CancelGameOverPresentation("CleanupGrabPresentation");
		SceneEscapeGate.EndScene();
		GrabStruggleHooks.SnapPlayerViewLikeDebugTeleport(grabScreen);
		Traverse traverse = Traverse.Create((object)grabScreen);
		traverse.Field("isDragonGrab").SetValue((object)false);
		traverse.Method("HideGrabUI", Array.Empty<object>()).GetValue();
		traverse.Method("RestoreAnimationController", Array.Empty<object>()).GetValue();
		// The animator block runs BEFORE the layer is hidden, and the order is the whole fix for
		// #18 (CHANGELOG §56). Rebind() + Update(0f) makes the animator write every property its
		// clips bind, and the six chaser-boss grab clips - WendigoKiss/Sex/SexIntro and
		// DragonGrab's three - are the only clips in the game carrying a GameObject m_IsActive
		// curve, on the same transform the sprite frames are drawn to. Evaluating them at t=0
		// therefore re-activates grabImage. With the hides first, that write landed last and left
		// the fullscreen layer up showing frame 0 with the animator disabled: the stuck screen.
		Animator grabScreenAnimator = traverse.Field("grabScreenAnimator").GetValue<Animator>();
		if (!(grabScreenAnimator == null))
		{
			grabScreenAnimator.SetBool("MaxHeat", false);
			GrabScreenHeatParam.Set(grabScreenAnimator, value: false);
			RuntimeAnimatorController originalController = traverse.Field("originalController").GetValue<RuntimeAnimatorController>();
			RuntimeAnimatorController defaultController = traverse.Field("defaultController").GetValue<RuntimeAnimatorController>();
			RuntimeAnimatorController controller = originalController ?? defaultController;
			if (controller != null)
			{
				grabScreenAnimator.runtimeAnimatorController = controller;
			}
			grabScreenAnimator.Rebind();
			grabScreenAnimator.Update(0f);
			grabScreenAnimator.enabled = false;
		}
		GameObject grabUI = traverse.Field("grabUI").GetValue<GameObject>();
		if (grabUI != null)
		{
			grabUI.SetActive(false);
		}
		GameObject grabOverlay = traverse.Field("grabOverlay").GetValue<GameObject>();
		if (grabOverlay != null)
		{
			grabOverlay.SetActive(false);
		}
		GameObject grabImage = traverse.Field("grabImage").GetValue<GameObject>();
		if (grabImage != null)
		{
			grabImage.SetActive(false);
		}
		// Same reasoning for the tint: WendigoSex, WendigoSexIntro and GhoulGrabStart bind
		// m_Color.a, so Update(0f) rewrites the overlay's alpha too. Zeroing it after is what
		// makes the grabUI stick.
		Image overlayImage = traverse.Field("overlayImage").GetValue<Image>();
		if (overlayImage != null)
		{
			Color grabOverlayColor = traverse.Field("grabOverlayColor").GetValue<Color>();
			grabOverlayColor.a = 0f;
			((Graphic)overlayImage).color = grabOverlayColor;
		}
		Button struggleButton = grabScreen.StruggleButton;
		if (struggleButton != null)
		{
			((Selectable)struggleButton).interactable = true;
		}
	}

	internal static void TryEndActiveHScene(bool forceImmediate = false)
	{
		if (!forceImmediate && !SceneEscapeGate.CanEscape)
		{
			return;
		}
		GrabScreen grabScreen = GrabScreen.Instance;
		if (grabScreen != null && IsGrabSessionLive(grabScreen))
		{
			GameObject grabEnemy = (grabScreen.IsGrabbed ? grabScreen.GrabbingEnemy : _grabSessionEnemy);
			EndActiveGrapple();
			grabScreen.EndGrab();
			FinalizeImpSceneExit(grabEnemy, resetEdiTracking: false);
			Plugin.DBG("GRAB-END", "EndGrab key");
		}
		else if (EndActiveGrapple())
		{
			FinalizeImpSceneExit(null, resetEdiTracking: true);
			Plugin.DBG("GRAPPLE-END", "EndGrab key during grapple");
		}
		else
		{
			CameraSwapTrigger currentTrigger = CameraSwapHooks.CurrentTrigger;
			if (currentTrigger != null && CameraSwapHooks.IsCinematicLive())
			{
				currentTrigger.ReturnToFirstPersonFromAnimation();
				Plugin.DBG("INTERACT-END", "EndGrab key");
			}
		}
	}

	private static bool EndActiveGrapple()
	{
		GrappleScreenobject grappleScreenobject = GrappleScreenobject.Instance;
		if (grappleScreenobject == null || !grappleScreenobject.IsGrappling)
		{
			return false;
		}
		grappleScreenobject.ForceEndGrapple();
		return true;
	}

	private static void FinalizeImpSceneExit(GameObject grabEnemy, bool resetEdiTracking)
	{
		NearbyEnemyHider.RestoreAll();
		if (resetEdiTracking)
		{
			Plugin.Instance?.ResetGrabTracking();
		}
	}

	internal static void StabilizeAfterGrabEnd(GameObject enemy)
	{
		if (!Plugin.GameplayTweaksEnabled)
		{
			return;
		}
		ApplyPlayerGrabImmunity();
		if (enemy == null)
		{
			return;
		}
        // Persistent Overkeks chasers recover in place rather than scheduling a staging respawn.
        if(OverkeksMode.Active && GrabStruggleHooks.IsChaserBossGrabEnemy(enemy) && ChaserSweeperHooks.IsActiveManagedChaser(enemy)) {
            EnemyReactivationHelper.ScheduleReactivate(enemy, applyGrabEndCooldown:true);
            return;
        }
		// Vanilla's EndGrabHidden has already run by the time this postfix does, and it does the
		// whole restore: SetActive(true), the transformed animator state back, health from the
		// grab screen's snapshot, ResetGrabTransientState, state = Idle, pathfinding on. Running
		// the mod's reactivation on top would re-do all of it a frame or two later against an
		// enemy that is already awake. All that is still ours is the configured re-grab cooldown,
		// which vanilla does not have — it sets lastGrabTime = now and nothing more.
		if (!GrabStruggleHooks.IsChaserBossGrabEnemy(enemy) && VanillaGrabHiding.Handles(enemy))
		{
            // Overkeks retains the original released-actor AI stun, including native hidden captures.
            // Other profiles keep their existing native restore/cooldown behavior.
            if (OverkeksMode.Active)
                EnemyReactivationHelper.ScheduleReactivate(enemy, applyGrabEndCooldown: true);
            else
                EnemyReactivationHelper.ApplyGrabEndCooldownToEnemy(enemy);
			return;
		}
		EnemyReactivationHelper.ScheduleReactivate(enemy, applyGrabEndCooldown: true);
		DragonEnemyAI dragonEnemyAI = enemy.GetComponent<DragonEnemyAI>();
		if (dragonEnemyAI != null)
		{
			StabilizeBossGrabEnemy((MonoBehaviour)(object)dragonEnemyAI);
			EnemyReactivationHelper.ScheduleRespawn(enemy, applyGrabEndCooldown: true);
			return;
		}
		ProximityDragonEnemyAI proximityDragonEnemyAI = enemy.GetComponent<ProximityDragonEnemyAI>();
		if (proximityDragonEnemyAI != null)
		{
			StabilizeBossGrabEnemy((MonoBehaviour)(object)proximityDragonEnemyAI);
			EnemyReactivationHelper.ScheduleRespawn(enemy, applyGrabEndCooldown: true);
		}
		else if (GrabStruggleHooks.IsChaserBossGrabEnemy(enemy))
		{
			EnemyReactivationHelper.ScheduleRespawn(enemy, applyGrabEndCooldown: true);
		}
		else
		{
			EnemyReactivationHelper.ScheduleReactivate(enemy, applyGrabEndCooldown: true);
		}
	}

	private static void StabilizeBossGrabEnemy(MonoBehaviour bossAi)
	{
		Traverse traverse = Traverse.Create((object)bossAi);
		// Same rule as ApplyGrabEndCooldown: a StopGrab landing while the boss is still in
		// Grabbing nulls a live coroutine and strands the state machine (§78).
		if (traverse.Method("StopGrab", Array.Empty<object>()).MethodExists() && !EnemyReactivationHelper.IsGrabbing(traverse))
		{
			traverse.Method("StopGrab", Array.Empty<object>()).GetValue();
		}
		EnemyReactivationHelper.ApplyGrabEndCooldown(bossAi);
		if (traverse.Method("ResetGrabFlag", Array.Empty<object>()).MethodExists())
		{
			traverse.Method("ResetGrabFlag", Array.Empty<object>()).GetValue();
		}
		if (traverse.Field("hasTriggeredDragonGrab").FieldExists())
		{
			traverse.Field("hasTriggeredDragonGrab").SetValue((object)false);
		}
		if (traverse.Field("hasGrabbedThisAttempt").FieldExists())
		{
			traverse.Field("hasGrabbedThisAttempt").SetValue((object)false);
		}
	}

	internal static void CancelGameOverPresentation(string source = "unknown")
	{
		GameOverScreen gameOverScreen = GameOverScreen.Instance;
		if (!(gameOverScreen == null))
		{
			// StopAllCoroutines below tears down ShowGameOverSequence, and that sequence is
			// the only thing that ever raises the "press any key" prompt: it waits
			// delayBeforePrompt (1 s) before setting promptPanel active and isWaitingForInput
			// true. A cancel that lands inside that window leaves the run with no prompt and
			// no way back to one, because OnPlayerDied is not called again. Once the mod has
			// deliberately presented a game over, this call is therefore inert.
			if (DragonGrabGameOverGuard.GameOverPresentationCommitted)
			{
				Plugin.DBG("GAMEOVER", "cancel from " + source + " ignored - committed game over is presenting");
				return;
			}
			// And once the panel is up it is the player's, whoever is asking. Every route into
			// this method funnels here - the survival revive, the Update-postfix block, both
			// teardown paths - so one refusal covers all of them (§119).
			if (DragonGrabGameOverGuard.GameOverIsThePlayersToAnswer())
			{
				// GameOverScreen.Update asks every frame for as long as the panel is up, so this is
				// collapsed rather than printed 780 times a run (§138).
				Plugin.DBGRepeat("GAMEOVER", "gameover-refusal", "cancel from " + source + " ignored - the game over is up and is the player's to answer");
				return;
			}
			Plugin.DBGRepeatEnd("GAMEOVER", "gameover-refusal");
			Traverse diag = Traverse.Create((object)gameOverScreen);
			Plugin.DBG("GAMEOVER", "cancel from " + source + " (waitingForInput="
				+ diag.Field("isWaitingForInput").GetValue<bool>() + " hasShownGameOver="
				+ diag.Field("hasShownGameOver").GetValue<bool>() + ")");
			gameOverScreen.StopAllCoroutines();
			Traverse traverse = Traverse.Create((object)gameOverScreen);
			traverse.Field("isWaitingForInput").SetValue((object)false);
			traverse.Field("hasShownGameOver").SetValue((object)false);
			GameObject promptPanel = traverse.Field("promptPanel").GetValue<GameObject>();
			if (promptPanel != null)
			{
				promptPanel.SetActive(false);
			}
			GameObject gameOverPanel = traverse.Field("gameOverPanel").GetValue<GameObject>();
			if (gameOverPanel != null)
			{
				gameOverPanel.SetActive(false);
			}
		}
	}

	private static void ApplyPlayerGrabImmunity()
	{
		GameObject gameObject = GameObject.FindGameObjectWithTag("Player");
		if (!(gameObject == null))
		{
			PlayerStats playerStats = gameObject.GetComponent<PlayerStats>();
			if (!(playerStats == null) && !(Plugin.Instance == null))
			{
                if(OverkeksMode.Active) {
                    OverkeksGrabRecovery.Arm(playerStats,OverkeksConfig.CfgEndGrabImmunitySeconds.Value);
                    return;
                }
				playerStats.CanBeGrabbed = false;
				Plugin.Instance.StartCoroutine(ReleasePlayerGrabImmunity(playerStats, (OverkeksMode.UsesRecipe ? OverkeksConfig.CfgEndGrabImmunitySeconds : Plugin.CfgEndGrabImmunitySeconds).Value));
			}
		}
	}

	private static IEnumerator ReleasePlayerGrabImmunity(PlayerStats playerStats, float seconds)
	{
		yield return (object)new WaitForSeconds(Mathf.Max(0.5f, seconds));
		if (playerStats != null)
		{
			playerStats.CanBeGrabbed = true;
		}
	}

	private static IEnumerator ReenableGrabAnimator(Animator grabAnimator)
	{
		yield return null;
		if (grabAnimator != null)
		{
			grabAnimator.enabled = true;
		}
	}
}
