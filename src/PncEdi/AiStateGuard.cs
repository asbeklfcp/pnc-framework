using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PncEdi;

// Watchdog for "in a coroutine-driven state with no coroutine" - the invariant behind bug #14.
//
// Every one of these AI classes drives itself from UpdateStateMachine, which switches on
// currentState. Auditing all six against the assembly, two of them have non-Dead states with
// **no case in that switch**, so the state machine cannot act while in them and the only way
// out is the driving coroutine finishing:
//
//   | class                  | states                                        | unhandled          |
//   | EnemyAI                | Idle Chasing Attacking Grabbing Dead          | Dead only          |
//   | ChargingEnemyAI        | Idle Chasing PreparingCharge Charging Dead    | PreparingCharge,   |
//   |                        | Stunned                                       | Charging           |
//   | ProjectileEnemyAI      | Idle Chasing Retreating Shooting Dead         | Dead only          |
//   | SpinningEnemyAI        | Idle Spinning Grabbing Shooting Dead          | Grabbing, Shooting |
//   | DragonEnemyAI          | Idle Chasing Attacking Grabbing Dead          | Dead only          |
//   | ProximityDragonEnemyAI | Idle Chasing Attacking Grabbing Dead          | Dead only          |
//
// Dead being unhandled is intended. The other four handle every state they can enter and
// recover on their own, so they are deliberately NOT patched here.

// Game 0.3.2 added ChargingEnemyAI.Stunned, and it is deliberately NOT in StuckStates below.
// Vanilla enters it when the player shakes a grappler off and leaves it from UpdateStateMachine
// on a plain `Time.time >= stunEndTime` test, so it is a state the switch *does* handle - the
// opposite of the invariant this guard watches. Guarding it would cut short three seconds of
// helplessness the game now grants on purpose. `patchaudit.py --ai` reports it as unhandled
// because that check only counts a branch that calls `Handle*`, and Stunned's branch calls
// TransitionToChasing/TransitionToIdle directly; the same heuristic has always reported
// PreparingCharge for the same reason.
//
// StuckStates is keyed by the *numeric* enum value, and 0.3.2 appended Stunned after Dead, so
// PreparingCharge and Charging are still 2 and 3. A build that inserted a state rather than
// appending one would silently re-point both entries at the wrong states, which is what
// EnemyAiAudit.AuditStateValues exists to catch - it asserts the names still sit at those
// numbers at startup, and it passed on 0.3.2.
//
// Both vulnerable classes commit the state before the coroutine is known to be running:
//
//   TransitionToGrabbing()  { currentState = Grabbing; StopSpin(); StopShooting(); StartGrab(); }
//   StartGrab()             { if (grabCoroutine != null) return;  if (!canGrab) return;
//                             if (grabScreen == null) return;  grabCoroutine = StartCoroutine(...); }
//
//   TransitionToCharging()  { currentState = Charging; StopPathfinding(); StartCharge(); }
//   StartCharge()           { if (chargeCoroutine != null) return;  if (player == null) return;
//                             chargeCoroutine = StartCoroutine(ChargeSequence()); }
//
// Any of those early-outs leaves the enemy in a state nothing can leave. Observed on Plantasha
// as state=Grabbing with every gate open and every coroutine null (§46).
//
// Watching the invariant rather than fixing TransitionToGrabbing is deliberate: there are four
// routes into that state per class, the sibling TransitionToShooting/TransitionToPreparingCharge
// have the same shape, and even a perfect transition still leaves a state the switch cannot
// handle. The condition is one line; the ways of reaching it are not.
//
// PreparingCharge and Charging are both driven by chargeCoroutine - PreparingCharge is assigned
// from inside ChargeSequence itself, so the same handle covers both.
[HarmonyPatch]
public static class AiStateGuard
{
	private sealed class Spec
	{
		internal FieldInfo CurrentState;
		internal FieldInfo IsDead;
		internal MethodInfo IsPlayerGrabbed;
		internal MethodInfo TransitionToIdle;

		// state value -> the coroutine field that is supposed to be driving it
		internal Dictionary<int, FieldInfo> StuckStates = new Dictionary<int, FieldInfo>();
		internal Dictionary<int, string> StateNames = new Dictionary<int, string>();
	}

