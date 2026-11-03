using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PncEdi;

public static class ClassHeatMultipliers
{
	private static Dictionary<string, float> _multipliers = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

	public static void Reload()
	{
		_multipliers.Clear();
		foreach (KeyValuePair<string, string> pair in ConfigMap.Pairs((OverkeksMode.UsesRecipe ? OverkeksConfig.CfgClassHeatMultipliers : Plugin.CfgClassHeatMultipliers)?.Value))
		{
			if (float.TryParse(pair.Value, out var multiplier))
			{
				_multipliers[pair.Key] = Mathf.Max(0.01f, multiplier);
			}
		}
	}

	public static float GetMultiplier(string className)
	{
		if (string.IsNullOrEmpty(className))
		{
			return 1f;
		}
		if (_multipliers.Count == 0)
		{
			Reload();
		}
		if (_multipliers.TryGetValue(className, out var value))
		{
			return value;
		}
		return 1f;
	}

	public static float GetVanillaHeatCapacity(PlayerClass playerClass, float armorHeatBonus = 0f)
	{
		if (playerClass == null)
		{
			return 100f;
		}
		return 100f * playerClass.heatCapacityMultiplier + playerClass.bonusHeatCapacity + armorHeatBonus;
	}

	/// <summary>
	/// Put the multiplier and the armour bonus together the way the *runtime* does, which is not
	/// the way they read on paper (§148, confirmed in play §158).
	///
	/// `PlayerStats.ApplyStatModifiers` multiplies the class base, and our
	/// <see cref="ApplyToPlayerHeat"/> rides on that; only afterwards does
	/// `ArmorData.ApplyStatModifiers` *add* its heat bonus. So the bonus is never multiplied, and
	/// a display that multiplies it promises capacity the run cannot deliver - the shipped x2 mage
	/// showed 300 and played at 275, the 25 points of armour being counted twice.
	///
	/// Pure and free of `PlayerClass` so `code/tests` can check the arithmetic without a Unity
	/// object.
	/// </summary>
	public static float ScaleCapacity(float vanillaCapacity, float armorHeatBonus, float multiplier)
	{
		return vanillaCapacity * multiplier + armorHeatBonus;
	}

	public static float GetEffectiveHeatCapacity(PlayerClass playerClass, float armorHeatBonus = 0f)
	{
		return ScaleCapacity(GetVanillaHeatCapacity(playerClass), armorHeatBonus, GetMultiplier(playerClass?.className));
	}

	public static void ApplyToPlayerHeat(PlayerClass playerClass, PlayerStats playerStats)
	{
		if (!(playerStats == null))
		{
			float multiplier = GetMultiplier(playerClass?.className);
			if (!(multiplier <= 1.0001f))
			{
				playerStats.SetMaxHeat(playerStats.MaxHeat * multiplier);
			}
		}
	}
}
