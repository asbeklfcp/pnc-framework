using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace PncCustomEnemies;

// Built into the signed PNC Custom Enemies loader; no extra BepInEx DLL.
// Contains ONLY the accepted Witch/Joker compatibility fixes from private QA.
internal static class GameplayGuards
{
    // These are manifest IDs. On-disk folders use hyphens but IDs use underscores.
    // v0.1.8 mistakenly used the directory names and skipped both gameplay guards.
    internal const string WitchPackageId = "femboy_witch";
    internal const string JokerPackageId = "joker_wall";
    private static Harmony _patches;
    internal static ManualLogSource Log;
    private static readonly FieldInfo WaitingForInput = AccessTools.Field(typeof(GameOverScreen), "isWaitingForInput");
    private static readonly FieldInfo HasShownGameOver = AccessTools.Field(typeof(GameOverScreen), "hasShownGameOver");
    private static bool _loggedGameOverLayer;

    internal static void Install(ManualLogSource log)
    {
        if (_patches != null) return;
        Log = log;
        if (WaitingForInput == null || HasShownGameOver == null)
            log.LogWarning("[GAMEPLAY-GUARDS] Game-over status fields unavailable; Joker visual guard cannot operate on this game build.");
        _patches = new Harmony("com.edi.pnc.customenemies.gameplayguards");
        log.LogInfo("[GAMEPLAY-GUARDS] package ids: witch=" + WitchPackageId
            + " loaded=" + PackageAssemblies.IsLoaded(WitchPackageId)
            + " active=" + PackageLifecycle.Active(WitchPackageId)
            + "; joker=" + JokerPackageId
            + " loaded=" + PackageAssemblies.IsLoaded(JokerPackageId)
            + " active=" + PackageLifecycle.Active(JokerPackageId));
        if (PackageAssemblies.IsLoaded(WitchPackageId) && PackageLifecycle.Active(WitchPackageId))
        {
            try { _patches.PatchAll(typeof(SingleWitchSpawnGuard)); }
            catch (Exception e) { log.LogError("[GAMEPLAY-GUARDS] Witch spawn patch failed: " + e); }
            log.LogInfo("[GAMEPLAY-GUARDS] Witch naturally spawns with one live pursuer per floor");
        }
        else log.LogInfo("[GAMEPLAY-GUARDS] Witch disabled; no Witch gameplay patches installed");
        if (PackageAssemblies.IsLoaded(JokerPackageId) && PackageLifecycle.Active(JokerPackageId))
        {
            JokerWallSupportGuard.Install(_patches, log);
            JokerDetectionRangeGuard.Install(_patches, log);
            CustomEnemyPlugin.Instance.gameObject.AddComponent<JokerGameOverVisualTick>();
            log.LogInfo("[GAMEPLAY-GUARDS] Joker asset-aware placement, extended detection and game-over visuals installed");
        }
        else log.LogInfo("[GAMEPLAY-GUARDS] Joker disabled; no Joker gameplay patches installed");
    }

    internal static void LateTick()
    {
        if (!PackageLifecycle.Active(JokerPackageId)) return;
        bool prompt = IsLegitimateGameOverPromptVisible();
        if (!prompt) { _loggedGameOverLayer = false; return; }
        GameObject overlay = GameObject.Find("WallPictureTrapGrabOverlay");
        if (overlay == null) return;
        Canvas canvas = overlay.GetComponent<Canvas>();
        if (canvas == null) return;
        if (canvas.sortingOrder > -1000)
        {
            canvas.sortingOrder = -1000;
            if (!_loggedGameOverLayer)
            {
                Log?.LogInfo("[GAMEPLAY-GUARDS] Joker overlay moved behind legitimate game-over controls");
                _loggedGameOverLayer = true;
            }
        }
    }

    private static bool IsLegitimateGameOverPromptVisible()
    {
        GameOverScreen screen = GameOverScreen.Instance;
        if (screen == null || WaitingForInput == null || HasShownGameOver == null) return false;
        try { return (bool)WaitingForInput.GetValue(screen) || (bool)HasShownGameOver.GetValue(screen); }
        catch (Exception) { return false; }
    }

    internal static void Uninstall() => _patches?.UnpatchSelf();
}

// Keep the late-UI overlay ordering without changing execution order of the
// existing BepInEx loader itself (which must load packages before spawning).
[DefaultExecutionOrder(30000)]
internal sealed class JokerGameOverVisualTick : MonoBehaviour
{
    private void LateUpdate() => GameplayGuards.LateTick();
}

