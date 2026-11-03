using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace PncEdi;

/// <summary>
/// Replaces vanilla SweeperEnemySpawner single-random chaser with one hidden instance per
/// enemySpawnData entry. After a chaser H-scene, returns the boss to ChaserHolder staging.
/// </summary>
[HarmonyPatch]
internal static class ChaserSweeperHooks
{
	/// <summary>First chaser slot activates this many seconds after StartSpawning / lastSpawnTime.</summary>
	private const float InitialSpawnDelaySeconds = 240f;

	/// <summary>Each additional chaser slot adds this delay (2nd = 270s, 3rd = 300s, …).</summary>
	private const float InitialSpawnStaggerSeconds = 30f;

	/// <summary>After H-scene or combat defeat, hidden again until this many seconds pass.</summary>
	private const float RestageDelaySeconds = 240f;

	/// <summary>Min horizontal distance between chaser spawn poses (boss colliders are large).</summary>
	private const float MinSpawnSeparation = 15f;

	/// <summary>Prefer spawn points at least this far from the player (pick best available if none qualify).</summary>
	private const float MinPlayerSpawnDistance = 18f;

	/// <summary>Never nudge more than this far from a level spawn point (stay on navmesh).</summary>
	private const float MaxSpawnNudgeFromPoint = 6f;

	private sealed class ChaserSlot
	{
		public GameObject Enemy;

		public GameObject Follower;

		public AudioSource Audio;

		public Transform SpawnPoint;

		public string AlertText;

		public float ActivateAt;

		public bool Activated;

		/// <summary>Set by ReturnToStaging; do not overwrite in ReschedulePendingSlots.</summary>
		public bool CustomActivateSchedule;
	}

	private sealed class SweeperState
	{
		public SweeperEnemySpawner Spawner;

		public readonly List<ChaserSlot> Slots = new List<ChaserSlot>();

		public bool Initialized;
	}

	private static readonly Dictionary<SweeperEnemySpawner, SweeperState> States = new Dictionary<SweeperEnemySpawner, SweeperState>();

	private static FieldInfo _enemySpawnDataField;

	private static FieldInfo _spawnPointsField;

	private static FieldInfo _currentEnemyField;

	private static FieldInfo _hasSpawnedField;

	private static FieldInfo _isSpawningField;

	private static FieldInfo _lastSpawnTimeField;

	internal static bool Enabled => OverkeksMode.Active;

	internal static bool GuideSpawnEnabled
	{
		get
		{
			if (!Enabled)
			{
				return false;
			}
			return OverkeksConfig.CfgChaserSpawnOnGuide != null && OverkeksConfig.CfgChaserSpawnOnGuide.Value;
		}
	}

	private const float GuideDragonChance = 0.05f;

	private const float GuideWendigoChance = 0.05f;

	internal static void ClearCache()
	{
		States.Clear();
		ChaserDamageHooks.ResetForScene();
	}
    internal static void PruneDestroyed() {
        var gone=new List<SweeperEnemySpawner>();foreach(var spawner in States.Keys)if(spawner==null)gone.Add(spawner);
        foreach(var spawner in gone)States.Remove(spawner);
    }

	internal static bool IsManagedChaser(GameObject enemy)
	{
		return FindSlot(enemy) != null;
	}

	internal static bool IsActiveManagedChaser(GameObject enemy)
	{
		ChaserSlot slot = FindSlot(enemy);
		if (slot == null || !slot.Activated)
		{
			return false;
		}
		return slot.Enemy != null;
	}

	/// <summary>
	/// True while a sweeper chaser is hunting, or a non-managed chaser boss is away from ChaserHolder.
	/// </summary>
	internal static bool IsHuntingChaser(GameObject enemy)
	{
		if (enemy == null || !enemy.activeInHierarchy)
		{
			return false;
		}
		// Pool fodder never counts as the hunting boss.
		if (ChaserFodderSpawn.IsFodder(enemy))
		{
			return false;
		}
		ChaserSlot slot = FindSlot(enemy);
		if (slot != null)
		{
			return slot.Activated;
		}
		if (!GrabStruggleHooks.IsChaserBossGrabEnemy(enemy))
		{
			return false;
		}
		if (IsNearChaserHolder(enemy.transform.position))
		{
			return false;
		}
		DragonEnemyAI dragon = enemy.GetComponent<DragonEnemyAI>();
		if (dragon != null && dragon.frozenByArena)
		{
			return false;
		}
		ProximityDragonEnemyAI proximity = enemy.GetComponent<ProximityDragonEnemyAI>();
		if (proximity != null && proximity.frozenByArena)
		{
			return false;
		}
		return true;
	}

