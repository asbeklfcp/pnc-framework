using System;
using System.Collections;
using System.Runtime.CompilerServices;
using HarmonyLib;
using PixelCrushers.GridController;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace PncEdi;

[HarmonyPatch]
public static class GrabStruggleHooks
{
	private static bool _hasSnapshot;
	private static bool _restoreOnEndGrab;
	private static Vector3 _preGrabPlayerPosition;
	private static Quaternion _preGrabPlayerRotation;
	private static Vector3 _preGrabCameraLocalPos;
	private static Quaternion _preGrabCameraLocalRot;
	private static float _preGrabCameraPitch;
	private static float _preGrabCameraYaw;

	internal static bool MatchesEnemyKeyList(GameObject enemy, string configKeys)
	{
		if (string.IsNullOrWhiteSpace(configKeys) || enemy == null)
		{
			return false;
		}
		string haystack = (NameRemap.ResolveEnemyKey(enemy.name) + " " + enemy.name).Trim();
		string[] keys = configKeys.Split(new char[1] { ';' }, StringSplitOptions.RemoveEmptyEntries);
		foreach (string entry in keys)
		{
			string key = entry.Trim();
			if (key.Length > 0 && haystack.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return true;
			}
		}
		return false;
	}

	internal static bool MatchesGameOverGrabEnemy(GameObject enemy)
	{
		return MatchesEnemyKeyList(enemy, (OverkeksMode.UsesRecipe ? OverkeksConfig.CfgGrabGameOverGrabEnemyKeys : Plugin.CfgGrabGameOverGrabEnemyKeys).Value);
	}

	internal static bool IsDragonGrabEnemy(GameObject enemy)
	{
		if (enemy == null)
		{
			return false;
		}
		return enemy.GetComponent<DragonEnemyAI>() != null || enemy.GetComponent<ProximityDragonEnemyAI>() != null;
	}

	internal static bool IsWendigoGrabEnemy(GameObject enemy)
	{
		return MatchesEnemyKeyList(enemy, "wendigo");
	}

	internal static bool IsChaserBossGrabEnemy(GameObject enemy)
	{
		return !ChaserFodderSpawn.IsFodder(enemy) && (IsDragonGrabEnemy(enemy) || IsWendigoGrabEnemy(enemy));
	}

	internal static void HandleBlockedGrabGameOver(GrabScreen grabScreen)
	{
		if (Plugin.GameplayTweaksEnabled && Plugin.GodModeEnabled && !(grabScreen == null) && grabScreen.IsGrabbed)
		{
			GameObject grabbingEnemy = grabScreen.GrabbingEnemy;
			if (IsDragonGrabEnemy(grabbingEnemy))
			{
				ApplyGodModeGrabHint(grabScreen);
			}
			else if (MatchesGameOverGrabEnemy(grabbingEnemy))
			{
				ApplyGodModeGrabHint(grabScreen);
			}
			else
			{
				ApplyGodModeGrabHint(grabScreen);
			}
		}
	}

	internal static void ApplyGodModeGrabHint(GrabScreen grabScreen)
	{
		if (Plugin.GameplayTweaksEnabled && Plugin.GodModeEnabled && !(grabScreen == null))
		{
			Text grabText = grabScreen.GrabText;
			if (grabText != null)
			{
				grabText.text = "Press button to struggle!";
			}
		}
	}

	internal static bool ShouldBlockGrabAttacks()
	{
		if (!Plugin.GameplayTweaksEnabled || !(OverkeksMode.UsesRecipe ? OverkeksConfig.CfgBlockAttacksDuringGrabStruggle : Plugin.CfgBlockAttacksDuringGrabStruggle).Value)
		{
			return false;
		}
		GrabScreen grabScreen = GrabScreen.Instance;
		return grabScreen != null && grabScreen.IsGrabbed;
	}

	private static void StopWeaponAttacksForGrab()
	{
		if (ShouldBlockGrabAttacks())
		{
			DualWieldingSystem dualWieldingSystem = DualWieldingSystem.Instance;
			if (dualWieldingSystem != null)
			{
				dualWieldingSystem.StopAllAttacks();
			}
		}
	}

