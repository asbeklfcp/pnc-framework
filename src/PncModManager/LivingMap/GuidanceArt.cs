using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Object=UnityEngine.Object;
namespace PncPrivateLivingMap {
internal sealed class GuidanceArt {
    internal const float Spacing=2.6f, Speed=2.1f, Range=55f;
    internal const int Slots=24, VertexCount=Slots*6;
    internal struct Segment {
        internal Vector3 A,F,Side;
        internal float Begin,End;
    }
    // Route geometry is prepared only on a real path revision. Motion itself
    // never touches the navigation graph or performs physics queries.
    readonly List<Segment> segments=new List<Segment>(64);
    readonly List<Vector3> vertices=new List<Vector3>(VertexCount);
    Mesh mesh;
    MeshRenderer renderer;
    Material material;
    int lastRevision=-1;
    bool lastMotion,dirty=true;
    float phase;
    float total;int lastVisible;
    Vector3 origin;
    Bounds bounds;
    internal int FrameUploads {get;private set;}
    internal int TopologyUploads {get;private set;}
    internal int PreparedRoutes {get;private set;}
    internal Renderer Renderer=>renderer;
    internal static float Wrap(float p)=>Mathf.Repeat(p,Spacing);
    internal static float Advance(float p,float dt)=>Wrap(p+Mathf.Clamp(dt,0,.1f)*Speed);
    internal static float Rebase(float p,float removed)=>Wrap(p-Mathf.Max(0,removed));
    internal void ShiftOrigin(float removed){phase=Rebase(phase,removed);dirty=true;}
    internal static float Prepare(List<Vector3> points,List<Segment> output,out Vector3 origin,out Bounds bounds){
        output.Clear();origin=points.Count>0?points[0]:Vector3.zero;
        bounds=new Bounds(origin,Vector3.one);
        float walked=0;
        for(int i=1;i<points.Count&&walked<Range;i++){
            var a=points[i-1];var d=points[i]-a;float length=d.magnitude;
            if(length<.001f)continue;
            var f=d/length;var side=Vector3.Cross(Vector3.up,f).normalized;
            float end=walked+length;
            if(side.sqrMagnitude>=.01f)output.Add(new Segment{A=a,F=f,Side=side,Begin=walked,End=end});
            bounds.Encapsulate(a);bounds.Encapsulate(a+f*Mathf.Min(length,Range-walked));
            walked=end;
        }
        bounds.Expand(1f);
        return Mathf.Min(walked,Range);
    }
    // All slots have permanent indices. An empty slot is a zero-area
    // triangle, not a mesh resize. Taper before each turn or route end.
    internal static int FillFrame(List<Segment> segs,float length,Vector3 origin,float phase,List<Vector3> v){
        while(v.Count<VertexCount)v.Add(origin);
        float at=Wrap(.7f+phase);int si=0,k=0,visible=0;
        for(int slot=0;slot<Slots;slot++,at+=Spacing){
            while(si<segs.Count&&segs[si].End<at)si++;
            Vector3 p=origin,f=Vector3.zero,side=Vector3.zero;
            float scale=0;
            if(si<segs.Count&&at<=length){
                var seg=segs[si];
                if(at>=seg.Begin&&at<=seg.End){
                    float margin=Mathf.Min(at-seg.Begin,Mathf.Min(seg.End-at,length-at));
                    scale=Mathf.SmoothStep(0,1,Mathf.Clamp01(margin/.55f));
                    p=seg.A+seg.F*(at-seg.Begin)+Vector3.up*.018f;f=seg.F;side=seg.Side;
                    if(scale>.001f)visible++;
                }
            }
            v[k++]=p+f*(.22f*scale);
            v[k++]=p+(-f*.20f+side*.16f)*scale;
            v[k++]=p+(-f*.07f+side*.045f)*scale;
            v[k++]=p+f*(.22f*scale);
            v[k++]=p+(-f*.07f-side*.045f)*scale;
            v[k++]=p+(-f*.20f-side*.16f)*scale;
        }
        return visible;
    }
    bool Create(){
        if(renderer!=null)return true;
        var shader=Shader.Find("Hidden/Internal-Colored");if(shader==null)return false;
        material=new Material(shader){hideFlags=HideFlags.HideAndDontSave};
        material.SetColor("_Color",new Color(.73f,1f,.96f,.92f));
        material.SetInt("_ZTest",(int)CompareFunction.LessEqual);material.SetInt("_ZWrite",0);
        material.SetInt("_SrcBlend",(int)BlendMode.SrcAlpha);material.SetInt("_DstBlend",(int)BlendMode.OneMinusSrcAlpha);material.SetInt("_Cull",0);
        material.renderQueue=3002;
        var go=new GameObject("PNC directional guidance"){hideFlags=HideFlags.HideAndDontSave};
        mesh=new Mesh{name="PNC waypoint chevrons",hideFlags=HideFlags.HideAndDontSave};mesh.MarkDynamic();
        while(vertices.Count<VertexCount)vertices.Add(Vector3.zero);
        mesh.SetVertices(vertices);
        var indices=new int[VertexCount];for(int i=0;i<indices.Length;i++)indices[i]=i;
        mesh.SetIndices(indices,MeshTopology.Triangles,0,false);TopologyUploads++;
        go.AddComponent<MeshFilter>().sharedMesh=mesh;
        renderer=go.AddComponent<MeshRenderer>();renderer.sharedMaterial=material;renderer.shadowCastingMode=ShadowCastingMode.Off;renderer.receiveShadows=false;renderer.enabled=false;
        return true;
    }
    internal void Tick(List<Vector3> points,bool show,bool motion,int revision){
        if(!show||points.Count<2){Hide();return;}
        if(!Create())return;
        bool changed=lastRevision!=revision;
        if(changed){
            total=Prepare(points,segments,out origin,out bounds);
            mesh.bounds=bounds;lastRevision=revision;PreparedRoutes++;dirty=true;
        }
        if(lastMotion!=motion){lastMotion=motion;dirty=true;}
        if(motion){phase=Advance(phase,Time.unscaledDeltaTime);dirty=true;}
        if(!dirty){renderer.enabled=lastVisible>0;return;}
        lastVisible=FillFrame(segments,total,origin,phase,vertices);
        mesh.SetVertices(vertices,0,VertexCount,MeshUpdateFlags.DontRecalculateBounds);
        FrameUploads++;renderer.enabled=lastVisible>0;dirty=false;
    }
    internal void Hide(){if(renderer!=null)renderer.enabled=false;}
    internal void Reset(){Hide();phase=0;lastRevision=-1;dirty=true;segments.Clear();}
    internal void Dispose(){
        if(renderer!=null)Object.Destroy(renderer.gameObject);
        if(material!=null)Object.Destroy(material);
        if(mesh!=null)Object.Destroy(mesh);
        renderer=null;material=null;mesh=null;
    }
}
}
