using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace PncEdi;

internal static class OverkeksChaserBossHelper
{
	internal static bool IsChaserBossPrefab(GameObject prefab)
	{
		if (prefab == null)
		{
			return false;
		}
		if (prefab.GetComponent<DragonEnemyAI>() != null || prefab.GetComponent<ProximityDragonEnemyAI>() != null)
		{
			return true;
		}
		string name = NameRemap.StripCloneSuffix(prefab.name);
		return name.IndexOf("wendigo", StringComparison.OrdinalIgnoreCase) >= 0
			|| name.IndexOf("dragon", StringComparison.OrdinalIgnoreCase) >= 0;
	}

	/// <summary>Full chase activation (vanilla: move to spawn point, then AI resumes chase via TransitionToChasing).</summary>
	internal static void ActivateForChase(GameObject enemy)
	{
		if (enemy == null)
		{
			return;
		}
		enemy.SetActive(true);
		OverkeksMode.PrepareChaserBody(enemy);
		ChaserDamageHooks.ApplyHealth(enemy);
		TryBindPlayer(enemy);
		PrepareChaserAiComponents(enemy);
		StartChaseOnAiComponents(enemy);
		SyncFollowerEntity(enemy);
		if (Plugin.Instance != null)
		{
			OverkeksMode.StartCoroutine(DelayedChaseWake(enemy));
		}
	}

	/// <summary>Legacy entry — same as ActivateForChase.</summary>
	internal static void WakeForChase(GameObject enemy)
	{
		ActivateForChase(enemy);
	}

	/// <summary>Stops movement while hidden at ChaserHolder (vanilla PauseAIBehavior).</summary>
	internal static void PauseForStaging(GameObject enemy)
	{
		if (enemy == null)
		{
			return;
		}
		if (InvokeAiMethod(enemy, "PauseAIBehavior"))
		{
			return;
		}
		InvokeAiMethod(enemy, "TransitionToIdle");
		InvokeAiMethod(enemy, "StopPathfinding");
	}

	private static IEnumerator DelayedChaseWake(GameObject enemy)
	{
		yield return null;
		if (enemy == null)
		{
			yield break;
		}
		TryBindPlayer(enemy);
		StartChaseOnAiComponents(enemy);
		SyncFollowerEntity(enemy);
	}

	private static void PrepareChaserAiComponents(GameObject enemy)
	{
		MonoBehaviour[] behaviours = GetChaserAiComponents(enemy);
		for (int i = 0; i < behaviours.Length; i++)
		{
			MonoBehaviour ai = behaviours[i];
			if (ai == null)
			{
				continue;
			}
			ai.enabled = true;
			EnemyKeepAliveHelper.ClearDeadState(ai);
			InvokeAiMethod(ai, "StopGrab");
			InvokeAiMethod(ai, "StopAttack");
			Traverse traverse = Traverse.Create(ai);
			if (traverse.Field("isAttemptingGrab").FieldExists())
			{
				traverse.Field("isAttemptingGrab").SetValue(false);
			}
			if (traverse.Field("isDashing").FieldExists())
			{
				traverse.Field("isDashing").SetValue(false);
			}
			if (traverse.Field("attackCoroutine").FieldExists())
			{
				traverse.Field("attackCoroutine").SetValue(null);
			}
			if (traverse.Field("grabCoroutine").FieldExists())
			{
				traverse.Field("grabCoroutine").SetValue(null);
			}
			if (traverse.Method("ResetGrabFlag").MethodExists())
			{
				traverse.Method("ResetGrabFlag").GetValue();
			}
			if (traverse.Field("hasTriggeredDragonGrab").FieldExists())
			{
				traverse.Field("hasTriggeredDragonGrab").SetValue(false);
			}
			if (traverse.Field("hasGrabbedThisAttempt").FieldExists())
			{
				traverse.Field("hasGrabbedThisAttempt").SetValue(false);
			}
			if (traverse.Field("wasPlayerGrabbedLastFrame").FieldExists())
			{
				traverse.Field("wasPlayerGrabbedLastFrame").SetValue(false);
			}
		}
	}