	/// <summary>
	/// Exit-guide (Navigator) used: optional chaser rolls / force the missing partner.
	/// </summary>
	internal static void OnGuideUsed()
	{
		if (!GuideSpawnEnabled)
		{
			return;
		}
		EnsureAllSweepersInitialized();
		ChaserSlot dragonActive = null;
		ChaserSlot dragonIdle = null;
		ChaserSlot wendigoActive = null;
		ChaserSlot wendigoIdle = null;
		SweeperState dragonState = null;
		SweeperState wendigoState = null;
		foreach (KeyValuePair<SweeperEnemySpawner, SweeperState> pair in States)
		{
			SweeperState state = pair.Value;
			if (state == null || state.Slots.Count == 0)
			{
				continue;
			}
			if (!GetIsSpawning(state.Spawner))
			{
				continue;
			}
			for (int i = 0; i < state.Slots.Count; i++)
			{
				ChaserSlot slot = state.Slots[i];
				if (slot.Enemy == null)
				{
					continue;
				}
				if (IsWendigoSlot(slot))
				{
					if (slot.Activated)
					{
						wendigoActive = slot;
					}
					else
					{
						wendigoIdle = slot;
						wendigoState = state;
					}
				}
				else if (IsDragonSlot(slot))
				{
					if (slot.Activated)
					{
						dragonActive = slot;
					}
					else
					{
						dragonIdle = slot;
						dragonState = state;
					}
				}
			}
		}
		bool dragonOut = dragonActive != null;
		bool wendigoOut = wendigoActive != null;
		if (dragonOut && wendigoOut)
		{
			Plugin.DBG("CHASER-GUIDE", "both chasers already hunting — guide spawn skipped");
			return;
		}
		if (dragonOut ^ wendigoOut)
		{
			if (!dragonOut && dragonIdle != null && dragonState != null)
			{
				Plugin.DBG("CHASER-GUIDE", "wendigo already out — forcing dragon");
				ActivateSlot(dragonState, dragonIdle);
				return;
			}
			if (!wendigoOut && wendigoIdle != null && wendigoState != null)
			{
				Plugin.DBG("CHASER-GUIDE", "dragon already out — forcing wendigo");
				ActivateSlot(wendigoState, wendigoIdle);
			}
			return;
		}
		bool spawned = false;
		if (dragonIdle != null && dragonState != null && UnityEngine.Random.value < GuideDragonChance)
		{
			Plugin.DBG("CHASER-GUIDE", "rolled dragon (" + (GuideDragonChance * 100f).ToString("0") + "%)");
			ActivateSlot(dragonState, dragonIdle);
			spawned = true;
		}
		if (wendigoIdle != null && wendigoState != null && UnityEngine.Random.value < GuideWendigoChance)
		{
			Plugin.DBG("CHASER-GUIDE", "rolled wendigo (" + (GuideWendigoChance * 100f).ToString("0") + "%)");
			ActivateSlot(wendigoState, wendigoIdle);
			spawned = true;
		}
		if (!spawned)
		{
			Plugin.DBG("CHASER-GUIDE", "no chaser roll");
		}
	}

	private static bool IsWendigoSlot(ChaserSlot slot)
	{
		return slot != null && GrabStruggleHooks.IsWendigoGrabEnemy(slot.Enemy);
	}

	private static bool IsDragonSlot(ChaserSlot slot)
	{
		if (slot == null || slot.Enemy == null || IsWendigoSlot(slot))
		{
			return false;
		}
		return GrabStruggleHooks.IsDragonGrabEnemy(slot.Enemy) || OverkeksChaserBossHelper.IsChaserBossPrefab(slot.Enemy);
	}

    internal static bool ActivateTestChaser(bool wendigo) {
        if(!Enabled)return false;
        EnsureAllSweepersInitialized();
        foreach(var state in States.Values)foreach(var slot in state.Slots) {
            if(slot.Enemy==null || (wendigo ? !IsWendigoSlot(slot) : !IsDragonSlot(slot)))continue;
            if(slot.Activated){Plugin.DBG("BOSS-TEST","requested chaser already active; no duplicate spawned");return true;}
            ActivateSlot(state,slot);
            Plugin.DBG("BOSS-TEST","managed chaser activated="+slot.Activated+"; actor="+slot.Enemy.name);
            return slot.Activated;
        }
        Plugin.DBG("BOSS-TEST","requested native chaser slot unavailable in this level");return false;
    }

