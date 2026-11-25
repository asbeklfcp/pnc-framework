using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using DunGen;
using Object = UnityEngine.Object;

namespace PncPrivateLivingMap
{
    // Source geometry from the actual generated room assets. Never invent shapes:
    // NavMesh walking triangles when present; otherwise the room prefab's upward
    // facing floor/bridge/platform mesh triangles; otherwise room bounds.
    internal struct FloorTriangle
    {
        public Vector2 A,B,C;
        public FloorTriangle(Vector2 a, Vector2 b, Vector2 c){A=a;B=b;C=c;}
    }

    internal static class RoomGeometry
    {
        internal const int MaxPerRoom=15000;
        private static readonly string[] WalkableNames={
            "floor","ground","bridge","walk","path","platform","road",
            "sewer","stairs","step","plank","terrain","tunnel","hall","ramp"
        };

        internal static void CaptureLocal(Room room)
        {
            if (room.Tile == null) return;
            bool namedBridge=room.Name.IndexOf("bridge",StringComparison.OrdinalIgnoreCase)>=0;
            try
            {
                foreach (var filter in room.Tile.GetComponentsInChildren<MeshFilter>(false))
                {
                    if(filter==null||!filter.gameObject.activeInHierarchy||filter.sharedMesh==null)continue;
                    string name=filter.name.ToLowerInvariant();
                    bool likelyFloor=false;
                    foreach(var k in WalkableNames)if(name.Contains(k)){likelyFloor=true;break;}
                    if (name.Contains("bridge"))namedBridge=true;
                    // Vertical props and decorative mesh layers should not be painted as floors.
                    if(!likelyFloor||name.Contains("ceiling")||name.Contains("wall")||
                       name.Contains("pillar")||name.Contains("roof")||name.Contains("beam"))continue;
                    var mesh=filter.sharedMesh;
                    // Packed PNC meshes are frequently NOT CPU-readable.
                    // Checking isReadable before Unity's vertices/triangles
                    // getters avoids Unity log spam and unsafe extraction.
                    if(!mesh.isReadable)continue;
                    Vector3[] vertices;int[] indices;
                    try { vertices=mesh.vertices;indices=mesh.triangles; } catch { continue; }
                    if(vertices==null||indices==null||indices.Length>120000||vertices.Length>90000)continue;
                    var transform=filter.transform;
                    for(int i=0;i+2<indices.Length&&room.Footprint.Count<MaxPerRoom;i+=3)
                    {
                        var a=transform.TransformPoint(vertices[indices[i]]);
                        var b=transform.TransformPoint(vertices[indices[i+1]]);
                        var c=transform.TransformPoint(vertices[indices[i+2]]);
                        var n=Vector3.Cross(b-a,c-a);
                        if(n.sqrMagnitude<.00001f || Mathf.Abs(n.normalized.y)<.72f)continue;
                        if(!Inside(room.WorldBounds,(a+b+c)/3f,.3f))continue;
                        // Exclude very high roof pieces whenever floor geometry is available.
                        if(((a+b+c)/3f).y>room.WorldBounds.min.y + Mathf.Max(7f,room.WorldBounds.size.y*.40f))continue;
                        room.Footprint.Add(new FloorTriangle(new Vector2(a.x,a.z),new Vector2(b.x,b.z),new Vector2(c.x,c.z)));
                    }
                }
            }
            catch { /* prefab geometry is optional: the real bounds remain authoritative */ }
            if(room.Footprint.Count==0)
            {
                // Extracted prefab meshes show some walkable parts are named by
                // generic mesh IDs. Allow real horizontal surfaces near the
                // floor rather than guessing a rectangle from the prefab name.
                foreach(var filter in room.Tile.GetComponentsInChildren<MeshFilter>(false))
                {
                    if(filter==null||!filter.gameObject.activeInHierarchy||filter.sharedMesh==null)continue;
                    string name=filter.name.ToLowerInvariant();
                    if(name.Contains("wall")||name.Contains("roof")||name.Contains("ceiling")||
                       name.Contains("pipe")||name.Contains("torch")||name.Contains("pillar")||
                       name.Contains("table")||name.Contains("fence")||name.Contains("door"))continue;
                    var bounds=filter.GetComponent<Renderer>()?.bounds;
                    if(bounds==null || bounds.Value.size.x<2f || bounds.Value.size.z<2f ||
                       bounds.Value.max.y>room.WorldBounds.min.y + Mathf.Max(8f,room.WorldBounds.size.y*.48f))continue;
                    Vector3[] vertices;int[] indices;
                    try{
                        var mesh=filter.sharedMesh;
                        if(!mesh.isReadable)continue;
                        vertices=mesh.vertices;indices=mesh.triangles;
                    }catch{continue;}
                    if(vertices==null||indices==null||indices.Length>90000)continue;
                    var world=filter.transform;
                    for(int i=0;i+2<indices.Length&&room.Footprint.Count<MaxPerRoom;i+=3)
                    {
                        var a=world.TransformPoint(vertices[indices[i]]);
                        var b=world.TransformPoint(vertices[indices[i+1]]);
                        var c=world.TransformPoint(vertices[indices[i+2]]);
                        var normal=Vector3.Cross(b-a,c-a);
                        if(normal.sqrMagnitude<.01f||Mathf.Abs(normal.normalized.y)<.81f)continue;
                        if(!Inside(room.WorldBounds,(a+b+c)/3f,.3f))continue;
                        room.Footprint.Add(new FloorTriangle(new Vector2(a.x,a.z),new Vector2(b.x,b.z),new Vector2(c.x,c.z)));
                    }
                    if(room.Footprint.Count>0)break;
                }
            }
            room.IsBridge=namedBridge;
            if(room.Footprint.Count>0)room.GeometrySource="FLOOR MESH";
        }

