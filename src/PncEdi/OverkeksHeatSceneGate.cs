using System;
using System.Collections.Generic;
using HarmonyLib;
using PixelCrushers.GridController;
using UnityEngine;

namespace PncEdi;

/// <summary>
/// GodMode only: every combat hit / grab attempt fills the heat (white) bar by severity.
/// Grabs are power attacks (~2× a normal hit, ~20 heat) but still cannot fill the bar alone.
/// When the bar is full an H-scene starts; heat is kept for the scene and cleared on EndGrab.
/// Passive heat cooldown is disabled in GodMode — heat only clears when a scene ends.
/// Imp / goonshroom grapples are left alone (they keep using StartGrapple).
/// Serpent hypnosis LandGrab is frame-spam — bank once then break hypnosis.
/// EnemyProjectile hits omit the attacker — we tag the shooter so a full bar still starts a scene.
/// </summary>
[HarmonyPatch]
internal static class OverkeksHeatSceneGate
{
	/// <summary>Normal hit cap — keeps chip damage from racing the bar (~11 typical).</summary>
	private const float MaxNormalFillFraction = 0.22f;

	/// <summary>Grab / power-attack cap — ~20 typical, never a one-shot from empty.</summary>
	private const float MaxGrabFillFraction = 0.25f;

	/// <summary>Grab heat relative to that enemy's normal attackDamage (~1.8× → ~20 from an ~11 hit).</summary>
	private const float GrabDamageMultiplier = 1.8f;

	private const float MinGrabHeat = 14f;

	private const float DefaultGrabHeat = 18f;

	/// <summary>Serpent hypnosis calls LandGrab every frame in grab range — only bank once per window.</summary>
	private const float GrabBankCooldownSeconds = 2.5f;

	/// <summary>Passive heat cooldown (non-GodMode profiles): wait after the last heat increase.</summary>
	private const float HeatCooldownDelaySeconds = 2f;

	/// <summary>Passive heat cooldown rate vs vanilla GetHeatCooldownRate() (non-GodMode profiles).</summary>
	private const float HeatCooldownRateFactor = 0.5f;

	private static readonly Dictionary<int, float> GrabBankedAt = new Dictionary<int, float>();

	private static GameObject _lastAttacker;

	/// <summary>Enemy that is currently spawning EnemyProjectile(s) via SpawnProjectile → FireSpread.</summary>
	private static GameObject _pendingProjectileShooter;

	private static bool _allowScene;

	private static bool _applyingCombatHeat;

	private static float _lastHeatIncreaseAt = float.NegativeInfinity;

	internal static bool ApplyingCombatHeat => _applyingCombatHeat;

	internal static bool Active
	{
		get
		{
			if (OverkeksMode.Active)
			{
				return OverkeksMode.Active;
			}
			return false;
		}
	}

	internal static void ResetForNewScene()
	{
		_lastAttacker = null;
		_pendingProjectileShooter = null;
		_allowScene = false;
		_applyingCombatHeat = false;
		_lastHeatIncreaseAt = float.NegativeInfinity;
		GrabBankedAt.Clear();
	}

	/// <summary>
	/// Cling ends: drop per-enemy grab-bank cooldowns so the next grab is not stuck as "already banked".
	/// If heat filled during cling but no scene started, retry the H-scene
	/// after vanilla's post-grapple GrabImmunity (~1s) so CanBeGrabbed is true again.
	/// </summary>
	internal static void OnGrappleEnded()
	{
		if (!Active)
		{
			return;
		}
		GrabBankedAt.Clear();
		Plugin.DBG("HEAT-SCENE", "cleared grab-bank cooldown after cling end");
		GrabScreen grabScreen = GrabScreen.Instance;
		if (grabScreen != null && grabScreen.IsGrabbed)
		{
			return;
		}
		PlayerStats playerStats = ResolvePlayerStats(grabScreen);
		if (playerStats == null || !IsHeatFull(playerStats))
		{
			return;
		}
		if (Plugin.Instance == null)
		{
			return;
		}
		((MonoBehaviour)(object)Plugin.Instance).StartCoroutine(DeferredHeatFullAfterGrapple(playerStats));
	}

	private static System.Collections.IEnumerator DeferredHeatFullAfterGrapple(PlayerStats playerStats)
	{
		// Vanilla GrappleScreenobject.GrabImmunity holds CanBeGrabbed false for 1s.
		yield return new WaitForSeconds(1.1f);
		if (!Active || playerStats == null)
		{
			yield break;
		}
		GrappleScreenobject grapple = GrappleScreenobject.Instance;
		if (grapple != null && grapple.IsGrappling)
		{
			yield break;
		}
		GrabScreen grabScreen = GrabScreen.Instance;
		if (grabScreen != null && grabScreen.IsGrabbed)
		{
			yield break;
		}
		if (!IsHeatFull(playerStats) || !playerStats.CanBeGrabbed)
		{
			yield break;
		}
		if (_lastAttacker == null)
		{
			Plugin.DBG("HEAT-SCENE", "post-cling heat full but no last attacker — waiting for next hit/grab");
			yield break;
		}
		Plugin.DBG("HEAT-SCENE", "post-cling heat still full -> try StartGrab");
		TryStartSceneIfHeatFull(playerStats, _lastAttacker);
	}

