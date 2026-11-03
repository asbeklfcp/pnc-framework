using System;
using System.Collections;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace PncEdi;

[HarmonyPatch]
internal static class DragonGrabHooks
{
	private static bool _chaserBossPresentationActive;

	// "We hid the fullscreen layer and have not put it back." Deliberately NOT
	// _chaserBossPresentationActive: that flag is cleared from two places, and
	// GrabEndHelper.FinishGrabEnd is one of them, so whether it is still set by the time this
	// class's EndGrab postfix runs depends on Harmony ordering between two unrelated patches.
	// The restore has to key on the thing it actually undoes.
	private static bool _fullscreenLayerSuppressed;
	private static bool _loggedChaserEndThisGrab;
	internal static bool IsChaserBossPresentationActive => _chaserBossPresentationActive;

	internal static bool UseChaserBossPresentation(GameObject enemy)
	{
		return Plugin.GameplayTweaksEnabled && GrabStruggleHooks.IsChaserBossGrabEnemy(enemy);
	}

	internal static void ClearChaserBossPresentation()
	{
		_chaserBossPresentationActive = false;
	}

	internal static bool IsChaserBossGrabPresentation(GrabScreen grabScreen)
	{
		if (grabScreen == null || !grabScreen.IsGrabbed)
		{
			return false;
		}
		if (_chaserBossPresentationActive)
		{
			return true;
		}
		if (GrabStruggleHooks.IsChaserBossGrabEnemy(grabScreen.GrabbingEnemy))
		{
			return true;
		}
		return Traverse.Create((object)grabScreen).Field("isDragonGrab").GetValue<bool>();
	}

	internal static void PrepareChaserBossGrabAnimator(GrabScreen grabScreen)
	{
		if (!(grabScreen == null))
		{
			Animator grabScreenAnimator = Traverse.Create((object)grabScreen).Field("grabScreenAnimator").GetValue<Animator>();
			if (grabScreenAnimator != null)
			{
				grabScreenAnimator.enabled = true;
			}
		}
	}

	internal static void SuppressFullscreenGrabLayer(GrabScreen grabScreen)
	{
		if (!(grabScreen == null))
		{
			Traverse traverse = Traverse.Create((object)grabScreen);
			_fullscreenLayerSuppressed = true;
			GameObject grabImage = traverse.Field("grabImage").GetValue<GameObject>();
			if (grabImage != null)
			{
				grabImage.SetActive(false);
			}
			GameObject grabOverlay = traverse.Field("grabOverlay").GetValue<GameObject>();
			if (grabOverlay != null)
			{
				grabOverlay.SetActive(false);
			}
			Image overlayImage = traverse.Field("overlayImage").GetValue<Image>();
			if (overlayImage != null)
			{
				Color grabOverlayColor = traverse.Field("grabOverlayColor").GetValue<Color>();
				grabOverlayColor.a = 0f;
				((Graphic)overlayImage).color = grabOverlayColor;
			}
		}
	}

	internal static void MaintainChaserBossFirstPersonView(GrabScreen grabScreen)
	{
		if (!(grabScreen == null))
		{
			Traverse traverse = Traverse.Create((object)grabScreen);
			Camera playerCamera = traverse.Field("playerCamera").GetValue<Camera>();
			if (playerCamera != null)
			{
				playerCamera.enabled = true;
			}
			Behaviour cameraController = traverse.Field("cameraController").GetValue<Behaviour>();
			if (cameraController != null)
			{
				cameraController.enabled = !(OverkeksMode.Active && grabScreen.IsGrabbed);
			}
			Behaviour playerController = traverse.Field("playerController").GetValue<Behaviour>();
			if (playerController != null)
			{
				playerController.enabled = !(OverkeksMode.Active && grabScreen.IsGrabbed);
			}
			Behaviour cmfSmoothPosition = traverse.Field("cmfSmoothPosition").GetValue<Behaviour>();
			if (cmfSmoothPosition != null)
			{
				cmfSmoothPosition.enabled = !(OverkeksMode.Active && grabScreen.IsGrabbed);
			}
			Behaviour cmfSmoothRotation = traverse.Field("cmfSmoothRotation").GetValue<Behaviour>();
			if (cmfSmoothRotation != null)
			{
				cmfSmoothRotation.enabled = !(OverkeksMode.Active && grabScreen.IsGrabbed);
			}
			Rigidbody playerRigidbody = traverse.Field("playerRigidbody").GetValue<Rigidbody>();
			if (playerRigidbody != null)
			{
				playerRigidbody.isKinematic = OverkeksMode.Active && grabScreen.IsGrabbed;
			}
		}
	}

