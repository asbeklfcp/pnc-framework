using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
namespace PncEdi;
// Native retreat picks a target then waits for an animation event. If the native
// controller lacks that contract, finish its planned teleport at its existing timeout.
// No new target, walking, attack or range rule is invented.
[HarmonyPatch]
internal static class ProjectileRetreatFallback {
    static readonly Dictionary<(RuntimeAnimatorController,string),string> Contracts=new Dictionary<(RuntimeAnimatorController,string),string>();
    sealed class Report {internal bool Done;}
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ProjectileEnemyAI,Report> Reports=new System.Runtime.CompilerServices.ConditionalWeakTable<ProjectileEnemyAI,Report>();
    static readonly MethodInfo Complete=typeof(ProjectileEnemyAI).GetMethod("OnTeleportAnimationEvent",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
    internal static string ContractStatus(ProjectileEnemyAI actor) {
        var animator=OptionalMembers.Read(actor,"animator") as Animator;
        if(animator==null || animator.runtimeAnimatorController==null)return "missing-animator";
        var controller=animator.runtimeAnimatorController;string trigger=OptionalMembers.Read(actor,"retreatTriggerName") as string ?? "";
        var key=(controller,trigger);if(Contracts.TryGetValue(key,out var result))return result;
        bool parameter=false,eventFound=false;
        try {
            foreach(var p in animator.parameters)if(p.name==trigger && p.type==AnimatorControllerParameterType.Trigger)parameter=true;
            foreach(var clip in controller.animationClips)if(clip!=null)foreach(var e in clip.events)if(e.functionName=="OnTeleportAnimationEvent")eventFound=true;
            result=!parameter?"missing-retreat-trigger":!eventFound?"missing-teleport-event":"native-contract-present";
        }catch {result="unverified-controller";}
        Contracts[key]=result;return result;
    }
    [HarmonyPatch(typeof(ProjectileEnemyAI),"HandleRetreatingState"),HarmonyPrefix]
    internal static bool BeforeRetreat(ProjectileEnemyAI __instance) {
        if(!OverkeksMode.Active || __instance==null || !__instance.isActiveAndEnabled || PauseHooks.SceneClockHeld || Time.timeScale<=.0001f)return true;
        if(GrabScreen.Instance!=null && GrabScreen.Instance.IsGrabbed)return true;
        if(OptionalMembers.Read(__instance,"isDead") is bool dead && dead)return true;
        if(!(OptionalMembers.Read(__instance,"hasPendingTeleport") is bool pending && pending))return true;
        if(Convert.ToInt32(OptionalMembers.Read(__instance,"currentState"))!=2)return true;
        if(!(OptionalMembers.Read(__instance,"retreatStartTime") is float start) ||
            !(OptionalMembers.Read(__instance,"teleportSafetyTimeout") is float timeout) || Time.time-start<=timeout)return true;
        string contract=ContractStatus(__instance);if(contract=="native-contract-present" || contract=="unverified-controller" || Complete==null)return true;
        try {
            var before=__instance.transform.position;Complete.Invoke(__instance,null);
            if(OptionalMembers.Read(__instance,"hasPendingTeleport") is bool stillPending && stillPending)return true;
            var report=Reports.GetOrCreateValue(__instance);
            if(!report.Done){report.Done=true;Plugin.DBG("RETREAT-CONTRACT","completed native planned teleport at safety timeout; actor="+__instance.name+"; contract="+contract+"; from="+before+"; to="+__instance.transform.position); }
            return false;
        }catch(Exception ex){Plugin.Log?.LogWarning("[RETREAT-CONTRACT] Native completion failed; retaining original safety fallback: "+ex.GetBaseException().Message);return true;}
    }
}