	internal static void NoteAttacker(GameObject attacker)
	{
		if (attacker == null || OverkeksMode.IsKeepableGrappler(attacker))
		{
			return;
		}
		_lastAttacker = attacker;
	}

	/// <summary>
	/// Vanilla ProjectileEnemyAI/EnemyAI call StartGrab on their own grabScreen reference.
	/// GrabScreen.Instance is often the GoonShroom screen among several scene instances —
	/// heat-full StartGrab(attacker) without the enemy's screen + controller shows mushrooms.
	/// </summary>
	private static void StartGrabWithEnemyArt(GrabScreen fallbackScreen, GameObject attacker)
	{
		if (attacker == null)
		{
			return;
		}
		GrabScreen grabScreen = ResolveEnemyGrabScreen(attacker) ?? fallbackScreen ?? GrabScreen.Instance;
		if (grabScreen == null)
		{
			return;
		}
		ResolveGrabArt(attacker, out RuntimeAnimatorController controller, out GrabAnimationData[] anims, out Vector3 posOff, out Vector3 rotOff);
		if (controller == null)
		{
			controller = FindGrabControllerForEnemy(attacker);
		}
		Plugin.DBG("HEAT-SCENE", "StartGrab art screen='" + grabScreen.name + "' ctrl='" + (controller != null ? controller.name : "<null>") + "' anims=" + (anims != null ? anims.Length : 0) + " enemy='" + NameRemap.StripCloneSuffix(attacker.name) + "'");
		if (controller != null || (anims != null && anims.Length != 0))
		{
			grabScreen.StartGrab(attacker, controller, posOff, rotOff, keepPlayerRotation: false, anims);
		}
		else
		{
			grabScreen.StartGrab(attacker);
		}
		// If the screen still runs a foreign family controller, force-swap once more.
		ForceControllerIfMismatched(grabScreen, attacker, controller);
	}

	private static GrabScreen ResolveEnemyGrabScreen(GameObject enemy)
	{
		if (enemy == null)
		{
			return null;
		}
		ProjectileEnemyAI projectile = enemy.GetComponent<ProjectileEnemyAI>();
		if (projectile != null && projectile.grabScreen != null)
		{
			return projectile.grabScreen;
		}
		EnemyAI enemyAi = enemy.GetComponent<EnemyAI>();
		if (enemyAi != null && enemyAi.grabScreen != null)
		{
			return enemyAi.grabScreen;
		}
		BrawlerEnemyAI brawler = enemy.GetComponent<BrawlerEnemyAI>();
		if (brawler != null && brawler.grabScreen != null)
		{
			return brawler.grabScreen;
		}
		SpinningEnemyAI spinning = enemy.GetComponent<SpinningEnemyAI>();
		if (spinning != null && spinning.grabScreen != null)
		{
			return spinning.grabScreen;
		}
		ChargingEnemyAI charging = enemy.GetComponent<ChargingEnemyAI>();
		if (charging != null && charging.grabScreen != null)
		{
			return charging.grabScreen;
		}
		DragonEnemyAI dragon = enemy.GetComponent<DragonEnemyAI>();
		if (dragon != null && dragon.grabScreen != null)
		{
			return dragon.grabScreen;
		}
		ProximityDragonEnemyAI prox = enemy.GetComponent<ProximityDragonEnemyAI>();
		if (prox != null && prox.grabScreen != null)
		{
			return prox.grabScreen;
		}
		return null;
	}

