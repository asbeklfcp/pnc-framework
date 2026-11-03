using UnityEngine;
namespace PncEdi;
// Per-actor presentation contract. Native enemies without replacement visuals are untouched.
[DefaultExecutionOrder(10000)]
internal sealed class ReskinPresentation:MonoBehaviour {
    Renderer[] baseRenderers;
    Transform visualRoot;
    SpriteRenderer visual;
    BrawlerEnemyAI hypnotist;
    float nativeLookHeight;
    bool configured,reportedVisualRepair;
    Transform healthBar;
    Transform healthBarArtwork;
    Vector3 nativeBarLocalPosition;
    int diagnosticBudget;
    float nextDiagnostic;
    Camera diagnosticCamera;
    MonoBehaviour actor;
    ReskinAudioLease audioLease;
    readonly System.Collections.Generic.List<Transform> replacementRoots=new System.Collections.Generic.List<Transform>();
    internal bool HasVisual => visualRoot!=null && visual!=null && visual.sprite!=null;
    // Future content definitions can supply a precise anchor instead of the bounds estimate.
    internal Transform EyeAnchor;
    internal float FaceHeightFraction=.83f;
    internal void Configure(GameObject replacement) {
        if(replacement==null)return;
        visualRoot=replacement.transform;visual=replacement.GetComponentInChildren<SpriteRenderer>(true);
        if(!replacementRoots.Contains(visualRoot))replacementRoots.Add(visualRoot);
        actor=EnemyAiTypes.First(gameObject);
        if(audioLease==null && actor!=null)audioLease=new ReskinAudioLease(actor);
        audioLease?.Apply();
        hypnotist=GetComponent<BrawlerEnemyAI>();
        if(!configured && hypnotist!=null)nativeLookHeight=hypnotist.hypnosisLookHeight;
        if(!configured && OptionalMembers.Read(actor,"healthBar") is Component bar) {healthBar=bar.transform;nativeBarLocalPosition=healthBar.localPosition;
            if(OptionalMembers.Read(bar,"fillImage") is Component fill && fill.transform.parent!=null && fill.transform.parent.IsChildOf(healthBar))healthBarArtwork=fill.transform.parent;
        }
        configured=true;diagnosticBudget=3;nextDiagnostic=Time.unscaledTime+1;CacheRenderers();Enforce();UpdateGazeTarget();
        var gravy=GetComponent<GravySerpentMarker>();if(gravy!=null)gravy.SharedPresentation=true;
        var baph=GetComponent<BaphometGargoyleMarker>();if(baph!=null)baph.SharedPresentation=true;
        Plugin.DBG("RESKIN-VISUAL","actor="+gameObject.name+"; baseRenderers="+baseRenderers.Length+"; replacement="+replacement.name+"; sprite="+(visual!=null&&visual.sprite!=null?visual.sprite.name:"<none>")+"; visible="+(visual!=null&&visual.enabled)+"; guards="+GetComponents<ReskinPresentation>().Length);
    }
    void CacheRenderers() {
        var all=GetComponentsInChildren<Renderer>(true);
        var list=new System.Collections.Generic.List<Renderer>();
        foreach(var renderer in all) {
            if(renderer==null || renderer==visual)continue;
            bool replacementOwned=renderer.GetComponentInParent<GravyVisual>()!=null || renderer.GetComponentInParent<BaphometStatueVisual>()!=null;
            foreach(var root in replacementRoots)if(root!=null && renderer.transform.IsChildOf(root)){replacementOwned=true;break;}
            if(!replacementOwned)list.Add(renderer);
        }
        baseRenderers=list.ToArray();
    }
    void OnTransformChildrenChanged(){if(configured)CacheRenderers();}
    void OnEnable(){if(configured){audioLease?.Apply();diagnosticBudget=3;nextDiagnostic=Time.unscaledTime+1;CacheRenderers();Enforce();UpdateGazeTarget();}}
    void OnDisable(){audioLease?.Restore();reportedVisualRepair=false;if(healthBar!=null)healthBar.localPosition=nativeBarLocalPosition;if(hypnotist!=null)hypnotist.hypnosisLookHeight=nativeLookHeight;}
    void OnDestroy(){audioLease?.Restore();if(healthBar!=null)healthBar.localPosition=nativeBarLocalPosition;if(hypnotist!=null)hypnotist.hypnosisLookHeight=nativeLookHeight;}
    void LateUpdate(){
        Enforce();
        if(HasVisual && healthBar!=null && visual.bounds.size.y>.01f) {
            var b=visual.bounds;
            // Anchor the visible background, not its parent: native prefabs already offset
            // Background upwards by 3.5 units. Preserve that hierarchy and its native facing.
            var offset=healthBarArtwork!=null ? healthBarArtwork.position-healthBar.position : Vector3.zero;
            healthBar.position=new Vector3(b.center.x,b.max.y+.25f,b.center.z)-offset;
        }
        if(diagnosticBudget>0 && Time.unscaledTime>=nextDiagnostic && HasVisual) {
            diagnosticBudget--;nextDiagnostic=Time.unscaledTime+5;
            if(diagnosticCamera==null)diagnosticCamera=Camera.main;
            var material=visual.sharedMaterial;
            Plugin.DBG("RESKIN-STATE","actor="+gameObject.name+"; id="+gameObject.GetInstanceID()+
                "; active="+gameObject.activeInHierarchy+"; renderer="+visual.enabled+"; forcedOff="+visual.forceRenderingOff+"; alpha="+visual.color.a+
                "; inView="+visual.isVisible+"; rootScale="+visualRoot.lossyScale+"; position="+visual.bounds.center+"; size="+visual.bounds.size+
                "; barRoot="+(healthBar!=null?healthBar.position:Vector3.zero)+"; barArtwork="+(healthBarArtwork!=null?healthBarArtwork.position:Vector3.zero)+
                "; layer="+visual.gameObject.layer+"; cameraMask="+(diagnosticCamera!=null?diagnosticCamera.cullingMask:0)+
                "; shader="+(material!=null&&material.shader!=null?material.shader.name:"<none>")+
                "; hp="+OptionalMembers.Read(actor,"currentHealth")+"; transformed="+OptionalMembers.Read(actor,"isTransformed"));
        }
    }
    internal void Enforce() {
        if(!configured || visual==null || visual.sprite==null || visualRoot==null)return;
        if(baseRenderers==null)CacheRenderers();
        foreach(var renderer in baseRenderers)if(renderer!=null && renderer.enabled)renderer.enabled=false;
        bool alive=actor!=null && !(OptionalMembers.Read(actor,"isDead") is bool dead && dead) &&
            !(OptionalMembers.Read(actor,"currentHealth") is int hp && hp<=0);
        // NearbyEnemyHider deactivates the actor during scenes. Never reveal an inactive
        // capture participant or resurrect a corpse merely to repair a renderer flag.
        if(alive && gameObject.activeInHierarchy && visual.gameObject.activeInHierarchy && !visual.enabled) {
            visual.enabled=true;
            if(!reportedVisualRepair){reportedVisualRepair=true;Plugin.DBG("RESKIN-VISUAL","restored living replacement renderer on "+gameObject.name);}
        }
    }
    internal void UpdateGazeTarget() {
        if(!configured || hypnotist==null || visual==null || visual.sprite==null)return;
        var bounds=visual.bounds;
        float target=EyeAnchor!=null ? EyeAnchor.position.y-transform.position.y :
            CreatorVisualPolicy.FaceHeightOffset(bounds.min.y,bounds.size.y,transform.position.y,FaceHeightFraction);
        if(float.IsNaN(target)||float.IsInfinity(target))return;
        hypnotist.hypnosisLookHeight=Mathf.Clamp(target,.1f,10f);
    }
}
