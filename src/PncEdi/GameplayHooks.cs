using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;

namespace PncEdi;

[HarmonyPatch]
public static class GameplayHooks
{
	private static bool _convertingBlockedDamageToHeat;

	// Reusable empties backing the scattered spawn points. Pooled because
	// GetAvailableSpawnPositions runs per spawn wave and the spawner consumes the
	// Transforms immediately.
	private static readonly List<Transform> ScatterPool = new List<Transform>();
	private static GameObject _scatterRoot;
	private static int _scatterUsed;

	// How much of the room's own spawn list to use, from no locks to full lock.
	//
	// This used to be `SpawnCountMultiplier * (1 + 1.2 * progress)`, a base times a ramp, which
	// made the interesting number - what a room costs at full lock - a product nobody had
	// written down: 2 x 2.2 = 4.4x, against a mean vanilla room of two enemies. Stating both
	// ends directly instead means the ceiling is a setting rather than an emergent figure, and
	// the two are independent: changing the base no longer drags the ceiling with it.
	//
	// `CurrentLockProgress` is locks/total and the total is derived from the character's own max
	// heat, so a character with more locks ramps in smaller steps over more of them and one with
	// fewer takes bigger steps - the ends match either way, which is the point of interpolating
	// on progress rather than on the lock count.
	private static float SpawnMultiplier
	{
		get
		{
			if (!(Plugin.GameplayTweaksEnabled && !OverkeksMode.Active))
			{
				return 1f;
			}
			float atNoLocks = Mathf.Max(1f, Plugin.CfgSpawnCountMultiplier.Value);
			// A ceiling under the base would shrink rooms as the run got harder, which is
			// backwards; clamp rather than honour it.
			float atFullLock = Mathf.Max(atNoLocks, Plugin.CfgSpawnCountMultiplierAtFullLock.Value);
			return Mathf.Lerp(atNoLocks, atFullLock, HeatLockSystem.CurrentLockProgress);
		}
	}

	// The extra slots used to be filled by re-adding the *same* Transform, so two enemies
	// were handed identical coordinates and spawned inside each other - most visibly imps,
	// which ImpSpawnWeightMultiplier makes the likeliest pick. The spawner consumes
	// Transforms, so a duplicate cannot simply be nudged; each extra slot needs a real
	// Transform somewhere valid nearby.
	internal static void ExpandSpawnPositions(List<Transform> positions, float multiplier)
	{
		if (positions == null || positions.Count == 0 || multiplier <= 1f)
		{
			return;
		}
		int count = positions.Count;
		// Round the fraction by rolling it, not by always taking the ceiling. A room of two points
		// at 1.25x wants 2.5 enemies; ceiling makes that 3 every single wave, so the first lock
		// acquired reads as a permanent +1 and the whole ramp between the two configured ends is
		// spent at its top. Rolling the 0.5 gives the intended average and keeps waves varied.
		float exact = (float)count * multiplier;
		int wanted = Mathf.FloorToInt(exact);
		if (Random.value < exact - (float)wanted)
		{
			wanted++;
		}
		wanted = Mathf.Max(count, wanted);
		int extra = wanted - count;
		if (extra <= 0)
		{
			return;
		}
		_scatterUsed = 0;
		int dropped = 0;
		for (int i = 0; i < extra; i++)
		{
			Transform transform = CreateScatteredPoint(positions[i % count]);
			if (transform == null)
			{
				// Nowhere valid nearby, so drop the slot entirely. These are bonus enemies
				// on top of what the level asked for; one fewer is better than two inside
				// each other, and better than one inside a wall.
				dropped++;
			}
			else
			{
				positions.Add(transform);
			}
		}
		if (dropped > 0)
		{
			Plugin.DBG("SPAWN", "dropped " + dropped + "/" + extra + " extra spawn slot(s) with no room to scatter into");
		}
	}

