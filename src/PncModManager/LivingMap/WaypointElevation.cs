using System;
using System.Collections.Generic;
using Pathfinding;
using UnityEngine;
namespace PncPrivateLivingMap {
internal static class WaypointElevation {
    static float Cross(Vector2 a,Vector2 b)=>a.x*b.y-a.y*b.x;
    static Vector2 XZ(Vector3 p)=>new Vector2(p.x,p.z);
    // Ordered crossings of the actual triangle portals, not a global height.
    internal static bool Profile(Vector3 a,Vector3 b,List<(Vector3 left,Vector3 right)> portals,List<Vector3> output){
        var start=XZ(a);var delta=XZ(b)-start;float last=-.001f;
        if(delta.sqrMagnitude<.000001f)return false;
        var local=new List<Vector3>{a};
        foreach(var portal in portals){
            var l=XZ(portal.left);var edge=XZ(portal.right)-l;
            float det=Cross(delta,edge),t,u;
            if(Mathf.Abs(det)<.000001f){
                if(Mathf.Abs(Cross(l-start,delta))>.002f)return false;
                float t0=Vector2.Dot(l-start,delta)/delta.sqrMagnitude;
                float t1=Vector2.Dot(l+edge-start,delta)/delta.sqrMagnitude;
                t=Mathf.Max(last,Mathf.Min(t0,t1),0);
                if(t>Mathf.Min(1,Mathf.Max(t0,t1))+.002f)return false;
                u=edge.sqrMagnitude>.000001f?Vector2.Dot(start+delta*t-l,edge)/edge.sqrMagnitude:0;
            }else{
                t=Cross(l-start,edge)/det;u=Cross(l-start,delta)/det;
            }
            if(t<last-.002f||t<-.002f||t>1.002f||u<-.002f||u>1.002f)return false;
            t=Mathf.Clamp01(t);last=t;
            var p=Vector3.Lerp(portal.left,portal.right,Mathf.Clamp01(u));
            var xy=start+delta*t;p.x=xy.x;p.z=xy.y;
            Append(local,p);
        }
        Append(local,b);
        // Simplify only genuinely collinear 3D segments. Keep slope and landing breaks.
        for(int i=local.Count-2;i>0;i--){
            var d=local[i+1]-local[i-1];
            if(d.sqrMagnitude<.000001f)continue;
            var q=local[i-1]+d*Mathf.Clamp01(Vector3.Dot(local[i]-local[i-1],d)/d.sqrMagnitude);
            if((q-local[i]).sqrMagnitude<.015f*.015f)local.RemoveAt(i);
        }
        foreach(var p in local)Append(output,p);
        return true;
    }
    internal static void Append(List<Vector3> points,Vector3 p){
        if(points.Count==0||(points[points.Count-1]-p).sqrMagnitude>.000001f)points.Add(p);
    }
    struct Span {
        internal float Lo,Hi,Y0,DY;
        internal float Y(float t)=>Y0+DY*t;
    }
    static bool Clip(float at,float slope,ref float lo,ref float hi){
        // Barycentric coordinates along the segment are affine.
        const float eps=.00001f;
        if(Mathf.Abs(slope)<.0000001f)return at>=-eps;
        float t=(-eps-at)/slope;
        if(slope>0)lo=Mathf.Max(lo,t);else hi=Mathf.Min(hi,t);
        return lo<=hi;
    }
    internal static bool TriangleProfile(Vector3 a,Vector3 b,List<(Vector3 a,Vector3 b,Vector3 c)> faces,List<Vector3> output){
        var start=XZ(a);var delta=XZ(b)-start;
        if(delta.sqrMagnitude<.000001f)return false;
        var spans=new List<Span>();
        foreach(var face in faces){
            var e=XZ(face.b-face.a);var f=XZ(face.c-face.a);var w=start-XZ(face.a);
            float det=Cross(e,f);if(Mathf.Abs(det)<.000001f)continue;
            float u=Cross(w,f)/det,v=Cross(e,w)/det,du=Cross(delta,f)/det,dv=Cross(e,delta)/det;
            float lo=0,hi=1;
            if(!Clip(u,du,ref lo,ref hi)||!Clip(v,dv,ref lo,ref hi)||!Clip(1-u-v,-du-dv,ref lo,ref hi)||hi-lo<.000001f)continue;
            spans.Add(new Span{Lo=lo,Hi=hi,Y0=face.a.y+u*(face.b.y-face.a.y)+v*(face.c.y-face.a.y),DY=du*(face.b.y-face.a.y)+dv*(face.c.y-face.a.y)});
        }
        float t=0,y=a.y;var local=new List<Vector3>{a};
        // Follow a continuous height surface, not the highest surface on the ray.
        // This rejects gaps and overlapping floors that are not joined.
        for(int step=0;t<.999999f&&step<=spans.Count;step++){
            int best=-1;float far=t;
            for(int i=0;i<spans.Count;i++){
                var span=spans[i];
                if(span.Lo>t+.00002f||span.Hi<=far+.000001f||Mathf.Abs(span.Y(t)-y)>.12f)continue;
                best=i;far=span.Hi;
            }
            if(best<0)return false;
            var chosen=spans[best];t=Mathf.Min(1,far);y=chosen.Y(t);
            var pos=start+delta*t;Append(local,new Vector3(pos.x,y,pos.y));
        }
        if(t<.999999f||Mathf.Abs(y-b.y)>.12f)return false;
        local[local.Count-1]=b;
        Simplify(local);
        foreach(var p in local)Append(output,p);
        return true;
    }
    static void Simplify(List<Vector3> local){
        for(int i=local.Count-2;i>0;i--){
            var d=local[i+1]-local[i-1];
            if(d.sqrMagnitude<.000001f)continue;
            var q=local[i-1]+d*Mathf.Clamp01(Vector3.Dot(local[i]-local[i-1],d)/d.sqrMagnitude);
            if((q-local[i]).sqrMagnitude<.015f*.015f)local.RemoveAt(i);
        }
    }
    static List<(Vector3,Vector3,Vector3)> Faces(List<GraphNode> nodes){
        var faces=new List<(Vector3,Vector3,Vector3)>();
        foreach(var n in nodes)if(n is TriangleMeshNode tri)
            faces.Add(((Vector3)tri.GetVertex(0),(Vector3)tri.GetVertex(1),(Vector3)tri.GetVertex(2)));
        return faces;
    }
    internal static bool Level(List<GraphNode> nodes,float height){
        foreach(var n in nodes){
            if(!(n is TriangleMeshNode tri))return false;
            for(int i=0;i<3;i++)if(Mathf.Abs(((Vector3)tri.GetVertex(i)).y-height)>.06f)return false;
        }
        return nodes.Count>0;
    }
    internal static bool Trace(Vector3 a,Vector3 b,NavGraph graph,List<Vector3> output,List<GraphNode> corridor=null){
        if((a-b).sqrMagnitude<.000001f){Append(output,a);return true;}
        if(!(graph is IRaycastableGraph ray))return false;
        var n=graph.GetNearest(a,NNConstraint.Default);
        if(n.node==null||Vector3.Distance(n.position,a)>.25f)return false;
        var nodes=new List<GraphNode>();
        bool blocked=ray.Linecast(n.position,b,n.node,out var hit,nodes);
        if(!blocked&&Level(nodes,a.y)&&Mathf.Abs(a.y-b.y)<.06f){
            Append(output,a);Append(output,b);return true;
        }
        if(!blocked&&TriangleProfile(a,b,Faces(nodes),output))return true;
        // The full corridor also resolves boundary ambiguity at funnel corners.
        return corridor!=null&&TriangleProfile(a,b,Faces(corridor),output);
    }
    internal static bool Conform(List<Vector3> corners,List<GraphNode> corridor,out List<Vector3> result){
        result=new List<Vector3>();
        if(corners.Count<2||corridor==null||corridor.Count==0)return false;
        var graph=corridor[0].Graph;
        foreach(var n in corridor)if(!(n is TriangleMeshNode)||n.Graph!=graph)return false;
        if(Level(corridor,corners[0].y)){result.AddRange(corners);return true;}
        for(int i=1;i<corners.Count;i++)if(!Trace(corners[i-1],corners[i],graph,result,corridor))return false;
        return result.Count>=2;
    }
    // The collision surface may differ slightly from the navmesh (stair treads).
    // Refine only near height changes and only against known floor colliders.
    internal static List<Vector3> Treads(List<Vector3> input){
        var output=new List<Vector3>();
        for(int i=1;i<input.Count;i++){
            var a=input[i-1];var b=input[i];Append(output,a);
            float length=Vector3.Distance(a,b);
            if(Mathf.Abs(a.y-b.y)<.06f||length>40||length<.15f)continue;
            int steps=Mathf.Min(160,Mathf.CeilToInt(length/.25f));
            for(int j=1;j<steps;j++){
                var p=Vector3.Lerp(a,b,j/(float)steps);
                float error=.45f;float height=p.y;
                foreach(var h in Physics.RaycastAll(p+Vector3.up*.5f,Vector3.down,1f,Physics.DefaultRaycastLayers,QueryTriggerInteraction.Ignore)){
                    if(h.normal.y<.55f||!ElevationMap.WalkSurface(h.collider))continue;
                    float e=Mathf.Abs(h.point.y-p.y);
                    if(e<error){error=e;height=h.point.y;}
                }
                p.y=height;Append(output,p);
            }
        }
        if(input.Count>0)Append(output,input[input.Count-1]);Simplify(output);return output;
    }
}}
