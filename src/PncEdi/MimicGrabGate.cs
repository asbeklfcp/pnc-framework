using System;
using System.Collections.Generic;
using HarmonyLib;
using System.Reflection;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PncEdi;

// Bug #13, second half: a mimic interacted with during post-grab immunity goes permanently
// inert. Diagnosed from the 2026-08-17 13:59 log, where the earlier distance hypothesis was
// disproved outright - range was 3.0 and inRange=True on every gate line.
//
// MimicEnemy.TriggerMimicGrab, from the game assembly:
//
//     if (hasTriggeredGrab || grabScreen == null) return;
//     if (grabScreen.IsGrabbed) return;      // its ONLY refusal check
//     hasTriggeredGrab = true;               // burnt unconditionally
//     PlaySound(revealSound); Instantiate(revealEffect);
//     grabScreen.StartGrab(...);             // ALSO checks CanBeGrabbed, and bails
//     Destroy(gameObject);
//
// StartGrab opens with `if (isGrabbed) return; if (playerStats == null || !playerStats
// .CanBeGrabbed) return;`, so the mimic and the thing it calls disagree about what refuses a
// grab. During immunity the mimic burns its one-shot flag, plays its reveal, and StartGrab
// no-ops. Vanilla survives that because Destroy still runs and the chest disappears;
// KeepEnemiesAfterGrab blocks the Destroy, so the chest stands there with the flag latched
// and MimicEnemy.Update gates on !hasTriggeredGrab forever. Nothing clears it, because
// EnemyReactivationHelper.RearmMimic only runs off a scene end and no scene ever started.
//
// Observed as one press 0.9 s after escaping the previous mimic scene:
//     13:58:14.212 [ENEMY-WAKE] re-armed mimic Weapons Mimic
//     13:58:14.532 [MIMIC-GATE] hasTriggeredGrab=False enabled=True   <- armed, E pressed
//     13:58:14.533 [GRAB-START] ignored: game declined the grab
//     13:58:14.784 [MIMIC-GATE] hasTriggeredGrab=True  enabled=True   <- burnt, no scene
//
// This is the third instance of the pattern in ../learnings/grab-and-ai-mechanics.md ("blocking a vanilla Destroy leaves
// one-shot state latched", §19), and the first to reach it without producing a scene at all.
//
// The window exists by construction: StabilizeAfterGrabEnd applies the immunity and schedules
// the reactivation that re-arms the mimic, and the re-arm lands ~0.6 s in while the immunity
// runs ~1 s (vanilla's GrabScreen.GrabImmunity coroutine - our EndGrabImmunitySeconds starts a
// second, longer one, but vanilla's sets CanBeGrabbed back to true at T+1 and cuts ours short).
// So there is always a stretch where the chest is armed and the game will still refuse.
[HarmonyPatch]
public static class MimicGrabGate
{
	private static FieldInfo _grabScreenField;
	private static FieldInfo _hasTriggeredField;

	// Instance id -> the time we first saw it stranded. Cleared as soon as it stops being
	// stranded, so this never grows past the number of mimics in the scene.
	private static readonly Dictionary<int, float> _strandedSince = new Dictionary<int, float>();

	private static GrabScreen GetGrabScreen(MimicEnemy mimic)
	{
		if (_grabScreenField == null)
		{
			_grabScreenField = AccessTools.Field(typeof(MimicEnemy), "grabScreen");
		}
		return _grabScreenField?.GetValue(mimic) as GrabScreen;
	}

	private static bool GetHasTriggered(MimicEnemy mimic)
	{
		if (_hasTriggeredField == null)
		{
			_hasTriggeredField = AccessTools.Field(typeof(MimicEnemy), "hasTriggeredGrab");
		}
		return _hasTriggeredField != null && (bool)(_hasTriggeredField.GetValue(mimic) ?? ((object)false));
	}

	private static void SetHasTriggered(MimicEnemy mimic, bool value)
	{
		if (_hasTriggeredField == null)
		{
			_hasTriggeredField = AccessTools.Field(typeof(MimicEnemy), "hasTriggeredGrab");
		}
		_hasTriggeredField?.SetValue(mimic, value);
	}