	private static void EnsureAllSweepersInitialized()
	{
		SweeperEnemySpawner[] spawners = UnityEngine.Object.FindObjectsByType<SweeperEnemySpawner>(FindObjectsInactive.Include, FindObjectsSortMode.None);
		for (int i = 0; i < spawners.Length; i++)
		{
			if (spawners[i] != null)
			{
				TryInitSweeper(spawners[i]);
			}
		}
	}

	/// <summary>Wait until the player is restored before moving the boss to ChaserHolder (avoids carrying the player into the box).</summary>
    private static bool CanRestage(GameObject enemy) => enemy!=null && CreatorVisualPolicy.ShouldRestage(
        IsManagedChaser(enemy),enemy.GetComponent<DragonEnemyAI>()!=null || enemy.GetComponent<ProximityDragonEnemyAI>()!=null,
        ChaserFodderSpawn.IsFodder(enemy));

	internal static void ScheduleReturnToStaging(GameObject enemy)
	{
		if (!Enabled || !CanRestage(enemy))
		{
			return;
		}
		// Pool fodder dragons/wendigos must never enter ChaserHolder / sweeper restage.
		if (ChaserFodderSpawn.IsFodder(enemy))
		{
			return;
		}
		if (Plugin.Instance != null)
		{
			OverkeksMode.StartCoroutine(ReturnToStagingAfterPlayerRestore(enemy));
			return;
		}
		ReturnToStaging(enemy);
	}

	private static IEnumerator ReturnToStagingAfterPlayerRestore(GameObject enemy)
	{
		yield return null;
		yield return null;
		if (enemy != null)
		{
			ReturnToStaging(enemy);
		}
	}

	internal static bool IsNearChaserHolder(Vector3 position, float minDistance = 8f)
	{
		if (!TryGetChaserHolderPosition(out Vector3 holderPosition))
		{
			return false;
		}
		return HorizontalDistance(position, holderPosition) < minDistance;
	}

	internal static Vector3 PushAwayFromChaserHolder(Vector3 position, float minDistance = 12f)
	{
		if (!TryGetChaserHolderPosition(out Vector3 holderPosition))
		{
			return position;
		}
		float dist = HorizontalDistance(position, holderPosition);
		if (dist >= minDistance)
		{
			return position;
		}
		Vector3 away = position - holderPosition;
		away.y = 0f;
		if (away.sqrMagnitude < 0.01f)
		{
			away = Vector3.forward;
		}
		Vector3 pushed = holderPosition + away.normalized * minDistance;
		pushed.y = position.y;
		return pushed;
	}

	internal static bool TryGetChaserHolderPosition(out Vector3 position)
	{
		position = Vector3.zero;
		GameObject holder = FindChaserHolder();
		if (holder == null)
		{
			return false;
		}
		position = holder.transform.position;
		return true;
	}

	internal static void ReturnToStaging(GameObject enemy)
	{
		if (!Enabled || !CanRestage(enemy))
		{
			return;
		}
		if (ChaserFodderSpawn.IsFodder(enemy))
		{
			return;
		}
        EnemyReactivationHelper.CancelScheduledFor(enemy);
		OverkeksMode.EnsurePlayerNotAttachedToEnemy(enemy);
		ChaserSlot slot = FindSlot(enemy);
		if (slot == null)
		{
			ApplyHiddenStaging(enemy, null);
			ResetChaserAi(enemy);
			Plugin.DBG("CHASER-STAGE", NameRemap.StripCloneSuffix(enemy.name) + " (no sweeper slot) -> ChaserHolder");
			return;
		}
		slot.Activated = false;
		slot.CustomActivateSchedule = true;
		MonoBehaviour ai = slot.Enemy.GetComponent<DragonEnemyAI>();
		if (ai == null)
		{
			ai = slot.Enemy.GetComponent<ProximityDragonEnemyAI>();
		}
		ChaserHealthBarUi.Hide(ai);
		if (GuideSpawnEnabled)
		{
			slot.ActivateAt = float.PositiveInfinity;
			OverkeksChaserBossHelper.PauseForStaging(slot.Enemy);
			ApplyHiddenStaging(slot.Enemy, slot);
			ResetChaserAi(slot.Enemy);
			ChaserDamageHooks.ApplyHealth(slot.Enemy);
			Plugin.DBG("CHASER-STAGE", NameRemap.StripCloneSuffix(enemy.name) + " -> hidden; waits for exit guide");
			return;
		}
		slot.ActivateAt = Time.time + RestageDelaySeconds;
		OverkeksChaserBossHelper.PauseForStaging(slot.Enemy);
		ApplyHiddenStaging(slot.Enemy, slot);
		ResetChaserAi(slot.Enemy);
		ChaserDamageHooks.ApplyHealth(slot.Enemy);
		Plugin.DBG("CHASER-STAGE", NameRemap.StripCloneSuffix(enemy.name) + " -> hidden; re-attack in " + RestageDelaySeconds + "s");
	}

