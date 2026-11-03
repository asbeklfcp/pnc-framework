using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace PncEdi;

[HarmonyPatch]
public static class ClassSelectionHooks
{
	private static float GetArmorHeatBonus(ClassSelectionUI ui, PlayerClass playerClass)
	{
		return Traverse.Create((object)ui).Method("GetArmorHeatBonus", new object[1] { playerClass }).GetValue<float>();
	}

	/// <summary>
	/// What this class will actually play at, under the profile in force.
	///
	/// The patches stay installed under every profile (that is `GameplayProfiles`' whole design -
	/// no patch is added or removed mid-run), so the gate has to live here rather than in whether
	/// the hook exists. Without it the screen shows a multiplied number under `Vanilla` and
	/// `GodMode`, neither of which ever calls `ApplyToPlayerHeat` - a figure that cannot happen in
	/// that run (§158).
	/// </summary>
	private static float DisplayHeatCapacity(ClassSelectionUI ui, PlayerClass playerClass)
	{
		float armorHeatBonus = GetArmorHeatBonus(ui, playerClass);
		return GameplayProfiles.UsesClassHeatScaling
			? ClassHeatMultipliers.GetEffectiveHeatCapacity(playerClass, armorHeatBonus)
			: ClassHeatMultipliers.GetVanillaHeatCapacity(playerClass, armorHeatBonus);
	}

	[HarmonyPatch(typeof(ClassSelectionUI), "CalculateBarNormalizationValues")]
	[HarmonyPostfix]
	public static void CalculateBarNormalizationValues_Postfix(ClassSelectionUI __instance)
	{
		Traverse traverse = Traverse.Create((object)__instance);
		List<PlayerClass> classes = traverse.Field("availableClasses").GetValue<List<PlayerClass>>();
		if (classes == null)
		{
			return;
		}
		float maxCapacity = 0f;
		foreach (PlayerClass playerClass in classes)
		{
			if (!(playerClass == null))
			{
				float effectiveHeatCapacity = DisplayHeatCapacity(__instance, playerClass);
				if (effectiveHeatCapacity > maxCapacity)
				{
					maxCapacity = effectiveHeatCapacity;
				}
			}
		}
		traverse.Field("maxEffectiveHeat").SetValue((object)maxCapacity);
	}

	[HarmonyPatch(typeof(ClassSelectionUI), "DisplayClassDetails")]
	[HarmonyPostfix]
	public static void DisplayClassDetails_Postfix(ClassSelectionUI __instance, PlayerClass playerClass)
	{
		if (!(playerClass == null))
		{
			float effectiveHeatCapacity = DisplayHeatCapacity(__instance, playerClass);
			Traverse traverse = Traverse.Create((object)__instance);
			float maxEffectiveHeat = traverse.Field("maxEffectiveHeat").GetValue<float>();
			Image heatBarFill = traverse.Field("heatBarFill").GetValue<Image>();
			if (heatBarFill != null)
			{
				heatBarFill.fillAmount = ((maxEffectiveHeat > 0f) ? (effectiveHeatCapacity / maxEffectiveHeat) : 1f);
			}
			object heatValueText = traverse.Field("heatValueText").GetValue<object>();
			if (heatValueText != null)
			{
				Traverse.Create(heatValueText).Property("text", (object[])null).SetValue((object)Mathf.RoundToInt(effectiveHeatCapacity).ToString());
			}
		}
	}

	[HarmonyPatch(typeof(ClassSelectionUI), "GetClassStatsText")]
	[HarmonyPostfix]
	public static void ClassSelection_GetClassStatsText_Postfix(PlayerClass playerClass, ref string __result)
	{
		AppendHeatModLine(playerClass, ref __result);
	}

	[HarmonyPatch(typeof(ItemUnlocksScreenUI), "GetClassStatsText")]
	[HarmonyPostfix]
	public static void ItemUnlocks_GetClassStatsText_Postfix(PlayerClass playerClass, ref string __result)
	{
		AppendHeatModLine(playerClass, ref __result);
	}

	private static void AppendHeatModLine(PlayerClass playerClass, ref string stats)
	{
		if (!(playerClass == null) && GameplayProfiles.UsesClassHeatScaling)
		{
			float multiplier = ClassHeatMultipliers.GetMultiplier(playerClass.className);
			if (!(multiplier <= 1.0001f))
			{
				int capacity = Mathf.RoundToInt(ClassHeatMultipliers.GetVanillaHeatCapacity(playerClass) * multiplier);
				stats += $"\n<b>Max Heat (EDI ×{multiplier:G}):</b> {capacity}\n";
			}
		}
	}
}