	private static Transform CreateScatteredPoint(Transform source)
	{
		if (source == null)
		{
			return null;
		}
		Vector3 position = source.position;
		float radius = Mathf.Max(0.5f, Plugin.CfgSpawnScatterRadius.Value);
		float innerRadius = radius * 0.4f;
		int noFloor = 0;
		int blocked = 0;
		int walled = 0;
		for (int i = 0; i < 8; i++)
		{
			float f = Random.Range(0f, 360f) * ((float)System.Math.PI / 180f);
			float distance = Random.Range(innerRadius, radius);
			Vector3 candidate = position + new Vector3(Mathf.Cos(f) * distance, 0f, Mathf.Sin(f) * distance);
			// Snap to the real floor - the spawn point's own Y is not valid ground a couple
			// of metres away. Same correction section 8.2 applied to TryGetSpawnTransform.
			if (!Physics.Raycast(candidate + Vector3.up * 3f, Vector3.down, out var hitInfo, 12f, -5, (QueryTriggerInteraction)1))
			{
				noFloor++;
				continue;
			}
			candidate.y = hitInfo.point.y;
			// Body-sized clearance, which also rejects a point already occupied by another
			// enemy - the whole point of the exercise. The lower sphere's centre must sit
			// higher than its own radius above the snapped floor, or the capsule dips below
			// the ground and collides with it every single time.
			if (Physics.CheckCapsule(candidate + Vector3.up * 0.6f, candidate + Vector3.up * 1.7f, 0.3f, -5, (QueryTriggerInteraction)1))
			{
				blocked++;
				continue;
			}
			// Reject anything behind a wall from the original point.
			if (Physics.Linecast(position + Vector3.up, candidate + Vector3.up, -5, (QueryTriggerInteraction)1))
			{
				walled++;
				continue;
			}
			return TakeScatterPoint(candidate, source.rotation);
		}
		Plugin.DBG("SPAWN", "no scatter point after 8 tries (no floor " + noFloor + ", blocked " + blocked + ", behind wall " + walled + ")");
		return null;
	}

	private static Transform TakeScatterPoint(Vector3 position, Quaternion rotation)
	{
		if (_scatterRoot == null)
		{
			_scatterRoot = new GameObject("PncEdi_SpawnScatter");
		}
		Transform point;
		if (_scatterUsed < ScatterPool.Count && ScatterPool[_scatterUsed] != null)
		{
			point = ScatterPool[_scatterUsed];
		}
		else
		{
			GameObject gameObject = new GameObject("scatter_" + _scatterUsed);
			gameObject.transform.SetParent(_scatterRoot.transform, false);
			point = gameObject.transform;
			if (_scatterUsed < ScatterPool.Count)
			{
				ScatterPool[_scatterUsed] = point;
			}
			else
			{
				ScatterPool.Add(point);
			}
		}
		_scatterUsed++;
		point.position = position;
		point.rotation = rotation;
		return point;
	}

	internal static void RelaxSpawnFilters(MonoBehaviour spawner, float multiplier)
	{
		if (!(multiplier <= 1f) && !(spawner == null))
		{
			Traverse traverse = Traverse.Create((object)spawner);
			float minDistanceFromPlayer = traverse.Field("minDistanceFromPlayer").GetValue<float>();
			traverse.Field("minDistanceFromPlayer").SetValue((object)Mathf.Max(0.5f, minDistanceFromPlayer / multiplier));
		}
	}

	private static void EnsureFallbackSpawnPositions(MonoBehaviour spawner, List<Transform> positions)
	{
		if (positions == null || positions.Count > 0 || spawner == null)
		{
			return;
		}
		Transform[] spawnPositions = Traverse.Create((object)spawner).Field("spawnPositions").GetValue<Transform[]>();
		if (spawnPositions == null)
		{
			return;
		}
		foreach (Transform transform in spawnPositions)
		{
			if (transform != null && transform.gameObject.activeInHierarchy)
			{
				positions.Add(transform);
			}
		}
		if (positions.Count > 0)
		{
			Plugin.DBG("SPAWN", NameRemap.StripCloneSuffix(spawner.gameObject.name) + ": fallback " + positions.Count + " spawn point(s)");
		}
	}

