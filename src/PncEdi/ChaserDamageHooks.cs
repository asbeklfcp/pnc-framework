using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using PixelCrushers.GridController;
using UnityEngine;

namespace PncEdi;

/// <summary>
/// Vanilla DragonEnemyAI / ProximityDragonEnemyAI.TakeDamage is a no-op (damage number 0).
/// Hunting chasers take real damage; on defeat they restage like after an H-scene.
/// Also installs hit proxies on child colliders (melee only resolves IDamageable on the hit collider)
/// and drives the miniboss HP bar. Wendigo gets attack/move tuning.
/// </summary>
[HarmonyPatch]
internal static class ChaserDamageHooks
{
	internal const int DragonMaxHealth = 125;

	internal const int WendigoMaxHealth = 80;

	private const float WendigoAttackRate = 1.5f;

	private const float WendigoMoveRate = 1.25f;

	private static readonly HashSet<int> WendigoTunedIds = new HashSet<int>();
    sealed class TraceBudget {internal int Count;}
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MonoBehaviour,TraceBudget> DamageTraces =
        new System.Runtime.CompilerServices.ConditionalWeakTable<MonoBehaviour,TraceBudget>();
    static void TraceDamage(MonoBehaviour ai,string reason,int damage) {
        if(ai==null)return;var budget=DamageTraces.GetOrCreateValue(ai);if(budget.Count>=24)return;budget.Count++;
        var actor=ai.gameObject;
        Plugin.DBG("CHASER-DAMAGE","actor="+actor.name+"; id="+ai.GetInstanceID()+"; reason="+reason+
            "; incoming="+damage+"; hp="+OptionalMembers.Read(ai,"currentHealth")+"/"+OptionalMembers.Read(ai,"maxHealth")+
            "; active="+actor.activeInHierarchy+"; managed="+ChaserSweeperHooks.IsManagedChaser(actor)+
            "; hunting="+ChaserSweeperHooks.IsHuntingChaser(actor)+"; frozen="+OptionalMembers.Read(ai,"frozenByArena"));
    }


	internal static void ResetForScene()
	{
		WendigoTunedIds.Clear();
		ChaserHealthBarUi.Clear();
	}

	internal static void ApplyHealth(GameObject enemy)
	{
		if (enemy == null)
		{
			return;
		}
		int max = ResolveMaxHealth(enemy);
		if (ChaserFodderSpawn.IsFodder(enemy))
		{
			max = Mathf.Max(1, Mathf.CeilToInt(max * ChaserFodderSpawn.HealthScalePublic));
		}
		DragonEnemyAI dragon = enemy.GetComponent<DragonEnemyAI>();
		if (dragon != null)
		{
			dragon.maxHealth = max;
			dragon.currentHealth = max;
		}
		ProximityDragonEnemyAI proximity = enemy.GetComponent<ProximityDragonEnemyAI>();
		if (proximity != null)
		{
			proximity.maxHealth = max;
			proximity.currentHealth = max;
		}
		foreach (MonoBehaviour ai in EnemyAiTypes.On(enemy))
		{
			EnemyKeepAliveHelper.ClearDeadState(ai);
		}
		EnsureHitProxies(enemy);
        ChaserHitLayerLease.Register(enemy);
		TryApplyWendigoTuning(enemy);
	}

	internal static void OnChaserActivated(GameObject enemy)
	{
		if (enemy == null)
		{
			return;
		}
		ApplyHealth(enemy);
		MonoBehaviour ai = GetChaserAi(enemy);
		if (ai == null)
		{
			return;
		}
		Traverse t = Traverse.Create((object)ai);
		int max = t.Field("maxHealth").GetValue<int>();
		int current = t.Field("currentHealth").GetValue<int>();
		ChaserHealthBarUi.Show(ai, ResolveDisplayName(enemy), current, max);
	}

	internal static int ResolveMaxHealth(GameObject enemy)
	{
		int max = GrabStruggleHooks.IsWendigoGrabEnemy(enemy) ? WendigoMaxHealth : DragonMaxHealth;
		if (!OverkeksMode.Active)
		{
			return max;
		}
		float mult = OverkeksConfig.CfgEnemyHealthMultiplier?.Value ?? 1f;
		if (mult < 0.05f)
		{
			mult = 0.05f;
		}
		if (Mathf.Approximately(mult, 1f))
		{
			return max;
		}
		return Mathf.Max(1, Mathf.CeilToInt(max * mult));
	}

	internal static string ResolveDisplayName(GameObject enemy)
	{
		if (GrabStruggleHooks.IsWendigoGrabEnemy(enemy))
		{
			return "Wendigo";
		}
		return "Dragon";
	}

