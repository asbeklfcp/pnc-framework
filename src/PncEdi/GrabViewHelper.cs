using System.Collections;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace PncEdi;

/// <summary>
/// Gameplay camera during/after grabs; resets grab canvas so the next vanilla H-scene is not a white screen.
/// Ported from 0.2.1 — was missing in 0.3.1 and left chaser cleanup stranding grabImage/camera.
/// </summary>
[HarmonyPatch]
internal static class GrabViewHelper
{
	internal static void ResetGrabUiBaseline(GrabScreen grabScreen)
	{
		if (!OverkeksMode.HooksAllowed || grabScreen == null)
		{
			return;
		}
		ResetGrabSessionState(grabScreen);
		// Fix alphas zeroed by Gravy/Baphomet even while layers stay hidden until next StartGrab.
		RestoreVanillaGrabImageAlphas(Traverse.Create((object)grabScreen));
		HideGrabCanvasLayers(grabScreen);
	}

	internal static void ResetGrabSessionState(GrabScreen grabScreen)
	{
		if (!OverkeksMode.HooksAllowed || grabScreen == null)
		{
			return;
		}
		Traverse traverse = Traverse.Create((object)grabScreen);
		traverse.Field("isCoolingDown").SetValue((object)false);
		traverse.Field("maxHeatAnimationPlaying").SetValue((object)false);
		traverse.Field("heatIncreaseBlocked").SetValue((object)false);
		traverse.Field("heatFullyCooled").SetValue((object)false);
		traverse.Field("lastHeatBuildup").SetValue((object)0f);
		ResetGrabOverlayColor(traverse);
	}

	internal static void HideGrabCanvasLayers(GrabScreen grabScreen)
	{
		if (!OverkeksMode.HooksAllowed || grabScreen == null)
		{
			return;
		}
		Traverse traverse = Traverse.Create((object)grabScreen);
		SetGrabLayerActive(traverse, "grabUI", active: false);
		SetGrabLayerActive(traverse, "grabOverlay", active: false);
		SetGrabLayerActive(traverse, "grabImage", active: false);
		SetGrabLayerActive(traverse, "enemyHealthUI", active: false);
	}

	/// <summary>Vanilla fullscreen grab canvas (grabUI + grabImage). Safe to call after a mistaken hide.</summary>
	internal static void EnsureFullscreenGrabUi(GrabScreen grabScreen)
	{
		if (!OverkeksMode.HooksAllowed || grabScreen == null)
		{
			return;
		}
		if (DragonGrabHooks.UseChaserBossPresentation(grabScreen.GrabbingEnemy))
		{
			return;
		}
		if (grabScreen.GrabbingEnemy != null
			&& (grabScreen.GrabbingEnemy.GetComponent<BaphometGargoyleMarker>() != null
				|| grabScreen.GrabbingEnemy.GetComponent<GravySerpentMarker>() != null))
		{
			return;
		}
		Traverse traverse = Traverse.Create((object)grabScreen);
		RestoreVanillaGrabImageAlphas(traverse);
		RestoreGrabUiMasks(traverse);
		ResetGrabOverlayColor(traverse);
		SetGrabLayerActive(traverse, "grabUI", active: true);
		SetGrabLayerActive(traverse, "grabOverlay", active: true);
		SetGrabLayerActive(traverse, "grabImage", active: true);
		EnsureGrabAnimatorForFullscreenGrab(grabScreen);
	}

	/// <summary>
	/// Gravy/Baphomet HideVanillaGrabBackdrop zeros Image alphas on grabOverlay/grabImage.
	/// EndGrab restores alphas for the next vanilla H-scene, but keeps layers hidden —
	/// re-enabling them here fights the next gravy/baphomet StartGrab and leaves a white flash.
	/// </summary>
	internal static void RestoreVanillaGrabPresentation(GrabScreen grabScreen)
	{
		if (!OverkeksMode.HooksAllowed || grabScreen == null)
		{
			return;
		}
		Traverse traverse = Traverse.Create((object)grabScreen);
		RestoreVanillaGrabImageAlphas(traverse);
		RestoreGrabUiMasks(traverse);
		ResetGrabOverlayColor(traverse);
		EnsureGrabAnimatorForFullscreenGrab(grabScreen);
		HideGrabCanvasLayers(grabScreen);
		BaphometGrabOverlay.Hide();
	}

