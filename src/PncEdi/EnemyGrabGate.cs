using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PncEdi;

// One rule, applied to every enemy: do not commit to a grab the game is going to refuse.
//
// GrabScreen.StartGrab opens with
//
//     if (isGrabbed) return;
//     if (playerStats == null || !playerStats.CanBeGrabbed) return;
//
// and does none of its setup in those cases. The ~1 s post-grab immunity (vanilla's own
// GrabScreen.GrabImmunity coroutine) therefore turns every enemy's grab connection into a
// silent no-op - while the ENEMY side has already run whatever it does before the call.
//
// Audited across the whole assembly, every call site of GrabScreen.StartGrab. They fall into
// three groups:
//
//   handles refusal correctly (`if (!grabScreen.IsGrabbed) return;` straight after):
//     EnemyAI.HandleGrabHit, EnemyAI.OnPlayerGrabDetected,
//     SpinningEnemyAI.HandleGrabHit, ProjectileEnemyAI.OnGrabProjectileHitPlayer
//
//   does NOT check, and commits state regardless:
//     DragonEnemyAI.HandleGrabHit / .OnPlayerGrabDetected
//     ProximityDragonEnemyAI.HandleGrabHit / .OnPlayerGrabDetected
//       -> sets hasTriggeredDragonGrab (one-shot), calls ModifyGrabForDragon(),
//          unconditionally stops grabCoroutine, then calls RemoveEnemyAfterGrab().
//          A refused grab removes the dragon from the level with no scene ever playing.
//     MimicEnemy.TriggerMimicGrab
//       -> burns hasTriggeredGrab and destroys itself; see MimicGrabGate (§44), which is the
//          same rule applied at the mimic's own entry point.
//
//   not an enemy path: GrappleScreenobject.TriggerTrioGrabOverflow (left alone).
//
// So the dragons had the same defect as the mimic and nobody had hit it yet, and the four
// "correct" ones are only correct about the grab - SpinningEnemyAI still strands its state
// machine for unrelated reasons (see AiStateGuard).
//
// ONLY the dragons are patched, and that restriction is load-bearing rather than caution.
// Vanilla's refused-grab path in the four correct classes does real work:
//
//     hasGrabbedThisAttempt = true;    // this swing is spent
//     grabInDamagingState = false;     // stop testing for the rest of the window
//     StartGrab(...);
//     if (!grabScreen.IsGrabbed) return;
//
// Those two assignments are what make a refused grab *whiff*. Gating the method skips them, so
// the enemy would keep testing every frame and connect the instant immunity lapsed - escape a
// grab and be re-grabbed by the same enemy a second later, mid-swing. That is a worse game than
// the one being fixed, and those three classes have no bug to fix in the first place. Left
// alone deliberately; do not "finish the set" by adding them.
//
// For the dragons there is no such trade: the alternative to gating is being deleted from the
// level. They pay the same cost (their swing no longer self-cancels) for a much larger benefit.
// MimicEnemy is handled at its own entry point in MimicGrabGate (§44) for the same reason.
[HarmonyPatch]
public static class EnemyGrabGate
{
	private static readonly Dictionary<Type, FieldInfo> _grabScreenFields = new Dictionary<Type, FieldInfo>();
	private static FieldInfo _playerStatsField;
	private static PlayerStats _cachedPlayerStats;
	private static readonly Dictionary<int, float> _lastLog = new Dictionary<int, float>();
	private static readonly Dictionary<Type, FieldInfo> _damagingStateFields = new Dictionary<Type, FieldInfo>();

	internal static void ResetForNewScene()
	{
		_lastLog.Clear();
		_cachedPlayerStats = null;
	}

	private static GrabScreen ReadGrabScreen(object ai)
	{
		Type t = ai.GetType();
		if (!_grabScreenFields.TryGetValue(t, out var field))
		{
			field = AccessTools.Field(t, "grabScreen");
			_grabScreenFields[t] = field;
		}
		GrabScreen fromField = field?.GetValue(ai) as GrabScreen;
		if (fromField != null)
		{
			return fromField;
		}
		return GrabScreen.Instance;
	}

