using HarmonyLib;
using UnityEngine;

namespace PncEdi;

// What is left of the grapple hooks after §114 gave the enemies' lifecycle back to the game.
// Everything here is about the mod's own state - the log line, and the heat-lock survival window
// that brackets any scene the player is dragged into. The stow/keep-alive/release hooks that used
// to live beside them are gone; see GrappleEnemies for why.
[HarmonyPatch]
public static class GrappleHooks
{
	[HarmonyPatch(typeof(GrappleScreenobject), "StartGrapple")]
	[HarmonyPostfix]
	public static void StartGrapple_Postfix(GrappleScreenobject __instance, GameObject enemy)
	{
		if (!(__instance == null))
		{
			Plugin.DBG("GRAPPLE-START", string.Format("enemy={0} count={1}", (enemy != null) ? NameRemap.StripCloneSuffix(enemy.name) : "?", __instance.GrappleCount));
			HeatLockSystem.EnsureSceneEntrySurvival("grapple");
		}
	}

	[HarmonyPatch(typeof(GrappleScreenobject), "EndGrapple")]
	[HarmonyPostfix]
	public static void EndGrapple_Postfix()
	{
		GrappleScreenobject grappleScreenobject = GrappleScreenobject.Instance;
		GrabScreen grabScreen = GrabScreen.Instance;
		if (grappleScreenobject != null && !grappleScreenobject.IsGrappling && (grabScreen == null || !grabScreen.IsGrabbed))
		{
			NearbyEnemyHider.RestoreAll();
		}
		HeatLockSystem.CompleteSceneEntrySurvival("grapple");
		Plugin.DBG("GRAPPLE-END", "grapple over");
	}
}
