using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using Object=UnityEngine.Object;

namespace PncPrivateLivingMap
{
    // Sample the frozen visual clip on an inert mirror using a manual playable.
    // No controller state machine, gameplay scripts, collision, audio or events run.
    internal sealed class PausedActorArt : IDisposable
    {
        sealed class Preview
        {
            internal GameObject Root;
            internal Animator Source;
            internal AnimationClip Clip;
            internal RuntimeAnimatorController Controller;
            internal float Start, Speed, Started;
            internal bool Loop;
            internal int Frame=-1;
            internal PlayableGraph Graph;
            internal AnimationClipPlayable Playable;
            internal Animator MirrorAnimator;
            internal void Destroy()
            {
                if(Graph.IsValid())Graph.Destroy();
                if(Root!=null)Object.Destroy(Root);
            }
            internal readonly Dictionary<SpriteRenderer,SpriteRenderer> Sprites=new Dictionary<SpriteRenderer,SpriteRenderer>();
            internal readonly Dictionary<SpriteRenderer,Matrix4x4> BindPose=new Dictionary<SpriteRenderer,Matrix4x4>();
        }
        readonly PackageSpritePreview packageArt=new PackageSpritePreview();
        readonly Dictionary<Animator,Preview> previews=new Dictionary<Animator,Preview>();
        readonly Dictionary<SpriteRenderer,Sprite> saved=new Dictionary<SpriteRenderer,Sprite>();
        readonly Dictionary<SpriteRenderer,SpriteRenderer> sceneryProxies=new Dictionary<SpriteRenderer,SpriteRenderer>();
        readonly List<SpriteRenderer> revealedScenery=new List<SpriteRenderer>();
        readonly MaterialPropertyBlock sceneryBlock=new MaterialPropertyBlock();
        readonly List<Material> sceneryMaterials=new List<Material>(4);
        readonly Dictionary<SpriteRenderer,Animator> owners=new Dictionary<SpriteRenderer,Animator>();
        readonly HashSet<Animator> failed=new HashSet<Animator>();
        readonly HashSet<Animator> unavailableScenery=new HashSet<Animator>();
        static GameObject Mirror(Transform source,Transform parent,Preview preview)
        {
            var obj=new GameObject(source.name){hideFlags=HideFlags.HideAndDontSave,layer=2};
            obj.transform.SetParent(parent,false);
            obj.transform.localPosition=source.localPosition;
            obj.transform.localRotation=source.localRotation;
            obj.transform.localScale=source.localScale;
            if(source.TryGetComponent<SpriteRenderer>(out var sprite))
            {
                var copy=obj.AddComponent<SpriteRenderer>();
                copy.forceRenderingOff=true;copy.enabled=true;copy.sprite=sprite.sprite;copy.color=sprite.color;copy.flipX=sprite.flipX;copy.flipY=sprite.flipY;
                preview.Sprites[sprite]=copy;
            }
            for(int i=0;i<source.childCount;i++)Mirror(source.GetChild(i),obj.transform,preview);
            obj.SetActive(source.gameObject.activeSelf);
            return obj;
        }
        Preview Build(Animator animator,bool scenery)
        {
            if(animator.runtimeAnimatorController==null)return null;
            AnimationClip clip=null;float weight=-1,start=0,speed=animator.speed;bool loop=false;
            if(animator.isInitialized && animator.layerCount>0)
            {
                var state=animator.GetCurrentAnimatorStateInfo(0);
                foreach(var item in animator.GetCurrentAnimatorClipInfo(0))
                    if(item.clip!=null && item.clip.length>0 && item.weight>weight){clip=item.clip;weight=item.weight;}
                if(clip!=null){start=state.normalizedTime*clip.length;speed*=state.speed*state.speedMultiplier;loop=clip.isLooping||state.loop;}
            }
            // Culled/inactive remembered props can lose initialized state. Only
            // recover an unambiguous authored loop; never guess a gameplay state.
            if(clip==null && scenery)
            {
                foreach(var candidate in animator.runtimeAnimatorController.animationClips)
                {
                    if(candidate==null)continue;
                    if(clip!=null&&clip!=candidate)return null;
                    clip=candidate;
                }
                if(clip==null||!clip.isLooping||clip.length<=0)return null;
                loop=true;
            }
            if(clip==null || (scenery&&!loop))return null;
            var p=new Preview{Source=animator,Controller=animator.runtimeAnimatorController,Clip=clip,Loop=loop,
                Start=start,Speed=speed,Started=Time.unscaledTime};
            // Inert mirror, never instantiate the original gameplay object.
            p.Root=Mirror(animator.transform,null,p);
            // These objects contain visual components only, never source scripts.
            p.Root.SetActive(true);
            foreach(var pair in p.Sprites)
            {
                if(scenery)
                    for(var t=pair.Value.transform;t!=null;t=t.parent)t.gameObject.SetActive(true);
                p.BindPose[pair.Key]=pair.Value.transform.localToWorldMatrix.inverse;
            }
            if(p.Sprites.Count==0){p.Destroy();return null;}
            try
            {
                p.MirrorAnimator=p.Root.AddComponent<Animator>();
                p.MirrorAnimator.cullingMode=AnimatorCullingMode.AlwaysAnimate;
                p.MirrorAnimator.applyRootMotion=false;p.MirrorAnimator.fireEvents=false;
                p.Graph=PlayableGraph.Create("PNC map-only visual clip");
                p.Graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                p.Playable=AnimationClipPlayable.Create(p.Graph,clip);
                p.Playable.SetApplyFootIK(false);p.Playable.SetApplyPlayableIK(false);
                var output=AnimationPlayableOutput.Create(p.Graph,"Sprite preview",p.MirrorAnimator);
                output.SetSourcePlayable(p.Playable);
                p.Playable.SetSpeed(0);
                p.Graph.Play();
                return p;
            }
            catch{p.Destroy();throw;}
        }
        internal Sprite Sample(SpriteRenderer renderer,bool loopingOnly=false,bool retained=false)
        {
            if(!MapPause.Active || renderer==null || (!retained&&(!renderer.enabled || !renderer.gameObject.activeInHierarchy || renderer.forceRenderingOff)))return null;
            if(!loopingOnly){var packageFrame=packageArt.Sample(renderer); if(packageFrame!=null)return packageFrame;}
            if(!owners.TryGetValue(renderer,out var animator))
            {
                animator=renderer.GetComponentInParent<Animator>(true);
                // Cache missing owners for this pause session.
                owners[renderer]=animator;
            }
            if(animator==null||(!retained&&!animator.isActiveAndEnabled) || failed.Contains(animator) || (loopingOnly&&unavailableScenery.Contains(animator)))return null;
            try
            {
                if(!previews.TryGetValue(animator,out var p) || p.Controller!=animator.runtimeAnimatorController)
                {
                    if(p!=null){p.Destroy();previews.Remove(animator);}
                    p=Build(animator,loopingOnly);
                    if(p==null){if(loopingOnly)unavailableScenery.Add(animator);return null;}
                    previews[animator]=p;
                }
                if(loopingOnly && !p.Loop)return null; if(!p.Sprites.TryGetValue(renderer,out var copy) || copy==null)return null;
                if(p.Frame!=Time.frameCount)
                {
                    float elapsed=Time.unscaledTime-p.Started;
                    float t=p.Start+elapsed*p.Speed;
                    t=p.Loop?Mathf.Repeat(t,p.Clip.length):Mathf.Clamp(t,0,Mathf.Max(0,p.Clip.length-.0001f));
                    p.Playable.SetTime(t);
                    p.Graph.Evaluate(0);
                    foreach(var pair in p.Sprites)if(pair.Value!=null)pair.Value.forceRenderingOff=true;
                    p.Frame=Time.frameCount;
                }
                return copy.sprite;
            }
            catch(Exception ex)
            {
                failed.Add(animator);
                Debug.LogWarning("[PNC MAP] Visual clip preview unavailable: "+ex.Message);
                return null;
            }
        }
        internal void Apply(SpriteRenderer renderer,bool loopingOnly=false)
        {
            var sprite=Sample(renderer,loopingOnly);
            if(sprite==null || sprite==renderer.sprite || saved.ContainsKey(renderer))return;
            saved.Add(renderer,renderer.sprite);
            renderer.sprite=sprite;
        }
        // A visual-only proxy preserves animated pose, tint and frame while
        // leaving source transforms, colliders and gameplay components untouched.
        internal bool ApplyScenery(SpriteRenderer renderer)
        {
            var sprite=Sample(renderer,true);
            if(sprite==null || !owners.TryGetValue(renderer,out var animator) || animator==null ||
                !previews.TryGetValue(animator,out var p) || !p.Sprites.TryGetValue(renderer,out var copy) || copy==null)return false;
            var parent=animator.transform.parent;
            var matrix=(parent!=null?parent.localToWorldMatrix:Matrix4x4.identity)*copy.transform.localToWorldMatrix;
            var source=renderer.localToWorldMatrix;
            bool poseChanged=false;
            for(int i=0;i<16;i++)if(Mathf.Abs(matrix[i]-source[i])>.0001f){poseChanged=true;break;}
            if(!poseChanged && copy.color==renderer.color && copy.flipX==renderer.flipX && copy.flipY==renderer.flipY)
            {
                if(sprite!=renderer.sprite&&!saved.ContainsKey(renderer)){saved.Add(renderer,renderer.sprite);renderer.sprite=sprite;}
                return false;
            }
            if(!sceneryProxies.TryGetValue(renderer,out var proxy)||proxy==null)
            {
                var go=new GameObject("PNC map-only animated scenery"){hideFlags=HideFlags.HideAndDontSave,layer=renderer.gameObject.layer};
                proxy=go.AddComponent<SpriteRenderer>();proxy.forceRenderingOff=true;
                sceneryProxies[renderer]=proxy;
            }
            proxy.gameObject.layer=renderer.gameObject.layer;
            proxy.transform.SetPositionAndRotation(matrix.GetColumn(3),matrix.rotation);
            proxy.transform.localScale=matrix.lossyScale;
            sceneryMaterials.Clear();renderer.GetSharedMaterials(sceneryMaterials);proxy.SetSharedMaterials(sceneryMaterials);
            proxy.sortingLayerID=renderer.sortingLayerID;proxy.sortingOrder=renderer.sortingOrder;
            proxy.shadowCastingMode=renderer.shadowCastingMode;proxy.receiveShadows=renderer.receiveShadows;
            proxy.sprite=sprite;proxy.color=copy.color;proxy.flipX=copy.flipX;proxy.flipY=copy.flipY;
            proxy.drawMode=renderer.drawMode;proxy.size=renderer.size;
            sceneryBlock.Clear();renderer.GetPropertyBlock(sceneryBlock);proxy.SetPropertyBlock(sceneryBlock);
            proxy.forceRenderingOff=false;revealedScenery.Add(proxy);
            return true;
        }
#if MAP_QA
        internal bool MaxClean()=>previews.Count==0&&saved.Count==0&&sceneryProxies.Count==0&&revealedScenery.Count==0&&owners.Count==0;
        internal SpriteRenderer QaRenderedScenery(SpriteRenderer original)=>sceneryProxies.TryGetValue(original,out var proxy)&&proxy!=null&&!proxy.forceRenderingOff?proxy:original;
#endif
        // Animate only a proxy that was already eligible for remembered scenery.
        // The live source remains hidden/inactive and is never modified.
        internal void ApplyRetainedScenery(VisualInventory.Part part)
        {
            if(!(part.Source is SpriteRenderer source)||!(part.NativeProxy is SpriteRenderer proxy))return;
            var sprite=Sample(source,true,true);
            if(sprite==null || !owners.TryGetValue(source,out var animator) || animator==null ||
                !previews.TryGetValue(animator,out var p) || !p.Sprites.TryGetValue(source,out var copy) || copy==null ||
                !p.BindPose.TryGetValue(source,out var inverse))return;
            var matrix=Matrix4x4.TRS(part.NativePosition,part.NativeRotation,part.NativeScale)*inverse*copy.transform.localToWorldMatrix;
            proxy.transform.SetPositionAndRotation(matrix.GetColumn(3),matrix.rotation);
            proxy.transform.localScale=matrix.lossyScale;
            proxy.sprite=sprite;proxy.color=copy.color;proxy.flipX=copy.flipX;proxy.flipY=copy.flipY;
        }
        internal void Restore()
        {
            foreach(var pair in saved)if(pair.Key!=null)pair.Key.sprite=pair.Value;
            saved.Clear();
            foreach(var proxy in revealedScenery)if(proxy!=null)proxy.forceRenderingOff=true;
            revealedScenery.Clear();
        }
        public void Dispose()
        {
            Restore();
            foreach(var p in previews.Values)p.Destroy();
            foreach(var proxy in sceneryProxies.Values)if(proxy!=null)Object.Destroy(proxy.gameObject);
            sceneryProxies.Clear();
            previews.Clear();owners.Clear();failed.Clear();unavailableScenery.Clear();packageArt.Clear();
        }
    }
}
