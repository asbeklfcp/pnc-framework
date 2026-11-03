using System;
using System.Collections.Generic;
using HarmonyLib;
using PixelCrushers.GridController;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PncEdi;

// Heat potions no longer shed a flat amount of heat. They remove horny locks instead, and
// the heat those locks were holding up goes with them.
//
// The flat cooling was close to useless once locks existed: HeatLockSystem.CoolHeat_Prefix
// trims any cooling at the lock floor, so a potion drunk while locked was partly or wholly
// discarded. Removing locks is the only thing that actually lowers that floor.
//
// Vanilla's own cooling call is suppressed rather than the item asset being edited -
// instantHeatReduction lives on a shared ScriptableObject, so writing to it would leak
// across every copy of the item.
[HarmonyPatch]
public static class HeatPotionLocks
{
	private static bool _suppressCooling;
	internal static bool SuppressCooling => _suppressCooling;

	internal static bool Enabled
	{
		get
		{
			if (!Plugin.GameplayTweaksEnabled || !HeatLockSystem.Enabled)
			{
				return false;
			}
			string configured = (OverkeksMode.UsesRecipe ? OverkeksConfig.CfgHeatPotionLockRemoval : Plugin.CfgHeatPotionLockRemoval)?.Value;
			return !string.IsNullOrWhiteSpace(configured);
		}
	}

	// "small heat potion=2;medium heat potion=5;large heat potion=10", matched
	// case-insensitively against the item name. Longest match wins, so a generic key cannot
	// shadow a more specific one.
	private static int ResolveLocks(string itemName)
	{
		if (string.IsNullOrWhiteSpace(itemName))
		{
			return 0;
		}
		string name = itemName.Trim();
		int locks = 0;
		int bestKeyLength = 0;
		// Longest matching key wins, tracked as a running maximum rather than by sorting: this
		// runs on a potion pickup, not on load, and the table is a handful of entries.
		foreach (KeyValuePair<string, string> pair in ConfigMap.Pairs((OverkeksMode.UsesRecipe ? OverkeksConfig.CfgHeatPotionLockRemoval : Plugin.CfgHeatPotionLockRemoval)?.Value))
		{
			if (int.TryParse(pair.Value, out var parsed) && pair.Key.Length > bestKeyLength
			    && name.IndexOf(pair.Key, StringComparison.OrdinalIgnoreCase) >= 0)
			{
				bestKeyLength = pair.Key.Length;
				locks = parsed;
			}
		}
		return Mathf.Max(0, locks);
	}

	[HarmonyPatch(typeof(ConsumableSystem), "ApplyConsumableEffects")]
	[HarmonyPrefix]
	public static void ApplyConsumableEffects_Prefix(InventoryItem item)
	{
		if (!Enabled || item == null || !(item.instantHeatReduction > 0f))
		{
			return;
		}
		int locks = ResolveLocks(item.itemName);
		if (locks <= 0)
		{
			// Some other cooling item: leave vanilla's behaviour alone.
			return;
		}
		// Suppress before removing, so the removal's own SetHeat is not mistaken for the
		// vanilla cooling call we are replacing.
		_suppressCooling = true;
		int removed = HeatLockSystem.RemoveLocks(locks, "potion '" + item.itemName + "'");
		if (removed <= 0)
		{
			Plugin.DBG("HEAT-POTION", "'" + item.itemName + "' had no horny to remove");
		}
	}

	// Finalizer rather than postfix: a postfix is skipped if the original throws, which would
	// leave every later CoolHeat call in the run suppressed.
	[HarmonyPatch(typeof(ConsumableSystem), "ApplyConsumableEffects")]
	[HarmonyFinalizer]
	public static void ApplyConsumableEffects_Finalizer()
	{
		_suppressCooling = false;
	}
}
