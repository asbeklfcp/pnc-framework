using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace PncEdi;

/// <summary>
/// Random-pool Dragon / Wendigo (not sweeper chaser bosses): half chaser HP, no restage,
/// ordinary grab treatment, 25% smaller and feet-aligned to the ground.
/// Fodder dragons also close in before swinging (attackRange matches scale), keep a fuller
/// attack hit volume, and deal 3× melee damage so the smaller pack still threatens.
/// </summary>
[HarmonyPatch]
internal static class ChaserFodderSpawn
{
	private const float HealthScale = 0.5f;

	internal static float HealthScalePublic => HealthScale;

	private const float VisualScale = 0.75f;

	/// <summary>Fodder dragon melee damage vs vanilla prefab (stacks with EnemyDamageToPlayerMultiplier).</summary>
	private const float DragonFodderDamageScale = 3f;

	/// <summary>
	/// World-space attack trigger size vs pre-shrink. 1 = restore full-size reach after VisualScale;
	/// &gt;1 expands the hit volume inward so claws still connect when standing closer.
	/// </summary>
	private const float DragonFodderAttackHitWorldScale = 1.2f;


	private static readonly HashSet<int> FodderInstanceIds = new HashSet<int>();

	internal static void ClearCache()
	{
		FodderInstanceIds.Clear();
	}


	internal static bool IsChaserFodderKey(string key)
	{
		if (string.IsNullOrEmpty(key))
		{
			return false;
		}
		return key.Equals("dragon", StringComparison.OrdinalIgnoreCase)
			|| key.Equals("wendigo", StringComparison.OrdinalIgnoreCase);
	}

	internal static bool IsChaserFodderPrefab(GameObject prefab)
	{
		if (prefab == null || !OverkeksChaserBossHelper.IsChaserBossPrefab(prefab))
		{
			return false;
		}
		return IsChaserFodderKey(NameRemap.ResolveEnemyKey(prefab.name));
	}

	internal static bool IsFodder(GameObject enemy)
	{
		if (enemy == null)
		{
			return false;
		}
		if (enemy.GetComponent<ChaserFodderMarker>() != null)
		{
			return true;
		}
		return FodderInstanceIds.Contains(enemy.GetInstanceID());
	}

	/// <summary>
	/// After an H-scene: clear stuck dead/grab flags and re-arm hit proxies so fodder stays killable.
	/// </summary>
	internal static void RecoverAfterGrab(GameObject enemy)
	{
		if (!IsFodder(enemy))
		{
			return;
		}
		foreach (MonoBehaviour ai in EnemyAiTypes.On(enemy))
		{
			if (ai == null)
			{
				continue;
			}
			EnemyKeepAliveHelper.ClearDeadState(ai);
			Traverse t = Traverse.Create((object)ai);
			if (t.Field("hasTriggeredDragonGrab").FieldExists())
			{
				t.Field("hasTriggeredDragonGrab").SetValue(false);
			}
			if (t.Field("hasGrabbedThisAttempt").FieldExists())
			{
				t.Field("hasGrabbedThisAttempt").SetValue(false);
			}
			if (t.Method("ResetGrabFlag", System.Array.Empty<object>()).MethodExists())
			{
				t.Method("ResetGrabFlag", System.Array.Empty<object>()).GetValue();
			}
			if (t.Field("wasPlayerGrabbedLastFrame").FieldExists())
			{
				t.Field("wasPlayerGrabbedLastFrame").SetValue(false);
			}
			// Stuck isDead with HP left = silent invulnerability in ChaserDamageHooks.
			if (t.Field("isDead").FieldExists() && t.Field("isDead").GetValue<bool>())
			{
				t.Field("isDead").SetValue(false);
			}
			if (t.Field("maxHealth").FieldExists() && t.Field("currentHealth").FieldExists())
			{
				int max = t.Field("maxHealth").GetValue<int>();
				if (max <= 0)
				{
					max = Mathf.Max(1, Mathf.CeilToInt(ChaserDamageHooks.ResolveMaxHealth(enemy) * HealthScale));
					t.Field("maxHealth").SetValue(max);
				}
				int current = t.Field("currentHealth").GetValue<int>();
				if (current <= 0)
				{
					t.Field("currentHealth").SetValue(max);
				}
			}
		}
		ChaserDamageHooks.EnsureHitProxies(enemy);
		FodderInstanceIds.Add(enemy.GetInstanceID());
		Plugin.DBG("SPAWN", "chaser fodder recovered after grab '" + NameRemap.StripCloneSuffix(enemy.name) + "'");
	}

	internal static void TryConsumePendingAndConvert(GameObject enemy)
	{
		if (enemy == null || enemy.GetComponent<OverkeksSpawnVariant>()?.Fodder != true || IsFodder(enemy) || !IsChaserFodderPrefab(enemy))
		{
			return;
		}
		if (ChaserSweeperHooks.IsManagedChaser(enemy))
		{
			return;
		}
		ApplyFodder(enemy);
	}

	private static void ApplyFodder(GameObject enemy)
	{
        OverkeksMode.Own(enemy);
		if (enemy.GetComponent<ChaserFodderMarker>() != null)
		{
			FodderInstanceIds.Add(enemy.GetInstanceID());
			return;
		}
		int id = enemy.GetInstanceID();
		FodderInstanceIds.Add(id);
		OverkeksMode.AddOwnedComponent<ChaserFodderMarker>(enemy);
		ApplyHalfHealth(enemy);
		ApplyVisualScaleGrounded(enemy, VisualScale);
		ApplyDragonFodderCombatTuning(enemy);
		ChaserDamageHooks.EnsureHitProxies(enemy);
		Plugin.DBG("SPAWN", "chaser fodder '" + NameRemap.StripCloneSuffix(enemy.name)
			+ "' (hp x" + HealthScale.ToString("0.##")
			+ ", scale x" + VisualScale.ToString("0.##") + ")");
	}

