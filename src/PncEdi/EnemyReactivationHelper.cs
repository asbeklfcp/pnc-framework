using System;
using System.Reflection;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PncEdi;

internal static class EnemyReactivationHelper
{
	private sealed class ScheduledEntry
	{
		public GameObject Enemy;
		public bool ApplyGrabEndCooldown;
		public int Token;
	}

	private static readonly Dictionary<int, ScheduledEntry> Pending = new Dictionary<int, ScheduledEntry>();
	private static int _wakeTokenCounter;
	internal static bool HasReactivationDelay => Plugin.GameplayTweaksEnabled && GetDelaySeconds() > 0f;
	internal static bool HasPendingWake => Pending.Count > 0;
    internal static void CancelScheduledFor(GameObject enemy) {
        if(enemy!=null && Pending.Remove(enemy.GetInstanceID()))Plugin.DBG("ENEMY-WAKE","cancelled stale recovery for staged "+enemy.name);
    }
    private static bool IsStagedChaser(GameObject enemy) => OverkeksMode.Active && enemy!=null &&
        ChaserSweeperHooks.IsManagedChaser(enemy) && !ChaserSweeperHooks.IsActiveManagedChaser(enemy);

	internal static void ScheduleReactivate(GameObject enemy, bool applyGrabEndCooldown = false)
	{
        if(OverkeksMode.Active && enemy!=null && IsDeadEnemy(enemy))return;
        if(IsStagedChaser(enemy))return;
		if (enemy == null)
		{
			return;
		}
		if (ShouldReactivateInstantly(enemy))
		{
			ReactivateMimicNow(enemy, applyGrabEndCooldown);
			return;
		}
		float delaySeconds = GetDelaySeconds();
		if (delaySeconds <= 0f || Plugin.Instance == null)
		{
			Reactivate(enemy, applyGrabEndCooldown);
			return;
		}
		RevealOnly(enemy);
		int instanceID = enemy.GetInstanceID();
		int token = ++_wakeTokenCounter;
		if (Pending.TryGetValue(instanceID, out var value))
		{
			value.Enemy = enemy;
			value.ApplyGrabEndCooldown = applyGrabEndCooldown || value.ApplyGrabEndCooldown;
			value.Token = token;
		}
		else
		{
			Pending[instanceID] = new ScheduledEntry
			{
				Enemy = enemy,
				ApplyGrabEndCooldown = applyGrabEndCooldown,
				Token = token
			};
			Plugin.DBG("ENEMY-WAKE", NameRemap.StripCloneSuffix(enemy.name) + " visible now, AI in " + delaySeconds + "s");
		}
		Plugin.Instance.StartCoroutine(ReactivateAfterDelay(enemy, instanceID, applyGrabEndCooldown, delaySeconds, token));
	}

	internal static void ScheduleRespawn(GameObject enemy, bool applyGrabEndCooldown = false)
	{
		if (enemy == null)
		{
			return;
		}
		if (ShouldReactivateInstantly(enemy))
		{
			ReactivateMimicNow(enemy, applyGrabEndCooldown);
			return;
		}
		float delaySeconds = GetDelaySeconds();
		if (delaySeconds <= 0f || Plugin.Instance == null)
		{
			Reactivate(enemy, applyGrabEndCooldown);
			return;
		}
		HideForRespawn(enemy);
		int instanceID = enemy.GetInstanceID();
		int token = ++_wakeTokenCounter;
		if (Pending.TryGetValue(instanceID, out var value))
		{
			value.Enemy = enemy;
			value.ApplyGrabEndCooldown = applyGrabEndCooldown || value.ApplyGrabEndCooldown;
			value.Token = token;
		}
		else
		{
			Pending[instanceID] = new ScheduledEntry
			{
				Enemy = enemy,
				ApplyGrabEndCooldown = applyGrabEndCooldown,
				Token = token
			};
		}
		Plugin.DBG("ENEMY-RESPAWN", NameRemap.StripCloneSuffix(enemy.name) + " hidden, respawn in " + delaySeconds + "s");
		Plugin.Instance.StartCoroutine(ReactivateAfterDelay(enemy, instanceID, applyGrabEndCooldown, delaySeconds, token));
	}

	internal static void CancelScheduled()
	{
		ReactivateAllPending();
	}

	internal static bool IsScheduled(GameObject enemy)
	{
		return enemy != null && Pending.ContainsKey(enemy.GetInstanceID());
	}

