using HarmonyLib;
using UnityEngine;

namespace PncEdi;

internal static class DragonGrabGameOverGuard
{
	private const float PostGrabSuppressSeconds = 15f;
	private static float _suppressGameOverUntil;
	private static bool _chaserBossGrabActive;

	// Set the moment the mod deliberately presents a game over of its own
	// (HeatLockSystem.CompleteSceneEntrySurvival). While it is set every cancel path is inert
	// - see GrabEndHelper.CancelGameOverPresentation for why a late cancel is unrecoverable.
	private static bool _gameOverPresentationCommitted;
	internal static bool GameOverPresentationCommitted => _gameOverPresentationCommitted;

	internal static void CommitGameOverPresentation(string source)
	{
		_gameOverPresentationCommitted = true;
		Plugin.DBG("GAMEOVER", "presentation committed from " + source + " - cancels ignored until it is consumed");
	}

	// Cleared where the presentation is over or the run has moved on: the player pressed the
	// key (ShowGameOverPanel), the run restarted, or a scene revived the player.
	//
	// That last one is not a leak, it is the mod's central death rule (§8, §13): at 0 HP the
	// player stays grabbable, the grab that lands plays as the death scene, and the game over
	// is presented again when it ends. A prompt torn down by a grab starting is that handoff
	// working, so the latch has to stand down for it - it exists only to stop a game over
	// being cancelled by teardown belonging to the scene that just ended (§75, §77).
	internal static void ReleaseGameOverPresentation(string source, bool respectPrompt = false)
	{
		if (!_gameOverPresentationCommitted)
		{
			return;
		}
		// `respectPrompt` is the revive path's own restraint, and it is what the 2026-08-24 run
		// was missing. The handoff below is written for a grab that lands on a player who has
		// just died - before the presentation has put anything on screen. Once the prompt is up
		// the death has been *presented*: the player is looking at it and pressing a key is the
		// only thing that should move it on. Releasing there re-opened every cancel path, and
		// the Blinded Beast - re-grabbing about a second after its own grab ended - took the
		// prompt away every time it appeared, so the run could be seen and never answered. The
		// panel guard added in §119 could not catch it, because reaching the panel is exactly
		// what the player never got to do.
		if (respectPrompt && GameOverPromptIsUp())
		{
			Plugin.DBG("GAMEOVER", "release from " + source + " refused - the prompt is up and is the player's to answer");
			return;
		}
		_gameOverPresentationCommitted = false;
		Plugin.DBG("GAMEOVER", "presentation released by " + source);
	}

	internal static void OnChaserBossGrabStarted(GameObject enemy)
	{
		if (Plugin.GameplayTweaksEnabled && !(enemy == null) && GrabStruggleHooks.IsChaserBossGrabEnemy(enemy))
		{
			_chaserBossGrabActive = true;
			CancelPendingGameOver();
		}
	}

	internal static void OnChaserBossGrabEnded()
	{
		_chaserBossGrabActive = false;
		_suppressGameOverUntil = Time.time + 15f;
		CancelPendingGameOver();
		SceneEscapeGate.EndScene();
		GrabEndHelper.EnsureGrabSessionClosed();
	}

	internal static bool ShouldBlockGameOver()
	{
		if (!Plugin.GameplayTweaksEnabled)
		{
			return false;
		}
		if (HeatLockSystem.ForcingPendingSceneEntryGameOver || _gameOverPresentationCommitted)
		{
			return false;
		}
		bool sceneActive = IsSexSceneActive();
		if (IsPlayerActuallyDead() && !sceneActive)
		{
			return false;
		}
		if (sceneActive)
		{
			return true;
		}
		if (Plugin.GodModeEnabled)
		{
			return true;
		}
		if (Time.time < _suppressGameOverUntil)
		{
			return true;
		}
		if (_chaserBossGrabActive)
		{
			return true;
		}
		return IsChaserBossGrabScreen(GrabScreen.Instance);
	}