	private static readonly Dictionary<Type, Spec> _specs = new Dictionary<Type, Spec>();
	private static readonly Dictionary<int, float> _strandedSince = new Dictionary<int, float>();

	internal static void ResetForNewScene()
	{
		_strandedSince.Clear();
	}

	private static Spec GetSpec(Type t)
	{
		if (_specs.TryGetValue(t, out var spec))
		{
			return spec;
		}
		spec = new Spec
		{
			CurrentState = AccessTools.Field(t, "currentState"),
			IsDead = AccessTools.Field(t, "isDead"),
			IsPlayerGrabbed = AccessTools.Method(t, "IsPlayerGrabbedByGrabScreen", Array.Empty<Type>(), (Type[])null),
			TransitionToIdle = AccessTools.Method(t, "TransitionToIdle", Array.Empty<Type>(), (Type[])null)
		};
		if (t == typeof(SpinningEnemyAI))
		{
			FieldInfo grab = AccessTools.Field(t, "grabCoroutine");
			FieldInfo shoot = AccessTools.Field(t, "shootCoroutine");
			if (grab != null)
			{
				spec.StuckStates[2] = grab;
				spec.StateNames[2] = "Grabbing";
			}
			if (shoot != null)
			{
				spec.StuckStates[3] = shoot;
				spec.StateNames[3] = "Shooting";
			}
		}
		else if (t == typeof(ChargingEnemyAI))
		{
			FieldInfo charge = AccessTools.Field(t, "chargeCoroutine");
			if (charge != null)
			{
				spec.StuckStates[2] = charge;
				spec.StateNames[2] = "PreparingCharge";
				spec.StuckStates[3] = charge;
				spec.StateNames[3] = "Charging";
			}
		}
		_specs[t] = spec;
		return spec;
	}

	private static void Check(MonoBehaviour ai)
	{
		if (!Plugin.GameplayTweaksEnabled)
		{
			return;
		}
		float grace = (OverkeksMode.UsesRecipe ? OverkeksConfig.CfgAiUnstickSeconds : Plugin.CfgAiUnstickSeconds).Value;
		if (grace <= 0f)
		{
			return;
		}
		Spec spec = GetSpec(ai.GetType());
		if (spec.CurrentState == null || spec.TransitionToIdle == null || spec.StuckStates.Count == 0)
		{
			return;
		}
		int id = ai.GetInstanceID();
		// Cheapest test first. This postfix runs for every spinner and every charging enemy on
		// every frame, so the ordering matters: a plain field read rejects the overwhelming
		// majority of frames before anything reflective or virtual happens.
		int state = (int)spec.CurrentState.GetValue(ai);
		if (!spec.StuckStates.TryGetValue(state, out var driver) || driver.GetValue(ai) != null)
		{
			_strandedSince.Remove(id);
			return;
		}
		if (spec.IsDead != null && (bool)(spec.IsDead.GetValue(ai) ?? ((object)false)))
		{
			_strandedSince.Remove(id);
			return;
		}
		// Mirror Update's own pause condition exactly. While a grab scene OR a cinematic is
		// running the AI is deliberately parked - PauseAIBehavior has already stopped its
		// coroutines, so it looks stranded and is not. Missing the cinematic half would let a
		// peek scene reset an enemy that was mid-charge.
		if (spec.IsPlayerGrabbed != null && (bool)(spec.IsPlayerGrabbed.Invoke(ai, Array.Empty<object>()) ?? ((object)false)))
		{
			_strandedSince.Remove(id);
			return;
		}
		if (CinematicCameraSwapTrigger.IsAnyInCinematicView)
		{
			_strandedSince.Remove(id);
			return;
		}
		if (!_strandedSince.TryGetValue(id, out var since))
		{
			// The seam where a sequence nulls its handle a frame before transitioning is
			// normal; only a state that persists past the grace period is actually stranded.
			_strandedSince[id] = Time.time;
			return;
		}
		if (Time.time - since < grace)
		{
			return;
		}
		_strandedSince.Remove(id);
		spec.TransitionToIdle.Invoke(ai, Array.Empty<object>());
		Plugin.DBG("AI-GUARD", $"unstuck '{NameRemap.StripCloneSuffix(ai.gameObject.name)}' " +
			$"({ai.GetType().Name}): state={spec.StateNames[state]} with no coroutine for {grace:F1}s -> Idle " +
			"(UpdateStateMachine has no handler for that state, so nothing else could recover it)");
	}