	internal static void TryWakeOnPlayerAttack()
	{
        if(OverkeksMode.Active)return;
		if (Plugin.GameplayTweaksEnabled && HasReactivationDelay)
		{
			if (HasPendingWake)
			{
				ReactivateAllPending("player attack");
			}
			WakeVisibleDisabledEnemies();
		}
	}

    internal static void TryWakeOnDamage(GameObject enemy) {
        // Retain the hook contract, but do not turn a hit into an early capture recovery.
        // Damage dispatch itself continues natively while the scheduled AI wake owns timing.
    }

	private static void WakeVisibleDisabledEnemies()
	{
		int woken = 0;
		woken += WakeDisabledAiType<EnemyAI>();
		woken += WakeDisabledAiType<ChargingEnemyAI>();
		woken += WakeDisabledAiType<SpinningEnemyAI>();
		woken += WakeDisabledAiType<ProjectileEnemyAI>();
		woken += WakeDisabledAiType<DragonEnemyAI>();
		woken += WakeDisabledAiType<ProximityDragonEnemyAI>();
		if (woken > 0)
		{
			Plugin.DBG("ENEMY-WAKE", "reactivated " + woken + " visible disabled enemy object(s) (player attack)");
		}
	}

	private static int WakeDisabledAiType<T>() where T : MonoBehaviour
	{
		T[] ts = Object.FindObjectsByType<T>((FindObjectsInactive)1, (FindObjectsSortMode)0);
		int woken = 0;
		foreach (T t in ts)
		{
			if (!(t == null) && !((Behaviour)(object)t).enabled)
			{
				GameObject gameObject = ((Component)(object)t).gameObject;
				if (gameObject.activeInHierarchy && !IsScheduled(gameObject) && !ShouldSkipWake(gameObject))
				{
					Reactivate(gameObject);
					woken++;
				}
			}
		}
		return woken;
	}

	private static bool ShouldSkipWake(GameObject root)
	{
        if(IsStagedChaser(root))return true;
		if (root.CompareTag("Player") || GrappleEnemies.IsClingingNow((Object)(object)root))
		{
			return true;
		}
		DragonEnemyAI dragonEnemyAI = root.GetComponent<DragonEnemyAI>();
		if (dragonEnemyAI != null && dragonEnemyAI.frozenByArena)
		{
			return true;
		}
		ProximityDragonEnemyAI proximityDragonEnemyAI = root.GetComponent<ProximityDragonEnemyAI>();
		if (proximityDragonEnemyAI != null && proximityDragonEnemyAI.frozenByArena)
		{
			return true;
		}
		return IsDeadEnemy(root);
	}

	private static bool IsDeadEnemy(GameObject root)
	{
		foreach (MonoBehaviour monoBehaviour in EnemyAiTypes.On(root))
		{
			if (!(monoBehaviour == null))
			{
				Traverse traverse = Traverse.Create((object)monoBehaviour);
				if (traverse.Field("isDead").FieldExists() && traverse.Field("isDead").GetValue<bool>())
				{
					return true;
				}
				if (traverse.Field("currentHealth").FieldExists() && traverse.Field("currentHealth").GetValue<int>() <= 0)
				{
					return true;
				}
			}
		}
		return false;
	}

	internal static void ReactivateAllPending(string reason = null)
	{
		if (Pending.Count == 0)
		{
			return;
		}
		ScheduledEntry[] scheduledEntries = new ScheduledEntry[Pending.Count];
		Pending.Values.CopyTo(scheduledEntries, 0);
		Pending.Clear();
		int woken = 0;
		foreach (ScheduledEntry scheduledEntry in scheduledEntries)
		{
			if (!(scheduledEntry.Enemy == null))
			{
				Reactivate(scheduledEntry.Enemy, scheduledEntry.ApplyGrabEndCooldown);
				woken++;
			}
		}
		if (woken > 0)
		{
			string because = (string.IsNullOrEmpty(reason) ? "" : (" (" + reason + ")"));
			Plugin.DBG("ENEMY-WAKE", "reactivated " + woken + " enemy object(s)" + because);
		}
	}

	private static float GetDelaySeconds()
	{
		if (!Plugin.GameplayTweaksEnabled)
		{
			return 0f;
		}
		return Mathf.Max(0f, (OverkeksMode.UsesRecipe ? OverkeksConfig.CfgEnemyReactivationDelaySeconds : Plugin.CfgEnemyReactivationDelaySeconds).Value);
	}

