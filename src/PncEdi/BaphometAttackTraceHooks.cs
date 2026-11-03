using System;
using Shot = PncEdi.ShotTraceRegistry<PncEdi.BaphometAttackTrace>.Record;
using HarmonyLib;
using UnityEngine;
namespace PncEdi;
[HarmonyPatch]
internal static class BaphometAttackTraceHooks {
    static readonly ShotTraceRegistry<BaphometAttackTrace> Shots=new ShotTraceRegistry<BaphometAttackTrace>();
    [ThreadStatic] static BaphometAttackTrace firing;
    static void Register(Component projectile,BaphometAttackTrace trace,string kind) {
        Shots.Register(projectile,trace,kind);
        if(trace==null)return;
        trace.Note("projectile-created","kind="+kind+"; projectile="+projectile.GetInstanceID()+"; pos="+projectile.transform.position);
    }
    static Shot Find(Component p)=>p!=null&&Shots.TryGet(p,out var shot)&&shot.Actor!=null&&shot.Actor.Active?shot:null;
    static void Contact(Component p,Collider other,string phase) {
        var shot=Find(p);if(shot==null)return;
        shot.Actor.Note("collision-"+phase,"kind="+shot.Kind+"; projectile="+p.GetInstanceID()+"; other="+(other!=null?other.name:"null")+
            "; player="+(other!=null&&other.GetComponentInParent<PlayerStats>()!=null)+"; hasHit="+OptionalMembers.Read(p,"hasHit")+"; pos="+p.transform.position);
    }
    [HarmonyPatch(typeof(ProjectileEnemyAI),"PlayShootSound"),HarmonyPostfix]
    static void ShootCue(ProjectileEnemyAI __instance)=>BaphometAttackTrace.Find(__instance)?.Note("attack-audio-cue","kind=damage; clip="+(OptionalMembers.Read(__instance,"shootSound") as AudioClip)?.name);
    [HarmonyPatch(typeof(ProjectileEnemyAI),"PlayGrabSound"),HarmonyPostfix]
    static void GrabCue(ProjectileEnemyAI __instance)=>BaphometAttackTrace.Find(__instance)?.Note("attack-audio-cue","kind=capture; clip="+(OptionalMembers.Read(__instance,"grabSound") as AudioClip)?.name);
    [HarmonyPatch(typeof(ProjectileEnemyAI),"TransitionToShooting"),HarmonyPostfix]
    static void Shooting(ProjectileEnemyAI __instance){var t=BaphometAttackTrace.Find(__instance);t?.Note("transition-shooting",t.State());}
    [HarmonyPatch(typeof(ProjectileEnemyAI),"TransitionToRetreating"),HarmonyPostfix]
    static void Retreating(ProjectileEnemyAI __instance){var t=BaphometAttackTrace.Find(__instance);t?.Note("transition-retreat",t.State()+"; pending="+OptionalMembers.Read(__instance,"hasPendingTeleport")+"; target="+OptionalMembers.Read(__instance,"pendingTeleportTarget"));}
    [HarmonyPatch(typeof(ProjectileEnemyAI),"TransitionToChasing"),HarmonyPostfix]
    static void Chasing(ProjectileEnemyAI __instance){var t=BaphometAttackTrace.Find(__instance);t?.Note("transition-chasing",t.State());}
    [HarmonyPatch(typeof(ProjectileEnemyAI),"TryFindFleeNodeOnAstar"),HarmonyPostfix]
    static void FleeNode(ProjectileEnemyAI __instance,bool __result)=>BaphometAttackTrace.Find(__instance)?.Note("retreat-target","source=Astar; found="+__result);
    [HarmonyPatch(typeof(ProjectileEnemyAI),"TryFindPlayerRoomSpawn"),HarmonyPostfix]
    static void RoomNode(ProjectileEnemyAI __instance,bool __result)=>BaphometAttackTrace.Find(__instance)?.Note("retreat-target","source=native-room; found="+__result);
    [HarmonyPatch(typeof(ProjectileEnemyAI),"OnTeleportAnimationEvent"),HarmonyPrefix]
    static void TeleportBefore(ProjectileEnemyAI __instance){var t=BaphometAttackTrace.Find(__instance);t?.Note("teleport-request",t.State()+"; pending="+OptionalMembers.Read(__instance,"hasPendingTeleport")+"; target="+OptionalMembers.Read(__instance,"pendingTeleportTarget"));}
    [HarmonyPatch(typeof(ProjectileEnemyAI),"OnTeleportAnimationEvent"),HarmonyPostfix]
    static void TeleportAfter(ProjectileEnemyAI __instance){var t=BaphometAttackTrace.Find(__instance);t?.Note("teleport-completed",t.State());}
    [HarmonyPatch(typeof(ProjectileEnemyAI),"SpawnProjectile"),HarmonyPrefix]
    static void DamageSpawnBefore(ProjectileEnemyAI __instance,out BaphometAttackTrace __state) {
        __state=firing;firing=BaphometAttackTrace.Find(__instance);
        firing?.Note("spawn-animation-event","kind=damage; duplicateGuard="+OptionalMembers.Read(__instance,"hasSpawnedProjectileThisAttack"));
    }
    [HarmonyPatch(typeof(ProjectileEnemyAI),"SpawnProjectile"),HarmonyFinalizer]
    static Exception DamageSpawnEnd(Exception __exception,BaphometAttackTrace __state){firing=__state;return __exception;}
    [HarmonyPatch(typeof(ProjectileEnemyAI),"SpawnGrabProjectile"),HarmonyPrefix]
    static void GrabSpawnBefore(ProjectileEnemyAI __instance)=>BaphometAttackTrace.Find(__instance)?.Note("spawn-animation-event","kind=capture; duplicateGuard="+OptionalMembers.Read(__instance,"hasSpawnedGrabProjectileThisAttack"));
    [HarmonyPatch(typeof(EnemyProjectile),"Initialize"),HarmonyPostfix]
    static void DamageCreated(EnemyProjectile __instance)=>Register(__instance,firing,"damage");
    [HarmonyPatch(typeof(GrabProjectile),"Initialize"),HarmonyPostfix]
    static void GrabCreated(GrabProjectile __instance,ProjectileEnemyAI __3)=>Register(__instance,BaphometAttackTrace.Find(__3),"capture");
    [HarmonyPatch(typeof(EnemyProjectile),"OnTriggerEnter"),HarmonyPrefix]
    static void DamageContactBefore(EnemyProjectile __instance,Collider __0)=>Contact(__instance,__0,"before");
    [HarmonyPatch(typeof(EnemyProjectile),"OnTriggerEnter"),HarmonyPostfix]
    static void DamageContactAfter(EnemyProjectile __instance,Collider __0)=>Contact(__instance,__0,"after");
    [HarmonyPatch(typeof(GrabProjectile),"OnTriggerEnter"),HarmonyPrefix]
    static void GrabContactBefore(GrabProjectile __instance,Collider __0)=>Contact(__instance,__0,"before");
    [HarmonyPatch(typeof(GrabProjectile),"OnTriggerEnter"),HarmonyPostfix]
    static void GrabContactAfter(GrabProjectile __instance,Collider __0)=>Contact(__instance,__0,"after");
    struct Impact {internal Shot Shot;internal PlayerStats Player;internal int HP;internal float Heat;}
    [HarmonyPatch(typeof(EnemyProjectile),"DealDamageToPlayer"),HarmonyPrefix,HarmonyPriority(Priority.First+1)]
    static void DamageBefore(EnemyProjectile __instance,GameObject __0,out Impact __state) {
        __state=new Impact{Shot=Find(__instance),Player=__0!=null?__0.GetComponentInParent<PlayerStats>():null};
        if(__state.Shot!=null&&__state.Player!=null){__state.HP=__state.Player.CurrentHealth;__state.Heat=__state.Player.CurrentHeat;}
    }
    [HarmonyPatch(typeof(EnemyProjectile),"DealDamageToPlayer"),HarmonyPostfix,HarmonyPriority(Priority.Last)]
    static void DamageAfter(Impact __state) {
        if(__state.Shot==null||__state.Player==null)return;
        __state.Shot.Actor.Note("damage-resolved","HP="+__state.HP+"->"+__state.Player.CurrentHealth+"; heat="+__state.Heat+"->"+__state.Player.CurrentHeat);
    }
    [HarmonyPatch(typeof(ProjectileEnemyAI),"OnGrabProjectileHitPlayer"),HarmonyPostfix]
    static void GrabResolved(ProjectileEnemyAI __instance)=>BaphometAttackTrace.Find(__instance)?.Note("capture-projectile-resolved","heat-bank/capture acceptance is recorded by HEAT-SCENE");
    [HarmonyPatch(typeof(EnemyProjectile),"DestroyProjectile"),HarmonyPrefix]
    static void DamageDestroyed(EnemyProjectile __instance){var s=Find(__instance);s?.Actor.Note("projectile-ended","kind=damage; hasHit="+OptionalMembers.Read(__instance,"hasHit"));}
    [HarmonyPatch(typeof(GrabProjectile),"DestroyProjectile"),HarmonyPrefix]
    static void GrabDestroyed(GrabProjectile __instance){var s=Find(__instance);s?.Actor.Note("projectile-ended","kind=capture; hasHit="+OptionalMembers.Read(__instance,"hasHit"));}
}