	private static void RestoreVanillaGrabImageAlphas(Traverse traverse)
	{
		GameObject grabImage = traverse.Field("grabImage").GetValue<GameObject>();
		if (grabImage != null)
		{
			Image[] images = grabImage.GetComponentsInChildren<Image>(includeInactive: true);
			for (int i = 0; i < images.Length; i++)
			{
				Image image = images[i];
				if (image == null)
				{
					continue;
				}
				NeutralizeSolidWhiteFill(image);
				Color color = image.color;
				// Sprite-less near-white panels were zeroed by Gravy/Baphomet so the overlay
				// could show. Restoring them to opaque white is the post-gravy whitescreen.
				if (image.sprite == null && IsNearWhite(color))
				{
					color.a = 0f;
					image.color = color;
				}
				else if (color.a < 0.99f)
				{
					color.a = 1f;
					image.color = color;
				}
				image.enabled = true;
				if (!image.gameObject.activeSelf)
				{
					image.gameObject.SetActive(value: true);
				}
			}
		}
		GameObject grabOverlay = traverse.Field("grabOverlay").GetValue<GameObject>();
		if (grabOverlay != null)
		{
			Image overlayImage = grabOverlay.GetComponent<Image>();
			if (overlayImage != null)
			{
				Color color = overlayImage.color;
				if (color.a < 0.01f)
				{
					// Leave final alpha to ResetGrabOverlayColor (grabOverlayColor).
					color.a = 1f;
					overlayImage.color = color;
				}
			}
		}
	}

	/// <summary>
	/// Gravy/Baphomet HideVanillaGrabHoleChrome disables Mask/RectMask2D on grabUI and never
	/// turns them back on — the next vanilla grab then draws unmasked white panels.
	/// </summary>
	private static void RestoreGrabUiMasks(Traverse traverse)
	{
		GameObject grabUi = traverse.Field("grabUI").GetValue<GameObject>();
		if (grabUi == null)
		{
			return;
		}
		Mask[] masks = grabUi.GetComponentsInChildren<Mask>(includeInactive: true);
		for (int i = 0; i < masks.Length; i++)
		{
			if (masks[i] != null)
			{
				masks[i].enabled = true;
			}
		}
		RectMask2D[] rectMasks = grabUi.GetComponentsInChildren<RectMask2D>(includeInactive: true);
		for (int i = 0; i < rectMasks.Length; i++)
		{
			if (rectMasks[i] != null)
			{
				rectMasks[i].enabled = true;
			}
		}
	}

	private static void NeutralizeSolidWhiteFill(Image image)
	{
		if (image == null || image.sprite != null)
		{
			return;
		}
		Color color = image.color;
		if (!IsNearWhite(color))
		{
			return;
		}
		// Keep the slot for layout/animator writes, but never present an opaque white quad.
		color.a = 0f;
		image.color = color;
	}

	private static bool IsNearWhite(Color color)
	{
		return color.r > 0.85f && color.g > 0.85f && color.b > 0.85f;
	}

	internal static void ResetGrabOverlayColor(GrabScreen grabScreen)
	{
		if (grabScreen == null)
		{
			return;
		}
		ResetGrabOverlayColor(Traverse.Create((object)grabScreen));
	}

	private static void ResetGrabOverlayColor(Traverse traverse)
	{
		Image overlayImage = traverse.Field("overlayImage").GetValue<Image>();
		if (overlayImage == null)
		{
			return;
		}
		Color grabOverlayColor = traverse.Field("grabOverlayColor").GetValue<Color>();
		overlayImage.color = grabOverlayColor;
	}

	private static void SetGrabLayerActive(Traverse traverse, string fieldName, bool active)
	{
		GameObject layer = traverse.Field(fieldName).GetValue<GameObject>();
		if (layer != null)
		{
			layer.SetActive(active);
		}
	}

