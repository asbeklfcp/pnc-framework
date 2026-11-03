using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PncEdi;

// The goonshroom's grab screen names its heat parameter "Max Heat"; every other grab screen in
// the game names it "MaxHeat", and Assembly-CSharp contains the literal "MaxHeat" exactly once
// and "Max Heat" not at all. So `grabScreenAnimator.SetBool("MaxHeat", true)` - the line at the
// end of GrabScreen.TriggerMaxHeatAnimation - is a silent no-op on that one controller. Unity
// does not warn about a parameter that does not exist.
//
// The consequence is not cosmetic. Vanilla's cum ends on an animation *event* carried by the cum
// clip, which calls OnMaxHeatAnimationComplete; that event is the only thing that clears
// maxHeatAnimationPlaying / heatIncreaseBlocked / isCoolingDown. When the transition never fires:
//
//   * GoonShroom_GrabscreenCum never plays - so its funscript never plays either, and the row
//     has therefore never played in gameplay,
//   * heatIncreaseBlocked stays true, so heat never builds for the rest of the grab, and
//   * TriggerMaxHeatAnimation early-returns on `maxHeatAnimationPlaying`, so no later cum can
//     trigger either. The grab is wedged until EndGrab.
//
// The 2026-08-23 log holds it exactly: `releasing the cum (animator in 'GoonShroom_GrabscreenStart')`
// against the imp's `(animator in 'Imp_Grab_Cum')`, then 21 seconds of `blocked=True
// cumPlaying=True` with heat pinned at the floor and no OnMaxHeatAnimationComplete.
//
// This mirrors the bool onto whatever the controller actually calls the parameter, so vanilla's
// own state machine runs and vanilla's own event fires. Nothing here reimplements the cum; it
// only delivers the message the game is already trying to send. It is written as a general
// normalisation rather than a goonshroom special case because the failure is unobservable - a
// no-op SetBool is silent - and the next controller with a stray space would cost the same
// session to find again.
[HarmonyPatch]
internal static class GrabScreenHeatParam
{
	internal const string Canonical = "MaxHeat";

	// Keyed by RuntimeAnimatorController instance id: the alias is a property of the controller
	// asset, not of the animator playing it. Value is null when the controller is well-named,
	// which is every controller but one.
	private static readonly Dictionary<int, string> AliasByController = new Dictionary<int, string>();

	// True while vanilla's TriggerMaxHeatAnimation was going to proceed rather than early-return.
	private static bool _triggerProceeded;

	// True when this call to OnMaxHeatAnimationComplete is the one that actually ends the cum.
	private static bool _completeReleased;

	internal static void ClearCache()
	{
		AliasByController.Clear();
	}

	private static string Normalise(string name)
	{
		if (string.IsNullOrEmpty(name))
		{
			return "";
		}
		System.Text.StringBuilder sb = new System.Text.StringBuilder(name.Length);
		for (int i = 0; i < name.Length; i++)
		{
			char c = name[i];
			if (c != ' ' && c != '_' && c != '-')
			{
				sb.Append(char.ToLowerInvariant(c));
			}
		}
		return sb.ToString();
	}

	// The parameter this controller actually uses for the canonical one, or null when it has the
	// canonical name (the ordinary case) or nothing resembling it.
	private static string ResolveAlias(Animator animator)
	{
		if (animator == null)
		{
			return null;
		}
		RuntimeAnimatorController controller = animator.runtimeAnimatorController;
		if (controller == null)
		{
			return null;
		}
		int id = controller.GetInstanceID();
		if (AliasByController.TryGetValue(id, out var cached))
		{
			return cached;
		}
		string alias = null;
		try
		{
			AnimatorControllerParameter[] parameters = animator.parameters;
			string wanted = Normalise(Canonical);
			for (int i = 0; i < parameters.Length; i++)
			{
				if (parameters[i].name == Canonical)
				{
					alias = null;
					break;
				}
				if (alias == null && (int)parameters[i].type == 4 && Normalise(parameters[i].name) == wanted)
				{
					alias = parameters[i].name;
				}
			}
		}
		catch (Exception ex)
		{
			Plugin.DBG("HEAT-PARAM", "could not read parameters off '" + controller.name + "': " + ex.Message);
			alias = null;
		}
		AliasByController[id] = alias;
		if (alias != null)
		{
			Plugin.DBG("HEAT-PARAM", "'" + controller.name + "' has no '" + Canonical
				+ "' parameter but has '" + alias + "' - mirroring onto it, or its cum never plays");
		}
		return alias;
	}

