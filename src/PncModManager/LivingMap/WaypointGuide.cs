using System;
using System.Collections.Generic;
using UnityEngine;
using Pathfinding;
using UnityEngine.Rendering;
using Object=UnityEngine.Object;
namespace PncPrivateLivingMap {
internal sealed class WaypointGuide {
    internal bool Active {get;private set;}
    internal Vector3 Destination {get;private set;}
    internal string Status {get;private set;}="Shift-click a floor to set waypoint";
    internal float Distance {get;private set;}
    internal readonly List<Vector3> Points=new List<Vector3>();
    internal static readonly Color Tint=new Color(.22f,.94f,.87f,1);
    int requestVersion; bool pending; Vector3 lastStart;
    LineRenderer world; Material material,mapInk; Mesh mesh;
    FunnelModifier funnel;
    readonly List<int> indices=new List<int>();
    float next; int generation=-1;
    int verifiedPrefix; bool planned;
    float lastSuccessfulRoute;
    bool hasLockedCrossing;
    Vector3 lastProgressPosition; float nextFullPlan;
    internal Renderer WorldRenderer=>world;
    CapturedNav snapshot;
    readonly GuidanceArt art=new GuidanceArt();
    int artRevision;
    internal BepInEx.Configuration.ConfigEntry<bool> Motion,DestinationHud;
    internal Renderer PulseRenderer=>art.Renderer;
    GUIStyle destinationStyle;
    Texture2D destinationDiamond;
    // Font-independent, antialiased hollow diamond. Build once per guide.
    internal static float DiamondAlpha(float x,float y){
        float d=Mathf.Abs(x)+Mathf.Abs(y);
        return Mathf.Clamp01((14f-d)*1.5f+.5f)*Mathf.Clamp01((d-10f)*1.5f+.5f);
    }
    Texture2D GetDestinationDiamond(){
        if(destinationDiamond!=null)return destinationDiamond;
        const int size=32;
        var pixels=new Color32[size*size];
        for(int y=0;y<size;y++)for(int x=0;x<size;x++){
            byte alpha=(byte)Mathf.RoundToInt(DiamondAlpha(x-15.5f,y-15.5f)*255);
            pixels[y*size+x]=new Color32(255,255,255,alpha);
        }
        destinationDiamond=new Texture2D(size,size,TextureFormat.RGBA32,false){
            name="PNC destination diamond",hideFlags=HideFlags.HideAndDontSave,
            filterMode=FilterMode.Bilinear,wrapMode=TextureWrapMode.Clamp
        };
        destinationDiamond.SetPixels32(pixels);
        destinationDiamond.Apply(false,true);
        return destinationDiamond;
    }
    internal string PinNotice {get;private set;}="";
    float pinNoticeUntil;
    internal string CurrentPinNotice=>Time.unscaledTime<pinNoticeUntil?PinNotice:"";
    void Notice(string text){PinNotice=text;pinNoticeUntil=Time.unscaledTime+2.5f;}
    internal void DrawDestinationHud(MapModel model){
        if(!Active||Points.Count<2||DestinationHud?.Value==false||Time.timeScale==0||Event.current.type!=EventType.Repaint)return;
        var camera=model.GameplayCamera;if(camera==null)return;
        var point=Destination+Vector3.up*1.1f;
        if(Vector3.Distance(model.Player3D,Destination)>22f||!ClearArrivalSegment(camera.transform.position,point))return;
        var q=camera.WorldToScreenPoint(point);
        if(q.z<=0||q.x<36||q.x>Screen.width-36||q.y<36||q.y>Screen.height-36)return;
        if(destinationStyle==null)destinationStyle=new GUIStyle(GUI.skin.label){alignment=TextAnchor.MiddleCenter,fontSize=15,fontStyle=FontStyle.Bold};
        var old=GUI.color;GUI.color=Tint;destinationStyle.normal.textColor=Color.white;
        float y=Screen.height-q.y;
        GUI.DrawTexture(new Rect(q.x-8,y-22,16,16),GetDestinationDiamond(),ScaleMode.StretchToFill,true);
        GUI.Label(new Rect(q.x-65,y,130,22),Mathf.CeilToInt(Distance)+" m",destinationStyle);
        GUI.color=old;
    }
    float nextCapture;
    internal sealed class SavedPin {
        internal Vector3 Point; internal string Name; internal int Id;
    }
    internal readonly List<SavedPin> Pins=new List<SavedPin>();
    int pinSerial;
    internal void ResetDungeon(){Clear();Pins.Clear();pinSerial=0;snapshot=null;nextCapture=0;PinNotice="";pinNoticeUntil=0;}
    internal bool SelectPin(MapModel model,SavedPin pin){
        if(pin==null||!Pins.Contains(pin)||!Walkable(model,pin.Point))return false;
        SetDestination(model,pin.Point);return true;
    }
    internal bool SavePin(MapModel model,Ray ray,float? floor){
        if(!TryPickPoint(model,ray,floor,out var point,out _))return false;
        foreach(var pin in Pins)if(Vector3.Distance(pin.Point,point)<1.5f){Notice(pin.Name+" already saved");return true;}
        if(Pins.Count>=24){Notice("24 saved pins - remove one first");return false;}
        var room=model.LocateRoom(point);
        string name=room?.Name??"";
        string low=name.ToLowerInvariant();
        string kind=low.Contains("shop")?"Shop":low.Contains("end room")||low.Contains("exit")?"Exit":low.Contains("boss")?"Boss":"Pin";
        int id=++pinSerial;
        Pins.Add(new SavedPin{Point=point,Name=kind+" "+id,Id=id});
        Notice("Saved "+kind+" "+id);return true;
    }
    internal void RemovePin(SavedPin pin){if(pin!=null&&Pins.Remove(pin))Notice("Removed "+pin.Name);}
    void EnsureSnapshot(MapModel model){
        if(snapshot!=null&&snapshot.Generation!=model.DungeonGeneration)snapshot=null;
        if(!model.ReadyMode||snapshot!=null||Time.unscaledTime<nextCapture)return;
        nextCapture=Time.unscaledTime+5f;
        try{snapshot=CapturedNav.Capture(model);}catch(Exception e){Debug.LogWarning("[PNC WAYPOINT] Snapshot unavailable: "+e.Message);}
    }
    bool TrySnapshot(MapModel model){
        if(!model.ReadyMode||snapshot==null||snapshot.Generation!=model.DungeonGeneration||!Walkable(model,Destination))return false;
        try{
            if(!Ground(model.Player3D,out var floor))return false;
            var locked=new List<Vector3>();
            foreach(var room in model.Rooms)if(room.Tile!=null)
                foreach(var door in room.Tile.AllDoorways)
                    if(door!=null&&door.IsLocked)locked.Add(door.transform.position);
            if(!snapshot.TryRoute(floor.point,Destination,locked,out var route))return false;
            if(!StableWaypoint.Trim(route,floor.point,3f,Supported,out var checkedRoute))return false;
            if(!AcceptReplacement(model,checkedRoute,false))return RetainSuffix(model);
            Points.Clear();foreach(var p in checkedRoute)Points.Add(p+Vector3.up*.18f);
            Distance=StableWaypoint.Length(checkedRoute);verifiedPrefix=0;planned=true;hasLockedCrossing=false;
            Status="Planned "+Distance.ToString("0")+" m | cached navigation";
            next=Time.unscaledTime+.65f;UpdateArt();
            Debug.Log("[PNC WAYPOINT] Snapshot route "+Distance.ToString("0.0")+" points="+Points.Count+" generation="+snapshot.Generation);
            return true;
        }catch(Exception e){Debug.LogWarning("[PNC WAYPOINT] Snapshot route unavailable: "+e.Message);return false;}
    }
    bool Walkable(MapModel model,Vector3 point){
        var room=model.LocateRoom(point);
        return room!=null&&room.Renderable;
    }
    static bool Hit(Ray ray,Vector3 a,Vector3 b,Vector3 c,out float distance){
        distance=0; var e1=b-a;var e2=c-a;var p=Vector3.Cross(ray.direction,e2);
        float det=Vector3.Dot(e1,p);if(Mathf.Abs(det)<.000001f)return false;
        float inv=1/det;var t=ray.origin-a;float u=Vector3.Dot(t,p)*inv;
        if(u<0||u>1)return false;
        var q=Vector3.Cross(t,e1);float v=Vector3.Dot(ray.direction,q)*inv;
        if(v<0||u+v>1)return false;
        distance=Vector3.Dot(e2,q)*inv;return distance>=0;
    }
    internal bool Pick(MapModel model,Ray ray,float? floor){
        if(!TryPickPoint(model,ray,floor,out var point,out _))return false;
        SetDestination(model,point);return true;
    }
    bool TryPickPoint(MapModel model,Ray ray,float? floor,out Vector3 point,out GraphNode pickedNode){
        point=default;pickedNode=null;
        var astar=AstarPath.active;
        if(astar==null||astar.isScanning||astar.IsAnyGraphUpdateInProgress){Status="Navigation graph not ready";return false;}
        float best=float.PositiveInfinity;Vector3 chosenPoint=default;int triangles=0;
        var origin=model.Player3D;
        if(Ground(origin,out var ground))origin=ground.point;
        var originNode=astar.GetNearest(origin,NNConstraint.Default).node;
        float connectedBest=float.PositiveInfinity;Vector3 connectedPoint=default;
        GraphNode chosenNode=null,connectedNode=null;
        foreach(var graph in astar.graphs){
            if(graph==null||!graph.isScanned)continue;
            graph.GetNodes((Action<GraphNode>)(node=>{
                if(!node.Walkable||!(node is TriangleMeshNode tri))return;
                triangles++;
                if(!Hit(ray,(Vector3)tri.GetVertex(0),(Vector3)tri.GetVertex(1),(Vector3)tri.GetVertex(2),out float d))return;
                var hit=ray.GetPoint(d);
                if(floor.HasValue&&Mathf.Abs(hit.y-floor.Value)>1.5f)return;
                if(!Walkable(model,hit))return;
                if(d<best){best=d;chosenPoint=hit;chosenNode=node;}
                if(!floor.HasValue&&originNode!=null&&node.Area==originNode.Area&&node.Graph==originNode.Graph&&d<connectedBest){
                    connectedBest=d;connectedPoint=hit;connectedNode=node;
                }
            }));
        }
        // All-layer overview can expose a roof/pit triangle directly above
        // the intended walkable floor. Prefer an actual connected triangle on
        // the same picking ray, never an arbitrary nearby room.
        if(connectedNode!=null&&chosenNode!=connectedNode){
            Debug.Log("[PNC WAYPOINT] All-layer pick preferred connected surface y="+connectedPoint.y.ToString("F2")+" over y="+chosenPoint.y.ToString("F2"));
            best=connectedBest;chosenPoint=connectedPoint;chosenNode=connectedNode;
        }
        // A graph need not use triangles. For those graphs pick a real floor collider
        // and snap only to a nearby walkable graph position.
        if(float.IsInfinity(best)){
            foreach(var hit in Physics.RaycastAll(ray,2000f,Physics.DefaultRaycastLayers,QueryTriggerInteraction.Ignore)){
                if(hit.normal.y<.5f||hit.distance>=best||!Walkable(model,hit.point))continue;
                if(floor.HasValue&&Mathf.Abs(hit.point.y-floor.Value)>1.5f)continue;
                var n=astar.GetNearest(hit.point,NNConstraint.Default);
                if(n.node==null||!n.node.Walkable||Vector3.Distance(n.position,hit.point)>1.5f)continue;
                best=hit.distance;chosenPoint=n.position;chosenNode=n.node;
            }
        }
        Debug.Log("[PNC WAYPOINT] Pick triangles="+triangles+" hit="+!float.IsInfinity(best));
        if(float.IsInfinity(best)){Status="No walkable floor here";return false;}
        point=chosenPoint;pickedNode=chosenNode;return true;
    }
    void SetDestination(MapModel model,Vector3 point){
        requestVersion++;pending=false;Points.Clear();Distance=0;planned=false;verifiedPrefix=0;hasLockedCrossing=false;lastSuccessfulRoute=0;Active=true;Destination=point;generation=model.DungeonGeneration;next=0;nextFullPlan=0;nextArrivalCheck=0;
        Debug.Log("[PNC WAYPOINT] Target "+point.ToString("F3"));art.Reset();UpdateArt();EnsureSnapshot(model);Refresh(model);
    }
    internal void Tick(MapModel model,bool visible,bool expanded){
        if(!visible||!model.HasPlayer){ShowWorld(false);return;}
        if(Active&&generation!=model.DungeonGeneration){ResetDungeon();return;}
        if(Active&&Reached(model)){Clear();Status="Waypoint reached";Debug.Log("[PNC WAYPOINT] Arrived; route cleared");return;}
        if(Active&&Time.unscaledTime>=next)Refresh(model);
        ShowWorld(Active&&!expanded&&Points.Count>=2&&Time.timeScale>0);
    }
    // Use actual horizontal proximity, not just two positions snapped to the same node.
    // Require a short, unobstructed graph segment to reject nearby points across walls/floors.
    float nextArrivalCheck;
    static bool PlayerCollider(Collider c){
        return c!=null&&(c.GetComponentInParent<FirstPersonController>()!=null||c.GetComponentInParent<PlayerStats>()!=null);
    }
    static readonly RaycastHit[] navHits=new RaycastHit[64];
    static RaycastHit[] QueryHits(Vector3 origin,Vector3 direction,float length,out int count){
        count=Physics.RaycastNonAlloc(origin,direction,navHits,length,Physics.DefaultRaycastLayers,QueryTriggerInteraction.Ignore);
        if(count<navHits.Length)return navHits;
        var complete=Physics.RaycastAll(origin,direction,length,Physics.DefaultRaycastLayers,QueryTriggerInteraction.Ignore);
        count=complete.Length;return complete;
    }
    static bool Ground(Vector3 point,out RaycastHit support){
        support=default;float nearest=float.PositiveInfinity;
        var hits=QueryHits(point+Vector3.up*1.8f,Vector3.down,4f,out int count);
        for(int i=0;i<count;i++){var hit=hits[i];
            if(PlayerCollider(hit.collider)||hit.distance>=nearest)continue;
            nearest=hit.distance;support=hit;
        }
        return !float.IsInfinity(nearest)&&support.normal.y>.55f;
    }
    static bool ClearArrivalSegment(Vector3 a,Vector3 b){
        var d=b-a;
        if(d.sqrMagnitude<.0001f)return true;
        var hits=QueryHits(a,d.normalized,d.magnitude,out int count);
        for(int i=0;i<count;i++)if(!PlayerCollider(hits[i].collider))return false;
        return true;
    }
    bool Reached(MapModel model){
        var player=model.Player3D;var delta=Destination-player;
        float horizontal=new Vector2(delta.x,delta.z).magnitude;
        if(horizontal>2.5f||Mathf.Abs(delta.y)>3f)return false;
        if(Time.unscaledTime<nextArrivalCheck)return false;
        nextArrivalCheck=Time.unscaledTime+.10f;
        if(!Ground(player,out var from)||!Ground(Destination,out var to)||
            Mathf.Abs(from.point.y-to.point.y)>.8f||Mathf.Abs(to.point.y-Destination.y)>1.15f)return false;
        if(!ClearArrivalSegment(from.point+Vector3.up*.35f,to.point+Vector3.up*.35f)||
           !ClearArrivalSegment(from.point+Vector3.up*1.1f,to.point+Vector3.up*1.1f))return false;
        Debug.Log("[PNC WAYPOINT] Arrival distance="+horizontal.ToString("0.00")+" floors="+from.point.y.ToString("0.00")+"/"+to.point.y.ToString("0.00"));
        return true;
    }
    // Center samples only in narrow, bounded, mostly level sections. Preserve
    // every original corner and endpoint; reject any new segment outside the graph.
    List<Vector3> CenterRoute(List<Vector3> corners,Pathfinding.Path result){
        if(result.path==null||result.path.Count<2)return corners;
        var graph=result.path[0].Graph as IRaycastableGraph;
        if(graph==null)return corners;
        foreach(var n in result.path)if(!(n is TriangleMeshNode)||n.Graph!=result.path[0].Graph)return corners;
        var astar=AstarPath.active;if(astar==null)return corners;
        var output=new List<Vector3>(corners.Count*2){corners[0]};
        int shifted=0;float originalLength=0,newLength=0;
        for(int i=1;i<corners.Count;i++){
            var a=corners[i-1];var b=corners[i];var d=b-a;float length=d.magnitude;originalLength+=length;
            if(length>3f&&Mathf.Abs(d.y)<length*.12f){
                var side=Vector3.Cross(Vector3.up,d).normalized;
                int count=Mathf.Min(48,Mathf.CeilToInt(length/2.5f));
                for(int j=1;j<count;j++){
                    var at=Vector3.Lerp(a,b,j/(float)count);
                    var nearest=astar.GetNearest(at,NNConstraint.Default);
                    if(nearest.node==null||nearest.node.Graph!=result.path[0].Graph||Vector3.Distance(nearest.position,at)>.25f)continue;
                    var p=nearest.position;
                    if(!graph.Linecast(p,p+side*3.5f,nearest.node,out var right)||
                       !graph.Linecast(p,p-side*3.5f,nearest.node,out var left))continue;
                    float r=Vector3.Dot(right.point-p,side),l=Vector3.Dot(p-left.point,side);
                    if(r<0||l<0||r+l<.5f||r+l>6f)continue;
                    float shift=Mathf.Clamp((r-l)*.5f,-1.5f,1.5f);
                    if(Mathf.Abs(shift)<.12f)continue;
                    var wanted=p+side*shift;
                    var center=astar.GetNearest(wanted,NNConstraint.Default);
                    if(center.node==null||center.node.Graph!=result.path[0].Graph||Vector3.Distance(center.position,wanted)>.18f)continue;
                    output.Add(center.position);shifted++;
                }
            }
            output.Add(b);
        }
        if(shifted==0)return corners;
        for(int i=1;i<output.Count;i++){
            var a=output[i-1];var b=output[i];
            var n=astar.GetNearest(a,NNConstraint.Default);
            if(n.node==null||n.node.Graph!=result.path[0].Graph||Vector3.Distance(n.position,a)>.25f||
                graph.Linecast(n.position,b,n.node,out _))return corners;
            newLength+=Vector3.Distance(a,b);
        }
        if(newLength>originalLength*1.15f+1f)return corners;
        // Remove only nearly collinear samples when the replacement is still on the graph.
        for(int i=output.Count-2;i>0;i--){
            var a=output[i-1];var b=output[i+1];var d=b-a;
            if(d.sqrMagnitude<.0001f)continue;
            var near=a+d*Mathf.Clamp01(Vector3.Dot(output[i]-a,d)/d.sqrMagnitude);
            if(Vector3.Distance(near,output[i])>.12f)continue;
            var n=astar.GetNearest(a,NNConstraint.Default);
            if(n.node!=null&&n.node.Graph==result.path[0].Graph&&!graph.Linecast(n.position,b,n.node,out _))output.RemoveAt(i);
        }
        Debug.Log("[PNC WAYPOINT] Centered "+shifted+" samples; "+corners.Count+" -> "+output.Count+" points");
        return output;
    }
    bool TryReadyPlan(MapModel model,NNInfo start,NNInfo goal){
        if(!model.ReadyMode)return false;
        try{
            if(!ReadyWaypointPlanner.TryPlan(model,start,goal,out var route))return false;
            if(Vector3.Distance(route.Points[route.Points.Count-1],Destination)>1.5f)return false;
            var profile=WaypointElevation.Treads(route.Points);
            if(!AcceptReplacement(model,profile,route.LockedLinks>0))return RetainSuffix(model);
            Points.Clear();foreach(var p in profile)Points.Add(p+Vector3.up*.18f);
            Debug.Log("[PNC WAYPOINT] Elevation fallback "+route.Points.Count+" -> "+Points.Count);
            Distance=StableWaypoint.Length(profile);verifiedPrefix=route.WorldCount;planned=route.DoorLinks>0;hasLockedCrossing=route.LockedLinks>0;lastSuccessfulRoute=Time.unscaledTime;
            Status=(planned?"Planned ":"Waypoint ")+Distance.ToString("0")+" m"+(hasLockedCrossing?" | locked door ahead":"");
            next=Time.unscaledTime+1.5f;UpdateArt();
            Debug.Log("[PNC WAYPOINT] Continuous guide world="+(world!=null?world.positionCount:0)+" map="+Points.Count+" nativePrefix="+verifiedPrefix+" doorwayLinks="+route.DoorLinks+" locked="+route.LockedLinks);
            return true;
        }catch(Exception e){Debug.LogWarning("[PNC WAYPOINT] Ready planner error "+e.Message);return false;}
    }
    // Retain a valid suffix, not a stale prefix back to the starting room.
    internal static bool Supported(Vector3 a,Vector3 b){
        float length=Vector3.Distance(a,b);
        if(length>18f)return false;
        int steps=Mathf.Max(1,Mathf.CeilToInt(length/.65f));
        for(int i=0;i<=steps;i++){
            var p=Vector3.Lerp(a,b,i/(float)steps);
            bool supported=false;
            var hits=QueryHits(p+Vector3.up*.65f,Vector3.down,1.3f,out int count);
            for(int j=0;j<count;j++){
                var h=hits[j];if(h.normal.y>.5f&&Mathf.Abs(h.point.y-p.y)<.48f&&ElevationMap.WalkSurface(h.collider)){supported=true;break;}
            }
            if(!supported)return false;
        }
        return ClearArrivalSegment(a+Vector3.up*.5f,b+Vector3.up*.5f)&&ClearArrivalSegment(a+Vector3.up*1.1f,b+Vector3.up*1.1f);
    }
    readonly List<Vector3> bareScratch=new List<Vector3>(),suffixScratch=new List<Vector3>();
    bool RetainSuffix(MapModel model){
        if(!Active||Points.Count<2||!Walkable(model,Destination)||
            Vector3.Distance(Points[Points.Count-1]-Vector3.up*.18f,Destination)>1.5f)return false;
        var player=model.Player3D;
        if(Ground(player,out var floor))player=floor.point;
        else return false;
        var bare=VisualInventory.GlobalFast?bareScratch:new List<Vector3>(Points.Count);bare.Clear();
        foreach(var p in Points)bare.Add(p-Vector3.up*.18f);
        float move=Vector3.Distance(player,lastProgressPosition);
        float budget=Mathf.Clamp(move*2+8,10,35);
        List<Vector3> suffix;
        if(!(VisualInventory.GlobalFast?StableWaypoint.TrimInto(bare,player,budget,Supported,suffixScratch,out suffix):StableWaypoint.Trim(bare,player,budget,Supported,out suffix)))return false;
        bool changed=suffix.Count!=Points.Count;
        if(!changed)for(int i=0;i<suffix.Count;i++)if((Points[i]-(suffix[i]+Vector3.up*.18f)).sqrMagnitude>.0004f){changed=true;break;}
        if(changed){
            // This is a validated suffix, so removed length is exactly forward
            // progress. Rebase animation phase to preserve world-space spacing.
            art.ShiftOrigin(StableWaypoint.Length(bare)-StableWaypoint.Length(suffix));
            Points.Clear();foreach(var p in suffix)Points.Add(p+Vector3.up*.18f);
        }
        Distance=StableWaypoint.Length(suffix);lastProgressPosition=player; Status=(planned||Status.Contains("checking ahead")?"Planned ":"Waypoint ")+Distance.ToString("0")+" m"+(Status.Contains("checking ahead")?" | checking ahead":"")+(hasLockedCrossing?" | locked door ahead":"");
        if(changed)UpdateArt();return true;
    }
    bool AcceptReplacement(MapModel model,List<Vector3> candidate,bool locked){
        if(candidate.Count<2||Vector3.Distance(candidate[candidate.Count-1],Destination)>1.5f)return false;
        var player=model.Player3D;
        if(Ground(player,out var floor))player=floor.point;
        if(Vector3.Distance(candidate[0],player)>.4f){
            if(!StableWaypoint.Trim(candidate,player,35,Supported,out var trimmed))return false;
            candidate.Clear();candidate.AddRange(trimmed);
        }
        if(RetainSuffix(model)&&!StableWaypoint.PreferRoute(Distance,hasLockedCrossing,StableWaypoint.Length(candidate),locked)){
            Status=(planned?"Planned ":"Waypoint ")+Distance.ToString("0")+" m"+(hasLockedCrossing?" | locked door ahead":"");
            Debug.Log("[PNC WAYPOINT] Stable suffix retained "+Distance.ToString("0.0")+" vs "+StableWaypoint.Length(candidate).ToString("0.0"));
            return false;
        }
        lastProgressPosition=player;return true;
    }
    bool CanHoldRoute(MapModel model){
        return RetainSuffix(model);
    }
    void RouteUnavailable(MapModel model,string reason){
        if(CanHoldRoute(model)){
            Status="Planned "+Distance.ToString("0")+" m | checking ahead"+(hasLockedCrossing?" | locked door":"");
            return;
        }
        if(TrySnapshot(model))return;
        Points.Clear();Distance=0;verifiedPrefix=0;planned=false;hasLockedCrossing=false;
        Status=reason;UpdateArt();
    }
    void Refresh(MapModel model){
        if(pending)return;
        next=Time.unscaledTime+.65f;
        if(!Walkable(model,Destination)){Points.Clear();Distance=0;Status="Waypoint outside revealed map";UpdateArt();return;}
        bool progress=RetainSuffix(model);
        if(progress&&Time.unscaledTime<nextFullPlan)return;
        nextFullPlan=Time.unscaledTime+3f;
        var astar=AstarPath.active;
        if(astar==null||astar.isScanning||astar.IsAnyGraphUpdateInProgress){RouteUnavailable(model,"Navigation graph not ready");return;}
        var origin=model.Player3D;
        if(Ground(origin,out var floor))origin=floor.point;
        var start=astar.GetNearest(origin,NNConstraint.Default);
        var goal=astar.GetNearest(Destination,NNConstraint.Default);
        if(start.node==null||goal.node==null||!start.node.Walkable||!goal.node.Walkable||
            Vector3.Distance(start.position,origin)>3f||Vector3.Distance(goal.position,Destination)>1.5f){
            RouteUnavailable(model,"No complete route available");return;
        }
        if(Reached(model)){Clear();Status="Waypoint reached";return;}
        if(Points.Count>=2&&(lastStart-start.position).sqrMagnitude<.15f*.15f&&MapPause.Active)return;
        lastStart=start.position;
        if(start.node.Area!=goal.node.Area||start.node.Graph!=goal.node.Graph){
            Debug.Log("[PNC WAYPOINT] Disconnected native areas "+start.node.Area+"/"+goal.node.Area+" target="+Destination.ToString("F3"));
            if(TryReadyPlan(model,start,goal))return;
            RouteUnavailable(model,"No connected route available");next=Time.unscaledTime+1f;return;
        }
        int id=++requestVersion;pending=true;lastStart=start.position;
        if(Points.Count<2)Status="Calculating route...";
        var request=ABPath.Construct(start.position,goal.position,result=>{
            if(id!=requestVersion||!Active||generation!=model.DungeonGeneration)return;
            pending=false;
            if(result.error||result.CompleteState!=PathCompleteState.Complete||result.vectorPath==null||result.vectorPath.Count<2){
                Debug.Log("[PNC WAYPOINT] Native route failed "+result.errorLog);
                if(TryReadyPlan(model,start,goal))return;
                RouteUnavailable(model,"No complete route available");return;
            }
            if(Vector3.Distance(((ABPath)result).endPoint,Destination)>1.5f){
                Debug.Log("[PNC WAYPOINT] Native endpoint mismatch "+((ABPath)result).endPoint.ToString("F3")+" target="+Destination.ToString("F3"));
                if(TryReadyPlan(model,start,goal))return;
                RouteUnavailable(model,"No complete route to marker");return;
            }
            int rawCount=result.vectorPath.Count;
            var raw=new List<Vector3>(result.vectorPath);
            List<Vector3> corners=raw;
            try{
                if(funnel==null){
                    var host=new GameObject("PNC waypoint path smoothing"){hideFlags=HideFlags.HideAndDontSave};
                    host.SetActive(false);funnel=host.AddComponent<FunnelModifier>();
                }
                funnel.Apply(result);
                if(result.vectorPath!=null&&result.vectorPath.Count>=2)corners=result.vectorPath;
            }catch(Exception ex){Debug.LogWarning("[PNC WAYPOINT] Smoothing unavailable: "+ex.Message);}
            Debug.Log("[PNC WAYPOINT] Funnel "+rawCount+" -> "+corners.Count);
            try{corners=CenterRoute(corners,result);}catch(Exception ex){Debug.LogWarning("[PNC WAYPOINT] Centering fallback: "+ex.Message);}
            if(Vector3.Distance(corners[corners.Count-1],Destination)>1.5f){
                if(TryReadyPlan(model,start,goal))return;
                RouteUnavailable(model,"No complete route to marker");return;
            }
            if(WaypointElevation.Conform(corners,result.path,out var elevated)){
                Debug.Log("[PNC WAYPOINT] Elevation profile "+corners.Count+" -> "+elevated.Count);
                corners=WaypointElevation.Treads(elevated);
            }else {if(model.ReadyMode&&TryReadyPlan(model,start,goal))return;RouteUnavailable(model,"Checking elevation route");return;}
            Debug.Log("[PNC WAYPOINT] Verified endpoint "+corners[corners.Count-1].ToString("F3")+" target="+Destination.ToString("F3"));
            if(!AcceptReplacement(model,corners,false)){if(!RetainSuffix(model))RouteUnavailable(model,"Route changed; recalculating");return;}
            Points.Clear();Distance=0;verifiedPrefix=corners.Count;planned=false;hasLockedCrossing=false;lastSuccessfulRoute=Time.unscaledTime;
            for(int i=1;i<corners.Count;i++)Distance+=Vector3.Distance(corners[i-1],corners[i]);
            foreach(var point in corners)Points.Add(point+Vector3.up*.18f);
            Status="Waypoint  "+Distance.ToString("0")+" m";
            Debug.Log("[PNC WAYPOINT] Complete graph route corners="+Points.Count+" length="+Distance.ToString("0.0"));
            UpdateArt();
        });
        request.traversalConstraint.graphMask=GraphMask.FromGraph(start.node.Graph);
        try{AstarPath.StartPath(request);}catch(Exception ex){pending=false;RouteUnavailable(model,"Route unavailable");Debug.LogWarning("[PNC WAYPOINT] "+ex.Message);}
    }
    static Material Ink(CompareFunction depth){
        var shader=Shader.Find("Hidden/Internal-Colored");if(shader==null)return null;
        var m=new Material(shader);m.SetColor("_Color",Tint);m.SetInt("_ZTest",(int)depth);m.SetInt("_ZWrite",0);
        m.SetInt("_SrcBlend",(int)BlendMode.SrcAlpha);m.SetInt("_DstBlend",(int)BlendMode.OneMinusSrcAlpha);
        return m;
    }
    void UpdateArt(){
        artRevision++;
        if(mesh==null){mesh=new Mesh{name="PNC waypoint map route"};mesh.MarkDynamic();}
        mesh.Clear();indices.Clear();
        if(Points.Count>=2){
            for(int i=1;i<Points.Count;i++){indices.Add(i-1);indices.Add(i);}
            mesh.SetVertices(Points);mesh.SetIndices(indices,MeshTopology.Lines,0,true);
        }
        if(world==null&&Points.Count>=2){
            material=Ink(CompareFunction.LessEqual);
            if(material!=null){
                var host=new GameObject("PNC waypoint guidance"){hideFlags=HideFlags.HideAndDontSave};
                world=host.AddComponent<LineRenderer>();world.enabled=false;world.useWorldSpace=true;world.sharedMaterial=material;
                world.startWidth=.09f;world.endWidth=.09f;world.startColor=Color.white;world.endColor=Color.white;
                world.shadowCastingMode=ShadowCastingMode.Off;world.receiveShadows=false;
                world.numCornerVertices=3;world.numCapVertices=3;
            }
        }
        if(world!=null){int count=Points.Count;world.positionCount=count;for(int i=0;i<count;i++)world.SetPosition(i,Points[i]);if(count<2)world.enabled=false;}
    }
    void ShowWorld(bool value){
        if(world!=null&&world.enabled!=value)world.enabled=value;
        art.Tick(Points,value,Motion?.Value!=false,artRevision);
    }
    internal void Draw(CommandBuffer commands){
        if(!Active||Points.Count<2||mesh==null)return;
        if(mapInk==null)mapInk=Ink(CompareFunction.Always);
        if(mapInk!=null)commands.DrawMesh(mesh,Matrix4x4.identity,mapInk);
    }
    internal void Clear(){
        requestVersion++;pending=false;Active=false;Points.Clear();Distance=0;planned=false;verifiedPrefix=0;hasLockedCrossing=false;lastSuccessfulRoute=0;nextArrivalCheck=0;Status="Shift-click a floor to set waypoint";next=0;nextFullPlan=0;
        art.Reset();if(world!=null){world.enabled=false;world.positionCount=0;}
        if(mesh!=null)mesh.Clear();
    }
    internal void Dispose(){
        ResetDungeon();art.Dispose();if(destinationDiamond!=null)Object.Destroy(destinationDiamond);destinationDiamond=null;if(world!=null)Object.Destroy(world.gameObject);if(material!=null)Object.Destroy(material);
        if(mesh!=null)Object.Destroy(mesh);if(mapInk!=null)Object.Destroy(mapInk);
        if(funnel!=null)Object.Destroy(funnel.gameObject);funnel=null;
        world=null;material=null;mesh=null;mapInk=null;
    }
}}