	/// <summary>
	/// Shrink made the attack trigger miss at the old stand-off distance. Fodder dragons only:
	/// stand closer (attackRange × VisualScale), enlarge the attack sphere in world space, damage ×3.
	/// </summary>
	private static void ApplyDragonFodderCombatTuning(GameObject enemy)
	{
		if (enemy == null || GrabStruggleHooks.IsWendigoGrabEnemy(enemy))
		{
			return;
		}
		if (!GrabStruggleHooks.IsDragonGrabEnemy(enemy))
		{
			return;
		}
		DragonEnemyAI dragon = enemy.GetComponent<DragonEnemyAI>();
		if (dragon != null)
		{
			dragon.attackDamage = Mathf.Max(1, Mathf.RoundToInt(dragon.attackDamage * DragonFodderDamageScale));
			dragon.attackRange *= VisualScale;
			ExpandAttackHitCollider(dragon.attackDetectionCollider);
			Plugin.DBG("SPAWN", "dragon fodder combat dmg x" + DragonFodderDamageScale.ToString("0.##")
				+ " attackRange=" + dragon.attackRange.ToString("0.##")
				+ " hitWorld x" + DragonFodderAttackHitWorldScale.ToString("0.##"));
			return;
		}
		ProximityDragonEnemyAI proximity = enemy.GetComponent<ProximityDragonEnemyAI>();
		if (proximity != null)
		{
			proximity.attackDamage = Mathf.Max(1, Mathf.RoundToInt(proximity.attackDamage * DragonFodderDamageScale));
			proximity.attackRange *= VisualScale;
			ExpandAttackHitCollider(proximity.attackDetectionCollider);
			Plugin.DBG("SPAWN", "dragon fodder combat dmg x" + DragonFodderDamageScale.ToString("0.##")
				+ " attackRange=" + proximity.attackRange.ToString("0.##")
				+ " hitWorld x" + DragonFodderAttackHitWorldScale.ToString("0.##"));
		}
	}

	private static void ExpandAttackHitCollider(SphereCollider collider)
	{
		if (collider == null || VisualScale < 0.05f)
		{
			return;
		}
		// Root was scaled by VisualScale; boost local radius so world reach ≈ original × HitWorldScale.
		collider.radius *= DragonFodderAttackHitWorldScale / VisualScale;
	}

	private static void ApplyHalfHealth(GameObject enemy)
	{
		int max = Mathf.Max(1, Mathf.CeilToInt(ChaserDamageHooks.ResolveMaxHealth(enemy) * HealthScale));
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
		Plugin.DBG("SPAWN", "chaser fodder HP now " + max);
	}

	private static void ApplyVisualScaleGrounded(GameObject enemy, float scale)
	{
		if (enemy == null || Mathf.Approximately(scale, 1f))
		{
			return;
		}
		float footY = CombinedRendererMinY(enemy);
		Vector3 localScale = enemy.transform.localScale;
		enemy.transform.localScale = localScale * scale;
		float newFootY = CombinedRendererMinY(enemy);
		Vector3 pos = enemy.transform.position;
		pos.y += footY - newFootY;
		enemy.transform.position = pos;
	}

	private static float CombinedRendererMinY(GameObject root)
	{
		Renderer[] renderers = root.GetComponentsInChildren<Renderer>(includeInactive: true);
		bool any = false;
		Bounds bounds = default;
		for (int i = 0; i < renderers.Length; i++)
		{
			Renderer renderer = renderers[i];
			if (renderer == null || !renderer.enabled)
			{
				continue;
			}
			if (renderer is SpriteRenderer || renderer is MeshRenderer || renderer is SkinnedMeshRenderer)
			{
				if (!any)
				{
					bounds = renderer.bounds;
					any = true;
				}
				else
				{
					bounds.Encapsulate(renderer.bounds);
				}
			}
		}
		if (!any)
		{
			return root.transform.position.y;
		}
		return bounds.min.y;
	}

	[HarmonyPatch(typeof(DragonEnemyAI), "Start")]
	[HarmonyPostfix]
	private static void DragonEnemyAI_Start_Postfix(DragonEnemyAI __instance)
	{
 if(!OverkeksMode.Active) return;
		if (__instance != null)
		{
			TryConsumePendingAndConvert(__instance.gameObject);
		}
	}

	[HarmonyPatch(typeof(ProximityDragonEnemyAI), "Start")]
	[HarmonyPostfix]
	private static void ProximityDragonEnemyAI_Start_Postfix(ProximityDragonEnemyAI __instance)
	{
 if(!OverkeksMode.Active) return;
		if (__instance != null)
		{
			TryConsumePendingAndConvert(__instance.gameObject);
		}
	}

	[HarmonyPatch(typeof(DragonEnemyAI), "OnDestroy")]
	[HarmonyPostfix]
	private static void DragonEnemyAI_OnDestroy_Postfix(DragonEnemyAI __instance)
	{
 if(!OverkeksMode.HooksAllowed) return;
		if (__instance != null)
		{
			FodderInstanceIds.Remove(__instance.gameObject.GetInstanceID());
		}
	}

	[HarmonyPatch(typeof(ProximityDragonEnemyAI), "OnDestroy")]
	[HarmonyPostfix]
	private static void ProximityDragonEnemyAI_OnDestroy_Postfix(ProximityDragonEnemyAI __instance)
	{
 if(!OverkeksMode.HooksAllowed) return;
		if (__instance != null)
		{
			FodderInstanceIds.Remove(__instance.gameObject.GetInstanceID());
		}
	}
}