	// Exactly the two conditions StartGrab refuses on, read off the same objects it reads.
	// Returning false when we cannot tell is deliberate: an unknown state must not suppress a
	// grab that would have worked.
	internal static bool WouldGameRefuseGrab(GrabScreen grabScreen)
	{
		if (grabScreen == null)
		{
			return false;
		}
		if (grabScreen.IsGrabbed)
		{
			return true;
		}
		if (_playerStatsField == null)
		{
			_playerStatsField = AccessTools.Field(typeof(GrabScreen), "playerStats");
		}
		PlayerStats playerStats = _playerStatsField?.GetValue(grabScreen) as PlayerStats;
		if (playerStats == null)
		{
			// Cached: this runs from HandleGrabHit, i.e. every frame per enemy, and an
			// uncached FindGameObjectWithTag there would be a per-frame scene scan for as
			// long as the grab screen's own reference stayed null.
			if (_cachedPlayerStats == null)
			{
				GameObject player = GameObject.FindGameObjectWithTag("Player");
				if (player != null)
				{
					_cachedPlayerStats = player.GetComponent<PlayerStats>();
				}
			}
			playerStats = _cachedPlayerStats;
		}
		if (playerStats == null)
		{
			return false;
		}
		return !playerStats.CanBeGrabbed;
	}

	// HandleGrabHit runs from Update EVERY FRAME and its own first line is
	// `if (!grabInDamagingState) return;`. A prefix sits in front of that, so without this check
	// the gate fires for any enemy merely ticking - which is exactly what happened in the 15:49
	// log: a Wendigo parked in the level (never spawned, just present with its AI running)
	// produced nine `refused` lines while attempting nothing at all. Every one was a false alarm,
	// and a log line that claims a save it did not make is worse than no line.
	//
	// Blocking only inside the damaging window also makes the gate minimal: outside it the
	// original would have returned immediately anyway, so there was never anything to protect.
	private static bool IsMidGrabAttempt(object ai)
	{
		Type t = ai.GetType();
		if (!_damagingStateFields.TryGetValue(t, out var field))
		{
			field = AccessTools.Field(t, "grabInDamagingState");
			_damagingStateFields[t] = field;
		}
		if (field == null)
		{
			return true;                         // unknown shape: do not silently stop gating
		}
		return (bool)(field.GetValue(ai) ?? ((object)false));
	}

	// These run from Update every frame, so a block is common and must not spam the log.
	private static bool ShouldBlock(object ai, string where, bool requireDamagingState)
	{
		if (!Plugin.GameplayTweaksEnabled || !(OverkeksMode.UsesRecipe ? OverkeksConfig.CfgEnemyGrabRespectsImmunity : Plugin.CfgEnemyGrabRespectsImmunity).Value)
		{
			return false;
		}
		if (requireDamagingState && !IsMidGrabAttempt(ai))
		{
			return false;
		}
		if (!WouldGameRefuseGrab(ReadGrabScreen(ai)))
		{
			return false;
		}
		try
		{
			GameObject go = ((Component)ai).gameObject;
			int id = go.GetInstanceID();
			if (!_lastLog.TryGetValue(id, out var last) || Time.time - last > 1.5f)
			{
				_lastLog[id] = Time.time;
				Plugin.DBG("GRAB-GATE", $"'{NameRemap.StripCloneSuffix(go.name)}' {where} refused: " +
					"the game would decline this grab (immunity or already grabbed) - not committing enemy state");
			}
		}
		catch (Exception)
		{
		}
		return true;
	}

	[HarmonyPatch(typeof(DragonEnemyAI), "HandleGrabHit")]
	[HarmonyPrefix]
	public static bool Dragon_HandleGrabHit(DragonEnemyAI __instance) => !ShouldBlock(__instance, "HandleGrabHit", requireDamagingState: true);

	[HarmonyPatch(typeof(DragonEnemyAI), "OnPlayerGrabDetected")]
	[HarmonyPrefix]
	public static bool Dragon_OnPlayerGrabDetected(DragonEnemyAI __instance) => !ShouldBlock(__instance, "OnPlayerGrabDetected", requireDamagingState: false);

	[HarmonyPatch(typeof(ProximityDragonEnemyAI), "HandleGrabHit")]
	[HarmonyPrefix]
	public static bool ProxDragon_HandleGrabHit(ProximityDragonEnemyAI __instance) => !ShouldBlock(__instance, "HandleGrabHit", requireDamagingState: true);

	[HarmonyPatch(typeof(ProximityDragonEnemyAI), "OnPlayerGrabDetected")]
	[HarmonyPrefix]
	public static bool ProxDragon_OnPlayerGrabDetected(ProximityDragonEnemyAI __instance) => !ShouldBlock(__instance, "OnPlayerGrabDetected", requireDamagingState: false);
}
