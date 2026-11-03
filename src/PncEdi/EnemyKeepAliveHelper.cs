using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PncEdi;

internal static class EnemyKeepAliveHelper
{
	// Health each AI had when its scene began, keyed by component instance id. Vanilla's
	// RemoveEnemyAfterGrab never touches currentHealth - it just marks the AI dead and
	// destroys the object - so a revive only ever happens to an enemy that was actually
	// killed during the scene. Restoring half of max handed the player's kill back with
	// interest; restoring the snapshot reads as "the scene interrupted the fight".
	private static readonly Dictionary<int, int> HealthSnapshots = new Dictionary<int, int>();

	internal static void SnapshotHealth(GameObject enemy)
	{
		if (!(enemy == null))
		{
			foreach (MonoBehaviour ai in EnemyAiTypes.On(enemy))
			{
				SnapshotHealthForAi(ai);
			}
		}
	}

	private static void SnapshotHealthForAi(MonoBehaviour ai)
	{
		if (!(ai == null))
		{
			Traverse traverse = Traverse.Create((object)ai);
			if (traverse.Field("currentHealth").FieldExists())
			{
				int currentHealth = traverse.Field("currentHealth").GetValue<int>();
				if (currentHealth > 0)
				{
					HealthSnapshots[ai.GetInstanceID()] = currentHealth;
				}
			}
		}
	}

	internal static void ResetForScene()
	{
		HealthSnapshots.Clear();
	}

	// "Snapshot" (default) = the health it had when the scene started; a number = that
	// percent of max, with 0 meaning "leave it dead".
	private static int ResolveReviveHealth(MonoBehaviour ai, int maxHealth)
	{
		string configured = (OverkeksMode.UsesRecipe ? OverkeksConfig.CfgGrabReviveHealth : Plugin.CfgGrabReviveHealth)?.Value?.Trim();
		if (string.IsNullOrEmpty(configured) || configured.Equals("Snapshot", StringComparison.OrdinalIgnoreCase))
		{
			if (HealthSnapshots.TryGetValue(ai.GetInstanceID(), out var value))
			{
				return Mathf.Clamp(value, 1, Mathf.Max(1, maxHealth));
			}
			return Mathf.Max(1, maxHealth / 2);
		}
		if (float.TryParse(configured, out var parsed))
		{
			if (parsed <= 0f)
			{
				return 0;
			}
			return Mathf.Clamp(Mathf.RoundToInt((float)maxHealth * parsed / 100f), 1, Mathf.Max(1, maxHealth));
		}
		return Mathf.Max(1, maxHealth / 2);
	}

	internal static void RestoreEnemyHealth(GameObject enemy)
	{
		if (!(enemy == null) && Plugin.GameplayTweaksEnabled && (OverkeksMode.UsesRecipe ? OverkeksConfig.CfgKeepEnemiesAfterGrab : Plugin.CfgKeepEnemiesAfterGrab).Value)
		{
			foreach (MonoBehaviour ai in EnemyAiTypes.On(enemy))
			{
				RestoreHealthForAi(ai);
			}
		}
	}

	private static void RestoreHealthForAi(MonoBehaviour ai)
	{
		if (ai == null)
		{
			return;
		}
		Traverse traverse = Traverse.Create((object)ai);
		int maxHealth = traverse.Field("maxHealth").GetValue<int>();
		int currentHealth = traverse.Field("currentHealth").GetValue<int>();
		if (currentHealth <= 0)
		{
			int reviveHealth = ResolveReviveHealth(ai, maxHealth);
			if (reviveHealth <= 0)
			{
				// Configured to leave kills dead: nothing to restore, and isDead stays set
				// so the reactivation and autofix passes keep skipping it.
				return;
			}
			HealthSnapshots.Remove(ai.GetInstanceID());
			traverse.Field("currentHealth").SetValue((object)reviveHealth);
			traverse.Field("isDead").SetValue((object)false);
			EnemyHealthBar healthBar = traverse.Field("healthBar").GetValue<EnemyHealthBar>();
			if (healthBar != null && maxHealth > 0)
			{
				healthBar.UpdateHealth((float)reviveHealth / (float)maxHealth);
			}
			Plugin.DBG("ENEMY-REVIVE", NameRemap.StripCloneSuffix(ai.gameObject.name) + " " + reviveHealth + "/" + maxHealth);
		}
	}

	internal static void ClearDeadState(MonoBehaviour ai)
	{
		if (ai == null)
		{
			return;
		}
		Traverse traverse = Traverse.Create((object)ai);
		// BrawlerEnemyAI is the one class that can be parked in Dead without isDead ever being
		// set: RemoveEnemyAfterGrab calls TransitionToDead directly, and only Die() writes the
		// flag. Keying the reset on isDead alone therefore left a serpent standing in a state
		// its own UpdateStateMachine has no case for - no coroutines, no movement (§76).
		if (ai is BrawlerEnemyAI)
		{
			if (traverse.Field("isDead").GetValue<bool>())
			{
				traverse.Field("isDead").SetValue((object)false);
			}
			if (traverse.Field("currentState").GetValue<BrawlerEnemyAI.AIState>() == (BrawlerEnemyAI.AIState)5)
			{
				traverse.Field("currentState").SetValue((object)(BrawlerEnemyAI.AIState)0);
			}
			return;
		}
		if (traverse.Field("isDead").GetValue<bool>())
		{
			traverse.Field("isDead").SetValue((object)false);
			if (ai is EnemyAI)
			{
				traverse.Field("currentState").SetValue((object)(EnemyAI.AIState)0);
			}
			else if (ai is ChargingEnemyAI)
			{
				traverse.Field("currentState").SetValue((object)(ChargingEnemyAI.AIState)1);
			}
			else if (ai is SpinningEnemyAI)
			{
				traverse.Field("currentState").SetValue((object)(SpinningEnemyAI.AIState)0);
			}
			else if (ai is DragonEnemyAI)
			{
				traverse.Field("currentState").SetValue((object)(DragonEnemyAI.AIState)0);
			}
			else if (ai is ProximityDragonEnemyAI)
			{
				traverse.Field("currentState").SetValue((object)(ProximityDragonEnemyAI.AIState)0);
			}
			else if (ai is ProjectileEnemyAI)
			{
				traverse.Field("currentState").SetValue((object)(ProjectileEnemyAI.AIState)0);
			}
		}
	}
}
