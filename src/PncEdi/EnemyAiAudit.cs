using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace PncEdi;

// Startup self-check for the two hand-built tables in AiStateGuard and EnemyGrabGate.
//
// Both encode facts read out of Assembly-CSharp on 2026-08-17 - which AI classes have states
// their own UpdateStateMachine cannot handle, which coroutine field drives each of those states,
// and which classes call GrabScreen.StartGrab without checking whether it was refused. None of
// that is discoverable at runtime, so both are hardcoded, and both fail SILENTLY if the game
// changes underneath them:
//
//   - a private field renamed (grabCoroutine -> ...) makes AccessTools.Field return null, the
//     spec ends up with no stuck states, and the watchdog quietly stops watching
//   - a new enemy class with the same shape is simply not in the table
//   - a new state added to an existing class is not in the table either
//
// A silently disabled guard is worse than no guard, because the bug it covers looks fixed. So
// this runs once at startup and says what it found. Anything printed as GAP or BROKEN is a
// prompt to redo the audit in CHANGELOG §46; a clean run prints one "ok" line.
//
// The heuristics are deliberately broad - "declares a GrabScreen field" for grab callers, "has a
// currentState field" for state machines. They over-report rather than under-report, because the
// cost of a false positive is one log line and the cost of a false negative is a hang nobody
// finds for three sessions.
internal static class EnemyAiAudit
{
	// Every MonoBehaviour that declared a GrabScreen field as of the audit. Each was checked by
	// hand for whether it re-tests IsGrabbed after calling StartGrab (see EnemyGrabGate).
	private static readonly HashSet<string> KnownGrabCallers = new HashSet<string>(StringComparer.Ordinal)
	{
		"EnemyAI", "ChargingEnemyAI", "ProjectileEnemyAI", "SpinningEnemyAI",
		"DragonEnemyAI", "ProximityDragonEnemyAI", "MimicEnemy",
		// 0.3.1's Black Serpent. LandGrab re-tests grabScreen.IsGrabbed straight after
		// StartGrab and returns if it was refused, so it needs no EnemyGrabGate prefix (§76).
		"BrawlerEnemyAI"
	};

	// Every MonoBehaviour with a currentState field as of the audit, i.e. everything whose
	// UpdateStateMachine switch had to be read to decide whether AiStateGuard must cover it.
	private static readonly HashSet<string> KnownStateMachines = new HashSet<string>(StringComparer.Ordinal)
	{
		"EnemyAI", "ChargingEnemyAI", "ProjectileEnemyAI", "SpinningEnemyAI",
		"DragonEnemyAI", "ProximityDragonEnemyAI",
		// 0.3.1's Black Serpent. UpdateStateMachine has a case for all five non-Dead states
		// (Idle, Chasing, Attacking, Shooting, Hypnotising), so no AiStateGuard entry (§76).
		// What it DID need was the keep-alive and reactivation coverage: RemoveEnemyAfterGrab
		// parks it in Dead without setting isDead.
		"BrawlerEnemyAI"
	};

	internal static void Run()
	{
		try
		{
			int gaps = 0;
			gaps += AuditReflection();
			gaps += AuditStateValues();
			gaps += AuditTypes();
			if (gaps == 0)
			{
				Plugin.DBG("AI-AUDIT", "ok - guard tables match the game assembly (see CHANGELOG §46 if this ever changes)");
			}
			else
			{
				Plugin.DBG("AI-AUDIT", gaps + " item(s) need attention - the AI guards may not cover this build. Re-run the audit in CHANGELOG §46.");
			}
		}
		catch (Exception ex)
		{
			Plugin.DBG("AI-AUDIT", "audit failed: " + ex.Message);
		}
	}

	// The silent-disable case: a renamed field leaves the watchdog compiled, patched, running and
	// doing nothing at all.
	private static int AuditReflection()
	{
		int gaps = 0;
		foreach (Type t in new[] { typeof(SpinningEnemyAI), typeof(ChargingEnemyAI) })
		{
			foreach (string member in (t == typeof(SpinningEnemyAI))
				? new[] { "currentState", "isDead", "grabCoroutine", "shootCoroutine" }
				: new[] { "currentState", "isDead", "chargeCoroutine" })
			{
				if (HarmonyLib.AccessTools.Field(t, member) == null)
				{
					Plugin.DBG("AI-AUDIT", $"BROKEN: {t.Name}.{member} no longer exists - AiStateGuard cannot watch this class");
					gaps++;
				}
			}
			foreach (string method in new[] { "IsPlayerGrabbedByGrabScreen", "TransitionToIdle" })
			{
				if (HarmonyLib.AccessTools.Method(t, method, Array.Empty<Type>(), (Type[])null) == null)
				{
					Plugin.DBG("AI-AUDIT", $"BROKEN: {t.Name}.{method}() no longer exists - AiStateGuard cannot watch this class");
					gaps++;
				}
			}
		}
		return gaps;
	}

