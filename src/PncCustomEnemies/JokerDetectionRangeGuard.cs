using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Logging;
using HarmonyLib;

namespace PncCustomEnemies;

// SIGNED PUBLIC COMPATIBILITY: Joker detects players at 8.5m instead of 7.5m, but maintains
// the EXACT original proximity/acceleration ramp at any pre-existing distance.
// Joker's existing UpdatePull reads Manifest.pullRadius twice: first to check
// range, second to compute proximity. Keep the FIRST (range) dynamic and make
// only the SECOND (force normalization) use the original 7.5m radius.
// This never alters pullStrength, maxPullSpeed, cooldown or captureDistance.
internal static class JokerDetectionRangeGuard
{
    private const float OriginalForceCurveRadius = 7.5f;

    internal static bool Install(Harmony harmony, ManualLogSource log)
    {
        Type type = AccessTools.TypeByName("WallPictureTraps.WallPictureTrap");
        MethodInfo method = type == null ? null : AccessTools.Method(type, "UpdatePull");
        if (method == null || method.ReturnType != typeof(void) || method.GetParameters().Length != 0)
        {
            log.LogError("[GAMEPLAY-GUARDS] Joker UpdatePull signature changed; force-preserving range patch NOT installed");
            return false;
        }
        // Check the installed package's actual Unity gameplay method, not the
        // possibly older extracted/repository code. A mismatch must fail closed.
        var il = method.GetMethodBody();
        if (il == null)
        {
            log.LogError("[GAMEPLAY-GUARDS] Joker UpdatePull has no IL body; detection patch NOT installed");
            return false;
        }
        try
        {
            MethodInfo patch = AccessTools.Method(typeof(JokerDetectionRangeGuard), nameof(PreserveOriginalForceCurve));
            harmony.Patch(method, transpiler: new HarmonyMethod(patch));
            log.LogInfo("[GAMEPLAY-GUARDS] Joker 8.5m detection configured; force ramp uses original 7.5m radius, pullStrength unchanged");
            return true;
        }
        catch (Exception ex)
        {
            log.LogError("[GAMEPLAY-GUARDS] Joker detection patch NOT installed: " + ex);
            return false;
        }
    }

    private static IEnumerable<CodeInstruction> PreserveOriginalForceCurve(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> list = new List<CodeInstruction>(instructions);
        List<int> radiusReads = new List<int>(2);
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].opcode != OpCodes.Ldfld) continue;
            FieldInfo field = list[i].operand as FieldInfo;
            if (field?.Name == "pullRadius"
                && field.DeclaringType?.FullName == "WallPictureTraps.WallPictureTrapManifest")
                radiusReads.Add(i);
        }
        if (radiusReads.Count != 2 || radiusReads[1] + 1 >= list.Count
            || list[radiusReads[1] + 1].opcode != OpCodes.Div)
        {
            // Never guess which instruction to replace if upstream Joker DLL
            // changes; leave its original behavior rather than break IL.
            GameplayGuards.Log?.LogError("[GAMEPLAY-GUARDS] Joker force preservation IL pattern differs; no changes applied (radius reads=" + radiusReads.Count + ")");
            return list;
        }

        // Before the second ldfld, the stack contains the manifest object.
        // Pop it then push 7.5f instead of reading the now-8.5m detection radius.
        // The original div and rest of acceleration/maxSpeed pipeline are intact.
        int index = radiusReads[1];
        CodeInstruction old = list[index];
        // Clone the instruction first so Harmony preserves existing branch
        // labels/exception metadata without linking mscorlib Label in our
        // netstandard2.1 private addon.
        CodeInstruction pop = new CodeInstruction(old);
        pop.opcode = OpCodes.Pop;
        pop.operand = null;
        list[index] = pop;
        list.Insert(index + 1, new CodeInstruction(OpCodes.Ldc_R4, OriginalForceCurveRadius));
        GameplayGuards.Log?.LogInfo("[GAMEPLAY-GUARDS] Joker force curve preserved at 7.5m while extending detection to 8.5m");
        return list;
    }
}
