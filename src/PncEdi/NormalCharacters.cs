using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using Object=UnityEngine.Object;
namespace PncEdi;

[HarmonyPatch]
internal static class NormalCharacters
{
    internal const string Section="Normal Characters";
    static readonly Dictionary<string,ConfigEntry<bool>> entries=new Dictionary<string,ConfigEntry<bool>>(StringComparer.OrdinalIgnoreCase);
    static int disabled;
    static bool cleanupPending;
    internal static void Initialize(ConfigFile config)
    {
        foreach(var pair in new[]{("imp","Imp"),("gargoyle","Gargoyle"),("gooper","Gooper"),("nun","Nun"),("zombie","Zombie"),("plantasha","Plantasha"),("goonshroom","Goonshroom"),("serpent","Black Serpent"),("mimic","Mimic"),("peekaboo","Peekaboo")})
        {
            var entry=config.Bind(Section,pair.Item2,true,"Allow this normal character to appear during gameplay.");
            entries.Add(pair.Item1,entry);
            entry.SettingChanged+=(s,e)=>{Refresh();cleanupPending=true;};
        }
        Refresh();cleanupPending=disabled>0;
    }
    static void Refresh()
    {
        disabled=entries.Values.Count(e=>!e.Value);
        EnemySpawnShuffle.ClearCache();
    }
    internal static bool AllowsKey(string key)=>string.IsNullOrEmpty(key)||CustomEnemyBridge.IsCustomKey(key)||!entries.TryGetValue(key,out var e)||e.Value;
    static string Key(string name)
    {
        string key=NameRemap.ResolveEnemyKey(name);
        if(entries.ContainsKey(key)||CustomEnemyBridge.IsCustomKey(key))return key;
        // Vanilla EnemyData labels can differ from prefab names.
        switch(key){case "imp":case "imp_enemy":return "imp";case "nun":case "hood":return "nun";case "black_serpent":return "serpent";case "plantenemy":case "plant_enemy":return "plantasha";case "peekaboo_enemy":return "peekaboo";default:return key;}
    }
    internal static bool Allows(GameObject prefab)=>prefab!=null&&(disabled==0||AllowsKey(Key(prefab.name)));
    static bool AllowsData(EnemyData data)=>data!=null&&AllowsKey(Key(data.enemyName));
    static GameObject[] Variants(EnemyData data)=>data==null?null:AccessTools.Field(typeof(EnemyData),"prefabVariants").GetValue(data) as GameObject[];
    static readonly System.Reflection.FieldInfo normalTable=AccessTools.Field(typeof(EnemySpawner),"enemyData");
    static readonly System.Reflection.FieldInfo arenaTable=AccessTools.Field(typeof(ArenaEnemySpawner),"enemyData");
    static EnemyData[] Table(MonoBehaviour spawner)=> (EnemyData[])(spawner is EnemySpawner?normalTable:arenaTable).GetValue(spawner);
    static void SelectAllowed(MonoBehaviour spawner,ref GameObject result)
    {
        if(disabled==0||result==null||Allows(result))return;
        var table=Table(spawner);
        if(table==null){result=null;return;}
        var candidates=new List<GameObject[]>();
        foreach(var data in table)
        {
            if(!AllowsData(data))continue;
            var variants=Variants(data);
            if(variants==null)continue;
            var allowed=variants.Where(Allows).ToArray();
            if(allowed.Length>0)candidates.Add(allowed);
        }
        if(candidates.Count==0){result=null;return;}
        var group=candidates[UnityEngine.Random.Range(0,candidates.Count)];
        result=group[UnityEngine.Random.Range(0,group.Length)];
    }
    // Last selection guard also catches results from existing shuffle/bias/custom patches.
    [HarmonyPatch(typeof(EnemySpawner),"GetEnemyPrefab"),HarmonyPostfix,HarmonyPriority(Priority.Normal),HarmonyBefore("com.edi.pnc.customenemies.gameplayguards")]
    static void RoomPick(EnemySpawner __instance,ref GameObject __result)=>SelectAllowed(__instance,ref __result);
    [HarmonyPatch(typeof(ArenaEnemySpawner),"GetEnemyPrefab"),HarmonyPostfix,HarmonyPriority(Priority.Normal),HarmonyBefore("com.edi.pnc.customenemies.gameplayguards")]
    static void ArenaPick(ArenaEnemySpawner __instance,ref GameObject __result)=>SelectAllowed(__instance,ref __result);
    // Direct arena spawn groups bypass the random-selection methods.
    static IEnumerator Empty(){yield break;}
    [HarmonyPatch(typeof(EnemySpawner),"SpawnSingleEnemy"),HarmonyPrefix,HarmonyPriority(Priority.First)]
    static bool RoomSpawn(GameObject __0,ref IEnumerator __result){if(disabled==0||Allows(__0))return true;__result=Empty();return false;}
    [HarmonyPatch(typeof(ArenaEnemySpawner),"SpawnSingleEnemy"),HarmonyPrefix,HarmonyPriority(Priority.First)]
    static bool ArenaSpawn(GameObject __0,ref IEnumerator __result){if(disabled==0||Allows(__0))return true;__result=Empty();return false;}
    // Mimics are also selected by the chest system, outside normal enemy spawners.
    // Its native SpawnMimicAtPosition falls back to SpawnChestAtPosition when this returns null.
    [HarmonyPatch(typeof(PixelCrushers.GridController.ChestManager),"GetMimicPrefab"),HarmonyPrefix]
    static bool ChestMimic(ref GameObject __result){if(AllowsKey("mimic"))return true;__result=null;return false;}
    [HarmonyPatch(typeof(PixelCrushers.GridController.ChestSpawner),"get_CanBeMimic"),HarmonyPostfix]
    static void ChestEligibility(ref bool __result){if(!AllowsKey("mimic"))__result=false;}
    internal static void Tick()
    {
        if(!cleanupPending)return;
        // Do not destroy an actor during a live capture or while a modal game pause is active.
        if((GrabScreen.Instance!=null&&GrabScreen.Instance.IsGrabbed)||Time.timeScale<=0f)return;
        cleanupPending=false;
        if(disabled==0)return;
        foreach(var spawner in Object.FindObjectsByType<EnemySpawner>(FindObjectsInactive.Include,FindObjectsSortMode.None))Clean(spawner);
        foreach(var spawner in Object.FindObjectsByType<ArenaEnemySpawner>(FindObjectsInactive.Include,FindObjectsSortMode.None))Clean(spawner);
        if(!AllowsKey("mimic"))
            foreach(var spawner in Object.FindObjectsByType<PixelCrushers.GridController.ChestSpawner>(FindObjectsInactive.Include,FindObjectsSortMode.None))
            {
                var objects=AccessTools.Field(spawner.GetType(),"spawnedObjects").GetValue(spawner) as List<GameObject>;
                if(objects==null)continue;
                for(int i=objects.Count-1;i>=0;i--)
                {
                    var go=objects[i];if(go==null||go.GetComponentInChildren<MimicEnemy>(true)==null||!go.scene.IsValid()||!go.scene.isLoaded)continue;
                    objects.RemoveAt(i);go.SetActive(false);Object.Destroy(go);
                }
            }
    }
    static void Clean(MonoBehaviour spawner)
    {
        var list=AccessTools.Field(spawner.GetType(),"spawnedEnemies").GetValue(spawner) as List<GameObject>;
        if(list==null)return;
        for(int i=list.Count-1;i>=0;i--)
        {
            var go=list[i];if(go==null||Allows(go))continue;
            // Remove only tracked scene instances; never touch prefab assets or pooled templates.
            if(!go.scene.IsValid()||!go.scene.isLoaded)continue;
            list.RemoveAt(i);
            go.SetActive(false);Object.Destroy(go);
        }
    }
}