	private static void ResolveGrabArt(GameObject enemy, out RuntimeAnimatorController controller, out GrabAnimationData[] anims, out Vector3 posOff, out Vector3 rotOff)
	{
		controller = null;
		anims = null;
		posOff = default;
		rotOff = default;
		if (enemy == null)
		{
			return;
		}
		ProjectileEnemyAI projectile = enemy.GetComponent<ProjectileEnemyAI>();
		if (projectile != null)
		{
			controller = projectile.grabAnimatorController;
			anims = projectile.grabScreenAnimations;
			posOff = projectile.grabCameraPositionOffset;
			rotOff = projectile.grabCameraRotationOffset;
			return;
		}
		EnemyAI enemyAi = enemy.GetComponent<EnemyAI>();
		if (enemyAi != null)
		{
			// Mirror EnemyAI.GetGrabScreenAssets (stage-2 → transformed grab controller/anims).
			bool transformed = enemyAi.IsTransformed && enemyAi.transformedGrabAnimatorController != null;
			controller = transformed ? enemyAi.transformedGrabAnimatorController : enemyAi.grabAnimatorController;
			anims = (enemyAi.IsTransformed
					&& enemyAi.transformedGrabScreenAnimations != null
					&& enemyAi.transformedGrabScreenAnimations.Length != 0)
				? enemyAi.transformedGrabScreenAnimations
				: enemyAi.grabScreenAnimations;
			posOff = enemyAi.grabCameraPositionOffset;
			rotOff = enemyAi.grabCameraRotationOffset;
			return;
		}
		BrawlerEnemyAI brawler = enemy.GetComponent<BrawlerEnemyAI>();
		if (brawler != null)
		{
			controller = brawler.grabAnimatorController;
			anims = brawler.grabScreenAnimations;
			posOff = brawler.grabCameraPositionOffset;
			rotOff = brawler.grabCameraRotationOffset;
			return;
		}
		SpinningEnemyAI spinning = enemy.GetComponent<SpinningEnemyAI>();
		if (spinning != null)
		{
			controller = spinning.grabAnimatorController;
			anims = spinning.grabScreenAnimations;
			posOff = spinning.grabCameraPositionOffset;
			rotOff = spinning.grabCameraRotationOffset;
			return;
		}
		ChargingEnemyAI charging = enemy.GetComponent<ChargingEnemyAI>();
		if (charging != null)
		{
			controller = charging.trioGrabAnimatorController;
			anims = charging.grabScreenAnimations;
			posOff = charging.trioGrabCameraPositionOffset;
			rotOff = charging.trioGrabCameraRotationOffset;
			return;
		}
		DragonEnemyAI dragon = enemy.GetComponent<DragonEnemyAI>();
		if (dragon != null)
		{
			controller = dragon.grabAnimatorController;
			anims = dragon.grabScreenAnimations;
			posOff = dragon.grabCameraPositionOffset;
			rotOff = dragon.grabCameraRotationOffset;
			return;
		}
		ProximityDragonEnemyAI prox = enemy.GetComponent<ProximityDragonEnemyAI>();
		if (prox != null)
		{
			controller = prox.grabAnimatorController;
			anims = prox.grabScreenAnimations;
			posOff = prox.grabCameraPositionOffset;
			rotOff = prox.grabCameraRotationOffset;
		}
	}

	private static RuntimeAnimatorController FindGrabControllerForEnemy(GameObject enemy)
	{
		if (enemy == null)
		{
			return null;
		}
		string key = NameRemap.ResolveEnemyKey(enemy.name);
		string galleryId = null;
		ProjectileEnemyAI projectile = enemy.GetComponent<ProjectileEnemyAI>();
		if (projectile != null)
		{
			galleryId = projectile.galleryEnemyID;
		}
		else
		{
			EnemyAI enemyAi = enemy.GetComponent<EnemyAI>();
			if (enemyAi != null)
			{
				galleryId = enemyAi.galleryEnemyID;
			}
			else
			{
				SpinningEnemyAI spinning = enemy.GetComponent<SpinningEnemyAI>();
				if (spinning != null)
				{
					galleryId = spinning.galleryEnemyID;
				}
			}
		}
		EnemyGalleryEntry[] entries = Resources.FindObjectsOfTypeAll<EnemyGalleryEntry>();
		RuntimeAnimatorController best = null;
		int bestScore = 0;
		for (int i = 0; i < entries.Length; i++)
		{
			EnemyGalleryEntry entry = entries[i];
			if (entry == null || entry.grabAnimatorController == null)
			{
				continue;
			}
			int score = ScoreGalleryGrabController(entry, key, galleryId);
			if (score > bestScore)
			{
				bestScore = score;
				best = entry.grabAnimatorController;
			}
		}
		if (best != null)
		{
			Plugin.DBG("HEAT-SCENE", "gallery grab ctrl '" + best.name + "' for key=" + key + " galleryId=" + (galleryId ?? "<none>") + " score=" + bestScore);
			return best;
		}
		RuntimeAnimatorController[] controllers = Resources.FindObjectsOfTypeAll<RuntimeAnimatorController>();
		for (int i = 0; i < controllers.Length; i++)
		{
			RuntimeAnimatorController ctrl = controllers[i];
			if (ctrl == null || string.IsNullOrEmpty(ctrl.name))
			{
				continue;
			}
			string name = ctrl.name;
			if (name.IndexOf("Grab", StringComparison.OrdinalIgnoreCase) < 0
				&& name.IndexOf("grab", StringComparison.OrdinalIgnoreCase) < 0)
			{
				continue;
			}
			if (!string.IsNullOrEmpty(key) && name.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0)
			{
				Plugin.DBG("HEAT-SCENE", "named grab ctrl '" + name + "' for key=" + key);
				return ctrl;
			}
		}
		return null;
	}

	private static int ScoreGalleryGrabController(EnemyGalleryEntry entry, string enemyKey, string galleryId)
	{
		int score = 0;
		string id = entry.enemyID ?? "";
		string name = entry.enemyName ?? "";
		string ctrl = entry.grabAnimatorController.name ?? "";
		if (!string.IsNullOrEmpty(galleryId)
			&& (id.Equals(galleryId, StringComparison.OrdinalIgnoreCase)
				|| id.IndexOf(galleryId, StringComparison.OrdinalIgnoreCase) >= 0))
		{
			score += 100;
		}
		if (!string.IsNullOrEmpty(enemyKey))
		{
			if (id.IndexOf(enemyKey, StringComparison.OrdinalIgnoreCase) >= 0
				|| name.IndexOf(enemyKey, StringComparison.OrdinalIgnoreCase) >= 0
				|| ctrl.IndexOf(enemyKey, StringComparison.OrdinalIgnoreCase) >= 0)
			{
				score += 50;
			}
		}
		if (ctrl.IndexOf("GoonShroom", StringComparison.OrdinalIgnoreCase) >= 0
			&& !string.Equals(enemyKey, "goonshroom", StringComparison.OrdinalIgnoreCase))
		{
			score -= 200;
		}
		return score;
	}

