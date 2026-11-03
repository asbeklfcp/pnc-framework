using System;
using System.Collections.Generic;
using BepInEx.Unity.Mono.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace PncEdi;

[HarmonyPatch]
internal static class HeatLockSystem
{
	internal struct ReleaseMarkerSource
	{
		public string Kind;
		public string Label;
		public Transform Transform;
	}

	private static readonly HashSet<string> UsedReleaseSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
	private static readonly HashSet<string> SeenSceneSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
	private static readonly Dictionary<string, TextMesh> ReleaseMarkers = new Dictionary<string, TextMesh>(StringComparer.OrdinalIgnoreCase);
	private static readonly List<ReleaseMarkerSource> MarkerSources = new List<ReleaseMarkerSource>();
	private static PlayerStats _player;
	private static int _baseHealth;
	private static float _baseHeat;
	private static int _locks;
	private static bool _adjustingHeat;
	private static bool _adjustingHealth;
	private static float _healAccumulator;
	private static Canvas _canvas;
	private static Text _text;
	private static Image _gaugeBack;
	private static Image _gaugeFill;
	private static Sprite _gaugeSprite;
	private static float _nextMarkerScanTime;
	private static string _lastReleaseStatus;
	private static float _releaseStatusUntil;
	private static string _activeReleaseStatus;
	private static bool _pendingKeyholeRelease;
	private static string _pendingKeyholeKey;
	private static string _pendingKeyholeLabel;
	private static float _pendingKeyholeArmedAt;
	private static string _lastInteractLockKey;
	private static bool _pendingSceneEntryGameOver;
	private static bool _forcingPendingSceneEntryGameOver;
	// One profile decides this now. Under Custom it is still exactly `Enabled && EnableHeatLocks`;
	// PressureAndRelease forces it on and the other two force it off, so no combination of the
	// individual switches can leave locks running under a profile that does not want them.
	internal static bool Enabled => GameplayProfiles.ReleaseModeEnabled;
	internal static int CurrentLocks => Enabled ? Mathf.Max(0, _locks) : 0;
	internal static float CurrentLockProgress => Enabled ? Mathf.Clamp01((float)CurrentLocks / (float)GetTotalLocks()) : 0f;
	internal static bool ForcingPendingSceneEntryGameOver => _forcingPendingSceneEntryGameOver;

	// True between "the player died entering a scene and we revived them to 1 HP so it could
	// play" and the game over that follows the scene. GrabHooks needs it: the death grab arrives
	// with its screen animator inactive, and this is what tells it that the grab is nevertheless
	// a real scene rather than a refused one. See CHANGELOG §50.
	internal static bool PendingSceneEntryGameOver => _pendingSceneEntryGameOver;

	// Every horny lock held. The heat floor is then maxHeat-1 (GetFullLockHeat), so the bar
	// refills in a single unit and the grab screen re-triggers its cum almost instantly.
	internal static bool AtFullLock => Enabled && _locks > 0 && _locks >= GetTotalLocks();

	// The lock floor, or 0 when no locks are held.
	internal static float CurrentHeatFloor
	{
		get
		{
			PlayerStats playerStats = _player ?? Object.FindAnyObjectByType<PlayerStats>();
			if (playerStats != null && TryGetLockedHeatRange(playerStats, out var minHeat, out var _))
			{
				return minHeat;
			}
			return 0f;
		}
	}

	// A grab screen cycles Imp_Grab_Loop -> (heat hits max) -> Imp_Grab_Cum -> (this animation
	// event clears "MaxHeat") -> Loop, and vanilla refills the whole bar in between. Under heat
	// locks the floor is maxHeat-1 at full lock, so the bar refills in a single unit: the screen
	// drops to the loop for a frame or two and snaps straight back to the cum. That reads as a
	// stutter on screen and makes the device jump between two scripts several times a second.
	//
	// At full lock, hold the cum instead. The event itself is allowed to run - it also clears
	// maxHeatAnimationPlaying / heatIncreaseBlocked / isCoolingDown / heatFullyCooled, and
	// skipping those would wedge the screen - and then the state is re-asserted and replayed
	// from the top, so the cum repeats cleanly with no visit to the loop.
	// The state to replay, captured BEFORE the original method runs. See the prefix.
	private static int _cumStateToHold;

	// Set by the prefix from vanilla's own `heatFullyCooled`: true only when this call to
	// OnMaxHeatAnimationComplete is the one that actually ends the cum.
	private static bool _cumWasReleasing;

	// `Time.time` the post-cum grace runs out at, or 0 when none is running.
	private static float _postCumGraceUntil;

	// The original sets MaxHeat=false, which immediately starts the cum -> loop transition. A
	// postfix reading GetCurrentAnimatorStateInfo therefore sees the LOOP, not the cum, and the
	// old code replayed that - leaving the animator looping with MaxHeat forced back to true and
	// no cum clip playing. No cum clip means no animation event, so maxHeatAnimationPlaying and
	// heatIncreaseBlocked never cleared again and heat could not rise for the rest of the run
	// (bug #15: "white screen", scenes wedging). The 2026-08-17 logs name it exactly - every
	// release logged `animator in 'Imp_Grab_Loop'` was followed by no event for 17-77 s, while
	// every `'Imp_Grab_Cum'` / `'GargoyleCumAlt'` was followed by one ~3 s later.
	//
	// Capture the state here, where the animator is genuinely still in the cum, and let the
	// postfix replay that.
	[HarmonyPatch(typeof(GrabScreen), "OnMaxHeatAnimationComplete")]
	[HarmonyPrefix]
	public static void OnMaxHeatAnimationComplete_Prefix(GrabScreen __instance)
	{
		_cumStateToHold = 0;
		_cumWasReleasing = false;
		try
		{
			if (__instance == null)
			{
				return;
			}
			// Vanilla's whole method is `if (heatFullyCooled) { ... }`, so a call with it false
			// changes nothing at all. Reading it here is the only way the postfix can tell "the
			// cum just ended" from "the event fired again into a no-op" - and arming a grace on a
			// no-op would block heat mid-loop, which is the opposite of what it is for.
			_cumWasReleasing = Traverse.Create((object)__instance).Field("heatFullyCooled").GetValue<bool>();
			if (!AtFullLock || !((OverkeksMode.UsesRecipe ? OverkeksConfig.CfgFullLockHoldsCum : Plugin.CfgFullLockHoldsCum)?.Value ?? true))
			{
				return;
			}
			Animator anim = Traverse.Create((object)__instance).Field("grabScreenAnimator").GetValue<Animator>();
			if (anim == null || !anim.isActiveAndEnabled)
			{
				return;
			}
			_cumStateToHold = anim.GetCurrentAnimatorStateInfo(0).fullPathHash;
		}
		catch (Exception ex)
		{
			Plugin.DBG("HEAT-LOCK", "hold-cum prefix failed: " + ex.Message);
		}
	}

	[HarmonyPatch(typeof(GrabScreen), "OnMaxHeatAnimationComplete")]
	[HarmonyPostfix]
	public static void OnMaxHeatAnimationComplete_Postfix(GrabScreen __instance)
	{
		// Unconditional, because the interesting case turned out to be this method never
		// running at all. Only the full-lock branch below used to log, so "the cum never
		// completes" and "the cum completes but we do not hold it" looked identical in a log.
		Plugin.DBG("HEAT-LOCK", "OnMaxHeatAnimationComplete fired (fullLock=" + AtFullLock + ")");
		if (!HoldCum(__instance))
		{
			ArmPostCumGrace(__instance);
		}
	}

	// True when the cum was kept on screen instead of the screen dropping to its grabbed loop.
	// False is the case the post-cum grace exists for, whether that is the ordinary sub-full-lock
	// path or a full lock whose hold could not run.
	private static bool HoldCum(GrabScreen __instance)
	{
		try
		{
			if (!AtFullLock || !((OverkeksMode.UsesRecipe ? OverkeksConfig.CfgFullLockHoldsCum : Plugin.CfgFullLockHoldsCum)?.Value ?? true) || __instance == null)
			{
				return false;
			}
			Animator grabScreenAnimator = Traverse.Create((object)__instance).Field("grabScreenAnimator").GetValue<Animator>();
			if (grabScreenAnimator == null || !grabScreenAnimator.isActiveAndEnabled)
			{
				return false;
			}
			if (_cumStateToHold == 0)
			{
				Plugin.DBG("HEAT-LOCK", "full lock -> no cum state captured, letting the loop run");
				return false;
			}
			grabScreenAnimator.SetBool("MaxHeat", true);
			// ... and under whatever name this controller actually uses; see GrabScreenHeatParam.
			GrabScreenHeatParam.Set(grabScreenAnimator, value: true);
			grabScreenAnimator.Play(_cumStateToHold, 0, 0f);
			_cumStateToHold = 0;
			Plugin.DBG("HEAT-LOCK", "full lock -> holding the cum animation instead of dropping to the loop");
			return true;
		}
		catch (Exception ex)
		{
			Plugin.DBG("HEAT-LOCK", "hold-cum postfix failed: " + ex.Message);
			return false;
		}
	}

	// A short guaranteed stretch of the grabbed loop after a cum ends.
	//
	// Under a horny lock the heat floor is `maxHeat * locks / total`, so at 13 or 14 of 15 it
	// sits one or two units below max. Vanilla's cum ends at the floor (CumCooldownEndsAtLockFloor
	// above), the bar then refills those one or two units in a fraction of a second, and
	// TriggerMaxHeatAnimation fires again before the loop has played through once. On screen that
	// is a flicker; on the device it is two scripts alternating, because loop and cum are
	// different gallery rows. Full lock does not go through here at all - `FullLockHoldsCum` keeps
	// the cum on screen there, which is a better answer when the floor is exactly one unit down.
	//
	// The lever is vanilla's own `heatIncreaseBlocked`, the flag it sets for the duration of a cum
	// and clears in OnMaxHeatAnimationComplete. Holding it a moment longer is that flag meaning
	// what it already means; nothing else reads it (HandleHeatBuildup's build branch is the single
	// reader), and EndGrab clears all four heat flags unconditionally, so the worst a grace that
	// somehow outlived its deadline could do is stop heat building until the grab ends.
	//
	// Deliberately NOT done by lowering the heat floor for the same window: the floor is read by
	// EnsureHeatBounds every tick, so heat would cool below it and then be snapped back up when
	// the grace expired - a jump of two units straight into the next cum, which is the jerk this
	// is meant to remove.
	private static void ArmPostCumGrace(GrabScreen grabScreen)
	{
		try
		{
			float graceSeconds = (OverkeksMode.UsesRecipe ? OverkeksConfig.CfgPostCumGraceSeconds : Plugin.CfgPostCumGraceSeconds)?.Value ?? 0f;
			if (!Enabled || !_cumWasReleasing || graceSeconds <= 0f || grabScreen == null)
			{
				return;
			}
			// With no floor under heat, vanilla already cools all the way to 0 and the loop gets
			// its full run; there is nothing to pace.
			if (CurrentHeatFloor <= 0f)
			{
				return;
			}
			_postCumGraceUntil = Time.time + graceSeconds;
			Plugin.DBG("HEAT-LOCK", $"post-cum grace {graceSeconds:F1}s (floor {CurrentHeatFloor:F1}, locks {CurrentLocks}/{GetTotalLocks()})");
		}
		catch (Exception ex)
		{
			Plugin.DBG("HEAT-LOCK", "post-cum grace arming failed: " + ex.Message);
		}
	}

