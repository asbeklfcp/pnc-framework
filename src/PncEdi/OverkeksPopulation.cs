using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace PncEdi;

[HarmonyPatch]
public static class OverkeksPopulation
{
	internal static void ResetForScene() {
        ScatterPool.Clear(); GapFilledSpawners.Clear(); TriggerExpandedSpawners.Clear();
        GapOccupiedScratch.Clear(); GapAddedScratch.Clear(); _scatterRoot=null; _gapRoot=null; _scatterUsed=0;
    }
    private const float MaxFloorYDelta = 2.25f;

	private static bool _convertingBlockedDamageToHeat;

	private static readonly List<Transform> ScatterPool = new List<Transform>();

	private static readonly HashSet<int> GapFilledSpawners = new HashSet<int>();

	private static readonly HashSet<int> TriggerExpandedSpawners = new HashSet<int>();

	private static readonly List<Vector3> GapOccupiedScratch = new List<Vector3>();

	private static readonly List<Transform> GapAddedScratch = new List<Transform>();

	private static GameObject _scatterRoot;

	private static GameObject _gapRoot;

	private static int _scatterUsed;

	private static float SpawnMultiplier
	{
		get
		{
			if (!OverkeksMode.Active)
			{
				return 1f;
			}
			float a = Mathf.Max(1f, OverkeksConfig.CfgSpawnCountMultiplier.Value);
			float b = Mathf.Max(a, OverkeksConfig.CfgSpawnCountMultiplierAtFullLock.Value);
			return Mathf.Lerp(a, b, HeatLockSystem.CurrentLockProgress);
		}
	}

	internal static void ExpandSpawnPositions(List<Transform> positions, float multiplier)
	{
		if (positions == null || positions.Count == 0 || multiplier <= 1f)
		{
			return;
		}
		int count = positions.Count;
		float num = (float)count * multiplier;
		int num2 = Mathf.FloorToInt(num);
		if (UnityEngine.Random.value < num - (float)num2)
		{
			num2++;
		}
		num2 = Mathf.Max(count, num2);
		int num3 = num2 - count;
		if (num3 <= 0)
		{
			return;
		}
		_scatterUsed = 0;
		int num4 = 0;
		for (int i = 0; i < num3; i++)
		{
			Transform source = positions[UnityEngine.Random.Range(0, count)];
			Transform transform = CreateScatteredPoint(source, positions);
			if (transform == null)
			{
				num4++;
			}
			else
			{
				positions.Add(transform);
			}
		}
		if (num4 > 0)
		{
			Plugin.DBG("SPAWN", "dropped " + num4 + "/" + num3 + " extra spawn slot(s) with no room to scatter into");
		}
	}

	private static Transform CreateScatteredPoint(Transform source, List<Transform> existing)
	{
		if (source == null)
		{
			return null;
		}
		Vector3 position = source.position;
		float radius = Mathf.Max(0.5f, OverkeksConfig.CfgSpawnScatterRadius.Value);
		float minInclusive = Mathf.Min(radius * 0.45f, Mathf.Max(2f, radius * 0.35f));
		float minSpacing = Mathf.Clamp(radius * 0.22f, 2.5f, 5.5f);
		int noFloor = 0;
		int blocked = 0;
		int notWalkable = 0;
		int tooClose = 0;
		int behindWall = 0;
		for (int i = 0; i < 24; i++)
		{
			float f = UnityEngine.Random.Range(0f, 360f) * (MathF.PI / 180f);
			float dist = UnityEngine.Random.Range(minInclusive, radius);
			Vector3 candidate = position + new Vector3(Mathf.Cos(f) * dist, 0f, Mathf.Sin(f) * dist);
			if (!TryResolveWalkableSpawn(candidate, position.y, out Vector3 snapped))
			{
				if (!Physics.Raycast(candidate + Vector3.up * 4f, Vector3.down, 16f, -5, QueryTriggerInteraction.Ignore))
				{
					noFloor++;
				}
				else if (Physics.CheckCapsule(candidate + Vector3.up * 0.6f, candidate + Vector3.up * 1.7f, 0.3f, -5, QueryTriggerInteraction.Ignore))
				{
					blocked++;
				}
				else
				{
					notWalkable++;
				}
				continue;
			}
			if (IsTooCloseToExisting(snapped, existing, minSpacing))
			{
				tooClose++;
				continue;
			}
			if (IsSpawnPathBlocked(position, snapped))
			{
				behindWall++;
				continue;
			}
			return TakeScatterPoint(snapped, source.rotation);
		}
		Plugin.DBG("SPAWN", "no scatter point after 24 tries (no floor " + noFloor + ", blocked " + blocked + ", not walkable " + notWalkable + ", too close " + tooClose + ", behind wall " + behindWall + ")");
		return null;
	}