	// Set the heat bool by whatever name this controller uses. Safe to call with either name
	// missing; a controller with both gets both, which is what a rename mid-version would look
	// like.
	internal static void Set(Animator animator, bool value)
	{
		if (animator == null || !animator.isActiveAndEnabled)
		{
			return;
		}
		try
		{
			string alias = ResolveAlias(animator);
			if (alias != null)
			{
				animator.SetBool(alias, value);
			}
		}
		catch (Exception ex)
		{
			Plugin.DBG("HEAT-PARAM", "mirror failed: " + ex.Message);
		}
	}

	private static Animator ScreenAnimator(GrabScreen screen)
	{
		if (screen == null)
		{
			return null;
		}
		return Traverse.Create((object)screen).Field("grabScreenAnimator").GetValue<Animator>();
	}

	// Vanilla early-returns when a cum is already playing, and only then does it set the bool.
	// Mirroring unconditionally would start a cum the game did not ask for, so capture whether
	// the original was going to proceed.
	[HarmonyPatch(typeof(GrabScreen), "TriggerMaxHeatAnimation")]
	[HarmonyPrefix]
	public static void TriggerMaxHeatAnimation_Prefix(GrabScreen __instance)
	{
		try
		{
			_triggerProceeded = __instance != null
				&& !Traverse.Create((object)__instance).Field("maxHeatAnimationPlaying").GetValue<bool>();
		}
		catch (Exception)
		{
			_triggerProceeded = false;
		}
	}

	[HarmonyPatch(typeof(GrabScreen), "TriggerMaxHeatAnimation")]
	[HarmonyPostfix]
	public static void TriggerMaxHeatAnimation_Postfix(GrabScreen __instance)
	{
		if (_triggerProceeded)
		{
			_triggerProceeded = false;
			Set(ScreenAnimator(__instance), value: true);
		}
	}

	// Same shape in reverse: the original clears the bool only when heatFullyCooled was set, and
	// clears the flag on its way out, so the postfix cannot tell what happened.
	[HarmonyPatch(typeof(GrabScreen), "OnMaxHeatAnimationComplete")]
	[HarmonyPrefix]
	public static void OnMaxHeatAnimationComplete_Prefix(GrabScreen __instance)
	{
		try
		{
			_completeReleased = __instance != null
				&& Traverse.Create((object)__instance).Field("heatFullyCooled").GetValue<bool>();
		}
		catch (Exception)
		{
			_completeReleased = false;
		}
	}

	// HeatLockSystem has its own postfix on this method, and at full lock it re-asserts the cum
	// (FullLockHoldsCum) by setting the bool back to true. Harmony does not order two postfixes
	// on one method unless it is told to, so this one is pinned to run first: clear the alias
	// here, and let the hold - which mirrors through Set() as well - have the last word. Without
	// the priority the two would race, and on the one controller that needs mirroring the race
	// would decide whether a full-lock cum holds or drops to the loop.
	[HarmonyPatch(typeof(GrabScreen), "OnMaxHeatAnimationComplete")]
	[HarmonyPostfix]
	[HarmonyPriority(800)]
	public static void OnMaxHeatAnimationComplete_Postfix(GrabScreen __instance)
	{
		if (_completeReleased)
		{
			_completeReleased = false;
			Set(ScreenAnimator(__instance), value: false);
		}
	}

	[HarmonyPatch(typeof(GrabScreen), "EndGrab")]
	[HarmonyPostfix]
	public static void EndGrab_Postfix(GrabScreen __instance)
	{
		Set(ScreenAnimator(__instance), value: false);
	}
}
