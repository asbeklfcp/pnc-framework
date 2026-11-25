using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;

namespace PncPrivateLivingMap
{
    internal enum FloorView { Auto, FloorSelection, AllFloors, Cutaway, Focus }

    internal sealed class ElevationMap
    {
        internal sealed class Layer
        {
            internal int Id;
            internal float Height;
            internal string Name;
            internal readonly HashSet<int> Neighbors=new HashSet<int>();
        }
        sealed class Surface
        {
            internal Vector3 A,B,C;
            internal Bounds Bounds;
            internal Layer Layer;
            internal readonly List<Surface> Adjacent=new List<Surface>();
            internal bool Sloped;
            internal bool Sample(Vector3 p,out float height)
            {
                height=0;
                if(p.x<Bounds.min.x-.015f||p.x>Bounds.max.x+.015f||p.z<Bounds.min.z-.015f||p.z>Bounds.max.z+.015f)return false;
                var u=B-A;var v=C-A;var w=p-A;
                float det=u.x*v.z-u.z*v.x;
                if(Mathf.Abs(det)<.00001f)return false;
                float x=(w.x*v.z-w.z*v.x)/det,y=(u.x*w.z-u.z*w.x)/det;
                height=A.y+x*u.y+y*v.y;
                return x>=-.002f && y>=-.002f && x+y<=1.002f;
            }
        }
        readonly List<Surface> all=new List<Surface>();
        readonly Dictionary<Vector2Int,List<Surface>> buckets=new Dictionary<Vector2Int,List<Surface>>();
        readonly HashSet<int> captured=new HashSet<int>();
        readonly Dictionary<(Vector3Int,Vector3Int),Surface> edges=new Dictionary<(Vector3Int,Vector3Int),Surface>();
        internal readonly List<Layer> Layers=new List<Layer>();
        internal Layer PlayerFloor { get;private set; }
        internal int Revision { get;private set; }
        internal Vector3 Support { get;private set; }
        internal string SupportName { get;private set; }
        float nextProbe;
        NavMeshTriangulation nav;
        bool fetched;
        static Vector2Int Cell(Vector3 v)=>new Vector2Int(Mathf.FloorToInt(v.x/8),Mathf.FloorToInt(v.z/8));
        static Vector3Int Key(Vector3 v)=>Vector3Int.RoundToInt(v*100);
        static int Compare(Vector3Int a,Vector3Int b)=>a.x!=b.x?a.x.CompareTo(b.x):a.y!=b.y?a.y.CompareTo(b.y):a.z.CompareTo(b.z);
        void Edge(Surface face,Vector3 a,Vector3 b)
        {
            var x=Key(a);var y=Key(b);
            var key=Compare(x,y)<=0?(x,y):(y,x);
            if(edges.TryGetValue(key,out var other)){
                other.Adjacent.Add(face);face.Adjacent.Add(other);
            } else edges[key]=face;
        }
        Layer Datum(float height)
        {
            // Merge survey measurements of the same horizontal datum within collider skin precision.
            foreach(var l in Layers) if(Mathf.Abs(l.Height-height)<.12f)return l;
            var layer=new Layer{Id=Layers.Count,Height=height,Name="Level at "+height.ToString("0.0")+" m"};
            Layers.Add(layer);Revision++;return layer;
        }
        void Triangle(Vector3 a,Vector3 b,Vector3 c)
        {
            var normal=Vector3.Cross(b-a,c-a).normalized;
            if(Mathf.Abs(normal.y)<.55f)return;
            var bounds=new Bounds(a,Vector3.zero);bounds.Encapsulate(b);bounds.Encapsulate(c);
            bool slope=Mathf.Abs(normal.y)<.995f;
            var s=new Surface{A=a,B=b,C=c,Bounds=bounds,Sloped=slope,Layer=slope?null:Datum(bounds.center.y)};
            all.Add(s);var start=Cell(bounds.min);var end=Cell(bounds.max);
            for(int x=start.x;x<=end.x;x++)for(int z=start.y;z<=end.y;z++)
            {
                var k=new Vector2Int(x,z);
                if(!buckets.TryGetValue(k,out var list))buckets[k]=list=new List<Surface>();
                list.Add(s);
            }
            Edge(s,a,b);Edge(s,b,c);Edge(s,c,a);
        }
        void ConnectLandings()
        {
            var work=new Queue<Surface>();
            foreach(var face in all)if(!face.Sloped)work.Enqueue(face);else face.Layer=null;
            while(work.Count>0)
            {
                var current=work.Dequeue();
                foreach(var next in current.Adjacent)
                {
                    if(next.Layer==null){next.Layer=current.Layer;work.Enqueue(next);}
                    else if(next.Layer!=current.Layer){
                        next.Layer.Neighbors.Add(current.Layer.Id);
                        current.Layer.Neighbors.Add(next.Layer.Id);
                    }
                }
            }
            Revision++;
        }
        readonly HashSet<uint> nativeNodes=new HashSet<uint>();
        int NativeWalkable(Room room)
        {
            var graphSystem=AstarPath.active;
            if(graphSystem==null||graphSystem.isScanning||graphSystem.data?.graphs==null)return 0;
            int count=0;
            foreach(var graph in graphSystem.data.graphs)
            {
                if(!(graph is Pathfinding.NavmeshBase mesh))continue;
                var tiles=mesh.GetTouchingTiles(room.WorldBounds,0);
                for(int x=tiles.xmin;x<=tiles.xmax;x++)for(int z=tiles.ymin;z<=tiles.ymax;z++)
                {
                    var tile=mesh.GetTile(x,z);
                    if(tile?.nodes==null)continue;
                    foreach(var node in tile.nodes)
                    {
                        if(node==null || !node.Walkable)continue;
                        var a=(Vector3)node.GetVertex(0);var b=(Vector3)node.GetVertex(1);var c=(Vector3)node.GetVertex(2);
                        if(!room.WorldBounds.Contains((a+b+c)/3))continue;
                        count++;if(nativeNodes.Add((uint)node.NodeIndex))Triangle(a,b,c);
                    }
                }
            }
            return count;
        }
        internal Layer Below(Vector3 p)
        {
            if(!buckets.TryGetValue(Cell(p),out var faces))return null;
            float highest=float.NegativeInfinity;Layer layer=null;
            foreach(var face in faces)if(face.Sample(p,out var h)&&h<=p.y+.12f&&h>highest)
            {highest=h;layer=face.Layer;}
            return layer;
        }
        readonly HashSet<int> indexedColliders=new HashSet<int>();
        // Native floor surveys for every generated tile, independent of the
        // specially authored shop staircase. No rectangular floor fill.
        sealed class RoomLandings
        {
            internal readonly Dictionary<int,Layer> Supports=new Dictionary<int,Layer>();
            internal readonly HashSet<int> Stairs=new HashSet<int>();
            internal readonly HashSet<Layer> Levels=new HashSet<Layer>();
        }
        readonly Dictionary<int,RoomLandings> roomLandings=new Dictionary<int,RoomLandings>();
        internal static bool StairName(string name)
        {
            name=name.ToLowerInvariant();
            return name.Contains("stair")||name.Contains("ramp")||name.Contains("ladder")||name.Contains("steps");
        }
        internal static bool StairHierarchy(Transform t)
        {
            for(;t!=null&&t.GetComponent<DunGen.Tile>()==null;t=t.parent)
                if(StairName(t.name))return true;
            return false;
        }
        internal static bool WalkSurface(Collider c)
        {
            if(c==null||c.isTrigger||!c.enabled||!c.gameObject.activeInHierarchy)return false;
            string name=c.name.ToLowerInvariant();
            var renderer=c.GetComponent<Renderer>();
            if(renderer!=null)foreach(var m in renderer.sharedMaterials)if(m!=null)name+=" "+m.name.ToLowerInvariant();
            if(name.Contains("ceiling")||name.Contains("cieling")||name.Contains("roof")||
                name.Contains("darkness")||name.Contains("wall")||name.Contains("fence")||
                name.Contains("railing")||name.Contains("table")||name.Contains("shelf")||
                name.Contains("pillar")||name.Contains("beam")||name.Contains("trigger"))return false;
            if(StairHierarchy(c.transform)||name.Contains("floor")||name.Contains("bridge")||
                name.Contains("walkway")||name.Contains("ground")||name.Contains("platform")||
                name.Contains("plank")||name.Contains("terrain")||name.Contains("landing"))return true;
            var b=c.bounds;
            // Native geometry with generic Quad/Plane names can be a landing.
            // Require a thin, horizontal collider of meaningful walkable size.
            return (c is BoxCollider || c is MeshCollider) && b.size.x>=1.1f && b.size.z>=1.1f &&
                b.size.y<=.25f && renderer!=null &&
                (c.name.StartsWith("Quad",StringComparison.OrdinalIgnoreCase) ||
                 c.name.StartsWith("Plane",StringComparison.OrdinalIgnoreCase));
        }
        void SurveyLandings(Room room)
        {
            var profile=new RoomLandings();
            roomLandings[room.Key]=profile;
            foreach(var c in room.Tile.GetComponentsInChildren<Collider>(false))
            {
                if(c==null||!c.enabled||c.isTrigger||c.GetComponentInParent<DunGen.Tile>()!=room.Tile)continue;
                if(!WalkSurface(c))continue;
                bool stairs=StairHierarchy(c.transform);
                if(stairs)profile.Stairs.Add(c.GetInstanceID());
                // Indexed triangles follow actual mesh edges, including holes,
                // gaps and bridge decks. Never use room bounds as walkable fill.
                IndexCollider(c);
                if(stairs)continue;
                Bounds b=c.bounds;
                // Probe the collider itself, not arbitrary furniture above it.
                // Multiple samples permit holes and split floor panels.
                Layer picked=null;
                foreach(var offset in new[]{Vector2.zero,new Vector2(-.32f,-.32f),new Vector2(.32f,-.32f),new Vector2(-.32f,.32f),new Vector2(.32f,.32f)})
                {
                    var origin=new Vector3(b.center.x+b.size.x*offset.x,b.max.y+.25f,b.center.z+b.size.z*offset.y);
                    if(!c.Raycast(new Ray(origin,Vector3.down),out var hit,b.size.y+.5f)||hit.normal.y<.98f)continue;
                    var layer=Datum(hit.point.y);
                    profile.Levels.Add(layer);
                    if(picked==null)picked=layer;
                }
                if(picked!=null&&profile.Levels.Contains(picked))profile.Supports[c.GetInstanceID()]=picked;
            }
        }
        internal Layer RoomOwner(int key,Renderer renderer,Vector3 foot,out bool stair)
        {
            var authored=ShopOwner(key,renderer,foot,out stair);
            if(authored!=null)return authored;
            stair=StairHierarchy(renderer.transform);
            // Actual supporting surfaces first; sample the footing on a
            // renderer's footprint if its center is over a genuine gap.
            var floor=Below(foot);
            if(floor!=null)return floor;
            var b=renderer.bounds;
            float best=float.NegativeInfinity;Layer chosen=null;
            foreach(var offset in new[]{new Vector2(-.35f,0),new Vector2(.35f,0),new Vector2(0,-.35f),new Vector2(0,.35f)})
            {
                var p=foot+new Vector3(offset.x*b.size.x,0,offset.y*b.size.z);
                var candidate=Below(p);
                if(candidate!=null&&candidate.Height>best){best=candidate.Height;chosen=candidate;}
            }
            if(chosen!=null)return chosen;
            return chosen;
        }
        internal int SurveyedRoomCount=>roomLandings.Count+shopFlights.Count;
        internal int DetectedStairCount=>roomLandings.Values.Sum(r=>r.Stairs.Count)+shopFlights.Values.Sum(r=>r.Stairs.Count);
        internal int DetectedSupportCount=>roomLandings.Values.Sum(r=>r.Supports.Count)+shopFlights.Values.Sum(r=>r.Platforms.Count);

