using UnityEngine;
using UnityEngine.UI;
namespace PncEdi;
// Visual leases only: never ForceHide (which clears native boss tracking).
internal static class CaptureBossHud {
    static CanvasGroup group;
    static float alpha;
    static bool added;
    static Canvas heatCanvas;
    static int heatOrder;
    static bool heatOverride;
    static bool heatEnabled;
    static Animator heatAnimator;
    static bool inventoryOpen,hasInventoryParameter,lookedUp;
    internal static void Tick() {
        var grab=GrabScreen.Instance;
        bool active=OverkeksMode.Active && grab!=null && grab.IsGrabbed;
        if(!active){Restore();return;}
        var ui=MiniBossHealthBarUI.Instance;
        var animator=ui!=null?OptionalMembers.Read(ui,"barAnimator") as Animator:null;
        if(animator!=null) {
            if(group!=null && group.gameObject!=animator.gameObject)RestoreBoss();
            if(group==null) {
                group=animator.GetComponent<CanvasGroup>();added=group==null;
                if(added)group=animator.gameObject.AddComponent<CanvasGroup>();
                alpha=group.alpha;
            }
            group.alpha=0;
        }
        if(!lookedUp) {
            lookedUp=true;
            PlayerStatsHUD hud=null;
            for(var parent=grab.transform.parent;parent!=null && hud==null;parent=parent.parent)
                hud=parent.GetComponentInChildren<PlayerStatsHUD>(true);
            heatAnimator=hud!=null?OptionalMembers.Read(hud,"heatAnimator") as Animator:null;
            if(heatAnimator!=null) {
                heatCanvas=heatAnimator.GetComponent<Canvas>();
                if(heatCanvas!=null){heatOrder=heatCanvas.sortingOrder;heatOverride=heatCanvas.overrideSorting;heatEnabled=heatCanvas.enabled;}
                foreach(var parameter in heatAnimator.parameters)
                    if(parameter.name=="InventoryOpen" && parameter.type==AnimatorControllerParameterType.Bool)hasInventoryParameter=true;
                if(hasInventoryParameter){inventoryOpen=heatAnimator.GetBool("InventoryOpen");heatAnimator.SetBool("InventoryOpen",true);}
                if(heatAnimator.HasState(0,Animator.StringToHash("Base Layer.HeatShow")))heatAnimator.Play("HeatShow",0,1f);
                Plugin.DBG("CAPTURE-HUD","native heat animator resolved; object="+heatAnimator.name+
                    "; active="+heatAnimator.gameObject.activeInHierarchy+"; enabled="+heatAnimator.enabled+
                    "; inventoryParameter="+hasInventoryParameter+"; canvas="+(heatCanvas!=null));
            } else Plugin.DBG("CAPTURE-HUD","native PlayerStatsHUD heatAnimator unresolved; parent="+(grab.transform.parent!=null?grab.transform.parent.name:"<none>"));
        }
        if(heatAnimator!=null) {
            heatAnimator.gameObject.SetActive(true);
            if(hasInventoryParameter)heatAnimator.SetBool("InventoryOpen",true);
        }
        if(heatCanvas!=null){heatCanvas.enabled=true;heatCanvas.overrideSorting=true;heatCanvas.sortingOrder=CreatorVisualPolicy.CaptureHudSortingOrder;}

    }
    static void RestoreBoss(){if(group!=null){group.alpha=alpha;if(added)UnityEngine.Object.Destroy(group);}group=null;added=false;}
    static void RestoreHeat(){
        if(heatAnimator!=null && hasInventoryParameter)heatAnimator.SetBool("InventoryOpen",inventoryOpen);
        if(heatCanvas!=null){heatCanvas.sortingOrder=heatOrder;heatCanvas.overrideSorting=heatOverride;heatCanvas.enabled=heatEnabled;}
        heatCanvas=null;heatAnimator=null;hasInventoryParameter=false;lookedUp=false;
    }
    static void Restore(){RestoreBoss();RestoreHeat();}
}
