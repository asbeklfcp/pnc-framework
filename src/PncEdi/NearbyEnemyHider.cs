using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PncEdi;

internal static class NearbyEnemyHider
{
	private struct HiddenEntry
	{
		public GameObject Root;
		public bool WasActive;
	}

	private static readonly List<HiddenEntry> Hidden = new List<HiddenEntry>();

	internal unsafe static void HideForScene(Vector3 origin, GameObject primaryEnemy = null, bool hidePrimary = true)
	{
		if (Plugin.GameplayTweaksEnabled && (OverkeksMode.UsesRecipe ? OverkeksConfig.CfgHideNearbyEnemiesOnScene : Plugin.CfgHideNearbyEnemiesOnScene).Value)
		{
			RestoreAll();
			float hideNearbyEnemyRadius = (OverkeksMode.UsesRecipe ? OverkeksConfig.CfgHideNearbyEnemyRadius : Plugin.CfgHideNearbyEnemyRadius).Value;
			HashSet<int> seen = new HashSet<int>();
			if (primaryEnemy != null && VanillaGrabHiding.Handles(primaryEnemy))
			{
				// 0.3.1 hides and restores the grabbed enemy itself, and registers it with the
				// grab screen so EndGrabHidden brings it back. Hiding it here as well would put it
				// in this list too, and RestoreAll would then reactivate it a second time on top
				// of vanilla's restore. Claim its id so the radius sweep below skips it as well.
				GameObject vanillaOwned = ResolveEnemyRoot(primaryEnemy);
				if (vanillaOwned != null)
				{
					seen.Add(vanillaOwned.GetInstanceID());
				}
			}
			else if (hidePrimary && primaryEnemy != null)
			{
				TryHide(primaryEnemy, seen, ignoreDistance: true);
			}
			for (int i = 0; i < EnemyAiTypes.All.Length; i++)
			{
				CollectNearby(EnemyAiTypes.All[i], origin, hideNearbyEnemyRadius, seen);
			}
			CollectNearby(typeof(MimicEnemy), origin, hideNearbyEnemyRadius, seen);
			if (Hidden.Count > 0)
			{
				// The AudioSource count is here for the silent-scene question (§119). Deactivating
				// an object silences every source under it, so a hide that carries sources is a
				// candidate for the `Can not play a disabled audio source` warning that follows
				// this line in a run - and a hide that carries none rules the mod's hider out and
				// leaves whoever else switched the object off.
				Plugin.DBG("SCENE-HIDE", "hid " + Hidden.Count + " enemy object(s) carrying "
					+ CountAudioSources() + " audio source(s) near "
					+ ((object)(*(Vector3*)(&origin))/*cast due to constrained. prefix*/).ToString());
			}
		}
	}

	private static int CountAudioSources()
	{
		int total = 0;
		for (int i = 0; i < Hidden.Count; i++)
		{
			GameObject root = Hidden[i].Root;
			if (root == null)
			{
				continue;
			}
			AudioSource[] sources = root.GetComponentsInChildren<AudioSource>(true);
			if (sources != null)
			{
				total += sources.Length;
			}
		}
		return total;
	}

	internal static void RestoreAll()
	{
		if (Hidden.Count != 0)
		{
			List<HiddenEntry> hiddenEntries = new List<HiddenEntry>(Hidden);
			int count = hiddenEntries.Count;
			Hidden.Clear();
			RestoreBatch(hiddenEntries);
			Plugin.DBG("SCENE-SHOW", "revealed " + count + " enemy object(s)" + (EnemyReactivationHelper.HasReactivationDelay ? ", AI wake scheduled" : ""));
		}
	}

	private static void RestoreBatch(List<HiddenEntry> batch)
	{
		for (int i = 0; i < batch.Count; i++)
		{
			HiddenEntry hiddenEntry = batch[i];
			if (!(hiddenEntry.Root == null) && hiddenEntry.WasActive)
			{
				EnemyReactivationHelper.ScheduleReactivate(hiddenEntry.Root);
			}
		}
	}

	private static void CollectNearby(Type type, Vector3 origin, float radius, HashSet<int> seen)
	{
		Object[] instances = Object.FindObjectsByType(type, (FindObjectsInactive)0, (FindObjectsSortMode)0);
		float radiusSquared = radius * radius;
		foreach (Component component in instances)
		{
			if (!(component == null))
			{
				GameObject gameObject = component.gameObject;
				Vector3 offset = gameObject.transform.position - origin;
				if (offset.sqrMagnitude <= radiusSquared)
				{
					TryHide(gameObject, seen, ignoreDistance: false);
				}
			}
		}
	}

	private static void TryHide(GameObject candidate, HashSet<int> seen, bool ignoreDistance)
	{
		GameObject gameObject = ResolveEnemyRoot(candidate);
		if (!(gameObject == null) && !ShouldSkipHide(gameObject))
		{
			int instanceID = gameObject.GetInstanceID();
			if (seen.Add(instanceID))
			{
				Hidden.Add(new HiddenEntry
				{
					Root = gameObject,
					WasActive = gameObject.activeSelf
				});
				gameObject.SetActive(false);
			}
		}
	}

	private static GameObject ResolveEnemyRoot(GameObject candidate)
	{
		if (candidate == null)
		{
			return null;
		}
		if (HasEnemyComponent(candidate))
		{
			return candidate;
		}
		MonoBehaviour ai = EnemyAiTypes.FirstInParent(candidate);
		if (ai != null)
		{
			return ai.gameObject;
		}
		MimicEnemy mimic = candidate.GetComponentInParent<MimicEnemy>();
		if (mimic != null)
		{
			return mimic.gameObject;
		}
		return candidate;
	}

	private static bool HasEnemyComponent(GameObject go)
	{
		return EnemyAiTypes.Any(go) || go.GetComponent<MimicEnemy>() != null;
	}

	private static bool ShouldSkipHide(GameObject root)
	{
		if (root.CompareTag("Player"))
		{
			return true;
		}
		GameObject gameObject = GameObject.FindGameObjectWithTag("Player");
		if (gameObject != null && root.transform.IsChildOf(gameObject.transform))
		{
			return true;
		}
		if (root.GetComponentInParent<CameraSwapTrigger>() != null)
		{
			return true;
		}
		if (GrappleEnemies.IsClingingNow((Object)(object)root))
		{
			return true;
		}
		return false;
	}
}
