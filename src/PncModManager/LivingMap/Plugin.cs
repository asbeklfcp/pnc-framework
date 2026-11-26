using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using PncFramework.Controls;
using BepInEx.Unity.Mono;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.InputSystem;
using DunGen;
using Object = UnityEngine.Object;

namespace PncPrivateLivingMap
{
    // Player-facing map; detailed preferences are edited in the map itself.
    [BepInPlugin(Id, "PNC Living Map", "0.1.13")]
    [BepInDependency("com.pnc.modmanager", BepInDependency.DependencyFlags.HardDependency)]
    [BepInProcess("Post Nut Calamity.exe")]
    public sealed partial class Plugin : BaseUnityPlugin
    {
        public const string Id = "com.pnc.private.living-map";
        internal const string MapActionId=Id+"/World Map/ToggleMap";
        internal static string MapShortcutLabel
        {
            get{
                var entry=ControlService.Catalog.Get(MapActionId);
                return entry==null||entry.Bindings.Count==0?"Unassigned":string.Join(" / ",entry.Bindings.Select(x=>x.Display));
            }
        }
        private ConfigEntry<bool> _enableMap;
        private ConfigEntry<bool> _showCharacters, _showTrail;
        private MapModel _model;
        private PortraitLibrary _view;
        readonly MapPause pause=new MapPause();
        private bool _expanded;
        private ConfigEntry<MapProjection> _miniMode, _fullMode;
        private VisualInventory _inventory;
        private WorldMapSurface _miniSurface, _fullSurface;
        private float _nextUiErrorLog;
        private bool _restoreCursor;
        private CursorLockMode _restoreLock;
        private bool _resumeGameplayCursor,_cursorHandoffPending;
        private int _cursorHandoffFrames;
        private float _nextCursorHandoff;
        private float _nextRoomScan;
        private float _nextEntityScan;
        private bool _lastSeenLevel;
        private PropertyInfo _managerIsOpen;

