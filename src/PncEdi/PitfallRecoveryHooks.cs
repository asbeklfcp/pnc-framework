using System.Collections;
using HarmonyLib;
using UnityEngine;

namespace PncEdi;

/// <summary>
/// PitfallTeleporter disables CharacterController / walker / FPS while falling.
/// If that coroutine is interrupted (or finishes with a bad teleport), melee/grab
/// RaycastAll never hits the player — enemies keep swinging while heat stays frozen.
/// Wrap the fall coroutine so controls always come back.
/// </summary>
[HarmonyPatch]
internal static class PitfallRecoveryHooks
{
	[HarmonyPatch(typeof(PitfallTeleporter), "HandlePitfall")]
	[HarmonyPostfix]
	private static void HandlePitfall_EnsureRecover(ref IEnumerator __result, GameObject player)
	{
		if (!OverkeksMode.Active || player == null || __result == null)
		{
			return;
		}
		__result = RunThenRecover(__result, player);
	}

	private static IEnumerator RunThenRecover(IEnumerator original, GameObject player)
	{
		while (true)
		{
			bool moved;
			try
			{
				moved = original.MoveNext();
			}
			catch
			{
				EnsurePlayerControllable(player, "pitfall threw");
				yield break;
			}
			if (!moved)
			{
				break;
			}
			yield return original.Current;
		}
		EnsurePlayerControllable(player, "pitfall finished");
	}

	internal static void EnsurePlayerControllable(GameObject player, string why)
	{
		if (player == null)
		{
			return;
		}
		player = player.transform.root.gameObject;
		bool fixedSomething = false;
		CharacterController characterController = player.GetComponent<CharacterController>();
		if (characterController != null && !characterController.enabled)
		{
			characterController.enabled = true;
			fixedSomething = true;
		}
		Rigidbody rb = player.GetComponent<Rigidbody>();
		if (rb != null && rb.isKinematic)
		{
			rb.isKinematic = false;
			fixedSomething = true;
		}
		FirstPersonController fps = player.GetComponent<FirstPersonController>();
		if (fps != null && !fps.enabled)
		{
			fps.enabled = true;
			fixedSomething = true;
		}
		// FirstPersonController IS the walker (extends AdvancedWalkerController) — no separate type ref needed.
		Behaviour[] behaviours = player.GetComponents<Behaviour>();
		for (int i = 0; i < behaviours.Length; i++)
		{
			Behaviour behaviour = behaviours[i];
			if (behaviour == null || behaviour.enabled)
			{
				continue;
			}
			string typeName = behaviour.GetType().Name;
			if (typeName == "AdvancedWalkerController" || typeName.EndsWith("WalkerController"))
			{
				behaviour.enabled = true;
				fixedSomething = true;
			}
		}
		PlayerStats stats = player.GetComponent<PlayerStats>();
		if (stats != null && !stats.CanBeGrabbed)
		{
			GrabScreen grab = GrabScreen.Instance;
			if (grab == null || !grab.IsGrabbed)
			{
				stats.CanBeGrabbed = true;
				fixedSomething = true;
			}
		}
		if (fixedSomething)
		{
			Physics.SyncTransforms();
			Plugin.DBG("PITFALL", "restored player control (" + why + ")");
		}
	}
}
