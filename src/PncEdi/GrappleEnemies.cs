using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PncEdi;

// What the mod needs to know about the enemies clinging to the player, and nothing more.
//
// This was `GrappleEnemyProtection`, and most of it was a keep-alive: it stowed each clinging
// enemy, held it alive through the grapple and put it back beside the player when it was shaken
// off. That existed because game **0.2.1** destroyed a shaken-off grappler outright -
// `RemoveOneGrappler` ended in `Object.Destroy(gameObject)`.
//
// **Game 0.3.1 replaced that line with `ReleaseGrappler(enemy, shakeSign)`**, which puts the enemy
// at the player's position plus the stored offset and a clearance push, then calls
// `ChargingEnemyAI.ThrowAfterGrapple`: `SetActive(true)`, every child collider and renderer
// re-enabled, charge flags cleared, `currentState = AIState.Idle`, and a rigidbody throw. That is
// the whole of what the keep-alive did and a physics throw on top of it, for every clinging family
// - `StartGrapple` reads `grappleAnimatorController` off `ChargingEnemyAI`, so every grappler is
// one. The mod was not protecting enemies the game destroyed; it was overriding a mechanic the
// game had grown for itself, and hiding it from ever being seen (§114).
//
// Vanilla still destroys grapplers on the paths that are *not* a shake-off - `EndGrapple` clears
// whatever is still clinging when the grapple ends any other way, and the trio-grab overflow
// destroys the non-representatives and then the representative once its grab scene ends. Those are
// the game's own lifecycle too, and are left alone.
internal static class GrappleEnemies
{
	// Which family is clinging to the player, e.g. "imp" or "goonshroom".
	//
	// Game 0.3.1 added a second grappler, and the cling dispatch names its scene as
	// `prefix + count` - so with one hardcoded prefix a goonshroom grapple played imp_1/2/3.
	// That is a *wrong* scene rather than a missing one, which nothing announces, so the family
	// has to come off the enemies actually attached (§66).
	//
	// Returns null when there is nothing to read: the caller keeps the old imp-only behaviour
	// there rather than going silent, since imps were the only grappler that ever existed.
	internal static string ResolveGrappleFamily(GrappleScreenobject grapple)
	{
		List<GameObject> gameObjects = GrapplingEnemies(grapple);
		if (gameObjects == null)
		{
			return null;
		}
		// First readable enemy wins. A mixed grapple is not a thing the game can produce - the
		// UI overlay has one animator per count - and if it ever were, the first is as good an
		// answer as any and the log line names it.
		for (int i = 0; i < gameObjects.Count; i++)
		{
			GameObject gameObject = gameObjects[i];
			if (gameObject == null)
			{
				continue;
			}
			string key = NameRemap.ResolveEnemyKey(gameObject.name);
			if (!string.IsNullOrEmpty(key))
			{
				return key;
			}
		}
		return null;
	}

	internal static List<GameObject> GrapplingEnemies(GrappleScreenobject grapple)
	{
		if (grapple == null)
		{
			return null;
		}
		return Traverse.Create((object)grapple).Field("grapplingEnemies").GetValue<List<GameObject>>();
	}