        void IndexCollider(Collider c)
        {
            if(c==null||!indexedColliders.Add(c.GetInstanceID()))return;
            var t=c.transform;
            if(c is BoxCollider box)
            {
                var h=box.size*.5f;var p=box.center;
                var a=t.TransformPoint(p+new Vector3(-h.x,h.y,-h.z));var b=t.TransformPoint(p+new Vector3(-h.x,h.y,h.z));
                var d=t.TransformPoint(p+new Vector3(h.x,h.y,-h.z));var e=t.TransformPoint(p+h);
                Triangle(a,b,e);Triangle(a,e,d);
            }
            if(c is MeshCollider mc && mc.sharedMesh!=null && mc.sharedMesh.isReadable)
            {
                var verts=mc.sharedMesh.vertices;var indices=mc.sharedMesh.triangles;
                for(int i=0;i+2<indices.Length;i+=3)
                {
                    var a=t.TransformPoint(verts[indices[i]]);var b=t.TransformPoint(verts[indices[i+1]]);var e=t.TransformPoint(verts[indices[i+2]]);
                    if(Vector3.Cross(b-a,e-a).normalized.y>.55f)Triangle(a,b,e);
                }
            }
        }
        sealed class ShopFlight
        {
            internal Layer Lower,Upper;
            internal bool HasUpperDoor;
            internal readonly List<Bounds> UpperPanels=new List<Bounds>();
            internal readonly Dictionary<int,Layer> Platforms=new Dictionary<int,Layer>();
            internal readonly HashSet<int> Stairs=new HashSet<int>();
        }
        readonly Dictionary<int,ShopFlight> shopFlights=new Dictionary<int,ShopFlight>();
        bool SurveyShop(Room room)
        {
            if(!string.Equals(room.Tile.name,"Shop(Clone)",StringComparison.OrdinalIgnoreCase))return false;
            var flight=new ShopFlight();
            var planes=new List<(Collider collider,Bounds bounds,float height)>();
            foreach(var c in room.Tile.GetComponentsInChildren<Collider>(false))
            {
                if(c.isTrigger||!c.enabled)continue;
                if(c.name=="Stairs" || c.name=="box collider for stairs" || (c.name=="box colider" && c.bounds.size.y>1 && room.Tile.GetComponentsInChildren<Transform>(false).Any(t=>t.name=="Stairs" && t.GetComponent<Renderer>() is Renderer stairMesh && stairMesh.bounds.Intersects(c.bounds)))){flight.Stairs.Add(c.GetInstanceID());continue;}
                var renderer=c.GetComponent<Renderer>();var material=renderer?.sharedMaterial;
                if(material==null||!material.name.StartsWith("WoodenFloor",StringComparison.OrdinalIgnoreCase))continue;
                var bounds=renderer.bounds;
                if(bounds.size.y>.05f)continue;
                if(c.Raycast(new Ray(bounds.center+Vector3.up,Vector3.down),out var hit,2) && hit.normal.y>.98f)
                    planes.Add((c,bounds,hit.point.y));
            }
            if(flight.Stairs.Count==0 || planes.Count<2)return false;
            foreach(var plane in planes)
            {
                var floor=Datum(plane.height);
                // A confirmed level landing takes precedence over the approximated navigation mesh.
                floor.Height=plane.bounds.center.y;
                floor.Name="Level at "+floor.Height.ToString("0.0")+" m";
                flight.Platforms[plane.collider.GetInstanceID()]=floor;
                if(flight.Lower==null||floor.Height<flight.Lower.Height)flight.Lower=floor;
                if(flight.Upper==null||floor.Height>flight.Upper.Height)flight.Upper=floor;
                // The shop landing panels are native planar quads. Use their actual corners even when rotated.
                var filter=plane.collider.GetComponent<MeshFilter>();
                if(filter!=null && filter.sharedMesh!=null && filter.sharedMesh.isReadable)
                {
                    var m=filter.sharedMesh;var v=m.vertices;var ix=m.triangles;
                    for(int i=0;i<ix.Length;i+=3)Triangle(filter.transform.TransformPoint(v[ix[i]]),filter.transform.TransformPoint(v[ix[i+1]]),filter.transform.TransformPoint(v[ix[i+2]]));
                }
            }
            if(flight.Lower==flight.Upper)return false;
            foreach(var plane in planes)if(flight.Platforms[plane.collider.GetInstanceID()]==flight.Upper)flight.UpperPanels.Add(plane.bounds);
            foreach(var door in room.Tile.GetComponentsInChildren<DunGen.Doorway>(false))
            {
                if(door.ConnectedDoorway==null || door.Tile!=room.Tile)continue;
                var p=door.transform.position;
                foreach(var panel in flight.UpperPanels)
                    if(Mathf.Abs(p.y-panel.center.y)<.35f && p.x>=panel.min.x-.5f && p.x<=panel.max.x+.5f && p.z>=panel.min.z-.5f && p.z<=panel.max.z+.5f)
                        flight.HasUpperDoor=true;
            }
            flight.Lower.Neighbors.Add(flight.Upper.Id);flight.Upper.Neighbors.Add(flight.Lower.Id);
            shopFlights[room.Key]=flight;Revision++;return true;
        }
        internal bool DisconnectedUpstairs(Room active,int room,Layer layer) => active!=null && active.Key!=room && shopFlights.TryGetValue(active.Key,out var s) && s.Upper==layer && !s.HasUpperDoor;
        internal bool OverlooksShopFloor(int room,Layer landing,Layer floor) => shopFlights.TryGetValue(room,out var flight) && flight.Upper==landing && flight.Lower==floor;
        internal Layer ShopOwner(int roomKey,Renderer renderer,Vector3 foot,out bool stair)
        {
            stair=false;
            if(!shopFlights.TryGetValue(roomKey,out var flight))return null;
            stair=renderer.name=="Stairs" || renderer.name=="box collider for stairs";
            // Resolve scenery by its footing, leaving tall ground-floor objects on their original floor.
            if(foot.y>=flight.Upper.Height-.12f)
            {
                var bounds=renderer.bounds;
                foreach(var panel in flight.UpperPanels)
                    if(bounds.max.x>=panel.min.x-.15f && bounds.min.x<=panel.max.x+.15f &&
                       bounds.max.z>=panel.min.z-.15f && bounds.min.z<=panel.max.z+.15f)return flight.Upper;
            }
            return flight.Lower;
        }
        internal void Observe(MapModel model)
        {
            if(Time.unscaledTime<nextProbe)return;
            nextProbe=Time.unscaledTime+.12f;
            if(!fetched){nav=NavMesh.CalculateTriangulation();fetched=true;}
            foreach(var r in model.Rooms)
            {
                if(!r.Renderable||r.Tile==null||!captured.Add(r.Key))continue;
                if(SurveyShop(r))break;
                int nativeCount=NativeWalkable(r);
                if(nativeCount==0)for(int i=0;i+2<nav.indices.Length;i+=3)
                {
                    var a=nav.vertices[nav.indices[i]];var b=nav.vertices[nav.indices[i+1]];var c=nav.vertices[nav.indices[i+2]];
                    if(r.WorldBounds.Contains((a+b+c)/3))Triangle(a,b,c);
                }
                SurveyLandings(r);
                ConnectLandings();break;
            }
            var pos=model.Player3D;
            var hits=Physics.RaycastAll(pos+Vector3.up*1.5f,Vector3.down,3.5f,Physics.DefaultRaycastLayers,QueryTriggerInteraction.Ignore);
            Array.Sort(hits,(a,b)=>a.distance.CompareTo(b.distance));
            foreach(var hit in hits)
            {
                if(hit.normal.y<.55f || hit.collider.GetComponentInParent<PlayerStats>()!=null)continue;
                var owner=hit.collider.GetComponentInParent<DunGen.Tile>();
                if(owner==null||!captured.Contains(owner.GetInstanceID()))continue;
                Support=hit.point;SupportName=hit.collider.name;
                if(shopFlights.TryGetValue(owner.GetInstanceID(),out var flight))
                {
                    if(flight.Platforms.TryGetValue(hit.collider.GetInstanceID(),out var landing))
                    {
                        PlayerFloor=landing;
                        if(hit.normal.y>.995f && Mathf.Abs(landing.Height-hit.point.y)>.005f)
                        {
                            landing.Height=hit.point.y;
                            landing.Name="Level at "+landing.Height.ToString("0.0")+" m";
                            Revision++;
                        }
                    }
                    else if(!flight.Stairs.Contains(hit.collider.GetInstanceID()) || (PlayerFloor!=flight.Lower && PlayerFloor!=flight.Upper))
                        PlayerFloor=hit.point.y>=flight.Upper.Height-.12f?flight.Upper:flight.Lower;
                    return;
                }
                if(roomLandings.TryGetValue(owner.GetInstanceID(),out var profile))
                {
                    if(profile.Supports.TryGetValue(hit.collider.GetInstanceID(),out var landing) &&
                        Mathf.Abs(landing.Height-hit.point.y)<.18f){PlayerFloor=landing;return;}
                    if(profile.Stairs.Contains(hit.collider.GetInstanceID())||StairHierarchy(hit.collider.transform))
                    {
                        var layer=Below(hit.point);
                        // Preserve current level while stepping onto a slope
                        // until a real supporting landing is reached.
                        if(layer!=null&&Mathf.Abs(layer.Height-hit.point.y)<.25f){PlayerFloor=layer;return;}
                        if(PlayerFloor!=null&&Mathf.Abs(PlayerFloor.Height-hit.point.y)<.55f)return;
                        // A newly reached landing may have no indexed triangles yet.
                        // Fall through to the original support probe instead of freezing the elevation.
                    }
                }
                IndexCollider(hit.collider);
                var supportLayer=Below(hit.point);
                if(supportLayer!=null&&Mathf.Abs(supportLayer.Height-hit.point.y)<=.22f)
                    PlayerFloor=supportLayer;
                else if(hit.normal.y>.98f)
                    PlayerFloor=Datum(hit.point.y);
                else if(PlayerFloor==null)PlayerFloor=supportLayer??Datum(hit.point.y);
                return;
            }
        }
    }
}