	private static void ForceControllerIfMismatched(GrabScreen grabScreen, GameObject attacker, RuntimeAnimatorController wanted)
	{
		if (grabScreen == null || wanted == null)
		{
			return;
		}
		Animator animator = Traverse.Create((object)grabScreen).Field("grabScreenAnimator").GetValue<Animator>();
		if (animator == null)
		{
			return;
		}
		RuntimeAnimatorController current = animator.runtimeAnimatorController;
		if (current == wanted)
		{
			return;
		}
		string enemyKey = NameRemap.ResolveEnemyKey(attacker != null ? attacker.name : null);
		string currentName = current != null ? current.name : "";
		bool foreignGoon = currentName.IndexOf("GoonShroom", StringComparison.OrdinalIgnoreCase) >= 0
			&& !string.Equals(enemyKey, "goonshroom", StringComparison.OrdinalIgnoreCase);
		bool foreignImp = currentName.IndexOf("Imp_Grab", StringComparison.OrdinalIgnoreCase) >= 0
			&& !string.Equals(enemyKey, "imp", StringComparison.OrdinalIgnoreCase);
		if (!foreignGoon && !foreignImp && current == wanted)
		{
			return;
		}
		if (foreignGoon || foreignImp || current != wanted)
		{
			animator.runtimeAnimatorController = wanted;
			Traverse.Create((object)grabScreen).Field("currentGrabController").SetValue(wanted);
			Plugin.DBG("HEAT-SCENE", "forced grab ctrl '" + wanted.name + "' (was '" + currentName + "') for " + (enemyKey ?? "?"));
		}
	}

	internal static float CapCombatHeatAmount(PlayerStats playerStats, float amount, bool grabPowerAttack)
	{
		if (playerStats == null || amount <= 0f)
		{
			return 0f;
		}
		float maxHeat = Mathf.Max(1f, playerStats.MaxHeat);
		float fraction = grabPowerAttack ? MaxGrabFillFraction : MaxNormalFillFraction;
		return Mathf.Min(amount, maxHeat * fraction);
	}

	internal static void ApplyCombatHeat(PlayerStats playerStats, float severity, bool grabPowerAttack = false)
	{
		if (playerStats == null || severity <= 0f)
		{
			return;
		}
		float amount = CapCombatHeatAmount(playerStats, severity, grabPowerAttack);
		if (amount <= 0f)
		{
			return;
		}
		_applyingCombatHeat = true;
		try
		{
			playerStats.GenerateHeat(amount);
		}
		finally
		{
			_applyingCombatHeat = false;
		}
	}

	internal static bool IsHeatFull(PlayerStats playerStats)
	{
		if (playerStats == null)
		{
			return false;
		}
		return playerStats.CurrentHeat >= Mathf.Max(1f, playerStats.MaxHeat) - 0.05f;
	}

	internal static void ResetHeatAfterScene(PlayerStats playerStats)
	{
		if (!(playerStats == null))
		{
			playerStats.SetHeat(0f);
			playerStats.ClearOverheatIfBelowMax();
			Plugin.DBG("HEAT-SCENE", "heat cleared after scene end");
		}
	}

	private static float ResolveGrabSeverity(GameObject enemy)
	{
		if (enemy == null)
		{
			return DefaultGrabHeat;
		}
		float baseDamage = 0f;
		EnemyAI enemyAI = enemy.GetComponent<EnemyAI>();
		if (enemyAI != null)
		{
			baseDamage = enemyAI.attackDamage;
		}
		else
		{
			BrawlerEnemyAI brawler = enemy.GetComponent<BrawlerEnemyAI>();
			if (brawler != null)
			{
				baseDamage = brawler.attackDamage;
			}
			else
			{
				DragonEnemyAI dragon = enemy.GetComponent<DragonEnemyAI>();
				if (dragon != null)
				{
					baseDamage = dragon.attackDamage;
				}
				else
				{
					ProximityDragonEnemyAI proxDragon = enemy.GetComponent<ProximityDragonEnemyAI>();
					if (proxDragon != null)
					{
						baseDamage = proxDragon.attackDamage;
					}
					else
					{
						SpinningEnemyAI spinning = enemy.GetComponent<SpinningEnemyAI>();
						if (spinning != null)
						{
							baseDamage = spinning.spinHitDamage;
						}
					}
				}
			}
		}
		if (baseDamage <= 0f)
		{
			return DefaultGrabHeat;
		}
		return Mathf.Max(MinGrabHeat, baseDamage * GrabDamageMultiplier);
	}