	// DO NOT re-add a restore here. This was tried in §54 and reverted in §55.
	//
	// SuppressFullscreenGrabLayer looks like it has no counterpart, but it does:
	// GrabEndHelper.CleanupGrabPresentation runs the whole teardown - HideGrabUI,
	// RestoreAnimationController, SetActive(false) on all three objects, the animator's controller
	// put back and the component disabled. Crucially it sets overlayImage's alpha to **0**, which
	// is the state this suppression wanted in the first place. A "restore" that puts
	// grabOverlayColor's original alpha back therefore fights the real teardown and can leave a
	// visible tint over the screen.
	//
	// The stuck-screen bug (#18) is not here. The probe below reported grabUI=<null>
	// grabOverlay=<null> grabImage=False on a frozen screen: none of the three objects this method
	// touches was the thing still being drawn.
	// One line naming what the grab layer looked like after the game was done with it. The stuck
	// screen was invisible to every existing marker: [GRAB-END] fires from a postfix and says
	// nothing about the UI, so the log read completely clean while the screen was frozen.
	private static void LogGrabLayerState(GrabScreen grabScreen, string when)
	{
		if (grabScreen == null || !Plugin.CfgDebug.Value)
		{
			return;
		}
		Traverse t = Traverse.Create((object)grabScreen);
		string Active(string field)
		{
			GameObject go = t.Field(field).GetValue<GameObject>();
			return (go == null) ? "<null>" : go.activeInHierarchy.ToString();
		}
		Animator a = t.Field("grabScreenAnimator").GetValue<Animator>();
		string anim = "<null>";
		if (a != null)
		{
			RuntimeAnimatorController rac = a.runtimeAnimatorController;
			// The animator's OWN GameObject is the thing that draws. grabUI/grabOverlay are null
			// on this build and grabImage was already false on a frozen screen, so none of those
			// is what stays on screen - this is the object to watch.
			GameObject go = a.gameObject;
			string clip = "<none>";
			AnimatorClipInfo[] ci = a.isActiveAndEnabled ? a.GetCurrentAnimatorClipInfo(0) : null;
			if (ci != null && ci.Length > 0 && ci[0].clip != null)
			{
				clip = ci[0].clip.name;
			}
			anim = "'" + go.name + "' active=" + go.activeInHierarchy
				+ " enabled=" + a.enabled
				+ " controller='" + ((rac != null) ? rac.name : "<none>")
				+ "' clip='" + clip + "'";
		}
		Plugin.DBG("CHASER-GRAB", when + ": grabUI=" + Active("grabUI") + " grabOverlay="
			+ Active("grabOverlay") + " grabImage=" + Active("grabImage")
			+ " isGrabbed=" + grabScreen.IsGrabbed + " animator=" + anim);
	}

	internal static void ForceRestorePlayerView(GrabScreen grabScreen)
	{
		if (!(grabScreen == null))
		{
			Traverse traverse = Traverse.Create((object)grabScreen);
			if (traverse.Method("RestorePlayerControls", Array.Empty<object>()).MethodExists())
			{
				traverse.Method("RestorePlayerControls", Array.Empty<object>()).GetValue();
			}
			MaintainChaserBossFirstPersonView(grabScreen);
			GrabStruggleHooks.SnapPlayerViewLikeDebugTeleport(grabScreen);
		}
	}

