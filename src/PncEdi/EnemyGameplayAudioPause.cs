using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
namespace PncEdi;
// Owned native actor sources only. No AudioListener, mixer, menu or world-wide audio mutation.
[HarmonyPatch]
internal static class EnemyGameplayAudioPause {
    sealed class Playback:ICapturePlayback {
        internal AudioSource Source;
        public bool Valid=>Source!=null && Source.isActiveAndEnabled;
        public bool Playing=>Source!=null && Source.isPlaying;
        public object Clip=>Source!=null?Source.clip:null;
        public void Pause()=>Source.Pause();
        public void Resume()=>Source.UnPause();
        public void Stop(){if(Source!=null)Source.Stop();}
    }
    sealed class Entry {internal MonoBehaviour Owner;internal Playback Playback;internal CapturePlaybackLease Lease=new CapturePlaybackLease();}
    static readonly Dictionary<int,Entry> Sources=new Dictionary<int,Entry>();
    static readonly List<int> Removed=new List<int>();
    static int scene=int.MinValue;
    static IEnumerable<MethodBase> TargetMethods() {
        foreach(var type in EnemyAiTypes.All){var method=AccessTools.Method(type,"Start",Type.EmptyTypes);if(method!=null)yield return method;}
    }
    [HarmonyPostfix,HarmonyPriority(Priority.Last)]
    static void AfterStart(MonoBehaviour __instance)=>Register(__instance);
    static void Register(MonoBehaviour owner) {
        if(owner==null)return;
        foreach(var source in owner.GetComponentsInChildren<AudioSource>(true)) {
            if(source==null)continue;
            int id=source.GetInstanceID();
            if(Sources.TryGetValue(id,out var previous)) {
                if(previous.Owner==owner && previous.Playback.Source==source)continue;
                previous.Lease.Clear();
            }
            Sources[id]=new Entry{Owner=owner,Playback=new Playback{Source=source}};
        }
    }
    internal static void Tick() {
        int currentScene=SceneManager.GetActiveScene().handle;
        if(currentScene!=scene) {
            foreach(var entry in Sources.Values)entry.Lease.Clear();Sources.Clear();scene=currentScene;
            foreach(var type in EnemyAiTypes.All)
                foreach(var actor in UnityEngine.Object.FindObjectsByType(type,FindObjectsSortMode.None))Register(actor as MonoBehaviour);
        }
        bool held=PauseHooks.SceneClockHeld || Time.timeScale<=.0001f;
        var grab=GrabScreen.Instance;
        var captureSource=grab!=null?OptionalMembers.Read(grab,"audioSource") as AudioSource:null;
        Removed.Clear();
        foreach(var pair in Sources) {
            var entry=pair.Value;var owner=entry.Owner;var source=entry.Playback.Source;
            if(owner==null || source==null){entry.Lease.Clear();Removed.Add(pair.Key);continue;}
            // Existing 3.0.20 capture lease retains sole ownership of GrabScreen audio.
            if(source==captureSource){entry.Lease.Clear();continue;}
            bool alive=owner.gameObject.activeInHierarchy;
            if(OptionalMembers.Read(owner,"isDead") is bool dead && dead)alive=false;
            entry.Lease.Tick(alive?(object)owner:null,entry.Playback,held);
        }
        foreach(int id in Removed)Sources.Remove(id);
    }
}