	private static PlayerStats ResolvePlayerStats(GrabScreen grabScreen)
	{
		if (grabScreen != null)
		{
			PlayerStats fromGrab = Traverse.Create((object)grabScreen).Field("playerStats").GetValue<PlayerStats>();
			if (fromGrab != null)
			{
				return fromGrab;
			}
		}
		GameObject player = GameObject.FindGameObjectWithTag("Player");
		if (player != null)
		{
			return player.GetComponent<PlayerStats>();
		}
		return null;
	}

	/// <summary>
	/// After a combat heat add: if the bar is full, start that attacker's H-scene.
	/// Projectile hits omit the attacker on TakeDamage — caller notes the shooter first.
	/// Unlike LandGrab (which StopHypnosis inside vanilla), a heat-full StartGrab from a
	/// projectile must clear an active hypnosis pull first or LateUpdate keeps rotating
	/// the camera and the grab UI can land as a white/blank screen.
	/// </summary>
	internal static void TryStartSceneIfHeatFull(PlayerStats playerStats, GameObject attacker)
	{
		if (!Active || playerStats == null || !IsHeatFull(playerStats))
		{
			return;
		}
		if (attacker == null)
		{
			attacker = _lastAttacker;
		}
		if (attacker == null || OverkeksMode.IsKeepableGrappler(attacker))
		{
			return;
		}
		GrappleScreenobject grapple = GrappleScreenobject.Instance;
		if (grapple != null && grapple.IsGrappling)
		{
			return;
		}
		GrabScreen grabScreen = GrabScreen.Instance;
		if (grabScreen == null || grabScreen.IsGrabbed || !playerStats.CanBeGrabbed)
		{
			return;
		}
		ClearActiveHypnosis("combat heat-full StartGrab");
		Plugin.DBG("HEAT-SCENE", "heat full from combat -> StartGrab '" + NameRemap.StripCloneSuffix(attacker.name) + "'");
		_allowScene = true;
		try
		{
			StartGrabWithEnemyArt(grabScreen, attacker);
		}
		finally
		{
			_allowScene = false;
		}
	}

	/// <summary>
	/// Same cleanup LandGrab gets via vanilla StopHypnosis — for heat-full scenes started
	/// from projectiles / blocked damage while a serpent (or gravy) is still hypnotising.
	/// </summary>
	internal static void ClearActiveHypnosis(string why)
	{
		BrawlerEnemyAI hypnotist = GetCurrentHypnotist();
		if (hypnotist == null)
		{
			return;
		}
		if (hypnotist.CurrentState == BrawlerEnemyAI.AIState.Hypnotising)
		{
			Traverse.Create((object)hypnotist).Method("TransitionToChasing").GetValue();
		}
		else
		{
			Traverse.Create((object)hypnotist).Method("StopHypnosis").GetValue();
		}
		Plugin.DBG("HEAT-SCENE", "cleared hypnosis before scene (" + why + ") '" + NameRemap.StripCloneSuffix(hypnotist.gameObject.name) + "'");
	}

	private static BrawlerEnemyAI GetCurrentHypnotist()
	{
		return Traverse.Create(typeof(BrawlerEnemyAI)).Field("currentHypnotist").GetValue<BrawlerEnemyAI>();
	}

	private static bool WasGrabBankedRecently(GameObject enemy)
	{
		if (enemy == null)
		{
			return false;
		}
		if (!GrabBankedAt.TryGetValue(enemy.GetInstanceID(), out var at))
		{
			return false;
		}
		return Time.time - at < GrabBankCooldownSeconds;
	}

	private static void MarkGrabBanked(GameObject enemy)
	{
		if (enemy != null)
		{
			GrabBankedAt[enemy.GetInstanceID()] = Time.time;
		}
	}

	/// <returns>False if this enemy already banked within the cooldown (no heat added).</returns>
	private static bool TryBankGrabPower(GameObject enemy, PlayerStats playerStats, out bool filledBar)
	{
		filledBar = false;
		if (playerStats == null || enemy == null)
		{
			return false;
		}
		if (WasGrabBankedRecently(enemy))
		{
			return false;
		}
		MarkGrabBanked(enemy);
		float severity = ResolveGrabSeverity(enemy);
		float before = playerStats.CurrentHeat;
		ApplyCombatHeat(playerStats, severity, grabPowerAttack: true);
		NoteAttacker(enemy);
		filledBar = IsHeatFull(playerStats);
		Plugin.DBG("HEAT-SCENE", "grab-power +" + (playerStats.CurrentHeat - before).ToString("0.#") + " -> " + playerStats.CurrentHeat.ToString("0.#") + "/" + playerStats.MaxHeat.ToString("0.#") + " '" + NameRemap.StripCloneSuffix(enemy.name) + "'" + (filledBar ? " FULL" : ""));
		return true;
	}