	private static IEnumerator ReactivateAfterDelay(GameObject enemy, int instanceId, bool applyGrabEndCooldown, float delay, int token)
	{
		yield return (object)new WaitForSeconds(delay);
		if (Pending.TryGetValue(instanceId, out var entry) && entry.Token == token)
		{
			Pending.Remove(instanceId);
			if (enemy != null && !(OverkeksMode.Active && IsDeadEnemy(enemy)))
			{
                if(IsStagedChaser(enemy)) {
                    Plugin.DBG("ENEMY-WAKE","skipped delayed recovery of staged "+enemy.name);
                    yield break;
                }
				Reactivate(enemy, applyGrabEndCooldown);
				Plugin.DBG("ENEMY-WAKE", "reactivated " + NameRemap.StripCloneSuffix(enemy.name));
			}
		}
	}

	internal static void RevealOnly(GameObject enemy)
	{
		if (!(enemy == null))
		{
			enemy.SetActive(true);
			EnableRenderersAndColliders(enemy);
			DisableAiComponents(enemy);
			// Do this at reveal, not just at the wake: the object is active again from here,
			// so an orphaned spinMovementActive would have the spin loop audio playing for
			// the whole wake delay with nothing spinning - Update is not running to stop it.
			ClearOrphanedActionState(enemy);
		}
	}

	private static void ClearOrphanedActionState(GameObject enemy)
	{
		foreach (MonoBehaviour ai in EnemyAiTypes.On(enemy))
		{
			ClearOrphanedActionStateFor(ai);
		}
	}

	private static void ClearOrphanedActionStateFor(MonoBehaviour ai)
	{
		if (!(ai == null))
		{
			ClearOrphanedActionState(Traverse.Create((object)ai));
		}
	}

	private static void HideForRespawn(GameObject enemy)
	{
		if (!(enemy == null))
		{
			DisableAiComponents(enemy);
			enemy.SetActive(false);
		}
	}

	internal static void Reactivate(GameObject enemy, bool applyGrabEndCooldown = false)
    {
        if(IsStagedChaser(enemy)) {
            CancelScheduledFor(enemy);Plugin.DBG("ENEMY-WAKE","blocked recovery of staged chaser "+enemy.name);return;
        }
		if (!(enemy == null))
		{
			enemy.SetActive(true);
			EnableRenderersAndColliders(enemy);
			RestoreRigidbodies(enemy);
			foreach (MonoBehaviour ai in EnemyAiTypes.On(enemy))
			{
				ReactivateComponent(ai, applyGrabEndCooldown);
			}
			MimicEnemy mimicEnemy = enemy.GetComponent<MimicEnemy>();
			if (mimicEnemy != null)
			{
				mimicEnemy.enabled = true;
				RearmMimic(mimicEnemy, enemy);
			}
			EnemyKeepAliveHelper.RestoreEnemyHealth(enemy);
		}
	}

	private static void ReactivateComponent(MonoBehaviour ai, bool applyGrabEndCooldown)
	{
		if (ai == null)
		{
			return;
		}
		// Once the player is dead the run is over and they are waiting on the
		// "press any key" prompt. Waking AI here lets an enemy grab them again and
		// blocks the quit-to-menu. Renderers/colliders are still restored by the
		// caller, so nothing turns invisible - only the AI stays off.
		if (Plugin.PlayerDead)
		{
			ai.enabled = false;
			return;
		}
		ai.enabled = true;
		EnemyKeepAliveHelper.ClearDeadState(ai);
		ChargingEnemyAI chargingEnemyAI = (ChargingEnemyAI)(object)((ai is ChargingEnemyAI) ? ai : null);
		if (chargingEnemyAI != null)
		{
			StabilizeChargingEnemy(chargingEnemyAI);
		}
		else
		{
			EnemyAI enemyAI = (EnemyAI)(object)((ai is EnemyAI) ? ai : null);
			if (enemyAI != null)
			{
				Traverse traverse = Traverse.Create((object)enemyAI);
				traverse.Field("currentState").SetValue((object)(EnemyAI.AIState)1);
				TryStartPathfinding(traverse);
			}
			else if (ai is SpinningEnemyAI)
			{
				Traverse spinning = Traverse.Create((object)ai);
				spinning.Field("currentState").SetValue((object)(SpinningEnemyAI.AIState)0);
			}
			else
			{
				ProjectileEnemyAI projectileEnemyAI = (ProjectileEnemyAI)(object)((ai is ProjectileEnemyAI) ? ai : null);
				if (projectileEnemyAI != null)
				{
					Traverse projectile = Traverse.Create((object)projectileEnemyAI);
					projectile.Field("currentState").SetValue((object)(ProjectileEnemyAI.AIState)1);
				}
				else if (ai is DragonEnemyAI)
				{
					Traverse dragon = Traverse.Create((object)ai);
					dragon.Field("currentState").SetValue((object)(DragonEnemyAI.AIState)1);
				}
				else if (ai is ProximityDragonEnemyAI)
				{
					Traverse proximityDragon = Traverse.Create((object)ai);
					proximityDragon.Field("currentState").SetValue((object)(ProximityDragonEnemyAI.AIState)1);
				}
			}
		}
		// Do this last, and before the grab cooldown so that still wins.
		ResumeAiBehaviour(ai);
        if(OverkeksMode.Active)NativeFormPresentation.Restore(ai);
		if (applyGrabEndCooldown)
		{
			ApplyGrabEndCooldown(ai);
		}
	}