	private static void StartChaseOnAiComponents(GameObject enemy)
	{
		MonoBehaviour[] behaviours = GetChaserAiComponents(enemy);
		for (int i = 0; i < behaviours.Length; i++)
		{
			MonoBehaviour ai = behaviours[i];
			if (ai == null)
			{
				continue;
			}
			Traverse traverse = Traverse.Create(ai);
			if (traverse.Field("player").GetValue<Transform>() == null)
			{
				InvokeAiMethod(ai, "InitializeComponents");
			}
			if (InvokeAiMethod(ai, "ResumeAIBehavior"))
			{
				continue;
			}
			if (InvokeAiMethod(ai, "TransitionToChasing"))
			{
				continue;
			}
			InvokeAiMethod(ai, "SetupPathfinding");
			InvokeAiMethod(ai, "StartPathfinding");
			SetChasingState(ai);
		}
	}

	private static void SetChasingState(MonoBehaviour ai)
	{
		if (ai is DragonEnemyAI)
		{
			Traverse.Create(ai).Field("currentState").SetValue(DragonEnemyAI.AIState.Chasing);
		}
		else if (ai is ProximityDragonEnemyAI)
		{
			Traverse.Create(ai).Field("currentState").SetValue(ProximityDragonEnemyAI.AIState.Chasing);
		}
		else if (ai is EnemyAI)
		{
			Traverse.Create(ai).Field("currentState").SetValue(EnemyAI.AIState.Chasing);
		}
	}

	private static MonoBehaviour[] GetChaserAiComponents(GameObject enemy)
	{
		return new MonoBehaviour[]
		{
			enemy.GetComponent<DragonEnemyAI>(),
			enemy.GetComponent<ProximityDragonEnemyAI>(),
			enemy.GetComponent<EnemyAI>()
		};
	}

	private static void TryBindPlayer(GameObject enemy)
	{
		GameObject player = GameObject.FindGameObjectWithTag("Player");
		if (player == null)
		{
			return;
		}
		MonoBehaviour[] behaviours = GetChaserAiComponents(enemy);
		for (int i = 0; i < behaviours.Length; i++)
		{
			MonoBehaviour ai = behaviours[i];
			if (ai == null)
			{
				continue;
			}
			Traverse traverse = Traverse.Create(ai);
			if (!traverse.Field("player").FieldExists())
			{
				continue;
			}
			if (traverse.Field("player").GetValue<Transform>() == null)
			{
				traverse.Field("player").SetValue(player.transform);
			}
		}
	}

	private static void SyncFollowerEntity(GameObject enemy)
	{
		Vector3 pos = enemy.transform.position;
		GameObject player = GameObject.FindGameObjectWithTag("Player");
		MonoBehaviour[] behaviours = GetChaserAiComponents(enemy);
		for (int i = 0; i < behaviours.Length; i++)
		{
			MonoBehaviour ai = behaviours[i];
			if (ai == null)
			{
				continue;
			}
			Traverse traverse = Traverse.Create(ai);
			if (!traverse.Field("followerEntity").FieldExists())
			{
				continue;
			}
			object follower = traverse.Field("followerEntity").GetValue();
			if (follower == null)
			{
				continue;
			}
			Traverse followerTraverse = Traverse.Create(follower);
			if (followerTraverse.Method("Teleport").MethodExists())
			{
				followerTraverse.Method("Teleport").GetValue(pos);
			}
			if (player != null && followerTraverse.Field("destination").FieldExists())
			{
				followerTraverse.Field("destination").SetValue(player.transform.position);
			}
			if (followerTraverse.Field("simulateMovement").FieldExists())
			{
				followerTraverse.Field("simulateMovement").SetValue(true);
			}
		}
	}

	private static bool InvokeAiMethod(GameObject enemy, string methodName)
	{
		MonoBehaviour[] behaviours = GetChaserAiComponents(enemy);
		bool invoked = false;
		for (int i = 0; i < behaviours.Length; i++)
		{
			if (InvokeAiMethod(behaviours[i], methodName))
			{
				invoked = true;
			}
		}
		return invoked;
	}

	private static bool InvokeAiMethod(MonoBehaviour ai, string methodName)
	{
		if (ai == null)
		{
			return false;
		}
		MethodInfo method = AccessTools.Method(ai.GetType(), methodName);
		if (method == null)
		{
			return false;
		}
		method.Invoke(ai, null);
		return true;
	}
}