	// Re-asserts the block every tick while the grace runs, and clears it once - so a grace can
	// only ever end, never latch. Vanilla clears the flag itself at the end of the cum, which is
	// before the deadline, hence re-asserting rather than setting it once.
	private static void TickPostCumGrace()
	{
		if (_postCumGraceUntil <= 0f)
		{
			return;
		}
		try
		{
			GrabScreen grabScreen = GrabScreen.Instance;
			if (grabScreen == null || !grabScreen.IsGrabbed)
			{
				// The grab ended; EndGrab has already cleared the stillInGrace itself.
				_postCumGraceUntil = 0f;
				return;
			}
			bool stillInGrace = Time.time < _postCumGraceUntil;
			Traverse.Create((object)grabScreen).Field("heatIncreaseBlocked").SetValue((object)stillInGrace);
			if (!stillInGrace)
			{
				_postCumGraceUntil = 0f;
				Plugin.DBG("HEAT-LOCK", "post-cum grace over -> heat building again");
			}
		}
		catch (Exception ex)
		{
			_postCumGraceUntil = 0f;
			Plugin.DBG("HEAT-LOCK", "post-cum grace tick failed: " + ex.Message);
		}
	}

	// Vanilla ends a cum only once heat has cooled to *exactly* zero:
	//
	//     HandleHeatBuildup:            if (CurrentHeat > 0f) return;  heatFullyCooled = true;
	//     OnMaxHeatAnimationComplete:   if (!heatFullyCooled) return;  animator.SetBool("MaxHeat", false);
	//
	// A horny lock puts a floor under heat, so CurrentHeat never reaches 0, heatFullyCooled is
	// never set, the completion event no-ops forever and the cum animation repeats for as long
	// as the grab lasts. This is the same trap section 11 already hit with overheat, which also
	// tested for exactly-0 and was made to clear at the lock floor instead. Do the same here:
	// the floor is this mod's zero.
	[HarmonyPatch(typeof(GrabScreen), "HandleHeatBuildup")]
	[HarmonyPostfix]
	public static void HandleHeatBuildup_Postfix(GrabScreen __instance)
	{
		try
		{
			if (!Enabled || !((OverkeksMode.UsesRecipe ? OverkeksConfig.CfgCumCooldownEndsAtLockFloor : Plugin.CfgCumCooldownEndsAtLockFloor)?.Value ?? true) || __instance == null)
			{
				return;
			}
			float currentHeatFloor = CurrentHeatFloor;
			if (currentHeatFloor <= 0f)
			{
				return;
			}
			Traverse traverse = Traverse.Create((object)__instance);
			if (!traverse.Field("isCoolingDown").GetValue<bool>())
			{
				return;
			}
			PlayerStats playerStats = _player ?? Object.FindAnyObjectByType<PlayerStats>();
			if (playerStats == null || playerStats.CurrentHeat > currentHeatFloor + 0.05f)
			{
				return;
			}
			traverse.Field("heatFullyCooled").SetValue((object)true);
			traverse.Field("isCoolingDown").SetValue((object)false);
			// Name the state the animator is actually in when the cum is released. Logs from
			// 2026-08-17 show maxHeatAnimationPlaying and heatIncreaseBlocked latching true and
			// never clearing (twice: Wendigo 05:19:17, imp grapple death 05:20:10), because
			// OnMaxHeatAnimationComplete - the animation event that clears them - is never called
			// at all: our own postfix on it would have logged at full lock and never did. So
			// either the animator never enters a cum state, or it enters one whose clip carries
			// no such event. This line tells the two apart.
			Animator cumAnim = traverse.Field("grabScreenAnimator").GetValue<Animator>();
			string where = "<no animator>";
			if (cumAnim != null)
			{
				AnimatorClipInfo[] ci = cumAnim.GetCurrentAnimatorClipInfo(0);
				where = ((ci != null && ci.Length > 0 && ci[0].clip != null)
					? ci[0].clip.name
					: ("hash " + cumAnim.GetCurrentAnimatorStateInfo(0).shortNameHash));
			}
			Plugin.DBG("HEAT-LOCK", $"cum cooldown reached the lock floor ({currentHeatFloor:F1}) -> releasing the cum (animator in '{where}')");
		}
		catch (Exception ex)
		{
			Plugin.DBG("HEAT-LOCK", "cum cooldown postfix failed: " + ex.Message);
		}
	}

	// "shop" is enough to catch every stage's own Stage{n}Shop, the same substring convention
	// Plugin.IsMenuSceneName uses for "menu".
	private static bool IsShopSceneName(string sceneName)
	{
		return !string.IsNullOrEmpty(sceneName) && sceneName.IndexOf("shop", StringComparison.OrdinalIgnoreCase) >= 0;
	}

	// sceneName is null for a mid-run profile switch (GameplayProfiles.OnProfileChanged), which
	// clears the locks unconditionally - they belong to the outgoing ruleset regardless of what
	// scene is on screen.
	internal static void ResetForScene(string sceneName = null)
	{
		// One-time releases are one-time *per run*, not per launch. Nothing ever cleared
		// these, so a diorama consumed in one run stayed consumed until the game was closed -
		// and under the old fire-on-earshot behaviour simply walking past was enough to spend
		// one. After a few runs every diorama in the save reads as already used, which looks
		// exactly like the release system being broken.
		if (UsedReleaseSources.Count > 0)
		{
			Plugin.DBG("HEAT-LOCK", "cleared " + UsedReleaseSources.Count + " used release source(s) for the new scene");
		}
		UsedReleaseSources.Clear();
		SeenSceneSources.Clear();
		AmbientReleaseGaze.ResetForScene();
		_player = null;
		_baseHealth = 0;
		_baseHeat = 0f;
		// A clamp armed just before this scene change is always discarded here rather than
		// landed: a shop transition is exactly the case it exists to protect against (see
		// RecordBaseHeat), and a non-shop transition is about to zero _locks outright below, which
		// makes applying it first pointless.
		if (_pendingClampTotalLocks >= 0)
		{
			Plugin.DBG("HEAT-LOCK", $"discarded a pending capacity-shrink clamp ({_locks} -> {_pendingClampTotalLocks}) at scene change");
			_pendingClampTotalLocks = -1;
		}
		// The shop is a rest stop inside the current stage, not a new one: locks held on the way
		// in have to survive it, or the exemption that pays them out from the Gravy service scene
		// (CfgServiceSceneKeys) always finds an empty set - the scene change that puts you in the
		// shop happens first, seconds before the service's own watchtime completes. The next real
		// floor still clears them, because that transition is not a shop scene either way.
		if (!IsShopSceneName(sceneName))
		{
			_locks = 0;
		}
		_adjustingHeat = false;
		_adjustingHealth = false;
		_healAccumulator = 0f;
		_lastReleaseStatus = null;
		_releaseStatusUntil = 0f;
		_activeReleaseStatus = null;
		_pendingKeyholeRelease = false;
		_pendingKeyholeKey = null;
		_pendingKeyholeLabel = null;
		_pendingKeyholeArmedAt = 0f;
		_lastInteractLockKey = null;
		_pendingSceneEntryGameOver = false;
		_forcingPendingSceneEntryGameOver = false;
		_postCumGraceUntil = 0f;
		_cumWasReleasing = false;
		DestroyReleaseMarkers();
		UpdateDisplay();
	}

	internal static void Tick()
	{
		if (!Enabled)
		{
			DestroyDisplay();
			DestroyReleaseMarkers();
			return;
		}
		if (_player == null)
		{
			_player = Object.FindAnyObjectByType<PlayerStats>();
			RecordBaseHealth(_player);
		}
		ConfirmPendingClamp();
		TickPostCumGrace();
		TraceGrabHeat();
		TraceOverheatLatch();
		EnsureHeatBounds(_player);
		ClearOverheatAtFloor(_player);
		ApplyHeatScaledAutoHeal(_player);
		TickPendingKeyholeRelease();
		UpdateDisplay();
		UpdateReleaseMarkers();
	}

	private static int _lastLatchState = -1;

	// Vanilla disarms the player through one latch: `hasBeenOverheated`, set the moment heat goes
	// past MaxHeat, and cleared only at *exactly* zero heat. Every weapon that costs heat is gated
	// on `CanAttackNow()`, which is that latch (DualWieldingSystem.PerformAttack /
	// PerformSpecialAttack), so a staff or tome stops working while a sword swings on - and the
	// only feedback is the weapon's own OverheatedSound, if it has one.
	//
	// A lock floor makes zero unreachable, which is why ClearOverheatAtFloor exists. But
	// `ClearOverheatIfBelowMax` refuses while heat is above MaxHeat, and at full lock heat parks at
	// 99 and overshoots past 100 on every cast, so whether the clear actually fires is a question
	// about a race, not about the code - and HEAT-TRACE cannot answer it because it only runs
	// during a grab, which is exactly when the player is not casting.
	//
	// So: one line whenever the latch or the attack gate changes, never per frame - plus a second
	// line, below, for the only state that is actually a defect.
	//
	// The transition lines alone cannot decide it, and a session read them as if they could (§138):
	// they carry heat at the two edges of a disarmed window and nothing in between, so a window that
	// opens at 100.1/100.0 and closes fourteen seconds later at 97.5 says *nothing* about whether
	// heat stayed at the cap or climbed to 130 and came back. Riding the cap is what the game does;
	// the report this instrument exists for is a latch that outlives the heat that set it. That is
	// one predicate - `hasBeenOverheated` while heat is **below** MaxHeat - and vanilla can never
	// produce it, because crossing below the cap is exactly when its own clear runs.
	private static void TraceOverheatLatch()
	{
		if (_player == null)
		{
			_lastLatchState = -1;
			_nextStuckLatchReportAt = 0f;
			return;
		}
		bool latched = Traverse.Create((object)_player).Field("hasBeenOverheated").GetValue<bool>();
		bool canAttack = _player.CanAttackNow();
		float minHeat = 0f;
		if (TryGetLockedHeatRange(_player, out var m, out var _))
		{
			minHeat = m;
		}

		int state = (latched ? 1 : 0) | (canAttack ? 2 : 0);
		if (state != _lastLatchState)
		{
			_lastLatchState = state;
			Plugin.DBG("OVERHEAT", $"hasBeenOverheated={latched} canAttack={canAttack} "
				+ $"heat={_player.CurrentHeat:F1}/{_player.MaxHeat:F1} floor={minHeat:F1} locks={_locks}/{GetTotalLocks()}");
		}

		// The defect itself: latched *after heat has reached the lock floor*, not merely below the
		// cap. Below-max-but-above-floor is the cum-cooldown drain running as designed - the latch
		// is *supposed* to hold there, `ClearOverheatAtLockFloor` has not yet had its trigger, and
		// the drain can take seconds. A dwell cannot tell that apart from the defect, because the
		// defect is also "still true a while after the transition": three sessions read this
		// instrument wrong that way in a row (§134, §138, §139's own dwell included). Gating on the
		// floor instead means the line goes quiet for the whole drain and only fires on the thing
		// nothing should be able to produce - vanilla's own clear runs the instant heat crosses back
		// under it. `StuckLatchFloorEpsilon` absorbs float noise at that boundary, nothing more.
		if (latched && _player.CurrentHeat <= minHeat + StuckLatchFloorEpsilon)
		{
			if (_stuckLatchSince <= 0f) _stuckLatchSince = Time.unscaledTime;
			float held = Time.unscaledTime - _stuckLatchSince;
			if (Time.unscaledTime >= _nextStuckLatchReportAt)
			{
				_nextStuckLatchReportAt = Time.unscaledTime + 1f;
				Plugin.DBG("OVERHEAT", "STUCK: disarmed with heat at the lock floor - "
					+ $"held={held:F1}s heat={_player.CurrentHeat:F1}/{_player.MaxHeat:F1} floor={minHeat:F1} "
					+ $"canAttack={canAttack} locks={_locks}/{GetTotalLocks()}");
			}
		}
		else
		{
			_stuckLatchSince = 0f;
			_nextStuckLatchReportAt = 0f;
		}
	}