	// The game pauses every AI while a scene is on screen: Update sees
	// IsPlayerGrabbedByGrabScreen() || CinematicCameraSwapTrigger.IsAnyInCinematicView,
	// calls PauseAIBehavior() (which calls StopPathfinding, i.e. followerEntity
	// .simulateMovement = false) and sets wasPlayerGrabbedLastFrame. It resumes from its own
	// Update, but *only* while that flag is set - and we disable the AI component for the
	// duration of the scene, so that Update frequently never runs and the resume never
	// happens.
	//
	// Writing currentState = Chasing directly does not stand in for it: TransitionToChasing
	// is what calls StartPathfinding, so the enemy ends up certain it is chasing while
	// simulateMovement is still false - standing still forever. Only EnemyAI and
	// ChargingEnemyAI happened to also get StartPathfinding here; ProjectileEnemyAI and both
	// dragons never did, and SpinningEnemyAI (no pathfinding at all) was simply parked in
	// Idle instead of re-evaluating what it should be doing.
	//
	// So drive the game's own ResumeAIBehavior, which re-picks the state properly for each
	// type and routes through the transitions that restart movement.
	private static void ResumeAiBehaviour(MonoBehaviour ai)
	{
		if (ai == null)
		{
			return;
		}
		Traverse traverse = Traverse.Create((object)ai);
		if (traverse.Field("wasPlayerGrabbedLastFrame").FieldExists())
		{
			traverse.Field("wasPlayerGrabbedLastFrame").SetValue((object)false);
		}
		ClearStaleCoroutineHandles(traverse);
		ClearOrphanedActionState(traverse);
		if (traverse.Method("ResumeAIBehavior", Array.Empty<object>()).MethodExists())
		{
			traverse.Method("ResumeAIBehavior", Array.Empty<object>()).GetValue();
		}
		// Belt-and-braces: idempotent, and it re-teleports the follower to where the enemy
		// actually is, which matters after a scene has moved it.
		TryStartPathfinding(traverse);
	}

	// The reactivation delay exists to stop an enemy resuming its AI the instant a scene ends and
	// re-grabbing the player before they can move. A mimic has no AI to resume - it is a chest
	// that reads the interact key in its own Update - so none of that applies to it, and the
	// delay only means a chest the player walked back to sits inert for EnemyReactivationDelay-
	// Seconds. Worse, it was never the delay that decided when a mimic came back: the log shows
	// the re-arm landing 0.6 s after a scene end because the player happened to swing a weapon
	// (`reactivated 1 enemy object(s) (player attack)`), so the actual wait was "until you attack
	// something", which nobody designed.
	//
	// Interacting with a mimic is entirely voluntary, so there is nothing to gate. Re-arm it on
	// the spot.
	//
	// This does NOT remove the need for MimicGrabGate - it widens the window that gate covers.
	// The re-arm and the player's post-grab immunity are independent clocks: the chest is now
	// armed at T+0 while GrabScreen.GrabImmunity holds CanBeGrabbed false until T+1, so the
	// stretch in which a press would burn hasTriggeredGrab on a refused grab goes from ~0.4 s to
	// the full ~1 s. The prefix is what makes that stretch harmless.
	private static bool ShouldReactivateInstantly(GameObject enemy)
	{
		if (!Plugin.GameplayTweaksEnabled || !(OverkeksMode.UsesRecipe ? OverkeksConfig.CfgMimicReactivateInstantly : Plugin.CfgMimicReactivateInstantly).Value)
		{
			return false;
		}
		return enemy.GetComponent<MimicEnemy>() != null;
	}