	[HarmonyPatch(typeof(NavigatorSpawner), "SpawnNavigator")]
	[HarmonyPostfix]
	private static void SpawnNavigator_Postfix()
	{
 if(!OverkeksMode.Active) return;
		OnGuideUsed();
	}

	[HarmonyPatch(typeof(SweeperEnemySpawner), "PickAndSpawnHiddenEnemy")]
	[HarmonyPrefix]
	private static bool PickAndSpawnHiddenEnemy_Prefix(SweeperEnemySpawner __instance)
	{
 if(!OverkeksMode.Active) return true;
		if (!Enabled || __instance == null)
		{
			return true;
		}
		if (!TryInitSweeper(__instance))
		{
			return true;
		}
		return false;
	}

	[HarmonyPatch(typeof(SweeperEnemySpawner), "StartSpawning")]
	[HarmonyPostfix]
	private static void StartSpawning_Postfix(SweeperEnemySpawner __instance)
	{
 if(!OverkeksMode.Active) return;
		if (!Enabled || __instance == null)
		{
			return;
		}
		if (States.TryGetValue(__instance, out SweeperState state) && state.Slots.Count > 0)
		{
			for (int i = 0; i < state.Slots.Count; i++)
			{
				state.Slots[i].CustomActivateSchedule = false;
			}
			ReschedulePendingSlots(state);
			if (GuideSpawnEnabled)
			{
				Plugin.DBG("CHASER-SPAWN", "StartSpawning — guide mode (no timer); wait for Navigator");
			}
			else
			{
				Plugin.DBG("CHASER-SPAWN", "StartSpawning — timer reset from lastSpawnTime (isSpawning=true)");
			}
			return;
		}
		TryInitSweeper(__instance);
	}

	[HarmonyPatch(typeof(SweeperEnemySpawner), "Update")]
	[HarmonyPrefix]
	private static bool Update_Prefix(SweeperEnemySpawner __instance)
	{
 if(!OverkeksMode.Active) return true;
		if (!Enabled || __instance == null)
		{
			return true;
		}
		if (!States.TryGetValue(__instance, out SweeperState state) || state.Slots.Count == 0)
		{
			if (!TryInitSweeper(__instance))
			{
				return true;
			}
			state = States[__instance];
		}
		if (state.Slots.Count == 0)
		{
			return true;
		}
		TickSweeper(state);
		return false;
	}

	/// <summary>
	/// Returns true when mod owns hidden chaser instances (vanilla PickAndSpawn should be skipped).
	/// </summary>
	private static bool TryInitSweeper(SweeperEnemySpawner spawner)
	{
        OverkeksMode.CaptureSpawner(spawner);
		EnsureReflection();
		if (States.TryGetValue(spawner, out SweeperState existing) && existing.Initialized && existing.Slots.Count > 0)
		{
			return true;
		}
		SweeperEnemySpawner.EnemySpawnData[] entries = GetSpawnData(spawner);
		Transform[] points = GetSpawnPoints(spawner);
		if (entries == null || entries.Length == 0 || points == null || points.Length == 0)
		{
			Plugin.DBG("CHASER-SPAWN", "init skipped — no enemySpawnData or spawnPoints on SweeperEnemySpawner");
			return false;
		}
		GameObject holder = FindChaserHolder();
		if (holder == null)
		{
			Plugin.DBG("CHASER-SPAWN", "init pending — ChaserHolder not in scene yet (retrying each frame)");
			return false;
		}
		DestroyVanillaEnemy(spawner);
		SweeperState state = existing ?? new SweeperState
		{
			Spawner = spawner
		};
		state.Slots.Clear();
		States[spawner] = state;
		List<Vector3> reservedSpawnPositions = new List<Vector3>();
		for (int i = 0; i < entries.Length; i++)
		{
			SweeperEnemySpawner.EnemySpawnData entry = entries[i];
			if (entry.enemyPrefab == null)
			{
				continue;
			}
			Transform spawnPoint = PickBestSpawnPoint(points, reservedSpawnPositions);
			ChaserSlot slot = new ChaserSlot
			{
				SpawnPoint = spawnPoint,
				AlertText = entry.spawnText,
				Activated = false
			};
			if (spawnPoint != null)
			{
				reservedSpawnPositions.Add(spawnPoint.position);
			}
			slot.Enemy = OverkeksMode.InstantiateOwned(entry.enemyPrefab, holder.transform.position, holder.transform.rotation);
			ConfigureHiddenSlot(slot);
			ChaserDamageHooks.ApplyHealth(slot.Enemy);
			state.Slots.Add(slot);
		}
		if (state.Slots.Count == 0)
		{
			Plugin.DBG("CHASER-SPAWN", "init skipped — all enemySpawnData prefabs null");
			return false;
		}
		state.Initialized = true;
		ReschedulePendingSlots(state);
		bool spawning = GetIsSpawning(spawner);
		if (GuideSpawnEnabled)
		{
			Plugin.DBG("CHASER-SPAWN", state.Slots.Count + " chaser(s) hidden; isSpawning=" + spawning
				+ " guide mode — appear on Navigator (5%/5%, partner forced if one is out)");
		}
		else
		{
			float waitFirst = GetSecondsUntilActivate(state, state.Slots[0]);
			Plugin.DBG("CHASER-SPAWN", state.Slots.Count + " chaser(s) hidden; isSpawning=" + spawning
				+ " first in " + waitFirst.ToString("F0") + "s (initial=" + InitialSpawnDelaySeconds
				+ "s stagger=" + InitialSpawnStaggerSeconds + "s, restage=" + RestageDelaySeconds + "s)");
		}
		return true;
	}