	// Float noise only, at the floor boundary - not a grace period. See TraceOverheatLatch.
	private const float StuckLatchFloorEpsilon = 0.05f;

	private static float _stuckLatchSince;
	private static float _nextStuckLatchReportAt;

	private static float _nextHeatTraceAt;
	private static float _lastTracedHeat = -1f;

	// The heat bar is hidden during grabs, so without this there is no way to see whether the
	// cum cooldown is actually moving heat or the lock floor is holding it at the top.
	private static void TraceGrabHeat()
	{
		GrabScreen grabScreen = GrabScreen.Instance;
		if (grabScreen == null || !grabScreen.IsGrabbed || _player == null)
		{
			_lastTracedHeat = -1f;
			return;
		}
		float currentHeat = _player.CurrentHeat;
		if (!(Time.time < _nextHeatTraceAt) || Mathf.Abs(currentHeat - _lastTracedHeat) >= 1f)
		{
			_nextHeatTraceAt = Time.time + 0.5f;
			_lastTracedHeat = currentHeat;
			float minHeat = 0f;
			if (TryGetLockedHeatRange(_player, out var m, out var _))
			{
				minHeat = m;
			}
			Traverse traverse = Traverse.Create((object)grabScreen);
			Plugin.DBG("HEAT-TRACE", $"heat={currentHeat:F1}/{_player.MaxHeat:F1} floor={minHeat:F1} locks={_locks}/{GetTotalLocks()} " + $"cooling={traverse.Field("isCoolingDown").GetValue<bool>()} blocked={traverse.Field("heatIncreaseBlocked").GetValue<bool>()} cumPlaying={traverse.Field("maxHeatAnimationPlaying").GetValue<bool>()}");
		}
	}

	internal static void HandleHotkeys()
	{
		if (IsRemoveHeatPressed())
		{
			RemoveOneLockFromHotkey();
		}
		if (IsAddHeatPressed())
		{
			AddOneLockFromHotkey();
		}
	}

	private static bool IsRemoveHeatPressed()
	{
		KeyboardShortcut keyRemoveHeat = Plugin.CfgKeyRemoveHeat.Value;
		return PncFramework.Controls.ControlService.IsDown(Plugin.CfgKeyRemoveHeat);
	}

	private static bool IsAddHeatPressed()
	{
		KeyboardShortcut keyAddHeat = Plugin.CfgKeyAddHeat.Value;
		return PncFramework.Controls.ControlService.IsDown(Plugin.CfgKeyAddHeat);
	}

	internal static void RecordBaseHealth(PlayerStats playerStats)
	{
		if (Enabled && !(playerStats == null))
		{
			_player = playerStats;
			int maxHealth = Mathf.Max(1, playerStats.MaxHealth);
			if (_baseHealth <= 0 || maxHealth < (OverkeksMode.UsesRecipe ? OverkeksConfig.CfgPlayerMaxHealth : Plugin.CfgPlayerMaxHealth).Value)
			{
				_baseHealth = maxHealth;
			}
			RecordBaseHeat(playerStats);
			UpdateDisplay();
		}
	}

	// The heat capacity is only final once the class multipliers have landed
	// (PlayerClassManager applies heatCapacityMultiplier/bonusHeatCapacity, then our own
	// ClassHeatMultipliers.ApplyToPlayerHeat multiplies again), and armour can move it
	// mid-run. So this re-records on every SetMaxHeat rather than snapshotting once, and
	// clamps the locks already taken if the capacity shrinks under them.
	// For one log line only (Plugin.OnSceneChanged). The question it answers is whether
	// activeSceneChanged fires before or after the new scene's Awake, which decides whether a
	// floor starts with this at 0 - correct, GetScalingHeat reads the live capacity back - or at
	// the prefab's 100, which is five locks for every class on every floor after the first,
	// because those floors take the LoadPlayerState branch and never call SetMaxHeat at all
	// (§148). Not derivable from the source; both orderings are consistent with it.
	internal static float BaseHeatForDiag => _baseHeat;

	// A capacity-shrink clamp arms here but does not land until PendingClampConfirm - either
	// applied by Tick once PendingClampConfirmSeconds pass with nothing to contradict it, or
	// discarded by ResetForScene if a shop transition follows first. See the two call sites for
	// why: the first attempt at this fix (§154) gated on "is this the first reading since a scene
	// reset", which assumed the transient shop-entry reading arrived *after* ResetForScene had
	// already zeroed _baseHeat. The log proved that wrong - the shop's SetMaxHeat(100) call, and
	// this clamp, both fire *before* Plugin.OnSceneChanged's own line, while _baseHeat still holds
	// the previous floor's real value, so "first reading since reset" was never true here. Worse,
	// the shop never sends a correcting SetMaxHeat afterward - MaxHeat genuinely stays 100 for the
	// whole visit - so no amount of waiting for a "second, truer reading" would ever arrive either.
	// The only signal that ever distinguishes this from a real armour-triggered shrink is the
	// scene-change event fired ~20ms later, which is too late to gate RecordBaseHeat itself but
	// not too late to unwind what it queued.
	private static int _pendingClampTotalLocks = -1;
	private static float _pendingClampAt;
	private const float PendingClampConfirmSeconds = 0.5f;

	internal static void RecordBaseHeat(PlayerStats playerStats)
	{
		if (!Enabled || playerStats == null)
		{
			return;
		}
		_player = playerStats;
		float maxHeat = Mathf.Max(1f, playerStats.MaxHeat);
		if (!(Mathf.Abs(maxHeat - _baseHeat) < 0.001f))
		{
			_baseHeat = maxHeat;
			int totalLocks = GetTotalLocks();
			if (_locks > totalLocks)
			{
				_pendingClampTotalLocks = totalLocks;
				_pendingClampAt = Time.unscaledTime;
			}
		}
	}

	// Called from Tick every frame: lands a pending clamp once it has gone unchallenged for
	// PendingClampConfirmSeconds, which only a genuine mid-floor capacity shrink (armour) survives
	// - a shop transition resolves via ResetForScene, below, well inside that window.
	private static void ConfirmPendingClamp()
	{
		if (_pendingClampTotalLocks < 0)
		{
			return;
		}
		if (Time.unscaledTime - _pendingClampAt < PendingClampConfirmSeconds)
		{
			return;
		}
		if (_locks > _pendingClampTotalLocks)
		{
			Plugin.DBG("HEAT-LOCK", $"capacity shrink clamp: {_locks} -> {_pendingClampTotalLocks} locks (baseHeat={_baseHeat:F1})");
			_locks = _pendingClampTotalLocks;
		}
		_pendingClampTotalLocks = -1;
	}

	private static void AdjustHeat(float amount)
	{
		PlayerStats playerStats = _player;
		if (playerStats == null)
		{
			playerStats = Object.FindAnyObjectByType<PlayerStats>();
		}
		if (!(playerStats == null))
		{
			_player = playerStats;
			float heat = Mathf.Clamp(playerStats.CurrentHeat + amount, 0f, playerStats.MaxHeat);
			_adjustingHeat = true;
			try
			{
				playerStats.SetHeat(heat);
			}
			finally
			{
				_adjustingHeat = false;
			}
			AfterHeatChanged(playerStats);
			Plugin.NotePlayerCum(playerStats);
			Plugin.DBG("HEAT-HOTKEY", ((amount > 0f) ? "+1" : "-1") + " -> " + heat.ToString("F1") + "/" + playerStats.MaxHeat.ToString("F1"));
		}
	}

	/// <summary>
	/// Add one lock because the player is standing somewhere that charges them - a custom enemy's
	/// charm circle - rather than because a scene played.
	///
	/// Returns whether a lock was actually gained, and the caller uses that: the witch's `lockSound`
	/// is meant to mark a real gain, so it must stay silent when the counter is already full or the
	/// profile has locks off entirely. That is why this reports rather than just doing.
	/// </summary>
	internal static bool AddTimedAuraLock(string label)
	{
		if (!Enabled)
		{
			return false;
		}
		PlayerStats playerStats = _player ?? Object.FindAnyObjectByType<PlayerStats>();
		if (playerStats == null)
		{
			return false;
		}
		_player = playerStats;
		RecordBaseHealth(playerStats);
		int totalLocks = GetTotalLocks();
		if (_locks >= totalLocks)
		{
			return false;
		}
		_locks++;
		if (_locks >= totalLocks)
		{
			ApplyFullLockHealthReset(playerStats);
		}
		EnsureHeatBounds(playerStats);
		Plugin.NotePlayerCum(playerStats);
		Plugin.DBG("HEAT-LOCK", "aura +" + (label ?? "exposure") + " -> " + _locks + "/" + totalLocks);
		UpdateDisplay();
		return true;
	}

	private static void AddOneLockFromHotkey()
	{
		PlayerStats playerStats = _player;
		if (playerStats == null)
		{
			playerStats = Object.FindAnyObjectByType<PlayerStats>();
		}
		if (playerStats == null)
		{
			return;
		}
		_player = playerStats;
		RecordBaseHealth(playerStats);
		int totalLocks = GetTotalLocks();
		if (_locks >= totalLocks)
		{
			EnsureHeatBounds(playerStats);
			UpdateDisplay();
			return;
		}
		_locks++;
		if (_locks >= totalLocks)
		{
			ApplyFullLockHealthReset(playerStats);
		}
		EnsureHeatBounds(playerStats);
		Plugin.NotePlayerCum(playerStats);
		Plugin.DBG("HEAT-HOTKEY", "+1 horny -> " + _locks + "/" + totalLocks);
		UpdateDisplay();
	}

	private static void RemoveOneLockFromHotkey()
	{
		PlayerStats playerStats = _player;
		if (playerStats == null)
		{
			playerStats = Object.FindAnyObjectByType<PlayerStats>();
		}
		if (playerStats == null)
		{
			return;
		}
		_player = playerStats;
		RecordBaseHealth(playerStats);
		int totalLocks = GetTotalLocks();
		if (_locks <= 0)
		{
			AdjustHeat(-1f);
			return;
		}
		_locks--;
		float maxHeat = Mathf.Max(1f, playerStats.MaxHeat);
		float targetHeat = ((_locks <= 0) ? Mathf.Max(0f, playerStats.CurrentHeat - 1f) : (maxHeat * (float)_locks / (float)totalLocks));
		_adjustingHeat = true;
		try
		{
			playerStats.SetHeat(Mathf.Clamp(targetHeat, 0f, maxHeat));
		}
		finally
		{
			_adjustingHeat = false;
		}
		Plugin.NotePlayerCum(playerStats);
		Plugin.DBG("HEAT-HOTKEY", "-1 horny -> " + _locks + "/" + totalLocks);
		UpdateDisplay();
	}