	// Several paths converge on a scene end - the grab-end restore, NearbyEnemyHider's reveal
	// and the attack-triggered wake - so ScheduleReactivate is called three times for the same
	// mimic in the same frame. The Pending dictionary used to absorb that (second and later
	// calls updated the entry instead of logging), and short-circuiting above it lost the
	// dedup: the 14:46 log carries 41 "instant re-arm" lines for 12 actual re-arms.
	//
	// Reactivate itself is idempotent, so the repeats are harmless - but a log line claiming a
	// re-arm that did not happen is worse than no line, because it is the line this change gets
	// verified by. Report only when something was genuinely latched.
	private static void ReactivateMimicNow(GameObject enemy, bool applyGrabEndCooldown)
	{
		bool wasStale = !enemy.activeInHierarchy;
		MimicEnemy mimic = enemy.GetComponent<MimicEnemy>();
		if (mimic != null)
		{
			if (!mimic.enabled)
			{
				wasStale = true;
			}
			Traverse traverse = Traverse.Create((object)mimic);
			if (traverse.Field("hasTriggeredGrab").FieldExists() && traverse.Field("hasTriggeredGrab").GetValue<bool>())
			{
				wasStale = true;
			}
		}
		Reactivate(enemy, applyGrabEndCooldown);
		if (wasStale)
		{
			Plugin.DBG("ENEMY-WAKE", "mimic " + NameRemap.StripCloneSuffix(enemy.name) + " -> instant re-arm (no reactivation delay)");
		}
	}

	// A mimic is one-shot by construction: TriggerMimicGrab sets hasTriggeredGrab and ends in
	// Object.Destroy, so vanilla never needs to reset the flag. KeepEnemiesAfterGrab blocks
	// that Destroy, which leaves the chest standing with the flag already latched - visible,
	// re-enabled, and permanently uninteractable, since Update gates on !hasTriggeredGrab.
	// Clear it so a mimic that survived its own scene can be used again.
	private static void RearmMimic(MimicEnemy mimic, GameObject enemy)
	{
		if (!Plugin.GameplayTweaksEnabled || !(OverkeksMode.UsesRecipe ? OverkeksConfig.CfgKeepEnemiesAfterGrab : Plugin.CfgKeepEnemiesAfterGrab).Value)
		{
			return;
		}
		Traverse traverse = Traverse.Create((object)mimic);
		if (traverse.Field("hasTriggeredGrab").FieldExists() && traverse.Field("hasTriggeredGrab").GetValue<bool>())
		{
			traverse.Field("hasTriggeredGrab").SetValue((object)false);
			Plugin.DBG("ENEMY-WAKE", "re-armed mimic " + NameRemap.StripCloneSuffix(enemy.name));
		}
	}

	// Deactivating a GameObject kills its coroutines but leaves the handles non-null, and
	// NearbyEnemyHider deactivates enemies for the duration of a scene. Guards like
	// SpinningEnemyAI.StartSpin's `if (spinCoroutine == null)` then refuse to ever start a
	// new one: spinMovementActive stays false, FixedUpdate zeroes the velocity every frame,
	// and the enemy never moves again - while still attacking, because HandleGrabHit runs
	// straight from Update rather than a coroutine.
	//
	// The game's own Stop* helpers both stop the coroutine and null the handle, so driving
	// those hands the AI a clean slate. Stopping an already-dead coroutine is harmless, and
	// going through them rather than nulling the fields directly means a genuinely live one
	// is torn down properly instead of leaked.
	private static void ClearStaleCoroutineHandles(Traverse ai)
	{
		InvokeIfExists(ai, "StopSpin");
		InvokeIfExists(ai, "StopCharge");
		InvokeIfExists(ai, "StopAttack");
		InvokeIfExists(ai, "StopShooting");
		// No IsGrabbing guard here, unlike ApplyGrabEndCooldown. Skipping the call would leave
		// the non-null handle that StartGrab refuses to overwrite, which is the bug this method
		// was written for (§78).
		//
		// §130 instrumented this line for a whole run rather than reasoning about it, because
		// the justification that used to stand here - "every handle it clears is stale by
		// construction, a live coroutine cannot have survived the deactivation" - is false.
		// Reactivate calls SetActive(true) unconditionally, so the ENEMY-AUTOFIX path reaches
		// enemies that were never hidden: all 49 logged calls found activeInHierarchy=True, and
		// three of them a genuinely live grabCoroutine. What the run also showed is that the
		// state was **never** Grabbing on any of them, so this has never produced §78's strand
		// and a guard here would have fired zero times. The three live handles belonged to the
		// enemy whose own scene had just ended, tearing down a coroutine already past its grab;
		// nothing was observed from it. Left as it is on measurement, not on the old argument.
		InvokeIfExists(ai, "StopGrab");
	}

