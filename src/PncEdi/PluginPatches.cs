using System;
using BepInEx;
using HarmonyLib;

namespace PncEdi;

/// <summary>
/// The Harmony registration list: every patch class the mod applies, in the order it applies them.
///
/// **This list is the mod.** Patches are registered per class rather than by an assembly-wide
/// <c>PatchAll()</c>, so a new patch class does nothing at all until it is named here - the
/// attributes are right, the class compiles, and nothing logs. CHANGELOG §109's fix was written
/// correctly, built, deployed, and had no effect for a whole run because of exactly this. It is
/// its own file so the list can be read as a list.
///
/// <c>patchaudit.py --registry</c> checks it: every class in the tree carrying a
/// <c>[HarmonyPatch]</c> member must appear below, and every name below must exist.
/// </summary>
public partial class Plugin
{
	/// <summary>Register one patch class. A failure here is a bug and should take the plugin down.</summary>
	private static void Patch(string harmonyId, Type patchClass)
	{
		new Harmony(harmonyId).PatchAll(patchClass);
	}

	/// <summary>
	/// Register one patch class whose target may legitimately be absent.
	///
	/// These four patch game internals that have moved or vanished between builds; the mod is
	/// still worth running without them, so the failure is a warning rather than a throw. Do not
	/// reach for this to quieten a patch that ought to bind - a patch that silently declines is
	/// the failure mode this project keeps paying for.
	/// </summary>
	private static void TryPatch(string harmonyId, Type patchClass)
	{
		try
		{
			new Harmony(harmonyId).PatchAll(patchClass);
		}
		catch (Exception ex)
		{
			Log.LogWarning($"{patchClass.Name} patch failed: {ex.Message}");
		}
	}