	private static void ReschedulePendingSlots(SweeperState state)
	{
		if (GuideSpawnEnabled)
		{
			for (int i = 0; i < state.Slots.Count; i++)
			{
				ChaserSlot slot = state.Slots[i];
				if (slot.Activated || slot.CustomActivateSchedule)
				{
					continue;
				}
				slot.ActivateAt = float.PositiveInfinity;
			}
			return;
		}
		float baseTime = GetLastSpawnTime(state.Spawner);
		if (baseTime <= 0f)
		{
			baseTime = Time.time;
		}
		for (int i = 0; i < state.Slots.Count; i++)
		{
			ChaserSlot slot = state.Slots[i];
			if (slot.Activated || slot.CustomActivateSchedule)
			{
				continue;
			}
			slot.ActivateAt = baseTime + GetInitialSpawnDelayForSlot(i);
		}
	}

	private static float GetInitialSpawnDelayForSlot(int slotIndex)
	{
		return InitialSpawnDelaySeconds + InitialSpawnStaggerSeconds * slotIndex;
	}

	private static float GetSecondsUntilActivate(SweeperState state, ChaserSlot slot)
	{
		return Mathf.Max(0f, slot.ActivateAt - Time.time);
	}

	private static GameObject FindChaserHolder()
	{
		GameObject holder = GameObject.Find("ChaserHolder");
		if (holder != null)
		{
			return holder;
		}
		GameObject[] roots = UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects();
		for (int r = 0; r < roots.Length; r++)
		{
			Transform[] transforms = roots[r].GetComponentsInChildren<Transform>(true);
			for (int t = 0; t < transforms.Length; t++)
			{
				if (transforms[t].name == "ChaserHolder")
				{
					return transforms[t].gameObject;
				}
			}
		}
		return null;
	}

	private static void TickSweeper(SweeperState state)
	{
		if (GuideSpawnEnabled)
		{
			return;
		}
		if (!GetIsSpawning(state.Spawner))
		{
			return;
		}
		for (int i = 0; i < state.Slots.Count; i++)
		{
			ChaserSlot slot = state.Slots[i];
			if (slot.Activated || slot.Enemy == null)
			{
				continue;
			}
			if (Time.time < slot.ActivateAt)
			{
				continue;
			}
			ActivateSlot(state, slot);
		}
	}