	// Same root cause as the stale handles, different victims. These flags are set by
	// animation events or by the first half of a coroutine and cleared by a *later* event or
	// the coroutine's second half - neither of which arrives if the GameObject is
	// deactivated mid-action, which is exactly what NearbyEnemyHider does for a scene.
	//
	// isDashing is the damaging one: SpinningEnemyAI.FixedUpdate only applies spin velocity
	// while `spinMovementActive && !isDashing`, and PerformDash guards on !isDashing, so one
	// orphaned dash leaves Plantasha spinning on the spot forever - animation and audio
	// playing, no movement. There is no dashCoroutine field and no StopDash to call, so the
	// flag has to be cleared directly.
	//
	// spinMovementActive drives UpdateSpinLoopSound, so an orphaned one also leaves the spin
	// loop audio running with nothing spinning.
	private static void ClearOrphanedActionState(Traverse ai)
	{
		ClearBoolIfExists(ai, "isDashing");
		ClearBoolIfExists(ai, "spinMovementActive");
		ClearBoolIfExists(ai, "grabInDamagingState");
		ClearBoolIfExists(ai, "hasDealtDamageThisAttack");
		ClearBoolIfExists(ai, "hasDealtChargeDamage");
		// Loops that only ever stop from Update, which does not run while we have the AI
		// component disabled.
		InvokeIfExists(ai, "StopSpinLoopSound");
		InvokeIfExists(ai, "StopIdleMovingSound");
	}

	private static void ClearBoolIfExists(Traverse ai, string field)
	{
		if (ai.Field(field).FieldExists() && ai.Field(field).GetValue<bool>())
		{
			ai.Field(field).SetValue((object)false);
		}
	}

	// The `Traverse.Method(...)` probe itself is what logs `AccessTools.Method: Could not find
	// method` at warning level, so asking "does this AI have StopSpin" costs a warning on every AI
	// that does not (§138). Reflection asks silently, and only then is Traverse used to call it -
	// which keeps the invoke on the same path it has always taken.
	private static void InvokeIfExists(Traverse ai, string method)
	{
		object target = ai.GetValue();
		if (target == null)
		{
			return;
		}
		MethodInfo found = target.GetType().GetMethod(method,
			BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy,
			null, Type.EmptyTypes, null);
		if (found != null)
		{
			ai.Method(method, Array.Empty<object>()).GetValue();
		}
	}

	// A spinner that believes it is spinning but has not been moving for a while: the stale
	// handle case above, which IsStuckChasing cannot see because SpinningEnemyAI has no
	// follower to check.
	internal static bool IsStuckSpinning(GameObject root)
	{
		if (root == null)
		{
			return false;
		}
		SpinningEnemyAI spinningEnemyAI = root.GetComponent<SpinningEnemyAI>();
		if (spinningEnemyAI == null || !spinningEnemyAI.enabled)
		{
			return false;
		}
		Traverse traverse = Traverse.Create((object)spinningEnemyAI);
		if (!traverse.Field("currentState").FieldExists() || Convert.ToInt32(traverse.Field("currentState").GetValue()) != 1)
		{
			return false;
		}
		if (!traverse.Field("spinMovementActive").FieldExists() || traverse.Field("spinMovementActive").GetValue<bool>())
		{
			return false;
		}
		if (!traverse.Field("spinStartedAtTime").FieldExists())
		{
			return false;
		}
		return Time.time - traverse.Field("spinStartedAtTime").GetValue<float>() > 5f;
	}

	// True when an AI insists it is chasing but its follower is not simulating movement -
	// the exact signature of the bug above, and something no other check catches because
	// the component is enabled and the object is active.
	internal static bool IsStuckChasing(GameObject root)
	{
		if (root == null)
		{
			return false;
		}
		foreach (MonoBehaviour ai in EnemyAiTypes.On(root))
		{
			if (IsStuckChasingAi(ai))
			{
				return true;
			}
		}
		return false;
	}

