using System;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PncEdi;

[HarmonyPatch]
public static class CameraSwapHooks
{
	public static Animator CurrentAnimator;
	public static string CurrentKey;
	public static string CurrentRawName;
	public static bool Active;
	public static CameraSwapTrigger CurrentTrigger;
	private static FieldInfo _animatorField;
	private static FieldInfo _galleryIdField;
	private static FieldInfo _isInCinematicField;
	private static FieldInfo _isSwappingField;
	private static float _activatedAt;
	private const float GraceSeconds = 2.5f;

	private static Animator GetAnimator(CameraSwapTrigger t)
	{
		if (t == null)
		{
			return null;
		}
		if (_animatorField == null)
		{
			_animatorField = typeof(CameraSwapTrigger).GetField("animatorToTrigger", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		}
		object? obj = _animatorField?.GetValue(t);
		return (Animator)((obj is Animator) ? obj : null);
	}

	private static string GetGalleryId(CameraSwapTrigger t)
	{
		if (t == null)
		{
			return null;
		}
		if (_galleryIdField == null)
		{
			_galleryIdField = typeof(CameraSwapTrigger).GetField("galleryEntryID", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		}
		return _galleryIdField?.GetValue(t) as string;
	}

	public static bool IsInCinematic(CameraSwapTrigger t)
	{
		if (t == null)
		{
			return false;
		}
		if (_isInCinematicField == null)
		{
			_isInCinematicField = typeof(CameraSwapTrigger).GetField("isInCinematicView", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		}
		return (bool)(_isInCinematicField?.GetValue(t) ?? ((object)false));
	}

	internal static string GetCurrentGalleryId()
	{
		return GetGalleryId(CurrentTrigger);
	}

	public static bool IsSwapping(CameraSwapTrigger t)
	{
		if (t == null)
		{
			return false;
		}
		if (_isSwappingField == null)
		{
			_isSwappingField = typeof(CameraSwapTrigger).GetField("isSwapping", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		}
		return (bool)(_isSwappingField?.GetValue(t) ?? ((object)false));
	}

	[HarmonyPatch(typeof(CameraSwapTrigger), "ActivateTrigger")]
	[HarmonyPostfix]
	public static void Activate_Postfix(CameraSwapTrigger __instance)
	{
		try
		{
			if (!(__instance == null))
			{
				CurrentAnimator = GetAnimator(__instance);
				string rawName = __instance.gameObject.name ?? "";
				string galleryId = GetGalleryId(__instance);
				CurrentRawName = NameRemap.StripCloneSuffix(rawName);
				CurrentKey = ResolveTriggerKey(galleryId, rawName, CurrentAnimator);
				CurrentTrigger = __instance;
				_activatedAt = Time.realtimeSinceStartup;
				Active = true;
				HeatLockSystem.EnsureSceneEntrySurvival("interact");
				HeatLockSystem.NoteInteractSceneTriggered(__instance, galleryId, CurrentRawName, CurrentAnimator);
				HeatLockSystem.TryReleaseFromKeyhole(__instance, galleryId, CurrentRawName, CurrentAnimator);
				HeatLockSystem.TryReleaseFromService(__instance, galleryId, CurrentRawName, CurrentAnimator);
				HeatLockSystem.ApplySceneEntryPenalty("interact");
				// Peepholes are a look, not a commitment: no watch-time gate holding the
				// player in the scene.
				if (!HeatLockSystem.IsKeyholeTrigger(__instance, galleryId, CurrentRawName, CurrentAnimator))
				{
					SceneEscapeGate.BeginScene();
				}
				Plugin.DBG("INTERACT-START", "trigger='" + CurrentRawName + "' galleryId='" + galleryId + "' key=" + CurrentKey + " animator=" + ((CurrentAnimator != null) ? CurrentAnimator.name : "null"));
				DispatchActivateEdi(galleryId, CurrentRawName, CurrentAnimator);
				if (!(__instance is TrapCameraSwapTrigger))
				{
					NearbyEnemyHider.HideForScene(GetSceneOrigin(__instance));
				}
			}
		}
		catch (Exception ex)
		{
			Plugin.Log?.LogWarning($"CameraSwap Activate patch error: {ex}");
		}
	}

	private static string ResolveTriggerKey(string galleryId, string rawName, Animator anim)
	{
		if (!string.IsNullOrEmpty(galleryId))
		{
			string fromGalleryId = NameRemap.ResolveEnemyKey(galleryId);
			if (!IsBareNumber(fromGalleryId))
			{
				return fromGalleryId;
			}
		}
		if (!string.IsNullOrEmpty(rawName))
		{
			string lower = rawName.ToLowerInvariant();
			if (!lower.Contains("camera") && !lower.Contains("gloryhole") && !lower.Contains("trigger"))
			{
				string fromName = NameRemap.ResolveEnemyKey(rawName);
				if (!IsBareNumber(fromName))
				{
					return fromName;
				}
			}
		}
		if (((anim != null) ? anim.runtimeAnimatorController : null) != null)
		{
			AnimationClip[] animationClips = anim.runtimeAnimatorController.animationClips;
			if (animationClips != null)
			{
				AnimationClip[] clips = animationClips;
				foreach (AnimationClip animationClip in clips)
				{
					if (!(animationClip == null))
					{
						string clipName = animationClip.name?.ToLowerInvariant() ?? "";
						if (clipName.Contains("minotaur") || clipName.Contains("minothaur") || clipName.Contains("gravy"))
						{
							return "gravy";
						}
						if (clipName.Contains("baphomet") || clipName.Contains("baph"))
						{
							return "baphomet";
						}
						if (clipName.Contains("dragon"))
						{
							return "dragon";
						}
						if (clipName.Contains("wendigo"))
						{
							return "wendigo";
						}
					}
				}
			}
		}
		return NameRemap.ResolveEnemyKey(rawName);
	}

	private static bool IsBareNumber(string s)
	{
		if (string.IsNullOrEmpty(s))
		{
			return true;
		}
		foreach (char c in s)
		{
			if (c < '0' || c > '9')
			{
				return false;
			}
		}
		return true;
	}

	[HarmonyPatch(typeof(TrapCameraSwapTrigger), "ActivateTrapSequence")]
	[HarmonyPostfix]
	public static void TrapPhase2_Postfix(TrapCameraSwapTrigger __instance)
	{
		try
		{
			if (!(__instance == null))
			{
				CurrentAnimator = GetAnimator((CameraSwapTrigger)(object)__instance);
				string rawName = __instance.gameObject.name ?? "";
				string galleryId = GetGalleryId((CameraSwapTrigger)(object)__instance);
				CurrentRawName = NameRemap.StripCloneSuffix(rawName);
				CurrentKey = ResolveTriggerKey(galleryId, rawName, CurrentAnimator);
				CurrentTrigger = (CameraSwapTrigger)(object)__instance;
				_activatedAt = Time.realtimeSinceStartup;
				Active = true;
				HeatLockSystem.EnsureSceneEntrySurvival("interact trap");
				HeatLockSystem.NoteInteractSceneTriggered((CameraSwapTrigger)(object)__instance, galleryId, CurrentRawName, CurrentAnimator);
				HeatLockSystem.TryReleaseFromKeyhole((CameraSwapTrigger)(object)__instance, galleryId, CurrentRawName, CurrentAnimator);
				HeatLockSystem.TryReleaseFromService((CameraSwapTrigger)(object)__instance, galleryId, CurrentRawName, CurrentAnimator);
				HeatLockSystem.ApplySceneEntryPenalty("interact trap");
				if (!HeatLockSystem.IsKeyholeTrigger((CameraSwapTrigger)(object)__instance, galleryId, CurrentRawName, CurrentAnimator))
				{
					SceneEscapeGate.BeginScene();
				}
				Plugin.DBG("INTERACT-START", "trigger='" + CurrentRawName + "' galleryId='" + galleryId + "' key=" + CurrentKey + " animator=" + ((CurrentAnimator != null) ? CurrentAnimator.name : "null") + " phase=trap2");
				DispatchActivateEdi(galleryId, CurrentRawName, CurrentAnimator);
				NearbyEnemyHider.HideForScene(GetSceneOrigin((CameraSwapTrigger)(object)__instance));
			}
		}
		catch (Exception ex)
		{
			Plugin.Log?.LogWarning($"TrapPhase2 patch error: {ex}");
		}
	}

	[HarmonyPatch(typeof(CameraSwapTrigger), "ReturnToFirstPersonCoroutine")]
	[HarmonyPostfix]
	public static void Return_Postfix()
	{
		ResetState();
	}

	public static bool IsCinematicLive()
	{
		if (!Active || CurrentTrigger == null)
		{
			return false;
		}
		if (Time.realtimeSinceStartup - _activatedAt < 2.5f)
		{
			return true;
		}
		try
		{
			return IsInCinematic(CurrentTrigger) || IsSwapping(CurrentTrigger);
		}
		catch
		{
			return false;
		}
	}

	private static Vector3 GetSceneOrigin(CameraSwapTrigger trigger)
	{
		GameObject gameObject = GameObject.FindGameObjectWithTag("Player");
		if (gameObject != null)
		{
			return gameObject.transform.position;
		}
		if (trigger != null)
		{
			return trigger.transform.position;
		}
		return Vector3.zero;
	}

	public static void ResetState()
	{
		if (Active)
		{
			HeatLockSystem.CompletePendingKeyholeRelease();
			Plugin.DBG("INTERACT-END", "key=" + CurrentKey);
			NearbyEnemyHider.RestoreAll();
		}
		HeatLockSystem.EndKeyholeSceneStatus();
		GalleryHooks.InGamePeekScene = false;
		Active = false;
		CurrentAnimator = null;
		CurrentKey = null;
		CurrentRawName = null;
		CurrentTrigger = null;
		HeatLockSystem.CompleteSceneEntrySurvival("interact");
	}

	private static void DispatchActivateEdi(string galleryId, string rawName, Animator anim)
	{
		string firstClipName = GetFirstClipName(anim);
		string peekGallery = PeekGalleryMap.Resolve(galleryId, rawName, firstClipName);
		if (PeekGalleryMap.IsPeekScript(peekGallery))
		{
			GalleryHooks.InGamePeekScene = true;
			HeatLockSystem.QueueReleaseFromPeekScene(CurrentTrigger, galleryId, rawName, firstClipName);
			Plugin.DBG("PEEK-INGAME", "galleryId='" + galleryId + "' clip='" + firstClipName + "' -> " + peekGallery);
			Plugin.SendPlay(peekGallery);
			return;
		}
		GalleryHooks.InGamePeekScene = false;
		if (!string.IsNullOrEmpty(firstClipName))
		{
			string interactKey = CurrentKey ?? "interact";
			string galleryName = GrabHooks.BuildGalleryName(interactKey, firstClipName);
			Plugin.DBG("INTERACT-INIT", interactKey + "/" + firstClipName + " -> " + galleryName);
			Plugin.SendPlay(galleryName);
		}
	}

	private static string GetFirstClipName(Animator anim)
	{
		if (anim == null || anim.runtimeAnimatorController == null)
		{
			return null;
		}
		AnimationClip[] animationClips = anim.runtimeAnimatorController.animationClips;
		if (animationClips == null || animationClips.Length == 0)
		{
			return null;
		}
		for (int i = 0; i < animationClips.Length; i++)
		{
			if (animationClips[i] != null && !string.IsNullOrEmpty(animationClips[i].name))
			{
				return animationClips[i].name;
			}
		}
		return null;
	}
}
