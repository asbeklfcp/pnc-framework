using HarmonyLib;
using UnityEngine;
namespace PncEdi;
// Full native GrabScreen captures share one owned sound source. UI, music,
// world and clinging-grapple sources are not discovered or muted here.
[HarmonyPatch]
internal static class CaptureAudioPause {
    sealed class Playback:ICapturePlayback {
        internal readonly AudioSource Source;
        internal Playback(AudioSource source){Source=source;}
        public bool Valid=>Source!=null && Source.isActiveAndEnabled;
        public bool Playing=>Source!=null && Source.isPlaying;
        public object Clip=>Source!=null?Source.clip:null;
        public void Pause()=>Source.Pause();public void Resume()=>Source.UnPause();public void Stop(){if(Source!=null)Source.Stop();}
    }
    static Playback playback;
    static readonly CapturePlaybackLease Lease=new CapturePlaybackLease(message=>Plugin.DBG("CAPTURE-AUDIO",message));
    internal static void Tick() {
        var screen=GrabScreen.Instance;
        if(screen==null || !screen.IsGrabbed){Clear();return;}
        var source=OptionalMembers.Read(screen,"audioSource") as AudioSource;
        if(source==null){Clear();return;}
        if(playback==null || playback.Source!=source)playback=new Playback(source);
        object owner=(object)screen.GrabbingEnemy ?? OptionalMembers.Read(screen,"hiddenEnemyToRestore") ?? screen;
        Lease.Tick(owner,playback,PauseHooks.SceneClockHeld || Time.timeScale<=.0001f);
    }
    internal static void Clear(){Lease.Clear();playback=null;}
    [HarmonyPatch(typeof(GrabScreen),"EndGrab"),HarmonyPostfix,HarmonyPriority(Priority.Last)]
    static void AfterEnd()=>Clear();
}