	private static Transform ResolveCameraRoot(GrabScreen grabScreen)
	{
		if (grabScreen == null)
		{
			return null;
		}
		Traverse traverse = Traverse.Create((object)grabScreen);
		Transform root = traverse.Field("cameraRoot").GetValue<Transform>();
		if (root != null)
		{
			return root;
		}
		Component playerController = traverse.Field("playerController").GetValue<Component>();
		if (playerController == null)
		{
			return null;
		}
		Transform cameraTransform = Traverse.Create((object)playerController).Field("cameraTransform").GetValue<Transform>();
		if (cameraTransform == null)
		{
			return null;
		}
		root = cameraTransform;
		Transform controllerTransform = ((playerController != null) ? playerController.transform : null);
		while (root.parent != null && controllerTransform != null && (Object)(object)root.parent != (Object)(object)controllerTransform)
		{
			root = root.parent;
		}
		return root;
	}

	private static void SavePreGrabTransform(GrabScreen grabScreen)
	{
		_hasSnapshot = false;
		_restoreOnEndGrab = false;
		if (!Plugin.GameplayTweaksEnabled || grabScreen == null)
		{
			return;
		}
		GameObject player = GameObject.FindGameObjectWithTag("Player");
		Transform cameraRoot = ResolveCameraRoot(grabScreen);
		Traverse traverse = Traverse.Create((object)grabScreen);
		Component cameraController = traverse.Field("cameraController").GetValue<Component>();
		if (!(player == null) && !(cameraRoot == null))
		{
			Transform transform = player.transform;
			_preGrabPlayerPosition = transform.position;
			_preGrabPlayerRotation = transform.rotation;
			_preGrabCameraLocalPos = cameraRoot.localPosition;
			_preGrabCameraLocalRot = cameraRoot.localRotation;
			if (cameraController != null)
			{
				_preGrabCameraPitch = Traverse.Create((object)cameraController).Method("GetCurrentXAngle", Array.Empty<object>()).GetValue<float>();
				_preGrabCameraYaw = Traverse.Create((object)cameraController).Method("GetCurrentYAngle", Array.Empty<object>()).GetValue<float>();
			}
			_hasSnapshot = true;
		}
	}

	private static void ApplyPreGrabTransform(GrabScreen grabScreen)
	{
		if (!_hasSnapshot || grabScreen == null)
		{
			return;
		}
		GameObject gameObject = GameObject.FindGameObjectWithTag("Player");
		Traverse traverse = Traverse.Create((object)grabScreen);
		Transform cameraRoot = ResolveCameraRoot(grabScreen);
		Behaviour cmfSmoothPosition = traverse.Field("cmfSmoothPosition").GetValue<Behaviour>();
		Behaviour cmfSmoothRotation = traverse.Field("cmfSmoothRotation").GetValue<Behaviour>();
		Behaviour playerController = traverse.Field("playerController").GetValue<Behaviour>();
		Rigidbody rigidbody = traverse.Field("playerRigidbody").GetValue<Rigidbody>();
		if (gameObject != null)
		{
			Transform transform = gameObject.transform;
			CharacterController characterController = gameObject.GetComponent<CharacterController>();
			if (characterController != null)
			{
				((Collider)characterController).enabled = false;
			}
			transform.SetPositionAndRotation(_preGrabPlayerPosition, _preGrabPlayerRotation);
			if (characterController != null)
			{
				((Collider)characterController).enabled = true;
			}
			if (rigidbody == null)
			{
				rigidbody = gameObject.GetComponent<Rigidbody>();
			}
			if (rigidbody != null)
			{
				rigidbody.isKinematic = false;
				rigidbody.linearVelocity = Vector3.zero;
				rigidbody.angularVelocity = Vector3.zero;
			}
		}
		if (cameraRoot != null)
		{
			cameraRoot.localPosition = _preGrabCameraLocalPos;
			cameraRoot.localRotation = _preGrabCameraLocalRot;
		}
		Behaviour cameraController = traverse.Field("cameraController").GetValue<Behaviour>();
		if (cameraController != null)
		{
			cameraController.enabled = true;
			Traverse.Create((object)cameraController).Method("SetRotationAngles", new object[2] { _preGrabCameraPitch, _preGrabCameraYaw }).GetValue();
		}
		if (playerController != null)
		{
			playerController.enabled = true;
		}
		if (cmfSmoothPosition != null)
		{
			cmfSmoothPosition.enabled = true;
			Traverse.Create((object)cmfSmoothPosition).Method("ResetCurrentPosition", Array.Empty<object>()).GetValue();
		}
		if (cmfSmoothRotation != null)
		{
			cmfSmoothRotation.enabled = true;
			Traverse.Create((object)cmfSmoothRotation).Method("ResetCurrentRotation", Array.Empty<object>()).GetValue();
		}
	}

