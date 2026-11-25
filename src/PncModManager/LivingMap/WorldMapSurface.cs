using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using BepInEx;
using UnityEngine;
using UnityEngine.Rendering;

namespace PncPrivateLivingMap
{
    internal enum MapProjection { Overhead, Diorama, Isometric, Orbit }

    // Only this inventory reads scene components. No GameObjects, Cameras or Lights are allocated.
    internal sealed class VisualInventory : IDisposable
    {
        internal const bool ReadyFastPath=true;
        internal const bool GlobalFast=true;
        internal static long SurveyComparisons,SurveyMismatches,MeshEvaluations,NativeEvaluations;
        Room PickSurveyRoom(MapModel model,float now){
            Room best=null;int rank=4;
            foreach(var tile in model.Rooms){
                if(!tile.Renderable||tile.Tile==null)continue;
                bool known=nextRoomSurvey.TryGetValue(tile.Key,out var after);
                if(known&&now<after)continue;
                int score=(known?2:0)+(tile.Visited?0:1);
                if(score>=rank)continue;
                best=tile;rank=score;if(rank==0)break;
            }
            return best;
        }
        Room PickSurveyOriginal(MapModel model,float now){
            foreach(var tile in model.Rooms.OrderBy(r=>nextRoomSurvey.ContainsKey(r.Key)).ThenBy(r=>!r.Visited))
                if(tile.Renderable&&tile.Tile!=null&&(!nextRoomSurvey.TryGetValue(tile.Key,out var after)||now>=after))return tile;
            return null;
        }
        internal sealed class Part
        {
            internal Renderer Source;
            internal int Id;
            internal Component TextSource;
            internal string Path;
            internal Mesh Mesh;
            internal bool Roof, Visible, ShopkeeperFront;
            internal bool StaticScenery, HasSnapshot, Retained;
            internal Room Room;
            internal Renderer NativeProxy;
            internal float LastProxyUse;
            internal Mesh NativeMesh;
            internal Sprite NativeSprite;
            internal Texture VisualTexture;
            internal Material[] NativeMaterials;
            internal MaterialPropertyBlock NativeBlock;
            internal MaterialPropertyBlock[] NativeBlocks;
            internal Vector3 NativePosition,NativeScale;
            internal Quaternion NativeRotation;
            internal Color NativeColor;
            internal bool NativeFlipX,NativeFlipY;
            internal int NativeSortingLayer,NativeSortingOrder,NativeLayer;
            internal ShadowCastingMode NativeShadows;
            internal bool NativeReceiveShadows;
            internal Matrix4x4 Matrix;
            internal Bounds Bounds;
            internal ElevationMap.Layer Floor;
            internal bool LargeBarrier; internal bool Backdrop; internal bool Structural; internal Mesh Section; internal float SectionHeight=float.NaN;
            internal int RoomKey; internal bool Stair; internal int FloorRevision=-1; internal Vector3 Foot;
            internal Material[] Materials;
            internal Material[] DepthMaterials;
            internal int[] DepthPasses;
            internal MaterialPropertyBlock[] Properties, GhostProperties;
        }
        internal readonly Dictionary<int,List<Part>> Rooms = new Dictionary<int,List<Part>>();
        readonly Dictionary<(Mesh,Vector3),Mesh> shaded=new Dictionary<(Mesh,Vector3),Mesh>();
        Mesh Shade(Mesh source, Matrix4x4 transform)
        {
            if(!source.isReadable) return source;
            Vector3 sun=new Vector3(-.48f,.84f,-.26f).normalized;
            Vector3 direction=transform.inverse.MultiplyVector(sun).normalized;
            var key=(source,direction);
            if(shaded.TryGetValue(key,out var cached)) return cached;
            var normals=source.normals;
            if(normals.Length!=source.vertexCount)return source;
            var copy=UnityEngine.Object.Instantiate(source);
            var colors=source.colors32;
            bool original=colors.Length==normals.Length;
            if(!original)colors=new Color32[normals.Length];
            for(int i=0;i<colors.Length;i++)
            {
                float light=.38f+.82f*Mathf.Max(0,Vector3.Dot(direction,normals[i]));
                var c=original?(Color)colors[i]:Color.white;
                colors[i]=new Color(c.r*light,c.g*light,c.b*light,c.a);
            }
            copy.colors32=colors;
            shaded.Add(key,copy);
            return copy;
        }
        readonly Dictionary<Sprite,Mesh> sprites = new Dictionary<Sprite,Mesh>();
        readonly Dictionary<Material,Material> materials = new Dictionary<Material,Material>();
        readonly HashSet<int> detected=new HashSet<int>();
        readonly Dictionary<int,float> nextRoomSurvey=new Dictionary<int,float>();
        float nextLooseSurvey;
        readonly Stack<Transform> looseNodes=new Stack<Transform>();
        readonly List<GameObject> looseRoots=new List<GameObject>(64);
        readonly System.Diagnostics.Stopwatch looseBudget=new System.Diagnostics.Stopwatch();
        readonly System.Diagnostics.Stopwatch refreshBudget=new System.Diagnostics.Stopwatch();
        readonly System.Diagnostics.Stopwatch surveyBudget=new System.Diagnostics.Stopwatch();
        readonly Dictionary<int,List<Renderer>> loose=new Dictionary<int,List<Renderer>>();
#if MAP_QA
        bool auditChanged;
#endif
        void FindLoose(MapModel model)
        {
            if(looseNodes.Count==0)
            {
                if(Time.unscaledTime<nextLooseSurvey)return;
                nextLooseSurvey=Time.unscaledTime+4;
                UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects(looseRoots);
                foreach(var root in looseRoots)looseNodes.Push(root.transform);
                looseRoots.Clear();
            }
            looseBudget.Restart();
            while(looseNodes.Count>0 && looseBudget.Elapsed.TotalMilliseconds<.7)
            {
                var t=looseNodes.Pop();
                if(t==null || (t.gameObject.hideFlags & HideFlags.HideAndDontSave)!=0 || !t.gameObject.activeInHierarchy || t.GetComponent<DunGen.Tile>()!=null || t.GetComponent<Camera>()!=null ||
                   t.GetComponent<PlayerStats>()!=null || t is RectTransform || t.GetComponent<EnemyAI>()!=null)continue;
                for(int i=0;i<t.childCount;i++)looseNodes.Push(t.GetChild(i));
                var renderer=t.GetComponent<Renderer>();
                if(renderer==null||detected.Contains(renderer.GetInstanceID()))continue;
                var b=renderer.bounds;
                if(!Physics.Raycast(b.center+Vector3.up*.25f,Vector3.down,out var hit,b.extents.y+4,Physics.DefaultRaycastLayers,QueryTriggerInteraction.Ignore))continue;
                var tile=hit.collider.GetComponentInParent<DunGen.Tile>();
                if(tile==null)continue;
                var key=tile.GetInstanceID();
                if(!Rooms.ContainsKey(key))continue;
                if(!loose.TryGetValue(key,out var found))loose[key]=found=new List<Renderer>();
                if(!found.Contains(renderer))found.Add(renderer);
            }
        }
        IEnumerator<Renderer> Survey(Room room)
        {
            foreach(var r in room.Tile.GetComponentsInChildren<Renderer>(true))
                if(r!=null && !detected.Contains(r.GetInstanceID()) && r.GetComponentInParent<DunGen.Tile>(true)==room.Tile)yield return r;
            if(loose.TryGetValue(room.Key,out var more))
            {
                var items=more.ToArray();more.Clear();
                foreach(var r in items)if(r!=null && !detected.Contains(r.GetInstanceID()))yield return r;
            }
        }
        IEnumerator<Renderer> pending;
        Room pendingRoom;
        List<Part> pendingParts;
        float nextRefresh;
        int refreshed;
        readonly List<Part> liveParts=new List<Part>();
        readonly List<string> audit = new List<string>();
        readonly Shader shader = Shader.Find("UI/Default") ?? Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Texture");
        static string PathOf(Transform t) => t.parent == null ? t.name : PathOf(t.parent) + "/" + t.name;
        internal Mesh SpriteMesh(Sprite sprite)
        {
            if (sprites.TryGetValue(sprite,out var mesh)) return mesh;
            mesh = new Mesh {name=sprite.name};
            mesh.vertices=Array.ConvertAll(sprite.vertices,v=>new Vector3(v.x,v.y,0));
            mesh.uv=sprite.uv;
            mesh.triangles=Array.ConvertAll(sprite.triangles,i=>(int)i);
            sprites.Add(sprite,mesh);
            return mesh;
        }
        Material MapMaterial(Material source)
        {
            if(materials.TryGetValue(source,out var result)) return result;

            bool spriteFamily=source.shader.name.IndexOf("sprite",StringComparison.OrdinalIgnoreCase)>=0;
            var resolved=Shader.Find("Sprites/Default")??shader;
            result=resolved!=null?new Material(resolved):new Material(source);
            string prop=source.HasProperty("_BaseMap")?"_BaseMap":source.HasProperty("_MainTex")?"_MainTex":null;
            if(prop!=null)
            {
                result.mainTexture=source.GetTexture(prop);
                result.mainTextureScale=source.GetTextureScale(prop);
                result.mainTextureOffset=source.GetTextureOffset(prop);
            }
            if(result.HasProperty("_BaseColor") && source.HasProperty("_BaseColor")) result.SetColor("_BaseColor",source.GetColor("_BaseColor"));
            if(result.HasProperty("_Color") && source.HasProperty("_Color")) result.SetColor("_Color",source.GetColor("_Color"));
            if(result.HasProperty("_Cutoff") && source.HasProperty("_Cutoff")) result.SetFloat("_Cutoff",source.GetFloat("_Cutoff"));
            materials.Add(source,result);
            return result;
        }
        internal ElevationMap Elevations=new ElevationMap();
        int generation=-1;
        int activeRoomKey=int.MinValue;
        int preparedRooms, preparedRevision=-1;
        float nextPreparedCheck;
        internal int PreparedRooms(MapModel model)
        {
            if(preparedRevision!=model.Revision||Time.unscaledTime>=nextPreparedCheck){
                preparedRevision=model.Revision;nextPreparedCheck=Time.unscaledTime+.25f;
                preparedRooms=0;
                foreach(var room in model.Rooms)if(room.Renderable&&Rooms.TryGetValue(room.Key,out var parts)&&parts.Any(p=>p.StaticScenery&&p.Visible&&p.Mesh!=null))preparedRooms++;
            }
            return preparedRooms;
        }
        internal int RetainedCount
        {
            get {int count=0;foreach(var part in liveParts)if(part.Retained&&part.Visible&&part.Mesh!=null)count++;return count;}
        }
        #if MAP_QA
        #endif
        // Cache only scenery authored under a room. Dynamic actor silhouettes,
        // temporary effects, moving doors and loose runtime objects stay live.
        static bool IsPitfallDeck(Renderer r,Room room,Material[] source)
        {
            if(!(r is MeshRenderer) || room.Tile==null || room.Tile.name!="PitfallRoom(Clone)" ||
               r.transform.parent?.name!="Ceiling" || !r.name.StartsWith("Quad",StringComparison.OrdinalIgnoreCase) ||
               source.Length!=1 || source[0]==null || !source[0].name.Equals("Bricks",StringComparison.OrdinalIgnoreCase))return false;
            var c=r.GetComponent<MeshCollider>();
            var b=r.bounds;
            if(c==null||c.sharedMesh==null||!c.enabled||c.isTrigger||b.size.y>.25f||b.size.x<1.1f||b.size.z<1.1f)return false;
            if(c.gameObject.activeInHierarchy)
                return c.Raycast(new Ray(b.center+Vector3.up*.6f,Vector3.down),out var hit,1.2f) && hit.normal.y>.8f;
            // Inactive source colliders cannot be raycast. Use the authored quad
            // normal instead, without activating the source room.
            var mesh=c.sharedMesh;
            if(!mesh.isReadable)return false;
            var normals=mesh.normals;
            if(normals.Length==0)return false;
            return r.localToWorldMatrix.inverse.transpose.MultiplyVector(normals[0]).normalized.y>.8f;
        }
        static bool MayRemember(Renderer r,Room room,string path)
        {
            if(!(r is MeshRenderer || r is SpriteRenderer) || r.GetComponentInParent<DunGen.Tile>(true)!=room.Tile)return false;
            string low=path.ToLowerInvariant();
            return !Array.Exists(new[]{"enemy","controller","spawner","(clone)/door","/door/","/door (","/door animated","/npc","/boss","gravy positions","/fx_","/particle","/projectile","/pickup","/trap","/mimic"},low.Contains)
                && r.GetComponentInParent<PlayerStats>(true)==null;
        }
        internal void Update(MapModel model)
        {
            if(generation!=model.DungeonGeneration)
            {
                if(generation>=0)Dispose();
                generation=model.DungeonGeneration;
            }
            activeRoomKey=model.ActiveRoom?.Key??int.MinValue;
            if(model.ReadyMode)ReadCullEvidence();
            Elevations.Observe(model);
            FindLoose(model);
            if(Time.unscaledTime >= nextRefresh && liveParts.Count>0)
            {
                refreshBudget.Restart();
                int remaining=liveParts.Count;
                while(remaining-->0 && refreshBudget.Elapsed.TotalMilliseconds<1.5)
                {
                    if(refreshed>=liveParts.Count)refreshed=0;
                    var part=liveParts[refreshed++];
                    Refresh(part);
                }
                nextRefresh=Time.unscaledTime+(remaining<0?.085f:0);
            }
            if(pending==null)
            {
                float now=Time.unscaledTime;
                var tile=GlobalFast?PickSurveyRoom(model,now):PickSurveyOriginal(model,now);
#if MAP_QA
                if(WorldMapSurface.ReadyAudit){
                    SurveyComparisons++;
                    if(tile!=PickSurveyOriginal(model,now))SurveyMismatches++;
                }
#endif
                if(tile!=null)
                {
                    pendingRoom=tile;
                    if(!Rooms.TryGetValue(tile.Key,out pendingParts))Rooms[tile.Key]=pendingParts=new List<Part>();
                    nextRoomSurvey[tile.Key]=Time.unscaledTime+3;
                    pending=Survey(tile);
                }
            }
            if(pending==null)return;
            var room=pendingRoom;
            var parts=pendingParts;
            surveyBudget.Restart();
            while(surveyBudget.Elapsed.TotalMilliseconds<2)
            {
                if(!pending.MoveNext())
                {
                    pending.Dispose();pending=null;
                    #if MAP_QA
                    if(auditChanged){File.WriteAllLines(Path.Combine(Paths.BepInExRootPath,"world-map-inventory.tsv"),audit);auditChanged=false;}
#endif
                    break;
                }
                var r=pending.Current;
                if(r==null || room.Tile==null)continue;
                {
                    var part=new Part {RoomKey=room.Key,Room=room,Source=r,Id=r.GetInstanceID(),Path=PathOf(r.transform)};
                    part.StaticScenery=MayRemember(r,room,part.Path);
                    part.ShopkeeperFront=r is SpriteRenderer && part.Path.Contains("/Gravy Controller/Gravy Positions/") && (r.name=="Front" || r.transform.parent?.name=="Gravy Positions");
                    string state;
                    try
                    {
                        if(!detected.Add(part.Id))continue;
                        // Live simulation renderers are drawn directly by the native map camera.
                        if(r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer || r is SkinnedMeshRenderer)continue;
#if MAP_QA
                        auditChanged=true;
#endif
                        string path=PathOf(r.transform);
                        part.Roof=Array.Exists(path.Split('/'),v=>v.Equals("Roof",StringComparison.OrdinalIgnoreCase)||v.Equals("Ceiling",StringComparison.OrdinalIgnoreCase));
                        var sprite=r as SpriteRenderer;
                        if(r is MeshRenderer) part.Mesh=r.GetComponent<MeshFilter>()?.sharedMesh;
                        else if(sprite!=null && sprite.sprite!=null) part.Mesh=SpriteMesh(sprite.sprite);
                        if(part.Mesh==null && r is MeshRenderer)
                        {
                            part.TextSource=r.GetComponent("TextMeshPro");
                            if(part.TextSource!=null)part.Mesh=TextMesh(part.TextSource);
                            else if(r.GetComponent<MeshFilter>()==null && r.GetComponent<MeshCollider>() is MeshCollider support && support.sharedMesh!=null)
                            {
                                audit.Add(room.Key+"\t"+room.Name+"\t"+part.Id+"\tMeshRenderer\t"+part.Path+"\tcollision surface only; no authored visual mesh");
                                continue;
                            }
                        }
                        if(part.Mesh!=null && part.TextSource==null && r is MeshRenderer) part.Mesh=Shade(part.Mesh,r.localToWorldMatrix);
                        if(part.Mesh==null && part.TextSource==null && sprite==null) throw new InvalidOperationException("Missing mesh or unsupported renderer");
                        var source=r.sharedMaterials;
                        part.Backdrop=Array.Exists(source,m=>m!=null && m.name.Equals("sky",StringComparison.OrdinalIgnoreCase)) ||
                            part.Path.EndsWith("/shop extension/moon",StringComparison.OrdinalIgnoreCase);
                        string asset=r.name.ToLowerInvariant();
                        string parentPath=part.Path.ToLowerInvariant();
                        bool furnishing=Array.Exists(new[]{"shelf","shelv","book","stand","pedistal","pedestal","counter","display","table","gravy","slot machine","banner","painting"},parentPath.Contains);
                        bool masonry=asset.StartsWith("tile brick") || asset=="wall" || asset.StartsWith("wall (") || asset.StartsWith("wall panel") ||
                            (asset.StartsWith("quad") && Array.Exists(source,m=>m!=null && (m.name.IndexOf("wall",StringComparison.OrdinalIgnoreCase)>=0 || m.name.IndexOf("brick",StringComparison.OrdinalIgnoreCase)>=0)));
                        // Classify structural obstructions using both native
                        // mesh names and actual material/bounds evidence.
                        // Keep rails, narrow pipes and decorative furniture.
                        var bounds=r.bounds;
                        bool structuralMaterial=Array.Exists(source,m=>m!=null &&
                            Array.Exists(new[]{"wall","brick","masonry","cobblestone","cavern rock","rock face","stone wall"},m.name.ToLowerInvariant().Contains));
                        bool actualBarrier=bounds.size.y>=1.5f && Mathf.Max(bounds.size.x,bounds.size.z)>=2.2f;
                        bool delicate=Array.Exists(new[]{"fence","rail","pipe","chain","ladder","stair","doorway","door frame","torch"},asset.Contains);
                        part.LargeBarrier=r is MeshRenderer && !furnishing && !delicate && actualBarrier &&
                            (masonry || structuralMaterial || asset.Contains("boulder") || asset.StartsWith("fx_shard_rock") ||
                             asset.StartsWith("rock (") || asset.Contains("wall section") || asset.Contains("cave wall") || asset.Contains("cliff"));
                        part.Structural=r is MeshRenderer && Array.Exists(source,m=>m!=null &&
                          (m.name.IndexOf("wall",StringComparison.OrdinalIgnoreCase)>=0 || m.name.IndexOf("wood beam",StringComparison.OrdinalIgnoreCase)>=0 ||
                           m.name.Equals("Bricks",StringComparison.OrdinalIgnoreCase) || m.name.IndexOf("bedroom trim",StringComparison.OrdinalIgnoreCase)>=0));
                        part.Roof |= Array.Exists(source,m=>m!=null && (m.name.Equals("Darkness",StringComparison.OrdinalIgnoreCase) ||
                            m.name.IndexOf("ceiling",StringComparison.OrdinalIgnoreCase)>=0 || m.name.IndexOf("cieling",StringComparison.OrdinalIgnoreCase)>=0));
                        // PitfallRoom has real bridge deck quads under a misleading Ceiling group.
                        // Keep explicit roof/darkness materials hidden; only the authored brick deck qualifies.
                        if(part.Roof && IsPitfallDeck(r,room,source))part.Roof=false;
                        if(source.Length==0) throw new InvalidOperationException("No source materials");
                        part.Materials=new Material[Math.Max(1,part.Mesh?.subMeshCount??0)];
                        part.DepthMaterials=new Material[part.Materials.Length];
                        part.DepthPasses=new int[part.Materials.Length];
                        part.Properties=new MaterialPropertyBlock[part.Materials.Length];
                        part.GhostProperties=new MaterialPropertyBlock[part.Materials.Length];
                        for(int i=0;i<part.Materials.Length;i++)
                        {
                            var material=source[Math.Min(i,source.Length-1)];
                            part.Materials[i]=part.TextSource!=null?material:MapMaterial(material);
                            part.DepthMaterials[i]=material;
                            part.DepthPasses[i]=material.FindPass("DepthOnly");
                            var block=new MaterialPropertyBlock();
                            if(i<source.Length) r.GetPropertyBlock(block,i);
                            if(block.isEmpty) r.GetPropertyBlock(block);
                            Texture albedo=sprite!=null?sprite.sprite?.texture:part.Materials[i].mainTexture;
                            if(albedo!=null) block.SetTexture("_MainTex",albedo);
                            if(part.TextSource==null)
                            {
                                Color tint=material.HasProperty("_BaseColor")?material.GetColor("_BaseColor"):material.HasProperty("_Color")?material.GetColor("_Color"):Color.white;
                                if(sprite!=null)tint*=sprite.color;
                                block.SetVector("_Color",Vector4.Scale(tint,new Vector4(1.85f,1.85f,1.85f,1)));
                            }
                            part.Properties[i]=block;
                            var ghost=new MaterialPropertyBlock();
                            if(albedo!=null)ghost.SetTexture("_MainTex",albedo);
                            ghost.SetColor("_Color",new Color(.8f,.85f,.92f,.29f));
                            part.GhostProperties[i]=ghost;
                        }
                        Refresh(part);parts.Add(part);liveParts.Add(part);
#if MAP_QA
                        state="resolved\t"+r.bounds+"\t"+string.Join("|",Array.ConvertAll(source,m=>m.name+":"+m.shader.name+":"+string.Join(",",Array.ConvertAll(m.GetTexturePropertyNames(),k=>k+"="+(m.GetTexture(k)?.name??"null")))))+"\t"+part.Materials[0].shader.name+"\tmesh-readable="+(part.Mesh?.isReadable.ToString()??"waiting for native text");
#else
                        state="resolved";
#endif
                    }
                    catch(Exception ex)
                    {

                        state="unsupported " + ex.Message;
                    }
#if MAP_QA
                    if(audit.Count<20000)audit.Add(room.Key+"\t"+room.Name+"\t"+r.GetInstanceID()+"\t"+r.GetType().Name+"\t"+PathOf(r.transform)+"\t"+state);
#endif
                }
            }
        }
        static readonly System.Reflection.FieldInfo NativeTextMesh=HarmonyLib.AccessTools.Field(HarmonyLib.AccessTools.TypeByName("TMPro.TMP_Text"),"m_mesh");
        static Mesh TextMesh(Component source)=>NativeTextMesh?.GetValue(source) as Mesh;
        // A disabled renderer is not automatically scenery. Accept explicit native
        // culling evidence, or an enabled local subtree under an inactive tile.
        static readonly System.Reflection.FieldInfo AdjacentRenderers=HarmonyLib.AccessTools.Field(typeof(DunGen.AdjacentRoomCulling),"tileRenderers");
        static readonly System.Reflection.FieldInfo AdjacentVis=HarmonyLib.AccessTools.Field(typeof(DunGen.AdjacentRoomCulling),"tileVisibilities");
        static readonly System.Reflection.FieldInfo BasicRenderers=HarmonyLib.AccessTools.Field(typeof(DunGen.BasicRoomCullingCamera),"rendererVisibilities");
        static readonly System.Reflection.FieldInfo SectrHidden=HarmonyLib.AccessTools.Field(typeof(SECTR_CullingCamera),"hiddenRenderers");
        readonly HashSet<int> culledScenery=new HashSet<int>();
        float nextCullSurvey;
        void ReadCullEvidence()
        {
            if(Time.unscaledTime<nextCullSurvey)return;
            nextCullSurvey=Time.unscaledTime+1;
            culledScenery.Clear();
            foreach(var c in UnityEngine.Object.FindObjectsByType<DunGen.AdjacentRoomCulling>(FindObjectsSortMode.None)){
                if(!c.isActiveAndEnabled||c.IncludeDisabledComponents)continue;
                if(!(AdjacentRenderers?.GetValue(c) is Dictionary<DunGen.Tile,List<Renderer>> lists)||!(AdjacentVis?.GetValue(c) is Dictionary<DunGen.Tile,bool> vis))continue;
                foreach(var row in lists)if(row.Key!=null&&vis.TryGetValue(row.Key,out bool shown)&&!shown)
                    foreach(var r in row.Value)if(r!=null&&!r.enabled&&(!c.OverrideRendererVisibilities.TryGetValue(r,out bool enabled)||enabled))culledScenery.Add(r.GetInstanceID());
            }
            foreach(var c in UnityEngine.Object.FindObjectsByType<DunGen.BasicRoomCullingCamera>(FindObjectsSortMode.None)){
                if(!c.isActiveAndEnabled||!(BasicRenderers?.GetValue(c) is System.Collections.IDictionary lists))continue;
                foreach(System.Collections.DictionaryEntry row in lists)foreach(var entry in (System.Collections.IEnumerable)row.Value){
                    var type=entry.GetType();
                    if(type.GetField("Enabled")?.GetValue(entry) is bool enabled&&enabled&&type.GetField("Renderer")?.GetValue(entry) is Renderer r&&r!=null&&!r.enabled)culledScenery.Add(r.GetInstanceID());
                }
            }
            foreach(var c in UnityEngine.Object.FindObjectsByType<SECTR_CullingCamera>(FindObjectsSortMode.None)){
                if(!c.isActiveAndEnabled||!(SectrHidden?.GetValue(c) is System.Collections.IDictionary hidden))continue;
                foreach(System.Collections.DictionaryEntry row in hidden){
                    var child=row.Value;var type=child.GetType();
                    var f=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic;
                    if(type.GetField("renderCulled",f)?.GetValue(child) is bool culled&&culled&&type.GetField("renderer",f)?.GetValue(child) is Renderer r&&r!=null&&!r.enabled)culledScenery.Add(r.GetInstanceID());
                }
            }
        }
        bool AuthoredInInactiveTile(Part p,Renderer r)
        {
            if(!p.StaticScenery || p.Room?.ReadyReveal!=true || p.Room.Tile==null || r==null || r.forceRenderingOff || r.shadowCastingMode==ShadowCastingMode.ShadowsOnly)return false;
            for(var t=r.transform;t!=null && t!=p.Room.Tile.transform;t=t.parent)
                if(!t.gameObject.activeSelf)return false;
            return (r.enabled&&!p.Room.Tile.gameObject.activeInHierarchy)||culledScenery.Contains(p.Id);
        }
        void Refresh(Part part)
        {
            var r=part.Source;
            bool live=r!=null && !r.forceRenderingOff && r.enabled && r.gameObject.activeInHierarchy && r.shadowCastingMode!=ShadowCastingMode.ShadowsOnly;
#if MAP_QA
            const bool simulatedCull=false;
            if(simulatedCull)live=false;
#else
            const bool simulatedCull=false;
#endif
            part.Retained=!live && part.StaticScenery && part.HasSnapshot && part.Mesh!=null &&
                (part.RoomKey!=activeRoomKey || simulatedCull);
            bool initialInactive=!live && !part.HasSnapshot && AuthoredInInactiveTile(part,r);
            if(initialInactive){part.Retained=true;}
            part.Visible=live||part.Retained;
            // Keep the original map-only mesh, material, transform and floor
            // snapshot. No activation or state change on the source object.
            if(!live && !initialInactive)
            {
                if(part.NativeProxy!=null && Time.unscaledTime-part.LastProxyUse>12f){UnityEngine.Object.Destroy(part.NativeProxy.gameObject);part.NativeProxy=null;}
                return;
            }
            if(part.TextSource!=null)part.Mesh=TextMesh(part.TextSource);
            part.Matrix=r.localToWorldMatrix;
            part.Bounds=r.bounds;
            var foot=new Vector3(part.Bounds.center.x,part.Bounds.min.y,part.Bounds.center.z);
            if(part.FloorRevision!=Elevations.Revision || (foot-part.Foot).sqrMagnitude>.0025f){
                part.Foot=foot;part.Floor=Elevations.RoomOwner(part.RoomKey,r,foot,out part.Stair)??Elevations.Below(foot);part.FloorRevision=Elevations.Revision;
            }
            if(r is SpriteRenderer sprite)
            {
                var frame=sprite.sprite;
                if(frame==null){part.Mesh=null;return;}
                part.Mesh=SpriteMesh(frame);
                part.Matrix*=Matrix4x4.Scale(new Vector3(sprite.flipX?-1:1,sprite.flipY?-1:1,1));
                var texture=frame.texture;
                if(part.VisualTexture!=texture)
                {
                    foreach(var block in part.Properties)block.SetTexture("_MainTex",texture);
                    foreach(var block in part.GhostProperties)block.SetTexture("_MainTex",texture);
                    part.VisualTexture=texture;
                }
            }
            if(part.StaticScenery&&part.Mesh!=null&&part.Mesh.vertexCount>0)
            {
                part.HasSnapshot=true;
                if(part.NativeMaterials==null || (r.transform.position-part.NativePosition).sqrMagnitude>.0001f || r.transform.rotation!=part.NativeRotation || r.transform.lossyScale!=part.NativeScale || (r is SpriteRenderer current && current.sprite!=part.NativeSprite))SaveNativeSnapshot(part,r);
                if(part.NativeProxy!=null){UnityEngine.Object.Destroy(part.NativeProxy.gameObject);part.NativeProxy=null;}
            }
        }
        static void SaveNativeSnapshot(Part p,Renderer r)
        {
            p.NativePosition=r.transform.position;p.NativeRotation=r.transform.rotation;p.NativeScale=r.transform.lossyScale;
            p.NativeMesh=r is MeshRenderer?r.GetComponent<MeshFilter>()?.sharedMesh:null;
            if(r is SpriteRenderer sprite){p.NativeSprite=sprite.sprite;p.NativeColor=sprite.color;p.NativeFlipX=sprite.flipX;p.NativeFlipY=sprite.flipY;}
            if(p.NativeMaterials==null || p.NativeMaterials.Length==0)
            {
                p.NativeMaterials=r.sharedMaterials;
                p.NativeBlock=new MaterialPropertyBlock();
                p.NativeBlocks=new MaterialPropertyBlock[p.NativeMaterials.Length];
                for(int i=0;i<p.NativeBlocks.Length;i++)p.NativeBlocks[i]=new MaterialPropertyBlock();
            }
            r.GetPropertyBlock(p.NativeBlock);
            for(int i=0;i<p.NativeBlocks.Length;i++)r.GetPropertyBlock(p.NativeBlocks[i],i);
            p.NativeSortingLayer=r.sortingLayerID;p.NativeSortingOrder=r.sortingOrder;
            p.NativeLayer=r.gameObject.layer;p.NativeShadows=r.shadowCastingMode;p.NativeReceiveShadows=r.receiveShadows;
        }
        internal static void EnsureNativeProxy(Part p)
        {
            if(p.NativeProxy!=null)return;
            if(p.NativeMaterials==null || (p.NativeMesh==null&&p.NativeSprite==null))return;
            var go=new GameObject("PNC map-only retained scenery");
            go.hideFlags=HideFlags.HideAndDontSave;
            go.layer=p.NativeLayer;
            go.transform.SetPositionAndRotation(p.NativePosition,p.NativeRotation);
            go.transform.localScale=p.NativeScale;
            Renderer rr;
            if(p.NativeSprite!=null)
            {
                var spr=go.AddComponent<SpriteRenderer>();rr=spr;rr.forceRenderingOff=true;
                spr.sprite=p.NativeSprite;spr.color=p.NativeColor;spr.flipX=p.NativeFlipX;spr.flipY=p.NativeFlipY;
            }
            else
            {
                go.AddComponent<MeshFilter>().sharedMesh=p.NativeMesh;
                rr=go.AddComponent<MeshRenderer>();rr.forceRenderingOff=true;
            }
            rr.sharedMaterials=p.NativeMaterials;
            rr.sortingLayerID=p.NativeSortingLayer;rr.sortingOrder=p.NativeSortingOrder;
            rr.shadowCastingMode=p.NativeShadows;rr.receiveShadows=p.NativeReceiveShadows;
            rr.SetPropertyBlock(p.NativeBlock);
            for(int i=0;i<p.NativeBlocks.Length;i++)if(!p.NativeBlocks[i].isEmpty)rr.SetPropertyBlock(p.NativeBlocks[i],i);
            p.NativeProxy=rr;
        }
        public void Dispose()
        {
            foreach(var parts in Rooms.Values) foreach(var part in parts) {if(part.NativeProxy!=null)UnityEngine.Object.Destroy(part.NativeProxy.gameObject);if(part.Section!=null)UnityEngine.Object.Destroy(part.Section);}
            foreach(var mesh in shaded.Values) UnityEngine.Object.Destroy(mesh);
            shaded.Clear();
            foreach(var mesh in sprites.Values) UnityEngine.Object.Destroy(mesh);
            foreach(var material in materials.Values) UnityEngine.Object.Destroy(material);
            culledScenery.Clear();nextCullSurvey=0;preparedRevision=-1;preparedRooms=0;loose.Clear();looseNodes.Clear();nextRoomSurvey.Clear();detected.Clear();Elevations=new ElevationMap();pending?.Dispose();pending=null;liveParts.Clear();refreshed=0;Rooms.Clear();sprites.Clear();materials.Clear();audit.Clear();
        }
    }