	private static void ApplySpawnMultiplier(MonoBehaviour spawner, ref List<Transform> positions)
	{
		if ((Plugin.GameplayTweaksEnabled && !OverkeksMode.Active))
		{
			float spawnMultiplier = SpawnMultiplier;
			EnsureFallbackSpawnPositions(spawner, positions);
			int before = ((positions != null) ? positions.Count : 0);
			ExpandSpawnPositions(positions, spawnMultiplier);
			// The ramp is otherwise invisible: below 1x ExpandSpawnPositions returns without
			// touching anything, so "the floor is vanilla again" and "the patch never ran"
			// look identical in a log. Print the figure and both ends of the point list.
			Plugin.DBG("SPAWN", "wave x" + spawnMultiplier.ToString("0.00") + " at lock progress "
				+ HeatLockSystem.CurrentLockProgress.ToString("0.00") + ": " + before + " -> "
				+ ((positions != null) ? positions.Count : 0) + " spawn point(s)");
		}
	}

	[HarmonyPatch(typeof(EnemySpawner), "Awake")]
	[HarmonyPostfix]
	public static void EnemySpawner_Awake_Postfix(EnemySpawner __instance)
	{
		RelaxSpawnFilters((MonoBehaviour)(object)__instance, SpawnMultiplier);
	}

	[HarmonyPatch(typeof(EnemySpawner), "GetAvailableSpawnPositions")]
	[HarmonyPostfix]
	public static void EnemySpawner_GetPositions_Postfix(EnemySpawner __instance, ref List<Transform> __result)
	{
		ApplySpawnMultiplier((MonoBehaviour)(object)__instance, ref __result);
	}

	[HarmonyPatch(typeof(ArenaEnemySpawner), "Awake")]
	[HarmonyPostfix]
	public static void ArenaEnemySpawner_Awake_Postfix(ArenaEnemySpawner __instance)
	{
		RelaxSpawnFilters((MonoBehaviour)(object)__instance, SpawnMultiplier);
	}

	[HarmonyPatch(typeof(ArenaEnemySpawner), "GetAvailableSpawnPositions")]
	[HarmonyPostfix]
	public static void ArenaEnemySpawner_GetPositions_Postfix(ArenaEnemySpawner __instance, ref List<Transform> __result)
	{
		ApplySpawnMultiplier((MonoBehaviour)(object)__instance, ref __result);
	}

	[HarmonyPatch(typeof(PlayerClassManager), "ApplyStatModifiers")]
	[HarmonyPostfix]
	public static void ApplyStatModifiers_Postfix(PlayerClassManager __instance, PlayerStats playerStats)
	{
		// Unconditional, and before the gate: a profile switch has to be able to put back what the
		// game gave the player, and only a run that is *not* being modified can be observed for it.
		GameplayProfiles.ObserveVanillaStats(playerStats);
		// God Mode has no use for a resized heat bar and Vanilla must not have one, so the profile
		// answers this rather than the bare tweaks gate. The gate stays on the base-health record,
		// which the lock system needs under any profile that can build locks at all.
		if (GameplayProfiles.UsesClassHeatScaling)
		{
			ClassHeatMultipliers.ApplyToPlayerHeat((__instance != null) ? __instance.GetSelectedClass() : null, playerStats);
		}
		if (Plugin.GameplayTweaksEnabled)
		{
			HeatLockSystem.RecordBaseHealth(playerStats);
		}
		GameplayProfiles.EnsureNamedGodModeHealth(playerStats);
		OverkeksMode.ApplyPlayer(playerStats);
	}

	[HarmonyPatch(typeof(PlayerStats), "Awake")]
	[HarmonyPostfix]
	public static void PlayerStats_Awake_Postfix(PlayerStats __instance)
	{
		GameplayProfiles.ObserveVanillaStats(__instance);
		if (Plugin.GameplayTweaksEnabled)
		{
			HeatLockSystem.RecordBaseHealth(__instance);
			if (GameplayProfiles.Current == GameplayProfile.Custom && Plugin.GodModeEnabled && !HeatLockSystem.Enabled)
			{
				int maxHealth = Mathf.Max(1, (OverkeksMode.UsesRecipe ? OverkeksConfig.CfgPlayerMaxHealth : Plugin.CfgPlayerMaxHealth).Value);
				__instance.SetMaxHealth(maxHealth, true);
			}
		}
		GameplayProfiles.EnsureNamedGodModeHealth(__instance);
		OverkeksMode.ApplyPlayer(__instance);
	}