	private static bool IsStuckChasingAi(MonoBehaviour ai)
	{
		if (ai == null || !ai.enabled)
		{
			return false;
		}
		Traverse traverse = Traverse.Create((object)ai);
		if (!traverse.Field("currentState").FieldExists() || Convert.ToInt32(traverse.Field("currentState").GetValue()) != 1)
		{
			return false;
		}
		Traverse followerField = traverse.Field("followerEntity");
		if (!followerField.FieldExists())
		{
			return false;
		}
		object followerObject = followerField.GetValue();
		if (followerObject == null || (followerObject as Object) == null)
		{
			return false;
		}
		Traverse followerTraverse = Traverse.Create(followerObject);
		if (followerTraverse.Property("simulateMovement").PropertyExists())
		{
			return !followerTraverse.Property("simulateMovement").GetValue<bool>();
		}
		if (followerTraverse.Field("simulateMovement").FieldExists())
		{
			return !followerTraverse.Field("simulateMovement").GetValue<bool>();
		}
		return false;
	}

	// The cooldown alone, for an enemy the game restored itself (0.3.1's hideInsteadOfDestroyOnGrab
	// path). Nothing else here applies: vanilla's EndGrabHidden has already reset the transient
	// state and put the AI back in Idle, so the only thing missing is the mod's configured
	// re-grab delay on top of vanilla's `lastGrabTime = now`. Deliberately without the StopGrab
	// call the full version makes — see the comment there.
	internal static void ApplyGrabEndCooldownToEnemy(GameObject enemy)
	{
		if (enemy == null)
		{
			return;
		}
		foreach (MonoBehaviour ai in EnemyAiTypes.On(enemy))
		{
			ApplyGrabEndCooldown(ai, stopGrab: false);
		}
	}

	internal static void ApplyGrabEndCooldown(MonoBehaviour enemyAi, bool stopGrab = true)
	{
		if (!(enemyAi == null))
		{
			Traverse traverse = Traverse.Create((object)enemyAi);
			float grabCooldown = traverse.Field("grabCooldown").GetValue<float>();
			float floor = Mathf.Max(grabCooldown, (OverkeksMode.UsesRecipe ? OverkeksConfig.CfgEndGrabEnemyCooldownSeconds : Plugin.CfgEndGrabEnemyCooldownSeconds).Value);
			if (traverse.Field("hasGrabbedThisAttempt").FieldExists())
			{
				traverse.Field("hasGrabbedThisAttempt").SetValue((object)true);
			}
			if (traverse.Field("lastGrabTime").FieldExists())
			{
				traverse.Field("lastGrabTime").SetValue((object)(Time.time + floor - grabCooldown));
				// "Was the cooldown applied" had no answer in the log, and the 2026-08-24 run
				// needed one: the Blinded Beast re-grabbed 1.0 s after its own grab ended, which
				// is either this never running or the AI reaching the grab by a route that does
				// not consult `lastGrabTime`. The run after it answered that - the line was
				// there, saying +10.0 s, and the beast came back at +2.28 s both times. It
				// reaches the grab through a route that never reads `lastGrabTime`, so this
				// write is advisory on that path however large it is. That is why the floor is
				// 0 by default now (§122): a number the AI can ignore is not protection, and
				// what actually needed protecting was the game over on screen, which the
				// presentation latch holds.
				Plugin.DBG("GRAB-COOLDOWN", NameRemap.StripCloneSuffix(((Component)enemyAi).gameObject.name)
					+ (floor > grabCooldown
						? ": next grab no earlier than +" + floor.ToString("F1") + "s (its own grabCooldown is "
							+ grabCooldown.ToString("F1") + "s)"
						: ": vanilla's own grabCooldown of " + grabCooldown.ToString("F1") + "s stands - no floor forced"));
			}
			if (traverse.Field("wasPlayerGrabbedLastFrame").FieldExists())
			{
				traverse.Field("wasPlayerGrabbedLastFrame").SetValue((object)false);
			}
			if (traverse.Field("grabInDamagingState").FieldExists())
			{
				traverse.Field("grabInDamagingState").SetValue((object)false);
			}
			// Only on the mod's own reactivation path. Vanilla's restore does not need it.
			//
			// And never while the AI is still in Grabbing: StopGrab stops the coroutine and
			// nulls the handle without transitioning, and GrabSequence is the only thing that
			// leaves the state - SpinningEnemyAI and the dragons have no case for Grabbing in
			// UpdateStateMachine at all. Killing it mid-flight is #14's strand, recovered
			// 0.5 s later by AiStateGuard, which is exactly the visible hitch (§78).
			if (stopGrab && traverse.Method("StopGrab", Array.Empty<object>()).MethodExists())
			{
				if (IsGrabbing(traverse))
				{
					Plugin.DBG("AI-GUARD", "kept " + NameRemap.StripCloneSuffix(enemyAi.gameObject.name)
						+ "'s live grab: StopGrab skipped while state=Grabbing");
				}
				else
				{
					traverse.Method("StopGrab", Array.Empty<object>()).GetValue();
				}
			}
		}
	}