	internal static bool IsImpEnemy(GameObject enemy)
	{
		if (enemy == null)
		{
			return false;
		}
		if (NameRemap.ResolveEnemyKey(enemy.name) == "imp")
		{
			return true;
		}
		ChargingEnemyAI chargingEnemyAI = enemy.GetComponent<ChargingEnemyAI>();
		if (chargingEnemyAI != null && !string.IsNullOrEmpty(chargingEnemyAI.galleryEnemyID) && chargingEnemyAI.galleryEnemyID.IndexOf("imp", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			return true;
		}
		EnemyAI enemyAI = enemy.GetComponent<EnemyAI>();
		if (enemyAI != null && !string.IsNullOrEmpty(enemyAI.galleryEnemyID) && enemyAI.galleryEnemyID.IndexOf("imp", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			return true;
		}
		return enemy.name.IndexOf("imp", StringComparison.OrdinalIgnoreCase) >= 0;
	}

	// Is this object part of the grapple that is running right now?
	//
	// Every caller wants the same thing: leave a clinging enemy alone. A clinging enemy is
	// deactivated (vanilla's `PerformGrabAttack` does it after `StartGrapple` succeeds), so the
	// autofix, the waker and the hider would each otherwise "rescue" an enemy that is exactly
	// where it belongs. The answer comes off the game's own `grapplingEnemies` list rather than
	// a list of ours: the mod no longer keeps one, and a second list could only disagree.
	internal static bool IsClingingNow(Object target)
	{
		if (target == (Object)null)
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
		if (gameObject == null)
		{
			return false;
		}
		GrappleScreenobject grappleScreenobject = GrappleScreenobject.Instance;
		if (grappleScreenobject == null || !grappleScreenobject.IsGrappling)
		{
			return false;
		}
		List<GameObject> gameObjects = GrapplingEnemies(grappleScreenobject);
		if (gameObjects == null)
		{
			return false;
		}
		for (int i = 0; i < gameObjects.Count; i++)
		{
			GameObject clinging = gameObjects[i];
			if (!(clinging == null) && ((Object)(object)gameObject == (Object)(object)clinging || gameObject.transform.IsChildOf(clinging.transform)))
			{
				return true;
			}
		}
		return false;
	}

	// How many are clinging, for the cling scene name (`prefix + count`).
	internal static int ClingingTier(GrappleScreenobject grapple)
	{
		List<GameObject> gameObjects = GrapplingEnemies(grapple);
		if (gameObjects == null)
		{
			return 0;
		}
		int count = 0;
		for (int i = 0; i < gameObjects.Count; i++)
		{
			if (gameObjects[i] != null)
			{
				count++;
			}
		}
		return Mathf.Clamp(count, 0, 3);
	}

	// The grapple visual is a 2D UI overlay (GrappleScreenobject.grappleUI + the
	// OneImp/TwoImps/ThreeImps animator), not the world model, and vanilla deactivates the world
	// model in `ChargingEnemyAI.PerformGrabAttack` right after `StartGrapple` succeeds. A
	// reinforcement enemy spawned straight into a grapple never charged, so nothing deactivated
	// it and it stands around in the level while its overlay clings to the player. This is the
	// one thing the mod still has to do to a clinging enemy, and it is only ever called on a
	// spawn of ours.
	//
	// Nothing is recorded for a restore: whichever way the grapple ends, vanilla owns the other
	// side. `ThrowAfterGrapple` re-enables every collider and renderer and clears `isKinematic`
	// on the way out, and the other endings destroy the object.
	internal static void StowSpawnedGrappler(GameObject enemy)
	{
		if (enemy == null)
		{
			return;
		}
		Collider[] componentsInChildren = enemy.GetComponentsInChildren<Collider>(true);
		for (int i = 0; i < componentsInChildren.Length; i++)
		{
			componentsInChildren[i].enabled = false;
		}
		Renderer[] componentsInChildren2 = enemy.GetComponentsInChildren<Renderer>(true);
		for (int j = 0; j < componentsInChildren2.Length; j++)
		{
			componentsInChildren2[j].enabled = false;
		}
		ChargingEnemyAI chargingEnemyAI = enemy.GetComponent<ChargingEnemyAI>();
		if (chargingEnemyAI != null)
		{
			Traverse traverse = Traverse.Create((object)chargingEnemyAI);
			traverse.Field("isCharging").SetValue((object)false);
			traverse.Field("hasDealtChargeDamage").SetValue((object)false);
			traverse.Method("StopCharge", Array.Empty<object>()).GetValue();
			traverse.Method("StopPathfinding", Array.Empty<object>()).GetValue();
			chargingEnemyAI.enabled = false;
		}
		// With every collider off there is nothing left to stand on, so a dynamic Rigidbody just
		// integrates gravity and sinks through the floor. Vanilla's SilenceForGrab pins it
		// kinematic for the same reason.
		Rigidbody rigidbody = enemy.GetComponent<Rigidbody>();
		if (rigidbody != null)
		{
			rigidbody.linearVelocity = Vector3.zero;
			rigidbody.angularVelocity = Vector3.zero;
			rigidbody.isKinematic = true;
		}
		// The trio-grab overflow hands one grappler to GrabScreen as the on-screen representative
		// and re-activates it itself; deactivating that one would pull the enemy out from under
		// the live grab scene.
		if (!IsGrabRepresentative(enemy))
		{
			enemy.SetActive(false);
		}
	}

	private static bool IsGrabRepresentative(GameObject enemy)
	{
		GrabScreen grabScreen = GrabScreen.Instance;
		return grabScreen != null && grabScreen.IsGrabbed && (Object)(object)grabScreen.GrabbingEnemy == (Object)(object)enemy;
	}
}
