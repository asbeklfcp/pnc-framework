using System;
using System.Collections.Generic;
using HarmonyLib;
using PixelCrushers.GridController;
using UnityEngine;

namespace PncEdi;

/// <summary>
/// Drops a small capped number of heat-reduction potions per room/arena spawner
/// (near random spawn points — not 1–2 per gap-fill point), and auto-collects them
/// when the player walks close (PotionAutoUse still applies on pickup).
/// </summary>
internal static class SpawnHeatPotionDrop
{
	private const float PlaceRadiusMin = 1.1f;

	private const float PlaceRadiusMax = 2.6f;

	private const float MaxFloorYDelta = 2.25f;

	private static readonly HashSet<int> SeededSpawners = new HashSet<int>();

	private static readonly List<Transform> ValidPointsScratch = new List<Transform>(32);

	private static InventoryItem _cachedHeatPotion;

	private static bool _resolvedHeatPotion;

	internal static bool Active => OverkeksFeatureRuntime.Drops;

	internal static void ResetForScene()
	{
		SeededSpawners.Clear();
	}

	internal static void SeedNearSpawner(MonoBehaviour spawner)
	{
		if (!Active || spawner == null)
		{
			return;
		}
		if (!SeededSpawners.Add(spawner.GetInstanceID()))
		{
			return;
		}
		int maxDrops = OverkeksFeatureRuntime.PotionCount;
		if (maxDrops <= 0)
		{
			return;
		}
		InventoryItem potion = ResolveHeatPotion();
		if (potion == null)
		{
			Plugin.DBG("SPAWN-POTION", "no heat potion InventoryItem found — skip drops");
			return;
		}
		Transform[] positions = Traverse.Create((object)spawner).Field("spawnPositions").GetValue<Transform[]>();
		if (positions == null || positions.Length == 0)
		{
			return;
		}
		ValidPointsScratch.Clear();
		for (int i = 0; i < positions.Length; i++)
		{
			Transform point = positions[i];
			if (point != null)
			{
				ValidPointsScratch.Add(point);
			}
		}
		if (ValidPointsScratch.Count == 0)
		{
			return;
		}
		Shuffle(ValidPointsScratch);
		int target = Mathf.Min(maxDrops, ValidPointsScratch.Count);
		if (target > 1)
		{
			target = UnityEngine.Random.Range(1, target + 1);
		}
		float pickupRange = Mathf.Max(1.5f, OverkeksFeatureRuntime.PotionRange);
		int total = 0;
		for (int i = 0; i < ValidPointsScratch.Count && total < target; i++)
		{
			if (TryPlacePotion(potion, ValidPointsScratch[i].position, pickupRange))
			{
				total++;
			}
		}
		if (total > 0)
		{
			Plugin.DBG("SPAWN-POTION", "seeded " + total + "/" + maxDrops + " '" + potion.itemName + "' on " + spawner.GetType().Name + " (" + ValidPointsScratch.Count + " spawn point(s))");
		}
	}

	private static void Shuffle(List<Transform> list)
	{
		for (int i = list.Count - 1; i > 0; i--)
		{
			int j = UnityEngine.Random.Range(0, i + 1);
			Transform tmp = list[i];
			list[i] = list[j];
			list[j] = tmp;
		}
	}

	private static bool TryPlacePotion(InventoryItem potion, Vector3 around, float pickupRange)
	{
		if (!TryPickNearbyFloor(around, out Vector3 pos))
		{
			pos = around + Vector3.up * 0.35f;
		}
		else
		{
			pos += Vector3.up * 0.35f;
		}
		GameObject go = InventoryItemPickup.CreatePickup(potion, pos);
		if (go == null)
		{
			return false;
		}
		InventoryItemPickup pickup = go.GetComponent<InventoryItemPickup>();
		if (pickup == null)
		{
			return false;
		}
		OverkeksFeatureRuntime.Own(go);
		Rigidbody body = go.GetComponent<Rigidbody>();
		if (body != null)
		{
			body.isKinematic = true;
			body.useGravity = false;
		}
		SpawnHeatPotionMagnet magnet = go.GetComponent<SpawnHeatPotionMagnet>();
		if (magnet == null)
		{
			magnet = go.AddComponent<SpawnHeatPotionMagnet>();
		}
		magnet.Range = pickupRange;
		return true;
	}