	// Removes up to `count` locks and the heat they were holding. Clamped at 0 locks and at
	// 0 heat, so an oversized potion just empties the meter rather than going negative.
	// Returns how many were actually removed.
	internal static int RemoveLocks(int count, string reason)
	{
		if (!Enabled || count <= 0)
		{
			return 0;
		}
		PlayerStats playerStats = _player;
		if (playerStats == null)
		{
			playerStats = Object.FindAnyObjectByType<PlayerStats>();
		}
		if (playerStats == null)
		{
			return 0;
		}
		_player = playerStats;
		RecordBaseHealth(playerStats);
		int totalLocks = GetTotalLocks();
		int removed = Mathf.Clamp(count, 0, Mathf.Max(0, _locks));
		if (removed <= 0)
		{
			return 0;
		}
		float maxHeat = Mathf.Max(1f, playerStats.MaxHeat);
		float heatPerLock = maxHeat / (float)Mathf.Max(1, totalLocks);
		_locks -= removed;
		float newHeat = Mathf.Clamp(playerStats.CurrentHeat - heatPerLock * (float)removed, 0f, maxHeat);
		_adjustingHeat = true;
		try
		{
			playerStats.SetHeat(newHeat);
		}
		finally
		{
			_adjustingHeat = false;
		}
		// The floor has just dropped, so re-clamp and let the overheat lift if the new floor
		// allows it.
		EnsureHeatBounds(playerStats);
		ClearOverheatAtFloor(playerStats);
		// Keeps the filler in step - heat drove the intensity map and the bucket has moved.
		Plugin.NotePlayerCum(playerStats);
		Plugin.DBG("HEAT-LOCK", reason + ": -" + removed + " horny -> " + _locks + "/" + totalLocks + ", heat -" + (heatPerLock * (float)removed).ToString("F0") + " -> " + playerStats.CurrentHeat.ToString("F0") + "/" + maxHeat.ToString("F0"));
		UpdateDisplay();
		return removed;
	}

	internal static bool IsFullyLocked()
	{
		return Enabled && _locks >= GetTotalLocks();
	}

	internal static bool IsFullLockDangerActive()
	{
		return IsFullyLocked();
	}

	internal static void EnsureSceneEntrySurvival(string source)
	{
		if (!Plugin.GameplayTweaksEnabled)
		{
			return;
		}
		// The trio scene this hands off to is itself a scene entry, so without this the
		// grab's own EnsureSceneEntrySurvival would revive the player to 1 HP (and clear
		// PlayerDead) for the death scene - the exact thing GrappleDeathSequence exists to
		// avoid. Keep the arming, drop the revive.
		if (GrappleDeathSequence.Active)
		{
			ArmPendingGameOver(source + " (grapple death)");
			return;
		}
		// A scene starting behind the game over panel does not get to undo it. Refusing the
		// cancel alone would leave the player alive at 1 HP behind a panel they had already
		// answered; the death has been presented, so this stands down entirely (§119).
		if (DragonGrabGameOverGuard.GameOverIsThePlayersToAnswer())
		{
			Plugin.DBG("HEAT-LOCK", "scene entry survival declined from " + source + " - the game over is already the player's to answer");
			return;
		}
		PlayerStats playerStats = _player ?? Object.FindAnyObjectByType<PlayerStats>();
		if (playerStats == null || playerStats.CurrentHealth > 0)
		{
			return;
		}
		_player = playerStats;
		_adjustingHealth = true;
		try
		{
			if (playerStats.MaxHealth <= 0)
			{
				playerStats.SetMaxHealth(1, false);
			}
			playerStats.SetHealth(1);
		}
		finally
		{
			_adjustingHealth = false;
		}
		Plugin.PlayerDead = false;
		_pendingSceneEntryGameOver = true;
		DragonGrabGameOverGuard.CancelPendingGameOver();
		Plugin.DBG("HEAT-LOCK", "scene entry survival set HP to 1 from " + source);
	}

	// Every caller runs after its own scene flag is already cleared (FinishGrabEnd is an
	// EndGrab postfix, ResetState clears Active first, EndGrapple_Postfix follows
	// isGrappling = false), so anything still true here is a *different* scene.
	private static bool IsAnySceneActive()
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

	// Arms the deferred game over without touching health, for a player who is meant to
	// stay dead through the scene (GrappleDeathSequence). EnsureSceneEntrySurvival's
	// revive-to-1-HP is what makes a grapple feel survivable, which is the opposite of
	// what a death sequence wants.
	internal static void ArmPendingGameOver(string source)
	{
		if (Plugin.GameplayTweaksEnabled)
		{
			_pendingSceneEntryGameOver = true;
			DragonGrabGameOverGuard.CancelPendingGameOver();
			Plugin.DBG("HEAT-LOCK", "scene game over armed from " + source);
		}
	}

	internal static void CompleteSceneEntrySurvival(string source)
	{
		if (!_pendingSceneEntryGameOver)
		{
			return;
		}
		// The trio overflow ends the grapple while the grab it escalated into is only just
		// starting - GrappleScreenobject.TriggerTrioGrabOverflow calls StartGrab and then
		// EndGrapple - so completing here would drop a game over on top of a scene that has
		// not played yet. Stay armed and let the scene that is still running finish it.
		if (IsAnySceneActive())
		{
			Plugin.DBG("HEAT-LOCK", "scene entry survival held past " + source + " (another scene still live)");
			return;
		}
		PlayerStats playerStats = _player ?? Object.FindAnyObjectByType<PlayerStats>();
		if (playerStats == null)
		{
			_pendingSceneEntryGameOver = false;
			return;
		}
		_pendingSceneEntryGameOver = false;
		_forcingPendingSceneEntryGameOver = true;
		// Committed before the trigger, not after: TriggerGameOver starts ShowGameOverSequence
		// synchronously up to its first yield, and everything that runs in the same frame -
		// a second EndGrab, the escape key, the mod's own teardown - would otherwise cancel it.
		DragonGrabGameOverGuard.CommitGameOverPresentation("scene entry survival from " + source);
		try
		{
			playerStats.SetHealth(0);
			Traverse.Create((object)playerStats).Method("Die", Array.Empty<object>()).GetValue();
			GameOverScreen gameOverScreen = GameOverScreen.Instance;
			if (gameOverScreen != null)
			{
				gameOverScreen.TriggerGameOver();
			}
		}
		finally
		{
			_forcingPendingSceneEntryGameOver = false;
		}
		Plugin.DBG("HEAT-LOCK", "scene entry survival completed with game over from " + source);
	}

	internal static void ApplySceneEntryPenalty(string source)
	{
		if (IsFullLockDangerActive() && !(_player == null) && !Plugin.PlayerDead && _player.CurrentHealth > 0)
		{
			int damage = Mathf.Min(40, _player.CurrentHealth);
			if (damage > 0)
			{
				Plugin.DBG("HEAT-LOCK", "scene penalty -" + damage + " HP from " + source);
				_player.TakeDamage(damage);
			}
		}
	}

	internal static void NoteGrabSceneTriggered(GameObject enemy)
	{
		if (Enabled && !(enemy == null))
		{
			if (GrabStruggleHooks.IsChaserBossGrabEnemy(enemy))
			{
				SetMaxLocks("chaser " + NameRemap.StripCloneSuffix(enemy.name));
				return;
			}
			string key = BuildSceneKey("grab", NameRemap.StripCloneSuffix(enemy.name), enemy.transform);
			string label = "grab " + NameRemap.StripCloneSuffix(enemy.name);
			bool oncePerSource = (OverkeksMode.UsesRecipe ? OverkeksConfig.CfgHeatLockOncePerGrabSource : Plugin.CfgHeatLockOncePerGrabSource)?.Value ?? false;
			// A miniboss costs more than one lock. Between an ordinary grab (one) and a chaser
			// boss (straight to the limit) there was nothing, so the Blinded Beast - which is
			// harder to be caught by than anything that walks - paid exactly what an imp does.
			// The first call carries the repeat guard; the rest go through unguarded, because
			// the guard is keyed on the source and would refuse its own siblings.
			int locks = IsMinibossEnemy(enemy) ? Mathf.Max(1, (OverkeksMode.UsesRecipe ? OverkeksConfig.CfgHeatLockMinibossLocks : Plugin.CfgHeatLockMinibossLocks)?.Value ?? 1) : 1;
			if (!AddLockForFirstScene(key, label, oncePerSource))
			{
				return;
			}
			for (int i = 1; i < locks; i++)
			{
				AddLockForFirstScene(key, label + $" (miniboss {i + 1}/{locks})", oncePerSource: false);
			}
		}
	}

	// Is this enemy one of the MinibossEnemyKeys? The key is the remapped one (`blinded_beast`),
	// not the prefab name, so the list reads the same as every other enemy list in the config.
	internal static bool IsMinibossEnemy(GameObject enemy)
	{
        if(OverkeksMode.Active && BlindedBeastWeakSpawn.IsWeakRandomSpawn(enemy))return false;
		if (enemy == null)
		{
			return false;
		}
		return IsMinibossKey(OverkeksMode.Active ? EnemyIdentityTuning.ResolveIdentityKey(enemy,EnemyAiTypes.First(enemy)) : NameRemap.ResolveEnemyKey(enemy.name));
	}