        private HarmonyLib.Harmony actorHooks;
        private static MapModel liveRegistry;
        static void Pooled(GameObject __0){if(__0!=null)liveRegistry?.ReturnActor(__0);}
        static void CheckedOut(GameObject __result){if(__result!=null)liveRegistry?.CheckoutActor(__result);}
        static void PhaseChanged(ThreadMistressAI __instance){if(__instance!=null)liveRegistry?.ForgetActor(__instance.gameObject);}
        static void Died(MonoBehaviour __instance){if(__instance!=null)liveRegistry?.ForgetActor(__instance.gameObject);}
        void MonitorActors()
        {
            liveRegistry=_model;
            actorHooks=new HarmonyLib.Harmony(Id+".actor-lifecycle");
            actorHooks.Patch(HarmonyLib.AccessTools.Method(typeof(EnemySpawner),"ReturnEnemyToPool"),prefix:new HarmonyLib.HarmonyMethod(typeof(Plugin),nameof(Pooled)));
            actorHooks.Patch(HarmonyLib.AccessTools.Method(typeof(EnemySpawner),"GetPooledEnemy"),postfix:new HarmonyLib.HarmonyMethod(typeof(Plugin),nameof(CheckedOut)));
            actorHooks.Patch(HarmonyLib.AccessTools.Method(typeof(ThreadMistressAI),"TransformToStageTwo"),postfix:new HarmonyLib.HarmonyMethod(typeof(Plugin),nameof(PhaseChanged)));
            foreach(var t in MapModel.NativeActorComponents)
            {
                var death=t.GetMethod("Die",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly,null,Type.EmptyTypes,null);
                if(death!=null)actorHooks.Patch(death,postfix:new HarmonyLib.HarmonyMethod(typeof(Plugin),nameof(Died)));
            }
        }
        private void Awake()
        {
            try
            {
                _managerIsOpen = HarmonyLib.AccessTools.Property(HarmonyLib.AccessTools.TypeByName("PncModManager.Plugin"), "IsOpen");
                _enableMap = Config.Bind("World Map", "Enable Map", true,
                    "Show the real discovered-room minimap. Change the Open / Close Map binding in the Mod Manager Controls page. Disabling removes the map immediately.");
                _showCharacters = Config.Bind("World Map", "Character Markers", true,
                    "Show head-image markers for live, active enemies and special characters in explored rooms. Their positions come from game objects.");
                _enableMap.SettingChanged += (_, __) => { if (!_enableMap.Value) CloseExpanded(); };
                _model = new MapModel(Logger);
                _model.ReadySetting=Config.Bind("World Map","Ready Map",false,"Preload scenery of all generated rooms without changing exploration progress or activating gameplay.");
                _model.ReadyMode=_model.ReadySetting.Value;
                MonitorActors();MapPause.Install(actorHooks);
                _view = new PortraitLibrary(Paths.BepInExRootPath);
                SceneManager.activeSceneChanged += OnSceneChange;
                _miniMode = Config.Bind("World Map", "Minimap Mode", MapProjection.Overhead, "Projection used during gameplay.");
                _fullMode = Config.Bind("World Map", "Expanded Map Mode", MapProjection.Orbit, "Projection used when the map shortcut is pressed.");
                _showTrail=Config.Bind("World Map","Movement Trail",true,"Display the recorded route on both map surfaces.");
            _model.Waypoint.Motion=Config.Bind("Navigation","Animated directional guidance",true,"Animate subtle chevrons along the route. Disable for stationary chevrons.");
            _model.Waypoint.DestinationHud=Config.Bind("Navigation","Nearby destination indicator",true,"Show distance at a visible destination within 22 units.");

                _inventory = new VisualInventory();
                _miniSurface = new WorldMapSurface();
                _fullSurface = new WorldMapSurface(){MiniControls=_miniSurface};
                _miniSurface.MiniX=Config.Bind("Minimap Placement","Horizontal position",1f,new ConfigDescription("Saved normalized horizontal placement. Edit by dragging in the expanded map Minimap settings.",new AcceptableValueRange<float>(0,1)));
                _miniSurface.MiniY=Config.Bind("Minimap Placement","Vertical position",0f,new ConfigDescription("Saved normalized vertical placement. Edit by dragging in the expanded map Minimap settings.",new AcceptableValueRange<float>(0,1)));
                ControlService.RegisterAction(new ActionEntry(MapActionId,"Open / Close Map","Open or close the living map during gameplay.",Id,ActionContext.Gameplay,new[]{BindingSpec.Parse("keyboard:M")}));
            _fullSurface.NavigationIntel=Config.Bind("Navigation","Map intelligence",true,"Show important room labels and locked/unlocked doors in expanded map.");
            _miniSurface.NavigationIntel=_fullSurface.NavigationIntel;
                _fullSurface.CharacterMarkers=_showCharacters;_fullSurface.MovementTrail=_showTrail;
                _fullSurface.FlySpeed=Config.Bind("World Map","Virtual camera speed",5f,new BepInEx.Configuration.ConfigDescription("Virtual movement speed",new BepInEx.Configuration.AcceptableValueRange<float>(1,20)));
                _fullSurface.LookGain=Config.Bind("World Map","Virtual look multiplier",1f,new BepInEx.Configuration.ConfigDescription("Mouse sensitivity multiplier",new BepInEx.Configuration.AcceptableValueRange<float>(.2f,3f)));
                _fullSurface.SmoothMove=Config.Bind("World Map","Smooth camera movement",true,"Ease camera movement in and out.");
                _miniSurface.ProjectionMode=_miniMode;_fullSurface.ProjectionMode=_fullMode;
                _miniSurface.FloorMode=Config.Bind("World Map","Minimap Floor View",FloorView.AllFloors,"Select elevation presentation; show all explored layers by default.");
                _miniSurface.BackgroundOpacity=Config.Bind("World Map","Minimap background layer opacity",12f,"Opacity percentage of other elevations in Focus Layer.");
                _miniSurface.LensRadius=Config.Bind("World Map","Minimap LensRadius",15f,"Circle radius as a percentage of the shorter map edge.");
                _miniSurface.LensFeather=Config.Bind("World Map","Minimap LensFeather",24f,"Soft edge width as a percentage of the circle radius.");
                _miniSurface.LensStrength=Config.Bind("World Map","Minimap LensStrength",65f,"Transparency inside the circle.");
                _miniSurface.TransparentLens=Config.Bind("World Map","Minimap TransparentLens",false,"Keep foreground walls partly visible inside the circle.");
                _miniSurface.CleanLens=Config.Bind("World Map","Minimap Hide Lens Circle",false,"Hide cutaway editing controls. Radius, feather and transparency apply in both views.");
                _miniSurface.MapHideBlockers=Config.Bind("World Map","Minimap hide obstructions",false,"Hide foreground scenery covering the map focus.");
                _miniSurface.FloorIndex=Config.Bind("World Map","Minimap Floor Index",0,"Floor in increasing elevation order.");
                _fullSurface.FloorMode=Config.Bind("World Map","Expanded Floor View",FloorView.AllFloors,"Select elevation presentation; show all explored layers by default.");
                _fullSurface.BackgroundOpacity=Config.Bind("World Map","Expanded background layer opacity",12f,"Opacity percentage of other elevations in Focus Layer.");
                _fullSurface.LensRadius=Config.Bind("World Map","Expanded LensRadius",15f,"Circle radius as a percentage of the shorter map edge.");
                _fullSurface.LensFeather=Config.Bind("World Map","Expanded LensFeather",24f,"Soft edge width as a percentage of the circle radius.");
                _fullSurface.LensStrength=Config.Bind("World Map","Expanded LensStrength",65f,"Transparency inside the circle.");
                _fullSurface.TransparentLens=Config.Bind("World Map","Expanded TransparentLens",false,"Keep foreground walls partly visible inside the circle.");
                _fullSurface.CleanLens=Config.Bind("World Map","Expanded Hide Lens Circle",false,"Hide cutaway editing controls. Radius, feather and transparency apply in both views.");
                _fullSurface.FreeHideBlockers=Config.Bind("World Map","Free Cam hide obstructions",false,"Apply the obstruction cutaway in Free Cam.");
                _fullSurface.PlayerHideBlockers=Config.Bind("World Map","Player Cam hide obstructions",false,"Apply the obstruction cutaway in Player Cam.");
                _fullSurface.MapHideBlockers=Config.Bind("World Map","Expanded hide obstructions",false,"Hide foreground scenery covering the map focus.");
                _fullSurface.FloorIndex=Config.Bind("World Map","Expanded Floor Index",0,"Floor in increasing elevation order.");
#if MAP_QA
                StartCoroutine(CompareActualRoom());
#endif
            }
            catch (Exception e) { Logger.LogError("[LIVING MAP] Initialization failed: " + e); enabled = false; }
        }

