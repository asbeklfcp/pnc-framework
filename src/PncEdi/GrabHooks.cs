using System;
using System.Text;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace PncEdi;

[HarmonyPatch]
public static class GrabHooks
{
	public static Animator CurrentAnimator;
	public static string CurrentEnemyKey;
	public static GrabAnimationData[] CurrentAnims;
	public static string CurrentRawPrefab;
	private static FieldInfo _animatorField;
	private static string _lastNotReadyReason;
	private static FieldInfo _grabAnimsField;

	internal static void ClearLiveGrabState()
	{
		CurrentAnimator = null;
		CurrentEnemyKey = null;
		CurrentAnims = null;
		CurrentRawPrefab = null;
	}

	// Names the controller now on the grab-screen animator and the state it is actually in, one
	// frame into the grab. For a mimic the intro is MimicGrabInit and the loop MimicGrabLoop, so
	// this distinguishes "the animator rebound and started at the intro" from "it carried on in
	// the loop" without needing to guess which one happened. See the call site in StartGrab.
	// Which AudioSource is supposed to carry this scene's sound, and on what object.
	//
	// The 2026-08-24 run threw `Can not play a disabled audio source` seven times, and every one
	// of them lands within a millisecond of a scene hide - all five Blinded Beast grabs, and one
	// each for the dragon and the wendigo, with the goonshroom, nun, imp and mimic grabs silent
	// on it. That is not load, which is what it looked like from the chair: it is exactly the
	// families whose enemy GameObject is switched off for the duration of its own scene. The
	// Blinded Beast is the one prefab in the game that sets hideInsteadOfDestroyOnGrab, so
	// VANILLA hides it and the mod's keep-alive stands down (§73); the chaser bosses are hidden
	// by the mod's own path. Either way, a Play() on a source under a deactivated object is a
	// warning and a silence.
	//
	// What this cannot say from the C# is which source the game meant to play - the clip is
	// assigned at runtime, so it is null in the assets and null in a census taken too early.
	// Hence a census at grab start, before anything hides: object, clip, and whether it is live.
	// One run with this in turns "sometimes there is no audio" into a named source.
	private static void LogGrabAudioSources(GameObject enemy)
	{
		if (enemy == null)
		{
			return;
		}
		AudioSource[] sources = enemy.GetComponentsInChildren<AudioSource>(true);
		if (sources == null || sources.Length == 0)
		{
			Plugin.DBG("GRAB-AUDIO", NameRemap.StripCloneSuffix(enemy.name) + ": no AudioSource on the enemy - its scene audio comes from somewhere else");
			return;
		}
		StringBuilder census = new StringBuilder();
		census.Append(NameRemap.StripCloneSuffix(enemy.name)).Append(": ").Append(sources.Length).Append(" source(s)");
		for (int i = 0; i < sources.Length; i++)
		{
			AudioSource source = sources[i];
			if (source == null)
			{
				continue;
			}
			census.Append(" | obj='").Append(source.gameObject.name).Append("'")
				.Append(" clip='").Append((source.clip != null) ? source.clip.name : "<null>").Append("'")
				.Append(" activeInHierarchy=").Append(source.gameObject.activeInHierarchy)
				.Append(" enabled=").Append(source.enabled)
				.Append(" playing=").Append(source.isPlaying);
		}
		Plugin.DBG("GRAB-AUDIO", census.ToString());
	}

	private static void LogGrabAnimatorState()
	{
		Animator a = CurrentAnimator;
		if (a == null)
		{
			Plugin.DBG("GRAB-ANIM", "no grab-screen animator");
			return;
		}
		RuntimeAnimatorController rac = a.runtimeAnimatorController;
		string ctrl = (rac != null) ? rac.name : "<null>";
		AnimatorStateInfo si = a.GetCurrentAnimatorStateInfo(0);
		string state = "<unknown>";
		AnimatorClipInfo[] ci = a.GetCurrentAnimatorClipInfo(0);
		if (ci != null && ci.Length > 0 && ci[0].clip != null)
		{
			state = ci[0].clip.name;
		}
		Plugin.DBG("GRAB-ANIM", "controller='" + ctrl + "' clip='" + state + "' hash=" + si.shortNameHash + " t=" + si.normalizedTime.ToString("0.00"));
	}