	/// <summary>
	/// Blinded Beast / fodder grab swings often miss the player raycast when overlapping after a pitfall
	/// (RaycastAll does not hit a collider the ray starts inside). Bank grab-power on the attempt itself —
	/// same idea as serpent LandGrab — so heat still fills and a full bar can start the scene.
	/// </summary>
	internal static void BankGrabAttemptFromAi(GameObject enemy)
	{
		if (!Active || enemy == null || OverkeksMode.IsKeepableGrappler(enemy))
		{
			return;
		}
		PlayerStats playerStats = ResolvePlayerStats(GrabScreen.Instance);
		if (playerStats == null)
		{
			return;
		}
		if (IsHeatFull(playerStats))
		{
			TryStartSceneIfHeatFull(playerStats, enemy);
			return;
		}
		if (!TryBankGrabPower(enemy, playerStats, out bool filledBar))
		{
			return;
		}
		if (filledBar)
		{
			TryStartSceneIfHeatFull(playerStats, enemy);
		}
	}

	[HarmonyPatch(typeof(EnemyAI), "StartGrab")]
	[HarmonyPostfix]
	private static void EnemyAI_StartGrab_BankAttempt(EnemyAI __instance)
	{
 if(!OverkeksMode.Active) return;
		if (__instance != null)
		{
			BankGrabAttemptFromAi(__instance.gameObject);
		}
	}

	[HarmonyPatch(typeof(SpinningEnemyAI), "StartGrab")]
	[HarmonyPostfix]
	private static void Spinning_StartGrab_BankAttempt(SpinningEnemyAI __instance)
	{
 if(!OverkeksMode.Active) return;
		if (__instance != null)
		{
			BankGrabAttemptFromAi(__instance.gameObject);
		}
	}

	private static void BreakHypnosisToChase(BrawlerEnemyAI brawler)
	{
		if (brawler == null || brawler.CurrentState != BrawlerEnemyAI.AIState.Hypnotising)
		{
			return;
		}
		Traverse.Create((object)brawler).Method("TransitionToChasing").GetValue();
		Plugin.DBG("HEAT-SCENE", "broke serpent hypnosis after power-grab bank '" + NameRemap.StripCloneSuffix(brawler.gameObject.name) + "'");
	}

	/// <returns>True = run vanilla StartGrab; false = converted to heat only.</returns>
	private static bool TryAdmitStartGrab(GrabScreen grabScreen, GameObject enemy)
	{
		if (!Active)
		{
			return true;
		}
		PlayerStats playerStats = ResolvePlayerStats(grabScreen);
		if (playerStats == null)
		{
			return true;
		}
		if (_allowScene || IsHeatFull(playerStats))
		{
			// During post-grapple GrabImmunity vanilla refuses StartGrab — defer, keep heat full.
			if (!_allowScene && !playerStats.CanBeGrabbed)
			{
				Plugin.DBG("HEAT-SCENE", "defer StartGrab (heat full, grab immunity) '" + ((enemy != null) ? NameRemap.StripCloneSuffix(enemy.name) : "?") + "'");
				return false;
			}
			// Projectile / combat heat-full can admit while hypnosis is still pulling the camera.
			ClearActiveHypnosis("admit heat-full StartGrab");
			Plugin.DBG("HEAT-SCENE", "admit StartGrab (heat full) '" + ((enemy != null) ? NameRemap.StripCloneSuffix(enemy.name) : "?") + "'");
			return true;
		}
		if (WasGrabBankedRecently(enemy))
		{
			return false;
		}
		if (!TryBankGrabPower(enemy, playerStats, out var filledBar))
		{
			return false;
		}
		if (filledBar)
		{
			if (!playerStats.CanBeGrabbed)
			{
				Plugin.DBG("HEAT-SCENE", "defer StartGrab (grab filled heat, grab immunity) '" + ((enemy != null) ? NameRemap.StripCloneSuffix(enemy.name) : "?") + "'");
				return false;
			}
			Plugin.DBG("HEAT-SCENE", "admit StartGrab (grab filled heat) '" + ((enemy != null) ? NameRemap.StripCloneSuffix(enemy.name) : "?") + "'");
			return true;
		}
		return false;
	}

	[HarmonyPatch(typeof(GrabScreen), "EndGrab")]
	[HarmonyPostfix]
	[HarmonyPriority(Priority.Last)]
	private static void EndGrab_ResetHeat_Postfix(GrabScreen __instance)
	{
 if(!OverkeksMode.HooksAllowed) return;
		if (!Active)
		{
			return;
		}
		ResetHeatAfterScene(ResolvePlayerStats(__instance));
	}

	[HarmonyPatch(typeof(GrappleScreenobject), "EndGrapple")]
	[HarmonyPostfix]
	[HarmonyPriority(Priority.Last)]
	private static void EndGrapple_ResetBank_Postfix()
	{
 if(!OverkeksMode.Active) return;
		OnGrappleEnded();
	}

	[HarmonyPatch(typeof(GrabScreen), "StartGrab")]
	[HarmonyPrefix]
	[HarmonyPriority(1000)]
	private static bool StartGrab_HeatGate_Prefix(GrabScreen __instance, GameObject enemy)
	{
 if(!OverkeksMode.Active) return true;
		return TryAdmitStartGrab(__instance, enemy);
	}

