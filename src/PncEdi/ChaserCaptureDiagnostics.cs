using System.Collections;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
namespace PncEdi;
// Event-only observation, capped at four accepted chaser captures per scene.
// Never teleports, reparents, unlocks or changes rendering.
[HarmonyPatch]
internal static class ChaserCaptureDiagnostics {
    static int scene=int.MinValue,count;
    static GameObject actor;
    static string Describe(GrabScreen grab,GameObject enemy) {
        var player=GameObject.FindGameObjectWithTag("Player");
        var camera=OptionalMembers.Read(grab,"playerCamera") as Camera;
        var root=OptionalMembers.Read(grab,"cameraRoot") as Transform;
        var holder=GameObject.Find("ChaserHolder");
        PlayerStatsHUD hud=null;
        for(var parent=grab.transform.parent;parent!=null&&hud==null;parent=parent.parent)hud=parent.GetComponentInChildren<PlayerStatsHUD>(true);
        var heat=hud!=null?OptionalMembers.Read(hud,"heatAnimator") as Animator:null;
        var canvas=heat!=null?heat.GetComponent<Canvas>():null;
        var fill=hud!=null?OptionalMembers.Read(hud,"heatFill") as Image:null;
        string drawn="";int drawnCount=0;
        foreach(var graphic in grab.GetComponentsInChildren<Graphic>(true)) {
            if(graphic==null || !graphic.enabled || !graphic.gameObject.activeInHierarchy || graphic.color.a<=.01f)continue;
            var image=graphic as Image;
            drawn+=graphic.name+":"+(image!=null&&image.sprite!=null?image.sprite.name:"<graphic>")+";";
            if(++drawnCount>=4)break;
        }
        float alpha=1;
        if(heat!=null)foreach(var group in heat.GetComponentsInParent<CanvasGroup>(true))alpha*=group.alpha;
        return "grabbed="+grab.IsGrabbed+"; player="+(player!=null?player.transform.position.ToString():"<none>")+
            "; playerParent="+(player!=null&&player.transform.parent!=null?player.transform.parent.name:"<none>")+
            "; camera="+(camera!=null?camera.transform.position.ToString():"<none>")+
            "; cameraEnabled="+(camera!=null&&camera.enabled)+"; mask="+(camera!=null?camera.cullingMask:0)+
            "; cameraClip="+(camera!=null?(camera.nearClipPlane+"/"+camera.farClipPlane):"<none>")+
            "; cameraParent="+(camera!=null&&camera.transform.parent!=null?camera.transform.parent.name:"<none>")+
            "; cameraRoot="+(root!=null?root.position.ToString():"<none>")+
            "; cameraLocal="+(root!=null?root.localPosition.ToString():"<none>")+
            "; actor="+(enemy!=null?enemy.transform.position.ToString():"<none>")+
            "; actorActive="+(enemy!=null&&enemy.activeInHierarchy)+
            "; slotActive="+(enemy!=null&&ChaserSweeperHooks.IsActiveManagedChaser(enemy))+
            "; holder="+(holder!=null?holder.transform.position.ToString():"<none>")+
            "; uiRoot="+grab.transform.root.name+
            "; visibleGrabGraphics="+drawn+
            "; heatAnimator="+(heat!=null?heat.name:"<none>")+"; heatActive="+(heat!=null&&heat.gameObject.activeInHierarchy)+
            "; heatEnabled="+(heat!=null&&heat.enabled)+"; heatGroupAlpha="+alpha+
            "; heatCanvas="+(canvas!=null?(canvas.enabled+"/"+canvas.sortingOrder+"/"+canvas.renderMode):"<none>")+
            "; heatFill="+(fill!=null?(fill.enabled+"/"+fill.fillAmount+"/"+fill.color.a):"<none>");
    }
    [HarmonyPatch(typeof(GrabScreen),"StartGrab"),HarmonyPrefix,HarmonyPriority(Priority.First)]
    static void Before(GrabScreen __instance,GameObject enemy,out string __state) {
        __state=null;
        int current=SceneManager.GetActiveScene().handle;
        if(scene!=current){scene=current;count=0;actor=null;}
        if(count>=4 || !OverkeksMode.Active || __instance==null || __instance.IsGrabbed ||
            !GrabStruggleHooks.IsChaserBossGrabEnemy(enemy))return;
        __state=Describe(__instance,enemy);
    }
    [HarmonyPatch(typeof(GrabScreen),"StartGrab"),HarmonyPostfix,HarmonyPriority(Priority.Last)]
    static void After(GrabScreen __instance,GameObject enemy,string __state) {
        if(__state==null || __instance==null || !__instance.IsGrabbed)return;
        count++;actor=enemy;
        Plugin.DBG("CHASER-CONTRACT","phase=before; "+__state);
        Plugin.DBG("CHASER-CONTRACT","phase=capture; "+Describe(__instance,enemy));
        if(Plugin.Instance!=null)Plugin.Instance.StartCoroutine(CaptureAfterFrames(__instance,enemy));
    }
    [HarmonyPatch(typeof(GrabScreen),"EndGrab"),HarmonyPrefix]
    static void BeforeEnd(GrabScreen __instance,out bool __state) =>
        __state=OverkeksMode.Active && actor!=null && __instance!=null && __instance.IsGrabbed;
    [HarmonyPatch(typeof(GrabScreen),"EndGrab"),HarmonyPostfix,HarmonyPriority(Priority.Last)]
    static void AfterEnd(GrabScreen __instance,bool __state) {
        if(!__state || __instance==null)return;
        var endedActor=actor;actor=null;
        Plugin.DBG("CHASER-CONTRACT","phase=exit; "+Describe(__instance,endedActor));
        if(Plugin.Instance!=null)Plugin.Instance.StartCoroutine(AfterFrames(__instance,endedActor));
    }
    static IEnumerator CaptureAfterFrames(GrabScreen grab,GameObject enemy) {
        yield return null;yield return null;
        if(grab!=null&&grab.IsGrabbed)Plugin.DBG("CHASER-CONTRACT","phase=capture+2frames; "+Describe(grab,enemy));
    }
    static IEnumerator AfterFrames(GrabScreen grab,GameObject endedActor) {
        yield return null;yield return null;
        if(grab!=null && !grab.IsGrabbed)Plugin.DBG("CHASER-CONTRACT","phase=exit+2frames; "+Describe(grab,endedActor));
    }
}
