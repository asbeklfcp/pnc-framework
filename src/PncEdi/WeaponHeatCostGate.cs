using HarmonyLib;
using PixelCrushers.GridController;
using UnityEngine;

namespace PncEdi;

/// <summary>
/// Weapons/spells no longer fill the heat bar when casting. The would-be heat cost
/// instead briefly slows movement to a fixed crawl speed, without touching current
/// heat, overheat state, or H-scene gates.
/// Stun starts with the cast/attack animation (StartAttack / StartSpecialAttack), not on projectile hit.
/// Movement stun caps horizontal speed on FirstPersonController velocity so doom-pace /
/// class walk multipliers cannot cancel the slowdown. Vanilla heat overheat stays independent.
/// </summary>
[HarmonyPatch]
internal static class WeaponHeatCostGate
{
	private const float BaseHeatPerShot = 5f;

	private const float ProjectileHeatMultiplier = 1.5f;

	/// <summary>
	/// Absolute horizontal speed (units/s) while stunned — ~¼ of vanilla base walk (5).
	/// Caps magnitude; ignores PlayerMoveSpeedMultiplier / class buffs so stun stays a crawl.
	/// </summary>
	private const float StunMaxSpeed = 1.25f;

	/// <summary>Floor for any heat-cost cast — light spells still feel the cast recovery.</summary>
	private const float MinStunSeconds = 0.5f;

	/// <summary>Ceiling for a single cast — extreme (≈100 heat) spells land here.</summary>
	private const float MaxStunSeconds = 1f;

	/// <summary>Heat cost that maps to MaxStunSeconds. Below this scales down toward MinStunSeconds.</summary>
	private const float HeatForMaxStun = 100f;

	private static bool _suppressWeaponGenerateHeat;

	private static float _stunUntil = float.NegativeInfinity;

	internal static bool Active => OverkeksFeatureRuntime.Casts;

	internal static bool IsWeaponStunActive => Active && Time.time < _stunUntil;

	internal static void ResetForNewScene()
	{
		_stunUntil = float.NegativeInfinity;
		_suppressWeaponGenerateHeat = false;
	}

	/// <summary>Mirrors DualWieldingSystem.CalculateHeatCost (without rapid-fire).</summary>
	private static float EstimateAttackHeatCost(WeaponData weapon)
	{
		if (weapon == null || !weapon.UsesHeat)
		{
			return 0f;
		}
		float num = BaseHeatPerShot;
		if (weapon.heatPerShot > 0f)
		{
			num = weapon.heatPerShot;
		}
		if (weapon.IsProjectileWeapon)
		{
			num *= ProjectileHeatMultiplier;
			if (weapon.ProjectileCount > 1)
			{
				num *= weapon.ProjectileCount;
			}
		}
		return num;
	}

	private static float EstimateSpecialHeatCost(WeaponData weapon)
	{
		if (weapon == null)
		{
			return 0f;
		}
		if (weapon.SpecialAttackHeatCost > 0f)
		{
			return weapon.SpecialAttackHeatCost;
		}
		if (!weapon.UsesHeat)
		{
			return 0f;
		}
		return EstimateAttackHeatCost(weapon) * 1.5f;
	}

	private static void ApplyWeaponStun(float heatAmount)
	{
		if (heatAmount <= 0f)
		{
			return;
		}
		_stunUntil = ResourcePolicy.RecoveryEnd(_stunUntil,Time.time,heatAmount);
	}

	[HarmonyPatch(typeof(DualWieldingSystem), "CalculateHeatCost")]
	[HarmonyPostfix]
	private static void CalculateHeatCost_Postfix(ref float __result)
	{
		if (Active)
		{
			__result = 0f;
		}
	}

	/// <summary>Stun when the attack/cast animation starts — not when the projectile lands.</summary>
	[HarmonyPatch(typeof(DualWieldingSystem), "StartAttack")]
	[HarmonyPostfix]
	private static void StartAttack_Postfix(WeaponSlot slot)
	{
		if (!Active || slot == null || slot.weaponData == null)
		{
			return;
		}
		WeaponData weapon = slot.weaponData;
		if (weapon.UsesHeat && !weapon.HeatOnSpecialOnly)
		{
			ApplyWeaponStun(EstimateAttackHeatCost(weapon));
		}
	}

	[HarmonyPatch(typeof(DualWieldingSystem), "StartSpecialAttack")]
	[HarmonyPostfix]
	private static void StartSpecialAttack_Postfix(WeaponSlot slot)
	{
		if (!Active || slot == null || slot.weaponData == null)
		{
			return;
		}
		WeaponData weapon = slot.weaponData;
		if (weapon.UsesHeat || weapon.SpecialAttackHeatCost > 0f)
		{
			ApplyWeaponStun(EstimateSpecialHeatCost(weapon));
		}
	}

	[HarmonyPatch(typeof(DualWieldingSystem), "GenerateHeatForAttack")]
	[HarmonyPrefix]
	private static bool GenerateHeatForAttack_Prefix()
	{
		// Heat already converted to stun at StartAttack / StartSpecialAttack.
		return !Active;
	}

	[HarmonyPatch(typeof(DualWieldingSystem), "OnSpecialAttackHit")]
	[HarmonyPrefix]
	private static void OnSpecialAttackHit_Prefix()
	{
		if (Active)
		{
			_suppressWeaponGenerateHeat = true;
		}
	}

	[HarmonyPatch(typeof(DualWieldingSystem), "OnSpecialAttackHit")]
	[HarmonyFinalizer]
	private static void OnSpecialAttackHit_Finalizer()
	{
		_suppressWeaponGenerateHeat = false;
	}

	[HarmonyPatch(typeof(PlayerStats), "GenerateHeat")]
	[HarmonyPrefix]
	[HarmonyPriority(800)]
	private static bool GenerateHeat_SkipWeaponCost_Prefix()
	{
		if (Active && _suppressWeaponGenerateHeat)
		{
			return false;
		}
		return true;
	}

	[HarmonyPatch(typeof(PlayerStats), "CanGenerateHeat")]
	[HarmonyPrefix]
	private static bool CanGenerateHeat_AllowCasting_Prefix(ref bool __result)
	{
		if (!Active)
		{
			return true;
		}
		__result = true;
		return false;
	}

	[HarmonyPatch(typeof(WeaponData), "GetHeatCost")]
	[HarmonyPostfix]
	private static void WeaponData_GetHeatCost_Postfix(ref float __result)
	{
		if (Active)
		{
			__result = 0f;
		}
	}

	[HarmonyPatch(typeof(WeaponData), "GetHeatCostWithRapidFire")]
	[HarmonyPostfix]
	private static void WeaponData_GetHeatCostWithRapidFire_Postfix(ref float __result)
	{
		if (Active)
		{
			__result = 0f;
		}
	}

	/// <summary>
	/// Cap to a fixed crawl speed — ignores PlayerMoveSpeedMultiplier / class buffs already baked into velocity.
	/// Vanilla overheat (overflowMovementPenalty) is untouched.
	/// </summary>
	[HarmonyPatch(typeof(FirstPersonController), "CalculateMovementVelocity")]
	[HarmonyPostfix]
	private static void FirstPersonController_CalculateMovementVelocity_Postfix(ref Vector3 __result)
	{
		if (!IsWeaponStunActive)
		{
			return;
		}
		// Preserve jump/fall velocity; the original's magnitude cap also slowed Y
		// despite documenting a horizontal crawl. This is an explicit adaptation.
		float scale=ResourcePolicy.HorizontalScale(__result.x,__result.z,StunMaxSpeed);
		__result.x*=scale; __result.z*=scale;
	}
}