        private void OnSceneChange(Scene from, Scene to)
        {
            _fullSurface?.ResetViewpoints();
            _miniSurface?.ResetNativeScene();_fullSurface?.ResetNativeScene();
            _inventory?.Dispose();
            CloseExpanded();
            _model?.Clear();
            _cursorHandoffPending=false;

            _nextRoomScan = 0;
            _nextEntityScan = 0;
            _lastSeenLevel = false;
        }

        private bool IsMapVisible()
        {
            if (!_enableMap.Value || _model == null || _view == null || !_model.HasPlayer)
                return false;
            if (SceneManager.GetActiveScene().name.IndexOf("menu", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            try
            {
                if (_managerIsOpen != null && (bool)_managerIsOpen.GetValue(null, null)) return false;
            }
            catch { }
            return true;
        }

        private void Update()
        {
            if (_model == null || _enableMap == null) return;
            if (!_enableMap.Value) { CloseExpanded(); _model.Waypoint.Clear(); return; }
            if(_model.ReadySetting!=null)_model.ReadyMode=_model.ReadySetting.Value;
            float t = Time.unscaledTime;
            if (t >= _nextRoomScan)
            {
                _nextRoomScan = t + 1.7f;
                _model.ScanWorld();
                // Trigger one bounded native scenery snapshot per explored room.
                // Does not modify game renderers/materials and never invents
                // unexplored floor content.

                if (_model.HasPlayer && !_lastSeenLevel)
                {
                    _lastSeenLevel = true;
                    Logger.LogInfo("[LIVING MAP] Player acquired; world tiles=" + _model.TileCount +
                        ", visited=" + _model.DiscoveredCount + ". Using actual scene geometry only.");
                }
                if (!_model.HasPlayer) _lastSeenLevel = false;
            }
            if (!_model.HasPlayer) { CloseExpanded(); _model.Waypoint.Clear(); return; }
            _model.TickPlayer(t);
            if(_fullSurface.SyncCameraWorld(_model,_inventory.Elevations) && _expanded)
                _fullSurface.ResetPerspectiveForOpen(_model,_inventory.Elevations);
            _model.Waypoint.Tick(_model,IsMapVisible(),_expanded);

            if (_showCharacters.Value && t >= _nextEntityScan)
            {
                _nextEntityScan = t + 0.15f;
                _model.ScanEnemies(_view);
            }
            else if (!_showCharacters.Value) _model.ClearMarkers();

            if (PressedMapKey() && IsMapVisible() && (_expanded || !NativeUiOwnsCursor()))
            {
                if (_expanded) CloseExpanded();
                else OpenExpanded();
            }
            if(_expanded){pause.TickAudio();if(!_miniSurface.PlacementEditing)_fullSurface.Navigate(_model);}
            if (_expanded && !IsMapVisible()) CloseExpanded();
            if(IsMapVisible()) _inventory.Update(_model);
#if MAP_QA
            if(!_model.RecordedProbe.HasValue)
#endif
            _model.Route.Record(_model.Player3D,t,_inventory.Elevations.PlayerFloor?.Id??-1);
        }

        private bool PressedMapKey()
        {
            return !TextEntryOwnsMapKey() && ControlService.IsActionDown(MapActionId);
        }

        private static bool TextEntryOwnsMapKey()
        {
            if (InputManager.IsConsoleOpen || RebindEntry.IsCapturingInput) return true;
            var events = UnityEngine.EventSystems.EventSystem.current;
            var selected = events != null ? events.currentSelectedGameObject : null;
            if (selected == null) return false;
            var tmp = selected.GetComponentInParent<TMPro.TMP_InputField>();
            if (tmp != null && tmp.isActiveAndEnabled && tmp.isFocused) return true;
            var legacy = selected.GetComponentInParent<UnityEngine.UI.InputField>();
            return legacy != null && legacy.isActiveAndEnabled && legacy.isFocused;
        }

        private void OpenExpanded()
        {
            if (_expanded) return;
            if(_model.RefreshDungeonIdentity()){_model.ScanWorld();_model.TickPlayer(Time.unscaledTime);}
            _restoreCursor = Cursor.visible;
            _restoreLock = Cursor.lockState;
            _resumeGameplayCursor = !NativeUiOwnsCursor() && _model!=null && _model.HasPlayer
                && InputManager.Look!=null && InputManager.Look.enabled && Time.timeScale>0f;
            pause.Open();if(!MapPause.Active)return;
            _cursorHandoffPending=false;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            _expanded = true;
            _fullSurface.ResetPerspectiveForOpen(_model,_inventory.Elevations);

        }

        private void CloseExpanded()
        {

            if (!_expanded) return;
            _expanded = false;
            try
            {
                _miniSurface?.CancelPlacement();
                _fullSurface.LeaveVirtual();
                _fullSurface.SuspendNativeCamera();
                _fullSurface.ClearPausedActorArt();_miniSurface.ClearPausedActorArt();
            }
            finally
            {
                pause.Close();
                _cursorHandoffPending=_resumeGameplayCursor;
                _cursorHandoffFrames=3;_nextCursorHandoff=0;
                if(Application.isFocused && !NativeUiOwnsCursor())
                {
                    if(_resumeGameplayCursor && CanResumeGameplayCursor())ApplyGameplayCursor();
                    else if(!_resumeGameplayCursor){Cursor.lockState=_restoreLock;Cursor.visible=_restoreCursor;}
                }
            }
        }

        private void OnGUI()
        {
            if (!IsMapVisible() || Event.current == null) return;
            try
            {
                var oldColor = GUI.color;
                var oldDepth = GUI.depth;
                var oldMatrix = GUI.matrix;
                try
                {
                    GUI.matrix = Matrix4x4.identity;
                    GUI.depth = -180;
                    GUI.color = Color.white;
                    #if MAP_QA
                    if(_qaHide) return;
#endif
                    if(_expanded && _miniSurface.PlacementEditing)
                    {
                        _miniSurface.DrawPlacementEditor(_model,_inventory,_miniMode.Value,_showCharacters.Value,_showTrail.Value);
                    }
                    else if (_expanded) _fullSurface.Draw(_model,_inventory,_fullMode.Value,false,_showCharacters.Value,_showTrail.Value,m=>_fullMode.Value=m,CloseExpanded);
                    else {_miniSurface.Draw(_model,_inventory,_miniMode.Value,true,_showCharacters.Value,_showTrail.Value,null,CloseExpanded);_model.Waypoint.DrawDestinationHud(_model);}
                }
                finally { GUI.color = oldColor; GUI.depth = oldDepth; GUI.matrix = oldMatrix; }
            }
            catch (Exception e)
            {
                // Never make a mod render failure break the game.
                // Previous modulo-1200 logging silently lost many short
                // failures during opening and GUI interaction. Log the first
                // error immediately and throttle subsequent stack traces.
                if(Time.unscaledTime>=_nextUiErrorLog){
                    _nextUiErrorLog=Time.unscaledTime+5f;
                    Logger.LogWarning("[LIVING MAP UI ERROR] "+e);
                }
            }
        }

        private bool NativeUiOwnsCursor()
        {
            if(TextEntryOwnsMapKey() || ControlService.IsCapturing || ControlService.BlockGameplay)return true;
            try{if(_managerIsOpen!=null && (bool)_managerIsOpen.GetValue(null,null))return true;}catch{}
            var nativePause=Object.FindAnyObjectByType<PixelCrushers.GridController.PauseMenuManager>();
            if(nativePause!=null && nativePause.IsPaused)return true;
            var inventory=Object.FindAnyObjectByType<PixelCrushers.GridController.InventoryUI>();
            if(inventory!=null && inventory.IsInventoryOpen)return true;
            return (GrabScreen.Instance!=null && GrabScreen.Instance.IsGrabbed)
                || (GrappleScreenobject.Instance!=null && GrappleScreenobject.Instance.IsGrappling);
        }
        private bool CanResumeGameplayCursor()=>Application.isFocused && !_expanded && !MapPause.Active
            && _model!=null && _model.HasPlayer && Time.timeScale>0f && InputManager.Look!=null && InputManager.Look.enabled && !NativeUiOwnsCursor();
        private static void ApplyGameplayCursor()
        {
            Cursor.lockState=CursorLockMode.Locked;
            Cursor.visible=false;
        }
        private void LateUpdate()
        {
            if(!_cursorHandoffPending || _expanded)return;
            if(Time.unscaledTime<_nextCursorHandoff)return;
            if(!CanResumeGameplayCursor()){_nextCursorHandoff=Time.unscaledTime+.10f;return;}
            ApplyGameplayCursor();
            if(--_cursorHandoffFrames<=0)_cursorHandoffPending=false;
        }
        private void OnDisable(){CloseExpanded();_cursorHandoffPending=false;_model?.Waypoint.Clear();}
        private void OnApplicationFocus(bool focus)
        {
            if(!focus && _expanded){_fullSurface.ReleaseVirtualInput();_miniSurface?.EndPlacementDrag();}
            if(focus && _expanded){_fullSurface.Capture(false);}
            else if(focus && _cursorHandoffPending){_nextCursorHandoff=0;}
        }

        private void OnDestroy()
        {
            CloseExpanded();
            actorHooks?.UnpatchSelf();liveRegistry=null;
            SceneManager.activeSceneChanged -= OnSceneChange;
            _inventory?.Dispose(); _miniSurface?.Dispose(); _fullSurface?.Dispose();
            try { _view?.Dispose(); } catch { }
            _model?.Waypoint.Dispose();
            _model?.Clear();
        }
    }

    internal sealed class Room
    {
        public int Key;
        public Tile Tile;
        public Bounds WorldBounds;
        public bool Visited;
        internal bool ReadyReveal;
        internal bool Renderable => Visited || ReadyReveal;
        public float FirstVisitedHeight;
        public string Name;
        public bool IsBridge;
        public string GeometrySource="ROOM BOUNDS";
        public int FootprintRevision;
        public readonly List<FloorTriangle> Footprint=new List<FloorTriangle>();
        public readonly List<int> Neighbors = new List<int>();
        public Vector2 Center => new Vector2(WorldBounds.center.x, WorldBounds.center.z);
    }
    internal sealed class PointOfInterest
    {
        public Vector2 Position;
        public string Name;
    }
    internal sealed class Marker
    {
        public int ObjectId;
        public Transform Actor;
        public readonly List<Renderer> Body=new List<Renderer>(8);
        public MonoBehaviour Controller;
        public bool LastKnown;
        public float LastSeen;
        public readonly List<SpriteRenderer> Artwork=new List<SpriteRenderer>(8);
        public Vector3 WorldPosition;
        public Vector2 Position;
        public string Name;
        public Texture2D Head;
        public Rect PortraitUv;
    }

    internal sealed partial class MapModel
    {
        internal readonly WaypointGuide Waypoint=new WaypointGuide();
        private readonly BepInEx.Logging.ManualLogSource _log;
        private readonly Dictionary<int, Room> _rooms = new Dictionary<int, Room>();
        private readonly Dictionary<int,Marker> tracked=new Dictionary<int,Marker>();
        private readonly List<int> retired=new List<int>();
        private int discoveryBucket;
        private readonly List<Marker> _markers = new List<Marker>();
        private readonly List<PointOfInterest> _points = new List<PointOfInterest>();
        internal readonly RouteTrace Route=new RouteTrace();
        private readonly List<Vector2> _trail = new List<Vector2>(512);
        private readonly HashSet<int> _visited = new HashSet<int>();
        private int _dungeonId;
        private int _dungeonRootTile;
        private int _sceneHandle;
        private bool _reportedMissing;
        private PlayerStats _playerStats;
        private Transform _player;
        private Type _wallTrapType;
        private Type _witchControllerType;

        private readonly Dictionary<Type,FieldInfo> _healthFields = new Dictionary<Type,FieldInfo>();
        private readonly Dictionary<Type,FieldInfo> _deadFields = new Dictionary<Type,FieldInfo>();
        private float _nextCameraCheck;
        private Camera _viewCamera;


        public int Revision { get; private set; }
        internal int DungeonGeneration { get; private set; }
        public IReadOnlyCollection<Room> Rooms => _rooms.Values;
        public IReadOnlyList<Marker> Markers => _markers;
        public IReadOnlyList<PointOfInterest> Points => _points;
        public IReadOnlyList<Vector2> Trail => _trail;
        public bool HasPlayer => _player != null && _player.gameObject.activeInHierarchy;
        #if MAP_QA
        internal Vector3? RecordedProbe;
        public Vector3 Player3D => RecordedProbe ?? (_player != null ? _player.position : Vector3.zero);
#else
        public Vector3 Player3D => _player != null ? _player.position : Vector3.zero;
#endif
        public Vector2 Player => new Vector2(Player3D.x, Player3D.z);
        public float Heading => _player != null ? _player.eulerAngles.y : 0f;
        // Use PNC's actual first-person controller camera, not a scene-wide
        // MainCamera tag (which can refer to an overlay or an auxiliary camera).
        private FirstPersonController _firstPerson;
        private MonoBehaviour _lookController;
        private static readonly FieldInfo CameraField=HarmonyLib.AccessTools.Field(typeof(FirstPersonController),"cameraComponent");
        private static readonly FieldInfo LookField=HarmonyLib.AccessTools.Field(typeof(FirstPersonController),"cameraController");
        private static readonly Type LookType=HarmonyLib.AccessTools.TypeByName("CMF.CameraController");
        private static readonly MethodInfo FacingMethod=LookType==null?null:HarmonyLib.AccessTools.Method(LookType,"GetFacingDirection");
        private Vector3 _lastLook=Vector3.forward;
        void ResolveLookCamera()
        {
            if(Time.unscaledTime<_nextCameraCheck&&_firstPerson!=null&&_viewCamera!=null&&_viewCamera.isActiveAndEnabled)return;
            _nextCameraCheck=Time.unscaledTime+1.0f;
            try{
                _firstPerson=_player!=null?_player.GetComponentInParent<FirstPersonController>():null;
                if(_firstPerson==null&&_player!=null)_firstPerson=_player.GetComponentInChildren<FirstPersonController>(true);
                if(_firstPerson==null)_firstPerson=Object.FindAnyObjectByType<FirstPersonController>();
                _viewCamera=_firstPerson!=null?CameraField?.GetValue(_firstPerson) as Camera:null;
                _lookController=_firstPerson!=null?LookField?.GetValue(_firstPerson) as MonoBehaviour:null;
                if(_viewCamera==null&&_lookController!=null)_viewCamera=_lookController.GetComponentInChildren<Camera>(true);
                if(_viewCamera==null&&_firstPerson!=null)_viewCamera=_firstPerson.GetComponentInChildren<Camera>(true);
                if(_viewCamera==null)_viewCamera=Camera.main;
            }catch{_viewCamera=Camera.main;}
        }
        internal Camera GameplayCamera { get {ResolveLookCamera();return _viewCamera;} }
        internal MonoBehaviour GameplayLookController { get {ResolveLookCamera();return _lookController;} }
        public float CameraHeading {
            get {
                ResolveLookCamera();
                Vector3 f=Vector3.zero;
                if(_viewCamera!=null&&_viewCamera.isActiveAndEnabled)f=_viewCamera.transform.forward;
                f.y=0;
                // Near-vertical look has no reliable horizontal bearing.
                // Prefer the native controller's horizontal facing there.
                if(f.sqrMagnitude<.001f&&_lookController!=null&&FacingMethod!=null){
                    try{f=(Vector3)FacingMethod.Invoke(_lookController,null);f.y=0;}catch{}
                }
                if(f.sqrMagnitude>.001f)_lastLook=f.normalized;
                else if(_viewCamera==null&&_lookController==null)_lastLook=Quaternion.Euler(0,Heading,0)*Vector3.forward;
                return Mathf.Atan2(_lastLook.x,_lastLook.z)*Mathf.Rad2Deg;
            }
        }
        public int TileCount => _rooms.Count;
        public int DiscoveredCount => _visited.Count;
        bool readyMode;
        internal ConfigEntry<bool> ReadySetting;
        internal bool ReadyMode {
            get => readyMode;
            set {
                if(readyMode==value)return;
                readyMode=value;
                if(ReadySetting!=null&&ReadySetting.Value!=value)ReadySetting.Value=value;
                foreach(var room in _rooms.Values)room.ReadyReveal=value;
                Revision++;
                _log.LogInfo("[LIVING MAP] Ready Map "+(value?"enabled":"disabled")+"; actual visits="+DiscoveredCount);
            }
        }
        internal int RenderableCount => readyMode?_rooms.Count:_visited.Count;
        public Room ActiveRoom { get; private set; }
        public MapModel(BepInEx.Logging.ManualLogSource logger) { _log = logger; }

        public void Clear()
        {
            DungeonGeneration++; Waypoint.ResetDungeon(); pooled.Clear();ClearMarkers();
            _rooms.Clear(); _markers.Clear(); _points.Clear(); _trail.Clear(); Route.Clear(); _visited.Clear();
            _dungeonId = 0; _dungeonRootTile=0; _sceneHandle = 0; _player = null; _playerStats = null;
             _reportedMissing = false; ActiveRoom=null;
             _nextCameraCheck=0; _viewCamera=null; _firstPerson=null;_lookController=null;_lastLook=Vector3.forward; Revision++;
        }

        internal bool RefreshDungeonIdentity()=>ObserveDungeonIdentity(Object.FindAnyObjectByType<Dungeon>());
        bool ObserveDungeonIdentity(Dungeon dungeon)
        {
            if(dungeon==null)return false; // loading gap is not a new generation
            int id=dungeon.GetInstanceID();
            int root=0;
            if(dungeon.AllTiles!=null && dungeon.AllTiles.Count>0 && dungeon.AllTiles[0]!=null)
                root=dungeon.AllTiles[0].GetInstanceID();
            bool changed=(_dungeonId!=0 && id!=_dungeonId)
                || (_dungeonId==id && _dungeonRootTile!=0 && root!=0 && root!=_dungeonRootTile);
            if(changed)
            {
                DungeonGeneration++; Waypoint.ResetDungeon(); pooled.Clear();ClearMarkers();
                _rooms.Clear(); _visited.Clear(); _trail.Clear(); Route.Clear(); ActiveRoom=null;Revision++;
                _nextCameraCheck=0;_viewCamera=null;_firstPerson=null;_lookController=null;
                _dungeonRootTile=0;
            }
            _dungeonId=id;if(root!=0)_dungeonRootTile=root;
            return changed;
        }
        public void ScanWorld()
        {
            if (_player == null)
            {
                _playerStats = Object.FindAnyObjectByType<PlayerStats>();
                if (_playerStats != null && _playerStats.isActiveAndEnabled)
                    _player = _playerStats.transform;
                else
                {
                    GameObject go = null;
                    try { go = GameObject.FindGameObjectWithTag("Player"); } catch { }
                    _player = go != null && go.activeInHierarchy ? go.transform : null;
                }
            }
            if (_player == null) return;
            var scene = SceneManager.GetActiveScene();
            if (_sceneHandle != 0 && _sceneHandle != scene.handle) { Clear(); return; }
            _sceneHandle = scene.handle;
            var dungeon = Object.FindAnyObjectByType<Dungeon>();
            ObserveDungeonIdentity(dungeon);
            IEnumerable<Tile> tiles = null;
            try
            {
                if (dungeon != null && dungeon.AllTiles != null && dungeon.AllTiles.Count > 0)
                    tiles = dungeon.AllTiles;
                else tiles = Object.FindObjectsByType<Tile>(FindObjectsSortMode.None);
            }
            catch { tiles = Object.FindObjectsByType<Tile>(FindObjectsSortMode.None); }

            int added = 0;
            foreach (var tile in tiles)
            {
                if (tile == null || (!readyMode && !tile.isActiveAndEnabled) || tile.gameObject.scene != scene) continue;
                Bounds b;
                try { b = tile.Bounds; } catch { continue; }
                if (b.size.x < 0.5f || b.size.z < 0.5f || b.size.x > 1000f || b.size.z > 1000f) continue;
                int id = tile.GetInstanceID();
                if (!_rooms.TryGetValue(id, out var entry))
                {
                    entry = new Room { Key = id, Tile = tile, WorldBounds = b,Name=RoomGeometry.CleanName(tile.name) };

                    _rooms.Add(id, entry); added++;
                }
                else {entry.WorldBounds = b;entry.Tile=tile;}
                entry.Visited = _visited.Contains(id); entry.ReadyReveal=readyMode;
            }
            if (added > 0)
            {
                foreach (var room in _rooms.Values)
                {
                    if (room.Tile == null) continue;
                    room.Neighbors.Clear();
                    try
                    {
                        foreach (var tile in room.Tile.GetAdjacentTiles())
                            if (tile != null && _rooms.ContainsKey(tile.GetInstanceID()))
                                room.Neighbors.Add(tile.GetInstanceID());
                    }
                    catch { }
                }
                Revision++;
            }
            var removed = _rooms.Where(kv => kv.Value.Tile == null || kv.Value.Tile.gameObject.scene != scene).Select(kv => kv.Key).ToArray();
            foreach (var id in removed)
            {
                // A discovered room belongs to this dungeon snapshot even
                // when its runtime tile is unloaded. Never reactivate it.
                // Scene/new-dungeon changes clear all remembered snapshots.
                if(_visited.Contains(id)||readyMode) { _rooms[id].Tile=null; continue; }
                _rooms.Remove(id); Revision++;
            }
            _points.Clear();
            try
            {
                foreach(var exit in Object.FindObjectsByType<LevelTransition>(FindObjectsSortMode.None))
                {
                    if(exit!=null&&exit.enabled&&exit.gameObject.activeInHierarchy&&exit.gameObject.scene==scene)
                        _points.Add(new PointOfInterest{Position=new Vector2(exit.transform.position.x,exit.transform.position.z),Name="EXIT"});
                }
            }
            catch { }
            if (_rooms.Count == 0 && !_reportedMissing)
            {
                _reportedMissing = true;
                _log.LogInfo("[LIVING MAP] Player present but DunGen rooms not ready; displaying genuine player exploration trail until rooms spawn.");
            }
        }

        public void TickPlayer(float t)
        {
            if (!HasPlayer) return;
            var p = Player;
            Room best=LocateRoom(Player3D);
            ActiveRoom=best;
            if (best != null && _visited.Add(best.Key))
            {
                best.FirstVisitedHeight=Player3D.y;
                best.Visited = true; Revision++;
                if (_visited.Count <= 4 || _visited.Count % 5 == 0)
                    _log.LogInfo("[LIVING MAP] Explored actual tile '" + best.Tile.name + "' (" + _visited.Count + "/" + _rooms.Count + ").");
            }

        }

        internal Room LocateRoom(Vector3 world)
        {
            // A collider belongs to the floor the actor is actually standing on.
            // Ignore triggers. Querying physics here does not step the simulation.
            if(Physics.Raycast(world+Vector3.up*1.8f,Vector3.down,out var hit,4f,Physics.DefaultRaycastLayers,QueryTriggerInteraction.Ignore))
            {
                var tile=hit.collider.GetComponentInParent<Tile>();
                if(tile!=null && _rooms.TryGetValue(tile.GetInstanceID(),out var owner))return owner;
            }
            Room match=null;
            foreach(var room in _rooms.Values)
            {
                var bounds=room.WorldBounds;
                if(room.Tile==null || world.x<bounds.min.x || world.x>bounds.max.x ||
                   world.z<bounds.min.z || world.z>bounds.max.z ||
                   world.y<bounds.min.y-1.25f || world.y>bounds.max.y+.25f)continue;
                if(match!=null)return null;
                match=room;
            }
            return match;
        }

        // The installed game defines seven independent AI classes (none inherits
        // EnemyAI), and a separate MimicEnemy component. Query all eight types.
        internal static readonly Type[] NativeActorComponents = {
            typeof(EnemyAI), typeof(ChargingEnemyAI), typeof(SpinningEnemyAI),
            typeof(ProjectileEnemyAI), typeof(DragonEnemyAI),
            typeof(ProximityDragonEnemyAI), typeof(BrawlerEnemyAI),
            typeof(MimicEnemy),typeof(Navigator),typeof(ThreadMistressAI),typeof(GasTrap),typeof(SteamTrap)
        };

        private bool IsActorAlive(MonoBehaviour actor)
        {
            var type=actor.GetType();
            if(!_healthFields.TryGetValue(type,out var field)){
                field=type.GetField("currentHealth",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic) ??
                      type.GetField("health",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic) ??
                      type.GetField("_health",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
                _healthFields[type]=field;
            }
            if(field!=null){
                try{
                    object value=field.GetValue(actor);
                    if(value!=null&&Convert.ToSingle(value)<=0f)return false;
                }catch{}
            }
            if(!_deadFields.TryGetValue(type,out var dead)){
                dead=type.GetField("isDead",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic) ??
                     type.GetField("dead",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
                _deadFields[type]=dead;
            }
            if(dead!=null){
                try{if(dead.FieldType==typeof(bool)&&(bool)dead.GetValue(actor))return false;}catch{}
            }
            return true;
        }

        private void AddActor(MonoBehaviour actor,PortraitLibrary view,string forcedName=null)
        {
            if(actor==null||!actor.isActiveAndEnabled||!actor.gameObject.activeInHierarchy||
               actor.gameObject.scene!=SceneManager.GetActiveScene())return;
            if(!IsActorAlive(actor))return;
            var root=actor.transform;
            var package=FindPackageOwner(root);
            if(package!=null && IsPackageActor(package))root=package.transform;
            else package=null;
            var pos=root.position;
            var owner=ActorRoom(root);
            if(owner==null || !owner.Visited)return;
            // Every active actor in the generated scene is eligible; the UI
            // still hides markers outside discovered geometry. Distance from
            // the player must never arbitrarily erase distant known characters.
            if(IsPooled(root))return;
            int id=root.gameObject.GetInstanceID();
            if(tracked.TryGetValue(id,out var existing))
            {
                // Refresh the render hierarchy on the existing staggered actor
                // discovery cadence, including dynamically replaced sprites.
                RefreshActorRenderers(existing,root);
                if(package!=null)existing.Controller=PackageController(package);
                return;
            }
            if(tracked.Count>=2048)return;
            for(var ancestor=root.parent;ancestor!=null && ancestor!=owner.Tile.transform;ancestor=ancestor.parent)
                if(tracked.ContainsKey(ancestor.gameObject.GetInstanceID()))return;
            if(package!=null)
            {
                retired.Clear();
                foreach(var pair in tracked)
                    if(pair.Value.Actor!=null && pair.Value.Actor.IsChildOf(root))retired.Add(pair.Key);
                foreach(var key in retired)tracked.Remove(key);
                forcedName=PackageName(package)??forcedName;
                actor=PackageController(package);
            }
            string name=forcedName??root.gameObject.name.Replace("(Clone)","").Trim();
            if(string.IsNullOrWhiteSpace(name))name=actor.GetType().Name;
            bool witch=name.IndexOf("witch",StringComparison.OrdinalIgnoreCase)>=0 ||
                       name.IndexOf("femboy",StringComparison.OrdinalIgnoreCase)>=0;
            if(!witch&&_witchControllerType!=null){
                try{
                    witch=actor.GetComponent(_witchControllerType)!=null ||
                           actor.GetComponentInChildren(_witchControllerType)!=null;
                }catch{}
            }
            var created=new Marker{Controller=actor,LastSeen=Time.unscaledTime,
                Actor=root,WorldPosition=pos,ObjectId=id,Position=new Vector2(pos.x,pos.z),
                Name=forcedName??RoomGeometry.CleanName(name),
                Head=view.PortraitFor(package!=null?package:actor,witch?"witch":name,out var uv),PortraitUv=uv
            };
            RefreshActorRenderers(created,root);
            tracked.Add(id,created);
        }

        static void RefreshActorRenderers(Marker marker,Transform root)
        {
            root.GetComponentsInChildren<Renderer>(true,marker.Body);
            marker.Artwork.Clear();
            foreach(var renderer in marker.Body)if(renderer is SpriteRenderer sprite)marker.Artwork.Add(sprite);
        }
        static bool HasVisibleActorBody(Marker marker)
        {
            foreach(var renderer in marker.Body)
                if(renderer!=null&&renderer.enabled&&!renderer.forceRenderingOff&&renderer.gameObject.activeInHierarchy)return true;
            return false;
        }
        public void ScanEnemies(PortraitLibrary view)
        {
            _markers.Clear();
            if(!HasPlayer)return;

            _witchControllerType=OptionalActorTypes.Resolve("CharmWitch.CharmWitchController");
            _wallTrapType=OptionalActorTypes.Resolve("WallPictureTraps.WallPictureTrap");
            int bucket=discoveryBucket++%(NativeActorComponents.Length+2);
            Type type=bucket<NativeActorComponents.Length?NativeActorComponents[bucket]:bucket==NativeActorComponents.Length?_wallTrapType:_witchControllerType;
            if(bucket==0 && pooled.Count>0){retired.Clear();foreach(var pair in pooled)if(pair.Value==null)retired.Add(pair.Key);foreach(var id in retired)pooled.Remove(id);}
            if(type!=null)
                foreach(var item in Object.FindObjectsByType(type,FindObjectsSortMode.None))
                    AddActor(item as MonoBehaviour,view,bucket==NativeActorComponents.Length?"Joker":bucket>NativeActorComponents.Length?"Witch":null);
            ScanPackageActors(view);
            retired.Clear();
            float now=Time.unscaledTime;
            foreach(var entry in tracked)
            {
                var m=entry.Value;var actor=m.Controller;
                if(actor==null || m.Actor==null || IsPooled(m.Actor) || m.Actor.gameObject.scene!=SceneManager.GetActiveScene() || !IsActorAlive(actor))
                {retired.Add(entry.Key);continue;}
                m.LastKnown=!actor.isActiveAndEnabled || (m.Body.Count>0 && !HasVisibleActorBody(m));
                if(!m.LastKnown)
                {
                    var pos=m.Actor.position;
                    if((m.WorldPosition-pos).sqrMagnitude>.000001f)
                    {
                        var owner=ActorRoom(m.Actor);
                        if(owner==null || !owner.Visited){retired.Add(entry.Key);continue;}
                    }
                    m.WorldPosition=pos;m.Position=new Vector2(pos.x,pos.z);m.LastSeen=now;
                }
                else if(now-m.LastSeen>180f)
                {retired.Add(entry.Key);continue;}
                _markers.Add(m);
            }
            foreach(var key in retired)tracked.Remove(key);
        }
        readonly Dictionary<int,GameObject> pooled=new Dictionary<int,GameObject>();
        internal void ReturnActor(GameObject root)
        {
            pooled[root.GetInstanceID()]=root;
            ForgetActor(root);
        }
        internal void CheckoutActor(GameObject root)
        {
            ForgetActor(root);
            pooled.Remove(root.GetInstanceID());
        }
        bool IsPooled(Transform actor)
        {
            if(pooled.Count!=0)
                for(var t=actor;t!=null;t=t.parent)
                    if(pooled.TryGetValue(t.gameObject.GetInstanceID(),out var root) && root==t.gameObject)return true;
            if(!actor.gameObject.activeInHierarchy)
                for(var t=actor.parent;t!=null;t=t.parent)
                    if(t.name=="_EnemyPool")return true;
            return false;
        }
        internal void ForgetActor(GameObject root)
        {
            retired.Clear();
            foreach(var entry in tracked)
                if(entry.Value.Actor!=null && (entry.Value.Actor.gameObject==root || entry.Value.Actor.IsChildOf(root.transform)))retired.Add(entry.Key);
            foreach(var key in retired){_markers.Remove(tracked[key]);tracked.Remove(key);}
        }
        public void ClearMarkers() { _markers.Clear();tracked.Clear();discoveryBucket=0; }

    }
}
