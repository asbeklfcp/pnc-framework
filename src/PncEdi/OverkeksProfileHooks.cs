using HarmonyLib;
using UnityEngine;
namespace PncEdi;
[HarmonyPatch]
internal static class OverkeksProfileHooks {
    [HarmonyPatch(typeof(EnemySpawner),"Awake"),HarmonyPrefix,HarmonyPriority(Priority.First)]
    static void BeforeSpawner(EnemySpawner __instance)=>OverkeksMode.PrepareSpawner(__instance);
    [HarmonyPatch(typeof(ArenaEnemySpawner),"Awake"),HarmonyPrefix,HarmonyPriority(Priority.First)]
    static void BeforeArena(ArenaEnemySpawner __instance)=>OverkeksMode.PrepareSpawner(__instance);
    [HarmonyPatch(typeof(GrabScreen),"StartGrab"),HarmonyPrefix,HarmonyPriority(Priority.First)]
    static void CaptureUi(GrabScreen __instance)=>OverkeksMode.CapturePresentation(__instance);
    [HarmonyPatch(typeof(PlayerStats),"ApplyDamage"),HarmonyPrefix,HarmonyPriority(Priority.First)]
    static bool Damage(PlayerStats __instance,int damage) {
        if(!OverkeksMode.Active)return true;
        if(!CumDamageGate.Suppressing&&damage>0&&__instance!=null) {
            OverkeksHeatSceneGate.ApplyCombatHeat(__instance,damage);
            OverkeksHeatSceneGate.TryStartSceneIfHeatFull(__instance,null);
        }
        return false;
    }
    // Native shake-off keeps living grapplers; share the same bounded release reward.
    [HarmonyPatch(typeof(GrappleScreenobject),"ReleaseGrappler"),HarmonyPostfix]
    static void AfterGrapplerRelease(GameObject __0) => OverkeksMode.ApplyCaptureReleaseReward(__0);
    [HarmonyPatch(typeof(BrawlerEnemyAI),"LateUpdate"),HarmonyPrefix]
    static void BeforeHypnosisLook(BrawlerEnemyAI __instance) {
        if(OverkeksMode.Active && __instance!=null)__instance.GetComponent<ReskinPresentation>()?.UpdateGazeTarget();
    }
    [HarmonyPatch(typeof(GrabScreen),"EndGrab"),HarmonyPrefix]
    static void BeforeEnd(GrabScreen __instance,out GameObject __state) {
        __state=OverkeksMode.ResolveCaptureEnemy(__instance);
        if(OverkeksMode.Active && __state!=null)foreach(var ai in EnemyAiTypes.On(__state))
            Plugin.DBG("CAPTURE-ACTOR","ending actor="+__state.name+"; id="+ai.GetInstanceID()+"; hp="+OptionalMembers.Read(ai,"currentHealth")+"; transformed="+OptionalMembers.Read(ai,"isTransformed"));
    }
    [HarmonyPatch(typeof(GrabScreen),"EndGrab"),HarmonyPostfix]
    static void AfterEnd(GameObject __state) {
        if(!OverkeksMode.Active||__state==null)return;
        OverkeksMode.ApplyCaptureReleaseReward(__state);
        if(ChaserSweeperHooks.IsActiveManagedChaser(__state)) {
            // Capture is not defeat. Existing managed slot stays active; retain its native AI.
            OverkeksMode.EnsurePlayerNotAttachedToEnemy(__state);
            EnemyReactivationHelper.ScheduleReactivate(__state, applyGrabEndCooldown:true);
            ChaserHitLayerLease.Register(__state);
        }
        if(ChaserFodderSpawn.IsFodder(__state))ChaserFodderSpawn.RecoverAfterGrab(__state);
    }
}