	private static bool IsTooCloseToExisting(Vector3 candidate, List<Transform> existing, float minSpacing)
	{
		if (existing == null || existing.Count == 0)
		{
			return false;
		}
		float minSqr = minSpacing * minSpacing;
		for (int i = 0; i < existing.Count; i++)
		{
			Transform transform = existing[i];
			if (transform == null)
			{
				continue;
			}
			Vector3 delta = transform.position - candidate;
			delta.y = 0f;
			if (delta.sqrMagnitude < minSqr)
			{
				return true;
			}
		}
		return false;
	}

	private static bool IsSpawnPathBlocked(Vector3 from, Vector3 to)
	{
		return SpawnWalkability.IsPathBlocked(from, to);
	}

	/// <summary>
	/// Floor-snap + capsule clear + A* walkable snap. Rejects walls, voids, and pit drops vs referenceY.
	/// </summary>
	private static bool TryResolveWalkableSpawn(Vector3 candidate, float referenceY, out Vector3 snapped)
	{
		snapped = candidate;
		if (!Physics.Raycast(candidate + Vector3.up * 4f, Vector3.down, out RaycastHit hitInfo, 16f, -5, QueryTriggerInteraction.Ignore))
		{
			return false;
		}
		snapped = hitInfo.point;
		if (snapped.y < referenceY - MaxFloorYDelta || snapped.y > referenceY + MaxFloorYDelta)
		{
			return false;
		}
		if (Physics.CheckCapsule(snapped + Vector3.up * 0.6f, snapped + Vector3.up * 1.7f, 0.35f, -5, QueryTriggerInteraction.Ignore))
		{
			return false;
		}
		return SpawnWalkability.TrySnapWalkable(snapped, referenceY, MaxFloorYDelta, out snapped);
	}

	private static Transform TakeScatterPoint(Vector3 position, Quaternion rotation)
	{
		if (_scatterRoot == null)
		{
			_scatterRoot = OverkeksMode.NewHelper("PncEdi_SpawnScatter");
		}
		Transform transform;
		if (_scatterUsed < ScatterPool.Count && ScatterPool[_scatterUsed] != null)
		{
			transform = ScatterPool[_scatterUsed];
		}
		else
		{
			GameObject gameObject = OverkeksMode.NewHelper("scatter_" + _scatterUsed);
			gameObject.transform.SetParent(_scatterRoot.transform, worldPositionStays: false);
			transform = gameObject.transform;
			if (_scatterUsed < ScatterPool.Count)
			{
				ScatterPool[_scatterUsed] = transform;
			}
			else
			{
				ScatterPool.Add(transform);
			}
		}
		_scatterUsed++;
		transform.position = position;
		transform.rotation = rotation;
		return transform;
	}

	internal static void RelaxSpawnFilters(MonoBehaviour spawner, float multiplier)
	{
		if (!(multiplier <= 1f) && !(spawner == null))
		{
			Traverse obj = Traverse.Create((object)spawner);
			float value = obj.Field("minDistanceFromPlayer").GetValue<float>();
			obj.Field("minDistanceFromPlayer").SetValue((object)Mathf.Max(0.5f, value / multiplier));
		}
	}

