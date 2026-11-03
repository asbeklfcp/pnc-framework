using System;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PncEdi;

[HarmonyPatch]
public static class InteractDiag
{
	private static FieldInfo _targetField;
	private static FieldInfo _keyField;

	// A mimic does not go through PlayerInteractor at all - MimicEnemy.Update reads the key
	// itself - so "[E-PRESS] no target" says nothing about it. (The working interaction at
	// 05:29:57 logged "no target" one frame before its GRAB-START.) Report the mimic's own gates
	// instead, which are exactly:
	//
	//     if (player == null) return;
	//     playerInRange = Vector3.Distance(transform.position, player.position) <= interactionRange;
	//     if (playerInRange && !hasTriggeredGrab && SafeInput.GetKeyDown(interactionKey)) TriggerMimicGrab();
	//
	// Bug #13/"mimics did not reactivate": after a mimic scene GRAB-RESTORE put the player at
	// (-48.19,-0.60,-17.80) against a mimic at (-49.60,0.80,-18.30) - a distance of 2.049 with
	// interactionRange 2.0, so E did nothing. Note 1.40 of that 2.049 is pure height difference
	// (chest at y=0.80, player pivot at y=-0.60): horizontally they are 1.50 apart, standing
	// right at it. This line prints the distance and each gate so the margin is visible rather
	// than inferred from two positions in separate log lines.
	private static void LogNearbyMimics(PlayerInteractor interactor)
	{
		try
		{
			Transform p = interactor.transform;
			MimicEnemy[] all = Object.FindObjectsByType<MimicEnemy>((FindObjectsInactive)1, (FindObjectsSortMode)0);
			for (int i = 0; i < all.Length; i++)
			{
				MimicEnemy m = all[i];
				if (m == null) continue;
				float d = Vector3.Distance(m.transform.position, p.position);
				if (d > 6f) continue;                       // only ones the player is plausibly at
				Traverse t = Traverse.Create((object)m);
				float range = t.Field("interactionRange").FieldExists() ? t.Field("interactionRange").GetValue<float>() : -1f;
				bool triggered = t.Field("hasTriggeredGrab").FieldExists() && t.Field("hasTriggeredGrab").GetValue<bool>();
				bool inRange = t.Field("playerInRange").FieldExists() && t.Field("playerInRange").GetValue<bool>();
				bool hasPlayer = t.Field("player").FieldExists() && t.Field("player").GetValue<Transform>() != null;
				Plugin.DBG("MIMIC-GATE", $"'{m.gameObject.name}' dist={d:F2} range={range:F2} " +
					$"inRange={inRange} hasTriggeredGrab={triggered} playerRef={hasPlayer} " +
					$"active={m.gameObject.activeInHierarchy} enabled={m.enabled}");
			}
		}
		catch (Exception ex)
		{
			Plugin.DBG("MIMIC-GATE", "failed: " + ex.Message);
		}
	}

	[HarmonyPatch(typeof(PlayerInteractor), "Update")]
	[HarmonyPostfix]
	public static void Update_Postfix(PlayerInteractor __instance)
	{
		try
		{
			if (Plugin.CfgDebug == null || !Plugin.CfgDebug.Value)
			{
				return;
			}
			if (_keyField == null)
			{
				_keyField = AccessTools.Field(typeof(PlayerInteractor), "interactKey");
			}
			KeyCode keyCode = (KeyCode)101;
			if (_keyField != null)
			{
				keyCode = (KeyCode)(_keyField.GetValue(__instance) ?? ((object)(KeyCode)101));
			}
			if (SafeInput.GetKeyDown(keyCode))
			{
				if (_targetField == null)
				{
					_targetField = AccessTools.Field(typeof(PlayerInteractor), "currentTarget");
				}
				LogNearbyMimics(__instance);
				object? obj = _targetField?.GetValue(__instance);
				IPlayerInteractable iPlayerInteractable = (IPlayerInteractable)((obj is IPlayerInteractable) ? obj : null);
				if (iPlayerInteractable == null)
				{
					Plugin.DBG("E-PRESS", "no target");
					return;
				}
				MonoBehaviour monoBehaviour = (MonoBehaviour)(object)((iPlayerInteractable is MonoBehaviour) ? iPlayerInteractable : null);
				string objectName = ((monoBehaviour != null) ? monoBehaviour.gameObject.name : "<not-MB>");
				string parentName = ((monoBehaviour != null && monoBehaviour.transform.parent != null) ? ((Component)monoBehaviour).transform.parent.gameObject.name : "<root>");
				Plugin.DBG("E-PRESS", $"type={((object)iPlayerInteractable).GetType().Name} go='{objectName}' parent='{parentName}' canInteract={iPlayerInteractable.CanInteract}");
			}
		}
		catch (Exception ex)
		{
			Plugin.Log?.LogWarning($"InteractDiag postfix error: {ex.Message}");
		}
	}
}