	// AiStateGuard keys on the raw int of the state (Grabbing = 2), because that is what the
	// field holds. Inserting a state into the middle of an AIState enum therefore repoints the
	// whole table without renaming anything - the guard would happily watch the wrong state and
	// still look healthy. Names are the stable thing, so check the names sit at the expected
	// values.
	private static int AuditStateValues()
	{
		int gaps = 0;
		var expected = new Dictionary<Type, Dictionary<int, string>>
		{
			[typeof(SpinningEnemyAI)] = new Dictionary<int, string> { [2] = "Grabbing", [3] = "Shooting" },
			[typeof(ChargingEnemyAI)] = new Dictionary<int, string> { [2] = "PreparingCharge", [3] = "Charging" }
		};
		foreach (var pair in expected)
		{
			FieldInfo stateField = HarmonyLib.AccessTools.Field(pair.Key, "currentState");
			if (stateField == null || !stateField.FieldType.IsEnum)
			{
				continue;
			}
			foreach (var slot in pair.Value)
			{
				string actual = Enum.GetName(stateField.FieldType, slot.Key);
				if (!string.Equals(actual, slot.Value, StringComparison.Ordinal))
				{
					Plugin.DBG("AI-AUDIT", $"BROKEN: {pair.Key.Name}.AIState value {slot.Key} is now '{actual ?? "<none>"}', " +
						$"expected '{slot.Value}' - the AiStateGuard table is keyed on the number and is now watching the wrong state");
					gaps++;
				}
			}
		}
		return gaps;
	}

	// New enemy classes. Anything that can start a grab, or that runs its own state machine, has
	// to be read the way the six were in §46 before it can be assumed safe.
	private static int AuditTypes()
	{
		int gaps = 0;
		Type[] types;
		try
		{
			types = typeof(EnemyAI).Assembly.GetTypes();
		}
		catch (ReflectionTypeLoadException ex)
		{
			types = ex.Types;
		}
		foreach (Type t in types)
		{
			if (t == null || !typeof(MonoBehaviour).IsAssignableFrom(t))
			{
				continue;
			}
			// Every class the game itself defines is in the global namespace - EnemyAI,
			// SpinningEnemyAI, MimicEnemy, GrabScreen. Everything namespaced is a third-party
			// asset (PixelCrushers.*, DunGen.*, Unity.*). The first run of this audit reported
			// PixelCrushers.GridController.DualWieldingSystem (holds a GrabScreen reference but
			// never calls StartGrab) and DunGen.Demo.AutoDoor (a door with a state enum) - both
			// false positives, and both indistinguishable from game code once Type.Name has
			// dropped the namespace. If the game ever namespaces its own code this filter has
			// to go, but that would be a far bigger change than this audit.
			if (t.Namespace != null)
			{
				continue;
			}
			FieldInfo[] fields;
			try
			{
				fields = t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
			}
			catch (Exception)
			{
				continue;
			}
			bool declaresGrabScreen = false;
			bool declaresCurrentState = false;
			foreach (FieldInfo f in fields)
			{
				if (f.FieldType == typeof(GrabScreen))
				{
					declaresGrabScreen = true;
				}
				if (f.Name == "currentState" && f.FieldType.IsEnum)
				{
					declaresCurrentState = true;
				}
			}
			if (declaresGrabScreen && !KnownGrabCallers.Contains(t.Name))
			{
				Plugin.DBG("AI-AUDIT", $"GAP: {t.FullName} can start a grab and was not in the §46 audit. Check whether it re-tests " +
					"IsGrabbed after StartGrab; if not it needs an EnemyGrabGate prefix or it will commit state to refused grabs.");
				gaps++;
			}
			if (declaresCurrentState && !KnownStateMachines.Contains(t.Name))
			{
				Plugin.DBG("AI-AUDIT", $"GAP: {t.FullName} runs its own state machine and was not in the §46 audit. Check whether " +
					"UpdateStateMachine has a case for every non-Dead state; states with no case can only be left by a coroutine " +
					"and need an AiStateGuard entry.");
				gaps++;
			}
		}
		return gaps;
	}
}
