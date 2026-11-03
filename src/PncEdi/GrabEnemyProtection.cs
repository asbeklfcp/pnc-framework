using UnityEngine;

namespace PncEdi;

internal static class GrabEnemyProtection
{
	private static GameObject _protectedEnemy;
	private static float _protectedUntil;
	internal static bool Enabled => Plugin.GameplayTweaksEnabled && (OverkeksMode.UsesRecipe ? OverkeksConfig.CfgKeepEnemiesAfterGrab : Plugin.CfgKeepEnemiesAfterGrab).Value;

	internal static void OnGrabStarted(GameObject enemy)
	{
		_protectedEnemy = enemy;
		_protectedUntil = float.PositiveInfinity;
		// Record the health it goes into the scene with, so a kill landed *during* the
		// scene is restored to that rather than to half of max.
		EnemyKeepAliveHelper.SnapshotHealth(enemy);
	}

	internal static void OnGrabEnded()
	{
		if (_protectedEnemy != null)
		{
			_protectedUntil = Time.time + (OverkeksMode.UsesRecipe ? OverkeksConfig.CfgKeepEnemiesAfterGrabGraceSeconds : Plugin.CfgKeepEnemiesAfterGrabGraceSeconds).Value;
		}
	}

	internal static void Clear()
	{
		_protectedEnemy = null;
		_protectedUntil = 0f;
	}

	// Protection while the scene is actually on screen, excluding the post-scene grace.
	// The grace exists so the grab teardown's delayed Destroy cannot delete the enemy; it
	// must not also make the enemy immune to the *player*, which is what happens if Die is
	// blocked during it - each killing blow gets rolled back and the health bar jumps up.
	internal static bool IsProtectedDuringScene(Object target)
	{
		if(CustomEnemyBridge.IsPackageTeardown(target) || OverkeksMode.IsTeardown(target)) return false;
		if (GrappleEnemies.IsClingingNow(target))
		{
			return true;
		}
		if (!float.IsPositiveInfinity(_protectedUntil))
		{
			return false;
		}
		return IsProtected(target);
	}

	internal static bool IsProtected(Object target)
	{
		if(CustomEnemyBridge.IsPackageTeardown(target) || OverkeksMode.IsTeardown(target)) return false;
		if (GrappleEnemies.IsClingingNow(target))
		{
			return true;
		}
		if (!Enabled || target == (Object)null || _protectedEnemy == null || Time.time > _protectedUntil)
		{
			return false;
		}
		GameObject gameObject = (GameObject)(object)((target is GameObject) ? target : null);
		if (gameObject == null)
		{
			Component component = (Component)(object)((target is Component) ? target : null);
			if (component != null)
			{
				gameObject = component.gameObject;
			}
		}
		if (gameObject != null && GrabStruggleHooks.IsChaserBossGrabEnemy(gameObject))
		{
			return false;
		}
		if (gameObject == null)
		{
			return false;
		}
		if ((Object)(object)gameObject == (Object)(object)_protectedEnemy)
		{
			return true;
		}
		return gameObject.transform.IsChildOf(_protectedEnemy.transform);
	}
}
