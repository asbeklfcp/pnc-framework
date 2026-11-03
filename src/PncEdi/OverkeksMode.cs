using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Object=UnityEngine.Object;

namespace PncEdi;

// Owns a creator-profile session, not global game objects or the core plugin's coroutines.
internal static class OverkeksMode
{
    internal static bool Active => live && GameplayProfiles.Current==GameplayProfile.Overkeks && !exiting;
    internal static bool HooksAllowed => Active || exiting;
    internal static bool Exiting => exiting;
    internal static bool UsesRecipe => live && (GameplayProfiles.Current==GameplayProfile.Overkeks || exiting);
    internal static int Epoch { get; private set; }
    static bool live,exiting;
    static OverkeksCoroutineHost host;
    static PlayerStats player;
    static readonly HashSet<Object> Owned=new HashSet<Object>();
    static readonly List<GameObject> Helpers=new List<GameObject>();
    static GameObject variantRoot;
    static readonly Dictionary<GameObject,GameObject> VariantTemplates=new Dictionary<GameObject,GameObject>();
    static readonly Dictionary<GameObject,NativeObject> Stashed=new Dictionary<GameObject,NativeObject>();
    static readonly Dictionary<MonoBehaviour,List<Action>> Spawners=new Dictionary<MonoBehaviour,List<Action>>();
    static readonly HashSet<int> Teardown=new HashSet<int>();
    static readonly List<Action> Presentation=new List<Action>();
    static bool presentationCaptured;
    static bool bossBarCaptured;
    static float healCarry;
    static GameObject healedEnemy;
    static float nextPlayerProbe;
    sealed class NativeObject { internal bool Active; internal Vector3 Position; internal Quaternion Rotation; }
    internal static float HeatProgress => player!=null&&player.MaxHeat>0 ? Mathf.Clamp01(player.CurrentHeat/player.MaxHeat) : 0;