	internal static void ExpandSpawnTriggers(MonoBehaviour spawner)
	{
		if (!OverkeksMode.Active || spawner == null)
		{
			return;
		}
		float expand = OverkeksConfig.CfgSpawnTriggerExpandMeters?.Value ?? 0f;
		if (expand <= 0.01f)
		{
			return;
		}
		int instanceId = spawner.GetInstanceID();
		if (!TriggerExpandedSpawners.Add(instanceId))
		{
			return;
		}
		Collider[] triggers = Traverse.Create((object)spawner).Field("triggerBoxes").GetValue<Collider[]>();
		if (triggers == null || triggers.Length == 0)
		{
			return;
		}
		int changed = 0;
		for (int i = 0; i < triggers.Length; i++)
		{
			Collider collider = triggers[i];
			if (collider == null)
			{
				continue;
			}
			if (ExpandTriggerCollider(collider, expand))
			{
				changed++;
			}
		}
		if (changed > 0)
		{
			Plugin.DBG("SPAWN", NameRemap.StripCloneSuffix(spawner.gameObject.name) + ": expanded " + changed + " spawn trigger(s) by " + expand.ToString("0.#") + "m");
		}
	}

	private static bool ExpandTriggerCollider(Collider collider, float expandMeters)
	{
		BoxCollider box = collider as BoxCollider;
		if (box != null)
		{
			Vector3 lossy = box.transform.lossyScale;
			float sx = Mathf.Max(0.01f, Mathf.Abs(lossy.x));
			float sz = Mathf.Max(0.01f, Mathf.Abs(lossy.z));
			Vector3 size = box.size;
			size.x += expandMeters * 2f / sx;
			size.z += expandMeters * 2f / sz;
			box.size = size;
			return true;
		}
		SphereCollider sphere = collider as SphereCollider;
		if (sphere != null)
		{
			float scale = Mathf.Max(Mathf.Abs(sphere.transform.lossyScale.x), Mathf.Abs(sphere.transform.lossyScale.z), 0.01f);
			sphere.radius += expandMeters / scale;
			return true;
		}
		CapsuleCollider capsule = collider as CapsuleCollider;
		if (capsule != null)
		{
			float scale = Mathf.Max(Mathf.Abs(capsule.transform.lossyScale.x), Mathf.Abs(capsule.transform.lossyScale.z), 0.01f);
			capsule.radius += expandMeters / scale;
			return true;
		}
		return false;
	}

