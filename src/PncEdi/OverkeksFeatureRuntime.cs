using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using PixelCrushers.GridController;
using UnityEngine;

namespace PncEdi;

// The creator recipe is fixed; opt-in Custom features remain separately configurable.
[HarmonyPatch]
internal static class OverkeksFeatureRuntime
{
    static ConfigEntry<bool> casts, potions, drops, cooling;
    static ConfigEntry<int> potionCount;
    static ConfigEntry<float> potionRange, normalCooling, bossCooling;
    static readonly Dictionary<GameObject,OverkeksDropLease> OwnedDrops = new Dictionary<GameObject,OverkeksDropLease>();
    static bool lastActive;
    static bool InCustom => ResourcePolicy.Allows(GameplayProfiles.Current,Plugin.GameplayTweaksEnabled,true);
    internal static bool Casts => OverkeksMode.Active || InCustom && (casts?.Value ?? false);
    internal static bool Potions => OverkeksMode.Active || InCustom && (potions?.Value ?? false);
    internal static bool Drops => OverkeksMode.Active || InCustom && (drops?.Value ?? false);
    internal static bool KillCooling => OverkeksMode.Active || InCustom && (cooling?.Value ?? false);
    internal static int PotionCount => OverkeksMode.Active ? 2 : potionCount?.Value ?? 2;
    internal static float PotionRange => OverkeksMode.Active ? 3.5f : potionRange?.Value ?? 3.5f;
    internal static float NormalCooling => OverkeksMode.Active ? 5 : normalCooling?.Value ?? 5;
    internal static float BossCooling => OverkeksMode.Active ? 10 : bossCooling?.Value ?? 10;

    internal static void Initialize(ConfigFile config) {
        const string section="Custom - Overkeks resource systems (staged)";
        casts=config.Bind(section,"CastRecovery",false,"Replace weapon heat with the original accumulating cast-recovery slowdown. Custom only.");
        potions=config.Bind(section,"PotionAutoUse",false,"Use eligible potions on pickup; otherwise keep native inventory pickup. Custom only.");
        drops=config.Bind(section,"ExtraHeatPotions",false,"Seed owned heat potions near room/arena spawners. Disabling removes uncollected owned drops.");
        cooling=config.Bind(section,"KillCooling",false,"Cool heat on defeats. Does not override Pressure and Release lock floors.");
        potionCount=config.Bind(section,"PotionsPerSpawner",2,new ConfigDescription("Original maximum per spawner.",new AcceptableValueRange<int>(0,20)));
        potionRange=config.Bind(section,"PotionPickupRange",3.5f,new ConfigDescription("Original attraction/pickup range.",new AcceptableValueRange<float>(1.5f,10)));
        normalCooling=config.Bind(section,"NormalKillCooling",5f,new ConfigDescription("Heat removed for an ordinary defeat.",new AcceptableValueRange<float>(0,1000)));
        bossCooling=config.Bind(section,"HighValueKillCooling",10f,new ConfigDescription("Heat removed for original high-value identities.",new AcceptableValueRange<float>(0,1000)));
        void ResetOn<T>(ConfigEntry<T> entry)=>entry.SettingChanged+=(_,__)=>{if(!OverkeksMode.UsesRecipe)Reset();};
        ResetOn(casts);ResetOn(potions);ResetOn(drops);ResetOn(cooling);
        ResetOn(potionCount);ResetOn(potionRange);ResetOn(normalCooling);ResetOn(bossCooling);
    }
    internal static void Own(GameObject go) {
        if(go==null) return;
        // A pool may recycle the object before Destroy(oldLease) is processed.
        // Always use a fresh identity; an old callback cannot release its replacement.
        var lease=go.AddComponent<OverkeksDropLease>(); lease.Capture(); OwnedDrops[go]=lease;
    }
    internal static void Release(GameObject go,OverkeksDropLease lease) {
        if(OwnedDrops.TryGetValue(go,out var current)&&ResourcePolicy.CurrentLease(current,lease))OwnedDrops.Remove(go);
    }
    internal static void Reset() {
        // Stop admission first through the live profile/feature gate; deferred Unity
        // destruction cannot permit stale magnets to collect a drop on exit.
        foreach(var go in new List<GameObject>(OwnedDrops.Keys)) if(go!=null) {
            foreach(var magnet in go.GetComponents<SpawnHeatPotionMagnet>()) magnet.enabled=false;
            if(PickupPoolManager.Instance!=null) PickupPoolManager.Instance.ReturnItemPickup(go);
            else { go.SetActive(false); UnityEngine.Object.Destroy(go); }
        }
        OwnedDrops.Clear(); SpawnHeatPotionDrop.ResetForScene();
        WeaponHeatCostGate.ResetForNewScene(); lastActive=false;
    }
    internal static void Tick() {
        bool active=InCustom || OverkeksMode.Active;
        if(lastActive&&!active) Reset();
        lastActive=active;
        var dead=new List<GameObject>();foreach(var go in OwnedDrops.Keys)if(go==null)dead.Add(go);
        foreach(var go in dead)OwnedDrops.Remove(go);
    }
    internal static string ResolveIdentity(GameObject enemy,MonoBehaviour ai) {
        if(enemy==null) return "";
        // Variant adapters will supply their own identity when they are ported.
        string gallery=null;
        if(ai!=null) { var field=Traverse.Create(ai).Field("galleryEnemyID"); if(field.FieldExists()) gallery=field.GetValue<string>(); }
        return EnemyIdentityTuning.ResolveIdentityKey(enemy,ai);
    }
    [HarmonyPatch(typeof(EnemySpawner),"Awake"),HarmonyPostfix]
    static void SeedRoom(EnemySpawner __instance)=>SpawnHeatPotionDrop.SeedNearSpawner(__instance);
    [HarmonyPatch(typeof(ArenaEnemySpawner),"Awake"),HarmonyPostfix]
    static void SeedArena(ArenaEnemySpawner __instance)=>SpawnHeatPotionDrop.SeedNearSpawner(__instance);
}

// A consumed/pool-returned object no longer belongs to this profile. In particular,
// cleanup must never destroy a native pickup that later reuses that pooled object.
internal sealed class OverkeksDropLease : MonoBehaviour
{
    Rigidbody body; bool kinematic,gravity;
    internal void Capture() {body=GetComponent<Rigidbody>();if(body!=null){kinematic=body.isKinematic;gravity=body.useGravity;}}
    void OnDisable() {
        if(body!=null){body.isKinematic=kinematic;body.useGravity=gravity;}
        foreach(var magnet in GetComponents<SpawnHeatPotionMagnet>()){magnet.enabled=false;Destroy(magnet);}
        OverkeksFeatureRuntime.Release(gameObject,this); Destroy(this);
    }
    void OnDestroy() => OverkeksFeatureRuntime.Release(gameObject,this);
}
