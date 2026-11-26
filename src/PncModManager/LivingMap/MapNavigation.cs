using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace PncPrivateLivingMap
{
    internal sealed class MapPause
    {
        readonly List<InputAction> actions=new List<InputAction>();
        readonly MapWitchAudio witchAudio=new MapWitchAudio();
        internal void TickAudio(){if(Active)witchAudio.Refresh();}
        internal static bool Active {get;private set;}
        float scale;
        InputManager owner;
        internal static bool AllowGameUpdate()=>!Active;
        internal void Open()
        {
            if(Active)return;
            scale=Time.timeScale;
            var manager=InputManager.I;
            var asset=manager!=null?AccessTools.Field(typeof(InputManager),"actionAsset").GetValue(manager) as InputActionAsset:null;
            if(asset==null)return;
            owner=manager;actions.Clear();
            foreach(var action in asset)if(action.enabled)actions.Add(action);
            Active=true;
            Time.timeScale=0;
            asset.Disable();
            witchAudio.Open();
        }
        internal void Close()
        {
            if(!Active)return;
            Active=false;
            Time.timeScale=scale;
            witchAudio.Close();
            if(owner!=null && owner==InputManager.I)foreach(var action in actions)action.Enable();
            owner=null;
            actions.Clear();
        }
        static Harmony actorHarmony;
        static readonly HashSet<Type> guardedCustomTypes=new HashSet<Type>();
        internal static void GuardCustom(Type type)
        {
            if(actorHarmony==null||type==null||!guardedCustomTypes.Add(type)||Array.IndexOf(MapModel.NativeActorComponents,type)>=0)return;
            var guard=new HarmonyMethod(typeof(MapPause),nameof(AllowGameUpdate));
            foreach(var name in new[]{"Update","LateUpdate"})
            {
                var method=type.GetMethod(name,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.DeclaredOnly,null,Type.EmptyTypes,null);
                if(method!=null&&!method.IsAbstract)try{actorHarmony.Patch(method,prefix:guard);}catch(Exception ex){Debug.LogWarning("[PNC MAP] Custom pause guard: "+ex.Message);}
            }
        }
        internal static void Install(Harmony harmony)
        {
            actorHarmony=harmony;guardedCustomTypes.Clear();
            var guard=new HarmonyMethod(typeof(MapPause),nameof(AllowGameUpdate));
            var types=new List<Type>(MapModel.NativeActorComponents){typeof(FirstPersonController),typeof(PlayerStats)};
            foreach(var name in new[]{"CMF.CameraController","CMF.SmoothPosition","PixelCrushers.GridController.PauseMenuManager","UnityEngine.EventSystems.EventSystem"})
            {
                var type=OptionalActorTypes.Resolve(name);if(type!=null)types.Add(type);
            }
            var patched=new HashSet<System.Reflection.MethodBase>();
            foreach(var type in types)
                foreach(var name in new[]{"Update","LateUpdate"})
                {
                    var method=AccessTools.DeclaredMethod(type,name,Type.EmptyTypes);
                    if(method!=null && !method.IsAbstract && patched.Add(method))harmony.Patch(method,prefix:guard);
                }
        }
    }

    internal sealed partial class WorldMapSurface
    {
        internal enum Viewpoint { Map,Free,Player }
        internal Viewpoint CameraMode;
        sealed class VirtualPose
        {
            internal bool Valid;
            internal Vector3 Position;
            internal Quaternion Rotation;
            internal float Fov;
        }
        readonly VirtualPose freePose=new VirtualPose(),playerPose=new VirtualPose();
        VirtualPose Pose=>CameraMode==Viewpoint.Free?freePose:playerPose;
        bool Perspective=>CameraMode!=Viewpoint.Map;
        int cameraGeneration=-1;
        internal bool Captured;
        internal BepInEx.Configuration.ConfigEntry<float> FlySpeed,LookGain;
        internal BepInEx.Configuration.ConfigEntry<bool> SmoothMove;
        internal BepInEx.Configuration.ConfigEntry<bool> CharacterMarkers,MovementTrail;
        Vector3 cameraVelocity;
        bool holdLook;
        Vector3 returnStart,returnEnd;
        Quaternion rotationStart,rotationEnd;
        float fovStart,fovEnd,returnTime,lookScale=.1f;
        bool returning,skipMouse,invertX,invertY;
        float upLimit=88,downLimit=88;
        InputAction virtualLook;
        void ReadLookSettings(MapModel model)
        {
            var controller=model.GameplayLookController;
            if(controller==null)return;
            var type=controller.GetType();
            float ReadFloat(object source,string field,float fallback)=>
                source!=null && AccessTools.Field(source.GetType(),field)?.GetValue(source) is float value?value:fallback;
            var input=AccessTools.Field(type,"cameraInput")?.GetValue(controller);
            lookScale=ReadFloat(controller,"cameraSpeed",1)*ReadFloat(input,"mouseInputMultiplier",.1f)*ReadFloat(input,"sensitivityMultiplier",1);
            invertX=input!=null && AccessTools.Field(input.GetType(),"invertHorizontalInput")?.GetValue(input) is true;
            invertY=input!=null && AccessTools.Field(input.GetType(),"invertVerticalInput")?.GetValue(input) is true;
            upLimit=Mathf.Clamp(ReadFloat(controller,"upperVerticalLimit",88),1,89);
            downLimit=Mathf.Clamp(ReadFloat(controller,"lowerVerticalLimit",88),1,89);
        }
        internal void Capture(bool capture)
        {
            bool enabled=capture && (Perspective || CameraMode==Viewpoint.Map);
            if(Captured!=enabled)cameraVelocity=Vector3.zero;
            holdLook=false;
            Captured=enabled;
            if(Captured && virtualLook==null && InputManager.Look!=null){virtualLook=InputManager.Look.Clone();virtualLook.Enable();}
            if(!Captured && virtualLook!=null){virtualLook.Dispose();virtualLook=null;}
            Cursor.lockState=Captured?CursorLockMode.Locked:CursorLockMode.None;
            Cursor.visible=!Captured;skipMouse=true;
        }
        internal void BeginHoldLook(){if(!Captured && CameraMode!=Viewpoint.Map){Capture(true);holdLook=Captured;}}
        internal void CenterMapOnPlayer(MapModel model,ElevationMap elevations)
        {
            returning=false;cameraVelocity=Vector3.zero;
            center=model.Player3D;follow=true;lastFollowTime=0;nextRender=0;
            FocusPlayerFloor(elevations);
            framedHeight=float.NaN;
        }
        internal void BeginMapOrbit(MapModel model)
        {
            if(CameraMode!=Viewpoint.Map || Captured)return;
            var mode=ProjectionMode.Value;
            if(mode!=MapProjection.Orbit)
            {
                if(mode==MapProjection.Overhead){yaw=0;tilt=90;}
                else if(mode==MapProjection.Isometric){yaw=45;tilt=35.26439f;}
                else tilt=58;
                SetProjection(MapProjection.Orbit);
            }
            ReadLookSettings(model);
            Capture(true);holdLook=Captured;
        }
        internal void AdvanceMap(MapModel model,Vector3 move,Vector2 look,bool fast,bool precise,float dt)
        {
            if(CameraMode!=Viewpoint.Map)return;
            if(look.sqrMagnitude>0 && Captured)
            {
                float gain=LookGain.Value;
                yaw=Mathf.Repeat(yaw+look.x*.5f*gain*(invertX?-1:1),360);
                tilt=Mathf.Clamp(tilt+look.y*.4f*gain*(invertY?-1:1),12,90);
                nextRender=0;
            }
            if(move.sqrMagnitude==0 && cameraVelocity.sqrMagnitude==0)return;
            float heading=ProjectionMode.Value==MapProjection.Overhead?0:ProjectionMode.Value==MapProjection.Isometric?45:yaw;
            var direction=Quaternion.Euler(0,heading,0)*new Vector3(move.x,0,move.z);
            var desired=Vector3.ClampMagnitude(direction,1)*Mathf.Clamp(radius*.85f,2,160)*CameraMotion.SpeedMultiplier(fast,precise);
            dt=Mathf.Clamp(dt,0,.05f);
            follow=false;
            if(SmoothMove!=null && !SmoothMove.Value){cameraVelocity=Vector3.zero;center+=desired*dt;}
            else
            {
                CameraMotion.Weights(dt,desired.sqrMagnitude>0?18f:28f,out float decay,out float oldWeight);
                center+=desired*dt+(cameraVelocity-desired)*oldWeight;
                cameraVelocity=desired+(cameraVelocity-desired)*decay;
                if(cameraVelocity.sqrMagnitude<.0001f)cameraVelocity=Vector3.zero;
            }
            nextRender=0;
        }
        internal void ReleaseVirtualInput()
        {
            Captured=false;holdLook=false;skipMouse=true;cameraVelocity=Vector3.zero;
            if(virtualLook!=null){virtualLook.Dispose();virtualLook=null;}
        }
        internal void LeaveVirtual(){returning=false;ReleaseVirtualInput();}
        internal void ResetViewpoints()
        {
            cameraGeneration=-1;cameraVelocity=Vector3.zero;freePose.Valid=playerPose.Valid=false;
            returning=false;framed=false;follow=true;lastFollowTime=0;framedHeight=float.NaN;
            displayedFloor=-1;layerFadeEnd=0;lastRenderFrame=-1;nextRender=0;
        }
        internal bool SyncCameraWorld(MapModel model,ElevationMap elevations)
        {
            if(cameraGeneration==model.DungeonGeneration)return false;
            ResetViewpoints();cameraGeneration=model.DungeonGeneration;
            CenterMapOnPlayer(model,elevations);
            return true;
        }
        void SeedPose(Camera camera)
        {
            Pose.Position=camera.transform.position;Pose.Rotation=camera.transform.rotation;
            Pose.Fov=camera.fieldOfView;Pose.Valid=true;
        }
        internal void SwitchCamera(Viewpoint mode,MapModel model,ElevationMap elevations=null)
        {
            SyncCameraWorld(model,elevations??cameraInventory?.Elevations);
            var camera=model.GameplayCamera;
            if(mode!=Viewpoint.Map && camera==null)return;
            if(target!=null && sceneReady && CameraMode!=mode)
            {
                EnsureLayer(ref previousLayerFrame,target);Graphics.Blit(target,previousLayerFrame);
                layerFadeEnd=Time.unscaledTime+.16f;
            }
            returning=false;CameraMode=mode;
            if(Perspective)ReadLookSettings(model);
            if(Perspective)
            {
                if(CameraMode==Viewpoint.Player || !Pose.Valid)
                {
                    SeedPose(camera);
                    FocusPlayerFloor(elevations??cameraInventory?.Elevations);
                }

            }
            cameraVelocity=Vector3.zero;Capture(false);nextRender=0;
        }
        internal void ResetPerspectiveForOpen(MapModel model,ElevationMap elevations)
        {
            SyncCameraWorld(model,elevations);
            returning=false;cameraVelocity=Vector3.zero;holdLook=false;
            // Keep the map's pan, follow mode, floor and projection on reopen.
            // SyncCameraWorld owns recentering when the dungeon changes.
            // Follow mode may have been initialized before a new scene's spawn finished.
            // Never overwrite a manually panned map (follow == false).
            if(follow)center=model.Player3D;
            lastFollowTime=0;
            framedHeight=float.NaN;displayedFloor=-1;layerFadeEnd=0;
            nextRender=0;lastRenderFrame=-1;skipMouse=true;
            if(!Perspective)return;
            if(CameraMode==Viewpoint.Player || !freePose.Valid)FocusPlayerFloor(elevations);
            var camera=model.GameplayCamera;if(camera==null)return;
            ReadLookSettings(model);
            if(CameraMode==Viewpoint.Player || !Pose.Valid)SeedPose(camera);
        }
        void FocusPlayerFloor(ElevationMap elevations)
        {
            if(elevations==null)return;
            SortLayers(elevations);
            int index=sortedLayers.IndexOf(elevations.PlayerFloor);
            if(index>=0&&(Focusing||FloorMode.Value==FloorView.FloorSelection))FloorIndex.Value=index;
        }
        internal void ReturnToPlayer(MapModel model,ElevationMap elevations)
        {
            if(CameraMode==Viewpoint.Map){CenterMapOnPlayer(model,elevations);return;}
            var camera=model.GameplayCamera;if(camera==null)return;
            FocusPlayerFloor(elevations);cameraVelocity=Vector3.zero;
            returnStart=Pose.Position;rotationStart=Pose.Rotation;fovStart=Pose.Fov;
            returnEnd=camera.transform.position;rotationEnd=camera.transform.rotation;fovEnd=camera.fieldOfView;
            returning=true;returnTime=Time.unscaledTime;nextRender=0;
        }
        internal void Navigate(MapModel model)
        {
            var keyboard=Keyboard.current;var mouse=Mouse.current;
            if(Application.isFocused && keyboard!=null && keyboard.homeKey.wasPressedThisFrame)
                ReturnToPlayer(model,cameraInventory?.Elevations);
            if(!Application.isFocused || keyboard!=null && keyboard.escapeKey.wasPressedThisFrame || holdLook && (mouse==null || !mouse.rightButton.isPressed))
            {
                if(Captured)Capture(false);
                cameraVelocity=Vector3.zero;
            }
            if(CameraMode==Viewpoint.Map && Application.isFocused)
            {
                var pan=Vector3.zero;var orbit=Vector2.zero;
                if(keyboard!=null && !keyboard.ctrlKey.isPressed)
                {
                    pan.x=(keyboard.dKey.isPressed||keyboard.rightArrowKey.isPressed?1:0)-(keyboard.aKey.isPressed||keyboard.leftArrowKey.isPressed?1:0);
                    pan.z=(keyboard.wKey.isPressed||keyboard.upArrowKey.isPressed?1:0)-(keyboard.sKey.isPressed||keyboard.downArrowKey.isPressed?1:0);
                }
                if(Captured && mouse!=null)
                {
                    if(!skipMouse)
                    {
                        orbit=mouse.delta.ReadValue();
                        float wheel=mouse.scroll.ReadValue().y;
                        if(Mathf.Abs(wheel)>.01f){radius=Mathf.Clamp(radius*Mathf.Exp(-.12f*Mathf.Clamp(Mathf.Abs(wheel)<10?wheel:wheel/120f,-4,4)),2,500);nextRender=0;}
                    }
                    skipMouse=false;
                }
                AdvanceMap(model,pan,orbit,keyboard!=null&&keyboard.shiftKey.isPressed,keyboard!=null&&keyboard.altKey.isPressed,Time.unscaledDeltaTime);
                return;
            }
            if(!Perspective)return;
            if(!Pose.Valid)
            {
                var camera=model.GameplayCamera;if(camera==null)return;
                ReadLookSettings(model);SeedPose(camera);FocusPlayerFloor(cameraInventory?.Elevations);
            }
            var move=Vector3.zero;var look=Vector2.zero;
            if(Captured)
            {
                if(keyboard!=null)
                {
                    move.x=(keyboard.dKey.isPressed||keyboard.rightArrowKey.isPressed?1:0)-(keyboard.aKey.isPressed||keyboard.leftArrowKey.isPressed?1:0);
                    move.z=(keyboard.wKey.isPressed||keyboard.upArrowKey.isPressed?1:0)-(keyboard.sKey.isPressed||keyboard.downArrowKey.isPressed?1:0);
                    move.y=(keyboard.spaceKey.isPressed||keyboard.eKey.isPressed?1:0)-(keyboard.ctrlKey.isPressed||keyboard.qKey.isPressed?1:0);
                }
                if(!skipMouse && mouse!=null)
                {
                    look=virtualLook!=null?virtualLook.ReadValue<Vector2>():mouse.delta.ReadValue();
                    float wheel=mouse.scroll.ReadValue().y;
                    if(Mathf.Abs(wheel)>.01f)
                        FlySpeed.Value=Mathf.Clamp(FlySpeed.Value*Mathf.Pow(1.2f,Mathf.Clamp(Mathf.Abs(wheel)<10?wheel:wheel/120f,-4,4)),1,20);
                }
                skipMouse=false;
            }
            AdvanceCore(move,look,keyboard!=null && keyboard.shiftKey.isPressed,keyboard!=null && keyboard.altKey.isPressed);
        }
        internal void Advance(Vector3 move,Vector2 look,bool fast)=>AdvanceCore(move,look,fast,false);
        void AdvanceCore(Vector3 move,Vector2 look,bool fast,bool precise)
        {
            if(!Perspective)return;
            if(move.sqrMagnitude>0 || look.sqrMagnitude>0)returning=false;
            if(returning)
            {
                cameraVelocity=Vector3.zero;
                float t=Mathf.Clamp01((Time.unscaledTime-returnTime)/.38f);
                float eased=Mathf.SmoothStep(0,1,t);
                Pose.Position=Vector3.Lerp(returnStart,returnEnd,eased);
                Pose.Rotation=Quaternion.Slerp(rotationStart,rotationEnd,eased);
                Pose.Fov=Mathf.Lerp(fovStart,fovEnd,eased);
                if(t>=1)returning=false;
                return;
            }
            if(look.sqrMagnitude>0)
            {
                var angles=Pose.Rotation.eulerAngles;
                float gain=lookScale*LookGain.Value;
                Pose.Rotation=Quaternion.Euler(Mathf.Clamp(Mathf.DeltaAngle(0,angles.x)-look.y*gain*(invertY?-1:1),-upLimit,downLimit),angles.y+look.x*gain*(invertX?-1:1),0);
            }
            var basis=Pose.Rotation;
            var direction=basis*new Vector3(move.x,0,move.z)+Vector3.up*move.y;
            var desired=Vector3.ClampMagnitude(direction,1)*FlySpeed.Value*CameraMotion.SpeedMultiplier(fast,precise);
            float dt=Mathf.Clamp(Time.unscaledDeltaTime,0,.05f);
            if(SmoothMove!=null && !SmoothMove.Value){cameraVelocity=Vector3.zero;Pose.Position+=desired*dt;return;}
            CameraMotion.Weights(dt,desired.sqrMagnitude>0?18f:28f,out float decay,out float oldWeight);
            Pose.Position+=desired*dt+(cameraVelocity-desired)*oldWeight;
            cameraVelocity=desired+(cameraVelocity-desired)*decay;
            if(cameraVelocity.sqrMagnitude<.0001f)cameraVelocity=Vector3.zero;
        }
    }
}