	private static void ActivateSlot(SweeperState state, ChaserSlot slot)
	{
		if (slot.Enemy == null)
		{
			return;
		}
		slot.CustomActivateSchedule = false;
		if (!TryResolveSpawnPose(state, slot, out Vector3 spawnPosition, out Quaternion spawnRotation))
		{
			if (slot.SpawnPoint != null)
			{
				spawnPosition = slot.SpawnPoint.position;
				spawnRotation = slot.SpawnPoint.rotation;
				Plugin.DBG("CHASER-SPAWN", NameRemap.StripCloneSuffix(slot.Enemy.name) + " using fallback spawn point");
			}
			else
			{
				Plugin.DBG("CHASER-SPAWN", NameRemap.StripCloneSuffix(slot.Enemy.name) + " activate failed — no spawn point");
				return;
			}
		}
		slot.Enemy.transform.SetPositionAndRotation(spawnPosition, spawnRotation);
		if (slot.Follower != null)
		{
			slot.Follower.SetActive(true);
		}
		if (slot.Audio != null)
		{
			slot.Audio.mute = false;
		}
		slot.Activated = true;
		ShowSpawnText(slot.AlertText);
		OverkeksChaserBossHelper.ActivateForChase(slot.Enemy);
		ChaserDamageHooks.OnChaserActivated(slot.Enemy);
		Plugin.DBG("CHASER-SPAWN", NameRemap.StripCloneSuffix(slot.Enemy.name) + " active at "
			+ spawnPosition.ToString("F1"));
	}

	private static void ConfigureHiddenSlot(ChaserSlot slot)
	{
		if (slot.Enemy == null)
		{
			return;
		}
		slot.Follower = slot.Enemy.transform.Find("Follower Entity")?.gameObject;
		if (slot.Follower != null)
		{
			slot.Follower.SetActive(false);
		}
		slot.Audio = slot.Enemy.GetComponent<AudioSource>();
		if (slot.Audio != null)
		{
			slot.Audio.mute = true;
		}
	}

	private static void ApplyHiddenStaging(GameObject enemy, ChaserSlot slot)
	{
		if (enemy == null)
		{
			return;
		}
		GameObject holder = FindChaserHolder();
		if (holder != null)
		{
			enemy.transform.SetPositionAndRotation(holder.transform.position, holder.transform.rotation);
		}
		if (slot != null)
		{
			if (slot.Follower == null)
			{
				slot.Follower = enemy.transform.Find("Follower Entity")?.gameObject;
			}
			if (slot.Follower != null)
			{
				slot.Follower.SetActive(false);
			}
			if (slot.Audio == null)
			{
				slot.Audio = enemy.GetComponent<AudioSource>();
			}
			if (slot.Audio != null)
			{
				slot.Audio.mute = true;
			}
		}
		else
		{
			GameObject follower = enemy.transform.Find("Follower Entity")?.gameObject;
			if (follower != null)
			{
				follower.SetActive(false);
			}
			AudioSource audio = enemy.GetComponent<AudioSource>();
			if (audio != null)
			{
				audio.mute = true;
			}
		}
		if (!enemy.activeSelf)
		{
			enemy.SetActive(true);
		}
	}

	private static void ResetChaserAi(GameObject enemy)
	{
		if (enemy == null)
		{
			return;
		}
		EnemyAI enemyAi = enemy.GetComponent<EnemyAI>();
		if (enemyAi != null)
		{
			enemyAi.ResetGrabFlag();
		}
		DragonEnemyAI dragon = enemy.GetComponent<DragonEnemyAI>();
		if (dragon != null)
		{
			dragon.ResetGrabFlag();
		}
		ProximityDragonEnemyAI proximityDragon = enemy.GetComponent<ProximityDragonEnemyAI>();
		if (proximityDragon != null)
		{
			proximityDragon.ResetGrabFlag();
		}
		Traverse traverse = Traverse.Create(enemy);
		if (traverse.Field("hasTriggeredDragonGrab").FieldExists())
		{
			traverse.Field("hasTriggeredDragonGrab").SetValue(false);
		}
		if (traverse.Field("hasGrabbedThisAttempt").FieldExists())
		{
			traverse.Field("hasGrabbedThisAttempt").SetValue(false);
		}
	}

	private static void ShowSpawnText(string text)
	{
		if (string.IsNullOrEmpty(text))
		{
			return;
		}
		AlertPanel alertPanel = UnityEngine.Object.FindAnyObjectByType<AlertPanel>();
		if (alertPanel == null)
		{
			return;
		}
		SweeperEnemySpawner spawner = UnityEngine.Object.FindAnyObjectByType<SweeperEnemySpawner>();
		float typeSpeed = 0.05f;
		float duration = 3f;
		if (spawner != null)
		{
			Traverse traverse = Traverse.Create(spawner);
			if (traverse.Field("typewriterSpeed").FieldExists())
			{
				typeSpeed = traverse.Field("typewriterSpeed").GetValue<float>();
			}
			if (traverse.Field("textDisplayDuration").FieldExists())
			{
				duration = traverse.Field("textDisplayDuration").GetValue<float>();
			}
		}
		alertPanel.ShowAlert(text, typeSpeed, duration);
	}