	private static bool TryPickNearbyFloor(Vector3 around, out Vector3 snapped)
	{
		for (int i = 0; i < 12; i++)
		{
			float f = UnityEngine.Random.Range(0f, 360f) * (MathF.PI / 180f);
			float dist = UnityEngine.Random.Range(PlaceRadiusMin, PlaceRadiusMax);
			Vector3 candidate = around + new Vector3(Mathf.Cos(f) * dist, 0f, Mathf.Sin(f) * dist);
			if (!Physics.Raycast(candidate + Vector3.up * 4f, Vector3.down, out RaycastHit hit, 16f, -5, QueryTriggerInteraction.Ignore))
			{
				continue;
			}
			snapped = hit.point;
			if (snapped.y < around.y - MaxFloorYDelta || snapped.y > around.y + MaxFloorYDelta)
			{
				continue;
			}
			if (Physics.CheckCapsule(snapped + Vector3.up * 0.6f, snapped + Vector3.up * 1.7f, 0.3f, -5, QueryTriggerInteraction.Ignore))
			{
				continue;
			}
			if (SpawnWalkability.TrySnapWalkable(snapped, around.y, MaxFloorYDelta, out Vector3 walkable))
			{
				snapped = walkable;
			}
			return true;
		}
		snapped = around;
		return false;
	}

	private static InventoryItem ResolveHeatPotion()
	{
		if (_resolvedHeatPotion)
		{
			return _cachedHeatPotion;
		}
		_resolvedHeatPotion = true;
		InventoryItem[] items = Resources.FindObjectsOfTypeAll<InventoryItem>();
		InventoryItem small = null;
		InventoryItem any = null;
		float anyCool = float.MaxValue;
		for (int i = 0; i < items.Length; i++)
		{
			InventoryItem item = items[i];
			if (item == null || string.IsNullOrEmpty(item.itemName) || item.instantHeatReduction <= 0f)
			{
				continue;
			}
			string name = item.itemName;
			if (name.IndexOf("heat", StringComparison.OrdinalIgnoreCase) < 0)
			{
				continue;
			}
			if (name.IndexOf("small", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				small = item;
				break;
			}
			if (item.instantHeatReduction < anyCool)
			{
				anyCool = item.instantHeatReduction;
				any = item;
			}
		}
		_cachedHeatPotion = small != null ? small : any;
		return _cachedHeatPotion;
	}
}

/// <summary>
/// Pulls a world heat potion into the player once they enter range (no look-at required).
/// </summary>
internal sealed class SpawnHeatPotionMagnet : MonoBehaviour
{
	internal float Range = 3.5f;

	private InventoryItemPickup _pickup;

	private Transform _player;

	private GameObject _playerGo;

	private void Awake()
	{
		_pickup = GetComponent<InventoryItemPickup>();
	}

	private void OnDisable()
	{
		// Pooled pickups are reused; do not leave this magnet on swords/other drops.
		UnityEngine.Object.Destroy(this);
	}

	private void Update()
	{
		if (_pickup == null || _pickup.IsPickedUp || !SpawnHeatPotionDrop.Active)
		{
			if (_pickup != null && _pickup.IsPickedUp)
			{
				UnityEngine.Object.Destroy(this);
			}
			return;
		}
		if (!TryGetPlayer(out Transform player, out GameObject playerGo))
		{
			return;
		}
		Vector3 delta = player.position - transform.position;
		delta.y = 0f;
		if (delta.sqrMagnitude > Range * Range)
		{
			return;
		}
		_pickup.OnLookedAt(playerGo);
		int pickupId = _pickup.GetInstanceID();
		_pickup.OnInteracted();
		// FinishPickup may have pooled/disabled this GO already (PoolReset clears wasPickedUp).
		if (!isActiveAndEnabled || _pickup == null)
		{
			return;
		}
		// Only force-despawn when auto-use already applied but the world object stayed.
		if (!_pickup.IsPickedUp && PotionAutoUse.LastAutoUsedPickupId == pickupId)
		{
			PotionAutoUse.CompleteWorldPickup(_pickup);
		}
		if (_pickup == null || _pickup.IsPickedUp)
		{
			UnityEngine.Object.Destroy(this);
		}
	}

	private bool TryGetPlayer(out Transform player, out GameObject playerGo)
	{
		if (_player != null)
		{
			player = _player;
			playerGo = _playerGo;
			return true;
		}
		PlayerStats stats = UnityEngine.Object.FindAnyObjectByType<PlayerStats>();
		if (stats == null)
		{
			player = null;
			playerGo = null;
			return false;
		}
		_player = stats.transform;
		_playerGo = stats.gameObject;
		player = _player;
		playerGo = _playerGo;
		return true;
	}
}
