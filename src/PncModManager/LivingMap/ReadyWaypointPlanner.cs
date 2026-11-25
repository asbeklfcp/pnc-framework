using System;
using System.Collections.Generic;
using UnityEngine;
using Pathfinding;
using DunGen;
namespace PncPrivateLivingMap {
internal static class ReadyWaypointPlanner {
    internal sealed class Plan {
        internal readonly List<Vector3> Points=new List<Vector3>();
        internal int WorldCount, DoorLinks, LockedLinks;
        internal bool NextLocked;
        internal float Length;
    }
    sealed class Link {
        internal GraphNode To;
        internal Vector3 FromPoint,ToPoint,Door;
        internal bool Virtual,Locked;
        internal float Cost;
    }
    static Vector3 Center(GraphNode n)=>(Vector3)n.position;
    static void Add(Dictionary<GraphNode,List<Link>> links,GraphNode from,Link edge){
        if(!links.TryGetValue(from,out var list))links[from]=list=new List<Link>();
        list.Add(edge);
    }
    static bool Inside(Room room,Vector3 p){
        var b=room.WorldBounds;
        return p.x>=b.min.x-.1f&&p.x<=b.max.x+.1f&&p.z>=b.min.z-.1f&&p.z<=b.max.z+.1f&&p.y>=b.min.y-1.3f&&p.y<=b.max.y+.3f;
    }
    static GraphNode DoorNode(MapModel model,Room room,Vector3 door,List<GraphNode> nodes,out Vector3 point){
        GraphNode best=null;point=default;float score=64f;
        foreach(var n in nodes){
            var center=Center(n);
            if(!Inside(room,center)||Mathf.Abs(center.y-door.y)>3f)continue;
            var p=n.ClosestPointOnNode(door);
            float d=(p-door).sqrMagnitude;
            if(d>=score||Mathf.Abs(p.y-door.y)>2.5f)continue;
            // Verify ownership by an interior sample, not the shared doorway edge.
            if(model.LocateRoom(center)!=room)continue;
            best=n;point=p;score=d;
        }
        return best;
    }
    static void Append(List<Vector3> points,Vector3 p){
        if(points.Count==0||(points[points.Count-1]-p).sqrMagnitude>.000001f)points.Add(p);
    }
    static List<Vector3> Smooth(List<Vector3> points,IRaycastableGraph graph,GraphNode startNode,List<GraphNode> corridor){
        if(graph==null||points.Count<3)return points;
        var output=new List<Vector3>{points[0]};
        int i=0;
        while(i<points.Count-1){
            int next=i+1;
            // Only remove intermediate portal points after native linecast verifies the shortcut.
            var n=startNode.Graph.GetNearest(points[i],NNConstraint.Default);
            if(n.node!=null&&Vector3.Distance(n.position,points[i])<.3f)
                for(int j=Mathf.Min(points.Count-1,i+48);j>i+1;j--){
                    var profile=new List<Vector3>();
                    if(WaypointElevation.Level(corridor,points[i].y)&&Mathf.Abs(points[i].y-points[j].y)<.06f&&!graph.Linecast(n.position,points[j],n.node,out _)){next=j;break;}
                    if(WaypointElevation.Trace(points[i],points[j],startNode.Graph,profile,corridor)){
                        foreach(var p in profile)Append(output,p);next=j;break;
                    }
                }
            Append(output,points[next]);i=next;
        }
        return output;
    }
    internal static bool TryPlan(MapModel model,NNInfo start,NNInfo goal,out Plan plan){
        if(PlanCore(model,start,goal,out plan,false))return true;
        return PlanCore(model,start,goal,out plan,true);
    }
    static bool PlanCore(MapModel model,NNInfo start,NNInfo goal,out Plan plan,bool repair){
        plan=null;
        var astar=AstarPath.active;
        if(astar==null||astar.isScanning||astar.IsAnyGraphUpdateInProgress||start.node==null||goal.node==null)return false;
        var nodes=new List<GraphNode>();
        foreach(var graph in astar.graphs)if(graph!=null&&graph.isScanned)
            graph.GetNodes((Action<GraphNode>)(n=>{if(n.Walkable&&n is TriangleMeshNode)nodes.Add(n);}));
        if(nodes.Count==0||nodes.Count>40000||!(start.node is TriangleMeshNode)||!(goal.node is TriangleMeshNode))return false;
        var rooms=new Dictionary<int,Room>();
        foreach(var r in model.Rooms)if(r.Tile!=null&&r.Renderable)rooms[r.Key]=r;
        var links=new Dictionary<GraphNode,List<Link>>();
        var pairs=new HashSet<int>();
        int bridges=0;
        foreach(var r in rooms.Values)
            foreach(var door in r.Tile.AllDoorways){
                if(door==null||door.ConnectedDoorway==null)continue;
                var other=door.ConnectedDoorway;
                if(other.Tile==null||!rooms.TryGetValue(other.Tile.GetInstanceID(),out var dest))continue;
                if(!pairs.Add(door.GetInstanceID()))continue;
                pairs.Add(other.GetInstanceID());
                var a=door.transform.position;var b=other.transform.position;
                if(Vector3.Distance(a,b)>3f)continue;
                var n=DoorNode(model,r,a,nodes,out var pa);
                var m=DoorNode(model,dest,b,nodes,out var pb);
                if(n==null||m==null||n==m||n.Area==m.Area&&n.Graph==m.Graph)continue;
                if(Vector3.Distance(pa,a)>8f||Vector3.Distance(pb,b)>8f||Mathf.Abs(pa.y-pb.y)>3f)continue;
                bool locked=door.IsLocked||other.IsLocked;
                var mid=(a+b)*.5f;mid.y=(pa.y+pb.y)*.5f;
                // Do not charge a large fictitious crossing cost for an ordinary
                // physically supported doorway. Unverified doorway links still
                // carry a penalty and are labeled planned.
                bool supported=WaypointGuide.Supported(pa,mid)&&WaypointGuide.Supported(mid,pb);
                float cost=Vector3.Distance(Center(n),pa)+Vector3.Distance(pa,mid)+Vector3.Distance(mid,pb)+Vector3.Distance(pb,Center(m))+(supported?0:15)+(locked?100:0);
                Add(links,n,new Link{To=m,Virtual=true,Locked=locked,FromPoint=pa,ToPoint=pb,Door=mid,Cost=cost});
                Add(links,m,new Link{To=n,Virtual=true,Locked=locked,FromPoint=pb,ToPoint=pa,Door=mid,Cost=cost});
                bridges++;
            }
        if(repair){
            var repaired=LocalNavRepair.Find(nodes,WaypointGuide.Supported,384,out int tested);
            foreach(var e in repaired){
                var mid=(e.P+e.Q)*.5f;
                float cost=Vector3.Distance(Center(e.A),e.P)+e.Length+Vector3.Distance(e.Q,Center(e.B))+.01f;
                Add(links,e.A,new Link{To=e.B,Virtual=true,FromPoint=e.P,ToPoint=e.Q,Door=mid,Cost=cost});
                Add(links,e.B,new Link{To=e.A,Virtual=true,FromPoint=e.Q,ToPoint=e.P,Door=mid,Cost=cost});
                bridges++;
            }
            Debug.Log("[PNC WAYPOINT] Local repair tested="+tested+" accepted="+repaired.Count);
        }
        var distance=new Dictionary<GraphNode,float>{{start.node,0}};
        var parent=new Dictionary<GraphNode,(GraphNode from,Link edge)>();
        var queue=new SortedSet<(float score,uint id,GraphNode node)>(Comparer<(float score,uint id,GraphNode node)>.Create((a,b)=>{
            int c=a.score.CompareTo(b.score);return c!=0?c:a.id.CompareTo(b.id);
        }));
        queue.Add((0,start.node.NodeIndex,start.node));
        int expanded=0;
        while(queue.Count>0&&expanded++<50000){
            var item=queue.Min;queue.Remove(item);
            var n=item.node;
            if(!distance.TryGetValue(n,out var current)||item.score!=current)continue;
            if(n==goal.node)break;
            void Relax(Link edge){
                var to=edge.To;float score=current+edge.Cost;
                if(distance.TryGetValue(to,out var old)&&old<=score)return;
                distance[to]=score;parent[to]=(n,edge);queue.Add((score,to.NodeIndex,to));
            }
            n.GetConnections(to=>{
                if(to==null||!to.Walkable||!(to is TriangleMeshNode)||to.Graph!=n.Graph)return;
                if(!n.GetPortal(to,out var left,out var right))return;
                var p=(left+right)*.5f;
                Relax(new Link{To=to,FromPoint=p,ToPoint=p,Cost=Vector3.Distance(Center(n),p)+Vector3.Distance(p,Center(to))+.001f});
            },32);
            if(links.TryGetValue(n,out var extra))foreach(var edge in extra)Relax(edge);
        }
        if(!distance.ContainsKey(goal.node)){Debug.Log("[PNC WAYPOINT] Ready planner no route; bridges="+bridges+" areas="+start.node.Area+"/"+goal.node.Area+" expanded="+expanded);return false;}
        var chain=new List<(GraphNode from,Link edge)>();
        for(var n=goal.node;n!=start.node;){
            if(!parent.TryGetValue(n,out var entry)||chain.Count>nodes.Count)return false;
            chain.Add(entry);n=entry.from;
        }
        chain.Reverse();
        var result=new Plan();var section=new List<Vector3>{start.position};var sectionNode=start.node;var sectionNodes=new List<GraphNode>{start.node};
        void Flush(){
            foreach(var p in Smooth(section,sectionNode.Graph as IRaycastableGraph,sectionNode,sectionNodes))Append(result.Points,p);
            section.Clear();sectionNodes.Clear();
        }
        foreach(var entry in chain){
            var e=entry.edge;
            Append(section,e.FromPoint);
            if(!e.Virtual){sectionNodes.Add(e.To);continue;}
            Flush();
            if(result.DoorLinks==0){result.WorldCount=result.Points.Count;result.NextLocked=e.Locked;}
            result.DoorLinks++;if(e.Locked)result.LockedLinks++;
            Append(result.Points,e.Door);Append(result.Points,e.ToPoint);
            section.Add(e.ToPoint);sectionNode=e.To;sectionNodes.Add(e.To);
        }
        Append(section,goal.position);Flush();
        if(result.DoorLinks==0)result.WorldCount=result.Points.Count;
        if(result.Points.Count<2||Vector3.Distance(result.Points[result.Points.Count-1],goal.position)>.1f)return false;
        for(int i=1;i<result.Points.Count;i++)result.Length+=Vector3.Distance(result.Points[i-1],result.Points[i]);
        Debug.Log("[PNC WAYPOINT] Ready plan length="+result.Length.ToString("0.0")+" bridges="+result.DoorLinks+"/"+bridges+" locked="+result.LockedLinks+" guide="+result.WorldCount+" points="+result.Points.Count+" end="+goal.position.ToString("F3"));
        plan=result;return true;
    }
}}
