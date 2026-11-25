using System;
using System.Collections.Generic;
using UnityEngine;
using Pathfinding;
namespace PncPrivateLivingMap {
    // Value-only snapshot: no retained GraphNode/graph handles or transient Area IDs.
    // It is a fallback, not a replacement for live obstacle and doorway checks.
    internal sealed class CapturedNav {
        internal sealed class Face {
            internal Vector3 A,B,C;
            internal Vector3 Center=default;
            internal readonly List<Edge> Edges=new List<Edge>();
        }
        internal struct Edge {
            internal int To;
            internal Vector3 Exit,Entry;
            internal bool Repair;
            internal float Cost;
        }
        internal readonly List<Face> Faces=new List<Face>();
        internal int Generation;
        internal int RepairCount;
        static void Add(Face a,Face b,int to,Vector3 p,Vector3 q,bool repair){
            a.Edges.Add(new Edge{To=to,Exit=p,Entry=q,Repair=repair,
                Cost=Vector3.Distance(a.Center,p)+Vector3.Distance(p,q)+Vector3.Distance(q,b.Center)+.001f});
        }
        internal static CapturedNav Capture(MapModel model) {
            var astar=AstarPath.active;
            if(astar==null||astar.isScanning||astar.IsAnyGraphUpdateInProgress)return null;
            var sw=System.Diagnostics.Stopwatch.StartNew();
            var nodes=new List<GraphNode>();
            foreach(var graph in astar.graphs)if(graph!=null&&graph.isScanned)
                graph.GetNodes((Action<GraphNode>)(n=>{if(n.Walkable&&n is TriangleMeshNode)nodes.Add(n);}));
            if(nodes.Count==0||nodes.Count>40000)return null;
            var snapshot=new CapturedNav{Generation=model.DungeonGeneration};
            var ids=new Dictionary<GraphNode,int>(nodes.Count);
            foreach(var n in nodes){
                var tri=(TriangleMeshNode)n;
                var a=(Vector3)tri.GetVertex(0);var b=(Vector3)tri.GetVertex(1);var c=(Vector3)tri.GetVertex(2);
                ids[n]=snapshot.Faces.Count;
                snapshot.Faces.Add(new Face{A=a,B=b,C=c,Center=(a+b+c)/3});
            }
            foreach(var n in nodes){
                int id=ids[n];var face=snapshot.Faces[id];
                n.GetConnections(to=>{
                    if(to==null||to.Graph!=n.Graph||!ids.TryGetValue(to,out int k)||!n.GetPortal(to,out var l,out var rr))return;
                    var mid=(l+rr)*.5f;Add(face,snapshot.Faces[k],k,mid,mid,false);
                },32);
            }
            // Use the same bounded physical gap checks as the accepted live planner.
            var repairs=LocalNavRepair.Find(nodes,WaypointGuide.Supported,384,out int tested);
            foreach(var e in repairs){
                int a=ids[e.A],b=ids[e.B];
                Add(snapshot.Faces[a],snapshot.Faces[b],b,e.P,e.Q,true);
                Add(snapshot.Faces[b],snapshot.Faces[a],a,e.Q,e.P,true);
            }
            snapshot.RepairCount=repairs.Count;
            Debug.Log("[PNC WAYPOINT] Captured nav faces="+nodes.Count+" repairs="+repairs.Count+" tested="+tested+" ms="+sw.Elapsed.TotalMilliseconds.ToString("0.0"));
            return snapshot;
        }
        // Closest point on a 3-D triangle, preserving distinction between stacked floors.
        internal static Vector3 Closest(Vector3 p,Vector3 a,Vector3 b,Vector3 c) {
            var ab=b-a;var ac=c-a;var ap=p-a;float d1=Vector3.Dot(ab,ap),d2=Vector3.Dot(ac,ap);
            if(d1<=0&&d2<=0)return a;
            var bp=p-b;float d3=Vector3.Dot(ab,bp),d4=Vector3.Dot(ac,bp);
            if(d3>=0&&d4<=d3)return b;
            float vc=d1*d4-d3*d2;
            if(vc<=0&&d1>=0&&d3<=0)return a+ab*(d1/(d1-d3));
            var cp=p-c;float d5=Vector3.Dot(ab,cp),d6=Vector3.Dot(ac,cp);
            if(d6>=0&&d5<=d6)return c;
            float vb=d5*d2-d1*d6;
            if(vb<=0&&d2>=0&&d6<=0)return a+ac*(d2/(d2-d6));
            float va=d3*d6-d5*d4;
            if(va<=0&&d4-d3>=0&&d5-d6>=0)return b+(c-b)*((d4-d3)/(d4-d3+d5-d6));
            float sum=va+vb+vc;
            if(Mathf.Abs(sum)<.00000001f)return a;
            return a+ab*(vb/sum)+ac*(vc/sum);
        }
        internal int Nearest(Vector3 point,float tolerance,out Vector3 snapped) {
            int best=-1;float distance=tolerance*tolerance;snapped=default;
            for(int i=0;i<Faces.Count;i++){
                var f=Faces[i];var q=Closest(point,f.A,f.B,f.C);
                float score=(q-point).sqrMagnitude;
                if(score>=distance)continue;
                best=i;distance=score;snapped=q;
            }
            return best;
        }
        static bool NearSegment(Vector3 p,Vector3 a,Vector3 b,float radius){
            var d=b-a;
            var q=a+d*(d.sqrMagnitude>.000001f?Mathf.Clamp01(Vector3.Dot(p-a,d)/d.sqrMagnitude):0);
            return (q-p).sqrMagnitude<radius*radius;
        }
        internal bool TryRoute(Vector3 origin,Vector3 destination,IReadOnlyList<Vector3> locked,out List<Vector3> path){
            path=null;int start=Nearest(origin,2.5f,out var a),goal=Nearest(destination,.8f,out var b);
            if(start<0||goal<0)return false;
            var dist=new float[Faces.Count];var parent=new int[Faces.Count];var via=new Edge[Faces.Count];
            for(int i=0;i<dist.Length;i++){dist[i]=float.PositiveInfinity;parent[i]=-1;}
            var queue=new SortedSet<(float score,int id)>();
            dist[start]=0;queue.Add((Vector3.Distance(Faces[start].Center,Faces[goal].Center),start));
            while(queue.Count>0){
                var item=queue.Min;queue.Remove(item);int id=item.id;
                var f=Faces[id];
                float h=Vector3.Distance(f.Center,Faces[goal].Center);
                if(item.score>dist[id]+h+.001f)continue;
                if(id==goal)break;
                foreach(var e in f.Edges){
                    var other=Faces[e.To];bool blocked=false;
                    foreach(var p in locked)if(NearSegment(p,f.Center,e.Exit,1.4f)||NearSegment(p,e.Exit,e.Entry,1.4f)||NearSegment(p,e.Entry,other.Center,1.4f)){blocked=true;break;}
                    if(blocked)continue;
                    float next=dist[id]+e.Cost;if(next>=dist[e.To])continue;
                    dist[e.To]=next;parent[e.To]=id;via[e.To]=e;
                    queue.Add((next+Vector3.Distance(other.Center,Faces[goal].Center),e.To));
                }
            }
            if(float.IsInfinity(dist[goal]))return false;
            var chain=new List<int>();
            for(int id=goal;id!=start;id=parent[id]){if(id<0||chain.Count>Faces.Count)return false;chain.Add(id);}
            chain.Reverse();
            var result=new List<Vector3>{a};
            var section=new List<Vector3>{a};var corridor=new List<(Vector3,Vector3,Vector3)>();
            void FaceIn(int id){var f=Faces[id];corridor.Add((f.A,f.B,f.C));}
            FaceIn(start);
            void Flush(){
                int i=0;
                while(i<section.Count-1){
                    int next=i+1;
                    for(int j=Mathf.Min(section.Count-1,i+24);corridor.Count<=512&&j>i+1;j--){
                        var profile=new List<Vector3>();
                        if(!WaypointElevation.TriangleProfile(section[i],section[j],corridor,profile))continue;
                        foreach(var p in profile)WaypointElevation.Append(result,p);
                        next=j;break;
                    }
                    WaypointElevation.Append(result,section[next]);i=next;
                }
                section.Clear();corridor.Clear();
            }
            foreach(int id in chain){
                var e=via[id];WaypointElevation.Append(section,e.Exit);
                if(e.Repair){Flush();WaypointElevation.Append(result,e.Entry);section.Add(e.Entry);}
                FaceIn(id);
            }
            WaypointElevation.Append(section,b);Flush();
            if(result.Count<2)return false;
            // Reject shortcuts through a newly locked doorway as well as search edges.
            foreach(var p in locked)for(int i=1;i<result.Count;i++)if(NearSegment(p,result[i-1],result[i],1.4f))return false;
            path=result;return true;
        }
    }
}