	[HarmonyPatch(typeof(DragonEnemyAI), "TakeDamage")]
	[HarmonyPrefix]
	private static bool DragonTakeDamage_Prefix(DragonEnemyAI __instance, int damage)
	{
 if(!OverkeksMode.Active) return true;
		return !TryApplyChaserDamage(__instance, damage);
	}

	[HarmonyPatch(typeof(ProximityDragonEnemyAI), "TakeDamage")]
	[HarmonyPrefix]
	private static bool ProximityTakeDamage_Prefix(ProximityDragonEnemyAI __instance, int damage)
	{
 if(!OverkeksMode.Active) return true;
		return !TryApplyChaserDamage(__instance, damage);
	}

	[HarmonyPatch(typeof(DragonEnemyAI), "Die")]
	[HarmonyPrefix]
	[HarmonyPriority(Priority.First)]
	private static bool DragonDie_Prefix(DragonEnemyAI __instance)
	{
 if(!OverkeksMode.Active) return true;
		return !TryRestageOnDefeat(__instance);
	}

	[HarmonyPatch(typeof(ProximityDragonEnemyAI), "Die")]
	[HarmonyPrefix]
	[HarmonyPriority(Priority.First)]
	private static bool ProximityDie_Prefix(ProximityDragonEnemyAI __instance)
	{
 if(!OverkeksMode.Active) return true;
		return !TryRestageOnDefeat(__instance);
	}

	/// <summary>Returns true when this prefix fully handled damage (skip vanilla).</summary>
	private static bool TryApplyChaserDamage(MonoBehaviour ai, int damage)
	{
		if (!OverkeksMode.Active || ai == null || damage <= 0)
		{
			return false;
		}
		damage = CommunityCombatRuntime.ScaleOutgoingDamage(damage);
		GameObject enemy = ai.gameObject;
		bool fodder = ChaserFodderSpawn.IsFodder(enemy);
		if (!ShouldReceiveCombatDamage(enemy, fodder))
		{
            TraceDamage(ai,"inactive-or-staged",damage);
			return false;
		}
		Traverse t = Traverse.Create((object)ai);
		if (t.Field("frozenByArena").FieldExists() && t.Field("frozenByArena").GetValue<bool>())
		{
            TraceDamage(ai,"arena-frozen",damage);
			return false;
		}
		if (IsGrabbedByThisEnemy(enemy))
		{
            TraceDamage(ai,"capture-owner",damage);
			return true;
		}
		// Stuck isDead after a blocked Die/grab = silent invulnerability. Clear if still in play.
		if (t.Field("isDead").FieldExists() && t.Field("isDead").GetValue<bool>())
		{
			int hp = t.Field("currentHealth").FieldExists() ? t.Field("currentHealth").GetValue<int>() : 0;
			if (hp <= 0 && !fodder && ChaserSweeperHooks.IsManagedChaser(enemy))
			{
				return true;
			}
			EnemyKeepAliveHelper.ClearDeadState(ai);
			if (t.Field("isDead").FieldExists() && t.Field("isDead").GetValue<bool>())
			{
				t.Field("isDead").SetValue(false);
			}
			Plugin.DBG("CHASER-HIT", NameRemap.StripCloneSuffix(enemy.name) + " cleared stuck isDead on hit");
		}
		int max = t.Field("maxHealth").GetValue<int>();
		if (max <= 0)
		{
			max = fodder
				? Mathf.Max(1, Mathf.CeilToInt(ResolveMaxHealth(enemy) * ChaserFodderSpawn.HealthScalePublic))
				: ResolveMaxHealth(enemy);
			t.Field("maxHealth").SetValue(max);
		}
		damage = CommunityCombatRuntime.CapDamageForMinHits(damage, max, ai.GetInstanceID());
		if (damage <= 0)
		{
			return true;
		}
		int current = Mathf.Max(0, t.Field("currentHealth").GetValue<int>() - damage);
		t.Field("currentHealth").SetValue(current);
        TraceDamage(ai,"applied",damage);
		PlayHurtFeedback(ai, t, damage);
		if (!fodder)
		{
			ChaserHealthBarUi.UpdateHealth(ai, current, max);
		}
		if (current <= 0)
		{
			if (fodder || !ChaserSweeperHooks.IsManagedChaser(enemy))
			{
				KillFodderPermanently(ai);
			}
			else
			{
				DefeatChaser(ai, t);
			}
		}
		return true;
	}