	internal static bool IsMinibossKey(string key)
	{
		if (string.IsNullOrEmpty(key))
		{
			return false;
		}
		string[] entries = ((OverkeksMode.UsesRecipe ? OverkeksConfig.CfgMinibossEnemyKeys : Plugin.CfgMinibossEnemyKeys)?.Value ?? "").Split(';');
		for (int i = 0; i < entries.Length; i++)
		{
			string entry = entries[i].Trim();
			if (entry.Length != 0 && key.Equals(entry, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}
		return false;
	}

	internal static void NoteInteractSceneTriggered(CameraSwapTrigger trigger, string galleryId, string rawName, Animator animator)
	{
		if (!Enabled || trigger == null || IsKeyhole(trigger, galleryId, rawName, animator))
		{
			return;
		}
		// A service is the other half of the same decision, and it had no caller: `IsService`
		// only ever stopped the lock being charged, so `TryReleaseFromService` sat unreached and
		// the payout it exists for never happened. Charging nothing and paying nothing are not
		// the same thing - Gravy is meant to lower the meter, not merely to be free.
		if (IsService(trigger, galleryId, rawName, animator))
		{
			TryReleaseFromService(trigger, galleryId, rawName, animator);
			return;
		}
		string firstClipName = GetFirstClipName(animator);
		string sceneKey = BuildSceneKey("interact", rawName ?? galleryId ?? firstClipName ?? trigger.name, trigger.transform);
		_lastInteractLockKey = (AddLockForFirstScene(sceneKey, "interact " + (rawName ?? galleryId ?? firstClipName ?? trigger.name)) ? sceneKey : null);
	}

	internal static void AfterHeatChanged(PlayerStats playerStats)
	{
		if (Enabled && !_adjustingHeat && !(playerStats == null))
		{
			_player = playerStats;
			RecordBaseHealth(playerStats);
			AcquireLocksFromHeat(playerStats);
			EnsureHeatBounds(playerStats);
			ClearOverheatAtFloor(playerStats);
			UpdateDisplay();
		}
	}

	internal static void TryReleaseFromAmbient(string gallery, string canonicalName, AudioSource source)
	{
		if (Enabled && !(source == null))
		{
			string key = BuildReleaseKey("ambient", canonicalName ?? gallery, source.transform);
			TryRelease(key, "ambient " + (canonicalName ?? gallery));
		}
	}

	internal static void TryReleaseFromKeyhole(CameraSwapTrigger trigger, string galleryId, string rawName, Animator animator)
	{
		if (Enabled && !(trigger == null) && IsKeyhole(trigger, galleryId, rawName, animator))
		{
			string label = BuildKeyholeLabel(trigger, galleryId, rawName);
			string key = BuildReleaseKey("keyhole", label, trigger.transform);
			// Peeking *arms* the release; watching it out pays it. §23 made this instant to
			// stop peepholes feeling like a trap, but that removed any reason to stay for the
			// scene. The trap was the countdown label, not the timer - it is a fill bar now.
			// Same key as QueueReleaseFromPeekScene, so whichever path fires first wins and
			// the other is a no-op.
			QueueKeyholeRelease(key, "peephole " + label);
		}
	}

	internal static void TryReleaseFromService(CameraSwapTrigger trigger, string galleryId, string rawName, Animator animator)
	{
		if (Enabled && !(trigger == null) && IsService(trigger, galleryId, rawName, animator))
		{
			string label = BuildServiceLabel(trigger, galleryId, rawName);
			string key = BuildReleaseKey("service", label, trigger.transform);
			QueueKeyholeRelease(key, "service " + label);
		}
	}

	internal static void QueueReleaseFromPeekScene(CameraSwapTrigger trigger, string galleryId, string rawName, string clip)
	{
		if (Enabled && !(trigger == null))
		{
			string label = BuildKeyholeLabel(trigger, galleryId, rawName);
			string key = BuildReleaseKey("keyhole", label, trigger.transform);
			UndoAccidentalInteractLock(trigger, galleryId, rawName, clip);
			QueueKeyholeRelease(key, "peephole " + label);
		}
	}

	internal static bool IsReleaseSourceUsed(string kind, string label, Transform transform)
	{
		return UsedReleaseSources.Contains(BuildReleaseKey(kind, label, transform));
	}

	private static void TryRelease(string key, string label)
	{
		if (string.IsNullOrEmpty(key))
		{
			return;
		}
		if (UsedReleaseSources.Contains(key))
		{
			SetReleaseStatus("Already used");
			return;
		}
		if (_locks <= 0)
		{
			// Checked *before* the source is marked spent. The gaze gate refuses to start the
			// meter at zero locks, but it re-checks per frame while the payout is three seconds
			// later - drinking a heat potion, or another release landing, empties the locks
			// inside that window. Marking first burned the diorama for the rest of the run and
			// looked exactly like the meter being broken. No status either: with no locks there
			// is nothing for the player to act on.
			Plugin.DBG("HEAT-LOCK", "release not spent, no locks held at payout from " + label);
			UpdateDisplay();
			return;
		}
		UsedReleaseSources.Add(key);
		int locks = _locks;
		bool enabled = (OverkeksMode.UsesRecipe ? OverkeksConfig.CfgAmbientReleaseClearsAllLocks : Plugin.CfgAmbientReleaseClearsAllLocks)?.Value ?? true;
		_locks = (enabled ? 0 : Mathf.Max(0, _locks - 1));
		if (_player == null)
		{
			_player = Object.FindAnyObjectByType<PlayerStats>();
		}
		if (_player != null)
		{
			EnsureHeatBounds(_player);
			// The peephole path has always done these two and this one never did, which is
			// why a diorama release dropped the lock count but left the heat sitting where it
			// was: overheat stays latched at the old floor, and the filler keeps driving off
			// a stale heat reading.
			ClearOverheatAtFloor(_player);
			Plugin.NotePlayerCum(_player);
		}
		int released = locks - _locks;
		SetReleaseStatus(enabled ? ("Horny cleared (-" + released + ")") : "Horny -1");
		Plugin.DBG("HEAT-LOCK", "release " + released + " lock(s) from " + label + " -> " + _locks + "/" + GetTotalLocks() + " (was " + locks + ")");
		UpdateDisplay();
	}


	internal static bool IsKeyholeTrigger(CameraSwapTrigger trigger, string galleryId, string rawName, Animator animator)
	{
		return IsKeyhole(trigger, galleryId, rawName, animator);
	}

	private static void QueueKeyholeRelease(string key, string label)
	{
		if (!string.IsNullOrEmpty(key))
		{
			if (UsedReleaseSources.Contains(key))
			{
				_activeReleaseStatus = null;
				SetReleaseStatus("Already used");
				UpdateDisplay();
			}
			else if (!_pendingKeyholeRelease || !string.Equals(_pendingKeyholeKey, key, StringComparison.OrdinalIgnoreCase))
			{
				_pendingKeyholeRelease = true;
				_pendingKeyholeKey = key;
				_pendingKeyholeLabel = label;
				_pendingKeyholeArmedAt = Time.time;
				// No text: the fill bar under the Horny readout is the countdown now.
				_activeReleaseStatus = null;
				Plugin.DBG("HEAT-LOCK", "queued clear-all release from " + label + " after watchtime");
				UpdateDisplay();
			}
		}
	}

	private static void UndoAccidentalInteractLock(CameraSwapTrigger trigger, string galleryId, string rawName, string clip)
	{
		if (_locks > 0 && !(trigger == null) && !string.IsNullOrEmpty(_lastInteractLockKey))
		{
			string b = BuildSceneKey("interact", rawName ?? galleryId ?? clip ?? trigger.name, trigger.transform);
			string b2 = BuildSceneKey("interact", rawName ?? galleryId ?? GetFirstClipName(Traverse.Create((object)trigger).Field("animatorToTrigger").GetValue<Animator>()) ?? trigger.name, trigger.transform);
			if (string.Equals(_lastInteractLockKey, b, StringComparison.OrdinalIgnoreCase) || string.Equals(_lastInteractLockKey, b2, StringComparison.OrdinalIgnoreCase))
			{
				_locks = Mathf.Max(0, _locks - 1);
				_lastInteractLockKey = null;
				Plugin.DBG("HEAT-LOCK", "removed accidental interact lock for peephole " + (rawName ?? galleryId ?? trigger.name) + " -> " + _locks + "/" + GetTotalLocks());
				UpdateDisplay();
			}
		}
	}

	// Seconds of peeping a keyhole release costs. Its own setting rather than the scene's
	// escape delay, which is what gated this before and is a different thing being timed.
	// 0 pays out the instant you peek.
	internal static float KeyholeWatchSeconds => Mathf.Max(0f, (OverkeksMode.UsesRecipe ? OverkeksConfig.CfgKeyholeReleaseWatchSeconds : Plugin.CfgKeyholeReleaseWatchSeconds)?.Value ?? 10f);
	internal static bool HasPendingKeyholeRelease => Enabled && _pendingKeyholeRelease;

	// At 0 seconds the release pays out on the same frame it is armed, so there is nothing to
	// fill and a bar would flash for one frame.
	internal static bool HasKeyholeWatchBar => HasPendingKeyholeRelease && KeyholeWatchSeconds > 0f;

	// Drives the same fill bar the diorama look timer uses.
	internal static float KeyholeWatchProgress
	{
		get
		{
			if (!_pendingKeyholeRelease)
			{
				return 0f;
			}
			float keyholeWatchSeconds = KeyholeWatchSeconds;
			if (keyholeWatchSeconds <= 0f)
			{
				return 1f;
			}
			return Mathf.Clamp01((Time.time - _pendingKeyholeArmedAt) / keyholeWatchSeconds);
		}
	}

	// Pays out the moment the bar fills rather than banking it until the scene ends, so a
	// peephole behaves like a diorama: watch it out and the locks go. Leaving early loses it.
	internal static void TickPendingKeyholeRelease()
	{
		if (_pendingKeyholeRelease && Time.time - _pendingKeyholeArmedAt >= KeyholeWatchSeconds)
		{
			PayPendingKeyholeRelease();
		}
	}

	internal static void CompletePendingKeyholeRelease()
	{
		if (!_pendingKeyholeRelease)
		{
			return;
		}
		string label = _pendingKeyholeLabel ?? "peephole";
		if (Time.time - _pendingKeyholeArmedAt < KeyholeWatchSeconds)
		{
			// Abandoned, not spent: the key never reaches UsedReleaseSources, so peeking again
			// re-arms it from zero. No status label either - the bar vanishing already says it.
			_pendingKeyholeRelease = false;
			_pendingKeyholeKey = null;
			_pendingKeyholeLabel = null;
			_activeReleaseStatus = null;
			SetAmbientGauge(0f);
			Plugin.DBG("HEAT-LOCK", "peephole left early, release still available for " + label);
			UpdateDisplay();
			return;
		}
		PayPendingKeyholeRelease();
	}

	private static void PayPendingKeyholeRelease()
	{
		string label = _pendingKeyholeLabel ?? "peephole";
		string pendingKeyholeKey = _pendingKeyholeKey;
		_pendingKeyholeRelease = false;
		_pendingKeyholeKey = null;
		_pendingKeyholeLabel = null;
		_activeReleaseStatus = null;
		SetAmbientGauge(0f);
		int locks = _locks;
		if (locks <= 0)
		{
			// Same reasoning as TryRelease: never spend the source on an empty payout. Ten
			// seconds of watching is ample time for a heat potion to clear the locks first.
			Plugin.DBG("HEAT-LOCK", "peephole not spent, no locks held at payout from " + label);
			UpdateDisplay();
			return;
		}
		if (!string.IsNullOrEmpty(pendingKeyholeKey))
		{
			UsedReleaseSources.Add(pendingKeyholeKey);
		}
		_locks = 0;
		if (_player == null)
		{
			_player = Object.FindAnyObjectByType<PlayerStats>();
		}
		if (_player != null)
		{
			EnsureHeatBounds(_player);
			// The deleted instant path did these two and this one never has. Now that it is the
			// only way a peephole pays out, it inherits the same "locks drop but the heat does
			// not move" bug the diorama path had (§28).
			ClearOverheatAtFloor(_player);
			Plugin.NotePlayerCum(_player);
		}
		// Same wording the instant path used, which is the one worth keeping now that this is
		// the only way a peephole pays out. Nothing shown when there was nothing to clear.
		if (locks > 0)
		{
			SetReleaseStatus("Horny cleared (-" + locks + ")");
		}
		Plugin.DBG("HEAT-LOCK", "clear all locks from " + label + " -> 0/" + GetTotalLocks() + " (was " + locks + ")");
		UpdateDisplay();
	}

	internal static void EndKeyholeSceneStatus()
	{
		if (!_pendingKeyholeRelease)
		{
			_activeReleaseStatus = null;
			UpdateDisplay();
		}
	}

	// The gaze gate short-circuits before TryRelease, which is what used to put "already
	// used" on screen for a diorama. Without this a spent or unusable diorama just shows no
	// bar, which is indistinguishable from the feature being broken.
	internal static void SetAmbientReleaseStatus(string status)
	{
		if (Enabled && !string.IsNullOrEmpty(status))
		{
			SetReleaseStatus(status);
			UpdateDisplay();
		}
	}

	private static void SetReleaseStatus(string status)
	{
		_lastReleaseStatus = status;
		_releaseStatusUntil = Time.realtimeSinceStartup + 6f;
	}


	private static void AcquireLocksFromHeat(PlayerStats playerStats)
	{
	}

	// `oncePerSource` false means every entry locks, regardless of what has been seen before.
	//
	// Grabs pass false, because the guard was never meaningful for them. The key carries the
	// source's POSITION (BuildSceneKey rounds to 0.1 m), so it did not mean "one lock per enemy"
	// at all - it meant "one lock per enemy per square decimetre". A walking enemy earned a lock
	// every time it caught you somewhere new, a mimic chest earned exactly one per run, and a
	// walking enemy that caught you twice in the same doorway was refused the second (observed:
	// `scene REPEAT grab Gargoyle Alt`). No reading of the design gets that behaviour, so it goes.
	//
	// Peepholes and dioramas still pass true. Those are passive and would otherwise be farmable
	// by walking back and forth past one, which is exactly what the guard is for.
	private static bool AddLockForFirstScene(string key, string label, bool oncePerSource = true)
	{
		if (string.IsNullOrEmpty(key))
		{
			return false;
		}
		if (oncePerSource && !SeenSceneSources.Add(key))
		{
			Plugin.DBG("HEAT-LOCK", "scene REPEAT " + label + " -> no lock (key already seen: " + key + ")");
			return false;
		}
		if (!oncePerSource)
		{
			SeenSceneSources.Add(key);
		}
		PlayerStats playerStats = _player ?? Object.FindAnyObjectByType<PlayerStats>();
		if (playerStats == null)
		{
			return false;
		}
		_player = playerStats;
		RecordBaseHealth(playerStats);
		int totalLocks = GetTotalLocks();
		if (_locks >= totalLocks)
		{
			return false;
		}
		_locks++;
		if (_locks >= totalLocks)
		{
		}
		EnsureHeatBounds(playerStats);
		Plugin.DBG("HEAT-LOCK", "scene +" + label + " -> " + _locks + "/" + totalLocks);
		UpdateDisplay();
		return true;
	}

	private static void SetMaxLocks(string label)
	{
		PlayerStats playerStats = _player ?? Object.FindAnyObjectByType<PlayerStats>();
		if (!(playerStats == null))
		{
			_player = playerStats;
			RecordBaseHealth(playerStats);
			int totalLocks = (_locks = GetTotalLocks());
			EnsureHeatBounds(playerStats);
			Plugin.DBG("HEAT-LOCK", "max from " + label + " -> " + _locks + "/" + totalLocks);
			UpdateDisplay();
		}
	}

	private static void EnsureHeatBounds(PlayerStats playerStats)
	{
		if (!Enabled || _adjustingHeat || playerStats == null || IsRecoveringFromOverheatSlowness(playerStats) || !TryGetLockedHeatRange(playerStats, out var minHeat, out var maxAllowed))
		{
			return;
		}
		float currentHeat = playerStats.CurrentHeat;
		float clamped = Mathf.Clamp(currentHeat, minHeat, maxAllowed);
		if (Mathf.Abs(clamped - currentHeat) < 0.001f)
		{
			return;
		}
		_adjustingHeat = true;
		try
		{
			playerStats.SetHeat(clamped);
		}
		finally
		{
			_adjustingHeat = false;
		}
	}

	private static bool TryGetLockedHeatRange(PlayerStats playerStats, out float minHeat, out float maxAllowed)
	{
		minHeat = 0f;
		maxAllowed = 0f;
		if (!Enabled || playerStats == null)
		{
			return false;
		}
		int totalLocks = GetTotalLocks();
		if (totalLocks <= 0 || _locks <= 0)
		{
			return false;
		}
		float maxHeat = Mathf.Max(1f, playerStats.MaxHeat);
		float fullLockHeat = GetFullLockHeat(maxHeat);
		minHeat = ((_locks >= totalLocks) ? fullLockHeat : (maxHeat * (float)_locks / (float)totalLocks));
		// The ceiling is vanilla's overflow cap, not MaxHeat: a costly spell cast near the
		// top should be able to push heat past max and then tick back down to the floor.
		// Clamping to MaxHeat here used to swallow that overshoot entirely. GenerateHeat
		// already caps itself at maxHeatOverflow, so this is really just "do not pull it
		// back down" - HandleHeatCooldown does the ticking.
		maxAllowed = Mathf.Max(maxHeat, playerStats.MaxHeatWithOverflow);
		return true;
	}

	private static void ApplyFullLockHealthReset(PlayerStats playerStats)
	{
		if (_adjustingHealth || playerStats == null)
		{
			return;
		}
		RecordBaseHealth(playerStats);
		int baseHealth = Mathf.Max(1, _baseHealth);
		_adjustingHealth = true;
		try
		{
			if (playerStats.MaxHealth != baseHealth)
			{
				playerStats.SetMaxHealth(baseHealth, false);
			}
			if (playerStats.CurrentHealth > baseHealth)
			{
				playerStats.SetHealth(baseHealth);
			}
		}
		finally
		{
			_adjustingHealth = false;
		}
	}

	// Full lock parks heat one point below the cap. It used to sit one point below
	// overheatingThreshold instead, to dodge vanilla's "no attacking until heat is back to
	// exactly 0" rule (PlayerStats.CanAttackNow / CanDash) which a lock floor makes
	// unreachable and therefore permanent. ClearOverheatAtFloor fixes that at the source,
	// so the threshold dodge is gone and full lock can mean what it says.
	private static float GetFullLockHeat(float maxHeat)
	{
		return Mathf.Max(0f, maxHeat - 1f);
	}

	// Vanilla clears hasBeenOverheated only at exactly 0 heat (HandleOverheatingStates) or
	// when a consumable calls ClearOverheatIfBelowMax - there is no automatic
	// "below max" check. A lock floor holds heat above 0 permanently, so the automatic
	// path can never fire and one overflow would disarm the player for the rest of the run.
	// The floor is this system's equivalent of "fully cooled", so clear there instead.
	// ClearOverheatIfBelowMax only asks for heat <= MaxHeat, which any floor satisfies.
	private static void ClearOverheatAtFloor(PlayerStats playerStats)
	{
		if (!Enabled || !(OverkeksMode.UsesRecipe ? OverkeksConfig.CfgClearOverheatAtLockFloor : Plugin.CfgClearOverheatAtLockFloor).Value || playerStats == null)
		{
			return;
		}
		// No locks held: floor is 0 and vanilla's own clear already covers it.
		if (!TryGetLockedHeatRange(playerStats, out var minHeat, out var _) || minHeat <= 0f)
		{
			return;
		}
		if (playerStats.CurrentHeat <= minHeat + 0.05f)
		{
			playerStats.ClearOverheatIfBelowMax();
		}
	}

	private static bool IsRecoveringFromOverheatSlowness(PlayerStats playerStats)
	{
		return playerStats != null && Traverse.Create((object)playerStats).Field("movementWasSlowed").GetValue<bool>();
	}

	private static void ApplyHeatScaledAutoHeal(PlayerStats playerStats)
	{
		if (playerStats == null || Plugin.PlayerDead || !playerStats.IsAlive || playerStats.CurrentHealth >= playerStats.MaxHealth || IsFullLockDangerActive())
		{
			_healAccumulator = 0f;
			return;
		}
		float maxHeat = Mathf.Max(1f, playerStats.MaxHeat);
		float heat = Mathf.Clamp(playerStats.CurrentHeat, 0f, maxHeat);
		if (heat >= maxHeat - 0.001f)
		{
			_healAccumulator = 0f;
			return;
		}
		float heatFraction = heat / maxHeat;
		float rateScale = Mathf.Pow(1f - heatFraction, 2f) * 0.01f;
		float healPerSecond = Mathf.Max(0f, (OverkeksMode.UsesRecipe ? OverkeksConfig.CfgHeatLockAutoHealRate : Plugin.CfgHeatLockAutoHealRate).Value) * rateScale;
		if (healPerSecond <= 0f)
		{
			return;
		}
		_healAccumulator += healPerSecond * Time.deltaTime;
		int wholeHitPoints = Mathf.FloorToInt(_healAccumulator);
		if (wholeHitPoints <= 0)
		{
			return;
		}
		_healAccumulator -= wholeHitPoints;
		_adjustingHealth = true;
		try
		{
			playerStats.Heal(Mathf.Min(wholeHitPoints, playerStats.MaxHealth - playerStats.CurrentHealth));
		}
		finally
		{
			_adjustingHealth = false;
		}
	}

	// The horny limit: how many locks it takes to go from empty to fully locked. Scales
	// off the cum meter (heat capacity) by default; "Health" restores the old max-HP
	// behaviour. The scaling stat is the FINAL one - RecordBaseHeat runs after both the
	// class's own heatCapacityMultiplier and our ClassHeatMultipliers have landed - so the
	// spread across classes is wide (Knight 100, Ranger/Mage 250 with the shipped x2), and
	// the divisor is what keeps the high-capacity classes from drowning in locks.
	// Rounds UP: a class whose capacity is not a whole multiple of the divisor keeps the
	// partial lock rather than having it truncated away (Rogue 150/20 = 7.5 -> 8, not 7).
	// Flooring also made the Mathf.Max(1, ...) load-bearing - it was the only thing stopping
	// a divisor above the smallest capacity from producing zero locks - and a clamp doing
	// real work is a clamp that hides the case it is covering. Ceiling can only ever return
	// 0 for a non-positive capacity, which neither source can produce, so the Max is now a
	// genuine backstop.
	//
	// This changes granularity only, not where the locks sit: TryGetLockedHeatRange computes
	// the floor as MaxHeat * _locks / totalLocks, a proportion of capacity rather than
	// _locks * HeatLockUnitsPerLock, so the last lock lands exactly at full either way.
	private static int GetTotalLocks()
	{
		float unitsPerLock = Mathf.Max(1, (OverkeksMode.UsesRecipe ? OverkeksConfig.CfgHeatLockUnitsPerLock : Plugin.CfgHeatLockUnitsPerLock)?.Value ?? 10);
		float scalingCapacity = (UseHealthScaling ? ((float)Mathf.Max(1, _baseHealth)) : GetScalingHeat());
		return Mathf.Max(1, Mathf.CeilToInt(scalingCapacity / unitsPerLock));
	}

	private static bool UseHealthScaling
	{
		get
		{
			string scaleSource = (OverkeksMode.UsesRecipe ? OverkeksConfig.CfgHeatLockScaleSource : Plugin.CfgHeatLockScaleSource)?.Value;
			return scaleSource != null && scaleSource.Trim().Equals("Health", StringComparison.OrdinalIgnoreCase);
		}
	}

	private static float GetScalingHeat()
	{
		if (_baseHeat > 0f)
		{
			return _baseHeat;
		}
		PlayerStats playerStats = _player;
		if (playerStats == null)
		{
			playerStats = Object.FindAnyObjectByType<PlayerStats>();
		}
		if (playerStats != null)
		{
			return Mathf.Max(1f, playerStats.MaxHeat);
		}
		return 100f;
	}

	// Peek holes carry their own looping audio, and several diorama patterns match it
	// ("wendigo hole scene", "zombie blowjob hole scene", "zombiebjpeep"), so proximity would
	// treat a peephole as a diorama. Peeks are driven by interacting with the hole instead -
	// TryReleaseFromKeyhole for the release, the camera-swap scene for playback - so the
	// ambient path has to leave them alone. Resolved names cannot tell them apart: a peek
	// hole matched by a diorama pattern still resolves to an ambient_* name.
	internal static bool IsNearPeekHole(Transform origin, float radius)
	{
		if (origin == null)
		{
			return false;
		}
		CameraSwapTrigger[] cameraSwapTriggers = Object.FindObjectsByType<CameraSwapTrigger>((FindObjectsSortMode)0);
		float radiusSquared = radius * radius;
		foreach (CameraSwapTrigger cameraSwapTrigger in cameraSwapTriggers)
		{
			if (cameraSwapTrigger == null)
			{
				continue;
			}
			Vector3 offset = cameraSwapTrigger.transform.position - origin.position;
			if (!(offset.sqrMagnitude > radiusSquared))
			{
				Animator animator = Traverse.Create((object)cameraSwapTrigger).Field("animatorToTrigger").GetValue<Animator>();
				string galleryId = Traverse.Create((object)cameraSwapTrigger).Field("galleryEntryID").GetValue<string>();
				string rawName = NameRemap.StripCloneSuffix(cameraSwapTrigger.gameObject.name ?? "");
				if (IsKeyhole(cameraSwapTrigger, galleryId, rawName, animator))
				{
					return true;
				}
			}
		}
		return false;
	}

	private static bool IsKeyhole(CameraSwapTrigger trigger, string galleryId, string rawName, Animator animator)
	{
		string lower = (galleryId + " " + rawName + " " + trigger.name).ToLowerInvariant();
		if (lower.Contains("keyhole") || lower.Contains("peephole") || lower.Contains("peakhole") || lower.Contains("peek") || lower.Contains("peep"))
		{
			return true;
		}
		string galleryName = PeekGalleryMap.Resolve(galleryId, rawName, GetFirstClipName(animator));
		if (PeekGalleryMap.IsPeekScript(galleryName))
		{
			return true;
		}
		if (animator == null || animator.runtimeAnimatorController == null)
		{
			return false;
		}
		AnimationClip[] animationClips = animator.runtimeAnimatorController.animationClips;
		if (animationClips == null)
		{
			return false;
		}
		for (int i = 0; i < animationClips.Length; i++)
		{
			if (!(animationClips[i] == null) && !string.IsNullOrEmpty(animationClips[i].name) && PeekGalleryMap.IsPeekScript(PeekGalleryMap.Resolve(galleryId, rawName, animationClips[i].name)))
			{
				return true;
			}
		}
		return false;
	}

	// A scene the player is *given* rather than charged for. It adds no lock, and pays out a
	// release instead - the same one a peephole gives, and once per source, so it cannot be
	// farmed by walking back to the same shopkeeper.
	//
	// This used to test one hard-coded fragment, `service`, which appears nowhere in the game:
	// no clip, no trigger, not once in `Assembly-CSharp`. So the exemption existed and had never
	// fired, and Gravy - who heals in the unmodded game - was charging a lock like any enemy.
	// The fragments are configuration now, `ServiceSceneKeys`, which is also how a custom-enemy
	// package gets to ship a shopkeeper without a code change.
	private static bool IsService(CameraSwapTrigger trigger, string galleryId, string rawName, Animator animator)
	{
		if (trigger == null)
		{
			return false;
		}
		string[] keys = ServiceSceneKeys();
		if (keys.Length == 0)
		{
			return false;
		}
		string lower = (galleryId + " " + rawName + " " + trigger.name).ToLowerInvariant();
		for (int i = 0; i < keys.Length; i++)
		{
			if (lower.Contains(keys[i]))
			{
				return true;
			}
		}
		if (animator == null || animator.runtimeAnimatorController == null)
		{
			return false;
		}
		AnimationClip[] animationClips = animator.runtimeAnimatorController.animationClips;
		if (animationClips == null)
		{
			return false;
		}
		for (int i = 0; i < animationClips.Length; i++)
		{
			AnimationClip clip = animationClips[i];
			if (clip == null || string.IsNullOrEmpty(clip.name))
			{
				continue;
			}
			string clipName = clip.name.ToLowerInvariant();
			for (int j = 0; j < keys.Length; j++)
			{
				if (clipName.Contains(keys[j]))
				{
					return true;
				}
			}
		}
		return false;
	}

	// Lower-cased once per call rather than per clip: the list is short and the clip loop is not.
	private static string[] ServiceSceneKeys()
	{
		string[] entries = ((OverkeksMode.UsesRecipe ? OverkeksConfig.CfgServiceSceneKeys : Plugin.CfgServiceSceneKeys)?.Value ?? "").Split(';');
		int count = 0;
		for (int i = 0; i < entries.Length; i++)
		{
			string entry = entries[i].Trim();
			if (entry.Length != 0)
			{
				entries[count++] = entry.ToLowerInvariant();
			}
		}
		Array.Resize(ref entries, count);
		return entries;
	}

	private static string GetFirstClipName(Animator animator)
	{
		if (animator == null || animator.runtimeAnimatorController == null)
		{
			return null;
		}
		AnimationClip[] animationClips = animator.runtimeAnimatorController.animationClips;
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

	private static string BuildKeyholeLabel(CameraSwapTrigger trigger, string galleryId, string rawName)
	{
		if (!string.IsNullOrEmpty(rawName))
		{
			return rawName;
		}
		if (!string.IsNullOrEmpty(galleryId))
		{
			return galleryId;
		}
		return (trigger != null) ? trigger.name : "unknown";
	}

	private static string BuildServiceLabel(CameraSwapTrigger trigger, string galleryId, string rawName)
	{
		if (!string.IsNullOrEmpty(rawName))
		{
			return rawName;
		}
		if (!string.IsNullOrEmpty(galleryId))
		{
			return galleryId;
		}
		return (trigger != null) ? trigger.name : "unknown";
	}

	private static string BuildReleaseKey(string kind, string name, Transform transform)
	{
		Vector3 position = ((transform != null) ? transform.position : Vector3.zero);
		string path = BuildTransformPath(transform);
		string[] parts = new string[13];
		Scene activeScene = SceneManager.GetActiveScene();
		parts[0] = activeScene.name;
		parts[1] = "|";
		parts[2] = kind;
		parts[3] = "|";
		parts[4] = name;
		parts[5] = "|";
		parts[6] = path;
		parts[7] = "|";
		parts[8] = Mathf.RoundToInt(position.x * 10f).ToString();
		parts[9] = ",";
		parts[10] = Mathf.RoundToInt(position.y * 10f).ToString();
		parts[11] = ",";
		parts[12] = Mathf.RoundToInt(position.z * 10f).ToString();
		return string.Concat(parts);
	}

	private static string BuildSceneKey(string kind, string name, Transform transform)
	{
		Vector3 position = ((transform != null) ? transform.position : Vector3.zero);
		string[] parts = new string[11];
		Scene activeScene = SceneManager.GetActiveScene();
		parts[0] = activeScene.name;
		parts[1] = "|";
		parts[2] = kind;
		parts[3] = "|";
		parts[4] = name;
		parts[5] = "|";
		parts[6] = Mathf.RoundToInt(position.x * 10f).ToString();
		parts[7] = ",";
		parts[8] = Mathf.RoundToInt(position.y * 10f).ToString();
		parts[9] = ",";
		parts[10] = Mathf.RoundToInt(position.z * 10f).ToString();
		return string.Concat(parts);
	}

	private static string BuildTransformPath(Transform transform)
	{
		if (transform == null)
		{
			return "";
		}
		string path = transform.name ?? "";
		Transform parent = transform.parent;
		while (parent != null)
		{
			path = parent.name + "/" + path;
			parent = parent.parent;
		}
		return path;
	}

	// Is there a run to show locks for?
	//
	// The readout used to gate on Enabled alone - the config setting - so ResetForScene rebuilt
	// the canvas on *every* scene change, menus included, and "Horny X/Y" sat over the main menu
	// with the diorama gauge under it. This is a display gate and nothing else: the locks keep
	// their values across the hide, because nothing here touches them.
	//
	// The test is the player, not the scene. Both candidates written down for this were lookups
	// by name (the scene name against a list) or by type (a MainMenuManager in the scene), and
	// both need a list that a new level or a renamed menu falls off silently. Every level has a
	// PlayerStats and no menu has one, so asking for the player answers the question the display
	// is actually asking, and stays right for a scene nobody has seen yet.
	//
	// The lookup is the same one Tick already does every frame while _player is null, so this
	// costs nothing new; the field is cleared per scene by ResetForScene.
	private static bool InGameplayScene()
	{
		if (_player != null)
		{
			return true;
		}
		_player = Object.FindAnyObjectByType<PlayerStats>();
		return _player != null;
	}

	private static void UpdateDisplay()
	{
		if (!Enabled || !InGameplayScene())
		{
			DestroyDisplay();
			return;
		}
		EnsureDisplay();
		if (_text != null)
		{
			string status =((!string.IsNullOrEmpty(_activeReleaseStatus)) ? _activeReleaseStatus : ((Time.realtimeSinceStartup <= _releaseStatusUntil) ? _lastReleaseStatus : null));
			_text.text = (string.IsNullOrEmpty(status) ? ("Horny " + _locks + "/" + GetTotalLocks()) : ("Horny " + _locks + "/" + GetTotalLocks() + "\n" + status));
		}
	}

	// Fill bar for the diorama look timer, sitting directly under the "Horny X/Y" readout on
	// the same top-right canvas. Width-driven rather than Image.fillAmount so it needs no
	// sliced sprite - a 1x1 white texture tinted per element is enough.
	internal static void SetAmbientGauge(float progress)
	{
		// Same gate as the readout it sits under: the gauge calls EnsureDisplay, so without it a
		// gaze progressing as the scene changes would rebuild the canvas the menu just lost.
		if (!Enabled || !InGameplayScene() || progress <= 0f)
		{
			if (_gaugeBack != null)
			{
				_gaugeBack.gameObject.SetActive(false);
			}
			return;
		}
		EnsureDisplay();
		EnsureGauge();
		if (!(_gaugeBack == null))
		{
			_gaugeBack.gameObject.SetActive(true);
			((Graphic)_gaugeFill).rectTransform.sizeDelta = new Vector2(260f * Mathf.Clamp01(progress), 0f);
		}
	}

	private static Sprite GaugeSprite()
	{
		if (_gaugeSprite == null)
		{
			Texture2D texture2D = new Texture2D(1, 1);
			texture2D.SetPixel(0, 0, Color.white);
			texture2D.Apply();
			_gaugeSprite = Sprite.Create(texture2D, new Rect(0f, 0f, 1f, 1f), new Vector2(0.5f, 0.5f));
		}
		return _gaugeSprite;
	}

	private static void EnsureGauge()
	{
		if (_gaugeBack != null || _canvas == null)
		{
			return;
		}
		GameObject gameObject = new GameObject("HeatLockGaugeBack");
		gameObject.transform.SetParent(_canvas.transform, false);
		_gaugeBack = gameObject.AddComponent<Image>();
		_gaugeBack.sprite = GaugeSprite();
		((Graphic)_gaugeBack).color = new Color(0f, 0f, 0f, 0.55f);
		((Graphic)_gaugeBack).raycastTarget = false;
		RectTransform rectTransform = ((Graphic)_gaugeBack).rectTransform;
		rectTransform.anchorMin = new Vector2(1f, 1f);
		rectTransform.anchorMax = new Vector2(1f, 1f);
		rectTransform.pivot = new Vector2(1f, 1f);
		rectTransform.anchoredPosition = new Vector2(-24f, -104f);
		rectTransform.sizeDelta = new Vector2(260f, 14f);
		GameObject fillObject = new GameObject("HeatLockGaugeFill");
		fillObject.transform.SetParent(gameObject.transform, false);
		_gaugeFill = fillObject.AddComponent<Image>();
		_gaugeFill.sprite = GaugeSprite();
		((Graphic)_gaugeFill).color = new Color(1f, 0.88f, 0.38f, 0.95f);
		((Graphic)_gaugeFill).raycastTarget = false;
		RectTransform rectTransform2 = ((Graphic)_gaugeFill).rectTransform;
		rectTransform2.anchorMin = new Vector2(0f, 0f);
		rectTransform2.anchorMax = new Vector2(0f, 1f);
		rectTransform2.pivot = new Vector2(0f, 0.5f);
		rectTransform2.anchoredPosition = Vector2.zero;
		rectTransform2.sizeDelta = new Vector2(0f, 0f);
	}

	private static void EnsureDisplay()
	{
		if (!(_text != null))
		{
			GameObject gameObject = new GameObject("PncEdiHeatLockCanvas");
			Object.DontDestroyOnLoad((Object)(object)gameObject);
			_canvas = gameObject.AddComponent<Canvas>();
			_canvas.renderMode = (RenderMode)0;
			_canvas.sortingOrder = 10000;
			gameObject.AddComponent<CanvasScaler>();
			gameObject.AddComponent<GraphicRaycaster>();
			GameObject textObject = new GameObject("HeatLockText");
			textObject.transform.SetParent(gameObject.transform, false);
			_text = textObject.AddComponent<Text>();
			_text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
			_text.fontSize = 28;
			_text.fontStyle = (FontStyle)1;
			_text.alignment = (TextAnchor)2;
			((Graphic)_text).color = new Color(1f, 0.88f, 0.38f, 1f);
			((Graphic)_text).raycastTarget = false;
			RectTransform rectTransform = ((Graphic)_text).rectTransform;
			rectTransform.anchorMin = new Vector2(1f, 1f);
			rectTransform.anchorMax = new Vector2(1f, 1f);
			rectTransform.pivot = new Vector2(1f, 1f);
			rectTransform.anchoredPosition = new Vector2(-24f, -24f);
			rectTransform.sizeDelta = new Vector2(560f, 130f);
		}
	}

	private static void UpdateReleaseMarkers()
	{
		if (Time.time >= _nextMarkerScanTime)
		{
			_nextMarkerScanTime = Time.time + 1f;
			RefreshReleaseMarkers();
		}
		Camera main = Camera.main;
		foreach (TextMesh value in ReleaseMarkers.Values)
		{
			if (value != null && main != null)
			{
				value.transform.rotation = Quaternion.LookRotation(value.transform.position - main.transform.position);
			}
		}
	}

	private static void RefreshReleaseMarkers()
	{
		MarkerSources.Clear();
		AmbientProximity.AppendReleaseMarkerSources(MarkerSources);
		CameraSwapTrigger[] cameraSwapTriggers = Object.FindObjectsByType<CameraSwapTrigger>((FindObjectsSortMode)0);
		foreach (CameraSwapTrigger cameraSwapTrigger in cameraSwapTriggers)
		{
			if (!(cameraSwapTrigger == null))
			{
				Animator animator = Traverse.Create((object)cameraSwapTrigger).Field("animatorToTrigger").GetValue<Animator>();
				string galleryId = Traverse.Create((object)cameraSwapTrigger).Field("galleryEntryID").GetValue<string>();
				string rawName = NameRemap.StripCloneSuffix(cameraSwapTrigger.gameObject.name ?? "");
				string keyholeLabel = BuildKeyholeLabel(cameraSwapTrigger, galleryId, rawName);
				if (IsKeyhole(cameraSwapTrigger, galleryId, rawName, animator) && ShouldShowReleaseMarker("keyhole", cameraSwapTrigger, keyholeLabel))
				{
					MarkerSources.Add(new ReleaseMarkerSource
					{
						Kind = "keyhole",
						Label = keyholeLabel,
						Transform = cameraSwapTrigger.transform
					});
				}
				string serviceLabel = BuildServiceLabel(cameraSwapTrigger, galleryId, rawName);
				if (IsService(cameraSwapTrigger, galleryId, rawName, animator) && ShouldShowReleaseMarker("service", cameraSwapTrigger, serviceLabel))
				{
					MarkerSources.Add(new ReleaseMarkerSource
					{
						Kind = "service",
						Label = serviceLabel,
						Transform = cameraSwapTrigger.transform
					});
				}
			}
		}
		HashSet<string> liveKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		List<string> keysToDrop = new List<string>();
		for (int j = 0; j < MarkerSources.Count; j++)
		{
			ReleaseMarkerSource releaseMarkerSource = MarkerSources[j];
			if (!(releaseMarkerSource.Transform == null))
			{
				string key = BuildReleaseKey(releaseMarkerSource.Kind, releaseMarkerSource.Label, releaseMarkerSource.Transform);
				if (releaseMarkerSource.Kind.Equals("ambient", StringComparison.OrdinalIgnoreCase))
				{
					keysToDrop.Add(key);
					continue;
				}
				liveKeys.Add(key);
				TextMesh orCreateMarker = GetOrCreateMarker(key, releaseMarkerSource.Transform);
				bool used = UsedReleaseSources.Contains(key);
				orCreateMarker.text = (used ? "Horny release used" : "Horny release ready");
				orCreateMarker.color = (used ? new Color(0.55f, 0.55f, 0.55f, 0.85f) : new Color(1f, 0.83f, 0.25f, 1f));
			}
		}
		foreach (string key in ReleaseMarkers.Keys)
		{
			if (!liveKeys.Contains(key))
			{
				keysToDrop.Add(key);
			}
		}
		for (int k = 0; k < keysToDrop.Count; k++)
		{
			if (ReleaseMarkers.TryGetValue(keysToDrop[k], out var marker))
			{
				if (marker != null)
				{
					Object.Destroy((Object)(object)marker.gameObject);
				}
				ReleaseMarkers.Remove(keysToDrop[k]);
			}
		}
	}

	private static TextMesh GetOrCreateMarker(string key, Transform parent)
	{
		if (ReleaseMarkers.TryGetValue(key, out var value) && value != null)
		{
			return value;
		}
		GameObject gameObject = new GameObject("PncEdiReleaseMarker");
		gameObject.transform.SetParent(parent, false);
		gameObject.transform.localPosition = Vector3.up * 1.25f;
		value = gameObject.AddComponent<TextMesh>();
		value.anchor = (TextAnchor)4;
		value.alignment = (TextAlignment)1;
		value.fontSize = 42;
		value.characterSize = 0.035f;
		ReleaseMarkers[key] = value;
		return value;
	}

	private static bool ShouldShowReleaseMarker(string kind, CameraSwapTrigger trigger, string label)
	{
		if (trigger == null)
		{
			return false;
		}
		GameObject gameObject = GameObject.FindGameObjectWithTag("Player");
		if (gameObject == null)
		{
			return false;
		}
		float range = Mathf.Max(3f, trigger.InteractionRange);
		if (Vector3.Distance(trigger.transform.position, gameObject.transform.position) > range)
		{
			return false;
		}
		return IsReleaseSourceUsed(kind, label, trigger.transform) || trigger.CanInteract;
	}

	private static void DestroyDisplay()
	{
		if (_canvas != null)
		{
			Object.Destroy((Object)(object)_canvas.gameObject);
		}
		_canvas = null;
		_text = null;
	}

	private static void DestroyReleaseMarkers()
	{
		foreach (TextMesh value in ReleaseMarkers.Values)
		{
			if (value != null)
			{
				Object.Destroy((Object)(object)value.gameObject);
			}
		}
		ReleaseMarkers.Clear();
		MarkerSources.Clear();
	}

	[HarmonyPatch(typeof(PlayerStats), "GenerateHeat")]
	[HarmonyPostfix]
	public static void GenerateHeat_Postfix(PlayerStats __instance)
	{
		AfterHeatChanged(__instance);
	}

	[HarmonyPatch(typeof(PlayerStats), "CoolHeat")]
	[HarmonyPrefix]
	public static bool CoolHeat_Prefix(PlayerStats __instance, ref float amount)
	{
		// A heat potion pays out in horny locks now, so its flat cooling is dropped.
		if (HeatPotionLocks.SuppressCooling)
		{
			return false;
		}
		if (IsRecoveringFromOverheatSlowness(__instance))
		{
			return true;
		}
		if (_adjustingHeat || amount <= 0f || !TryGetLockedHeatRange(__instance, out var minHeat, out var maxAllowed))
		{
			return true;
		}
		float currentHeat = __instance.CurrentHeat;
		float clamped = Mathf.Clamp(currentHeat, minHeat, maxAllowed);
		if (Mathf.Abs(clamped - currentHeat) >= 0.001f)
		{
			_adjustingHeat = true;
			try
			{
				__instance.SetHeat(clamped);
			}
			finally
			{
				_adjustingHeat = false;
			}
			return false;
		}
		float coolable = Mathf.Max(0f, currentHeat - minHeat);
		if (amount > coolable)
		{
			amount = coolable;
		}
		return amount > 0.001f;
	}

	[HarmonyPatch(typeof(PlayerStats), "CoolHeat")]
	[HarmonyPostfix]
	public static void CoolHeat_Postfix(PlayerStats __instance)
	{
		EnsureHeatBounds(__instance);
	}

	[HarmonyPatch(typeof(PlayerStats), "SetHeat")]
	[HarmonyPostfix]
	public static void SetHeat_Postfix(PlayerStats __instance)
	{
		AfterHeatChanged(__instance);
	}

	[HarmonyPatch(typeof(PlayerStats), "SetMaxHealth")]
	[HarmonyPostfix]
	public static void SetMaxHealth_Postfix(PlayerStats __instance)
	{
		if (!_adjustingHealth)
		{
			RecordBaseHealth(__instance);
		}
	}

	[HarmonyPatch(typeof(PlayerStats), "SetMaxHeat")]
	[HarmonyPostfix]
	public static void SetMaxHeat_Postfix(PlayerStats __instance)
	{
		RecordBaseHeat(__instance);
		EnsureHeatBounds(__instance);
		UpdateDisplay();
	}
}