	/// <summary>
	/// Hypnosis lands a grab every frame while in range. Bank one power hit, then break out of hypnosis
	/// so the serpent must re-open (cooldown) instead of instantly filling the bar.
	/// </summary>
	[HarmonyPatch(typeof(BrawlerEnemyAI), "LandGrab")]
	[HarmonyPrefix]
	[HarmonyPriority(1000)]
	private static bool LandGrab_HeatGate_Prefix(BrawlerEnemyAI __instance)
	{
 if(!OverkeksMode.Active) return true;
		if (!Active || __instance == null)
		{
			return true;
		}
		PlayerStats playerStats = ResolvePlayerStats(GrabScreen.Instance);
		if (playerStats == null)
		{
			return true;
		}
		if (IsHeatFull(playerStats))
		{
			return true;
		}
		bool banked = TryBankGrabPower(__instance.gameObject, playerStats, out var filledBar);
		if (filledBar)
		{
			return true;
		}
		BreakHypnosisToChase(__instance);
		if (!banked)
		{
			Plugin.DBG("HEAT-SCENE", "serpent LandGrab ignored (already banked this window)");
		}
		return false;
	}

	[HarmonyPatch(typeof(MimicEnemy), "TriggerMimicGrab")]
	[HarmonyPrefix]
	[HarmonyPriority(900)]
	private static bool TriggerMimicGrab_HeatGate_Prefix(MimicEnemy __instance)
	{
 if(!OverkeksMode.Active) return true;
		if (!Active || __instance == null)
		{
			return true;
		}
		PlayerStats playerStats = ResolvePlayerStats(GrabScreen.Instance);
		if (playerStats == null)
		{
			return true;
		}
		if (IsHeatFull(playerStats))
		{
			return true;
		}
		if (!TryBankGrabPower(__instance.gameObject, playerStats, out var filledBar))
		{
			return false;
		}
		return filledBar;
	}

	[HarmonyPatch(typeof(DualWieldingSystem), "TakeDamage", new[] { typeof(int), typeof(GameObject) })]
	[HarmonyPrefix]
	private static void DualWield_TakeDamage_NoteAttacker(GameObject attacker)
	{
 if(!OverkeksMode.Active) return;
		if (Active)
		{
			NoteAttacker(attacker);
		}
	}

	/// <summary>
	/// EnemyProjectile.DealDamageToPlayer calls TakeDamage(int) with no attacker.
	/// Tag the shooter on spawn so a full heat bar can still start that enemy's H-scene.
	/// </summary>
	[HarmonyPatch(typeof(EnemyAI), "SpawnProjectile")]
	[HarmonyPrefix]
	private static void EnemyAI_SpawnProjectile_Prefix(EnemyAI __instance)
	{
 if(!OverkeksMode.Active) return;
		BeginProjectileSpawn(__instance != null ? __instance.gameObject : null);
	}

	[HarmonyPatch(typeof(EnemyAI), "SpawnProjectile")]
	[HarmonyPostfix]
	private static void EnemyAI_SpawnProjectile_Postfix()
	{
 if(!OverkeksMode.Active) return;
		EndProjectileSpawn();
	}

	[HarmonyPatch(typeof(ProjectileEnemyAI), "SpawnProjectile")]
	[HarmonyPrefix]
	private static void ProjectileEnemy_SpawnProjectile_Prefix(ProjectileEnemyAI __instance)
	{
 if(!OverkeksMode.Active) return;
		BeginProjectileSpawn(__instance != null ? __instance.gameObject : null);
	}

	[HarmonyPatch(typeof(ProjectileEnemyAI), "SpawnProjectile")]
	[HarmonyPostfix]
	private static void ProjectileEnemy_SpawnProjectile_Postfix()
	{
 if(!OverkeksMode.Active) return;
		EndProjectileSpawn();
	}

	[HarmonyPatch(typeof(BrawlerEnemyAI), "SpawnProjectile")]
	[HarmonyPrefix]
	private static void Brawler_SpawnProjectile_Prefix(BrawlerEnemyAI __instance)
	{
 if(!OverkeksMode.Active) return;
		BeginProjectileSpawn(__instance != null ? __instance.gameObject : null);
	}

	[HarmonyPatch(typeof(BrawlerEnemyAI), "SpawnProjectile")]
	[HarmonyPostfix]
	private static void Brawler_SpawnProjectile_Postfix()
	{
 if(!OverkeksMode.Active) return;
		EndProjectileSpawn();
	}

	[HarmonyPatch(typeof(SpinningEnemyAI), "SpawnProjectile")]
	[HarmonyPrefix]
	private static void Spinning_SpawnProjectile_Prefix(SpinningEnemyAI __instance)
	{
 if(!OverkeksMode.Active) return;
		BeginProjectileSpawn(__instance != null ? __instance.gameObject : null);
	}

	[HarmonyPatch(typeof(SpinningEnemyAI), "SpawnProjectile")]
	[HarmonyPostfix]
	private static void Spinning_SpawnProjectile_Postfix()
	{
 if(!OverkeksMode.Active) return;
		EndProjectileSpawn();
	}

