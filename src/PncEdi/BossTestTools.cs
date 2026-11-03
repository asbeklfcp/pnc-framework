using BepInEx.Configuration;
using BepInEx.Unity.Mono.Configuration;
using UnityEngine;
namespace PncEdi;
// Deterministic test actions use the same saved control catalog and debug gate as other tools.
internal static class BossTestTools {
    static ConfigEntry<KeyboardShortcut> baphomet,gravy,beast,dragon,wendigo;
    static float nextSpawn;
    internal static bool PlacementRequested {get;private set;}
    internal static void Initialize(ConfigFile config) {
        BaphometAttackTrace.Initialize(config);
        ConfigEntry<KeyboardShortcut> Bind(string key,string description)=>config.Bind("Tools",key,new KeyboardShortcut(KeyCode.None),description+" Requires Overkeks, enabled debug spawning, a living player and an unpaused run. Rebind through F11 Controls.");
        baphomet=Bind("SpawnBaphometTestKey","Spawn one Baphomet directly using the tested native Gargoyle conversion.");
        gravy=Bind("SpawnGravyTestKey","Spawn one Gravy directly using the native Serpent conversion.");
        beast=Bind("SpawnBlindedBeastTestKey","Spawn one native full-strength Blinded Beast (150/250 native stage capacities), separate from weakened random-pool fodder.");
        dragon=Bind("CallDragonTestKey","Activate the existing managed Dragon chaser at its native spawn point; no duplicate chaser is created.");
        wendigo=Bind("CallWendigoTestKey","Activate the existing managed Wendigo chaser at its native spawn point; no duplicate chaser is created.");
    }
    internal static void Tick() {
        if(!OverkeksMode.Active || !Plugin.CfgEnableDebugEnemySpawn.Value || PauseHooks.SceneClockHeld || Time.timeScale<=.0001f || Time.unscaledTime<nextSpawn)return;
        string target=Hotkeys.IsDown(baphomet)?"baphomet":Hotkeys.IsDown(gravy)?"gravy":Hotkeys.IsDown(beast)?"beast":Hotkeys.IsDown(dragon)?"dragon":Hotkeys.IsDown(wendigo)?"wendigo":null;
        if(target==null)return;
        var player=GameObject.FindGameObjectWithTag("Player")?.GetComponent<PlayerStats>();
        if(player==null || !player.IsAlive || GrabEndHelper.IsEscapeSceneActive()) {Plugin.DBG("BOSS-TEST","ignored: player unavailable or a scene is active");return;}
        nextSpawn=Time.unscaledTime+1f;
        Plugin.DBG("BOSS-TEST","requested="+target);
        PlacementRequested=true;
        try {switch(target) {
            case "baphomet":BaphometGargoyleSpawn.Spawn();break;
            case "gravy":GravySerpentSpawn.Spawn();break;
            case "beast":
                var spawned=DebugEnemySpawn.SpawnEnemyInstance(Plugin.CfgSpawnBlindedBeastNameHint.Value,"boss test Blinded Beast");
                if(spawned!=null)OverkeksMode.Own(spawned);
                break;
            case "dragon":ChaserSweeperHooks.ActivateTestChaser(false);break;
            case "wendigo":ChaserSweeperHooks.ActivateTestChaser(true);break;
        }} finally {PlacementRequested=false;}
    }
}
