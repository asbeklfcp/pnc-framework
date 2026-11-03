using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
namespace PncEdi;
[HarmonyPatch]
internal static class OverkeksGrabRecovery {
    static readonly GrabRecoveryDeadline Deadline=new GrabRecoveryDeadline();
    internal static void Arm(PlayerStats player,float seconds) {
        if(!OverkeksMode.Active || player==null)return;
        Deadline.Arm(player,SceneManager.GetActiveScene().handle,OverkeksMode.Epoch,Time.time,seconds);
    }
    [HarmonyPatch(typeof(PlayerStats),"get_CanBeGrabbed"),HarmonyPostfix,HarmonyPriority(Priority.Last)]
    internal static void AfterEligibility(PlayerStats __instance,ref bool __result) {
        if(Deadline.Protects(__instance,SceneManager.GetActiveScene().handle,OverkeksMode.Epoch,Time.time,OverkeksMode.Active))__result=false;
    }
}
