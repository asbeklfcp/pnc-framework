using HarmonyLib;
using UnityEngine;
namespace PncEdi;
// CMF keeps its last controller velocity when movement is disabled for a capture.
// Gate locomotion audio at its producer, without muting scene/music/enemy sources.
[HarmonyPatch]
internal static class CaptureFootstepHooks {
    [HarmonyPatch(typeof(CMF.AudioControl),"FootStepUpdate")]
    [HarmonyPrefix]
    internal static bool FootstepPrefix(CMF.AudioControl __instance,AudioSource ___audioSource,
        ref float ___currentFootstepDistance,ref float ___currentFootStepValue) {
        if(__instance.GetComponentInParent<PlayerStats>()==null || (GrabScreen.Instance==null || !GrabEndHelper.IsGrabSessionLive(GrabScreen.Instance)))return true;
        ___currentFootstepDistance=0;___currentFootStepValue=0;
        if(___audioSource!=null && ___audioSource.isPlaying) {
            ___audioSource.Stop();Plugin.DBG("CAPTURE-FOOTSTEPS","stopped player locomotion audio during capture");
        }
        return false;
    }
}