	[HarmonyPatch(typeof(SpinningEnemyAI), "Update")]
	[HarmonyPostfix]
	public static void Spinning_Update(SpinningEnemyAI __instance)
	{
		try
		{
			Check((MonoBehaviour)(object)__instance);
		}
		catch (Exception ex)
		{
			Plugin.DBG("AI-GUARD", "guard error: " + ex.Message);
		}
	}

	[HarmonyPatch(typeof(ChargingEnemyAI), "Update")]
	[HarmonyPostfix]
	public static void Charging_Update(ChargingEnemyAI __instance)
	{
		try
		{
			Check((MonoBehaviour)(object)__instance);
		}
		catch (Exception ex)
		{
			Plugin.DBG("AI-GUARD", "guard error: " + ex.Message);
		}
	}
}

// The other half of the same seam: stop the state being committed when the call that is meant to
// drive it is already certain to decline.
//
// The comment on AiStateGuard says watching the invariant beats fixing TransitionToGrabbing, and
// that still holds for the general case - four routes in, and even a perfect transition leaves a
// state the switch cannot handle. This is the one case where the answer is decidable *before* the
// commit: SpinningEnemyAI.StartGrab early-outs on `!canGrab` and on `grabScreen == null`, and both
// are plain state readable from the prefix. Nothing about the transition can make either true.
//
// §130 is what this was written for. A movement-only custom enemy - CharmWitchController sets
// `canGrab = false` to hold the vanilla grab shut while keeping the borrowed chase movement - sat
// in grab range and lunged twice a second for as long as the player stood there. The cycle is
// TransitionToGrabbing committing Grabbing, StartGrab declining on !canGrab, AiStateGuard
// unsticking to Idle 0.5 s later, and grab range immediately transitioning again: 20 unsticks in
// one run, every one of them that enemy, in unbroken 0.5 s trains. The watchdog was doing its job;
// what it recovered from was being re-created as fast as it cleared it.
//
// A vanilla spinner reaches this the moment anything clears canGrab, so the gate is on the class
// rather than on the witch.
//
// Declining the transition leaves currentState where it was - Idle or Spinning, both handled by
// UpdateStateMachine - so the enemy goes on being driven instead of parked. The skipped
// StopSpin/StopShooting go with it, which is correct: those exist to make room for a grab that is
// not going to happen.
[HarmonyPatch]
public static class GrabTransitionGate
{
	private static readonly HashSet<int> _announced = new HashSet<int>();

	[HarmonyPatch(typeof(SpinningEnemyAI), "TransitionToGrabbing")]
	[HarmonyPrefix]
	public static bool SpinningEnemyAI_TransitionToGrabbing(SpinningEnemyAI __instance)
	{
		try
		{
			if (__instance == null)
			{
				return true;
			}
			string reason = null;
			if (!__instance.canGrab)
			{
				reason = "canGrab is false";
			}
			else if (Traverse.Create((object)__instance).Field("grabScreen").GetValue<Object>() == null)
			{
				reason = "it has no grabScreen";
			}
			if (reason == null)
			{
				return true;
			}
			// Once per enemy: this is refused on every frame the player stands in range, and the
			// point of the fix is that nothing downstream notices.
			int id = ((Object)(object)__instance).GetInstanceID();
			if (_announced.Add(id))
			{
				Plugin.DBG("AI-GUARD", "refused Grabbing for "
					+ NameRemap.StripCloneSuffix(__instance.gameObject.name)
					+ ": " + reason + ", so StartGrab would strand it");
			}
			return false;
		}
		catch (Exception ex)
		{
			Plugin.DBG("AI-GUARD", "grab gate error: " + ex.Message);
			return true;
		}
	}

	internal static void ResetForNewScene()
	{
		_announced.Clear();
	}
}