	/// <summary>
	/// Combat damage for hunting sweeper bosses, pool fodder, and any live dragon left in the
	/// play space (not sitting in ChaserHolder staging). Staged/hidden managed slots stay immune.
	/// </summary>
	private static bool ShouldReceiveCombatDamage(GameObject enemy, bool fodder)
	{
		if (enemy == null || !enemy.activeInHierarchy)
		{
			return false;
		}
		if (fodder)
		{
			return true;
		}
		if (ChaserSweeperHooks.IsHuntingChaser(enemy))
		{
			return true;
		}
		// Managed but not activated = waiting in ChaserHolder.
		if (ChaserSweeperHooks.IsManagedChaser(enemy))
		{
			return false;
		}
		if (!GrabStruggleHooks.IsDragonGrabEnemy(enemy) && !GrabStruggleHooks.IsWendigoGrabEnemy(enemy))
		{
			return false;
		}
		// Non-managed dragon still in the level (e.g. after grab without restage) must stay killable.
		return !ChaserSweeperHooks.IsNearChaserHolder(enemy.transform.position);
	}

	private static bool TryRestageOnDefeat(MonoBehaviour ai)
	{
		if (!OverkeksMode.Active || ai == null)
		{
			return false;
		}
		// Pool fodder / non-managed: let vanilla Die destroy them (no ChaserHolder restage).
		if (ChaserFodderSpawn.IsFodder(ai.gameObject) || !ChaserSweeperHooks.IsManagedChaser(ai.gameObject))
		{
			return false;
		}
		if (IsGrabbedByThisEnemy(ai.gameObject))
		{
			return false;
		}
		DefeatChaser(ai, Traverse.Create((object)ai));
		return true;
	}

	/// <summary>Permanent kill for pool-spawned dragon/wendigo — no restage, no HP bar.</summary>
	private static void KillFodderPermanently(MonoBehaviour ai)
	{
		if (ai == null)
		{
			return;
		}
		GameObject enemy = ai.gameObject;
		// Release keep-alive so vanilla Die/Destroy are not blocked after combat kill.
		if (GrabEnemyProtection.IsProtected(enemy) || GrabEnemyProtection.IsProtectedDuringScene(enemy))
		{
			GrabEnemyProtection.Clear();
		}
		Plugin.DBG("CHASER-KILL", NameRemap.StripCloneSuffix(enemy.name) + " fodder defeated — permanent");
		KillHeatCool.OnEnemyDefeated(enemy);
		System.Reflection.MethodInfo die = AccessTools.Method(ai.GetType(), "Die", Type.EmptyTypes);
		if (die != null)
		{
			die.Invoke(ai, null);
			return;
		}
		UnityEngine.Object.Destroy(enemy);
	}

	private static void DefeatChaser(MonoBehaviour ai, Traverse t)
	{
		if (ai == null)
		{
			return;
		}
		if (t.Field("isDead").FieldExists() && t.Field("isDead").GetValue<bool>())
		{
			return;
		}
		GameObject enemy = ai.gameObject;
		if (t.Field("isDead").FieldExists())
		{
			t.Field("isDead").SetValue(true);
		}
		if (t.Field("currentHealth").FieldExists())
		{
			t.Field("currentHealth").SetValue(0);
		}
		if (t.Method("TransitionToDead", Array.Empty<object>()).MethodExists())
		{
			t.Method("TransitionToDead", Array.Empty<object>()).GetValue();
		}
		if (t.Method("StopIdleMovingSound", Array.Empty<object>()).MethodExists())
		{
			t.Method("StopIdleMovingSound", Array.Empty<object>()).GetValue();
		}
		PlayClip(ai, t, "deathSound");
		int max = t.Field("maxHealth").FieldExists() ? Mathf.Max(1, t.Field("maxHealth").GetValue<int>()) : ResolveMaxHealth(enemy);
		ChaserHealthBarUi.UpdateHealth(ai, 0, max);
		ChaserHealthBarUi.Hide(ai);
		Plugin.DBG("CHASER-KILL", NameRemap.StripCloneSuffix(enemy.name) + " defeated — restage like after H-scene");
		KillHeatCool.OnEnemyDefeated(enemy);
		ChaserSweeperHooks.ScheduleReturnToStaging(enemy);
	}

