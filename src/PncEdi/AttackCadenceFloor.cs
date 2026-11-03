using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
namespace PncEdi;
// These native coroutines use attackCooldown as their lifetime as well as readiness.
// Never let tuning terminate one before its authored damage window has closed.
[HarmonyPatch]
internal static class AttackCadenceFloor {
    static readonly Dictionary<RuntimeAnimatorController,float> Windows=new Dictionary<RuntimeAnimatorController,float>();
    static IEnumerable<MethodBase> TargetMethods() {
        foreach(var type in new[]{typeof(EnemyAI),typeof(BrawlerEnemyAI),typeof(DragonEnemyAI),typeof(ProximityDragonEnemyAI)}) {
            var method=AccessTools.Method(type,"StartAttack",Type.EmptyTypes);if(method!=null)yield return method;
        }
    }
    [HarmonyPrefix]
    static void BeforeAttack(MonoBehaviour __instance) {
        if(!OverkeksMode.Active || __instance==null)return;
        var animator=OptionalMembers.Read(__instance,"animator") as Animator;
        var controller=animator!=null?animator.runtimeAnimatorController:null;
        if(controller==null || animator.speed<=0)return;
        if(!Windows.TryGetValue(controller,out float window)) {
            window=0;
            foreach(var clip in controller.animationClips)if(clip!=null)
                foreach(var e in clip.events)
                    if(e.functionName=="EndMeleeDamageState" || e.functionName=="EndGrabDamageState")window=Mathf.Max(window,e.time);
            Windows[controller]=window;
        }
        if(window<=0)return;
        float floor=(window+.05f)/animator.speed;
        if(OptionalMembers.Read(__instance,"attackCooldown") is float cooldown && cooldown<floor)
            OptionalMembers.Write(__instance,"attackCooldown",floor);
    }
}
