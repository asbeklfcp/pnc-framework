using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
namespace PncEdi;

// Shared, minimal hosts for the verified native 0.4.2 gallery animation contract.
// Never clone the live GrabScreen or search its UI for a plausible-looking Image.
internal static class CreatorVisualHost {
    static int scene=-1;
    static readonly Dictionary<string,RuntimeAnimatorController> Controllers=new Dictionary<string,RuntimeAnimatorController>(StringComparer.Ordinal);
    static readonly Dictionary<string,GameObject> Worlds=new Dictionary<string,GameObject>(StringComparer.Ordinal);
    static readonly HashSet<string> Failed=new HashSet<string>(StringComparer.Ordinal);
    static void EnsureIndex() {
        int current=SceneManager.GetActiveScene().handle;if(scene==current)return;
        scene=current;Controllers.Clear();Failed.Clear();
        foreach(var controller in Resources.FindObjectsOfTypeAll<RuntimeAnimatorController>())
            if(controller!=null && (controller.name=="Gallery_Gravy"||controller.name=="Gallery_Baphomet"||controller.name=="Gallery_Gravy_Grabbed"||controller.name=="Gallery_Baphomet_Grabbed"))Controllers[controller.name]=controller;
    }
    internal static RuntimeAnimatorController Controller(string name){EnsureIndex();return Controllers.TryGetValue(name,out var value)?value:null;}
    internal static void WarmCurrentScene() {
        TryWorld("Gallery_Gravy",out _,out _);TryWorld("Gallery_Baphomet",out _,out _);
    }
    internal static bool Valid(Image image)=>image!=null&&image.sprite!=null&&CreatorVisualPolicy.UsableFrame(image.sprite.name,image.sprite.rect.width,image.sprite.rect.height);
    internal static bool TryBuild(RuntimeAnimatorController controller,string state,Transform parent,bool world,out GameObject root,out Image image,out Animator animator) {
        root=null;image=null;animator=null;
        if(controller==null)return false;
        var host=OverkeksMode.NewOwned(world?"CreatorWorldHost":"CreatorSceneHost",typeof(RectTransform));host.SetActive(false);
        if(parent!=null)host.transform.SetParent(parent,false);
        var rect=host.GetComponent<RectTransform>();rect.anchorMin=Vector2.zero;rect.anchorMax=Vector2.one;rect.offsetMin=Vector2.zero;rect.offsetMax=Vector2.zero;rect.localScale=Vector3.one;
        image=OverkeksMode.AddOwnedComponent<Image>(host);image.color=Color.white;image.raycastTarget=false;image.preserveAspect=true;
        // Remaining bindings in these clips toggle Image.m_Enabled on these paths.
        // Keep empty layer images transparent; no copied chrome or health-bar sprites.
        foreach(string layer in new[]{"Layer 2","Layer 3","Layer 4"}) {
            var child=OverkeksMode.NewOwned(layer,typeof(RectTransform));child.transform.SetParent(host.transform,false);
            var extra=OverkeksMode.AddOwnedComponent<Image>(child);extra.color=Color.clear;extra.raycastTarget=false;
        }
        animator=OverkeksMode.AddOwnedComponent<Animator>(host);animator.runtimeAnimatorController=controller;animator.cullingMode=AnimatorCullingMode.AlwaysAnimate;
        host.SetActive(true);
        try {animator.Rebind();animator.Update(0f);animator.Play(state,0,0f);animator.Update(.001f);}
        catch(Exception ex){Plugin.Log?.LogWarning("[CREATOR-VISUAL] animation probe failed: "+ex.Message);}
        if(!Valid(image)) {
            Plugin.Log?.LogWarning("[CREATOR-VISUAL] rejected empty/chrome frame: controller="+controller.name+" state="+state+" sprite="+(image.sprite!=null?image.sprite.name:"<null>"));
            host.SetActive(false);UnityEngine.Object.Destroy(host);image=null;animator=null;return false;
        }
        if(world) {
            var body=OverkeksMode.NewOwned("WorldSprite");body.transform.SetParent(host.transform,false);
            var renderer=OverkeksMode.AddOwnedComponent<SpriteRenderer>(body);renderer.sprite=image.sprite;
            var bridge=OverkeksMode.AddOwnedComponent<GravyIdleVisualBridge>(host);bridge.Source=image;bridge.Target=renderer;bridge.Sync();
            OverkeksMode.AddOwnedComponent<CreatorWorldBillboard>(host);
        }
        root=host;return true;
    }
    internal static bool TryWorld(string controllerName,out GameObject template,out RuntimeAnimatorController controller,RuntimeAnimatorController resolvedHint=null) {
        EnsureIndex();template=null;controller=null;
        if(resolvedHint!=null && resolvedHint.name==controllerName && (!Controllers.TryGetValue(controllerName,out var indexed)||indexed!=resolvedHint)) {
            Controllers[controllerName]=resolvedHint;Failed.Remove(controllerName);
        }
        if(Worlds.TryGetValue(controllerName,out var old)&&old!=null) {template=old;controller=old.GetComponent<Animator>().runtimeAnimatorController;return true;}
        if(Failed.Contains(controllerName))return false;
        if(!Controllers.TryGetValue(controllerName,out controller)||!TryBuild(controller,"Idle",null,true,out template,out _,out _)) {Failed.Add(controllerName);return false;}
        template.SetActive(false);UnityEngine.Object.DontDestroyOnLoad(template);Worlds[controllerName]=template;
        Plugin.DBG("CREATOR-VISUAL","cached verified root-Image idle "+controllerName);return true;
    }
    internal static void ReleaseInvalidScene(GrabScreen grab) {
        Plugin.Log?.LogWarning("[CREATOR-VISUAL] Scene has no valid frame; scheduling safe release instead of a black capture.");
        BaphometGrabOverlay.Hide();OverkeksMode.StartCoroutine(ReleaseNextFrame(grab));
    }
    static IEnumerator ReleaseNextFrame(GrabScreen grab) {
        yield return null;
        if(OverkeksMode.Active&&grab!=null&&grab.IsGrabbed)GrabEndHelper.TryEndActiveHScene(forceImmediate:true);
    }
}
internal sealed class CreatorWorldBillboard:MonoBehaviour {
    Camera camera;
    void LateUpdate() {
        if(camera==null)camera=Camera.main;if(camera==null)return;
        Vector3 direction=transform.position-camera.transform.position;direction.y=0;
        if(direction.sqrMagnitude>.001f)transform.rotation=Quaternion.LookRotation(direction,Vector3.up);
    }
}
