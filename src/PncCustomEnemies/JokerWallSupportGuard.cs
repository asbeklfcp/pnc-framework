using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace PncCustomEnemies;

// SIGNED PUBLIC GAMEPLAY COMPATIBILITY. Recognized structural wall names were verified against
// 4,987 records in PNC-Asset-Vault/World_Objects_UI/Architecture/
// Full_Source_Indexes/Walls.json. All source assets stay on disk; there is
// NO runtime asset scanning, disk access, or per-frame wall detection.
//
// Original TryFindWall performs 64 allocating RaycastAll queries. This
// Harmony PREFIX replaces that search, rather than repeating it in a postfix.
// Bounded nonallocating physics scans occur only when Joker requests a place
// (initially / while roaming). They do not run in Update/LateUpdate.
internal static class JokerWallSupportGuard
{
    private const float SearchDistance = 18f;
    private const float MinimumHitDistance = 3f;
    private const float MaxNormalY = 0.17f;
    private const float MinimumNormalDot = 0.965f;
    private const float MaximumSurfaceStep = 0.16f;
    private const float FrontClearance = 1.20f;
    private const float PortraitHalfWidth = 1.08f;
    private const float PortraitHalfHeight = 1.09f;

    private const int FirstPassAngles = 32;
    private const int FallbackAngles = 16;
    private const int CandidateBudget = 2;
    private const int MaxWallValidationAttempts = 7;
    // Hard synchronous work budget; no level search should monopolize a frame.
    private const double MaxScanMilliseconds = 2.5;
    // When the physics result array is saturated, reject that ray rather
    // than risk missing a nearer bookcase and allowing a hidden wall.
    private static readonly RaycastHit[] CenterHits = new RaycastHit[64];
    private static readonly RaycastHit[] SurfaceHits = new RaycastHit[32];
    private static readonly RaycastHit[] VisibilityHits = new RaycastHit[32];
    private static readonly Collider[] FrontOverlaps = new Collider[64];

    private static readonly Vector2[] SurfaceOffsets = {
        new Vector2(-1.02f, 0f), new Vector2(1.02f, 0f),
        new Vector2(0f, -1.02f), new Vector2(0f, 1.02f),
        new Vector2(-0.92f, -0.92f), new Vector2(-0.92f, 0.92f),
        new Vector2(0.92f, -0.92f), new Vector2(0.92f, 0.92f)
    };

    // Cache object-name classification, NOT objects or scene positions.
    // Static names repeat across dozens of modular room tiles; bounded to
    // keep the cache from growing indefinitely across room generations.
    private static readonly Dictionary<string, bool> WallNameCache =
        new Dictionary<string, bool>(StringComparer.Ordinal);
    private const int MaxNameCacheEntries = 4096;
    private static FieldInfo _separationField;

    private static float _nextDiagnosticTime;

    internal static bool Install(Harmony harmony, ManualLogSource log)
    {
        Type registry = AccessTools.TypeByName("WallPictureTraps.WallPictureTrapRegistry");
        MethodInfo target = registry != null ? AccessTools.Method(registry, "TryFindWall") : null;
        if (target == null)
        {
            log.LogError("[GAMEPLAY-GUARDS] Joker asset-aware selector target missing; optimized hook NOT installed");
            return false;
        }
        ParameterInfo[] p = target.GetParameters();
        if (target.ReturnType != typeof(bool) || p.Length != 4
            || p[0].ParameterType != typeof(Transform)
            || p[1].ParameterType != typeof(List<Vector3>)
            || p[3].ParameterType != typeof(RaycastHit).MakeByRefType())
        {
            log.LogError("[GAMEPLAY-GUARDS] Joker TryFindWall signature incompatible; optimized hook NOT installed");
            return false;
        }
        // Method is private; passing the package manifest as object avoids
        // referencing a separate third-party assembly. Do not combine
        // object[] __args with ref __3: it previously produced (0,0,0) spawns.
        MethodInfo prefix = AccessTools.Method(typeof(JokerWallSupportGuard), nameof(TryFindWallPrefix));
        harmony.Patch(target, prefix: new HarmonyMethod(prefix) { priority = Priority.First });
        log.LogInfo("[GAMEPLAY-GUARDS] Joker OPTIMIZED asset-aware wall selector installed: 32+16 bounded angles, nonalloc physics, named architecture, occlusion checks");
        return true;
    }