	private static Animator GetGrabAnimator(GrabScreen gs)
	{
		if (gs == null)
		{
			return null;
		}
		if (_animatorField == null)
		{
			_animatorField = typeof(GrabScreen).GetField("grabScreenAnimator", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		}
		object? obj = _animatorField?.GetValue(gs);
		return (Animator)((obj is Animator) ? obj : null);
	}

	[HarmonyPatch(typeof(GrabScreen), "StartGrab")]
	[HarmonyPostfix]
	public static void StartGrab_Postfix(GrabScreen __instance, GameObject enemy)
	{
		// The grapple gate blocks this grab via a prefix returning false, but Harmony
		// still runs postfixes - so without this the blocked grab would yank EDI off the
		// cling script onto the enemy's grab script every retry.
		if (ImpGrappleGate.ShouldBlockNonGrappleSceneFor(enemy))
		{
			return;
		}
		// The GAME refuses grabs too, and the same postfix problem applies. StartGrab opens with
		//     if (isGrabbed) return;
		//     if (playerStats == null || !playerStats.CanBeGrabbed) return;
		//     isGrabbed = true;                       // everything real happens after this
		// so a grab declined during post-grapple immunity does none of its setup - no
		// SwapAnimationController, no UI - yet this postfix still fired and dispatched the
		// enemy's script. Observed 2026-08-17 as two StartGrabs 0.6 s apart for one grab, the
		// first logging `[GRAB-ANIM] controller='<null>'` and no heat lock, the second the real
		// one. IsGrabbed is the game's own answer to "did this grab actually start".
		if (!__instance.IsGrabbed)
		{
			// Every enemy in grab range asks on its own attack cadence, so this is one fact repeated
			// 609 times in a twenty-minute run - collapsed rather than printed (§138).
			Plugin.DBGRepeat("GRAB-START", "grab-declined", "ignored: game declined the grab (CanBeGrabbed false / already grabbed)");
			return;
		}
		// A real grab: flush whatever count of refusals came before it, so the two read together.
		Plugin.DBGRepeatEnd("GRAB-START", "grab-declined");
		// A package driving the grab screen itself is not an enemy grab, and every step below is
		// wrong for one. The 2026-08-26 run is the worked example: a wall trap's capture resolved
		// to `joker_blackserpent_grabscreen` - the key off the trap and the *state off whichever
		// enemy was grabbed last*, because nobody swaps the grab-screen controller for a trap - and
		// it was harmless only because that name matches no row. The audio fill then wrote four
		// goonshroom clips over the trap's own captureSound. The scene's rows are the package's to
		// dispatch, per stage, and it does; the mod stands down and holds no live grab state, so
		// the per-frame STEP poll in Plugin.Update cannot dispatch off a stale animator either.
		//
		// Heat locks are deliberately NOT part of standing down: a capture is an erotic scene and
		// holds heat like one, which is also what keeps the filler off the channel for its length.
		if (CustomEnemyBridge.OwnsGrabScene(enemy))
		{
			ClearLiveGrabState();
			Plugin.DBG("GRAB-START", "package scene on '" + NameRemap.StripCloneSuffix(enemy.name)
				+ "' - it owns the screen, its rows and its audio; the mod's grab pipeline stands down");
			return;
		}
		try
		{
			if (!(__instance == null) && !(enemy == null))
			{
				CurrentAnimator = GetGrabAnimator(__instance);
				CurrentRawPrefab = NameRemap.StripCloneSuffix(enemy.name);
				CurrentEnemyKey = NameRemap.ResolveEnemyKey(enemy.name);
				CurrentAnims = GetGrabScreenAnims(__instance);
				if (CurrentAnims == null || CurrentAnims.Length == 0)
				{
					CurrentAnims = ResolveGrabAnims(enemy);
				}
				string currentRawPrefab = CurrentRawPrefab;
				string currentEnemyKey = CurrentEnemyKey;
				GrabAnimationData[] currentAnims = CurrentAnims;
				Plugin.DBG("GRAB-START", $"prefab='{currentRawPrefab}' key={currentEnemyKey} anims={((currentAnims != null) ? currentAnims.Length : 0)}");
				// Bug #13: a mimic re-interacted with sometimes skips its intro and starts in the
				// loop. Vanilla mimics Destroy themselves after one grab, so a second interaction
				// is territory the game never had to handle; KeepEnemiesAfterGrab created it.
				// GrabScreen.PlayGrabAnimation is empty, so which state plays is decided purely by
				// whether assigning runtimeAnimatorController rebinds the state machine - if the
				// controller is already the one being assigned, Unity can skip the rebind and the
				// animator simply carries on from wherever it was left. Log the controller and the
				// state actually entered, so one run settles it instead of another guess.
				LogGrabAnimatorState();
				LogGrabAudioSources(enemy);
				FireCurrentGrabStep();
			}
		}
		catch (Exception ex)
		{
			Plugin.Log?.LogWarning($"StartGrab patch error: {ex}");
		}
	}

	[HarmonyPatch(typeof(GrabScreen), "EndGrab")]
	[HarmonyPostfix]
	public static void EndGrab_Postfix()
	{
		try
		{
			Plugin.DBG("GRAB-END", "prefab='" + CurrentRawPrefab + "' key=" + CurrentEnemyKey);
			NearbyEnemyHider.RestoreAll();
			ClearLiveGrabState();
		}
		catch (Exception ex)
		{
			Plugin.Log?.LogWarning($"EndGrab patch error: {ex}");
		}
	}

	private static GrabAnimationData[] ResolveGrabAnims(GameObject enemy)
	{
		// Every AI class declares its own public grabScreenAnimations, so this reads the field
		// off whichever one the prefab carries rather than naming the classes one at a time -
		// BrawlerEnemyAI (the Black Serpent) was missing from the hand-written chain (§76).
		foreach (MonoBehaviour ai in EnemyAiTypes.On(enemy))
		{
			GrabAnimationData[] anims = Traverse.Create((object)ai).Field("grabScreenAnimations").GetValue<GrabAnimationData[]>();
			if (anims != null)
			{
				return anims;
			}
		}
		MimicEnemy mimic = enemy.GetComponent<MimicEnemy>();
		if (mimic != null && mimic.grabScreenAnimations != null)
		{
			return mimic.grabScreenAnimations;
		}
		return Array.Empty<GrabAnimationData>();
	}

	private static GrabAnimationData[] GetGrabScreenAnims(GrabScreen gs)
	{
		if (gs == null)
		{
			return null;
		}
		if (_grabAnimsField == null)
		{
			_grabAnimsField = typeof(GrabScreen).GetField("currentGrabAnimations", BindingFlags.Instance | BindingFlags.NonPublic);
		}
		return _grabAnimsField?.GetValue(gs) as GrabAnimationData[];
	}

	public static string BuildGalleryName(string enemyKey, string stepName)
	{
		return ApplyControllerVariant(NameRemap.BuildGallerySlug(enemyKey, stepName));
	}

	// Two grab screens for one enemy, told apart by the controller that is driving them.
	//
	// The slug is enemy key + animator state, and game 0.3.1's Blinded Beast breaks that: it is a
	// two-stage miniboss whose `BlindedBeastGrabScreen` and `BlindedBeastTransformedGrabScreen`
	// each declare a state called exactly `Loop` and one called exactly `Cum`. Both stages
	// therefore slug to `blinded_beast_loop`, while their clips are different lengths (556/3667
	// against 750/3500 ms) - so one funscript cannot be right for both. The **controller** is the
	// only thing that differs, and it is already read and logged by `[GRAB-ANIM]` (§66).
	//
	// The gallery route needs none of this: `Gallery_BlindedBeast_Grabbed` names its states
	// `Start`/`Start_T`/`Cum`/`Cum_T`, so the viewer splits cleanly on its own. Same scene,
	// ambiguous from one route and not the other - the mirror of the §52 gallery split.
	//
	// Default-empty for every other enemy, so this is inert unless a controller matches.
	private static string ApplyControllerVariant(string slug)
	{
		string map = Plugin.CfgGrabVariantSuffixes?.Value ?? "";
		if (string.IsNullOrEmpty(slug) || string.IsNullOrEmpty(map))
		{
			return slug;
		}
		// Only while a grab is actually live. `BuildGalleryName` also serves the camera-swap
		// interact scenes, which have their own animator and would otherwise be resolved against
		// whatever controller the previous grab left in `CurrentAnimator` - §28's dead gate in
		// miniature: a value that is always read but only sometimes current.
		GrabScreen gs = GrabScreen.Instance;
		if (gs == null || !gs.IsGrabbed)
		{
			return slug;
		}
		Animator a = CurrentAnimator;
		if (a == null)
		{
			return slug;
		}
		RuntimeAnimatorController rac = a.runtimeAnimatorController;
		if (rac == null)
		{
			return slug;
		}
		string ctrl = rac.name ?? "";
		string[] entries = map.Split(';');
		string bestSuffix = null;
		int bestLen = -1;
		for (int i = 0; i < entries.Length; i++)
		{
			string entry = entries[i].Trim();
			int eq = entry.IndexOf('=');
			if (eq <= 0)
			{
				continue;
			}
			string key = entry.Substring(0, eq).Trim();
			// Longest key wins, so `BlindedBeastTransformedGrabScreen` cannot be shadowed by a
			// shorter `BlindedBeast` someone adds later.
			if (key.Length > bestLen
				&& ctrl.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0)
			{
				bestLen = key.Length;
				bestSuffix = entry.Substring(eq + 1).Trim();
			}
		}
		if (string.IsNullOrEmpty(bestSuffix) || slug.EndsWith(bestSuffix, StringComparison.OrdinalIgnoreCase))
		{
			return slug;
		}
		string variant = slug + bestSuffix;
		Plugin.DBG("GRAB-VARIANT", "controller '" + ctrl + "' -> " + variant);
		return variant;
	}

	public static string ResolveStateName(AnimatorStateInfo info)
	{
		if (CurrentAnims != null)
		{
			GrabAnimationData[] currentAnims = CurrentAnims;
			GrabAnimationData[] grabAnimationDatas = currentAnims;
			foreach (GrabAnimationData grabAnimationData in grabAnimationDatas)
			{
				if (grabAnimationData != null && !string.IsNullOrEmpty(grabAnimationData.animationName) && info.IsName(grabAnimationData.animationName))
				{
					return grabAnimationData.animationName;
				}
			}
		}
		return null;
	}

	internal static string ResolveClipName(Animator animator)
	{
		if (animator == null)
		{
			return null;
		}
		AnimatorClipInfo[] currentAnimatorClipInfo = animator.GetCurrentAnimatorClipInfo(0);
		if (currentAnimatorClipInfo != null && currentAnimatorClipInfo.Length != 0 && currentAnimatorClipInfo[0].clip != null)
		{
			return currentAnimatorClipInfo[0].clip.name;
		}
		return null;
	}

	internal static string ResolveGrabStepName(Animator animator, AnimatorStateInfo info)
	{
		string stepName = ResolveStateName(info);
		if (!string.IsNullOrEmpty(stepName))
		{
			return stepName;
		}
		stepName = ResolveClipName(animator);
		if (!string.IsNullOrEmpty(stepName) && CurrentAnims != null)
		{
			for (int i = 0; i < CurrentAnims.Length; i++)
			{
				GrabAnimationData grabAnimationData = CurrentAnims[i];
				if (grabAnimationData != null && !string.IsNullOrEmpty(grabAnimationData.animationName) && stepName.IndexOf(grabAnimationData.animationName, StringComparison.OrdinalIgnoreCase) >= 0)
				{
					return grabAnimationData.animationName;
				}
			}
		}
		return stepName;
	}

	internal static void RefreshDragonGrabPlayback()
	{
		RefreshChaserBossGrabPlayback();
	}

	internal static void RefreshChaserBossGrabPlayback()
	{
		if (CurrentEnemyKey != null && (string.Equals(CurrentEnemyKey, "dragon", StringComparison.OrdinalIgnoreCase) || string.Equals(CurrentEnemyKey, "wendigo", StringComparison.OrdinalIgnoreCase)) && (!(Plugin.Instance != null) || !Plugin.Instance.HasGrabStepPlayback()))
		{
			FireCurrentGrabStep();
		}
	}

	internal static bool IsGrabAnimatorReadyForEdi()
	{
		return DescribeGrabAnimatorReadiness() == null;
	}

	// Returns null when the grab is dispatchable, otherwise why it is not.
	//
	// This used to be a bare bool, and a refusal was completely silent - no log line, nothing.
	// That cost a full session to diagnose: a death grab produced `[GRAB-START]` and
	// `[GRAB-ANIM] controller='ZombieGrabScreen' clip='ZombieGrabScreen_Loop'`, then no
	// `[EDI] Play` at all, and the device ran damage filler over the whole scene. Everything in
	// the log said the grab was healthy, because the one gate that refused it said nothing.
	// Same principle as [ALIAS-GAP] and [AI-AUDIT]: a gap has to announce itself.
	internal static string DescribeGrabAnimatorReadiness()
	{
		if (CurrentAnimator == null)
		{
			return "no grab-screen animator";
		}
		if (CurrentAnims == null || CurrentAnims.Length == 0)
		{
			// An imp grab arrives here with no animations at all: its scene data lives in
			// GrappleScreenobject, not in the AI's grabScreenAnimations, so `[GRAB-START] ...
			// anims=0` and this gate refused every imp grab in the 2026-08-23 run - the device
			// stayed on imp_3 through a scene that had already moved on (§99).
			//
			// The array is not the only thing that can name the step. The very next log line
			// after each of those refusals was `[GRAB-ANIM] controller='Imp_Grab_Screen'
			// clip='Imp_Grab_Loop'`, and `Imp_Grab_Loop` slugs to `imp_grab_loop`, which is a
			// real gallery row - the animator knew all along. So the array being empty is only
			// fatal when the animator cannot name a clip either.
			if (!CanNameStepFromAnimator())
			{
				return "no grab animations resolved, and the animator names no clip";
			}
		}
		if (CurrentAnimator.isActiveAndEnabled)
		{
			return null;
		}
		if (DragonGrabHooks.IsChaserBossPresentationActive)
		{
			return null;
		}
		// The death grab: the player died, the game's OnPlayerDeath event tore its UI down, and
		// then an enemy grabbed the corpse. HeatLockSystem revived them to 1 HP precisely so the
		// scene could play out, so this IS a real scene - the animator being inactive is the
		// game's teardown, not a refused grab. Without this the scene is silently skipped and
		// Revive_Postfix then sees no gallery playing and forces filler over the top of it.
		if (HeatLockSystem.PendingSceneEntryGameOver)
		{
			return null;
		}
		GameObject go = CurrentAnimator.gameObject;
		return "grab-screen animator inactive (activeInHierarchy=" + go.activeInHierarchy
			+ " activeSelf=" + go.activeSelf + " enabled=" + CurrentAnimator.enabled
			+ " playerDead=" + Plugin.PlayerDead + ")";
	}

	// Whether ResolveGrabStepName would produce something without CurrentAnims to work from.
	// Wrapped because an animator mid-teardown can throw on GetCurrentAnimatorStateInfo, and a
	// throw here would propagate out of a Harmony patch into the game's own call stack.
	private static bool CanNameStepFromAnimator()
	{
		try
		{
			AnimatorStateInfo currentAnimatorStateInfo = CurrentAnimator.GetCurrentAnimatorStateInfo(0);
			return !string.IsNullOrEmpty(ResolveGrabStepName(CurrentAnimator, currentAnimatorStateInfo));
		}
		catch
		{
			return false;
		}
	}

	internal static void FireCurrentGrabStep()
	{
		string notReady = DescribeGrabAnimatorReadiness();
		if (notReady != null)
		{
			// Deduped: the chaser-boss refresh path retries until playback starts, so an
			// unchanging reason would otherwise repeat once per retry.
			if (notReady != _lastNotReadyReason)
			{
				_lastNotReadyReason = notReady;
				Plugin.DBG("GRAB-INIT", "skipped: " + notReady);
			}
			return;
		}
		_lastNotReadyReason = null;
		AnimatorStateInfo currentAnimatorStateInfo = CurrentAnimator.GetCurrentAnimatorStateInfo(0);
		string stepName = ResolveGrabStepName(CurrentAnimator, currentAnimatorStateInfo);
		// Null-guarded since the readiness gate above now lets an anims=0 grab through on the
		// strength of the animator's own clip name; before that, reaching here meant the grabAnimationDatas
		// existed.
		if (string.IsNullOrEmpty(stepName) && CurrentAnims != null)
		{
			GrabAnimationData[] currentAnims = CurrentAnims;
			GrabAnimationData[] grabAnimationDatas = currentAnims;
			foreach (GrabAnimationData grabAnimationData in grabAnimationDatas)
			{
				if (grabAnimationData != null && !string.IsNullOrEmpty(grabAnimationData.animationName))
				{
					stepName = grabAnimationData.animationName;
					break;
				}
			}
		}
		if (!string.IsNullOrEmpty(stepName))
		{
			string enemyKey = CurrentEnemyKey ?? "enemy";
			string galleryName = BuildGalleryName(enemyKey, stepName);
			Plugin.Instance?.ResetGrabStepHash();
			Plugin.DBG("GRAB-INIT", enemyKey + "/" + stepName + " -> " + galleryName
				+ ((CurrentAnims == null || CurrentAnims.Length == 0) ? " (from the animator's clip; no grab animations)" : ""));
			// Start the script where the animation already is. Eleven of the twelve grab-screen
			// controllers seen in a session begin at normalizedTime 0, so this changes nothing
			// for them; the Black Serpent's does not reset and is typically two-thirds through
			// a cycle by the time the grab starts, which is the reported desync.
			Plugin.SendPlay(galleryName, loop: true, inGame: true, filler: false, preservePhase: false,
				currentAnimatorStateInfo.normalizedTime, currentAnimatorStateInfo.length);
			Plugin.Instance?.MarkGrabStepHash(currentAnimatorStateInfo.shortNameHash);
		}
	}

	[HarmonyPatch(typeof(GrabScreen), "ShowEnemyHealthUI")]
	[HarmonyPrefix]
	private static bool HideEnemyHealthUiDuringGrab()
	{
		return false;
	}

	[HarmonyPatch(typeof(GrabScreen), "ShowGrabUI")]
	[HarmonyPostfix]
	private static void HidePlayerHeatUiDuringGrab(GrabScreen __instance)
	{
		// Hidden by default - vanilla shows it and it clutters the scene - but it is the only
		// on-screen read of what heat is doing mid-grab, which matters when the lock floor and
		// vanilla's cum cooldown are pulling against each other.
		SetPlayerHeatUiVisible(__instance, (OverkeksMode.UsesRecipe ? OverkeksConfig.CfgShowHeatBarDuringGrab : Plugin.CfgShowHeatBarDuringGrab)?.Value ?? false);
	}

	[HarmonyPatch(typeof(GrabScreen), "HideGrabUI")]
	[HarmonyPostfix]
	private static void RestorePlayerHeatUiAfterGrab(GrabScreen __instance)
	{
		SetPlayerHeatUiVisible(__instance, visible: true);
	}

	private static void SetPlayerHeatUiVisible(GrabScreen grabScreen, bool visible)
	{
		if (grabScreen == null)
		{
			return;
		}
		PlayerStats playerStats = Traverse.Create((object)grabScreen).Field("playerStats").GetValue<PlayerStats>();
		if (!(playerStats == null))
		{
			Image heatBarFill = Traverse.Create((object)playerStats).Field("heatBarFill").GetValue<Image>();
			if (!(heatBarFill == null))
			{
				Transform parent = heatBarFill.transform.parent;
				GameObject bar = ((parent != null) ? parent.gameObject : heatBarFill.gameObject);
				bar.SetActive(visible);
			}
		}
	}
}