// Last postfix sees the actual chosen prefab even when other gameplay modes
// replace GetEnemyPrefab via Harmony prefixes. Never deletes an existing Witch.
[HarmonyPatch]
internal static class SingleWitchSpawnGuard
{
    private const string WitchPrefabName = "CustomEnemy_femboy_witch";
    // Witch AI chases/teleports after it finds the player. A 40-metre/LOS
    // guard was insufficient: earlier living Witches remained active farther
    // away or around corners, letting three converge on the player.
    // Enforce one living, instantiated Witch per active FLOOR scene, without
    // changing its weighted first-spawn chance or removing existing enemies.
    // Reserve a successful Witch selection briefly to cover multiple spawn
    // requests in one generation batch before Instantiate has run.
    private const float PendingSelectionSeconds = 3.0f;
    private static readonly FieldInfo CurrentHealth = AccessTools.Field(typeof(SpinningEnemyAI), "currentHealth");
    private static float _nextLogTime;
    private static int _reservedSceneHandle = int.MinValue;
    private static float _reservedAt = float.NegativeInfinity;
    // A Witch that was genuinely active must continue occupying the floor
    // slot until it dies or is destroyed, even if it stops chasing or loses
    // its target. Do NOT infer activity from a '(Clone)' prefab name:
    // that previously miscounted 58 dormant pool objects.
    private static int _trackedSceneHandle = int.MinValue;
    private static readonly HashSet<int> ObservedLivingWitches = new HashSet<int>();

    [HarmonyPatch(typeof(EnemySpawner), "GetEnemyPrefab")]
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void OrdinarySpawnerPostfix(EnemySpawner __instance, ref GameObject __result)
        => SuppressSecondWitch(__instance, ref __result);

    [HarmonyPatch(typeof(ArenaEnemySpawner), "GetEnemyPrefab")]
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void ArenaSpawnerPostfix(ArenaEnemySpawner __instance, ref GameObject __result)
        => SuppressSecondWitch(__instance, ref __result);

    private static void SuppressSecondWitch(MonoBehaviour spawner, ref GameObject result)
    {
        // This hook runs only when an actual Witch prefab is selected, not
        // on every enemy spawn and NEVER every frame.
        if (result == null || !IsWitch(result)) return;

        Scene scene = SceneManager.GetActiveScene();
        if (!scene.IsValid() || !scene.isLoaded) return;

        int liveCount = CountLiveSpawnedWitches(scene.handle);
        float now = Time.unscaledTime;
        bool reserved = _reservedSceneHandle == scene.handle
            && now >= _reservedAt && now - _reservedAt < PendingSelectionSeconds;

        if (liveCount == 0 && !reserved)
        {
            // First Witch is allowed at the unchanged weight (1.0).
            // Do not let a second prefab roll sneak through while a spawn
            // batch is still scheduling the first Witch's Instantiate call.
            _reservedSceneHandle = scene.handle;
            _reservedAt = now;
            if (now >= _nextLogTime)
            {
                _nextLogTime = now + 3f;
                GameplayGuards.Log?.LogInfo("[GAMEPLAY-GUARDS] first Witch selected: reservation placed for floor '" + scene.name + "'");
            }
            return;
        }

        GameObject alternative = PickAuthoredNonWitchPrefab(spawner);
        // If a table contains only Witch, skip that roll; never force a
        // second Witch or destroy already spawned instances.
        result = alternative;
        if (now >= _nextLogTime)
        {
            _nextLogTime = now + 3f;
            GameplayGuards.Log?.LogInfo("[GAMEPLAY-GUARDS] blocked additional Witch while living Witch active in floor"
                + " '" + scene.name + "' (living=" + liveCount + ", pending=" + reserved + "); replacement="
                + (alternative != null ? alternative.name : "<none>"));
        }
    }

    private static bool IsWitch(GameObject prefab) =>
        prefab != null && prefab.name.StartsWith(WitchPrefabName, StringComparison.OrdinalIgnoreCase);