	// True while this AI is in the state its own grab coroutine drives.
	//
	// Resolved by enum NAME, not by number. AiStateGuard's table is keyed on the raw int
	// because that is what the boxed field hands back, and EnemyAiAudit exists to catch that
	// table drifting when the game renumbers a state; there is no reason to add a second thing
	// to audit when the name is right here. Grabbing is 2 on SpinningEnemyAI and 3 on EnemyAI
	// and both dragons, so a numeric constant would have been wrong for one of them anyway.
	internal static bool IsGrabbing(Traverse ai)
	{
		if (!ai.Field("currentState").FieldExists())
		{
			return false;
		}
		object state = ai.Field("currentState").GetValue();
		if (state == null)
		{
			return false;
		}
		Type type = state.GetType();
		if (!type.IsEnum || Array.IndexOf(Enum.GetNames(type), "Grabbing") < 0)
		{
			return false;
		}
		return Convert.ToInt32(state) == Convert.ToInt32(Enum.Parse(type, "Grabbing"));
	}

	private static void StabilizeChargingEnemy(ChargingEnemyAI chargingEnemy)
	{
		Traverse traverse = Traverse.Create((object)chargingEnemy);
		traverse.Field("isCharging").SetValue((object)false);
		traverse.Field("hasDealtChargeDamage").SetValue((object)false);
		traverse.Field("currentState").SetValue((object)(ChargingEnemyAI.AIState)1);
		traverse.Field("lastChargeTime").SetValue((object)Time.time);
		if (traverse.Method("StopCharge", Array.Empty<object>()).MethodExists())
		{
			traverse.Method("StopCharge", Array.Empty<object>()).GetValue();
		}
		if (traverse.Method("StartPathfinding", Array.Empty<object>()).MethodExists())
		{
			traverse.Method("StartPathfinding", Array.Empty<object>()).GetValue();
		}
	}

	private static void TryStartPathfinding(Traverse traverse)
	{
		if (traverse.Method("StartPathfinding", Array.Empty<object>()).MethodExists())
		{
			traverse.Method("StartPathfinding", Array.Empty<object>()).GetValue();
		}
	}

	private static void DisableAiComponents(GameObject enemy)
	{
		List<MonoBehaviour> monoBehaviours = EnemyAiTypes.On(enemy);
		monoBehaviours.Add((MonoBehaviour)(object)enemy.GetComponent<MimicEnemy>());
		foreach (MonoBehaviour monoBehaviour in monoBehaviours)
		{
			if (monoBehaviour != null)
			{
				monoBehaviour.enabled = false;
			}
		}
	}

	private static void EnableRenderersAndColliders(GameObject enemy)
	{
		Collider[] componentsInChildren = enemy.GetComponentsInChildren<Collider>(true);
		for (int i = 0; i < componentsInChildren.Length; i++)
		{
			componentsInChildren[i].enabled = true;
		}
		Renderer[] componentsInChildren2 = enemy.GetComponentsInChildren<Renderer>(true);
		for (int j = 0; j < componentsInChildren2.Length; j++)
		{
			componentsInChildren2[j].enabled = true;
		}
	}

	private static void RestoreRigidbodies(GameObject enemy)
	{
		Rigidbody[] componentsInChildren = enemy.GetComponentsInChildren<Rigidbody>(true);
		foreach (Rigidbody rigidbody in componentsInChildren)
		{
			rigidbody.isKinematic = false;
			rigidbody.linearVelocity = Vector3.zero;
			rigidbody.angularVelocity = Vector3.zero;
		}
	}
}