	internal static bool ShouldBlockLethalHealth()
	{
		if (!Plugin.GameplayTweaksEnabled)
		{
			return false;
		}
		if (HeatLockSystem.ForcingPendingSceneEntryGameOver || _gameOverPresentationCommitted)
		{
			return false;
		}
		if (IsSexSceneActive())
		{
			return true;
		}
		if (Plugin.GodModeEnabled)
		{
			return true;
		}
		if (HeatLockSystem.Enabled)
		{
			return false;
		}
		if (Time.time < _suppressGameOverUntil)
		{
			return true;
		}
		if (_chaserBossGrabActive)
		{
			return true;
		}
		return IsChaserBossGrabScreen(GrabScreen.Instance);
	}

	private static bool IsPlayerActuallyDead()
	{
		PlayerStats playerStats = Object.FindAnyObjectByType<PlayerStats>();
		return playerStats != null && playerStats.CurrentHealth <= 0;
	}

	private static bool IsSexSceneActive()
	{
		GrabScreen grabScreen = GrabScreen.Instance;
		if (grabScreen != null && grabScreen.IsGrabbed)
		{
			return true;
		}
		GrappleScreenobject grappleScreenobject = GrappleScreenobject.Instance;
		if (grappleScreenobject != null && grappleScreenobject.IsGrappling)
		{
			return true;
		}
		return CameraSwapHooks.Active;
	}

	// The panel with the Restart / Main Menu buttons is up, and the run is over.
	//
	// This is NOT the same state as the "press any key" prompt, and the difference is the whole
	// of §119. The prompt (isWaitingForInput) is what the mod's death rule is allowed to tear
	// down: at 0 HP the player stays grabbable, and a grab landing on the prompt turns into the
	// death scene, which is the handoff ReleaseGameOverPresentation stands down for. But
	// ShowGameOverPanel latches hasShownGameOver, and from there vanilla's own contract is that
	// nothing re-enters: OnPlayerDied, ShowGameOverPanel and Update all guard on it, because the
	// player is now looking at two buttons and choosing. Cancelling at that point does not hand
	// the death to a scene - it SetActive(false)s the panel those buttons are on, and the run has
	// no way out left.
	//
	// GameOverScreen is a per-scene component, so a reload starts with the field false again and
	// there is nothing here to leak into the next run.
	internal static bool GameOverPanelIsUp()
	{
		GameOverScreen gameOverScreen = GameOverScreen.Instance;
		if (gameOverScreen == null)
		{
			return false;
		}
		return Traverse.Create((object)gameOverScreen).Field("hasShownGameOver").GetValue<bool>();
	}

	// The "press any key" prompt, one stage before the panel. On its own it is fair game - the
	// mod's death rule tears it down so a grab can become the death scene - so this is only ever
	// asked together with `GameOverPresentationCommitted`: a prompt the mod itself raised, and
	// therefore one it has already spent the death on.
	internal static bool GameOverPromptIsUp()
	{
		GameOverScreen gameOverScreen = GameOverScreen.Instance;
		if (gameOverScreen == null)
		{
			return false;
		}
		return Traverse.Create((object)gameOverScreen).Field("isWaitingForInput").GetValue<bool>();
	}

	// Everything the player owns: the panel with the two buttons, and a prompt the mod put up
	// itself. One question for every caller that used to ask only about the panel.
	internal static bool GameOverIsThePlayersToAnswer()
	{
		return GameOverPanelIsUp() || (_gameOverPresentationCommitted && GameOverPromptIsUp());
	}

	internal static void CancelPendingGameOver(string source = "guard")
	{
		GrabEndHelper.CancelGameOverPresentation(source);
	}

	private static bool IsChaserBossGrabScreen(GrabScreen grabScreen)
	{
		if (grabScreen == null || !grabScreen.IsGrabbed)
		{
			return false;
		}
		if (GrabStruggleHooks.IsChaserBossGrabEnemy(grabScreen.GrabbingEnemy))
		{
			return true;
		}
		return Traverse.Create((object)grabScreen).Field("isDragonGrab").GetValue<bool>();
	}
}
