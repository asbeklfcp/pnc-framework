using System.Reflection;
using HarmonyLib;
using PixelCrushers.GridController;
using UnityEngine;

namespace PncEdi;

/// <summary>
/// Gameplay tweaks: consumable potions apply on world pickup instead of entering the inventory.
/// If the potion cannot be used yet (e.g. full HP and no heat to cool), vanilla inventory add still runs.
/// </summary>
[HarmonyPatch]
internal static class PotionAutoUse
{
	private static readonly MethodInfo PerformPickupMethod = AccessTools.Method(typeof(InventoryItemPickup), "PerformPickup");

	private static readonly MethodInfo FinishPickupMethod = AccessTools.Method(typeof(InventoryItemPickup), "FinishPickup");

	internal static bool Active => OverkeksFeatureRuntime.Potions;

	/// <summary>Instance id of the last pickup that auto-used successfully this frame (magnet despawn fallback).</summary>
	internal static int LastAutoUsedPickupId;

	internal static bool IsAutoUsePotion(InventoryItem item)
	{
		if (item == null || !item.IsConsumable)
		{
			return false;
		}
		if (item.instantHeatReduction > 0f || item.instantHealthChange != 0 || item.healAmount > 0)
		{
			return true;
		}
		if (item.effectType == EffectType.Heal)
		{
			return true;
		}
		string name = item.itemName;
		return !string.IsNullOrEmpty(name) && name.IndexOf("potion", System.StringComparison.OrdinalIgnoreCase) >= 0;
	}

	private static ConsumableSystem ResolveConsumable(InventoryItemPickup pickup)
	{
		GameObject player = Traverse.Create((object)pickup).Field("player").GetValue<GameObject>();
		if (player != null)
		{
			ConsumableSystem fromPlayer = player.GetComponent<ConsumableSystem>();
			if (fromPlayer != null)
			{
				return fromPlayer;
			}
			fromPlayer = player.GetComponentInChildren<ConsumableSystem>();
			if (fromPlayer != null)
			{
				return fromPlayer;
			}
		}
		InventoryUI ui = Traverse.Create((object)pickup).Field("playerInventoryUI").GetValue<InventoryUI>();
		if (ui != null)
		{
			ConsumableSystem fromUi = ui.GetComponent<ConsumableSystem>();
			if (fromUi != null)
			{
				return fromUi;
			}
			fromUi = ui.GetComponentInChildren<ConsumableSystem>();
			if (fromUi != null)
			{
				return fromUi;
			}
		}
		return Object.FindAnyObjectByType<ConsumableSystem>();
	}

	private static void DetachFromChestIfNeeded(InventoryItemPickup pickup)
	{
		Traverse t = Traverse.Create((object)pickup);
		if (!t.Field("isHeldInChest").GetValue<bool>())
		{
			return;
		}
		pickup.transform.SetParent(null);
		Rigidbody body = t.Field("itemRigidbody").GetValue<Rigidbody>();
		if (body != null)
		{
			body.isKinematic = false;
			body.useGravity = true;
		}
		t.Field("isHeldInChest").SetValue(false);
	}

	/// <summary>
	/// Consume-on-pickup must remove the world object even when Traverse.Method fails
	/// (spawn magnets would otherwise re-apply the potion every frame).
	/// </summary>
	internal static void CompleteWorldPickup(InventoryItemPickup pickup)
	{
		if (pickup == null)
		{
			return;
		}
		Traverse t = Traverse.Create((object)pickup);
		if (t.Field("wasPickedUp").GetValue<bool>())
		{
			return;
		}
		if (PerformPickupMethod != null)
		{
			PerformPickupMethod.Invoke(pickup, null);
			if (t.Field("wasPickedUp").GetValue<bool>())
			{
				return;
			}
		}
		t.Field("wasPickedUp").SetValue(true);
		GameObject prompt = t.Field("pickupPrompt").GetValue<GameObject>();
		if (prompt != null)
		{
			prompt.SetActive(false);
		}
		if (FinishPickupMethod != null)
		{
			FinishPickupMethod.Invoke(pickup, null);
			return;
		}
		if (PickupPoolManager.Instance != null)
		{
			PickupPoolManager.Instance.ReturnItemPickup(pickup.gameObject);
		}
		else
		{
			Object.Destroy(pickup.gameObject);
		}
	}

	[HarmonyPatch(typeof(InventoryItemPickup), "TryPickup")]
	[HarmonyPrefix]
	[HarmonyPriority(400)]
	private static bool TryPickup_AutoUsePotion_Prefix(InventoryItemPickup __instance)
	{
		if (!Active || __instance == null)
		{
			return true;
		}
		Traverse t = Traverse.Create((object)__instance);
		if (t.Field("wasPickedUp").GetValue<bool>())
		{
			return false;
		}
		InventoryItem item = __instance.GetItemData();
		if (!IsAutoUsePotion(item))
		{
			return true;
		}
		if (t.Field("playerInventoryUI").GetValue<InventoryUI>() == null)
		{
			return true;
		}
		ConsumableSystem consumable = ResolveConsumable(__instance);
		if (consumable == null)
		{
			return true;
		}
		DetachFromChestIfNeeded(__instance);
		if (!consumable.TryUseConsumable(item))
		{
			Plugin.DBG("POTION", "auto-use deferred (cannot use yet) '" + item.itemName + "' -> inventory");
			return true;
		}
		Plugin.DBG("POTION", "auto-used on pickup '" + item.itemName + "'");
		LastAutoUsedPickupId = __instance.GetInstanceID();
		CompleteWorldPickup(__instance);
		return false;
	}
}