	// Refuse the interaction before the flag is burnt, on exactly the condition StartGrab would
	// have refused it on. Returning false here is a no-op for the player: no reveal sound, no
	// effect, no Destroy - the chest is simply not interactable for the last fraction of a
	// second of immunity, and the next press works.
	[HarmonyPatch(typeof(MimicEnemy), "TriggerMimicGrab")]
	[HarmonyPrefix]
	public static bool TriggerMimicGrab_Prefix(MimicEnemy __instance)
	{
		try
		{
			if (!Plugin.GameplayTweaksEnabled || !(OverkeksMode.UsesRecipe ? OverkeksConfig.CfgMimicGrabRespectsImmunity : Plugin.CfgMimicGrabRespectsImmunity).Value)
			{
				return true;
			}
			// Nothing to protect if the flag is already set or the chest is genuinely spent:
			// let the original run and take its own early-out.
			if (GetHasTriggered(__instance))
			{
				return true;
			}
			GrabScreen grabScreen = GetGrabScreen(__instance);
			if (grabScreen == null)
			{
				return true;
			}
			// One writer for the rule: EnemyGrabGate applies the same test at every other
			// enemy's entry point, and a second copy here would be free to drift from it.
			if (EnemyGrabGate.WouldGameRefuseGrab(grabScreen))
			{
				Plugin.DBG("MIMIC-GATE", "refused '" + NameRemap.StripCloneSuffix(__instance.gameObject.name) + "': CanBeGrabbed false (post-grab immunity) - not burning hasTriggeredGrab");
				return false;
			}
		}
		catch (Exception ex)
		{
			Plugin.DBG("MIMIC-GATE", "prefix error: " + ex.Message);
		}
		return true;
	}

	// Belt-and-braces for any OTHER route that burns the flag without producing a scene - a
	// throwing StartGrab would do it too, since that skips the Destroy the same way. Update
	// only runs while the mimic is active and enabled, which is already most of the condition:
	// during a real scene NearbyEnemyHider has the object hidden and the component disabled,
	// so this cannot fire mid-scene.
	//
	// The delay is what keeps it from shortcutting the normal path. RearmMimic runs off the
	// reactivation, which applies the grab-end cooldown deliberately; clearing the flag the
	// instant a scene ends would let a mimic be re-used sooner than intended. Waiting a few
	// seconds means the ordinary re-arm always wins and this only ever rescues a chest that
	// nothing else was going to.
	[HarmonyPatch(typeof(MimicEnemy), "Update")]
	[HarmonyPostfix]
	public static void Update_Postfix(MimicEnemy __instance)
	{
		try
		{
			if (!Plugin.GameplayTweaksEnabled || !(OverkeksMode.UsesRecipe ? OverkeksConfig.CfgKeepEnemiesAfterGrab : Plugin.CfgKeepEnemiesAfterGrab).Value)
			{
				return;
			}
			float seconds = (OverkeksMode.UsesRecipe ? OverkeksConfig.CfgMimicStrandedRearmSeconds : Plugin.CfgMimicStrandedRearmSeconds).Value;
			if (seconds <= 0f)
			{
				return;
			}
			int id = __instance.GetInstanceID();
			GrabScreen grabScreen = GetGrabScreen(__instance);
			bool sceneRunning = grabScreen != null && grabScreen.IsGrabbed;
			if (!GetHasTriggered(__instance) || sceneRunning)
			{
				_strandedSince.Remove(id);
				return;
			}
			if (!_strandedSince.TryGetValue(id, out var since))
			{
				_strandedSince[id] = Time.time;
				return;
			}
			if (Time.time - since < seconds)
			{
				return;
			}
			_strandedSince.Remove(id);
			SetHasTriggered(__instance, value: false);
			Plugin.DBG("MIMIC-GATE", $"re-armed stranded '{NameRemap.StripCloneSuffix(__instance.gameObject.name)}': hasTriggeredGrab was set with no scene running for {seconds:F1}s");
		}
		catch (Exception ex)
		{
			Plugin.DBG("MIMIC-GATE", "sweep error: " + ex.Message);
		}
	}

	internal static void ResetForNewScene()
	{
		_strandedSince.Clear();
	}
}