	private static ChaserSlot FindSlot(GameObject enemy)
	{
		foreach (SweeperState state in States.Values)
		{
			for (int i = 0; i < state.Slots.Count; i++)
			{
				ChaserSlot slot = state.Slots[i];
				if (slot.Enemy == null)
				{
					continue;
				}
				if (slot.Enemy == enemy || enemy.transform.IsChildOf(slot.Enemy.transform) || slot.Enemy.transform.IsChildOf(enemy.transform))
				{
					return slot;
				}
			}
		}
		return null;
	}

	private static void DestroyVanillaEnemy(SweeperEnemySpawner spawner)
	{
		EnsureReflection();
		GameObject current = _currentEnemyField?.GetValue(spawner) as GameObject;
		if (current != null)
		{
			OverkeksMode.StashNative(current);
			_currentEnemyField.SetValue(spawner, null);
		}
		_hasSpawnedField?.SetValue(spawner, false);
	}

	private static void EnsureReflection()
	{
		if (_enemySpawnDataField != null)
		{
			return;
		}
		_enemySpawnDataField = OptionalMembers.Field(typeof(SweeperEnemySpawner), "enemySpawnData");
		_spawnPointsField = OptionalMembers.Field(typeof(SweeperEnemySpawner), "spawnPoints");
		_currentEnemyField = OptionalMembers.Field(typeof(SweeperEnemySpawner), "currentEnemy");
		_hasSpawnedField = OptionalMembers.Field(typeof(SweeperEnemySpawner), "hasSpawned");
		_isSpawningField = OptionalMembers.Field(typeof(SweeperEnemySpawner), "isSpawning");
		_lastSpawnTimeField = OptionalMembers.Field(typeof(SweeperEnemySpawner), "lastSpawnTime");
	}

	private static SweeperEnemySpawner.EnemySpawnData[] GetSpawnData(SweeperEnemySpawner spawner)
	{
		return _enemySpawnDataField?.GetValue(spawner) as SweeperEnemySpawner.EnemySpawnData[];
	}

	private static Transform[] GetSpawnPoints(SweeperEnemySpawner spawner)
	{
		return _spawnPointsField?.GetValue(spawner) as Transform[];
	}

	private static bool GetIsSpawning(SweeperEnemySpawner spawner)
	{
		return (bool)(_isSpawningField?.GetValue(spawner) ?? true);
	}

	private static float GetLastSpawnTime(SweeperEnemySpawner spawner)
	{
		return (float)(_lastSpawnTimeField?.GetValue(spawner) ?? 0f);
	}

	private static bool TryResolveSpawnPose(SweeperState state, ChaserSlot slot, out Vector3 position, out Quaternion rotation)
	{
		position = Vector3.zero;
		rotation = Quaternion.identity;
		Transform[] points = GetSpawnPoints(state.Spawner);
		List<Vector3> blocked = CollectBlockingPositions(state, slot);
		Transform chosen = PickBestSpawnPointForActivation(points, blocked, slot.SpawnPoint);
		if (chosen == null)
		{
			return false;
		}
		slot.SpawnPoint = chosen;
		Vector3 anchor = chosen.position;
		position = anchor;
		rotation = chosen.rotation;
		position = ApplySmallSpawnNudge(anchor, position, blocked, slot.Enemy);
		return true;
	}

	/// <summary>Picks a level spawn point; only small nudges off the point (never far across the map).</summary>
	private static Transform PickBestSpawnPointForActivation(Transform[] points, List<Vector3> blocked, Transform fallback)
	{
		if (points == null || points.Length == 0)
		{
			return fallback;
		}
		Transform best = null;
		float bestScore = -1f;
		for (int i = 0; i < points.Length; i++)
		{
			Transform candidate = points[i];
			if (candidate == null)
			{
				continue;
			}
			float score = GetSpawnPointClearance(candidate.position, blocked);
			if (score > bestScore)
			{
				bestScore = score;
				best = candidate;
			}
		}
		return best ?? fallback;
	}