	private void ApplyPatches()
	{
		Patch("com.edi.pnc", typeof(GrabHooks));
        Patch("com.edi.pnc.captureaudio",typeof(CaptureAudioPause));
        Patch("com.edi.pnc.attacktrace",typeof(BaphometAttackTraceHooks));
        Patch("com.edi.pnc.retreatcontract",typeof(ProjectileRetreatFallback));
        Patch("com.edi.pnc.capturefootsteps", typeof(CaptureFootstepHooks));
		Patch("com.edi.pnc.grapple", typeof(GrappleHooks));
		Patch("com.edi.pnc.death", typeof(DeathHooks));
		Patch("com.edi.pnc.cam", typeof(CameraSwapHooks));
		Patch("com.edi.pnc.gallery", typeof(GalleryHooks));
		Patch("com.edi.pnc.galleryunlock", typeof(GalleryUnlockHooks));
		Patch("com.edi.pnc.classheat", typeof(ClassSelectionHooks));


		// The gameplay patches install unconditionally, and each one gates on
		// Plugin.GameplayTweaksEnabled from inside instead.
		//
		// They used to sit behind `if (GameplayTweaksEnabled)` here, which was correct while that
		// answer could only change by editing the config and restarting. Gameplay profiles made it
		// changeable mid-run, and a patch that is not installed cannot start working when the
		// profile asks for it: a session that began on Vanilla would switch to Pressure and Release
		// and get nothing, silently. Installing them always and gating the bodies is the shape that
		// makes a live switch mean something, and it is the same reason no patch is ever *removed*
		// on a switch either - Harmony unpatching a running game is how you get half-patched state.
		Patch("com.edi.pnc.gameplay", typeof(GameplayHooks));
		Patch("com.edi.pnc.overkeks", typeof(OverkeksProfileHooks));
		Patch("com.edi.pnc.overkeks", typeof(GooperTuning));
		Patch("com.edi.pnc.overkeks", typeof(PitfallRecoveryHooks));
		Patch("com.edi.pnc.overkeks", typeof(EnemyReactivationOnDamageHook));
		Patch("com.edi.pnc.overkeks", typeof(GrabViewHelper));
		Patch("com.edi.pnc.overkeks", typeof(OverkeksPopulation));
		Patch("com.edi.pnc.overkeks", typeof(OverkeksHeatSceneGate));
		Patch("com.edi.pnc.overkeks", typeof(OverkeksEnemyCombatTuning));
		Patch("com.edi.pnc.overkeks", typeof(OverkeksNunGrabTuning));
		Patch("com.edi.pnc.overkeks", typeof(OverkeksEnemySpawnShuffle));
		Patch("com.edi.pnc.overkeks", typeof(ChaserDamageHooks));
		Patch("com.edi.pnc.overkeks", typeof(ChaserSweeperHooks));
		Patch("com.edi.pnc.overkeks", typeof(ChaserFodderSpawn));
		Patch("com.edi.pnc.overkeks", typeof(BlindedBeastWeakSpawn));
		Patch("com.edi.pnc.overkeks", typeof(BaphometGargoyleSpawn));
		Patch("com.edi.pnc.overkeks", typeof(GravySerpentSpawn));
		Patch("com.edi.pnc.community.combat", typeof(CommunityCombatRuntime));
		Patch("com.edi.pnc.community.resources", typeof(OverkeksFeatureRuntime));
		Patch("com.edi.pnc.community.enemies", typeof(OverkeksEnemyRuntime));
		Patch("com.edi.pnc.community.resources", typeof(WeaponHeatCostGate));
		Patch("com.edi.pnc.community.resources", typeof(PotionAutoUse));
		Patch("com.edi.pnc.community.resources", typeof(KillHeatCool));

		Patch("com.edi.pnc.gameplay", typeof(NunGrabTuning));
		Patch("com.edi.pnc.gameplay", typeof(EnemyCombatTuning));
		Patch("com.edi.pnc.gameplay", typeof(EnemySpawnShuffle));
		Patch("com.edi.pnc.gameplay", typeof(ImpGrappleGate));
		Patch("com.edi.pnc.mimicgate", typeof(MimicGrabGate));
		Patch("com.edi.pnc.aiguard", typeof(AiStateGuard));
		Patch("com.edi.pnc.aiguard", typeof(GrabTransitionGate));
		Patch("com.edi.pnc.enemygrabgate", typeof(EnemyGrabGate));
		Patch("com.edi.pnc.grabsurvival", typeof(GrabSurvivalHooks));
		Patch("com.edi.pnc.grabstruggle", typeof(GrabStruggleHooks));
		Patch("com.edi.pnc.dragongrab", typeof(DragonGrabHooks));
		Patch("com.edi.pnc.grabend", typeof(GrabEndHooks));
        Patch("com.edi.pnc.enemyaudiopause", typeof(EnemyGameplayAudioPause));
        Patch("com.edi.pnc.idlelooppauseguard", typeof(EnemyIdleLoopPauseGuard));
        Patch("com.edi.pnc.chasercapturediagnostics", typeof(ChaserCaptureDiagnostics));
        Patch("com.edi.pnc.chaserteleport", typeof(ChaserCaptureTeleportGuard));
        Patch("com.edi.pnc.grabrecoverydeadline", typeof(OverkeksGrabRecovery));
        Patch("com.edi.pnc.cadencefloor", typeof(AttackCadenceFloor));
		Patch("com.edi.pnc.heatlocks", typeof(HeatLockSystem));
		Patch("com.edi.pnc.heatparam", typeof(GrabScreenHeatParam));
		Patch("com.edi.pnc.grabaudio", typeof(GrabScreenAudioFill));
		Patch("com.edi.pnc.heatpotions", typeof(HeatPotionLocks));
		Patch("com.edi.pnc.cumdamage", typeof(CumDamageGate));
		Patch("com.edi.pnc.grappledeath", typeof(GrappleDeathSequence));
		Patch("com.edi.pnc.gameplay", typeof(EnemyReactivationOnAttackStartHook));
		Patch("com.edi.pnc.gameplay", typeof(EnemyReactivationOnAttackHitHook));
		Patch("com.edi.pnc.gameplay", typeof(EnemyReactivationPerformAttackHook));
		Patch("com.edi.pnc.gameplay", typeof(EnemyReactivationPerformSpecialAttackHook));
		// Capability diagnostics must also run under Vanilla: users can enable gameplay
		// profiles later in this session, and a quiet startup must not imply full coverage.
		EnemyAiAudit.Run();
		if (GameplayTweaksEnabled)
		{
			// Startup diagnostics rather than behaviour, so these stay behind the gate: they are
			// one-shot dumps and there is nothing to switch on later.
			Hotkeys.AuditBindings();
			// 0.3.1 turned legacy UnityEngine.Input into a throw (see SafeInput), so which backend
			// BepInEx resolved is the difference between every hotkey working and none of them.
			// Name it once at startup rather than inferring it from keys that do nothing. Reading
			// it here forces BepInEx's one-shot probe early, where it resolves NullInputSystem —
			// SafeInput.EnsureBackend repairs that on the first frame the keyboard exists, and
			// logs again when it does.
			DBG("INPUT", "backend at startup " + SafeInput.BackendName);
		}
		TryPatch("com.edi.pnc.pathspam", typeof(PathfindingSpamFix));
		TryPatch("com.edi.pnc.idiag", typeof(InteractDiag));
		TryPatch("com.edi.pnc.freecam", typeof(FreeCamHooks));
		TryPatch("com.edi.pnc.pause", typeof(PauseHooks));
		Log.LogInfo($"[{PluginName}] Harmony patches applied.");
	}
}