	internal static void SnapPlayerViewLikeDebugTeleport(GrabScreen grabScreen)
	{
		if (!Plugin.GameplayTweaksEnabled || grabScreen == null)
		{
			return;
		}
		GameObject gameObject = GameObject.FindGameObjectWithTag("Player");
		if (!(gameObject == null))
		{
			Transform transform = gameObject.transform;
			Vector3 vector3 = (_hasSnapshot ? _preGrabPlayerPosition : transform.position);
			Quaternion quaternion = (_hasSnapshot ? _preGrabPlayerRotation : transform.rotation);
			CharacterController characterController = gameObject.GetComponent<CharacterController>();
			if (characterController != null)
			{
				((Collider)characterController).enabled = false;
			}
			transform.SetPositionAndRotation(vector3, quaternion);
			if (characterController != null)
			{
				((Collider)characterController).enabled = true;
			}
			Rigidbody rigidbody = Traverse.Create((object)grabScreen).Field("playerRigidbody").GetValue<Rigidbody>();
			if (rigidbody == null)
			{
				rigidbody = gameObject.GetComponent<Rigidbody>();
			}
			if (rigidbody != null)
			{
				rigidbody.linearVelocity = Vector3.zero;
				rigidbody.angularVelocity = Vector3.zero;
			}
			Transform cameraRoot = ResolveCameraRoot(grabScreen);
			if (cameraRoot != null && _hasSnapshot)
			{
				cameraRoot.localPosition = _preGrabCameraLocalPos;
				cameraRoot.localRotation = _preGrabCameraLocalRot;
			}
			Behaviour cameraController = Traverse.Create((object)grabScreen).Field("cameraController").GetValue<Behaviour>();
			if (cameraController != null && _hasSnapshot)
			{
				Traverse.Create((object)cameraController).Method("SetRotationAngles", new object[2] { _preGrabCameraPitch, _preGrabCameraYaw }).GetValue();
			}
			Behaviour cmfSmoothPosition = Traverse.Create((object)grabScreen).Field("cmfSmoothPosition").GetValue<Behaviour>();
			Behaviour cmfSmoothRotation = Traverse.Create((object)grabScreen).Field("cmfSmoothRotation").GetValue<Behaviour>();
			if (cmfSmoothPosition != null)
			{
				Traverse.Create((object)cmfSmoothPosition).Method("ResetCurrentPosition", Array.Empty<object>()).GetValue();
			}
			if (cmfSmoothRotation != null)
			{
				Traverse.Create((object)cmfSmoothRotation).Method("ResetCurrentRotation", Array.Empty<object>()).GetValue();
			}
		}
	}

	internal static void RestorePreGrabTransform(GrabScreen grabScreen)
	{
		if (Plugin.GameplayTweaksEnabled && !(grabScreen == null) && _restoreOnEndGrab && _hasSnapshot)
		{
			_restoreOnEndGrab = false;
			ApplyPreGrabTransform(grabScreen);
			Plugin.DBG("GRAB-RESTORE", "player -> " + _preGrabPlayerPosition.ToString());
			if (Plugin.Instance != null)
			{
				Plugin.Instance.StartCoroutine(DelayedRestorePreGrab(grabScreen));
			}
			else
			{
				ClearSnapshot();
			}
		}
	}

	private static IEnumerator DelayedRestorePreGrab(GrabScreen grabScreen)
	{
		yield return null;
		yield return null;
		if (grabScreen != null && _hasSnapshot)
		{
			ApplyPreGrabTransform(grabScreen);
			Plugin.DBG("GRAB-RESTORE", "delayed player -> " + _preGrabPlayerPosition.ToString());
		}
		ClearSnapshot();
	}

	private static void ClearSnapshot()
	{
		_hasSnapshot = false;
		_restoreOnEndGrab = false;
	}