	internal static void InjectGapFillSpawnPoints(MonoBehaviour spawner)
	{
		if (!OverkeksMode.Active || spawner == null || OverkeksConfig.CfgSpawnGapFillEnabled == null || !OverkeksConfig.CfgSpawnGapFillEnabled.Value)
		{
			return;
		}
		int instanceId = spawner.GetInstanceID();
		if (!GapFilledSpawners.Add(instanceId))
		{
			return;
		}
		Traverse traverse = Traverse.Create((object)spawner);
		Transform[] spawnPositions = traverse.Field("spawnPositions").GetValue<Transform[]>();
		if (spawnPositions == null || spawnPositions.Length == 0)
		{
			return;
		}
		GapOccupiedScratch.Clear();
		GapAddedScratch.Clear();
		for (int i = 0; i < spawnPositions.Length; i++)
		{
			Transform transform = spawnPositions[i];
			if (transform != null)
			{
				GapOccupiedScratch.Add(transform.position);
			}
		}
		if (GapOccupiedScratch.Count == 0)
		{
			return;
		}
		if (!TryGetGapFillBounds(spawnPositions, traverse, out Bounds bounds))
		{
			return;
		}
		float minSpacing = Mathf.Max(3f, OverkeksConfig.CfgSpawnGapFillMinSpacing.Value);
		int maxExtra = OverkeksConfig.CfgSpawnGapFillMaxExtra.Value;
		if (maxExtra <= 0)
		{
			maxExtra = Mathf.Max(spawnPositions.Length, 12);
		}
		float step = minSpacing * 0.7f;
		float minX = bounds.min.x;
		float maxX = bounds.max.x;
		float minZ = bounds.min.z;
		float maxZ = bounds.max.z;
		if (maxX - minX < step || maxZ - minZ < step)
		{
			return;
		}
		// Prefer filling the padded room footprint; cap still prevents absurd counts in huge open maps.
		int areaEstimate = Mathf.Max(1, Mathf.CeilToInt(((maxX - minX) / step) * ((maxZ - minZ) / step) * 0.45f));
		maxExtra = Mathf.Min(maxExtra, Mathf.Max(areaEstimate, spawnPositions.Length));
		Quaternion rotation = spawnPositions[0] != null ? spawnPositions[0].rotation : Quaternion.identity;
		float sampleY = GapOccupiedScratch[0].y;
		for (float x = minX + step * 0.5f; x <= maxX && GapAddedScratch.Count < maxExtra; x += step)
		{
			for (float z = minZ + step * 0.5f; z <= maxZ && GapAddedScratch.Count < maxExtra; z += step)
			{
				Vector3 candidate = new Vector3(x, sampleY, z);
				if (!TrySnapGapCandidate(candidate, GapOccupiedScratch, minSpacing, sampleY, out Vector3 snapped))
				{
					continue;
				}
				Transform point = CreateGapFillPoint(spawner, snapped, rotation, GapAddedScratch.Count);
				GapAddedScratch.Add(point);
				GapOccupiedScratch.Add(snapped);
			}
		}
		if (GapAddedScratch.Count == 0)
		{
			return;
		}
		Transform[] expanded = new Transform[spawnPositions.Length + GapAddedScratch.Count];
		Array.Copy(spawnPositions, expanded, spawnPositions.Length);
		for (int i = 0; i < GapAddedScratch.Count; i++)
		{
			expanded[spawnPositions.Length + i] = GapAddedScratch[i];
		}
		traverse.Field("spawnPositions").SetValue(expanded);
		Plugin.DBG("SPAWN", NameRemap.StripCloneSuffix(spawner.gameObject.name) + ": gap-fill +" + GapAddedScratch.Count + " spawn point(s) (now " + expanded.Length + ")");
	}

	private static bool TryGetGapFillBounds(Transform[] spawnPositions, Traverse traverse, out Bounds bounds)
	{
		bounds = default;
		bool hasBounds = false;
		for (int i = 0; i < spawnPositions.Length; i++)
		{
			Transform transform = spawnPositions[i];
			if (transform == null)
			{
				continue;
			}
			if (!hasBounds)
			{
				bounds = new Bounds(transform.position, Vector3.zero);
				hasBounds = true;
			}
			else
			{
				bounds.Encapsulate(transform.position);
			}
		}
		if (!hasBounds)
		{
			return false;
		}
		Collider[] triggers = traverse.Field("triggerBoxes").GetValue<Collider[]>();
		if (triggers != null)
		{
			for (int i = 0; i < triggers.Length; i++)
			{
				Collider collider = triggers[i];
				if (collider != null)
				{
					bounds.Encapsulate(collider.bounds);
				}
			}
		}
		float pad = Mathf.Max(0f, OverkeksConfig.CfgSpawnGapFillPad.Value);
		bounds.Expand(new Vector3(pad * 2f, 0f, pad * 2f));
		if (bounds.size.x < 6f || bounds.size.z < 6f)
		{
			bounds.Expand(new Vector3(Mathf.Max(0f, 12f - bounds.size.x), 0f, Mathf.Max(0f, 12f - bounds.size.z)));
		}
		return true;
	}

	private static bool TrySnapGapCandidate(Vector3 candidate, List<Vector3> occupied, float minSpacing, float referenceY, out Vector3 snapped)
	{
		snapped = candidate;
		if (!TryResolveWalkableSpawn(candidate, referenceY, out snapped))
		{
			return false;
		}
		float minSqr = minSpacing * minSpacing;
		for (int i = 0; i < occupied.Count; i++)
		{
			Vector3 delta = occupied[i] - snapped;
			delta.y = 0f;
			if (delta.sqrMagnitude < minSqr)
			{
				return false;
			}
		}
		Vector3 nearest = occupied[0];
		float nearestSqr = float.MaxValue;
		for (int i = 0; i < occupied.Count; i++)
		{
			Vector3 delta = occupied[i] - snapped;
			delta.y = 0f;
			float sqr = delta.sqrMagnitude;
			if (sqr < nearestSqr)
			{
				nearestSqr = sqr;
				nearest = occupied[i];
			}
		}
		if (IsSpawnPathBlocked(nearest, snapped))
		{
			return false;
		}
		return true;
	}

