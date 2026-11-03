using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Video;
using PncCustomEnemies.Api;
using PncEdi;

namespace PncCustomEnemies;

internal static class PackageLifecycle
{
    private sealed class State
    {
        internal CleanupScope Scope;
        internal string Status;
        internal readonly Dictionary<int, GameObject> Objects = new Dictionary<int, GameObject>();
        internal int VerifyFrame = -1;
        internal bool HadErrors;
        internal GameObject EndingCapture;
        internal float VerifyActiveSeconds;
    }
    private static readonly Dictionary<string, State> States = new Dictionary<string, State>(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, HashSet<string>> Dependents = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

    internal static void Bind(string id, ConfigEntry<bool> entry)
    {
        if (States.ContainsKey(id)) return;
        var state = new State { Scope=new CleanupScope(entry.Value), Status=entry.Value ? "Active" : "Disabled" };
        States.Add(id,state);
        entry.SettingChanged += (_,__) =>
        {
            if (!entry.Value) Disable(id);
            else if (!state.Scope.Active) state.Status="Restart required to enable";
        };
    }
    internal static bool Active(string id) => States.TryGetValue(id,out var state) && state.Scope.Active;
    internal static bool IsTeardown(UnityEngine.Object target)
    {
        GameObject obj=target as GameObject;
        if(obj==null && target is Component component) obj=component.gameObject;
        if(obj==null) return false;
        var owner=obj.GetComponentInParent<PackageOwnedObject>(true);
        return owner!=null && !string.IsNullOrEmpty(owner.OwnerId) && States.TryGetValue(owner.OwnerId,out var state) && !state.Scope.Active;
    }
    internal static string Status(string id) => States.TryGetValue(id,out var state) ? state.Status : "Not registered";
    internal static void Depend(string consumer,string provider)
    {
        if (string.Equals(consumer,provider,StringComparison.OrdinalIgnoreCase)) return;
        if (!Dependents.TryGetValue(provider,out var set)) Dependents[provider]=set=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        set.Add(consumer);
    }
    internal static void Cleanup(string id,string key,Action release)
    {
        if (!States.TryGetValue(id,out var state) || !state.Scope.Track("hook:"+key,20,release))
            release(); // late resource registration cannot resurrect disabled content
    }
    internal static void Own(string id,GameObject obj)
    {
        if (obj == null) return;
        if (!States.TryGetValue(id,out var state) || !state.Scope.Active)
        {
            StopObject(obj);obj.SetActive(false);UnityEngine.Object.Destroy(obj);return;
        }
        int token=obj.GetInstanceID();
        if (state.Objects.ContainsKey(token)) return;
        state.Objects[token]=obj;
        state.Scope.Track("capture:"+token,0,()=>
        {
            var screen=GrabScreen.Instance;
            if (obj != null && screen != null && screen.IsGrabbed && screen.GrabbingEnemy != null &&
                (screen.GrabbingEnemy == obj || screen.GrabbingEnemy.transform.IsChildOf(obj.transform)))
            {
                state.EndingCapture=screen.GrabbingEnemy;
                screen.EndGrab();
                if(screen.IsGrabbed && ReferenceEquals(screen.GrabbingEnemy,state.EndingCapture))
                    throw new InvalidOperationException("Owned capture did not end");
            }
        });
        state.Scope.Track("media:"+token,10,()=> { if(obj != null) StopObject(obj); });
        state.Scope.Track("object:"+token,40,()=>
        {
            if (obj == null) return;
            obj.SetActive(false);UnityEngine.Object.Destroy(obj);
        });
    }
    private static void StopObject(GameObject obj)
    {
        foreach(var audio in obj.GetComponentsInChildren<AudioSource>(true)) { audio.Stop();audio.mute=true; }
        foreach(var video in obj.GetComponentsInChildren<VideoPlayer>(true)) video.Stop();
        foreach(var runner in obj.GetComponentsInChildren<MonoBehaviour>(true)) runner.StopAllCoroutines();
    }
    internal static void Forget(string id,int token)
    {
        if (!States.TryGetValue(id,out var state)) return;
        state.Objects.Remove(token);
        foreach(var prefix in new[]{"capture:","media:","object:"}) state.Scope.Forget(prefix+token);
    }
    internal static void Disable(string id)
    {
        if (!States.TryGetValue(id,out var state) || state.Status=="Disabling") return;
        state.Status="Disabling";
        // Close admission before callbacks/dependent teardown, so no new object
        // can be admitted by an EndGrab or OnDisable reentrant callback.
        state.Scope.CloseAdmission();
        if (Dependents.TryGetValue(id,out var children))
            foreach(var child in new List<string>(children)) Disable(child);
        var errors=state.Scope.Disable();
        state.HadErrors=errors.Count>0;
        PackageBehaviours.RemoveProvider(id);
        state.Status=errors.Count==0 ? "Cleanup pending verification" : "Cleanup failed; restart required";
        state.VerifyFrame=Time.frameCount+2;
        state.VerifyActiveSeconds=0f;
        CustomEnemyPlugin.Log?.LogInfo($"[PackageLifecycle] {id}: {state.Status}; owned={state.Objects.Count}; errors={errors.Count}");
        foreach(var error in errors) CustomEnemyPlugin.Log?.LogError("[PackageLifecycle] "+id+": "+error);
    }
    internal static void Tick()
    {
        foreach(var pair in States)
        {
            var state=pair.Value;
            if(state.VerifyFrame<0 || Time.frameCount<state.VerifyFrame) continue;
            int remaining=0;
            var names=new List<string>();
            foreach(var obj in state.Objects.Values) if(obj != null) { remaining++;names.Add(obj.name); }
            var screen=GrabScreen.Instance;
            bool stuckCapture=!ReferenceEquals(state.EndingCapture,null) && screen != null && screen.IsGrabbed &&
                (ReferenceEquals(screen.GrabbingEnemy,state.EndingCapture) || screen.GrabbingEnemy==null);
            bool held=PauseHooks.SceneClockHeld;
            if(!held) state.VerifyActiveSeconds+=Time.unscaledDeltaTime;
            var result=CleanupVerificationPolicy.Evaluate(state.HadErrors,remaining,state.Scope.Count,stuckCapture,held,state.VerifyActiveSeconds);
            if(result==CleanupVerification.Pending) continue;
            state.VerifyFrame=-1;
            state.Status=result==CleanupVerification.Complete ? "Disabled (cleanup verified)" : "Cleanup failed; restart required";
            CustomEnemyPlugin.Log?.LogInfo($"[PackageLifecycle] {pair.Key}: {state.Status}; remainingObjects={remaining}; pendingCleanup={state.Scope.Count}; stuckCapture={stuckCapture}; remainingNames={string.Join(",",names)}");
        }
    }
}

// Serialized ID travels with native prefab clones; inactive objects retain
// ownership until actual destruction, unlike OnDisable-based unregistering.
internal sealed class PackageOwnedObject : MonoBehaviour
{
    [SerializeField] private string packageId;
    internal string OwnerId => packageId;
    internal void SetOwner(string id)
    {
        if(!string.IsNullOrEmpty(packageId) && !string.Equals(packageId,id,StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Object already belongs to package "+packageId);
        packageId=id;PackageLifecycle.Own(id,gameObject);
    }
    internal void SetTemplateOwner(string id) { packageId=id; }
    private void OnEnable() { if(!string.IsNullOrEmpty(packageId)) PackageLifecycle.Own(packageId,gameObject); }
    private void OnDestroy() { if(!string.IsNullOrEmpty(packageId)) PackageLifecycle.Forget(packageId,gameObject.GetInstanceID()); }
}
