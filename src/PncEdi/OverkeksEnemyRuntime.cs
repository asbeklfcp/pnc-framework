using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace PncEdi;

[HarmonyPatch]
internal static class OverkeksEnemyRuntime
{
    static ConfigEntry<bool> enabled;
    static ConfigEntry<string> identities;
    static ConfigEntry<float> move,meleeMove,meleeRate,damage;
    internal static bool Active=>OverkeksMode.Active || ResourcePolicy.Allows(GameplayProfiles.Current,Plugin.GameplayTweaksEnabled,enabled?.Value??false);
    internal static string IdentityProfiles=>OverkeksMode.Active ? "" : identities?.Value??"";
    sealed class Snapshot {
        internal MonoBehaviour Ai;
        internal readonly List<Action> Restore=new List<Action>();
        internal int Maximum;
        internal bool FollowerCaptured;
    }
    static readonly Dictionary<int,Snapshot> Snapshots=new Dictionary<int,Snapshot>();
    static readonly Dictionary<MonoBehaviour,Snapshot> Native = new Dictionary<MonoBehaviour,Snapshot>();
    static readonly HashSet<MonoBehaviour> ModeApplied = new HashSet<MonoBehaviour>();
    static readonly HashSet<MonoBehaviour> Initialized = new HashSet<MonoBehaviour>();
    static readonly Dictionary<MonoBehaviour,string> PendingIdentities = new Dictionary<MonoBehaviour,string>();
    static readonly Dictionary<GameObject,int> SpawnEpochs = new Dictionary<GameObject,int>();
    internal static bool SpawnedInMode(GameObject enemy) => enemy!=null && SpawnEpochs.TryGetValue(enemy,out int epoch) && epoch==OverkeksMode.Epoch && OverkeksMode.Active;
    internal static void CaptureForMode(MonoBehaviour ai) {
        if(ai==null)return;
        if(Native.TryGetValue(ai,out var existing)){CaptureFollower(existing);return;}
        OverkeksEnemyCombatTuning.Forget(ai.GetInstanceID());EnemyIdentityTuning.Forget(ai.GetInstanceID());
        var snap=new Snapshot{Ai=ai,Maximum=Get(ai,"maxHealth") is int hp?hp:0};
        foreach(var name in ModeFields)Lease(snap,ai,name);
        CaptureFollower(snap);
        Native.Add(ai,snap);
    }
    static void CaptureFollower(Snapshot snapshot) {
        // InitializeComponents discovers/configures the follower during native Start.
        // Never lease a pre-initialization/null reference as the movement baseline.
        if(snapshot.FollowerCaptured || !Initialized.Contains(snapshot.Ai))return;
        var follower=Get(snapshot.Ai,"followerEntity");if(follower==null)return;
        Lease(snapshot,follower,"maxSpeed");snapshot.FollowerCaptured=true;
    }
    static readonly string[] ModeFields={"maxHealth","secondStageMaxHealth","attackDamage","projectileDamage","attackCooldown","chargeCooldown","secondStageMoveSpeed","chargeSpeed","spinSpeed","grabCooldown","shootCooldown","spinMaxRange","spinCooldown","grabRange","attackRange","minRange"};
    static void Start(MonoBehaviour ai) {
        Initialized.Add(ai);CaptureForMode(ai);
        PendingIdentities.TryGetValue(ai,out var identity);PendingIdentities.Remove(ai);
        if(OverkeksMode.Active) {SpawnEpochs[ai.gameObject]=OverkeksMode.Epoch;ApplyMode(ai,identity);}
        else Apply(ai);
    }
    internal static void EnterMode() {
        foreach(var type in new[]{typeof(EnemyAI),typeof(SpinningEnemyAI),typeof(ProjectileEnemyAI),typeof(BrawlerEnemyAI),typeof(ChargingEnemyAI)})
            foreach(var obj in UnityEngine.Object.FindObjectsByType(type,FindObjectsSortMode.None)) ApplyMode(obj as MonoBehaviour);
    }
    static void RestoreHealthFraction(MonoBehaviour ai,int current,int maximum) {
        if(Get(ai,"maxHealth") is int restored && maximum>0)
            Set(ai,"currentHealth",current<=0?0:Mathf.Max(1,Mathf.CeilToInt(restored*Mathf.Clamp01(current/(float)maximum))));
    }
    // Conversion changes identity, not the native baseline. Both manual-before-Start and
    // natural-after-Start paths must rebase from the same snapshot before tuning once.
    internal static void RefreshConvertedActor(GameObject actor,string identity) {
        if(!OverkeksMode.Active){EnemyIdentityTuning.Apply(actor,identity);return;}
        var ai=EnemyAiTypes.First(actor);if(ai==null)return;
        CaptureForMode(ai);
        if(!Initialized.Contains(ai)) {
            PendingIdentities[ai]=identity;
            Plugin.DBG("OVERKEKS-ENEMY","conversion queued until native Start; actor="+actor.name+"; identity="+identity);return;
        }
        ModeApplied.Remove(ai);ApplyMode(ai,identity);
    }
    static void ApplyMode(MonoBehaviour ai,string identity=null) {
        if(!OverkeksMode.Active||ai==null||!Initialized.Contains(ai)||ModeApplied.Contains(ai))return;
        if(Get(ai,"isDead") is bool dead && dead)return;
        var diagnosticClock=System.Diagnostics.Stopwatch.StartNew();
        var diagnosticStages=new System.Text.StringBuilder();
        double previousMs=0;
        void Mark(string stage) {double now=diagnosticClock.Elapsed.TotalMilliseconds;diagnosticStages.Append(stage).Append("Ms=").Append((now-previousMs).ToString("0.###",System.Globalization.CultureInfo.InvariantCulture)).Append("; ");previousMs=now;}
        CaptureForMode(ai);Mark("capture");
        int current=Get(ai,"currentHealth") is int health?health:0;
        int maximum=Get(ai,"maxHealth") is int hp?hp:0;
        // Enrol before the first write so a failed compatibility adapter is reversible too.
        ModeApplied.Add(ai);
        try {
        foreach(var restore in Native[ai].Restore)restore();
        OverkeksEnemyCombatTuning.Forget(ai.GetInstanceID());
        EnemyIdentityTuning.Forget(ai.GetInstanceID());
        Mark("restore");
        OverkeksEnemyCombatTuning.ApplyEnemyHealthMultiplier(ai);
        OverkeksEnemyCombatTuning.ApplyEnemyPace(ai,ai is EnemyAI||ai is BrawlerEnemyAI||ai is ChargingEnemyAI);
        if(ai is ProjectileEnemyAI projectile)GooperTuning.Apply(projectile);
        if(ai is EnemyAI melee)OverkeksNunGrabTuning.ApplyNunGrabPreference(melee);
        if(ai is SpinningEnemyAI spin)OverkeksEnemyCombatTuning.ApplySpinningEnemyTuningForKey(spin);
        Mark("globalTuning");
        EnemyIdentityTuning.Apply(ai.gameObject,identity);Mark("identityTuning");
        BlindedBeastWeakSpawn.TryConsumePendingAndWeaken(ai.gameObject);
        } finally {RestoreHealthFraction(ai,current,maximum);Mark("weakAndHealth");diagnosticClock.Stop();
            Plugin.DBG("SPAWN-PERF","phase=mode-adapter; actor="+ai.name+"; totalMs="+diagnosticClock.Elapsed.TotalMilliseconds.ToString("0.###",System.Globalization.CultureInfo.InvariantCulture)+"; "+diagnosticStages);}
        Plugin.DBG("OVERKEKS-ENEMY","applied "+EnemyIdentityTuning.ResolveIdentityKey(ai.gameObject,ai)+" nativeHP="+Native[ai].Maximum+" modeHP="+Get(ai,"maxHealth")+" currentHP="+Get(ai,"currentHealth")+" isDead="+Get(ai,"isDead"));
    }
    internal static void ExitMode() {
        int count=0;
        foreach(var ai in ModeApplied)if(ai!=null && !OverkeksMode.IsOwned(ai)) {
            int current=Get(ai,"currentHealth") is int health?health:0;
            int maximum=Get(ai,"maxHealth") is int hp?hp:0;
            foreach(var restore in Native[ai].Restore)restore();
            // Native transformation is run progression, not a temporary profile multiplier.
            // Restore the native second-stage capacity/speed when that stage was reached in-mode.
            if(ai is EnemyAI && Get(ai,"hasSecondStage") is bool stage && stage && Get(ai,"isTransformed") is bool transformed && transformed) {
                if(Get(ai,"secondStageMaxHealth") is int stageMaximum && stageMaximum>0)Set(ai,"maxHealth",stageMaximum);
                if(Get(ai,"secondStageMoveSpeed") is float stageSpeed && stageSpeed>0)Set(Get(ai,"followerEntity"),"maxSpeed",stageSpeed);
            }
            RestoreHealthFraction(ai,current,maximum);count++;
        }
        ModeApplied.Clear();PendingIdentities.Clear();SpawnEpochs.Clear();EnemyIdentityTuning.ResetForScene();
        var gone=new List<MonoBehaviour>();foreach(var ai in Native.Keys)if(ai==null)gone.Add(ai);
        foreach(var ai in gone){Native.Remove(ai);Initialized.Remove(ai);PendingIdentities.Remove(ai);}
        Plugin.DBG("OVERKEKS-ENEMY","restored native stats on "+count+" surviving actors; health fractions preserved");
    }
    internal static void PruneDestroyed() {
        var gone=new List<MonoBehaviour>();foreach(var ai in Native.Keys)if(ai==null)gone.Add(ai);
        foreach(var ai in gone){Native.Remove(ai);ModeApplied.Remove(ai);Initialized.Remove(ai);PendingIdentities.Remove(ai);}
        var dead=new List<GameObject>();foreach(var enemy in SpawnEpochs.Keys)if(enemy==null)dead.Add(enemy);
        foreach(var enemy in dead)SpawnEpochs.Remove(enemy);
    }
    static bool wasActive;
    static float nextScan;
    static readonly string[] Fields={"maxHealth","secondStageMaxHealth","attackDamage","projectileDamage","attackCooldown","chargeCooldown","secondStageMoveSpeed","chargeSpeed","spinSpeed"};
    internal static bool HasMarker(GameObject enemy,string name) {
        // Both markers belong to this assembly. Avoid Harmony's broad type-name search
        // for every ordinary enemy and every diagnostic identity resolution.
        if(enemy==null)return false;
        return name switch {
            "PncEdi.BaphometGargoyleMarker" => enemy.GetComponent<BaphometGargoyleMarker>()!=null,
            "PncEdi.GravySerpentMarker" => enemy.GetComponent<GravySerpentMarker>()!=null,
            _ => false
        };
    }
    internal static void ScaleFollower(object follower,float factor) {
        if(Get(follower,"maxSpeed") is float speed&&speed>0)Set(follower,"maxSpeed",Mathf.Max(.5f,speed*factor));
    }
    internal static void Initialize(ConfigFile config) {
        const string section="Custom - Overkeks enemy tuning (staged)";
        enabled=config.Bind(section,"Enabled",false,"Original global/identity tuning for mapped native enemies. Custom only; real-time exit restores leased stats.");
        identities=config.Bind(section,"IdentityProfiles","zombie=move:0.78,hp:1.75,dmg:1.85;nun=move:1.35,hp:0.95,dmg:1.4;gargoyle=move:1.08,hp:1.5,dmg:1.45;gooper=move:1.12,hp:1.4,dmg:1.4;plantasha=move:1.1,hp:1.45,dmg:1.4;gravy=move:0.7,hp:1.9,dmg:1.45;baphomet=move:0.75,hp:3,dmg:1.4","Original per-identity multipliers. No new character artwork is included.");
        ConfigEntry<float> Bind(string key,float value)=>config.Bind(section,key,value,new ConfigDescription("Original global factor.",new AcceptableValueRange<float>(.1f,10)));
        move=Bind("Move",1.55f);meleeMove=Bind("MeleeMove",1.95f);meleeRate=Bind("MeleeRate",2.15f);damage=Bind("Damage",1.55f);
        void ResetOn<T>(ConfigEntry<T> entry)=>entry.SettingChanged+=(_,__)=>{if(!OverkeksMode.UsesRecipe)Reset();};
        ResetOn(enabled);ResetOn(identities);ResetOn(move);ResetOn(meleeMove);ResetOn(meleeRate);ResetOn(damage);
    }
    static object Get(object obj,string name) {
return OptionalMembers.Read(obj,name);
    }
    static void Set(object obj,string name,object value) {
OptionalMembers.Write(obj,name,value);
    }
    static void Lease(Snapshot snapshot,object target,string name) {
        if(target==null)return;
        var field=OptionalMembers.Field(target.GetType(),name);
        var prop=field==null?OptionalMembers.Property(target.GetType(),name):null;
        if(field==null&&(prop==null||!prop.CanRead||!prop.CanWrite))return;
        var original=Get(target,name);
        snapshot.Restore.Add(()=>Set(target,name,original));
    }
    static void Scale(object target,string name,float factor) {
        if(target==null)return;
        var value=Get(target,name);
        if(value is float number&&number>0)Set(target,name,number*factor);
        else if(value is int integer&&integer>0)Set(target,name,Mathf.Max(1,Mathf.CeilToInt(integer*factor)));
    }
    static void Apply(MonoBehaviour ai) {
        if(OverkeksMode.Active||!Active||ai==null||Snapshots.ContainsKey(ai.GetInstanceID()))return;
        var dead=Get(ai,"isDead"); if(dead is bool b&&b)return;
        var snapshot=new Snapshot{Ai=ai,Maximum=Get(ai,"maxHealth") is int hp?hp:0};
        float healthRatio=1;
        if(snapshot.Maximum>0&&Get(ai,"currentHealth") is int current)healthRatio=Mathf.Clamp01(current/(float)snapshot.Maximum);
        foreach(var name in Fields)Lease(snapshot,ai,name);
        object follower=Get(ai,"followerEntity");Lease(snapshot,follower,"maxSpeed");
        Snapshots.Add(ai.GetInstanceID(),snapshot);
        EnemyIdentityTuning.Apply(ai.gameObject);
        Scale(follower,"maxSpeed",ai is EnemyAI||ai is ChargingEnemyAI?meleeMove.Value:move.Value);
        Scale(ai,"chargeSpeed",meleeMove.Value);Scale(ai,"secondStageMoveSpeed",move.Value);
        if(!NunGrabTuning.IsNunEnemy(ai.gameObject)) {
            if(Get(ai,"attackCooldown") is float cooldown&&cooldown>0)Set(ai,"attackCooldown",cooldown/meleeRate.Value);
            if(Get(ai,"chargeCooldown") is float charge&&charge>0)Set(ai,"chargeCooldown",charge/meleeRate.Value);
        }
        Scale(ai,"attackDamage",damage.Value);Scale(ai,"projectileDamage",damage.Value);
        if(Get(ai,"maxHealth") is int maximum&&Get(ai,"currentHealth") is int)
            Set(ai,"currentHealth",healthRatio<=0?0:Mathf.Max(1,Mathf.CeilToInt(maximum*healthRatio)));
    }
    internal static void Tick() {
        if(OverkeksMode.Active) return;
        bool active=Active;if(wasActive&&!active)Reset();wasActive=active;
        if(!active||Time.unscaledTime<nextScan)return;
        nextScan=Time.unscaledTime+1;
        var dead=new List<int>();foreach(var pair in Snapshots)if(pair.Value.Ai==null)dead.Add(pair.Key);
        foreach(int id in dead){Snapshots.Remove(id);EnemyIdentityTuning.Forget(id);}
        foreach(var type in new[]{typeof(EnemyAI),typeof(SpinningEnemyAI),typeof(ProjectileEnemyAI),typeof(BrawlerEnemyAI),typeof(ChargingEnemyAI)})
            foreach(var obj in UnityEngine.Object.FindObjectsByType(type,FindObjectsSortMode.None))Apply(obj as MonoBehaviour);
    }
    internal static void Reset() {
        foreach(var snapshot in Snapshots.Values)if(snapshot.Ai!=null) {
            int current=Get(snapshot.Ai,"currentHealth") is int c?c:0;
            int maximum=Get(snapshot.Ai,"maxHealth") is int m?m:0;
            float ratio=maximum>0?Mathf.Clamp01(current/(float)maximum):0;
            foreach(var restore in snapshot.Restore)restore();
            if(maximum>0&&snapshot.Maximum>0&&Get(snapshot.Ai,"currentHealth") is int)
                Set(snapshot.Ai,"currentHealth",current<=0?0:Mathf.Max(1,Mathf.CeilToInt(snapshot.Maximum*ratio)));
        }
        Snapshots.Clear();EnemyIdentityTuning.ResetForScene();wasActive=false;nextScan=0;
    }
    [HarmonyPatch(typeof(EnemyAI),"Start"),HarmonyPostfix,HarmonyPriority(Priority.Last)]
    static void Melee(EnemyAI __instance)=>Start(__instance);
    [HarmonyPatch(typeof(SpinningEnemyAI),"Start"),HarmonyPostfix,HarmonyPriority(Priority.Last)]
    static void Spinning(SpinningEnemyAI __instance)=>Start(__instance);
    [HarmonyPatch(typeof(ProjectileEnemyAI),"Start"),HarmonyPostfix,HarmonyPriority(Priority.Last)]
    static void Projectile(ProjectileEnemyAI __instance)=>Start(__instance);
    [HarmonyPatch(typeof(BrawlerEnemyAI),"Start"),HarmonyPostfix,HarmonyPriority(Priority.Last)]
    static void Brawler(BrawlerEnemyAI __instance)=>Start(__instance);
    [HarmonyPatch(typeof(ChargingEnemyAI),"Start"),HarmonyPostfix,HarmonyPriority(Priority.Last)]
    static void Charging(ChargingEnemyAI __instance)=>Start(__instance);
}
