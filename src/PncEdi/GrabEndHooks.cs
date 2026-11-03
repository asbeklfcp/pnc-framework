using HarmonyLib;
using UnityEngine;

namespace PncEdi;

[HarmonyPatch]
internal static class GrabEndHooks
{
	[HarmonyPatch(typeof(GrabScreen), "EndGrab")]
	[HarmonyPrefix]
	[HarmonyPriority(1000)]
	public static void EndGrab_Prepare_Prefix(GrabScreen __instance)
	{
		if (!(__instance == null))
		{
			GrabEndHelper.PrepareGrabEnd(__instance);
		}
	}

	[HarmonyPatch(typeof(GrabScreen), "EndGrab")]
	[HarmonyPostfix]
	[HarmonyPriority(50)]
	public static void EndGrab_Finish_Postfix(GrabScreen __instance)
	{
		GrabEndHelper.FinishGrabEnd(__instance);
	}

	[HarmonyPatch(typeof(GrabScreen), "EndGrab")]
	[HarmonyPostfix]
	[HarmonyPriority(500)]
	public static void EndGrab_FinalizeVisuals_Postfix(GrabScreen __instance)
	{
		GrabEndHelper.FinalizeGrabEndVisuals(__instance);
	}
}