    internal sealed partial class WorldMapSurface : IDisposable
    {
        #if MAP_QA
        readonly Dictionary<int,string> results=new Dictionary<int,string>();
        internal string QaLayerReport(VisualInventory inventory)
        {
            var counts=new System.Collections.Generic.SortedDictionary<float,int>();
            foreach(var room in inventory.Rooms.Values)foreach(var part in room)
                if(part.Floor!=null&&!part.Stair&&results.TryGetValue(part.Id,out var status)&&(status.Contains("submitted to GPU")||(status=="native camera eligible"||status=="retained native camera eligible")))
                {float height=part.Floor.Height;counts.TryGetValue(height,out int n);counts[height]=n+1;}
            return string.Join(",",System.Linq.Enumerable.Select(counts,p=>p.Key.ToString("F2")+":"+p.Value));
        }
        internal void Report(VisualInventory inventory,string file)
        {
            using(var writer=new StreamWriter(file))
            foreach(var room in inventory.Rooms)
                foreach(var part in room.Value)
                {
                    results.TryGetValue(part.Id,out var status);
                    writer.WriteLine(room.Key+"\t"+part.Id+"\t"+part.Path+"\t"+(status??"pending display"));
                }
        }
#endif
        readonly PausedActorArt pausedActorArt=new PausedActorArt();
        internal void ClearPausedActorArt()=>pausedActorArt.Dispose();
        readonly HashSet<int> spriteMarkers=new HashSet<int>();
        internal string RenderedFloor {get;private set;}
        internal bool UsingRetainedScenery {get;private set;}
        internal int SpriteCount=>spriteMarkers.Count;
        readonly MaterialPropertyBlock artProperties=new MaterialPropertyBlock();
        Material spriteMaterial;
        void DrawCharacterArt(MapModel model,VisualInventory inventory,Rect rect,bool mini)
        {
            if(spriteMaterial==null)
            {
                var shader=Shader.Find("Sprites/Default");
                if(shader==null)return;
                spriteMaterial=new Material(shader);
            }
            var rotation=Quaternion.LookRotation(Vector3.Cross(right,up),up);
            foreach(var marker in model.Markers)
            {
                if(marker.LastKnown || marker.Actor==null || !marker.Actor.gameObject.activeInHierarchy)continue;
                if(RelativeLayer(inventory.Elevations,marker.WorldPosition)!=0)continue;
                SpriteRenderer body=null;
                foreach(var art in marker.Artwork)
                    if(art!=null && art.enabled && art.gameObject.activeInHierarchy && art.sprite!=null &&
                       (body==null || art.sprite.bounds.size.y>body.sprite.bounds.size.y))body=art;
                if(body==null)continue;
                DrawBody(body,marker.Actor.position,mini,rect,inventory,rotation,pausedActorArt.Sample(body));
                spriteMarkers.Add(marker.ObjectId);
            }
            if(model.ActiveRoom!=null && inventory.Rooms.TryGetValue(model.ActiveRoom.Key,out var scenery))
                foreach(var part in scenery)
                    if(part.ShopkeeperFront && part.Visible && (!Focusing || part.Floor==selected) && part.Source is SpriteRenderer keeper && keeper.sprite!=null)
                        DrawBody(keeper,keeper.bounds.center,mini,rect,inventory,rotation);
        }
        void DrawBody(SpriteRenderer body,Vector3 position,bool mini,Rect rect,VisualInventory inventory,Quaternion rotation,Sprite sampled=null)
        {
            var sprite=sampled!=null?sampled:body.sprite;
            float depth=-view.MultiplyPoint(position).z;
            if(Perspective && depth<.025f)return;
            float halfHeight=Perspective?Mathf.Tan(Pose.Fov*Mathf.Deg2Rad*.5f)*depth:radius;
            float size=(mini?23f:36f)*2*halfHeight/rect.height/Mathf.Max(.001f,Mathf.Max(sprite.bounds.size.x,sprite.bounds.size.y));
            var scale=new Vector3(body.flipX?-size:size,body.flipY?-size:size,size);
            var matrix=Matrix4x4.TRS(position,rotation,scale)*Matrix4x4.Translate(-sprite.bounds.center);
            artProperties.Clear();artProperties.SetTexture("_MainTex",sprite.texture);
            artProperties.SetColor("_Color",body.color);
            commands.DrawMesh(inventory.SpriteMesh(sprite),matrix,spriteMaterial,0,0,artProperties);
        }
        readonly List<(VisualInventory.Part part,Mesh mesh,float depth)> solids=new List<(VisualInventory.Part,Mesh,float)>();
        readonly List<VisualInventory.Part> translucent=new List<VisualInventory.Part>();
        int CompareDepth(VisualInventory.Part a,VisualInventory.Part b)=>view.MultiplyPoint3x4(a.Bounds.center).z.CompareTo(view.MultiplyPoint3x4(b.Bounds.center).z);
        readonly CommandBuffer commands=new CommandBuffer {name="PNC private map"};
        RenderTexture target;
        float nextRender, yaw=32, tilt=45, radius=12;
        int lastRenderFrame=-1;
        float lastFollowTime;
        bool follow=true;
        internal BepInEx.Configuration.ConfigEntry<FloorView> FloorMode;
        internal BepInEx.Configuration.ConfigEntry<int> FloorIndex;
        internal WorldMapSurface MiniControls;
        internal BepInEx.Configuration.ConfigEntry<MapProjection> ProjectionMode;
        internal void SetProjection(MapProjection mode){ProjectionMode.Value=mode;nextRender=0;}
        internal MapProjection RenderedProjection {get;private set;}
        ElevationMap.Layer selected;
        bool editMini,framed;
        internal BepInEx.Configuration.ConfigEntry<float> BackgroundOpacity;
        bool Focusing => FloorMode.Value==FloorView.Focus;
        Camera contextCamera;
        RenderTexture contextScene;
        Material contextBlend,focusBlend;
        bool contextReady;
        RenderTexture previousLayerFrame;
        float layerFadeEnd;
        FloorView displayedMode;
        int displayedFloor=-1;
        readonly List<ElevationMap.Layer> sortedLayers=new List<ElevationMap.Layer>();
        ElevationMap sortedMap;
        void SortLayers(ElevationMap map)
        {
            if(sortedMap==map && sortedLayers.Count==map.Layers.Count)return;
            sortedMap=map;sortedLayers.Clear();sortedLayers.AddRange(map.Layers);
            sortedLayers.Sort((a,b)=>a.Height.CompareTo(b.Height));
        }
        float framedHeight=float.NaN;
        float lastHeightTime;
        int RelativeLayer(ElevationMap elevations,Vector3 position)
        {
            if(selected==null || FloorMode.Value==FloorView.AllFloors)return 0;
            var floor=elevations.Below(position);
            float delta=(floor?.Height??position.y)-selected.Height;
            return delta>1?1:delta< -1?-1:0;
        }
        void FloorControls(ElevationMap map)
        {
            int pick=GUILayout.Toolbar((int)FloorMode.Value,new[]{"This layer","Select floor","All layers","Cutaway"});
            if(pick!=(int)FloorMode.Value)FloorMode.Value=(FloorView)pick;
            if(GUILayout.Button("Down"))Step(map,-1);
            GUILayout.Label(selected?.Name??"Locating floor");
            if(GUILayout.Button("Up"))Step(map,1);
            if(GUILayout.Button(FloorMode.Value==FloorView.AllFloors?"View only this layer":"View all layers"))ToggleLayerView();
        }
        internal void ToggleLayerView(){FloorMode.Value=FloorMode.Value==FloorView.AllFloors?FloorView.Auto:FloorView.AllFloors;nextRender=0;}
        void SelectLayerMode(ElevationMap map,FloorView mode)
        {
            if(mode==FloorMode.Value)return;
            if((mode==FloorView.Focus || mode==FloorView.FloorSelection) && selected!=null)
            {
                SortLayers(map);int index=sortedLayers.IndexOf(selected);
                if(index>=0)FloorIndex.Value=index;
            }
            FloorMode.Value=mode;nextRender=0;
        }
        void Step(ElevationMap map,int delta)
        {
            SortLayers(map);var sorted=sortedLayers;
            FloorIndex.Value=Mathf.Clamp(sorted.IndexOf(selected)+delta,0,Math.Max(0,sorted.Count-1));
            if(FloorMode.Value!=FloorView.FloorSelection)FloorMode.Value=FloorView.Focus;nextRender=0;
        }
        Vector3 center;
        Matrix4x4 view,projection;
        Vector3 right,up;
        Vector3 PlaneOffset(Vector2 pixels, Rect rect)
        {
            var flatUp=new Vector3(up.x,0,up.z).normalized;
            return (right*pixels.x-flatUp*pixels.y/Mathf.Max(.15f,new Vector2(up.x,up.z).magnitude))*(2*radius/rect.height);
        }
        void Configure(MapModel model,MapProjection mode,bool mini)
        {
            if(Perspective && !mini)
            {
                right=Pose.Rotation*Vector3.right;up=Pose.Rotation*Vector3.up;
                view=Matrix4x4.Scale(new Vector3(1,1,-1))*Matrix4x4.TRS(Pose.Position,Pose.Rotation,Vector3.one).inverse;
                return;
            }
            if(follow||mini)
            {
                var live=model.Player3D;float now=Time.unscaledTime;
                if(lastFollowTime<=0 || now-lastFollowTime>.3f || (center-live).sqrMagnitude>64)center=live;
                else center=Vector3.Lerp(center,live,1-Mathf.Exp(-32*(now-lastFollowTime)));
                lastFollowTime=now;
            }
            float angle=mode==MapProjection.Overhead?90:mode==MapProjection.Isometric?35.26439f:mode==MapProjection.Diorama?58:tilt;
            float heading=mini?model.CameraHeading:mode==MapProjection.Isometric?45:mode==MapProjection.Overhead?0:yaw;
            var rotation=Quaternion.Euler(angle,heading,0);
            float wanted=selected?.Height??center.y;
            float time=Time.unscaledTime;
            framedHeight=float.IsNaN(framedHeight)||time-lastHeightTime>.4f?wanted:Mathf.Lerp(framedHeight,wanted,1-Mathf.Exp(-14*(time-lastHeightTime)));
            lastHeightTime=time;
            var targetCenter=new Vector3(center.x,framedHeight,center.z);
            right=rotation*Vector3.right;up=rotation*Vector3.up;
            view=Matrix4x4.Scale(new Vector3(1,1,-1))*Matrix4x4.TRS(targetCenter-rotation*Vector3.forward*(radius*2+65),rotation,Vector3.one).inverse;
        }
        Vector2 Project(Vector3 world,Rect rect)
        {
            var eye=view.MultiplyPoint(world);if(Perspective && eye.z>=-.025f)return new Vector2(-10000,-10000);
            var p=projection.MultiplyPoint(eye);
            return new Vector2(rect.x+(1+p.x)*rect.width*.5f,rect.y+(1-p.y)*rect.height*.5f);
        }
        readonly Dictionary<int,Vector3> navigationAnchors=new Dictionary<int,Vector3>();
        readonly List<Rect> navigationLabels=new List<Rect>(48);
        bool ReserveLabel(Rect r,Rect local){
            if(r.xMin<local.xMin||r.yMin<local.yMin+50||r.xMax>local.xMax||r.yMax>local.yMax)return false;
            foreach(var other in navigationLabels)if(other.Overlaps(r))return false;
            navigationLabels.Add(r);return true;
        }
        static readonly Color PinTint=new Color(.98f,.73f,.29f,1);
        static readonly Color DoorLockedTint=new Color(.96f,.48f,.31f,1);
        static readonly Color DoorOpenTint=new Color(.64f,.81f,.67f,.95f);
        internal BepInEx.Configuration.ConfigEntry<bool> NavigationIntel;
        WaypointGuide.SavedPin NearestPin(MapModel model,VisualInventory inventory,Rect rect,Vector2 mouse){
            WaypointGuide.SavedPin nearest=null;float best=16*16;
            foreach(var pin in model.Waypoint.Pins){
                if(model.LocateRoom(pin.Point)?.Renderable!=true||RelativeLayer(inventory.Elevations,pin.Point)!=0)continue;
                float score=(Project(pin.Point,rect)-mouse).sqrMagnitude;
                if(score<best){best=score;nearest=pin;}
            }
            return nearest;
        }
        readonly Dictionary<string,string> roomIcons=new Dictionary<string,string>(StringComparer.Ordinal);
        string RoomIcon(string source){
            source=source??"";
            if(VisualInventory.GlobalFast&&roomIcons.TryGetValue(source,out var result))return result;
            string name=source.ToLowerInvariant();
            string icon=name.Contains("shop")?"SHOP":name.Contains("end room")||name.Contains("exit")?"EXIT":
                name.Contains("boss")?"BOSS":name.Contains("treasure")?"LOOT":name.Contains("stair")?"STAIRS":
                name.Contains("entryfloor")?"START":name.Contains("choice room")?"CHOICE":"";
            if(VisualInventory.GlobalFast){
                if(roomIcons.Count>=256)roomIcons.Clear();
                roomIcons[source]=icon;
            }
            return icon;
        }
        void DrawNavigationIntel(MapModel model,VisualInventory inventory,Rect local,bool mini){
            if(Perspective||Event.current.type!=EventType.Repaint)return;
            navigationLabels.Clear();
            if(model.Waypoint.Active){
                var q=Project(model.Waypoint.Destination,local);
                navigationLabels.Add(new Rect(q.x-8,q.y-14,144,28));
            }
            foreach(var pin in model.Waypoint.Pins){
                if(model.LocateRoom(pin.Point)?.Renderable!=true||RelativeLayer(inventory.Elevations,pin.Point)!=0)continue;
                var q=Project(pin.Point,local);
                navigationLabels.Add(new Rect(q.x-7,q.y-12,110,24));
            }
            if(NavigationIntel?.Value!=false&&!mini){
                foreach(var room in model.Rooms){
                    if(!room.Renderable||room.Tile==null)continue;
                    string icon=RoomIcon(room.Name);
                    if(!navigationAnchors.TryGetValue(room.Key,out var p))continue;
                    if(RelativeLayer(inventory.Elevations,p)!=0)continue;
                    var pos=Project(p,local);
                    if(!local.Contains(pos))continue;
                    if(icon.Length>0){
                        float width=icon.Length*7+12;
                        var label=new Rect(pos.x-width/2,pos.y-9,width,18);
                        if(!ReserveLabel(label,local))continue;
                        Fill(label,new Color(.045f,.04f,.035f,.84f));
                        Label(label,icon,11,Ivory,TextAnchor.MiddleCenter);
                    }else if(radius<80){
                        var tint=room.Visited?new Color(.79f,.73f,.6f,.65f):new Color(.92f,.67f,.32f,.8f);
                        Fill(new Rect(pos.x-2,pos.y-2,4,4),tint);
                    }
                }
                if(radius<105)foreach(var room in model.Rooms){
                    if(!room.Renderable||room.Tile==null||!navigationAnchors.ContainsKey(room.Key))continue;
                    foreach(var door in room.Tile.AllDoorways){
                        if(door==null||door.ConnectedDoorway==null||door.GetInstanceID()>door.ConnectedDoorway.GetInstanceID())continue;
                        if(RelativeLayer(inventory.Elevations,door.transform.position)!=0)continue;
                        var pos=Project(door.transform.position,local);if(!local.Contains(pos))continue;
                        bool locked=door.IsLocked||door.ConnectedDoorway.IsLocked;
                        var tint=locked?DoorLockedTint:DoorOpenTint;
                        Fill(new Rect(pos.x-4,pos.y-4,8,8),new Color(.06f,.05f,.04f,.9f));
                        Fill(new Rect(pos.x-3,pos.y-3,6,6),tint);
                        if(locked){var label=new Rect(pos.x-16,pos.y-20,32,14);if(ReserveLabel(label,local))Label(label,"LOCK",9,DoorLockedTint,TextAnchor.MiddleCenter);}
                    }
                }
            }
            foreach(var pin in model.Waypoint.Pins){
                if(model.LocateRoom(pin.Point)?.Renderable!=true||RelativeLayer(inventory.Elevations,pin.Point)!=0)continue;
                var pos=Project(pin.Point,local);if(!local.Contains(pos))continue;
                if(model.Waypoint.Active&&Vector3.Distance(model.Waypoint.Destination,pin.Point)<.2f)continue;
                float size=mini?6:10;
                Fill(new Rect(pos.x-size/2-1,pos.y-size/2-1,size+2,size+2),new Color(.08f,.05f,.02f,.92f));
                Fill(new Rect(pos.x-size/2,pos.y-size/2,size,size),PinTint);
                if(!mini){
                    var label=new Rect(pos.x+8,pos.y-9,95,18);
                    Fill(new Rect(label.x,label.y,Mathf.Min(90,pin.Name.Length*7+8),label.height),new Color(.045f,.04f,.035f,.84f));
                    Label(label,pin.Name,11,PinTint);
                }
            }
        }
        internal void Draw(MapModel model,VisualInventory inventory,MapProjection mode,bool mini,bool markers,bool trail,Action<MapProjection> select,Action close)
        {
            LayoutMap(mini);
            var rect=mapArea;
            if(!mini && !framed && model.ActiveRoom!=null)
            {
                var bounds=model.ActiveRoom.WorldBounds;
                radius=Mathf.Max(10,Mathf.Max(bounds.size.x*rect.height/rect.width,bounds.size.z)*.61f);
                if(model.ReadyMode&&CameraMode==Viewpoint.Map){Fit(model,rect);CenterMapOnPlayer(model,inventory.Elevations);}
                framed=true;
            }
            selected=inventory.Elevations.PlayerFloor;
            if((Focusing || FloorMode.Value==FloorView.FloorSelection) && inventory.Elevations.Layers.Count>0)
                {SortLayers(inventory.Elevations);selected=sortedLayers[Mathf.Clamp(FloorIndex.Value,0,sortedLayers.Count-1)];}
            Configure(model,mode,mini);
            var e=Event.current;
            if(!mini && rect.Contains(e.mousePosition) && e.type==EventType.MouseDown && e.button==1 && !Captured && !e.shift && !e.control)
            {
                if(CameraMode==Viewpoint.Map){BeginMapOrbit(model);mode=ProjectionMode.Value;}
                else BeginHoldLook();
                e.Use();
            }
            if(!mini && !Perspective && !Captured && rect.Contains(e.mousePosition))
            {
                if(e.type==EventType.MouseDown&&(e.shift||e.control)&&(e.button==0||e.button==1)){
                    var nearby=NearestPin(model,inventory,rect,e.mousePosition);
                    if(e.control&&e.button==1)model.Waypoint.RemovePin(nearby);
                    else if(e.shift&&e.button==1)model.Waypoint.Clear();
                    else if(e.shift&&!e.control&&nearby!=null)model.Waypoint.SelectPin(model,nearby);
                    else{
                        float a=rect.width/rect.height;
                        var proj=Matrix4x4.Ortho(-radius*a,radius*a,-radius,radius,.1f,radius*4+130);
                        var inv=(proj*view).inverse;
                        float x=(e.mousePosition.x-rect.x)/rect.width*2-1,y=1-(e.mousePosition.y-rect.y)/rect.height*2;
                        var from=inv.MultiplyPoint(new Vector3(x,y,-1));
                        var to=inv.MultiplyPoint(new Vector3(x,y,1));
                        var ray=new Ray(from,(to-from).normalized);
                        float? floor=FloorMode.Value==FloorView.AllFloors?(float?)null:selected?.Height;
                        if(e.control)model.Waypoint.SavePin(model,ray,floor);
                        else model.Waypoint.Pick(model,ray,floor);
                    }
                    nextRender=0;e.Use();
                }
                else if(e.type==EventType.ScrollWheel)
                {
                    var cursor=e.mousePosition-rect.center;
                    var before=PlaneOffset(cursor,rect);
                    radius=Mathf.Clamp(radius*Mathf.Exp(e.delta.y*.075f),2,500);
                    center+=before-PlaneOffset(cursor,rect);follow=false;cameraVelocity=Vector3.zero;e.Use();nextRender=0;
                }
                else if(e.type==EventType.MouseDrag)
                {
                    if(e.button==0){center-=PlaneOffset(e.delta,rect);follow=false;cameraVelocity=Vector3.zero;}
                    e.Use();nextRender=0;
                }
            }
            Chrome(model,inventory,mode,mini,select,close);
            if(e.type!=EventType.Repaint) return;
            Configure(model,mode,mini);
            if(target==null || target.width!=(int)rect.width || target.height!=(int)rect.height)
            {
                ReleaseTarget();
                target=new RenderTexture((int)rect.width,(int)rect.height,24,SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf)?RenderTextureFormat.ARGBHalf:RenderTextureFormat.ARGB32,RenderTextureReadWrite.Linear);
                target.Create();nextRender=0;
            }
            int currentFloor=selected?.Id??-1;
            if(displayedFloor!=currentFloor || displayedMode!=FloorMode.Value)
            {
                if(displayedFloor>=0 && (Focusing || displayedMode==FloorView.Focus) && sceneReady)
                {
                    EnsureLayer(ref previousLayerFrame,target);
                    Graphics.Blit(target,previousLayerFrame);
                    layerFadeEnd=Time.unscaledTime+.20f;
                }
                displayedFloor=currentFloor;displayedMode=FloorMode.Value;nextRender=0;
            }
            float aspect=rect.width/rect.height;
            projection=Perspective?Matrix4x4.Perspective(Pose.Fov,aspect,.025f,1200):Matrix4x4.Ortho(-radius*aspect,radius*aspect,-radius,radius,.1f,radius*4+130);
            if(Time.frameCount!=lastRenderFrame && Time.unscaledTime>=nextRender)
            {
                lastRenderFrame=Time.frameCount;
                // Phase-preserving deadline: no extra frame lost to quantization.
                float now=Time.unscaledTime,interval=mini?Mathf.Clamp(Time.smoothDeltaTime*1.25f,1f/30,1f/18):1f/120;
#if MAP_QA
                if(!mini)interval=1f/MaxTargetHz;
#endif
                nextRender=mini||nextRender<=0||now-nextRender>interval*2?now+interval:nextRender+interval;
#if MAP_QA
                if(MaxLegacyCadence)nextRender=now+Mathf.Clamp(Time.smoothDeltaTime*1.25f,mini?1f/30:1f/45,1f/18);
#endif
                #if MAP_QA
                results.Clear();
#endif

                PrepareBlockerTest(model,inventory);if(HideBlockers.Value)BuildLensRays();
                bool nativeShop=NativeShopFrame=RenderNativeRoom(model,inventory,aspect);
                bool cut=HideBlockers.Value;
                if(cut)EnsureLayer(ref lensTarget,target);
                for(int layer=0;layer<=(cut?1:0);layer++)
                {
                bool reveal=layer==1;
                var output=reveal?lensTarget:target;
                solids.Clear();translucent.Clear();commands.Clear();commands.SetRenderTarget(output);
                if(nativeShop)
                {
                    var foreground=reveal && cutReady?cutScene:sceneTarget;
                    if(Focusing && contextReady)
                    {
                        if(contextBlend==null)
                        {
                            var shader=Shader.Find("Sprites/Default");
                            contextBlend=new Material(shader);focusBlend=new Material(shader);
                        }
                        commands.ClearRenderTarget(true,true,new Color(.11f,.09f,.08f).linear);
                        contextBlend.color=new Color(.84f,.88f,.91f,Mathf.Clamp01(BackgroundOpacity.Value*.01f));
                        commands.Blit(contextScene,output,contextBlend);
                        commands.Blit(foreground,output,focusBlend);
                    }
                    else commands.Blit(foreground,output);
                }
                commands.SetRenderTarget(output);
                if(!nativeShop)commands.ClearRenderTarget(true,true,new Color(.14f,.12f,.10f).linear);
                commands.SetViewProjectionMatrices(view,GL.GetGPUProjectionMatrix(projection,false));
                navigationAnchors.Clear();
                bool onePass=VisualInventory.GlobalFast;
                var floorView=FloorMode.Value;
                bool filter=floorView!=FloorView.AllFloors && selected!=null;
                bool intel=!mini&&NavigationIntel?.Value!=false;
                var activeRoom=model.ActiveRoom;
                var cutDirection=view.inverse.MultiplyVector(Vector3.back).normalized;
                var cutRay=new Ray(center-cutDirection*(radius*2+65),cutDirection);
                for(int pass=0;!nativeShop && pass<(onePass?1:2);pass++)
                foreach(var room in model.Rooms)
                {
                    if(!room.Renderable || !inventory.Rooms.TryGetValue(room.Key,out var parts))continue;
                    bool roomDisconnected=filter&&inventory.Elevations.DisconnectedUpstairs(activeRoom,room.Key,selected);
                    bool roomBalcony=filter&&floorView!=FloorView.Auto&&activeRoom?.Key==room.Key;
                    foreach(var part in parts)
                    {
                        if(!room.Visited&&!part.StaticScenery)continue;
#if MAP_QA
                        VisualInventory.MeshEvaluations++;
#endif
                        bool disconnected=onePass?roomDisconnected:filter&&inventory.Elevations.DisconnectedUpstairs(model.ActiveRoom,room.Key,selected);
                        bool belowBalcony=(onePass?roomBalcony:filter&&FloorMode.Value!=FloorView.Auto&&model.ActiveRoom?.Key==room.Key)&&inventory.Elevations.OverlooksShopFloor(room.Key,selected,part.Floor);
                        bool hidden=(reveal && IsObstruction(part)) || disconnected || part.Backdrop || (FloorMode.Value==FloorView.AllFloors && part.Roof) || filter && !part.Stair && part.Floor!=null && part.Floor!=selected && !belowBalcony;
                        if(filter && part.Roof && part.Bounds.min.y>selected.Height)hidden=true;
                        if(!hidden && filter && FloorMode.Value==FloorView.Cutaway && part.Floor==null && part.Bounds.min.y>selected.Height)
                        {
                            var ray=cutRay;
                            if(!onePass){var direction=view.inverse.MultiplyVector(Vector3.back).normalized;ray=new Ray(center-direction*(radius*2+65),direction);}
                            hidden=part.Bounds.IntersectRay(ray,out float distance)&&distance<radius*2+65;
                        }
                        Mesh drawMesh=part.Mesh;
                        if(!hidden && part.Visible && filter && part.Structural && drawMesh!=null)
                        {
                            float wallBase=belowBalcony?part.Floor.Height:selected.Height;
                            float low=wallBase-.35f,high=wallBase+1.35f;
                            if(part.Bounds.max.y<low || part.Bounds.min.y>high)hidden=true;
                            else if(drawMesh.isReadable && (part.Bounds.min.y<low || part.Bounds.max.y>high))
                            {
                                if(part.Section==null || !Mathf.Approximately(part.SectionHeight,wallBase))
                                {
                                    if(part.Section!=null)UnityEngine.Object.Destroy(part.Section);
                                    part.Section=WallSection.Build(drawMesh,part.Matrix,low,high);part.SectionHeight=wallBase;
                                }
                                drawMesh=part.Section;
                            }
                        }
                        bool empty=drawMesh==null || drawMesh.vertexCount==0 || drawMesh.subMeshCount==0;
#if MAP_QA
                        if(onePass||pass==1)results[part.Id]=!part.Visible?"inactive or renderer hidden":hidden?(disconnected?"no doorway on selected landing":part.Backdrop?"sky backdrop":"elevation cutaway"):empty?"no current geometry":part.Retained?"retained scenery submitted to GPU":"submitted to GPU";
#endif
                        if(!part.Visible || hidden || empty)continue;
                        bool ghost=false;
                        if(ghost){if(onePass||pass==1)translucent.Add(part);continue;}
                        if(onePass||pass==1){
                            if(intel&&!part.Roof&&part.Bounds.size.y<.6f&&part.Bounds.size.x>1f&&part.Bounds.size.z>1f){
                                var p=part.Bounds.center;
                                if(!navigationAnchors.TryGetValue(room.Key,out var old)||(new Vector2(p.x,p.z)-room.Center).sqrMagnitude<(new Vector2(old.x,old.z)-room.Center).sqrMagnitude)
                                    navigationAnchors[room.Key]=p;
                            }
                            solids.Add((part,drawMesh,view.MultiplyPoint3x4(part.Bounds.center).z));if(!onePass)continue;
                        }
                        var matrix=part.Matrix;
                        for(int i=0;i<part.Materials.Length;i++)
                        {
                            if(pass==0 && part.DepthPasses[i]>=0) commands.DrawMesh(drawMesh,matrix,part.DepthMaterials[i],i,part.DepthPasses[i],part.Properties[i]);

                        }
                    }
                }
                solids.Sort((a,b)=>a.depth.CompareTo(b.depth));
                foreach(var solid in solids)
                    for(int i=0;i<solid.part.Materials.Length;i++)
                        commands.DrawMesh(solid.mesh,solid.part.Matrix,solid.part.Materials[i],i,0,solid.part.Properties[i]);
                translucent.Sort(CompareDepth);
                foreach(var part in translucent)
                    for(int i=0;i<part.Materials.Length;i++)commands.DrawMesh(part.Mesh,part.Matrix,part.Materials[i],i,0,part.GhostProperties[i]);
                commands.ClearRenderTarget(true,false,Color.clear);
                if(trail)model.Route.Draw(commands,FloorMode.Value==FloorView.AllFloors?-1:selected?.Id??-1,Focusing,Mathf.Clamp01(BackgroundOpacity.Value*.01f));
                model.Waypoint.Draw(commands);
                spriteMarkers.Clear();
                if(markers)DrawCharacterArt(model,inventory,rect,mini);
                Graphics.ExecuteCommandBuffer(commands);
                }
                if(cut)CompositeLens();
                RenderedFloor=selected?.Name;RenderedProjection=mode;
            }
            GUI.DrawTexture(rect,target,ScaleMode.StretchToFill,false);
            if(previousLayerFrame!=null && Time.unscaledTime<layerFadeEnd)
            {
                var tint=GUI.color;
                GUI.color=new Color(tint.r,tint.g,tint.b,tint.a*Mathf.SmoothStep(0,1,(layerFadeEnd-Time.unscaledTime)*5));
                GUI.DrawTexture(rect,previousLayerFrame);GUI.color=tint;
            }
            GUI.BeginGroup(rect);
            var local=new Rect(0,0,rect.width,rect.height);
            if(markers)foreach(var marker in model.Markers)
            {
                if(spriteMarkers.Contains(marker.ObjectId) || marker.Actor==null || (!marker.LastKnown && !marker.Actor.gameObject.activeInHierarchy))continue;
                var pos=Project(marker.LastKnown?marker.WorldPosition:marker.Actor.position,local);
                int relative=RelativeLayer(inventory.Elevations,marker.WorldPosition);
                if(relative!=0)
                {
                    if(Focusing)Label(new Rect(pos.x-13,pos.y-10,26,20),(marker.LastKnown?"?":"â€¢")+(relative>0?"â†‘":"â†“"),13,Muted,TextAnchor.MiddleCenter);
                    continue;
                }
                if(marker.LastKnown){Label(new Rect(pos.x-11,pos.y-11,22,22),"?",16,Muted,TextAnchor.MiddleCenter);continue;}
                if(marker.Head==null)continue;
                float d=mini?19:26;
                GUI.DrawTextureWithTexCoords(new Rect(pos.x-d/2,pos.y-d/2,d,d),marker.Head,marker.PortraitUv,true);
            }
            DrawNavigationIntel(model,inventory,local,mini);
            if(model.Waypoint.Active){
                var pin=Project(model.Waypoint.Destination,local);
                if(local.Contains(pin)){
                    Fill(new Rect(pin.x-5,pin.y-5,10,10),WaypointGuide.Tint);
                    string label="WAYPOINT";
                    if(!mini)foreach(var saved in model.Waypoint.Pins)if(Vector3.Distance(saved.Point,model.Waypoint.Destination)<.2f){label=saved.Name+" / ROUTE";break;}
                    Label(new Rect(pin.x+8,pin.y-12,140,24),label,mini?10:13,WaypointGuide.Tint);
                }
                Label(new Rect(8,8,local.width-16,22),model.Waypoint.Status,mini?11:14,WaypointGuide.Tint);
            }
            else if(!mini&&!Perspective)Label(new Rect(8,8,local.width-16,22),model.Waypoint.Status,12,Muted);
            if(!mini&&!Perspective){
                string notice=model.Waypoint.CurrentPinNotice;
                Label(new Rect(8,30,local.width-16,18),notice.Length>0?notice:"Shift-click: route  |  Ctrl-click: save pin  |  Ctrl-right: remove  |  Shift-right: clear route",11,notice.Length>0?PinTint:Muted);
            }
            var player=Project(model.Player3D,local);
            PlayerPointer(player,PointerAngle(model,local),mini);
            var actual=inventory.Elevations.PlayerFloor;
            int playerLayer=selected!=null && actual!=null && FloorMode.Value!=FloorView.AllFloors?
                (Mathf.Abs(actual.Height-selected.Height)<.12f?0:Math.Sign(actual.Height-selected.Height)):0;
            if(playerLayer!=0)Label(new Rect(player.x+8,player.y-12,60,24),playerLayer>0?"â†‘ Above":"â†“ Below",11,Ivory,TextAnchor.MiddleLeft);
            if(!mini && !editMini && HideBlockers.Value && !CleanLens.Value)
            {
                float size=Mathf.Min(local.width,local.height)*LensRadius.Value*.01f;
                var mid=local.center;var tint=new Color(.85f,.75f,.54f,.55f);
                Fill(new Rect(mid.x-size,mid.y-2*ui,ui,4*ui),tint);
                Fill(new Rect(mid.x+size,mid.y-2*ui,ui,4*ui),tint);
                Fill(new Rect(mid.x-2*ui,mid.y-size,4*ui,ui),tint);
                Fill(new Rect(mid.x-2*ui,mid.y+size,4*ui,ui),tint);
            }
            Cartography(local,mini);
            GUI.EndGroup();
        }
        internal bool NativeShopFrame { get; private set; }
        internal int ObstructionCandidates(MapModel model,VisualInventory inventory)
        {
            int n=0;
            foreach(var room in model.Rooms)if(room.Renderable&&inventory.Rooms.TryGetValue(room.Key,out var parts))
                foreach(var part in parts)if(part.Visible&&part.LargeBarrier)n++;
            return n;
        }
        Light roomLight;
        internal BepInEx.Configuration.ConfigEntry<bool> MapHideBlockers,FreeHideBlockers,PlayerHideBlockers;
        internal BepInEx.Configuration.ConfigEntry<bool> HideBlockers =>
            CameraMode==Viewpoint.Free?FreeHideBlockers:CameraMode==Viewpoint.Player?PlayerHideBlockers:MapHideBlockers;
        // Each occupied cell records the depth of room detail visible along that camera ray.
        const int Columns=64, Rows=48;
        readonly float[] detailDepth=new float[Columns*Rows];
        readonly List<(Ray ray,float z,float detail)> lensRays=new List<(Ray,float,float)>(Columns*Rows/4);
        Vector3 sight,cameraFocus;
        float halfWidth;
        float roomAspect=>mapArea.width/Mathf.Max(1,mapArea.height);
        void MarkDetail(Vector3 point)
        {
            if(Perspective)
            {
                var eye=view.MultiplyPoint(point);if(eye.z>=-.025f)return;
                var ndc=projection.MultiplyPoint(eye);
                int col=Mathf.FloorToInt((ndc.x+1)*Columns*.5f),row=Mathf.FloorToInt((ndc.y+1)*Rows*.5f);
                if(col>=0 && col<Columns && row>=0 && row<Rows)detailDepth[row*Columns+col]=Mathf.Max(detailDepth[row*Columns+col],-eye.z);
                return;
            }
            var p=point-cameraFocus;
            int x=Mathf.FloorToInt((Vector3.Dot(p,right)/halfWidth+1)*Columns*.5f);
            int y=Mathf.FloorToInt((Vector3.Dot(p,up)/radius+1)*Rows*.5f);
            if(x<0 || x>=Columns || y<0 || y>=Rows)return;
            int i=y*Columns+x;
            detailDepth[i]=Mathf.Max(detailDepth[i],Vector3.Dot(p,sight));
        }
        readonly List<Bounds> detailPrevious=new List<Bounds>(),detailCurrent=new List<Bounds>();
        readonly Dictionary<int,(Bounds bounds,bool result)> obstructionCache=new Dictionary<int,(Bounds,bool)>();
        Vector3 previousFocus,previousRight,previousUp,previousSight;
        Matrix4x4 previousView,previousProjection;
        float previousRadius,previousHalfWidth,previousLens,previousAspect;
        Vector3 previousPosePosition;
        Quaternion previousPoseRotation;
        bool previousPerspective,detailValid;
        void PrepareBlockerTest(MapModel model,VisualInventory inventory)
        {
            if(!VisualInventory.ReadyFastPath || !model.ReadyMode){
                detailValid=false;obstructionCache.Clear();
                PrepareBlockerTestBaseline(model,inventory);return;
            }
            if(!HideBlockers.Value){detailValid=false;obstructionCache.Clear();return;}
            sight=Vector3.Cross(right,up).normalized;
            cameraFocus=new Vector3(center.x,selected?.Height??center.y,center.z);
            halfWidth=radius*mapArea.width/mapArea.height;
            detailCurrent.Clear();
            foreach(var room in model.Rooms)
            {
                if(!room.Renderable || !inventory.Rooms.TryGetValue(room.Key,out var parts))continue;
                foreach(var part in parts)
                {
                    if((!room.Visited&&!part.StaticScenery) || !part.Visible || part.Backdrop || part.Roof || part.LargeBarrier)continue;
                    if(selected!=null && FloorMode.Value!=FloorView.AllFloors && !part.Stair &&
                        part.Floor!=null && part.Floor.Height>selected.Height+.12f)continue;
                    detailCurrent.Add(part.Bounds);
                }
            }
            bool same=detailValid && previousPerspective==Perspective &&
                previousFocus.Equals(cameraFocus)&&previousRight.Equals(right)&&previousUp.Equals(up)&&previousSight.Equals(sight)&&
                previousRadius==radius&&previousHalfWidth==halfWidth&&previousLens==LensRadius.Value&&previousAspect==roomAspect&&
                (!Perspective||(previousView.Equals(view)&&previousProjection.Equals(projection)&&previousPosePosition.Equals(Pose.Position)&&previousPoseRotation.Equals(Pose.Rotation)))&&
                detailCurrent.Count==detailPrevious.Count;
            if(same)for(int i=0;i<detailCurrent.Count;i++)if(!detailCurrent[i].Equals(detailPrevious[i])){same=false;break;}
            if(same){
                return;
            }
            detailPrevious.Clear();detailPrevious.AddRange(detailCurrent);
            previousFocus=cameraFocus;previousRight=right;previousUp=up;previousSight=sight;
            previousRadius=radius;previousHalfWidth=halfWidth;previousLens=LensRadius.Value;
            previousView=view;previousProjection=projection;previousPerspective=Perspective;previousAspect=roomAspect;
            if(Perspective){previousPosePosition=Pose.Position;previousPoseRotation=Pose.Rotation;}
            detailValid=true;obstructionCache.Clear();
            Array.Fill(detailDepth,float.NegativeInfinity);
            foreach(var b in detailCurrent){
                MarkDetail(b.center);
                if(b.size.y<.8f && b.extents.x*b.extents.z>1)
                    for(int i=-1;i<=1;i++)
                        for(int j=-1;j<=1;j++)MarkDetail(b.center+new Vector3(i*b.extents.x*.78f,0,j*b.extents.z*.78f));
            }
        }
        void PrepareBlockerTestBaseline(MapModel model,VisualInventory inventory)
        {
            if(!HideBlockers.Value)return;
            sight=Vector3.Cross(right,up).normalized;
            cameraFocus=new Vector3(center.x,selected?.Height??center.y,center.z);
            halfWidth=radius*mapArea.width/mapArea.height;
            Array.Fill(detailDepth,float.NegativeInfinity);
            foreach(var room in model.Rooms)
            {
                if(!room.Renderable || !inventory.Rooms.TryGetValue(room.Key,out var parts))continue;
                foreach(var part in parts)
                {
                    if((!room.Visited&&!part.StaticScenery) || !part.Visible || part.Backdrop || part.Roof || part.LargeBarrier)continue;
                    if(selected!=null && FloorMode.Value!=FloorView.AllFloors && !part.Stair &&
                        part.Floor!=null && part.Floor.Height>selected.Height+.12f)continue;
                    var b=part.Bounds;
                    MarkDetail(b.center);
                    if(b.size.y<.8f && b.extents.x*b.extents.z>1)
                        for(int i=-1;i<=1;i++)
                            for(int j=-1;j<=1;j++)MarkDetail(b.center+new Vector3(i*b.extents.x*.78f,0,j*b.extents.z*.78f));
                }
            }
        }
        void BuildLensRays()
        {
            lensRays.Clear();
            if(!Perspective)return;
            int cx=Columns/2,cy=Rows/2;
            float screenRadius=LensRadius.Value*.01f;
            int rx=Mathf.Max(1,Mathf.CeilToInt(Columns*screenRadius/roomAspect)),ry=Mathf.Max(1,Mathf.CeilToInt(Rows*screenRadius));
            for(int row=Mathf.Max(0,cy-ry);row<Mathf.Min(Rows,cy+ry+1);row+=2)
            for(int col=Mathf.Max(0,cx-rx);col<Mathf.Min(Columns,cx+rx+1);col+=2)
            {
                float detail=detailDepth[row*Columns+col];if(detail<=0)continue;
                var local=new Vector3((2f*(col+.5f)/Columns-1)/projection.m00,(2f*(row+.5f)/Rows-1)/projection.m11,1).normalized;
                lensRays.Add((new Ray(Pose.Position,Pose.Rotation*local),local.z,detail));
            }
        }
        static float ProjectExtent(Vector3 e,Vector3 axis) =>
            Mathf.Abs(axis.x)*e.x+Mathf.Abs(axis.y)*e.y+Mathf.Abs(axis.z)*e.z;
        bool IsObstruction(VisualInventory.Part part)
        {
            if(!VisualInventory.ReadyFastPath||!detailValid||!HideBlockers.Value||!part.LargeBarrier)
                return IsObstructionBaseline(part);
            bool result;
            if(obstructionCache.TryGetValue(part.Id,out var cached)&&cached.bounds.Equals(part.Bounds))result=cached.result;
            else{result=IsObstructionBaseline(part);obstructionCache[part.Id]=(part.Bounds,result);}
            return result;
        }
        bool IsObstructionBaseline(VisualInventory.Part part,bool reference=false)
        {
            if(!HideBlockers.Value || !part.LargeBarrier)return false;
            var b=part.Bounds;
            if(b.size.y<1.6f || Mathf.Max(b.size.x,b.size.z)<2.5f)return false;
            if(Perspective)
            {
                foreach(var sample in lensRays)
                    if(b.IntersectRay(sample.ray,out float hit) && hit*sample.z+.2f<sample.detail)return true;
                return false;
            }
            var delta=b.center-cameraFocus;
            float x=Vector3.Dot(delta,right),y=Vector3.Dot(delta,up);
            float w=ProjectExtent(b.extents,right),h=ProjectExtent(b.extents,up);
            float aperture=Mathf.Min(radius,halfWidth)*LensRadius.Value*.02f;
            if(Mathf.Abs(x)>w+aperture || Mathf.Abs(y)>h+aperture)return false;

            int x0=Mathf.Max(0,Mathf.CeilToInt(((x-w)/halfWidth+1)*Columns*.5f-.5f));
            int x1=Mathf.Min(Columns-1,Mathf.FloorToInt(((x+w)/halfWidth+1)*Columns*.5f-.5f));
            int y0=Mathf.Max(0,Mathf.CeilToInt(((y-h)/radius+1)*Rows*.5f-.5f));
            int y1=Mathf.Min(Rows-1,Mathf.FloorToInt(((y+h)/radius+1)*Rows*.5f-.5f));
            if(VisualInventory.ReadyFastPath&&!reference){
                float depth=Vector3.Dot(delta,sight);
                if(depth>=0f)return false;
                float threshold=depth+.75f;
                for(int row=y0;row<=y1;row++)
                    for(int col=x0;col<=x1;col++)
                        if(detailDepth[row*Columns+col]>threshold)return true;
            }else
                for(int row=y0;row<=y1;row++)
                    for(int col=x0;col<=x1;col++)
                        if(detailDepth[row*Columns+col]>Vector3.Dot(delta,sight)+.75f &&
                           Vector3.Dot(b.center-cameraFocus,sight)<0f)return true;
            return false;
        }
#if MAP_QA
        internal void TestOrbit(Vector3 target,float heading,float pitch)
        {
            follow=false;center=target;yaw=heading;tilt=pitch;nextRender=0;
        }
#endif
        internal BepInEx.Configuration.ConfigEntry<float> LensRadius,LensFeather,LensStrength;
        internal BepInEx.Configuration.ConfigEntry<bool> TransparentLens,CleanLens;
        RenderTexture lensTarget,cutScene;
        Camera cutCamera;
        Material lensMaterial;
        bool cutReady;
        static void EnsureLayer(ref RenderTexture layer,RenderTexture template)
        {
            if(layer!=null && layer.width==template.width && layer.height==template.height)return;
            if(layer!=null){layer.Release();UnityEngine.Object.Destroy(layer);}
            layer=new RenderTexture(template.descriptor);layer.Create();
        }
        void CompositeLens()
        {
            if(lensMaterial==null)lensMaterial=new Material(Shader.Find("Sprites/Default"));
            lensMaterial.mainTexture=lensTarget;
            var previous=RenderTexture.active;bool previousWrite=GL.sRGBWrite;RenderTexture.active=target;
            GL.sRGBWrite=target.sRGB && QualitySettings.activeColorSpace==ColorSpace.Linear;
            GL.PushMatrix();
            try
            {
                GL.LoadOrtho();lensMaterial.SetPass(0);
                float outer=LensRadius.Value*.01f*Mathf.Min(target.width,target.height);
                float inner=outer*(1-Mathf.Clamp01(LensFeather.Value*.01f));
                float alpha=TransparentLens.Value?LensStrength.Value*.01f:1f;
                // Editing guides are drawn by the UI. The radial mask is active in both modes.
                GL.Begin(GL.TRIANGLES);
                for(int i=0;i<80;i++)
                {
                    float a=i*Mathf.PI/40,b=(i+1)*Mathf.PI/40;
                    Vertex(0,0,alpha);Vertex(a,inner,alpha);Vertex(b,inner,alpha);
                    Vertex(a,inner,alpha);Vertex(a,outer,0);Vertex(b,outer,0);
                    Vertex(a,inner,alpha);Vertex(b,outer,0);Vertex(b,inner,alpha);
                }
                GL.End();
            }
            finally{GL.PopMatrix();RenderTexture.active=previous;GL.sRGBWrite=previousWrite;}
        }
        void Vertex(float angle,float distance,float alpha)
        {
            float x=.5f+Mathf.Cos(angle)*distance/target.width,y=.5f+Mathf.Sin(angle)*distance/target.height;
            GL.Color(new Color(1,1,1,alpha));GL.TexCoord2(x,y);GL.Vertex3(x,y,0);
        }
        Camera roomCamera;
        RenderTexture sceneTarget;
        MapModel cameraModel;
        VisualInventory cameraInventory;
        bool sceneReady, savedFog;
        readonly Dictionary<int,Renderer[]> tileArt=new Dictionary<int,Renderer[]>();
        readonly List<Renderer> hiddenArt=new List<Renderer>();
        readonly List<Renderer> revealedCache=new List<Renderer>();
        float nextTileRefresh;
        int nativeScene=-1,nativeRoom=-1;
        int nativeFloor=-1;
        float nativeFloorHeight=float.NaN;
        FloorView nativeFloorView;
        int cachedVisited=-1,cachedGeneration=-1;
        readonly List<Renderer> excluded=new List<Renderer>();
        Renderer[] currentSceneRenderers=Array.Empty<Renderer>();
        readonly HashSet<int> allowedNativeRenderers=new HashSet<int>();
        // Actors are not necessarily parented to their room tile. Keep their
        // live eligibility separate from the slower static scenery survey.
        readonly Dictionary<int,Renderer> nativeActorBodies=new Dictionary<int,Renderer>();
        readonly HashSet<int> eligibleNativeActorBodies=new HashSet<int>();
        readonly HashSet<int> knownNativeRooms=new HashSet<int>();
        readonly System.Diagnostics.Stopwatch proxyBudget=new System.Diagnostics.Stopwatch();
        readonly Plane[] cameraPlanes=new Plane[6];
        bool nativeCameraScope;
        internal void SuspendNativeCamera()
        {
            RestoreNativeCamera();
            if(roomCamera!=null)roomCamera.enabled=false;
            if(cutCamera!=null)cutCamera.enabled=false;
            if(contextCamera!=null)contextCamera.enabled=false;
            nextRender=0;
        }
        internal void ResetNativeScene()
        {
            SuspendNativeCamera();
            ClearPausedActorArt();contextReady=false;displayedFloor=-1;layerFadeEnd=0;sortedMap=null;framedHeight=float.NaN;sceneReady=false;cutReady=false;nativeScene=-1;nativeRoom=-1;
            nativeFloor=-1;nativeFloorHeight=float.NaN;
            cameraModel=null;cameraInventory=null;
            currentSceneRenderers=Array.Empty<Renderer>();cachedVisited=-1;excluded.Clear();detailValid=false;detailCurrent.Clear();detailPrevious.Clear();obstructionCache.Clear();
            allowedNativeRenderers.Clear();knownNativeRooms.Clear();
            nativeActorBodies.Clear();eligibleNativeActorBodies.Clear();
            tileArt.Clear();nextTileRefresh=0;nextRender=0;
        }
        void RestoreNativeCamera()
        {
            pausedActorArt.Restore();
            if(!nativeCameraScope)return;
            foreach(var item in hiddenArt)if(item!=null)item.forceRenderingOff=false;
            hiddenArt.Clear();
            foreach(var proxy in revealedCache)if(proxy!=null)proxy.forceRenderingOff=true;
            revealedCache.Clear();
            if(roomLight!=null)roomLight.enabled=false;
            RenderSettings.fog=savedFog;nativeCameraScope=false;
        }
        void HideForCamera(Renderer renderer)
        {
            if(renderer==null || renderer.forceRenderingOff)return;
            hiddenArt.Add(renderer);
            renderer.forceRenderingOff=true;
        }
        void PrepareNativeActors(MapModel model,VisualInventory inventory,Camera camera)
        {
            nativeActorBodies.Clear();
            eligibleNativeActorBodies.Clear();
            foreach(var marker in model.Markers)
            {
                if(marker.Actor==null || marker.Body==null)continue;
                bool live=!marker.LastKnown && marker.Actor.gameObject.activeInHierarchy &&
                    marker.Controller!=null && marker.Controller.isActiveAndEnabled;
                int relative=RelativeLayer(inventory.Elevations,marker.WorldPosition);
                bool onFloor=FloorMode.Value==FloorView.AllFloors || relative==0;
                bool eligible=live && (camera==contextCamera?!onFloor:onFloor);
                foreach(var renderer in marker.Body)
                {
                    if(renderer==null)continue;
                    int id=renderer.GetInstanceID();
                    nativeActorBodies[id]=renderer;
                    if(eligible)eligibleNativeActorBodies.Add(id);
                }
            }
            // Never enable a source renderer. Only temporarily hide actors on
            // other floors, and let RestoreNativeCamera undo our own changes.
            foreach(var entry in nativeActorBodies)
                if(!eligibleNativeActorBodies.Contains(entry.Key))HideForCamera(entry.Value);
        }
        bool RenderNativeRoom(MapModel model,VisualInventory inventory,float aspect)
        {
            var room=model.ActiveRoom;
            if(room?.Tile==null || !room.Visited || selected==null)return false;
            // Same native camera renderer for every discovered room type.
            // All floors and remembered scenery use the same native camera and source materials.
            int scene=UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
            if(nativeScene!=scene){ResetNativeScene();nativeScene=scene;}
            // Never reuse a completed image belonging to a different level.
            if(nativeRoom!=room.Key || nativeFloor!=selected.Id || nativeFloorView!=FloorMode.Value ||
                Mathf.Abs(nativeFloorHeight-selected.Height)>.02f)
            {
                nativeRoom=room.Key;nativeFloor=selected.Id;nativeFloorHeight=selected.Height;
                nativeFloorView=FloorMode.Value;sceneReady=false;cutReady=false;
            }

            var source=model.GameplayCamera;
            if(source==null)return false;
            if(roomCamera==null)
            {
                var host=new GameObject("Private world map camera");
                host.hideFlags=HideFlags.HideAndDontSave;
                roomLight=host.AddComponent<Light>();roomLight.type=LightType.Directional;roomLight.intensity=1.15f;roomLight.shadows=LightShadows.None;roomLight.enabled=false;
                roomCamera=host.AddComponent<Camera>();roomCamera.enabled=false;
                if(GraphicsSettings.currentRenderPipeline==null)
                {
                    Camera.onPreCull+=BeginBuiltinCamera;Camera.onPostRender+=EndBuiltinCamera;
                }
                RenderPipelineManager.beginCameraRendering+=BeginSceneCamera;
                RenderPipelineManager.endCameraRendering+=EndSceneCamera;
            }
            roomCamera.CopyFrom(source);
            roomCamera.cullingMask=(Perspective?source.cullingMask:~0)&~(1<<5);roomCamera.ResetCullingMatrix();
            roomCamera.enabled=false;roomCamera.targetTexture=null;
            var pose=view.inverse;
            roomCamera.transform.SetPositionAndRotation(pose.MultiplyPoint(Vector3.zero),Quaternion.LookRotation(-pose.MultiplyVector(Vector3.forward),up));
            roomCamera.orthographic=true;roomCamera.orthographicSize=radius;roomCamera.aspect=aspect;
            roomCamera.nearClipPlane=.1f;roomCamera.farClipPlane=radius*4+130;
            var focus=new Vector3(center.x,framedHeight,center.z);
            roomCamera.transform.position=focus-roomCamera.transform.forward*(radius+12);
            roomCamera.ResetWorldToCameraMatrix();roomCamera.ResetProjectionMatrix();
            if(Perspective)
            {
                roomCamera.transform.SetPositionAndRotation(Pose.Position,Pose.Rotation);
                roomCamera.orthographic=false;roomCamera.fieldOfView=Pose.Fov;roomCamera.nearClipPlane=.025f;roomCamera.farClipPlane=1200;
            }
            roomCamera.projectionMatrix=projection;
            roomCamera.ResetCullingMatrix();
            roomCamera.useOcclusionCulling=false;roomCamera.clearFlags=CameraClearFlags.SolidColor;
            roomCamera.backgroundColor=Focusing?Color.clear:new Color(.11f,.09f,.08f);
            if(sceneTarget==null || sceneTarget.width!=target.width || sceneTarget.height!=target.height)
            {
                if(sceneTarget!=null){sceneTarget.Release();UnityEngine.Object.Destroy(sceneTarget);}
                sceneTarget=new RenderTexture(target.width,target.height,24,RenderTextureFormat.ARGB32);
                sceneTarget.Create();sceneReady=false;
            }
            cameraModel=model;cameraInventory=inventory;
            roomCamera.targetTexture=sceneTarget;roomCamera.depth=source.depth-2;roomCamera.enabled=true;
            if(Focusing)
            {
                if(contextCamera==null)
                {
                    var host=new GameObject("Private map background floors"){hideFlags=HideFlags.HideAndDontSave};
                    contextCamera=host.AddComponent<Camera>();
                }
                int backgroundWidth=Math.Max(1,sceneTarget.width/2),backgroundHeight=Math.Max(1,sceneTarget.height/2);
                if(contextScene==null || contextScene.width!=backgroundWidth || contextScene.height!=backgroundHeight)
                {
                    if(contextScene!=null){contextScene.Release();UnityEngine.Object.Destroy(contextScene);}
                    contextScene=new RenderTexture(backgroundWidth,backgroundHeight,24,RenderTextureFormat.ARGB32){filterMode=FilterMode.Bilinear};
                    contextScene.Create();contextReady=false;
                }
                contextCamera.CopyFrom(roomCamera);
                contextCamera.transform.SetPositionAndRotation(roomCamera.transform.position,roomCamera.transform.rotation);
                contextCamera.targetTexture=contextScene;contextCamera.depth=roomCamera.depth-1;
                contextCamera.enabled=true;
            }
            else if(contextCamera!=null)contextCamera.enabled=false;
            if(HideBlockers.Value)
            {
                if(cutCamera==null)
                {
                    var host=new GameObject("Private map cutaway");host.hideFlags=HideFlags.HideAndDontSave;
                    cutCamera=host.AddComponent<Camera>();
                }
                if(cutScene==null||cutScene.width!=sceneTarget.width||cutScene.height!=sceneTarget.height)cutReady=false;
                EnsureLayer(ref cutScene,sceneTarget);
                cutCamera.CopyFrom(roomCamera);
                cutCamera.transform.SetPositionAndRotation(roomCamera.transform.position,roomCamera.transform.rotation);
                cutCamera.targetTexture=cutScene;cutCamera.depth=roomCamera.depth-.5f;cutCamera.enabled=true;
            }
            else {cutReady=false;if(cutCamera!=null)cutCamera.enabled=false;}
            return sceneReady;
        }
        void BeginBuiltinCamera(Camera c)=>BeginSceneCamera(default,c);
        void EndBuiltinCamera(Camera c)=>EndSceneCamera(default,c);
        void BeginSceneCamera(ScriptableRenderContext context,Camera camera)
        {
            if((camera!=roomCamera && camera!=cutCamera && camera!=contextCamera) || cameraModel?.ActiveRoom==null)return;
            #if MAP_QA
            MaxBegin(camera);
#endif
            RestoreNativeCamera();
            UsingRetainedScenery=false;
            savedFog=RenderSettings.fog;nativeCameraScope=true;
            try
            {
                RenderSettings.fog=false;
                GeometryUtility.CalculateFrustumPlanes(camera,cameraPlanes);
                if(roomLight!=null)roomLight.enabled=!Perspective;
                var model=cameraModel;var room=model.ActiveRoom;var inventory=cameraInventory;
                // Keep the source game's original native geometry and materials.
                // Reveal only geometry belonging to explored tiles; loose scene
                // objects are eligible only when associated with an explored tile.
                if(Time.unscaledTime>=nextTileRefresh || cachedVisited!=model.Revision || cachedGeneration!=model.DungeonGeneration)
                {
                    cachedVisited=model.Revision;cachedGeneration=model.DungeonGeneration;
                    nextTileRefresh=Time.unscaledTime+.65f;
                    allowedNativeRenderers.Clear();excluded.Clear();
                    foreach(var other in model.Rooms)
                    {
                        if(!other.Renderable || other.Tile==null)continue;
                        if(!VisualInventory.ReadyFastPath || other.Visited)
                            foreach(var rr in other.Tile.GetComponentsInChildren<Renderer>(true))
                                if(rr!=null && other.Visited)allowedNativeRenderers.Add(rr.GetInstanceID());
                        if(inventory.Rooms.TryGetValue(other.Key,out var list))
                            foreach(var part in list)if(part.Source!=null && (other.Visited||part.StaticScenery))allowedNativeRenderers.Add(part.Id);
                    }
                    foreach(var rr in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
                        if(rr!=null && !allowedNativeRenderers.Contains(rr.GetInstanceID()))
                        {
                            bool effect=rr is ParticleSystemRenderer || rr is TrailRenderer || rr is LineRenderer || rr is SkinnedMeshRenderer;
                            if(effect && rr.GetComponentInParent<DunGen.Tile>()==null && model.LocateRoom(rr.bounds.center)?.Visited==true)continue;
                            excluded.Add(rr);
                        }
                }
                HideForCamera(model.Waypoint.WorldRenderer);
                HideForCamera(model.Waypoint.PulseRenderer);
                PrepareNativeActors(model,inventory,camera);
#if MAP_QA
                QaInjectCachedActorExclusion();
#endif
                foreach(var rr in excluded)
                    if(rr!=null && !eligibleNativeActorBodies.Contains(rr.GetInstanceID()))HideForCamera(rr);
                if(MapPause.Active)
                    foreach(var entry in nativeActorBodies)
                        if(eligibleNativeActorBodies.Contains(entry.Key) && entry.Value is SpriteRenderer sprite)
                            pausedActorArt.Apply(sprite);
                proxyBudget.Restart();
                bool fast=VisualInventory.GlobalFast;
                var mode=FloorMode.Value;
                bool perspective=Perspective,focus=Focusing,paused=MapPause.Active;
                bool cutPass=camera==cutCamera,contextPass=camera==contextCamera;
                bool filterFloor=mode!=FloorView.AllFloors,checkBalcony=filterFloor&&mode!=FloorView.Auto;
                bool floorFocus=focus||mode==FloorView.FloorSelection||mode==FloorView.Auto;
                foreach(var other in model.Rooms)
                {
                    if(!other.Renderable)continue;
                    if(!inventory.Rooms.TryGetValue(other.Key,out var parts))continue;
                    bool disconnected=(fast?filterFloor:FloorMode.Value!=FloorView.AllFloors)&&inventory.Elevations.DisconnectedUpstairs(room,other.Key,selected);
                    bool shop=other.Tile!=null&&other.Tile.name=="Shop(Clone)";
                    foreach(var part in parts)
                    {
                        // Live actors already have their own camera/floor policy.
                        // Do not mistake a loose enemy for a structural blocker.
#if MAP_QA
                        VisualInventory.NativeEvaluations++;
#endif
                        if(nativeActorBodies.ContainsKey(part.Id))continue;
                        if(!other.Visited&&!part.StaticScenery){HideForCamera(part.Source);continue;}
                        bool belowBalcony=(fast?checkBalcony:FloorMode.Value!=FloorView.AllFloors&&FloorMode.Value!=FloorView.Auto)&&inventory.Elevations.OverlooksShopFloor(other.Key,selected,part.Floor);
                        bool obstruction=(fast?cutPass:camera==cutCamera)&&IsObstruction(part);
                        bool hidden=obstruction || disconnected || part.Backdrop || (!(fast?perspective:Perspective) && part.Roof);
                        if(fast?floorFocus:Focusing||FloorMode.Value==FloorView.FloorSelection||FloorMode.Value==FloorView.Auto)
                        {
                            bool stairConnection=part.Stair && part.Bounds.min.y<=selected.Height+1.35f && part.Bounds.max.y>=selected.Height-.35f;
                            bool onFloor=stairConnection || (!disconnected && (part.Floor==null || Mathf.Abs(part.Floor.Height-selected.Height)<.12f));
                            hidden=part.Backdrop || (!(fast?perspective:Perspective) && part.Roof) || ((fast?contextPass:camera==contextCamera)?onFloor:!onFloor || obstruction);
                        }
                        else if(!hidden && selected!=null && (fast?filterFloor:FloorMode.Value!=FloorView.AllFloors))
                        {
                            // Use the existing floor ownership. Never merge
                            // stacked landings or discard an upper stair by name.
                            // Match the floor ownership rules of the original
                            // elevation-aware command-buffer map, in both directions.
                            // Keep tall structural walls that actually span this level.
                            bool spansLevel=part.Structural && part.Bounds.min.y<=selected.Height+1.35f &&
                                part.Bounds.max.y>=selected.Height-.35f;
                            hidden=!part.Stair && part.Floor!=null &&
                                Mathf.Abs(part.Floor.Height-selected.Height)>.12f && !belowBalcony && !(spansLevel && (fast?mode:FloorMode.Value)!=FloorView.Auto);
                            if(!hidden && (fast?mode:FloorMode.Value)==FloorView.Cutaway && part.Floor==null &&
                                part.Bounds.min.y>selected.Height+1.35f && !part.Stair)hidden=true;
                            if((fast?shop:other.Tile!=null&&other.Tile.name=="Shop(Clone)") && part.Bounds.min.y>selected.Height+3.4f)hidden=true;
                        }
                        if(hidden || part.Retained)HideForCamera(part.Source);
                        if((fast?paused:MapPause.Active) && !hidden && !part.Retained && part.Visible && part.Source is SpriteRenderer scenery && GeometryUtility.TestPlanesAABB(cameraPlanes,part.Bounds))
                            if(pausedActorArt.ApplyScenery(scenery))HideForCamera(scenery);
                        bool retainedNative=!hidden && part.Retained && GeometryUtility.TestPlanesAABB(cameraPlanes,part.Bounds);
                        if(retainedNative)
                        {
                            part.LastProxyUse=Time.unscaledTime;
                            if(part.NativeProxy==null && proxyBudget.Elapsed.TotalMilliseconds<1.5)VisualInventory.EnsureNativeProxy(part);
                            retainedNative=part.NativeProxy!=null;
                        }
                        if(retainedNative)
                        {
                            UsingRetainedScenery=true;
                            if(fast?paused:MapPause.Active)pausedActorArt.ApplyRetainedScenery(part);
                            part.NativeProxy.forceRenderingOff=false;
                            revealedCache.Add(part.NativeProxy);
                        }
#if MAP_QA
                        RecordSceneFrames(camera,part);
                        results[part.Id]=hidden?"native camera cutaway":retainedNative?"retained native camera eligible":part.Visible&&!part.Retained?"native camera eligible":"inactive or renderer hidden";
#endif
                    }
                }
#if MAP_QA
                RecordNativeActorFrame(camera); QaRecordAnimation(camera); QaCustomFrame(camera); QaRecordWorldAnimation(camera);
#endif
            }
            catch(Exception ex)
            {
                RestoreNativeCamera();
                sceneReady=false;cutReady=false;camera.enabled=false;
                Debug.LogWarning("[PNC MAP ALL ROOMS] Native camera failed; using existing GPU map: "+ex.Message);
            }
        }
        void EndSceneCamera(ScriptableRenderContext context,Camera camera)
        {
            if(camera!=roomCamera && camera!=cutCamera && camera!=contextCamera)return;
            #if MAP_QA
            MaxEnd(camera);
#endif
            bool completed=nativeCameraScope;
            RestoreNativeCamera();
            if(completed){if(camera==contextCamera)contextReady=true;else if(camera==cutCamera)cutReady=true;else sceneReady=true;}
            camera.enabled=false;
        }
        internal float PointerAngle(MapModel model,Rect rect)
        {
            var position=model.Player3D;
            var forward=Quaternion.Euler(0,model.CameraHeading,0)*Vector3.forward;
            var look=Project(position+forward,rect)-Project(position,rect);
            return Mathf.Atan2(look.y,look.x)*Mathf.Rad2Deg+90;
        }
        void ReleaseTarget(){if(target!=null){target.Release();UnityEngine.Object.Destroy(target);target=null;}}
        public void Dispose(){ResetNativeScene();if(contextCamera!=null)UnityEngine.Object.Destroy(contextCamera.gameObject);if(contextScene!=null){contextScene.Release();UnityEngine.Object.Destroy(contextScene);}if(contextBlend!=null)UnityEngine.Object.Destroy(contextBlend);if(focusBlend!=null)UnityEngine.Object.Destroy(focusBlend);if(cutCamera!=null)UnityEngine.Object.Destroy(cutCamera.gameObject);if(lensMaterial!=null)UnityEngine.Object.Destroy(lensMaterial);foreach(var rt in new[]{lensTarget,cutScene,previousLayerFrame})if(rt!=null){rt.Release();UnityEngine.Object.Destroy(rt);}Camera.onPreCull-=BeginBuiltinCamera;Camera.onPostRender-=EndBuiltinCamera;RenderPipelineManager.beginCameraRendering-=BeginSceneCamera;RenderPipelineManager.endCameraRendering-=EndSceneCamera;if(sceneTarget!=null){sceneTarget.Release();UnityEngine.Object.Destroy(sceneTarget);}if(roomCamera!=null)UnityEngine.Object.Destroy(roomCamera.gameObject);ReleaseTarget();commands.Release();if(headingFont!=null)UnityEngine.Object.Destroy(headingFont);if(pointer!=null)UnityEngine.Object.Destroy(pointer);if(spriteMaterial!=null)UnityEngine.Object.Destroy(spriteMaterial);}
    }
}