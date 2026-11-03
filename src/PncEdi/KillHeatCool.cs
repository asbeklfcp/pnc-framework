using System;
using HarmonyLib;
using UnityEngine;

namespace PncEdi;

/// <summary>
/// Cool player heat when an enemy is defeated: 10 for gravy/serpent/baphomet/dragon/wendigo/blinded_beast, else 5.
/// </summary>
[HarmonyPatch]
internal static class KillHeatCool
{
	private static int _lastCoolId;

	private static float _lastCoolAt = -999f;

	internal static bool Active => OverkeksFeatureRuntime.KillCooling;

	internal static void OnEnemyDefeated(GameObject enemy)
	{
		if (!Active || enemy == null)
		{
			return;
		}
		int id = enemy.GetInstanceID();
		if (id == _lastCoolId && Time.unscaledTime - _lastCoolAt < 0.35f)
		{
			return;
		}
		PlayerStats player = UnityEngine.Object.FindAnyObjectByType<PlayerStats>();
		if (player == null || !player.IsAlive)
		{
			return;
		}
		string key = ResolveKey(enemy);
		float amount = IsHighValueKill(key)
			? Mathf.Max(0f, OverkeksFeatureRuntime.BossCooling)
			: Mathf.Max(0f, OverkeksFeatureRuntime.NormalCooling);
		if (amount <= 0f)
		{
			return;
		}
		_lastCoolId = id;
		_lastCoolAt = Time.unscaledTime;
		float before=player.CurrentHeat;
		player.CoolHeat(amount);
		Plugin.DBG("KILL-HEAT", "profile="+GameplayProfiles.Current+" requested="+amount.ToString("0.#")+" before="+before.ToString("0.###")+" after="+player.CurrentHeat.ToString("0.###")+" actual="+(before-player.CurrentHeat).ToString("0.###") + " from '" + key + "' (" + NameRemap.StripCloneSuffix(enemy.name) + ")");
	}

	private static string ResolveKey(GameObject enemy)
	{
		MonoBehaviour ai = EnemyAiTypes.First(enemy);
		string key = OverkeksFeatureRuntime.ResolveIdentity(enemy, ai);
		if (string.IsNullOrEmpty(key))
		{
			key = NameRemap.ResolveEnemyKey(enemy.name);
		}
		return key ?? "";
	}

	private static bool IsHighValueKill(string key)
	{
		if (string.IsNullOrEmpty(key))
		{
			return false;
		}
		if (key.Equals("gravy", StringComparison.OrdinalIgnoreCase)
			|| key.Equals("serpent", StringComparison.OrdinalIgnoreCase)
			|| key.Equals("snake", StringComparison.OrdinalIgnoreCase)
			|| key.Equals("baphomet", StringComparison.OrdinalIgnoreCase)
			|| key.Equals("dragon", StringComparison.OrdinalIgnoreCase)
			|| key.Equals("wendigo", StringComparison.OrdinalIgnoreCase)
			|| key.Equals("blinded_beast", StringComparison.OrdinalIgnoreCase)
			|| key.Equals("blindedbeast", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		return false;
	}

	[HarmonyTargetMethods]
	private static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> Targets()
	{
		foreach (var type in new[] { typeof(EnemyAI),typeof(DragonEnemyAI),typeof(ProximityDragonEnemyAI),typeof(ProjectileEnemyAI),typeof(ChargingEnemyAI),typeof(SpinningEnemyAI),typeof(BrawlerEnemyAI) })
		{
			var method=AccessTools.Method(type,"Die");
			if(method==null) throw new System.MissingMethodException(type.FullName,"Die");
			yield return method;
		}
	}

	[HarmonyPostfix]
	[HarmonyPriority(Priority.Last)]
	private static void Die_Postfix(MonoBehaviour __instance)
	{
		if (__instance == null)
		{
			return;
		}
		Traverse t = Traverse.Create((object)__instance);
		if (t.Field("isDead").FieldExists() && !t.Field("isDead").GetValue<bool>())
		{
			return;
		}
		OnEnemyDefeated(__instance.gameObject);
	}
}
