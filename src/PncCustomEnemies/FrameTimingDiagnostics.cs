using System;
using System.Globalization;
using UnityEngine;
using UnityEngine.SceneManagement;
using PncEdi;
namespace PncCustomEnemies;
// Fixed storage, all-frame maximum/counts, no per-frame scene scans or log output.
internal static class FrameTimingDiagnostics
{
    static readonly float[] Samples=new float[4096];
    static int count,stored,over33,over50,over100,sceneHandle;
    static double seconds;
    static float worst;
    static string context,scene;
    static string N(double value)=>value.ToString("0.###",CultureInfo.InvariantCulture);
    internal static void Flush(string reason)
    {
        if(count>0 && seconds>0) {
            Array.Sort(Samples,0,stored);
            float p95=Samples[Math.Max(0,(int)Math.Ceiling(stored*.95)-1)]*1000;
            CustomEnemyPlugin.Log?.LogInfo("[FRAME-TIMING] scene="+scene+"; context="+context+"; reason="+reason+"; frames="+count+"; windowSeconds="+N(seconds)+"; meanFPS="+N(count/seconds)+"; p95ms="+N(p95)+"; maxMs="+N(worst*1000)+"; over33ms="+over33+"; over50ms="+over50+"; over100ms="+over100+"; retainedSamples="+stored+"; percentileScope=first-retained; utc="+DateTime.UtcNow.ToString("O"));
        }
        count=stored=over33=over50=over100=0;seconds=0;worst=0;
    }
    internal static void Tick(bool enabled)
    {
        if(!enabled) {Flush("disabled");context=null;return;}
        string next=PauseHooks.SceneClockHeld ? "UI-held" : GrabScreen.Instance!=null && GrabScreen.Instance.IsGrabbed ? "capture" : "gameplay";
        var active=SceneManager.GetActiveScene();
        float dt=Time.unscaledDeltaTime;
        bool valid=dt>0 && !float.IsNaN(dt) && !float.IsInfinity(dt);
        if(context==null || context!=next || sceneHandle!=active.handle) {
            // Attribution at a pause/focus/scene boundary is ambiguous. Preserve it separately,
            // never silently drop it or claim a focus stall was a gameplay frame.
            string previous=context;
            Flush(sceneHandle!=active.handle ? "scene-change" : "context-change");
            if(previous!=null && valid)CustomEnemyPlugin.Log?.LogInfo("[FRAME-BOUNDARY] from="+previous+"; to="+next+"; fromScene="+scene+"; toScene="+active.name+"; frameMs="+N(dt*1000)+"; attribution=transition-unclassified; utc="+DateTime.UtcNow.ToString("O"));
            context=next;scene=active.name;sceneHandle=active.handle;
            return;
        }
        if(!valid)return;
        count++;seconds+=dt;worst=Math.Max(worst,dt);
        if(dt>.033f)over33++;if(dt>.05f)over50++;if(dt>.1f)over100++;
        if(stored<Samples.Length)Samples[stored++]=dt;
        if(seconds>=10)Flush("interval");
    }
}
