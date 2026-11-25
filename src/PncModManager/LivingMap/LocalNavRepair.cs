using System;
using System.Collections.Generic;
using UnityEngine;
using Pathfinding;
namespace PncPrivateLivingMap {
internal static class LocalNavRepair {
    internal struct Sample {
        internal GraphNode Node;
        internal Vector3 Point;
    }
    internal struct Crossing {
        internal GraphNode A,B;
        internal Vector3 P,Q;
        internal float Length;
    }
    // Match boundaries by actual vertex positions rather than transient area IDs.
    static (Int3,Int3) Key(Int3 a,Int3 b){
        bool swap=a.x>b.x||a.x==b.x&&(a.y>b.y||a.y==b.y&&a.z>b.z);
        return swap?(b,a):(a,b);
    }
    static (int,int,int) Cell(Vector3 p,float size)=>
        (Mathf.FloorToInt(p.x/size),Mathf.FloorToInt(p.y/size),Mathf.FloorToInt(p.z/size));
    internal static List<Crossing> Find(List<GraphNode> nodes,Func<Vector3,Vector3,bool> support,int budget,out int tested){
        const float range=2.25f;
        tested=0;
        var boundaries=new Dictionary<(Int3,Int3),List<GraphNode>>();
        foreach(var n in nodes){
            if(!(n is TriangleMeshNode t))continue;
            for(int i=0;i<3;i++){
                var key=Key(t.GetVertex(i),t.GetVertex((i+1)%3));
                if(!boundaries.TryGetValue(key,out var owners))boundaries[key]=owners=new List<GraphNode>();
                owners.Add(n);
            }
        }
        var samples=new List<Sample>();
        foreach(var pair in boundaries){
            var owners=pair.Value;
            bool internalEdge=owners.Count>1;
            foreach(var n in owners)if(n.Area!=owners[0].Area||n.Graph!=owners[0].Graph)internalEdge=false;
            if(internalEdge)continue;
            var a=(Vector3)pair.Key.Item1;var b=(Vector3)pair.Key.Item2;
            int steps=Mathf.Clamp(Mathf.CeilToInt(Vector3.Distance(a,b)/1.25f),1,24);
            foreach(var n in owners)
                for(int i=0;i<=steps;i++)samples.Add(new Sample{Node=n,Point=Vector3.Lerp(a,b,i/(float)steps)});
        }
        var bins=new Dictionary<(int,int,int),List<Sample>>();
        var candidates=new Dictionary<(uint,uint,int,int,int),Crossing>();
        foreach(var s in samples){
            var c=Cell(s.Point,range);
            for(int x=-1;x<=1;x++)for(int y=-1;y<=1;y++)for(int z=-1;z<=1;z++){
                if(!bins.TryGetValue((c.Item1+x,c.Item2+y,c.Item3+z),out var near))continue;
                foreach(var q in near){
                    if(s.Node==q.Node||s.Node.Area==q.Node.Area&&s.Node.Graph==q.Node.Graph)continue;
                    float len=Vector3.Distance(s.Point,q.Point);
                    if(len>range||Mathf.Abs(s.Point.y-q.Point.y)>.65f)continue;
                    // Keep separate crossings along an area boundary, not a single
                    // arbitrary bridge for the whole pair of connected components.
                    var mid=Cell((s.Point+q.Point)*.5f,3);
                    uint a=s.Node.Area,b=q.Node.Area;
                    var key=(Math.Min(a,b),Math.Max(a,b),mid.Item1,mid.Item2,mid.Item3);
                    if(candidates.TryGetValue(key,out var old)&&old.Length<=len)continue;
                    candidates[key]=new Crossing{A=s.Node,B=q.Node,P=s.Point,Q=q.Point,Length=len};
                }
            }
            if(!bins.TryGetValue(c,out var list))bins[c]=list=new List<Sample>();
            list.Add(s);
        }
        var sorted=new List<Crossing>(candidates.Values);
        sorted.Sort((a,b)=>a.Length.CompareTo(b.Length));
        var accepted=new List<Crossing>();
        foreach(var e in sorted){
            if(tested>=budget)break;tested++;
            // Probe slightly inside both triangles too, so coincident boundary
            // vertices cannot accidentally connect opposing sides of a wall.
            var a=Vector3.Lerp(e.P,(Vector3)e.A.position,.08f);
            var b=Vector3.Lerp(e.Q,(Vector3)e.B.position,.08f);
            if(!support(a,b)||!support(a,e.P)||!support(e.Q,b))continue;
            accepted.Add(e);
        }
        return accepted;
    }
}
}