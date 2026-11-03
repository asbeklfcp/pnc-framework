using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
namespace PncEdi;
// AudioSource.Pause reports isPlaying=false. Native idle-loop starters otherwise
// call Play repeatedly during a clock hold. Leave stopping/death cleanup native.
[HarmonyPatch]
internal static class EnemyIdleLoopPauseGuard {
    static IEnumerable<MethodBase> TargetMethods() {
        foreach(var type in EnemyAiTypes.All) {
            var method=AccessTools.Method(type,"StartIdleMovingSound",Type.EmptyTypes);
            if(method!=null)yield return method;
        }
    }
    [HarmonyPrefix,HarmonyPriority(Priority.First)]
    internal static bool BeforeStart(MonoBehaviour __instance) {
        if(!(PauseHooks.SceneClockHeld || Time.timeScale<=.0001f))return true;
        // Only a native actor's explicitly owned idle-loop source; not global audio.
        return __instance==null || !(OptionalMembers.Read(__instance,"loopingAudioSource") is AudioSource);
    }
}