    internal static void Enter() {
        if(live || GameplayProfiles.Current!=GameplayProfile.Overkeks) return;
        live=true; Epoch++; Teardown.Clear();
        try {
        var go=new GameObject("PNC Overkeks session"); Object.DontDestroyOnLoad(go);
        host=go.AddComponent<OverkeksCoroutineHost>();
        ApplyPlayer(Object.FindAnyObjectByType<PlayerStats>());
        PrepareLevelAssets();
        OverkeksEnemyRuntime.EnterMode();
        foreach(var spawner in Object.FindObjectsByType<EnemySpawner>(FindObjectsSortMode.None)) {
            PrepareSpawner(spawner); OverkeksPopulation.EnemySpawner_Awake_Postfix(spawner);
            SpawnHeatPotionDrop.SeedNearSpawner(spawner);
        }
        foreach(var spawner in Object.FindObjectsByType<ArenaEnemySpawner>(FindObjectsSortMode.None)) {
            PrepareSpawner(spawner); OverkeksPopulation.ArenaEnemySpawner_Awake_Postfix(spawner);
            SpawnHeatPotionDrop.SeedNearSpawner(spawner);
        }
        Plugin.DBG("OVERKEKS","entered epoch="+Epoch+"; recipe=October GodMode-oriented; maxHeat=100; passive cooling=ambient/peek only; Custom settings isolated");
        } catch(Exception ex) {
            Plugin.Log?.LogError("[OVERKEKS] Entry failed; returning to Vanilla: "+ex);
            Exit("failed entry");
            GameplayProfiles.ReturnToVanilla();
        }
    }
    internal static void ApplyPlayer(PlayerStats stats) {
        if(!Active || stats==null) return;
        bool changed=player!=stats; player=stats;
        if(Mathf.Abs(stats.MaxHeat-100)>0.01f) stats.SetMaxHeat(100);
        // Repair legacy enormous-HP emulation: preserve native capacity and block damage instead.
        if(changed && stats.IsAlive && stats.CurrentHealth>0 && stats.CurrentHealth<stats.MaxHealth)
            stats.SetHealth(stats.MaxHealth,false);
    }
    internal static void Tick() {
        if(GameplayProfiles.Current==GameplayProfile.Overkeks && !live) Enter();
        else if(GameplayProfiles.Current!=GameplayProfile.Overkeks && live) Exit("gate changed");
        if(Active && player==null && Time.unscaledTime>=nextPlayerProbe) {
            nextPlayerProbe=Time.unscaledTime+.5f;ApplyPlayer(Object.FindAnyObjectByType<PlayerStats>());
        }
        if(Active) {TickSceneHeal();TickBossBarDiagnostics();}
    }
    internal static void SceneChanged() {
        // New scene objects may already have run Awake. Keep their same-epoch leases;
        // old scene objects are destroyed by Unity. Do not erase new Awake registrations.
        player=null; OverkeksHeatSceneGate.ResetForNewScene();
        nextPlayerProbe=0;
        if(live){BaphometGrabOverlay.Hide();ChaserHealthBarUi.Clear();}
        ChaserSweeperHooks.PruneDestroyed();OverkeksEnemyRuntime.PruneDestroyed();
        // Awake in the incoming scene can precede this notification. Preserve new
        // per-object tuning/ownership records, otherwise it could be applied twice.
        OverkeksEnemySpawnShuffle.ClearCache();
        PrepareLevelAssets();
        Presentation.Clear();presentationCaptured=false;bossBarCaptured=false;healCarry=0;healedEnemy=null;
        Helpers.RemoveAll(go=>go==null);
        Owned.RemoveWhere(o=>o==null);
        var dead=new List<MonoBehaviour>(); foreach(var s in Spawners.Keys)if(s==null)dead.Add(s);
        foreach(var s in dead)Spawners.Remove(s);
        var gone=new List<GameObject>(); foreach(var s in Stashed.Keys)if(s==null)gone.Add(s);
        foreach(var s in gone)Stashed.Remove(s);
    }
    static void PrepareLevelAssets() {
        if(!Active||Object.FindAnyObjectByType<PlayerStats>()==null)return;
        var timer=System.Diagnostics.Stopwatch.StartNew();
        OverkeksEnemySpawnShuffle.WarmCurrentScene();CreatorVisualHost.WarmCurrentScene();
        Plugin.DBG("OVERKEKS-PREP","level preparation "+timer.ElapsedMilliseconds+"ms; metadata pool and native gallery hosts cached before exploration");
    }
    internal static void Exit(string why) {
        if(!live || exiting) return;
        exiting=true;
        int owned=Owned.Count,stashed=Stashed.Count,spawners=Spawners.Count;
        int failures=0;
        var ownedBosses=OwnedBossHudCleanup.Capture(Owned);
        void Safe(string step,Action action) {
            try{action();}catch(Exception ex){failures++;Plugin.Log?.LogError("[OVERKEKS] cleanup "+step+": "+ex);}
        }
        try {
            if(host!=null)Safe("coroutines",()=>host.StopAllCoroutines());
            // The full Overkeks heat/scene policy is leaving; finish an active scene before
            // removing its actors. Native health, controls, cursor and audio use shared teardown.
            Safe("scene",()=>GrabEndHelper.TryEndActiveHScene(forceImmediate:true));
            Safe("pending wakes",EnemyReactivationHelper.CancelScheduled);
            GrabEnemyProtection.Clear();
            foreach(var o in Owned)if(o!=null) {
                Teardown.Add(o.GetInstanceID());
                if(o is Behaviour behaviour)behaviour.enabled=false;
                var go=o as GameObject;
                if(go!=null)Safe("deactivate owned actor",()=>{ foreach(var audio in go.GetComponentsInChildren<AudioSource>(true))audio.Stop(); go.SetActive(false); });
            }
            foreach(var pair in Spawners)if(pair.Key!=null)foreach(var restore in pair.Value)Safe("spawner",restore);
            Safe("pickups",OverkeksFeatureRuntime.Reset); Safe("combat",CommunityCombatRuntime.Reset);
            Safe("enemy stats",OverkeksEnemyRuntime.ExitMode);
            foreach(var o in Owned)if(o!=null)Safe("destroy owned content",()=>Object.Destroy(o));
            foreach(var pair in Stashed)if(pair.Key!=null)Safe("native actor",()=>{
                pair.Key.transform.SetPositionAndRotation(pair.Value.Position,pair.Value.Rotation);
                pair.Key.SetActive(pair.Value.Active);
            });
            // Native SpawnEnemies coroutines can retain returned Transform lists. Keep these
            // inert points alive until scene unload rather than hand Unity dangling references.
            foreach(var helper in Helpers)if(helper!=null)helper.name="PNC retired spawn point";
            OverkeksPopulation.ResetForScene(); OverkeksEnemySpawnShuffle.ClearCache();
            OverkeksEnemyCombatTuning.ResetForScene(); ChaserSweeperHooks.ClearCache();
            OverkeksHeatSceneGate.ResetForNewScene(); ChaserHealthBarUi.Clear();
            foreach(var restore in Presentation)Safe("presentation",restore);
            Safe("owned boss HUD",()=>OwnedBossHudCleanup.Remove(ownedBosses));
            Presentation.Clear();presentationCaptured=false;bossBarCaptured=false;healCarry=0;healedEnemy=null;
            ChaserFodderSpawn.ClearCache();BlindedBeastWeakSpawn.ClearCache();
            Plugin.DBG("OVERKEKS","exit "+why+"; owned removal requests="+owned+" native restoration requests="+stashed+" spawners="+spawners+" cleanupErrors="+failures);
        } finally {
            Owned.Clear(); Spawners.Clear(); Stashed.Clear(); Helpers.Clear();
            VariantTemplates.Clear();variantRoot=null;
            if(host!=null)Object.Destroy(host.gameObject);
            host=null;player=null;live=false;exiting=false;
        }
    }
    internal static void CaptureBossBar(MiniBossHealthBarUI ui) {
        if(!Active||ui==null||bossBarCaptured)return;
        bossBarCaptured=true;
        foreach(var name in new[]{"currentBoss","scriptedFill","displayedFill","displayedMaxHealth","lastShownCurrent","lastShownMax"}) {
            var field=OptionalMembers.Field(typeof(MiniBossHealthBarUI),name);if(field==null)continue;
            var value=field.GetValue(ui);Presentation.Add(()=>{if(ui!=null)field.SetValue(ui,value);});
        }
    }
    internal static void CapturePresentation(GrabScreen grab) {
        if(!Active||grab==null||presentationCaptured)return;
        presentationCaptured=true;
        foreach(var name in new[]{"grabUI","grabOverlay","grabImage"}) {
            var field=OptionalMembers.Field(typeof(GrabScreen),name);
            var root=field?.GetValue(grab) as GameObject;if(root==null)continue;
            foreach(var transform in root.GetComponentsInChildren<Transform>(true)) {
                var go=transform.gameObject;bool active=go.activeSelf;
                Presentation.Add(()=>{if(go!=null)go.SetActive(active);});
            }
            foreach(var behaviour in root.GetComponentsInChildren<Behaviour>(true)) {
                bool enabled=behaviour.enabled;
                Presentation.Add(()=>{if(behaviour!=null)behaviour.enabled=enabled;});
            }
            foreach(var image in root.GetComponentsInChildren<UnityEngine.UI.Image>(true)) {
                var color=image.color;var material=image.material;var sprite=image.sprite;
                Presentation.Add(()=>{if(image!=null){image.color=color;image.material=material;image.sprite=sprite;}});
            }
            foreach(var animator in root.GetComponentsInChildren<Animator>(true)) {
                var controller=animator.runtimeAnimatorController;
                Presentation.Add(()=>{if(animator!=null)animator.runtimeAnimatorController=controller;});
            }
        }
    }
    internal static GameObject ResolveCaptureEnemy(GrabScreen grab) {
        if(grab==null || !GrabEndHelper.IsGrabSessionLive(grab))return null;
        return grab.GrabbingEnemy!=null ? grab.GrabbingEnemy : OptionalMembers.Read(grab,"hiddenEnemyToRestore") as GameObject;
    }
    static List<MonoBehaviour> HealingActors(GameObject enemy) {
        var actors=new List<MonoBehaviour>(EnemyAiTypes.On(enemy));
        if(enemy!=null)foreach(var ai in enemy.GetComponents<MonoBehaviour>())
            if(ai!=null && ai.GetType().Name=="ThreadMistressAI" && !actors.Contains(ai))actors.Add(ai);
        return actors;
    }
    internal static void ApplyCaptureReleaseReward(GameObject enemy) {
        if(!Active || enemy==null)return;
        int rewardOrdinal=0;
        foreach(var ai in HealingActors(enemy)) {
            if(OptionalMembers.Read(ai,"currentHealth") is not int current || current<=0 ||
                OptionalMembers.Read(ai,"maxHealth") is not int maximum || maximum<=0 ||
                OptionalMembers.Read(ai,"isDead") is bool dead && dead)continue;
            if(OverkeksConfig.HealCapturingEnemyOnRelease?.Value==true) {
                // Count actual living releases even when HP is already full; do not bank a full heal.
                if(rewardOrdinal==0)rewardOrdinal=CaptureRewardLedger.Next(enemy);
                int target=CaptureRewardLedger.Target(current,maximum,rewardOrdinal);
                if(target!=current) {
                    OptionalMembers.Write(ai,"currentHealth",target);
                    if(OptionalMembers.Read(ai,"healthBar") is EnemyHealthBar bar)bar.UpdateHealth((float)target/maximum);
                    ChaserHealthBarUi.UpdateHealth(ai,target,maximum);
                }
                Plugin.DBG("CAPTURE-REWARD","actor="+enemy.name+"; release="+rewardOrdinal+"; hp="+current+"->"+target+"; reason=release");
            }
            if(OverkeksConfig.TransformAfterCapture?.Value==true && ai is EnemyAI native &&
                OptionalMembers.Read(ai,"hasSecondStage") is bool stage && stage &&
                !(OptionalMembers.Read(ai,"isTransformed") is bool transformed && transformed)) {
                native.ForceTransform();
                Plugin.DBG("CAPTURE-REWARD","actor="+enemy.name+"; native second-stage transformation requested");
            }
        }
    }
    static float nextBossBarDiagnostic;
    static void TickBossBarDiagnostics() {
        if(Time.unscaledTime<nextBossBarDiagnostic)return;nextBossBarDiagnostic=Time.unscaledTime+5f;
        var ui=MiniBossHealthBarUI.Instance;if(ui==null)return;
        var owner=OptionalMembers.Read(ui,"currentBoss") as MonoBehaviour;
        if(owner==null) {
            Plugin.DBG("BOSS-BAR-STATE","actor=<none>; scripted="+OptionalMembers.Read(ui,"scriptedFill")+
                "; visible="+OptionalMembers.Read(ui,"isVisible")+"; displayed="+OptionalMembers.Read(ui,"lastShownCurrent")+"/"+OptionalMembers.Read(ui,"lastShownMax"));return;
        }
        Plugin.DBG("BOSS-BAR-STATE","actor="+owner.gameObject.name+"; id="+owner.GetInstanceID()+"; active="+owner.gameObject.activeInHierarchy+
            "; hp="+OptionalMembers.Read(owner,"currentHealth")+"/"+OptionalMembers.Read(owner,"maxHealth")+
            "; dead="+OptionalMembers.Read(owner,"isDead")+"; miniboss="+OptionalMembers.Read(owner,"isMiniBoss")+
            "; transformed="+OptionalMembers.Read(owner,"isTransformed")+"; displayed="+OptionalMembers.Read(ui,"lastShownCurrent")+"/"+OptionalMembers.Read(ui,"lastShownMax"));
    }
    static void TickSceneHeal() {
        var grab=GrabScreen.Instance;
        var enemy=ResolveCaptureEnemy(grab);
        if(enemy==null){healCarry=0;healedEnemy=null;return;}
        if(enemy!=healedEnemy){healedEnemy=enemy;healCarry=0;}
        healCarry+=(OverkeksConfig.CfgGrabSceneEnemyHealRate?.Value??0)*Time.deltaTime;
        int amount=Mathf.FloorToInt(healCarry);if(amount<=0)return;healCarry-=amount;
        foreach(var ai in HealingActors(enemy)) {
            var t=Traverse.Create(ai);if(!t.Field("currentHealth").FieldExists()||!t.Field("maxHealth").FieldExists())continue;
            int current=t.Field("currentHealth").GetValue<int>(),max=t.Field("maxHealth").GetValue<int>();
            if(current<=0||current>=max || OptionalMembers.Read(ai,"isDead") is bool dead && dead)continue;
            int healed=Mathf.Min(max,current+amount);t.Field("currentHealth").SetValue(healed);
            ChaserHealthBarUi.UpdateHealth(ai,healed,max);
            if(OptionalMembers.Read(ai,"healthBar") is EnemyHealthBar bar)bar.UpdateHealth(healed/(float)max);
        }
    }
    internal static bool IsKeepableGrappler(GameObject enemy) => GrappleEnemies.IsImpEnemy(enemy) || (enemy!=null && (NameRemap.ResolveEnemyKey(enemy.name)=="goonshroom" || enemy.name.IndexOf("goonshroom",StringComparison.OrdinalIgnoreCase)>=0));
    internal static void EnsurePlayerNotAttachedToEnemy(GameObject enemy) {
        if(enemy==null)return;
        var p=GameObject.FindGameObjectWithTag("Player");
        if(p!=null && p.transform.IsChildOf(enemy.transform))p.transform.SetParent(null,true);
    }
    internal static void PrepareChaserBody(GameObject enemy) {
        if(enemy==null)return;
        enemy.SetActive(true);
        foreach(var renderer in enemy.GetComponentsInChildren<Renderer>(true))renderer.enabled=true;
        foreach(var collider in enemy.GetComponentsInChildren<Collider>(true))collider.enabled=true;
        AccessTools.Method(typeof(EnemyReactivationHelper),"RestoreRigidbodies").Invoke(null,new object[]{enemy});
    }
    internal static Coroutine StartCoroutine(IEnumerator routine) => Active&&host!=null ? host.StartCoroutine(routine) : null;
    internal static void StopCoroutine(Coroutine routine) { if(host!=null && routine!=null)host.StopCoroutine(routine); }
    internal static GameObject NewHelper(string name) { var go=new GameObject(name); Helpers.Add(go); return go; }
    internal static GameObject PrepareVariant(GameObject prefab,bool weak,bool fodder) {
        if(!Active||prefab==null)return prefab;
        if(VariantTemplates.TryGetValue(prefab,out var existing)&&existing!=null)return existing;
        if(variantRoot==null) {variantRoot=NewHelper("Overkeks retained spawn templates");variantRoot.SetActive(false);}
        // Keep activeSelf authored, under an inactive parent: the template never runs
        // Awake/Start, while a native spawner's unparented clone activates normally.
        var template=Object.Instantiate(prefab,variantRoot.transform);
        var identity=template.AddComponent<OverkeksSpawnVariant>();identity.Weak=weak;identity.Fodder=fodder;
        var owner=template.AddComponent<OverkeksOwnedActor>();owner.Epoch=Epoch;
        VariantTemplates[prefab]=template;
        // A native spawn iterator may already hold this prefab when the mode exits.
        // Retain it inactive until scene unload; its old-epoch clone rejects admission.
        return template;
    }
    internal static GameObject NewOwned(string name, params Type[] components) { var go=new GameObject(name,components); Own(go); return go; }
    internal static T AddOwnedComponent<T>(GameObject go) where T:Component { var component=go.AddComponent<T>(); Owned.Add(component);return component; }
    internal static void Own(Object o) {
        if(o==null)return;
        Owned.Add(o);
        var go=o as GameObject;
        if(go!=null) {
            var marker=go.GetComponent<OverkeksOwnedActor>();
            if(marker==null)marker=go.AddComponent<OverkeksOwnedActor>();
            marker.Epoch=Epoch;
        }
    }
    internal static T TrackOwned<T>(T obj) where T:Object { Own(obj);return obj; }
    internal static bool IsOwned(Object o) {
        if(o==null)return false;
        if(Owned.Contains(o))return true;
        var go=o as GameObject; if(go==null&&o is Component component)go=component.gameObject;
        return go!=null&&go.GetComponentInParent<OverkeksOwnedActor>(true)!=null;
    }
    internal static bool IsTeardown(Object o) => o!=null&&(Teardown.Contains(o.GetInstanceID()) || exiting&&IsOwned(o));
    internal static T InstantiateOwned<T>(T original) where T:Object { var obj=Object.Instantiate(original);Own(obj);return obj; }
    internal static T InstantiateOwned<T>(T original,Vector3 pos,Quaternion rot) where T:Object { var obj=Object.Instantiate(original,pos,rot);Own(obj);return obj; }
    internal static T InstantiateOwned<T>(T original,Transform parent) where T:Object { var obj=Object.Instantiate(original,parent);Own(obj);return obj; }
    internal static T InstantiateOwned<T>(T original,Transform parent,bool worldPositionStays) where T:Object { var obj=Object.Instantiate(original,parent,worldPositionStays);Own(obj);return obj; }
    internal static T InstantiateOwned<T>(T original,Vector3 pos,Quaternion rot,Transform parent) where T:Object { var obj=Object.Instantiate(original,pos,rot,parent);Own(obj);return obj; }
    internal static void StashNative(GameObject go) {
        if(go==null||IsOwned(go)||Stashed.ContainsKey(go))return;
        Stashed[go]=new NativeObject{Active=go.activeSelf,Position=go.transform.position,Rotation=go.transform.rotation};
        go.SetActive(false);
    }
    internal static void CaptureSpawner(MonoBehaviour spawner) {
        if(!Active||spawner==null||Spawners.ContainsKey(spawner))return;
        var actions=new List<Action>();Spawners.Add(spawner,actions);
        // Ordinary spawner progress belongs to the run, not the profile. Restore authored
        // settings only. Sweeper's operational fields are leased because it is replaced.
        var names=new List<string>{"spawnPositions","minDistanceFromPlayer","useObjectPooling","preloadEnemies","destroyTriggersAfterSpawn","destroyTriggersAfterActivation"};
        if(spawner is SweeperEnemySpawner)names.AddRange(new[]{"enemySpawnData","spawnPoints","currentEnemy","hasSpawned","isSpawning","lastSpawnTime","selectedEnemyData","selectedSpawnPoint","followerEntity","enemyAudioSource"});
        foreach(var name in names) {
            var field=OptionalMembers.Field(spawner.GetType(),name);if(field==null||field.IsInitOnly)continue;
            object value=field.GetValue(spawner);if(value is Array array)value=array.Clone();
            var saved=value; actions.Add(()=>field.SetValue(spawner,saved));
        }
        foreach(var box in spawner.GetComponentsInChildren<BoxCollider>(true)) { var size=box.size;var center=box.center;actions.Add(()=>{if(box!=null){box.size=size;box.center=center;}}); }
        foreach(var sphere in spawner.GetComponentsInChildren<SphereCollider>(true)) { var radius=sphere.radius;actions.Add(()=>{if(sphere!=null)sphere.radius=radius;}); }
        foreach(var capsule in spawner.GetComponentsInChildren<CapsuleCollider>(true)) { var radius=capsule.radius;var height=capsule.height;actions.Add(()=>{if(capsule!=null){capsule.radius=radius;capsule.height=height;}}); }
    }
    internal static void PrepareSpawner(MonoBehaviour spawner) {
        if(!Active||spawner==null)return;
        CaptureSpawner(spawner);
        // Do not put reskinned actors in the native pool or destroy authored triggers;
        // both must be reusable when returning to Vanilla in this same run.
        foreach(var name in new[]{"useObjectPooling","preloadEnemies","destroyTriggersAfterSpawn","destroyTriggersAfterActivation"}) {
            var field=OptionalMembers.Field(spawner.GetType(),name);
            if(field!=null&&field.FieldType==typeof(bool))field.SetValue(spawner,false);
        }
    }
}
internal sealed class OverkeksCoroutineHost:MonoBehaviour {}
internal sealed class OverkeksSpawnVariant:MonoBehaviour { public bool Weak,Fodder; }
internal sealed class OverkeksOwnedActor:MonoBehaviour {
    public int Epoch;
    void OnEnable() {
        // Also catches native spawner clones of an owned template after switching away.
        if(Epoch!=0 && (!OverkeksMode.Active || Epoch!=OverkeksMode.Epoch)) { gameObject.SetActive(false);Destroy(gameObject);return; }
        if(OverkeksMode.Active)OverkeksMode.Own(gameObject);
    }
}