        // Called only on dungeon generation or when new tiles are added.
        internal static int OverlayNavMesh(IReadOnlyCollection<Room> rooms)
        {
            NavMeshTriangulation mesh;
            try { mesh=NavMesh.CalculateTriangulation(); } catch { return 0; }
            if(mesh.vertices==null||mesh.indices==null||mesh.indices.Length<6||mesh.indices.Length>450000)
                return 0;
            var dict=new Dictionary<int,List<FloorTriangle>>();
            foreach(var room in rooms)dict[room.Key]=new List<FloorTriangle>();
            int assigned=0;
            for(int i=0;i+2<mesh.indices.Length;i+=3)
            {
                var a=mesh.vertices[mesh.indices[i]];
                var b=mesh.vertices[mesh.indices[i+1]];
                var c=mesh.vertices[mesh.indices[i+2]];
                var centroid=(a+b+c)/3f;
                if(Vector3.Cross(b-a,c-a).sqrMagnitude<.001f)continue;
                foreach(var room in rooms)
                {
                    var dst=dict[room.Key];
                    if(dst.Count>=MaxPerRoom||!Inside(room.WorldBounds,centroid,.35f))continue;
                    dst.Add(new FloorTriangle(new Vector2(a.x,a.z),new Vector2(b.x,b.z),new Vector2(c.x,c.z)));
                    assigned++;break;
                }
            }
            if(assigned<6)return 0;
            int updated=0;
            foreach(var room in rooms)
            {
                var list=dict[room.Key];
                if(list.Count<2)continue;
                room.Footprint.Clear();
                room.Footprint.AddRange(list);
                room.GeometrySource="NAVMESH";
                room.FootprintRevision++;
                updated++;
            }
            return updated;
        }

        private static bool Inside(Bounds b,Vector3 c,float extra)=>
            c.x>=b.min.x-extra&&c.x<=b.max.x+extra&&
            c.z>=b.min.z-extra&&c.z<=b.max.z+extra&&
            c.y>=b.min.y-extra-2f&&c.y<=b.max.y+extra+2f;

        internal static string CleanName(string name)
        {
            if(string.IsNullOrWhiteSpace(name))return "Unknown Area";
            var result=name.Replace("(Clone)","").Replace('_',' ').Trim();
            result=System.Text.RegularExpressions.Regex.Replace(result,@"([a-z])([A-Z])","$1 $2");
            result=System.Text.RegularExpressions.Regex.Replace(result,@"\s+"," ");
            result=result.Replace("Entry Floor Sewers Test","Sewer Entrance").Replace("Peeproom","Peep Room");
            result=System.Text.RegularExpressions.Regex.Replace(result,@"(?i)\btest\b","").Trim();
            if(result.Length>45)result=result.Substring(0,45).Trim();
            return System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(result.ToLowerInvariant());
        }
        internal static string ShortName(string name)
        {
            if(string.IsNullOrWhiteSpace(name))return "AREA";
            string label=CleanName(name);
            if(label.StartsWith("Sewer ",StringComparison.OrdinalIgnoreCase))label=label.Substring(6);
            if(label.StartsWith("Dungeon ",StringComparison.OrdinalIgnoreCase))label=label.Substring(8);
            if(label.Length>19)label=label.Substring(0,18).TrimEnd()+"…";
            return label;
        }
    }
}