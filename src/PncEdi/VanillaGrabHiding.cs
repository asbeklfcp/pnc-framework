using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace PncEdi;

// Game 0.3.1 grew the mechanic the mod had been faking since §17: an enemy that grabs the player
// is hidden rather than destroyed, and comes back when the player escapes. It is a per-prefab
// flag with the game's own teardown behind it —
//
//   EnemyAI.hideInsteadOfDestroyOnGrab   "If true, this enemy is hidden (SetActive false) instead
//                                         of destroyed when it grabs the player. It reappears in
//                                         Idle if the player escapes the grab; if killed during
//                                         the grab, it dies normally at EndGrab."
//   EnemyAI.preserveHealthDuringGrab     damage dealt on the grab screen does not carry over
//
// and the two halves are `RemoveEnemyAfterGrab` (ResetGrabTransientState, state = Idle,
// GrabScreen.RegisterHiddenEnemy, SetActive(false)) and `IGrabHideable.EndGrabHidden(float
// remainingHealth)` (SetActive(true), restore the transformed animator state, lastGrabTime = now,
// health from the grab screen's snapshot, ResetGrabTransientState, state = Idle, pathfinding back
// on). That is everything `GrabEnemyProtection` + `EnemyKeepAliveHelper` + `EnemyReactivationHelper`
// were built to do, done by the code that owns the state.
//
// So the mod stands down per enemy, on the game's own flag rather than on a version check: a
// prefab that sets it is handled by vanilla and the mod does not arm any of its keep-alive; a
// prefab that does not is still on the mod's path, which is also what keeps 0.2.1 working. The
// decision is logged once per prefab, because "which of the two paths ran" is otherwise invisible
// and a wrong guess here looks exactly like a bug in the other one.
internal static class VanillaGrabHiding
{
	private const string HideFlagField = "hideInsteadOfDestroyOnGrab";
	private static readonly HashSet<string> _logged = new HashSet<string>();

	// Every AI class that declares the flag. ChargingEnemyAI is deliberately absent: it grapples
	// rather than grabs, and has no RemoveEnemyAfterGrab of its own.
	internal static bool Handles(GameObject enemy)
	{
		if (enemy == null)
		{
			return false;
		}
		// ReadFlag is a FieldExists() probe, so a class that never declared the flag - as
		// BrawlerEnemyAI does not - simply reads false and the mod's own keep-alive stays in
		// charge of it.
		foreach (MonoBehaviour ai in EnemyAiTypes.On(enemy))
		{
			if (ReadFlag(ai))
			{
				return true;
			}
		}
		return false;
	}

	internal static bool Handles(MonoBehaviour enemyAi)
	{
		return enemyAi != null && ReadFlag(enemyAi);
	}

	private static bool ReadFlag(MonoBehaviour ai)
	{
		if (ai == null)
		{
			return false;
		}
		Traverse field = Traverse.Create((object)ai).Field(HideFlagField);
		return field.FieldExists() && field.GetValue<bool>();
	}

	// One line per prefab, the first time that prefab is grabbed. Says which implementation owns
	// the enemy's survival, so the answer is in the log before anything goes wrong.
	internal static void LogDecisionOnce(GameObject enemy, bool vanillaHandles)
	{
		if (enemy == null)
		{
			return;
		}
		string prefab = NameRemap.StripCloneSuffix(enemy.name);
		if (_logged.Add(prefab))
		{
			Plugin.DBG("GRAB-HIDE", prefab + (vanillaHandles
				? ": vanilla hides it (hideInsteadOfDestroyOnGrab) - mod keep-alive stands down"
				: ": vanilla still destroys it - mod keep-alive active"));
		}
	}
}