	internal static void EnsureHitProxies(GameObject enemy)
	{
		MonoBehaviour ai = GetChaserAi(enemy);
		if (ai == null)
		{
			return;
		}
		Collider[] colliders = enemy.GetComponentsInChildren<Collider>(includeInactive: true);
		int added = 0;
		for (int i = 0; i < colliders.Length; i++)
		{
			Collider col = colliders[i];
			if (col == null)
			{
				continue;
			}
			if (col.GetComponent<IDamageable>() != null)
			{
				continue;
			}
			ChaserHitProxy proxy = col.gameObject.GetComponent<ChaserHitProxy>();
			if (proxy == null)
			{
				proxy = OverkeksMode.AddOwnedComponent<ChaserHitProxy>(col.gameObject);
				added++;
			}
			proxy.OwnerAi = ai;
		}
		if (added > 0)
		{
			Plugin.DBG("CHASER-HIT", NameRemap.StripCloneSuffix(enemy.name) + " hit-proxies on " + added + " collider(s)");
		}
	}

	private static void TryApplyWendigoTuning(GameObject enemy)
	{
		if (!GrabStruggleHooks.IsWendigoGrabEnemy(enemy))
		{
			return;
		}
		int id = enemy.GetInstanceID();
		if (!WendigoTunedIds.Add(id))
		{
			return;
		}
		MonoBehaviour ai = GetChaserAi(enemy);
		if (ai == null)
		{
			return;
		}
		Traverse t = Traverse.Create((object)ai);
		ScaleFloatField(t, "attackCooldown", 1f / WendigoAttackRate);
		ScaleFloatField(t, "grabCooldown", 1f / WendigoAttackRate);
		ScaleFloatField(t, "dashSpeed", WendigoMoveRate);
		ScaleFloatField(t, "dashDuration", 1f / WendigoMoveRate);
		if (t.Field("followerEntity").FieldExists())
		{
			object follower = t.Field("followerEntity").GetValue();
			if (follower != null)
			{
				Traverse ft = Traverse.Create(follower);
				ScaleFloatField(ft, "maxSpeed", WendigoMoveRate);
			}
		}
		Plugin.DBG("CHASER-TUNE", "wendigo attack x" + WendigoAttackRate.ToString("0.##") + ", move x" + WendigoMoveRate.ToString("0.##"));
	}

	private static void ScaleFloatField(Traverse t, string field, float mult)
	{
		if (!t.Field(field).FieldExists())
		{
			return;
		}
		float value = t.Field(field).GetValue<float>();
		if (value > 0f)
		{
			t.Field(field).SetValue(value * mult);
		}
	}

	private static MonoBehaviour GetChaserAi(GameObject enemy)
	{
		if (enemy == null)
		{
			return null;
		}
		DragonEnemyAI dragon = enemy.GetComponent<DragonEnemyAI>();
		if (dragon != null)
		{
			return dragon;
		}
		return enemy.GetComponent<ProximityDragonEnemyAI>();
	}

	private static bool IsGrabbedByThisEnemy(GameObject enemy)
	{
		GrabScreen grabScreen = GrabScreen.Instance;
		if (grabScreen == null || !grabScreen.IsGrabbed || enemy == null)
		{
			return false;
		}
		GameObject grabbing = grabScreen.GrabbingEnemy;
		if (grabbing == null)
		{
			return false;
		}
		if (grabbing == enemy)
		{
			return true;
		}
		return grabbing.transform.IsChildOf(enemy.transform) || enemy.transform.IsChildOf(grabbing.transform);
	}

	private static void PlayHurtFeedback(MonoBehaviour ai, Traverse t, int damage)
	{
		PlayClip(ai, t, "hurtSound");
		GameObject hitEffect = t.Field("hitEffect").FieldExists() ? t.Field("hitEffect").GetValue<GameObject>() : null;
		if (hitEffect != null)
		{
			OverkeksMode.InstantiateOwned(hitEffect, ai.transform.position + Vector3.up, Quaternion.identity);
		}
		if (DamageNumberManager.Instance != null)
		{
			DamageNumberManager.Instance.SpawnDamageNumber(ai.transform.position, damage);
		}
		if (t.Method("FlashDamageEffect", Array.Empty<object>()).MethodExists())
		{
			object flash = t.Method("FlashDamageEffect", Array.Empty<object>()).GetValue();
			if (flash is IEnumerator routine)
			{
				ai.StartCoroutine(routine);
			}
		}
	}

	private static void PlayClip(MonoBehaviour ai, Traverse t, string fieldName)
	{
		if (!t.Field(fieldName).FieldExists())
		{
			return;
		}
		AudioClip clip = t.Field(fieldName).GetValue<AudioClip>();
		if (clip == null)
		{
			return;
		}
		AudioSource source = ai.GetComponent<AudioSource>();
		if (source != null)
		{
			source.PlayOneShot(clip);
		}
	}
}
