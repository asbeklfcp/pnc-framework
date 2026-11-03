using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace PncEdi;

[HarmonyPatch]
internal static class ImpGrappleGate
{
	// The family of the grapple currently running ("imp", "goonshroom"), remembered so the gate
	// still knows what is clinging when the enemy list is momentarily unreadable - which it is
	// during teardown, and which is how a goonshroom grapple came to refuse the goonshroom's own
	// grab three times in the 2026-08-23 run (§99). Cleared when the grapple ends and on a scene
	// change.
	private static string _activeFamily;

	internal static bool IsGrappleActive()
	{
		GrappleScreenobject grappleScreenobject = GrappleScreenobject.Instance;
		return grappleScreenobject != null && grappleScreenobject.IsGrappling;
	}

	// Null when nothing is clinging, or when a grapple is running but no attached enemy can be
	// read and none has been seen yet. Callers must treat null as "unknown", not as "imp".
	internal static string ActiveGrappleFamily()
	{
		GrappleScreenobject grappleScreenobject = GrappleScreenobject.Instance;
		if (grappleScreenobject == null || !grappleScreenobject.IsGrappling)
		{
			_activeFamily = null;
			return null;
		}
		string family = GrappleEnemies.ResolveGrappleFamily(grappleScreenobject);
		if (!string.IsNullOrEmpty(family))
		{
			_activeFamily = family;
		}
		return _activeFamily;
	}

	internal static void ResetForNewScene()
	{
		_activeFamily = null;
	}

	// A grapple owns the screen: while one is running, any *other* scene would replace the cling
	// the player is actually in. So the test is "does this enemy belong to the grapple that is
	// already running", and the answer has to come from that grapple's own family.
	//
	// It used to come from GrappleEnemies.IsImpEnemy, which is a question about one
	// family rather than about the running one, so a goonshroom grapple refused the goonshroom's
	// own grab. The refusals of Gargoyle, Hood_Enemy and Zombie_Enemy_ALT1 in that same run were
	// right and still are - they are a different enemy interrupting a cling.
	//
	// Unknown family means the gate declines to block. Blocking on a guess is what produced the
	// wrong refusals; the cost of the other way round is one stray scene in a window that only
	// exists while a grapple tears down.
	internal static bool ShouldBlockNonGrappleSceneFor(GameObject enemy)
	{
		if (!Plugin.GameplayTweaksEnabled || !IsGrappleActive())
		{
			return false;
		}
		string family = ActiveGrappleFamily();
		if (string.IsNullOrEmpty(family))
		{
			return false;
		}
		if (enemy == null)
		{
			return true;
		}
		// IsImpEnemy reads galleryEnemyID and the raw name as well as the key, so it catches imp
		// variants a key lookup alone can miss. It is only consulted when the running grapple is
		// an imp one - the whole point of the change is that it cannot stand in for the others.
		if (family == "imp" && GrappleEnemies.IsImpEnemy(enemy))
		{
			return false;
		}
		string enemyKey = NameRemap.ResolveEnemyKey(enemy.name);
		return string.IsNullOrEmpty(enemyKey) || enemyKey != family;
	}

	internal static void EnsureNormalMovement()
	{
		if (!Plugin.GameplayTweaksEnabled)
		{
			return;
		}
		GrappleScreenobject grappleScreenobject = GrappleScreenobject.Instance;
		if (grappleScreenobject != null && grappleScreenobject.IsGrappling)
		{
			return;
		}
		GrabScreen grabScreen = GrabScreen.Instance;
		if (grabScreen != null && grabScreen.IsGrabbed)
		{
			return;
		}
		GameObject gameObject = GameObject.FindGameObjectWithTag("Player");
		if (!(gameObject == null))
		{
			PlayerStats playerStats = gameObject.GetComponent<PlayerStats>();
			if (playerStats != null)
			{
				playerStats.SetExternalMovementMultiplier(1f);
			}
		}
	}

	[HarmonyPatch(typeof(GrappleScreenobject), "StartGrapple")]
	[HarmonyPrefix]
	[HarmonyPriority(800)]
	private static bool StartGrapple_Prefix(GameObject enemy)
	{
		if (!ShouldBlockNonGrappleSceneFor(enemy))
		{
			return true;
		}
		Plugin.DBG("GRAPPLE-GATE", "blocked " + NameRemap.StripCloneSuffix(enemy.name) + " joining a " + ActiveGrappleFamily() + " grapple");
		return false;
	}

	[HarmonyPatch(typeof(GrabScreen), "StartGrab")]
	[HarmonyPrefix]
	[HarmonyPriority(800)]
	private static bool StartGrab_Prefix(GameObject enemy)
	{
		if (!ShouldBlockNonGrappleSceneFor(enemy))
		{
			return true;
		}
		Plugin.DBG("GRAPPLE-GATE", "blocked grab by " + NameRemap.StripCloneSuffix(enemy.name) + " during a " + ActiveGrappleFamily() + " grapple");
		return false;
	}

	[HarmonyPatch(typeof(CameraSwapTrigger), "ActivateTrigger")]
	[HarmonyPrefix]
	[HarmonyPriority(800)]
	private static bool ActivateTrigger_Prefix()
	{
		// Any cling, not just an imp one: an interact scene would replace whichever cling is on
		// screen, and there is nothing imp-specific about that.
		if (!Plugin.GameplayTweaksEnabled || !IsGrappleActive())
		{
			return true;
		}
		Plugin.DBG("GRAPPLE-GATE", "blocked interact scene during a " + ActiveGrappleFamily() + " grapple");
		return false;
	}

	[HarmonyPatch(typeof(GrappleScreenobject), "RemoveOneGrappler")]
	[HarmonyPostfix]
	private static void RemoveOneGrappler_Movement_Postfix(GrappleScreenobject __instance)
	{
		if (__instance != null && !__instance.IsGrappling)
		{
			EnsureNormalMovement();
		}
	}

	[HarmonyPatch(typeof(GrappleScreenobject), "EndGrapple")]
	[HarmonyPostfix]
	private static void EndGrapple_Movement_Postfix()
	{
		_activeFamily = null;
		EnsureNormalMovement();
	}

	[HarmonyPatch(typeof(GrappleScreenobject), "ForceEndGrapple")]
	[HarmonyPostfix]
	private static void ForceEndGrapple_Movement_Postfix()
	{
		_activeFamily = null;
		EnsureNormalMovement();
	}
}