	private static void ApplyChaserBossGrabLock(GrabScreen grabScreen)
	{
		Traverse traverse = Traverse.Create((object)grabScreen);
		traverse.Field("originalCursorMode").SetValue((object)Cursor.lockState);
		Cursor.lockState = (CursorLockMode)0;
		PlayerStats playerStats = traverse.Field("playerStats").GetValue<PlayerStats>();
		if (playerStats != null)
		{
			playerStats.SetExternalMovementMultiplier(0f);
		}
		Rigidbody playerRigidbody = traverse.Field("playerRigidbody").GetValue<Rigidbody>();
		if (playerRigidbody != null)
		{
			playerRigidbody.linearVelocity = Vector3.zero;
			playerRigidbody.angularVelocity = Vector3.zero;
		}
		Behaviour playerController = traverse.Field("playerController").GetValue<Behaviour>();
		if (playerController != null)
		{
			Traverse.Create((object)playerController).Method("SetMomentum", new object[1] { Vector3.zero }).GetValue();
		}
		traverse.Method("DisableHandUIOnly", Array.Empty<object>()).GetValue();
		traverse.Method("DisableInventoryAccess", Array.Empty<object>()).GetValue();
	}

	[HarmonyPatch(typeof(GrabScreen), "ApplyPlayerPositioning")]
	[HarmonyPrefix]
	[HarmonyPriority(700)]
	private static bool ApplyPlayerPositioning_SkipChaserBossCamera(GrabScreen __instance, GameObject enemy)
	{
		if (!UseChaserBossPresentation(enemy) || __instance == null)
		{
			return true;
		}
		return false;
	}

	[HarmonyPatch(typeof(GrabScreen), "ApplyPlayerPositioning")]
	[HarmonyPostfix]
	[HarmonyPriority(600)]
	private static void ApplyPlayerPositioning_ChaserBossPostfix(GrabScreen __instance, GameObject enemy)
	{
		if (UseChaserBossPresentation(enemy) && !(__instance == null))
		{
			SuppressFullscreenGrabLayer(__instance);
			MaintainChaserBossFirstPersonView(__instance);
		}
	}

	[HarmonyPatch(typeof(GrabScreen), "StartGrab")]
	[HarmonyPrefix]
	[HarmonyPriority(100)]
	private static void StartGrab_ChaserBossPrepare_Prefix(GrabScreen __instance, GameObject enemy)
	{
		if (UseChaserBossPresentation(enemy) && !(__instance == null))
		{
			_chaserBossPresentationActive = true;
			PrepareChaserBossGrabAnimator(__instance);
			Plugin.Instance?.ResetGrabStepHash();
			ResetBossGrabFlags(enemy);
		}
	}

	[HarmonyPatch(typeof(GrabScreen), "ShowGrabUI")]
	[HarmonyPrefix]
	[HarmonyPriority(700)]
	private static bool ShowGrabUI_SkipVanillaOverlay(GrabScreen __instance)
	{
		if (!IsChaserBossGrabPresentation(__instance))
		{
			return true;
		}
		SuppressFullscreenGrabLayer(__instance);
		MaintainChaserBossFirstPersonView(__instance);
		return false;
	}

	[HarmonyPatch(typeof(GrabScreen), "DisablePlayerControls")]
	[HarmonyPrefix]
	[HarmonyPriority(700)]
	private static bool DisablePlayerControls_SkipForChaserBoss(GrabScreen __instance)
	{
		if (!IsChaserBossGrabPresentation(__instance))
		{
			return true;
		}
		ApplyChaserBossGrabLock(__instance);
		SuppressFullscreenGrabLayer(__instance);
		MaintainChaserBossFirstPersonView(__instance);
		return false;
	}

