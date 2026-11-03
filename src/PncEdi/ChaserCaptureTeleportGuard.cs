using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
namespace PncEdi;
// Native Dragon/Wendigo animation events target the terminal BlackRoom scene.
// Persistent Overkeks encounters must not run that terminal teleport, including
// late callbacks after escape. Ordinary captures and Vanilla retain native behavior.
[HarmonyPatch]
internal static class ChaserCaptureTeleportGuard {
    static readonly CaptureTeleportLease Lease=new CaptureTeleportLease();
    static int reports;
    [HarmonyPatch(typeof(GrabScreen),"StartGrab"),HarmonyPrefix,HarmonyPriority(Priority.First)]
    static void BeforeStart(GrabScreen __instance,out bool __state) =>
        __state=__instance!=null && __instance.IsGrabbed;
    [HarmonyPatch(typeof(GrabScreen),"StartGrab"),HarmonyPostfix,HarmonyPriority(Priority.Last)]
    static void AfterStart(GrabScreen __instance,GameObject enemy,bool __state) {
        // Failed/reentrant StartGrab attempts do not replace an accepted session.
        if(__state || __instance==null || !__instance.IsGrabbed || __instance.GrabbingEnemy!=enemy)return;
        bool persistent=OverkeksMode.Active && enemy!=null &&
            GrabStruggleHooks.IsChaserBossGrabEnemy(enemy) && ChaserSweeperHooks.IsActiveManagedChaser(enemy);
        Lease.Accepted(__instance,SceneManager.GetActiveScene().handle,OverkeksMode.Epoch,persistent);
        reports=0;
    }
    internal static bool IsBlackRoomPoint(Transform point) {
        for(var current=point;current!=null;current=current.parent)
            if(string.Equals(NameRemap.StripCloneSuffix(current.name),"BlackRoom",StringComparison.OrdinalIgnoreCase))return true;
        return false;
    }
    [HarmonyPatch(typeof(GrabScreen),"TeleportPlayer"),HarmonyPrefix,HarmonyPriority(Priority.First)]
    internal static bool BeforeTeleport(GrabScreen __instance) {
        if(__instance==null)return true;
        var point=OptionalMembers.Read(__instance,"teleportPoint") as Transform;
        bool block=Lease.ShouldBlock(__instance,SceneManager.GetActiveScene().handle,OverkeksMode.Epoch,
            OverkeksMode.Active,IsBlackRoomPoint(point));
        if(!block || (OptionalMembers.Read(__instance,"enableTeleport") is bool enabled && !enabled))return true;
        if(reports++<4)Plugin.DBG("CHASER-TELEPORT","blocked native BlackRoom callback; grabbed="+
            __instance.IsGrabbed+"; destination="+point.position);
        return false;
    }
}

