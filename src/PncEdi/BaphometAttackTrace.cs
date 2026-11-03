using System;
using BepInEx.Configuration;
using UnityEngine;
namespace PncEdi;
[DefaultExecutionOrder(11000)]
internal sealed class BaphometAttackTrace:MonoBehaviour {
    static ConfigEntry<bool> nextSpawn;
    internal static void Initialize(ConfigFile config) {
        nextSpawn=config.Bind("Overkeks - Diagnostics","Trace next Baphomet spawn",false,
            "One-shot diagnostic for F8 manual Baphomet only. Automatically turns off after attaching; stops after 45 gameplay seconds or 120 lines. No attack/range changes.");
    }
    internal static void AttachManual(GameObject actor) {
        if(!OverkeksMode.Active || !BossTestTools.PlacementRequested || nextSpawn?.Value!=true || actor==null)return;
        nextSpawn.Value=false;
        if(actor.GetComponent<BaphometAttackTrace>()==null)OverkeksMode.AddOwnedComponent<BaphometAttackTrace>(actor);
    }
    TraceWindow window;ProjectileEnemyAI ai;Transform player;Animator animator;
    float nextSample,nextHeartbeat;string previousState;Vector3 previousPosition;bool finished;
    internal bool Active=>window!=null && OverkeksMode.Active && window.Active(Time.time);
    void Start() {
        ai=GetComponent<ProjectileEnemyAI>();if(ai==null){enabled=false;return;}
        player=OptionalMembers.Read(ai,"player") as Transform;animator=OptionalMembers.Read(ai,"animator") as Animator;
        previousPosition=transform.position;window=new TraceWindow(Time.time,45f,120);
        Note("begin","bounded=45-game-seconds/120-lines; retreatContract="+ProjectileRetreatFallback.ContractStatus(ai));
        Sample();
    }
    internal void Note(string action,string detail="") {
        if(window==null || !OverkeksMode.Active || !window.Take(Time.time))return;
        Plugin.DBG("ATTACK-TRACE","actor="+gameObject.GetInstanceID()+"; t="+Time.time.ToString("0.000",System.Globalization.CultureInfo.InvariantCulture)+"; event="+action+"; "+detail);
    }
    internal string State()=>"aiEnabled="+(ai!=null&&ai.isActiveAndEnabled)+"; state="+Read(ai,"currentState")+"; distance="+(player!=null?Vector3.Distance(transform.position,player.position).ToString("0.00",System.Globalization.CultureInfo.InvariantCulture):"?")+"; pos="+transform.position;
    static object Read(object target,string field){try{return OptionalMembers.Read(target,field)??"<unavailable>";}catch{return "<unavailable>";}}
    void Update() {
        if(!Active){Finish("expired/profile exit");return;}
        if(PauseHooks.SceneClockHeld || Time.timeScale<=.0001f || Time.time<nextSample)return;
        nextSample=Time.time+.25f;
        string state=Read(ai,"currentState").ToString();
        if(state!=previousState || Time.time>=nextHeartbeat){Sample();previousState=state;nextHeartbeat=Time.time+1f;}
    }
    void Sample() {
        var follower=OptionalMembers.Read(ai,"followerEntity");
        Note("state/navigation",State()+"; moved="+Vector3.Distance(previousPosition,transform.position).ToString("0.000",System.Globalization.CultureInfo.InvariantCulture)+
            "; lastShot="+Read(ai,"lastShootTime")+"; shootCooldown="+Read(ai,"shootCooldown")+"; lastGrab="+Read(ai,"lastGrabTime")+"; grabCooldown="+Read(ai,"grabCooldown")+"; castActive="+(OptionalMembers.Read(ai,"shootCoroutine")!=null)+"; minRange="+Read(ai,"minRange")+"; pendingTeleport="+Read(ai,"hasPendingTeleport")+"; target="+Read(ai,"pendingTeleportTarget")+
            "; follower="+(follower!=null)+"; followerEnabled="+Read(follower,"enabled")+"; simulate="+Read(follower,"simulateMovement")+"; stopped="+Read(follower,"isStopped")+
            "; speed="+Read(follower,"maxSpeed")+"; velocity="+Read(follower,"velocity")+"; destination="+Read(follower,"destination")+
            "; pathPending="+Read(follower,"pathPending")+"; reached="+Read(follower,"reachedDestination")+
            "; animator="+(animator!=null?animator.isActiveAndEnabled.ToString():"missing")+
            "; controller="+(animator!=null&&animator.runtimeAnimatorController!=null?animator.runtimeAnimatorController.name:"missing")+
            "; animStateHash="+(animator!=null?animator.GetCurrentAnimatorStateInfo(0).shortNameHash:0));
        previousPosition=transform.position;
    }
    void Finish(string reason){if(finished)return;finished=true;Plugin.DBG("ATTACK-TRACE","actor="+gameObject.GetInstanceID()+"; finished="+reason);enabled=false;}
    void OnDestroy(){if(window!=null)Finish("actor removed");}
    internal static BaphometAttackTrace Find(ProjectileEnemyAI owner) {
        if(owner==null)return null;var trace=owner.GetComponent<BaphometAttackTrace>();return trace!=null&&trace.Active?trace:null;
    }
}