	private static Transform CreateGapFillPoint(MonoBehaviour spawner, Vector3 position, Quaternion rotation, int index)
	{
		if (_gapRoot == null)
		{
			_gapRoot = OverkeksMode.NewHelper("PncEdi_SpawnGapFill");
		}
		GameObject gameObject = OverkeksMode.NewHelper("gap_" + spawner.GetInstanceID() + "_" + index);
		gameObject.transform.SetParent(_gapRoot.transform, worldPositionStays: false);
		gameObject.transform.SetPositionAndRotation(position, rotation);
		return gameObject.transform;
	}

	private static void EnsureFallbackSpawnPositions(MonoBehaviour spawner, List<Transform> positions)
	{
		if (positions == null || positions.Count > 0 || spawner == null)
		{
			return;
		}
		Transform[] value = Traverse.Create((object)spawner).Field("spawnPositions").GetValue<Transform[]>();
		if (value == null)
		{
			return;
		}
		Transform[] array = value;
		foreach (Transform transform in array)
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
		if (OverkeksMode.Active)
		{
			float spawnMultiplier = SpawnMultiplier;
			EnsureFallbackSpawnPositions(spawner, positions);
			int num = ((positions != null) ? positions.Count : 0);
			ExpandSpawnPositions(positions, spawnMultiplier);
			Plugin.DBG("SPAWN", "wave x" + spawnMultiplier.ToString("0.00") + " at lock progress " + HeatLockSystem.CurrentLockProgress.ToString("0.00") + ": " + num + " -> " + ((positions != null) ? positions.Count : 0) + " spawn point(s)");
		}
	}

	[HarmonyPatch(typeof(EnemySpawner), "Awake")]
	[HarmonyPostfix]
	public static void EnemySpawner_Awake_Postfix(EnemySpawner __instance)
	{
        if (!OverkeksMode.Active) return;
        OverkeksMode.CaptureSpawner(__instance);
		InjectGapFillSpawnPoints(__instance);
		SpawnHeatPotionDrop.SeedNearSpawner(__instance);
		ExpandSpawnTriggers(__instance);
		RelaxSpawnFilters(__instance, SpawnMultiplier);
	}

	[HarmonyPatch(typeof(EnemySpawner), "GetAvailableSpawnPositions")]
	[HarmonyPostfix]
	public static void EnemySpawner_GetPositions_Postfix(EnemySpawner __instance, ref List<Transform> __result)
	{
        if (!OverkeksMode.Active) return;
        OverkeksMode.CaptureSpawner(__instance);
		ApplySpawnMultiplier(__instance, ref __result);
	}

	[HarmonyPatch(typeof(ArenaEnemySpawner), "Awake")]
	[HarmonyPostfix]
	public static void ArenaEnemySpawner_Awake_Postfix(ArenaEnemySpawner __instance)
	{
        if (!OverkeksMode.Active) return;
        OverkeksMode.CaptureSpawner(__instance);
		InjectGapFillSpawnPoints(__instance);
		SpawnHeatPotionDrop.SeedNearSpawner(__instance);
		ExpandSpawnTriggers(__instance);
		RelaxSpawnFilters(__instance, SpawnMultiplier);
	}

	[HarmonyPatch(typeof(ArenaEnemySpawner), "GetAvailableSpawnPositions")]
	[HarmonyPostfix]
	public static void ArenaEnemySpawner_GetPositions_Postfix(ArenaEnemySpawner __instance, ref List<Transform> __result)
	{
        if (!OverkeksMode.Active) return;
        OverkeksMode.CaptureSpawner(__instance);
		ApplySpawnMultiplier(__instance, ref __result);
	}


}
