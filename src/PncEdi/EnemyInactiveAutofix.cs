using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PncEdi;

internal static class EnemyInactiveAutofix
{
	private static float _nextScanTime;

	internal static void Tick()
	{
		if (!Plugin.GameplayTweaksEnabled || !(OverkeksMode.UsesRecipe ? OverkeksConfig.CfgEnemyInactiveAutofix : Plugin.CfgEnemyInactiveAutofix).Value)
		{
			return;
		}
		float interval = Mathf.Max(5f, (OverkeksMode.UsesRecipe ? OverkeksConfig.CfgEnemyInactiveAutofixInterval : Plugin.CfgEnemyInactiveAutofixInterval).Value);
		if (Time.time < _nextScanTime)
		{
			return;
		}
		_nextScanTime = Time.time + interval;
		if (GrabEndHelper.IsEscapeSceneActive() || IsAnySceneOnScreen())
		{
			return;
		}
		GameObject gameObject = GameObject.FindGameObjectWithTag("Player");
		if (!(gameObject == null))
		{
			Vector3 position = gameObject.transform.position;
			float radius = Mathf.Max(5f, (OverkeksMode.UsesRecipe ? OverkeksConfig.CfgEnemyInactiveAutofixRadius : Plugin.CfgEnemyInactiveAutofixRadius).Value);
			float radiusSq = radius * radius;
			HashSet<int> seen = new HashSet<int>();
			int woken = 0;
			for (int i = 0; i < EnemyAiTypes.All.Length; i++)
			{
				woken += Scan(EnemyAiTypes.All[i], position, radiusSq, seen);
			}
			if (woken > 0)
			{
				Plugin.DBG("ENEMY-AUTOFIX", "reactivated " + woken + " stuck enemy object(s) within " + radius + "m");
			}
		}
	}

	// Every AI pauses itself while a scene is up (PauseAIBehavior), so a scan during one
	// would "fix" enemies that are behaving correctly.
	private static bool IsAnySceneOnScreen()
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
		return CameraSwapHooks.Active || CinematicCameraSwapTrigger.IsAnyInCinematicView;
	}

	private static int Scan(Type aiType, Vector3 origin, float radiusSq, HashSet<int> seen)
	{
		Object[] instances = Object.FindObjectsByType(aiType, (FindObjectsInactive)1, (FindObjectsSortMode)0);
		int woken = 0;
		foreach (Component component in instances)
		{
			if (component == null)
			{
				continue;
			}
			GameObject gameObject = component.gameObject;
			int instanceID = gameObject.GetInstanceID();
			if (seen.Add(instanceID))
			{
				Vector3 vector3 = gameObject.transform.position - origin;
				if (!(vector3.sqrMagnitude > radiusSq) && NeedsAutofix(gameObject))
				{
					EnemyReactivationHelper.Reactivate(gameObject);
					woken++;
					Plugin.DBG("ENEMY-AUTOFIX", NameRemap.StripCloneSuffix(gameObject.name));
				}
			}
		}
		return woken;
	}

	private static bool NeedsAutofix(GameObject root)
	{
		if (root == null || ShouldSkipAutofix(root))
		{
			return false;
		}
		if (!root.activeInHierarchy)
		{
			return !IsDeadEnemy(root);
		}
		// Also catch enemies whose component is enabled and object active, but which are
		// frozen mid-chase with movement simulation off - HasDisabledAi cannot see those.
		return (HasDisabledAi(root) || EnemyReactivationHelper.IsStuckChasing(root) || EnemyReactivationHelper.IsStuckSpinning(root)) && !IsDeadEnemy(root);
	}

	private static bool ShouldSkipAutofix(GameObject root)
	{
		if (root.CompareTag("Player"))
		{
			return true;
		}
		if (EnemyReactivationHelper.IsScheduled(root))
		{
			return true;
		}
		if (GrappleEnemies.IsClingingNow((Object)(object)root))
		{
			return true;
		}
		if (IsAttachedGrappler(root, out var attachedGrapplerTier))
		{
			if (attachedGrapplerTier > 0)
			{
				Plugin.DBG("ENEMY-AUTOFIX", "skipped attached grappler tier=" + attachedGrapplerTier + ": " + NameRemap.StripCloneSuffix(root.name));
			}
			return true;
		}
		if (root.GetComponent<MimicEnemy>() != null)
		{
			return true;
		}
		DragonEnemyAI dragonEnemyAI = root.GetComponent<DragonEnemyAI>();
		if (dragonEnemyAI != null && dragonEnemyAI.frozenByArena)
		{
			return true;
		}
		ProximityDragonEnemyAI proximityDragonEnemyAI = root.GetComponent<ProximityDragonEnemyAI>();
		if (proximityDragonEnemyAI != null && proximityDragonEnemyAI.frozenByArena)
		{
			return true;
		}
		return false;
	}

	private static bool IsAttachedGrappler(GameObject root, out int attachedGrapplerTier)
	{
		attachedGrapplerTier = 0;
		if (!GrappleEnemies.IsClingingNow((Object)(object)root))
		{
			return false;
		}
		attachedGrapplerTier = GrappleEnemies.ClingingTier(GrappleScreenobject.Instance);
		return true;
	}

	private static bool HasDisabledAi(GameObject root)
	{
		bool hasAi = false;
		bool anyEnabled = false;
		foreach (MonoBehaviour ai in EnemyAiTypes.On(root))
		{
			CollectAiState(ai, ref hasAi, ref anyEnabled);
		}
		return hasAi && !anyEnabled;
	}

	private static void CollectAiState(MonoBehaviour ai, ref bool hasAi, ref bool anyEnabled)
	{
		if (!(ai == null))
		{
			hasAi = true;
			if (ai.enabled)
			{
				anyEnabled = true;
			}
		}
	}

	private static bool IsDeadEnemy(GameObject root)
	{
		foreach (MonoBehaviour monoBehaviour in EnemyAiTypes.On(root))
		{
			if (!(monoBehaviour == null))
			{
				Traverse traverse = Traverse.Create((object)monoBehaviour);
				if (traverse.Field("isDead").FieldExists() && traverse.Field("isDead").GetValue<bool>())
				{
					return true;
				}
				if (traverse.Field("currentHealth").FieldExists() && traverse.Field("currentHealth").GetValue<int>() <= 0)
				{
					return true;
				}
			}
		}
		return false;
	}
}