	[HarmonyPatch(typeof(EnemyProjectile), "Initialize")]
	[HarmonyPostfix]
	private static void EnemyProjectile_Initialize_TagShooter(EnemyProjectile __instance)
	{
 if(!OverkeksMode.Active) return;
		if (!Active || __instance == null || _pendingProjectileShooter == null)
		{
			return;
		}
		EnemyProjectileHeatSource marker = __instance.GetComponent<EnemyProjectileHeatSource>();
		if (marker == null)
		{
			marker = OverkeksMode.AddOwnedComponent<EnemyProjectileHeatSource>(__instance.gameObject);
		}
		marker.Source = _pendingProjectileShooter;
	}

	[HarmonyPatch(typeof(EnemyProjectile), "DealDamageToPlayer")]
	[HarmonyPrefix]
	private static void EnemyProjectile_DealDamage_NoteAttacker(EnemyProjectile __instance)
	{
 if(!OverkeksMode.Active) return;
		if (!Active || __instance == null)
		{
			return;
		}
		EnemyProjectileHeatSource marker = __instance.GetComponent<EnemyProjectileHeatSource>();
		if (marker != null && marker.Source != null)
		{
			NoteAttacker(marker.Source);
			Plugin.DBG("HEAT-SCENE", "projectile hit from '" + NameRemap.StripCloneSuffix(marker.Source.name) + "'");
			return;
		}
		Plugin.DBG("HEAT-SCENE", "projectile hit with no shooter tag — heat-full scene may use stale attacker");
	}

	/// <summary>
	/// Belt-and-suspenders: even if ClearActiveHypnosis missed, never keep yanking the camera
	/// once an H-scene owns the screen (projectile heat-full used to leave LateUpdate fighting GrabScreen).
	/// </summary>
	[HarmonyPatch(typeof(BrawlerEnemyAI), "LateUpdate")]
	[HarmonyPrefix]
	private static bool LateUpdate_SkipHypnosisCameraDuringGrab(BrawlerEnemyAI __instance)
	{
 if(!OverkeksMode.Active) return true;
		if (!Active || __instance == null)
		{
			return true;
		}
		GrabScreen grabScreen = GrabScreen.Instance;
		if (grabScreen != null && grabScreen.IsGrabbed)
		{
			return false;
		}
		return true;
	}

	private static void BeginProjectileSpawn(GameObject shooter)
	{
		if (Active && shooter != null)
		{
			_pendingProjectileShooter = shooter;
		}
	}

	private static void EndProjectileSpawn()
	{
		_pendingProjectileShooter = null;
	}

	[HarmonyPatch(typeof(PlayerStats), "GenerateHeat")]
	[HarmonyPostfix]
	private static void GenerateHeat_NoteCooldownDelay(float amount)
	{
 if(!OverkeksMode.Active) return;
		// Slow cooldown applies under any gameplay-tweak profile (GodMode and PressureAndRelease).
		if (OverkeksMode.Active && amount > 0f)
		{
			_lastHeatIncreaseAt = Time.time;
		}
	}

	/// <summary>
	/// GodMode: no passive heat cooldown — bar only clears when an H-scene ends,
	/// except while an ambient funscript or in-world peek scene is playing (full vanilla rate).
	/// Other gameplay profiles: half the vanilla rate, only after 2s since the last heat increase
	/// (full rate while ambient/peek is active).
	/// GrabScreen.HandleHeatBuildup still cools heat during climax (uses CoolHeat directly).
	/// </summary>
	[HarmonyPatch(typeof(PlayerStats), "HandleHeatCooldown")]
	[HarmonyPrefix]
	private static bool HandleHeatCooldown_GodMode(PlayerStats __instance)
	{
 if(!OverkeksMode.Active) return true;
		if (!OverkeksMode.Active)
		{
			return true;
		}
		if (__instance == null || __instance.CurrentHeat <= 0f)
		{
			return false;
		}
		if (IsAmbientOrPeekHeatCoolActive())
		{
			float watchAmount = __instance.GetHeatCooldownRate() * Time.deltaTime;
			if (watchAmount > 0f)
			{
				__instance.CoolHeat(watchAmount);
			}
			return false;
		}
		if (OverkeksMode.Active)
		{
			return false;
		}
		if (Time.time - _lastHeatIncreaseAt < HeatCooldownDelaySeconds)
		{
			return false;
		}
		float amount = __instance.GetHeatCooldownRate() * HeatCooldownRateFactor * Time.deltaTime;
		if (amount > 0f)
		{
			__instance.CoolHeat(amount);
		}
		return false;
	}

	/// <summary>
	/// Ambient proximity funscript and in-world peek scenes cool heat for as long as they run.
	/// </summary>
	private static bool IsAmbientOrPeekHeatCoolActive()
	{
		if (AmbientProximity.IsFunscriptActive)
		{
			return true;
		}
		if (GalleryHooks.InGamePeekScene && CameraSwapHooks.Active)
		{
			return true;
		}
		return false;
	}
}

/// <summary>Marks which enemy fired an EnemyProjectile so GodMode heat-full can StartGrab that shooter.</summary>
internal sealed class EnemyProjectileHeatSource : MonoBehaviour
{
	internal GameObject Source;
}