	private static Vector3 ApplySmallSpawnNudge(Vector3 anchor, Vector3 position, List<Vector3> blocked, GameObject enemy)
	{
		string enemyLabel = enemy != null ? NameRemap.StripCloneSuffix(enemy.name) : "chaser";
		if (GetMinHorizontalDistance(position, blocked) < MinSpawnSeparation)
		{
			position = ClampNudgeFromAnchor(anchor, PushAwayFromOccupied(position, blocked, MinSpawnSeparation));
			Plugin.DBG("CHASER-SPAWN", enemyLabel + " small nudge — other chaser");
		}
		if (TryGetPlayerPosition(out Vector3 playerPosition)
			&& HorizontalDistance(position, playerPosition) < MinPlayerSpawnDistance)
		{
			List<Vector3> awayFromPlayer = new List<Vector3> { playerPosition };
			position = ClampNudgeFromAnchor(anchor, PushAwayFromOccupied(position, awayFromPlayer, MinPlayerSpawnDistance));
			Plugin.DBG("CHASER-SPAWN", enemyLabel + " small nudge — player nearby");
		}
		return position;
	}

	private static Vector3 ClampNudgeFromAnchor(Vector3 anchor, Vector3 nudged)
	{
		Vector3 offset = nudged - anchor;
		offset.y = 0f;
		if (offset.magnitude <= MaxSpawnNudgeFromPoint)
		{
			nudged.y = anchor.y;
			return nudged;
		}
		nudged = anchor + offset.normalized * MaxSpawnNudgeFromPoint;
		nudged.y = anchor.y;
		return nudged;
	}

	private static bool TryGetPlayerPosition(out Vector3 position)
	{
		position = Vector3.zero;
		GameObject player = GameObject.FindGameObjectWithTag("Player");
		if (player == null)
		{
			return false;
		}
		position = player.transform.position;
		return true;
	}

	private static List<Vector3> CollectBlockingPositions(SweeperState state, ChaserSlot activating)
	{
		List<Vector3> blocked = new List<Vector3>();
		for (int i = 0; i < state.Slots.Count; i++)
		{
			ChaserSlot other = state.Slots[i];
			if (other == activating)
			{
				continue;
			}
			if (other.Enemy != null && other.Activated)
			{
				blocked.Add(other.Enemy.transform.position);
			}
			if (other.SpawnPoint != null && other.Activated)
			{
				blocked.Add(other.SpawnPoint.position);
			}
		}
		return blocked;
	}

	private static Transform PickBestSpawnPoint(Transform[] points, List<Vector3> reservedPositions)
	{
		if (points == null || points.Length == 0)
		{
			return null;
		}
		Transform best = null;
		float bestClearance = -1f;
		for (int i = 0; i < points.Length; i++)
		{
			Transform candidate = points[i];
			if (candidate == null)
			{
				continue;
			}
			float clearance = GetSpawnPointClearance(candidate.position, reservedPositions);
			if (clearance > bestClearance)
			{
				bestClearance = clearance;
				best = candidate;
			}
		}
		return best ?? points[0];
	}

	private static float GetSpawnPointClearance(Vector3 position, List<Vector3> reservedPositions)
	{
		float clearance = GetMinHorizontalDistance(position, reservedPositions);
		if (TryGetPlayerPosition(out Vector3 playerPosition))
		{
			clearance = Mathf.Min(clearance, HorizontalDistance(position, playerPosition));
		}
		return clearance;
	}

	private static float GetMinHorizontalDistance(Vector3 position, List<Vector3> others)
	{
		if (others == null || others.Count == 0)
		{
			return float.MaxValue;
		}
		float min = float.MaxValue;
		for (int i = 0; i < others.Count; i++)
		{
			float dist = HorizontalDistance(position, others[i]);
			if (dist < min)
			{
				min = dist;
			}
		}
		return min;
	}

	private static float HorizontalDistance(Vector3 a, Vector3 b)
	{
		float dx = a.x - b.x;
		float dz = a.z - b.z;
		return Mathf.Sqrt(dx * dx + dz * dz);
	}

	private static Vector3 PushAwayFromOccupied(Vector3 desired, List<Vector3> occupied, float minDistance)
	{
		Vector3 pos = desired;
		for (int attempt = 0; attempt < 12; attempt++)
		{
			if (GetMinHorizontalDistance(pos, occupied) >= minDistance)
			{
				return pos;
			}
			Vector3 nearest = occupied[0];
			float nearestDist = HorizontalDistance(pos, nearest);
			for (int i = 1; i < occupied.Count; i++)
			{
				float dist = HorizontalDistance(pos, occupied[i]);
				if (dist < nearestDist)
				{
					nearestDist = dist;
					nearest = occupied[i];
				}
			}
			Vector3 away = pos - nearest;
			away.y = 0f;
			if (away.sqrMagnitude < 0.01f)
			{
				away = Vector3.forward;
			}
			pos = nearest + away.normalized * minDistance;
			pos.y = desired.y;
		}
		return pos;
	}
}