	[HarmonyPatch(typeof(GrabScreen), "StartGrab")]
	[HarmonyPrefix]
	[HarmonyPriority(950)]
	private static void StartGrab_ResetGrabUiBaseline_Prefix(GrabScreen __instance, GameObject enemy)
	{
        if(!OverkeksMode.HooksAllowed) return;
		if (!OverkeksMode.HooksAllowed || __instance == null)
		{
			return;
		}
		ResetGrabSessionState(__instance);
		if (DragonGrabHooks.UseChaserBossPresentation(enemy))
		{
			HideGrabCanvasLayers(__instance);
		}
	}

	[HarmonyPatch(typeof(GrabScreen), "StartGrab")]
	[HarmonyPostfix]
	[HarmonyPriority(50)]
	private static void StartGrab_EnsureFullscreenUi_Postfix(GrabScreen __instance, GameObject enemy)
	{
        if(!OverkeksMode.HooksAllowed) return;
		if (!OverkeksMode.HooksAllowed || __instance == null || !__instance.IsGrabbed)
		{
			return;
		}
		if (DragonGrabHooks.UseChaserBossPresentation(enemy))
		{
			return;
		}
		if (enemy != null
			&& (enemy.GetComponent<BaphometGargoyleMarker>() != null
				|| enemy.GetComponent<GravySerpentMarker>() != null))
		{
			return;
		}
		EnsureFullscreenGrabUi(__instance);
		if ((Object)(object)Plugin.Instance != null)
		{
			OverkeksMode.StartCoroutine(EnsureFullscreenGrabUiNextFrame(__instance));
		}
	}

	private static IEnumerator EnsureFullscreenGrabUiNextFrame(GrabScreen grabScreen)
	{
		yield return null;
		GrabScreen screen = GrabScreen.Instance ?? grabScreen;
		if (screen != null && screen.IsGrabbed && !DragonGrabHooks.UseChaserBossPresentation(screen.GrabbingEnemy)
			&& (screen.GrabbingEnemy == null
				|| (screen.GrabbingEnemy.GetComponent<BaphometGargoyleMarker>() == null
					&& screen.GrabbingEnemy.GetComponent<GravySerpentMarker>() == null)))
		{
			EnsureFullscreenGrabUi(screen);
		}
	}

	[HarmonyPatch(typeof(GrabScreen), "ShowGrabUI")]
	[HarmonyPostfix]
	private static void ShowGrabUI_PrepareFullscreen_Postfix(GrabScreen __instance)
	{
        if(!OverkeksMode.HooksAllowed) return;
		if (DragonGrabHooks.UseChaserBossPresentation(__instance != null ? __instance.GrabbingEnemy : null))
		{
			return;
		}
		if (__instance != null && __instance.GrabbingEnemy != null
			&& (__instance.GrabbingEnemy.GetComponent<BaphometGargoyleMarker>() != null
				|| __instance.GrabbingEnemy.GetComponent<GravySerpentMarker>() != null))
		{
			return;
		}
		EnsureFullscreenGrabUi(__instance);
	}

	/// <summary>Chaser cleanup disables the grab animator; vanilla H-scenes need it on or grabImage stays blank/white.</summary>
	internal static void EnsureGrabAnimatorForFullscreenGrab(GrabScreen grabScreen)
	{
		if (!OverkeksMode.HooksAllowed || grabScreen == null)
		{
			return;
		}
		Animator grabAnimator = Traverse.Create((object)grabScreen).Field("grabScreenAnimator").GetValue<Animator>();
		if (grabAnimator != null && !grabAnimator.enabled)
		{
			grabAnimator.enabled = true;
		}
	}

	[HarmonyPatch(typeof(GrabScreen), "UpdateGrabOverlayIntensity")]
	[HarmonyPrefix]
	private static bool UpdateGrabOverlay_SkipWhenHiddenOrChaser(GrabScreen __instance)
	{
        if(!OverkeksMode.HooksAllowed) return true;
		if (!OverkeksMode.HooksAllowed || __instance == null)
		{
			return true;
		}
		if (DragonGrabHooks.IsChaserBossGrabPresentation(__instance))
		{
			ResetGrabOverlayColor(__instance);
			return false;
		}
		GameObject grabOverlay = Traverse.Create((object)__instance).Field("grabOverlay").GetValue<GameObject>();
		if (grabOverlay == null || !grabOverlay.activeInHierarchy)
		{
			return false;
		}
		return true;
	}