	[HarmonyPatch(typeof(GrabScreen), "StartGrab")]
	[HarmonyPrefix]
	public static void StartGrab_Prefix(GrabScreen __instance)
	{
		SavePreGrabTransform(__instance);
	}

	[HarmonyPatch(typeof(GrabScreen), "StartGrab")]
	[HarmonyPostfix]
	public static void StartGrab_Postfix(GrabScreen __instance, GameObject enemy)
	{
		if (!Plugin.GameplayTweaksEnabled || !__instance.IsGrabbed)
		{
			ClearSnapshot();
			return;
		}
		_restoreOnEndGrab = _hasSnapshot;
		if (Plugin.GodModeEnabled)
		{
			ApplyGodModeGrabHint(__instance);
		}
		if (IsChaserBossGrabEnemy(enemy))
		{
			DragonGrabGameOverGuard.OnChaserBossGrabStarted(enemy);
		}
		HeatLockSystem.EnsureSceneEntrySurvival("grab");
		HeatLockSystem.NoteGrabSceneTriggered(enemy);
		HeatLockSystem.ApplySceneEntryPenalty("grab");
		GrabEndHelper.OnGrabSessionStarted(enemy);
		SceneEscapeGate.BeginScene();
		Vector3 origin = ((enemy != null) ? enemy.transform.position : __instance.transform.position);
		// A wall-picture trap *is* the scene - its artwork is the picture on the wall the player is
		// looking at - so hiding the grabbing enemy would hide what they came to see. Every other
		// enemy nearby is still hidden, which is what the sweep is for.
		bool hidePrimary = !CustomEnemyBridge.OwnsSceneVisual(enemy);
		NearbyEnemyHider.HideForScene(origin, enemy, hidePrimary);
		StopWeaponAttacksForGrab();
	}

	[HarmonyPatch(typeof(DualWieldingSystem), "PerformAttack")]
	[HarmonyPrefix]
	public static bool BlockPerformAttackDuringGrab()
	{
		if (ShouldBlockGrabAttacks())
		{
			return false;
		}
		return true;
	}

	[HarmonyPatch(typeof(DualWieldingSystem), "PerformSpecialAttack")]
	[HarmonyPrefix]
	public static bool BlockPerformSpecialAttackDuringGrab()
	{
		if (ShouldBlockGrabAttacks())
		{
			return false;
		}
		return true;
	}

	[HarmonyPatch(typeof(GrabScreen), "OnPlayerAttackDuringGrab")]
	[HarmonyPrefix]
	public static bool BlockOnPlayerAttackDuringGrab()
	{
		if (ShouldBlockGrabAttacks())
		{
			return false;
		}
		return true;
	}

	[HarmonyPatch(typeof(DualWieldingSystem), "StartBlocking")]
	[HarmonyPrefix]
	public static bool BlockBlockingDuringGrab()
	{
		if (ShouldBlockGrabAttacks())
		{
			return false;
		}
		return true;
	}

	[HarmonyPatch(typeof(GrabScreen), "EndGrab")]
	[HarmonyPrefix]
	public static void EndGrab_CaptureEnemy_Prefix(GrabScreen __instance, out GameObject __state)
	{
		__state = (__instance.IsGrabbed ? __instance.GrabbingEnemy : null);
		// On the vanilla-hiding path `grabbingEnemy` can already be null here - the enemy moved
		// to `hiddenEnemyToRestore` when it hid itself - and a null __state means the postfix
		// applies the mod's re-grab cooldown to nothing at all. That is the enemy the cooldown
		// is most needed on: vanilla's own EndGrabHidden sets `lastGrabTime = now` and nothing
		// more, so a miniboss can be back on the player inside a second.
		if (__state == null)
		{
			__state = Traverse.Create((object)__instance).Field("hiddenEnemyToRestore").GetValue<GameObject>();
		}
	}

	[HarmonyPatch(typeof(GrabScreen), "EndGrab")]
	[HarmonyPostfix]
	[HarmonyPriority(100)]
	public static void EndGrab_Postfix(GrabScreen __instance, GameObject __state)
	{
		RestorePreGrabTransform(__instance);
		GrabEndHelper.StabilizeAfterGrabEnd(__state);
	}
}