    private static GameObject PickAuthoredNonWitchPrefab(MonoBehaviour spawner)
    {
        if (spawner == null) return null;
        try
        {
            // The installed CustomEnemies injector keeps all original vanilla
            // entries (including their duplicates), then appends the Witch.
            FieldInfo field = AccessTools.Field(spawner.GetType(), "enemyData");
            EnemyData[] data = field?.GetValue(spawner) as EnemyData[];
            if (data == null) return null;
            List<EnemyData> original = new List<EnemyData>();
            foreach (EnemyData entry in data)
            {
                if (entry == null || entry.prefabVariants == null || entry.prefabVariants.Length == 0) continue;
                bool hasAllowed = false;
                foreach (GameObject variant in entry.prefabVariants)
                    if (variant != null && !IsWitch(variant)) { hasAllowed = true; break; }
                if (hasAllowed) original.Add(entry);
            }
            if (original.Count == 0) return null;
            EnemyData chosen = original[UnityEngine.Random.Range(0, original.Count)];
            List<GameObject> variants = new List<GameObject>();
            foreach (GameObject variant in chosen.prefabVariants)
                if (variant != null && !IsWitch(variant)) variants.Add(variant);
            return variants.Count > 0 ? variants[UnityEngine.Random.Range(0, variants.Count)] : null;
        }
        catch (Exception ex)
        {
            GameplayGuards.Log?.LogWarning("[GAMEPLAY-GUARDS] fallback spawn selection failed safely: " + ex.Message);
            return null;
        }
    }

    private static int CountLiveSpawnedWitches(int activeFloorSceneHandle)
    {
        if (_trackedSceneHandle != activeFloorSceneHandle)
        {
            _trackedSceneHandle = activeFloorSceneHandle;
            ObservedLivingWitches.Clear();
        }
        int count = 0;
        HashSet<int> stillAlive = new HashSet<int>();
        // Some grab/capture phases temporarily disable enemy renderers or the
        // base AI. Include inactive instances so another Witch cannot spawn
        // while the existing one is capturing the player.
        SpinningEnemyAI[] loaded = Object.FindObjectsByType<SpinningEnemyAI>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (SpinningEnemyAI ai in loaded)
        {
            if (ai == null || ai.IsDead) continue;
            Transform root = FindWitchParent(ai.transform);
            if (root == null || !root.gameObject.scene.isLoaded
                || root.gameObject.scene.handle != activeFloorSceneHandle)
                continue;

            // IMPORTANT: PNC creates dozens of inactive Witch template clones.
            // They must not be counted until one really starts pursuing or
            // capturing. After that, remember its instance ID until DEATH, so
            // losing sight/target cannot admit another Witch to this floor.
            if (CurrentHealth != null)
            {
                object value = CurrentHealth.GetValue(ai);
                if (value is int hp && hp <= 0) continue;
            }
            int token = ai.GetInstanceID();
            bool observed = ObservedLivingWitches.Contains(token);
            if (!observed && !IsRuntimeWitchInstance(root, ai)) continue;
            ObservedLivingWitches.Add(token);
            stillAlive.Add(token);
            count++;
        }
        // Destroyed/dead witches cannot reserve slots on later selections.
        ObservedLivingWitches.IntersectWith(stillAlive);
        return count;
    }

    private static bool IsRuntimeWitchInstance(Transform witchRoot, SpinningEnemyAI ai)
    {
        if (witchRoot == null || ai == null) return false;
        // The controller is attached to the Witch root, not its pooled
        // template. An instantiated-but-dormant clone has no player target.
        Component controller = witchRoot.GetComponent("CharmWitchController");
        if (controller == null) return false;

        Type type = controller.GetType();
        FieldInfo captureField = AccessTools.Field(type, "_capturing");
        // During a grab, Unity may temporarily disable gameplay components.
        // Keep the existing Witch reserved until the legitimate capture ends.
        if (captureField != null && captureField.GetValue(controller) is bool capturing && capturing)
            return true;

        if (!witchRoot.gameObject.activeInHierarchy || !ai.isActiveAndEnabled)
            return false;
        Behaviour behaviour = controller as Behaviour;
        if (behaviour == null || !behaviour.isActiveAndEnabled)
            return false;
        FieldInfo playerField = AccessTools.Field(type, "_player");
        Transform target = playerField?.GetValue(controller) as Transform;
        return target != null && target.gameObject.activeInHierarchy;
    }

    private static Transform FindWitchParent(Transform child)
    {
        for (Transform t = child; t != null; t = t.parent)
            if (IsWitch(t.gameObject)) return t;
        return null;
    }
}