    // Setting __result and the OUT collision hit, then returning false skips
    // the original costly 64-RayCastAll implementation. 100% reversible:
    // existing WallPictureTrap.dll and all gameplay/capture logic unchanged.
    private static bool TryFindWallPrefix(
        Transform __0, List<Vector3> __1, object __2,
        ref RaycastHit __3, ref bool __result)
    {
        long started = Stopwatch.GetTimestamp();
        int centerRays = 0, surfaceRays = 0, visibilityRays = 0, overlaps = 0;
        int structuralCandidates = 0, rejectedOrBlocked = 0, separated = 0;
        int wallValidations = 0;
        bool budgetStopped = false;
        try
        {
            __3 = default;
            __result = false;
            Transform player = __0;
            if (player == null) return false;

            float separation = GetSeparation(__2);
            float bestScore = float.PositiveInfinity;
            RaycastHit best = default;
            int valid = 0;
            Camera camera = Camera.main;
            int randomRotation = (Time.frameCount & 31);
            Vector3 playerPos = player.position;

            // Second height/angle set is a FALLBACK only. The first success
            // usually stops early once up to two good walls have been assessed.
            for (int pass = 0; pass < 2 && valid == 0 && !budgetStopped; pass++)
            {
                int directions = pass == 0 ? FirstPassAngles : FallbackAngles;
                float eyeHeight = pass == 0 ? 2.08f : 1.85f;
                Vector3 origin = playerPos + Vector3.up * eyeHeight;
                for (int i = 0; i < directions; i++)
                {
                    if (wallValidations >= MaxWallValidationAttempts
                        || 1000.0 * (Stopwatch.GetTimestamp() - started) / Stopwatch.Frequency > MaxScanMilliseconds)
                    {
                        budgetStopped = true;
                        break;
                    }
                    // Coprime stride covers the ring without repeating rays.
                    int bin = (randomRotation + i * (pass == 0 ? 13 : 7)) % directions;
                    float angle = (bin + (pass == 0 ? 0f : 0.5f)) * (360f / directions);
                    Vector3 direction = Quaternion.Euler(0f, angle, 0f) * Vector3.forward;
                    centerRays++;
                    int count = Physics.RaycastNonAlloc(origin, direction, CenterHits,
                        SearchDistance, ~0, QueryTriggerInteraction.Ignore);
                    if (count == CenterHits.Length) continue; // unknown nearest collider, fail closed

                    int nearest = NearestSolidHit(CenterHits, count, player);
                    if (nearest < 0) continue;
                    RaycastHit hit = CenterHits[nearest];
                    // NEVER tunnel through a prop to a wall behind it.
                    if (!IsCentralWall(hit, player)) continue;
                    structuralCandidates++;
                    if (!IsSeparated(hit.point, __1, separation)) { separated++; continue; }
                    wallValidations++;
                    if (!SupportsPortraitAndAccess(hit, player, eyeHeight,
                        ref surfaceRays, ref visibilityRays, ref overlaps))
                    {
                        rejectedOrBlocked++;
                        continue;
                    }

                    valid++;
                    float score = Mathf.Abs(hit.distance - 9f);
                    if (camera != null && camera.isActiveAndEnabled)
                    {
                        Vector3 v = camera.WorldToViewportPoint(hit.point);
                        if (v.z > 0f && v.x > 0.03f && v.x < 0.97f && v.y > 0.03f && v.y < 0.97f)
                            score += 4f;
                    }
                    if (score < bestScore) { best = hit; bestScore = score; }
                    if (valid >= CandidateBudget) break;
                }
            }

            if (valid > 0)
            {
                __3 = best;
                __result = true;
            }
            return false; // always bypass original allocation-heavy search
        }
        catch (Exception ex)
        {
            // A placement may fail, but must never spawn Joker at world origin
            // or change the game's capture, animation or death behavior.
            __3 = default;
            __result = false;
            if (Time.unscaledTime >= _nextDiagnosticTime)
            {
                _nextDiagnosticTime = Time.unscaledTime + 15f;
                GameplayGuards.Log?.LogWarning("[GAMEPLAY-GUARDS] Joker optimized selector skipped due to error: " + ex);
            }
            return false;
        }
        finally
        {
            double elapsedMs = 1000.0 * (Stopwatch.GetTimestamp() - started) / Stopwatch.Frequency;
            if (Time.unscaledTime >= _nextDiagnosticTime)
            {
                _nextDiagnosticTime = Time.unscaledTime + 5f;
                GameplayGuards.Log?.LogInfo("[GAMEPLAY-GUARDS] Joker selector PERF result=" + (__result ? "PLACED" : "SKIPPED")
                    + " centerRays=" + centerRays + " surfaceRays=" + surfaceRays
                    + " visibilityRays=" + visibilityRays + " overlapChecks=" + overlaps
                    + " structuralCandidates=" + structuralCandidates
                    + " validations=" + wallValidations + " budgetStop=" + budgetStopped
                    + " rejected=" + rejectedOrBlocked + " tooClose=" + separated
                    + " elapsedMs=" + elapsedMs.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)
                    + (__result ? " selectedWall=" + __3.transform.name + " selectedPoint=" + __3.point : ""));
            }
        }
    }

    // Use nearest non-player/non-Joker solid. No Array.Sort, no RaycastAll,
    // and no per-ray collections. Ignore ONLY own colliders; props are blockers.
    private static int NearestSolidHit(RaycastHit[] hits, int count, Transform player)
    {
        int nearestIndex = -1;
        float distance = float.PositiveInfinity;
        for (int i = 0; i < count; i++)
        {
            RaycastHit hit = hits[i];
            if (hit.collider == null || hit.collider.isTrigger
                || IsPlayerOrJoker(hit.transform, player)) continue;
            if (hit.distance < distance) { distance = hit.distance; nearestIndex = i; }
        }
        return nearestIndex;
    }

    private static bool IsPlayerOrJoker(Transform transform, Transform player)
    {
        if (transform == null) return true;
        if (player != null && (transform == player || transform.IsChildOf(player))) return true;
        for (Transform t = transform; t != null; t = t.parent)
        {
            string name = t.name;
            if (name.StartsWith("WallPictureTrap_", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("WallPictureTrapGrabOverlay", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static float GetSeparation(object manifest)
    {
        if (manifest == null) return 10f;
        try
        {
            if (_separationField == null)
                _separationField = AccessTools.Field(manifest.GetType(), "minimumSpawnSeparation");
            float value = _separationField == null ? 10f : Convert.ToSingle(_separationField.GetValue(manifest));
            return float.IsNaN(value) || float.IsInfinity(value) ? 10f : Mathf.Max(1f, value);
        }
        catch { return 10f; }
    }

    private static bool IsSeparated(Vector3 point, List<Vector3> occupied, float minimumDistance)
    {
        if (occupied == null) return true;
        float minSq = minimumDistance * minimumDistance;
        for (int i = 0; i < occupied.Count; i++)
            if ((occupied[i] - point).sqrMagnitude < minSq) return false;
        return true;
    }

    private static bool IsCentralWall(RaycastHit hit, Transform player)
    {
        return hit.collider != null && !hit.collider.isTrigger
            && hit.distance >= MinimumHitDistance
            && Mathf.Abs(hit.normal.y) <= MaxNormalY
            && !IsPlayerOrJoker(hit.transform, player)
            && IsArchitecturalWall(hit.transform);
    }

    private static bool SupportsPortraitAndAccess(
        RaycastHit center, Transform player, float eyeHeight,
        ref int surfaceRays, ref int visibilityRays, ref int overlaps)
    {
        Vector3 normal = center.normal.normalized;
        Vector3 right = Vector3.Cross(Vector3.up, normal);
        if (right.sqrMagnitude < 0.75f) return false;
        right.Normalize();

        // A broad wall must support all eight points around the full 2.16x2.17m
        // portrait (not just a center ray on a doorframe or pillar).
        for (int i = 0; i < SurfaceOffsets.Length; i++)
        {
            Vector2 offset = SurfaceOffsets[i];
            Vector3 surface = center.point + right * offset.x + Vector3.up * offset.y;
            Vector3 origin = surface + normal * 0.65f;
            surfaceRays++;
            int count = Physics.RaycastNonAlloc(origin, -normal, SurfaceHits,
                1.30f, ~0, QueryTriggerInteraction.Ignore);
            if (count == SurfaceHits.Length) return false;
            int nearest = NearestSolidHit(SurfaceHits, count, player);
            if (nearest < 0) return false;
            RaycastHit probe = SurfaceHits[nearest];

            if (!IsArchitecturalWall(probe.transform)
                || Vector3.Dot(probe.normal.normalized, normal) < MinimumNormalDot
                || Mathf.Abs(Vector3.Dot(probe.point - surface, normal)) > MaximumSurfaceStep)
                return false;
        }

        // One volume test replaces 8 allocating per-edge outward raycasts.
        // The wall's OWN collider is permitted; furniture, loose props,
        // doorframes and perpendicular architectural walls are blockers.
        overlaps++;
        Vector3 boxCenter = center.point + normal * (FrontClearance * 0.5f + 0.07f);
        Quaternion rotation = Quaternion.LookRotation(normal, Vector3.up);
        int overlapCount = Physics.OverlapBoxNonAlloc(boxCenter,
            new Vector3(PortraitHalfWidth, PortraitHalfHeight, FrontClearance * 0.5f),
            FrontOverlaps, rotation, ~0, QueryTriggerInteraction.Ignore);
        if (overlapCount == FrontOverlaps.Length) return false; // too crowded: fail closed
        for (int i = 0; i < overlapCount; i++)
        {
            Collider c = FrontOverlaps[i];
            if (c == null || c == center.collider || c.isTrigger) continue;
            if (IsPlayerOrJoker(c.transform, player)) continue;
            // Separate wall panels may overlap at a joint; still require a clear
            // approach, so only the selected wall collider is exempt.
            return false;
        }

        // A shelf farther from the wall can hide the portrait even if the
        // 1.2m approach volume is clear. Test visibility from player eye to
        // center AND both sides. Reject any intervening prop/architecture.
        Vector3 viewer = player.position + Vector3.up * eyeHeight;
        for (int i = -1; i <= 1; i++)
        {
            Vector3 target = center.point + right * (i * 0.78f);
            Vector3 direction = target - viewer;
            float distance = direction.magnitude;
            if (distance < 0.01f) return false;
            visibilityRays++;
            int n = Physics.RaycastNonAlloc(viewer, direction / distance, VisibilityHits,
                distance + 0.2f, ~0, QueryTriggerInteraction.Ignore);
            if (n == VisibilityHits.Length) return false;
            int nearest = NearestSolidHit(VisibilityHits, n, player);
            if (nearest < 0) return false;
            RaycastHit first = VisibilityHits[nearest];
            if (!IsArchitecturalWall(first.transform)
                || Vector3.Dot(first.normal.normalized, normal) < MinimumNormalDot
                || Mathf.Abs(Vector3.Dot(first.point - target, normal)) > 0.25f)
                return false;
        }
        return true;
    }

    internal static bool IsKnownStructuralWallName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (WallNameCache.TryGetValue(name, out bool approved)) return approved;
        approved = EvaluateStructuralName(name);
        if (WallNameCache.Count >= MaxNameCacheEntries) WallNameCache.Clear();
        WallNameCache[name] = approved;
        return approved;
    }

    // Pure name classifier grounded in the exported Architecture/Walls index.
    // It intentionally rejects wall pipes, WallPillars, beams, shelves, etc.
    private static bool EvaluateStructuralName(string name)
    {
        string n = name.Trim().ToLowerInvariant();
        if (n.EndsWith("(clone)", StringComparison.Ordinal))
            n = n.Substring(0, n.Length - 7).TrimEnd();
        int numbered = n.LastIndexOf(" (", StringComparison.Ordinal);
        if (numbered > 0 && n.EndsWith(")", StringComparison.Ordinal))
        {
            bool digits = true;
            for (int i = numbered + 2; i < n.Length - 1; i++)
                if (!char.IsDigit(n[i])) { digits = false; break; }
            if (digits) n = n.Substring(0, numbered);
        }
        if (n.StartsWith("backrooms wall block trim", StringComparison.Ordinal)) return true;
        if (Has(n, "pillar") || Has(n, "post") || Has(n, "pipe") || Has(n, "beam")
            || Has(n, "shelf") || Has(n, "book") || Has(n, "crate")
            || Has(n, "doorframe") || Has(n, "fence") || Has(n, "torch")
            || Has(n, "socket") || Has(n, "banner") || Has(n, "chain")
            || Has(n, "floor") || Has(n, "ceiling") || Has(n, "prop")
            || Has(n, "arch") || Has(n, "window") || Has(n, "plank")
            || Has(n, "shield") || Has(n, "trim")) return false;

        return n == "wall" || n == "walls" || n == "mansion wall"
            || n.StartsWith("mansion wall ", StringComparison.Ordinal)
            || n == "fading wall" || n == "door wall"
            || n == "retractable wall" || n == "side of wall"
            || n == "stair way wall" || n == "ornate wall 1"
            || n.StartsWith("ornate wall ", StringComparison.Ordinal)
            || n.StartsWith("dungeon wall section", StringComparison.Ordinal)
            || n.StartsWith("caverns wall section", StringComparison.Ordinal)
            || n.StartsWith("backrooms wall", StringComparison.Ordinal);
    }

    private static bool Has(string value, string part) => value.Contains(part);

    private static bool IsArchitecturalWall(Transform t)
    {
        if (t == null) return false;
        if (IsKnownStructuralWallName(t.name)) return true;
        string leaf = t.name;
        // Even if grouped under a real wall, props must never inherit its ID.
        if (ContainsIgnoreCase(leaf, "shelf") || ContainsIgnoreCase(leaf, "book")
            || ContainsIgnoreCase(leaf, "crate") || ContainsIgnoreCase(leaf, "pipe")
            || ContainsIgnoreCase(leaf, "pillar") || ContainsIgnoreCase(leaf, "beam")
            || ContainsIgnoreCase(leaf, "post") || ContainsIgnoreCase(leaf, "doorframe")
            || ContainsIgnoreCase(leaf, "prop") || ContainsIgnoreCase(leaf, "fence")
            || ContainsIgnoreCase(leaf, "torch") || ContainsIgnoreCase(leaf, "floor")
            || ContainsIgnoreCase(leaf, "plank") || ContainsIgnoreCase(leaf, "chair")
            || ContainsIgnoreCase(leaf, "table") || ContainsIgnoreCase(leaf, "dresser")
            || ContainsIgnoreCase(leaf, "cabinet") || ContainsIgnoreCase(leaf, "barrel")
            || ContainsIgnoreCase(leaf, "chain") || ContainsIgnoreCase(leaf, "socket")
            || ContainsIgnoreCase(leaf, "banner") || ContainsIgnoreCase(leaf, "arch")
            || ContainsIgnoreCase(leaf, "window") || ContainsIgnoreCase(leaf, "glass")
            || ContainsIgnoreCase(leaf, "light") || ContainsIgnoreCase(leaf, "item")
            || ContainsIgnoreCase(leaf, "interactive") || ContainsIgnoreCase(leaf, "loot")
            || ContainsIgnoreCase(leaf, "rail") || ContainsIgnoreCase(leaf, "statue")
            || ContainsIgnoreCase(leaf, "poster") || ContainsIgnoreCase(leaf, "trim")
            || ContainsIgnoreCase(leaf, "door")) return false;
        return t.parent != null && IsKnownStructuralWallName(t.parent.name);
    }

    private static bool ContainsIgnoreCase(string text, string needle)
        => !string.IsNullOrEmpty(text)
           && text.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
}