	private static void ResetBossGrabFlags(GameObject enemy)
	{
		if (!(enemy == null))
		{
			EnemyAI enemyAI = enemy.GetComponent<EnemyAI>();
			if (enemyAI != null)
			{
				enemyAI.ResetGrabFlag();
			}
			Traverse traverse = Traverse.Create((object)enemy);
			if (traverse.Field("hasTriggeredDragonGrab").FieldExists())
			{
				traverse.Field("hasTriggeredDragonGrab").SetValue((object)false);
			}
			if (traverse.Field("hasGrabbedThisAttempt").FieldExists())
			{
				traverse.Field("hasGrabbedThisAttempt").SetValue((object)false);
			}
		}
	}

	[HarmonyPatch(typeof(GrabScreen), "StartGrab")]
	[HarmonyPostfix]
	[HarmonyPriority(600)]
	private static void StartGrab_ChaserBossPostfix(GrabScreen __instance, GameObject enemy)
	{
		if (UseChaserBossPresentation(enemy) && !(__instance == null) && __instance.IsGrabbed)
		{
			SuppressFullscreenGrabLayer(__instance);
			MaintainChaserBossFirstPersonView(__instance);
			Button struggleButton = __instance.StruggleButton;
			if (struggleButton != null)
			{
				((Selectable)struggleButton).interactable = true;
			}
			if (Plugin.Instance != null)
			{
				Plugin.Instance.StartCoroutine(SyncChaserBossGrabEdiNextFrame());
			}
			else
			{
				GrabHooks.RefreshChaserBossGrabPlayback();
			}
			Plugin.DBG("CHASER-GRAB", NameRemap.ResolveEnemyKey((enemy != null) ? enemy.name : "") + " — overlay hidden, animator on for EDI");
		}
	}

	[HarmonyPatch(typeof(GrabScreen), "Update")]
	[HarmonyPostfix]
	private static void Update_KeepChaserBossView(GrabScreen __instance)
	{
		if (IsChaserBossGrabPresentation(__instance))
		{
			SuppressFullscreenGrabLayer(__instance);
			MaintainChaserBossFirstPersonView(__instance);
		}
	}

	[HarmonyPatch(typeof(GrabScreen), "EndGrab")]
	[HarmonyPostfix]
	[HarmonyPriority(650)]
	private static void EndGrab_ClearChaserBossPresentation(GrabScreen __instance)
	{
		bool wasChaser = _fullscreenLayerSuppressed;
		// Cleared here, not left latched: without this the probe kept firing on every later grab of
		// any enemy for the rest of the run, so the 00:03 log carried 19 GhoulGrabScreen lines for
		// one wendigo grab. The suppression re-arms itself on the next chaser-boss StartGrab.
		_fullscreenLayerSuppressed = false;
		ClearChaserBossPresentation();
		ForceRestorePlayerView(__instance);
		if (wasChaser)
		{
			// Read-only. GrabEndHelper.CleanupGrabPresentation owns the teardown - see the note
			// above for why nothing here may touch it.
			_loggedChaserEndThisGrab = true;
			LogGrabLayerState(__instance, "EndGrab left");
		}
	}

	// Priority 10: the last EndGrab postfix to run, so this samples the state *after*
	// GrabEndHelper.CleanupGrabPresentation (priority 500) has done the teardown. Comparing the two
	// lines says whether the teardown worked and something undid it, or never took effect.
	[HarmonyPatch(typeof(GrabScreen), "EndGrab")]
	[HarmonyPostfix]
	[HarmonyPriority(10)]
	private static void EndGrab_LogAfterCleanup(GrabScreen __instance)
	{
		if (_loggedChaserEndThisGrab)
		{
			_loggedChaserEndThisGrab = false;
			LogGrabLayerState(__instance, "after cleanup");
		}
	}

	private static IEnumerator SyncChaserBossGrabEdiNextFrame()
	{
		yield return null;
		GrabScreen grabScreen = GrabScreen.Instance;
		if (grabScreen != null && grabScreen.IsGrabbed)
		{
			SuppressFullscreenGrabLayer(grabScreen);
			MaintainChaserBossFirstPersonView(grabScreen);
		}
		GrabHooks.RefreshChaserBossGrabPlayback();
	}
}