	[HarmonyPatch(typeof(PlayerStats), "GenerateHeat")]
	[HarmonyPrefix]
	public static void GenerateHeat_Prefix(ref float amount)
	{
		if (Plugin.GameplayTweaksEnabled && !_convertingBlockedDamageToHeat && !OverkeksHeatSceneGate.ApplyingCombatHeat)
		{
			float enemyHeatGainRate = (OverkeksMode.UsesRecipe ? OverkeksConfig.CfgEnemyHeatGainRate : Plugin.CfgEnemyHeatGainRate).Value;
			if (!(enemyHeatGainRate >= 1f) && !(amount <= 0f) && IsEnemyHeatContext())
			{
				amount *= enemyHeatGainRate;
			}
		}
	}

	[HarmonyPatch(typeof(PlayerStats), "GenerateHeat")]
	[HarmonyPostfix]
	public static void GenerateHeat_Postfix(PlayerStats __instance, float amount)
	{
		Plugin.NotePlayerCum(__instance);
		if (Plugin.GameplayTweaksEnabled && !(amount <= 0f) && !_convertingBlockedDamageToHeat && !IsEnemyHeatContext())
		{
			EnemyReactivationHelper.TryWakeOnPlayerAttack();
		}
	}

	// Both TakeDamage and TakeRawDamage delegate here in the verified 0.4.2
	// assembly. Intercept once so raw damage cannot bypass named God Mode.
	[HarmonyPatch(typeof(PlayerStats), "ApplyDamage")]
	[HarmonyPrefix]
	public static bool TakeDamage_Prefix(PlayerStats __instance, int damage)
	{
		// Before NotePlayerDamage on purpose: suppressed cum damage should not register
		// with the damage-filler tracker either.
		if (CumDamageGate.Suppressing)
		{
			return false;
		}
		Plugin.NotePlayerDamage(__instance, damage);
		if (OverkeksMode.Active) return true;
		if (!Plugin.GameplayTweaksEnabled || !Plugin.GodModeEnabled)
		{
			return true;
		}
		if ((OverkeksMode.UsesRecipe ? OverkeksConfig.CfgConvertBlockedDamageToHeat : Plugin.CfgConvertBlockedDamageToHeat).Value && damage > 0 && __instance != null)
		{
			_convertingBlockedDamageToHeat = true;
			try
			{
				__instance.GenerateHeat((float)damage);
			}
			finally
			{
				_convertingBlockedDamageToHeat = false;
			}
		}
		return false;
	}

	[HarmonyPatch(typeof(PlayerStats), "SetHealth")]
	[HarmonyPrefix]
	public static bool SetHealth_Prefix(PlayerStats __instance, ref int health)
	{
		if (__instance != null && Plugin.GameplayTweaksEnabled && Plugin.GodModeEnabled && health < __instance.CurrentHealth)
		{
			return false;
		}
		if (health <= 0 && ShouldBlockLethalPlayerState())
		{
			return false;
		}
		return true;
	}

	[HarmonyPatch(typeof(PlayerStats), "Die")]
	[HarmonyPrefix]
	public static bool Die_Prefix()
	{
		if (ShouldBlockLethalPlayerState())
		{
			return false;
		}
		return true;
	}

	private static bool ShouldBlockLethalPlayerState()
	{
		return DragonGrabGameOverGuard.ShouldBlockLethalHealth();
	}

	private static bool IsEnemyHeatContext()
	{
		GrappleScreenobject grappleScreenobject = GrappleScreenobject.Instance;
		if (grappleScreenobject != null && grappleScreenobject.IsGrappling)
		{
			return true;
		}
		GrabScreen grabScreen = GrabScreen.Instance;
		return grabScreen != null && grabScreen.IsGrabbed;
	}
}