	internal static void EnsurePlayerViewActive(GrabScreen grabScreen)
	{
		if (!OverkeksMode.HooksAllowed)
		{
			return;
		}
		if (grabScreen != null)
		{
			Traverse traverse = Traverse.Create((object)grabScreen);
			EnableCamera(traverse.Field("playerCamera").GetValue<Camera>());
			EnableBehaviour(traverse.Field("cameraController").GetValue<Behaviour>());
			EnableBehaviour(traverse.Field("playerController").GetValue<Behaviour>());
			EnableBehaviour(traverse.Field("cmfSmoothPosition").GetValue<Behaviour>());
			EnableBehaviour(traverse.Field("cmfSmoothRotation").GetValue<Behaviour>());
			Rigidbody playerRigidbody = traverse.Field("playerRigidbody").GetValue<Rigidbody>();
			if (playerRigidbody != null)
			{
				playerRigidbody.isKinematic = false;
			}
		}
		EnablePlayerTaggedCameras();
	}

	internal static void RestorePlayerViewAfterGrab(GrabScreen grabScreen)
	{
		if (!OverkeksMode.HooksAllowed)
		{
			return;
		}
		if (grabScreen != null)
		{
			Traverse traverse = Traverse.Create((object)grabScreen);
			if (traverse.Method("RestorePlayerControls", System.Array.Empty<object>()).MethodExists())
			{
				traverse.Method("RestorePlayerControls", System.Array.Empty<object>()).GetValue();
			}
		}
		EnsurePlayerViewActive(grabScreen);
		if (grabScreen != null)
		{
			GrabStruggleHooks.SnapPlayerViewLikeDebugTeleport(grabScreen);
		}
		EnsurePlayerViewActive(grabScreen);
	}

	internal static void ScheduleRestoreAfterGrab(GrabScreen grabScreen)
	{
		if (!OverkeksMode.HooksAllowed || (Object)(object)Plugin.Instance == null)
		{
			RestorePlayerViewAfterGrab(grabScreen);
			return;
		}
		OverkeksMode.StartCoroutine(RestoreAfterGrabDelayed(grabScreen));
	}

	private static IEnumerator RestoreAfterGrabDelayed(GrabScreen grabScreen)
	{
		yield return null;
		yield return null;
		GrabScreen screen = GrabScreen.Instance ?? grabScreen;
		if (screen != null && screen.IsGrabbed && !DragonGrabHooks.UseChaserBossPresentation(screen.GrabbingEnemy))
		{
			yield break;
		}
		RestorePlayerViewAfterGrab(screen);
	}

	private static void EnablePlayerTaggedCameras()
	{
		GameObject player = GameObject.FindGameObjectWithTag("Player");
		if (player == null)
		{
			return;
		}
		Camera[] cameras = player.GetComponentsInChildren<Camera>(includeInactive: true);
		for (int i = 0; i < cameras.Length; i++)
		{
			EnableCamera(cameras[i]);
		}
	}

	private static void EnableCamera(Camera camera)
	{
		if (camera == null)
		{
			return;
		}
		if (!camera.gameObject.activeInHierarchy)
		{
			camera.gameObject.SetActive(value: true);
		}
		camera.enabled = true;
	}

	private static void EnableBehaviour(Behaviour behaviour)
	{
		if (behaviour != null)
		{
			behaviour.enabled = true;
		}
	}

	[HarmonyPatch(typeof(GrabScreen), "RestorePlayerControls")]
	[HarmonyPostfix]
	private static void RestorePlayerControls_Postfix(GrabScreen __instance)
	{
        if(!OverkeksMode.HooksAllowed) return;
		if (__instance != null && __instance.IsGrabbed && DragonGrabHooks.IsChaserBossGrabPresentation(__instance))
		{
			EnsurePlayerViewActive(__instance);
		}
	}

	[HarmonyPatch(typeof(GrabScreen), "HideGrabUI")]
	[HarmonyPostfix]
	private static void HideGrabUI_ResetBaseline_Postfix(GrabScreen __instance)
	{
        if(!OverkeksMode.HooksAllowed) return;
		ResetGrabOverlayColor(__instance);
	}
}
