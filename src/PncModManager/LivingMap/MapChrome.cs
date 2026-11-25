using System;
using UnityEngine;

namespace PncPrivateLivingMap
{
    internal sealed partial class WorldMapSurface
    {
        static readonly Color Ink=new Color32(31,29,25,255),Panel=new Color32(47,42,34,255),
            Brass=new Color32(164,131,76,255),Line=new Color32(97,81,60,255),
            Ivory=new Color32(244,230,199,255),Muted=new Color32(198,179,148,255);
        static readonly string[] Views={"2D","2.5D","Isometric","3D"};
        static readonly string[] Floors={"This layer","Manual","All layers","Cutaway"};
        Font headingFont;
        Texture2D pointer;
        void PlayerPointer(Vector2 position,float rotation,bool mini)
        {
            if(pointer==null)
            {
                const int size=48;pointer=new Texture2D(size,size,TextureFormat.RGBA32,false);
                var pixels=new Color32[size*size];
                for(int y=0;y<size;y++)for(int x=0;x<size;x++)
                {
                    float u=Mathf.Abs((x+.5f-size*.5f)/(size*.5f)),v=(y+.5f)/size;
                    float margin=Mathf.Min((.91f-v)*.83f-u,v-(.28f-.24f*u));
                    if(margin>0)pixels[y*size+x]=margin<.10f?new Color32(54,38,23,255):new Color32(255,215,130,255);
                }
                pointer.SetPixels32(pixels);pointer.Apply(false,true);
                pointer.wrapMode=TextureWrapMode.Clamp;pointer.filterMode=FilterMode.Bilinear;
            }
            var matrix=GUI.matrix;
            GUIUtility.RotateAroundPivot(rotation,position);
            float side=(mini?19:25)*ui;
            GUI.DrawTexture(new Rect(position.x-side/2,position.y-side/2,side,side),pointer);
            GUI.matrix=matrix;
        }
        GUIStyle copy,title;
        float ui;
        Rect panel,mapArea,rail;
        Vector2 railScroll;
        float railContentHeight;
        void LayoutMap(bool mini)
        {
            ui=Mathf.Clamp(Mathf.Min(Screen.width/1040f,Screen.height/610f),.45f,1.55f);
            if(copy==null)
            {
                headingFont=Font.CreateDynamicFontFromOSFont("Georgia",28);
                copy=new GUIStyle(GUI.skin.label){padding=new RectOffset(),wordWrap=false,clipping=TextClipping.Clip};
                title=new GUIStyle(copy){font=headingFont};
            }
            float edge=18*ui;
            if(mini)
            {
                float size=Mathf.Clamp(Screen.height*.245f,150*ui,218*ui);
                mapArea=MiniPlacementRect(Screen.width,Screen.height,size,ui,PlacementPosition);
                panel=new Rect(mapArea.x-2*ui,mapArea.y-2*ui,size+4*ui,size+28*ui);
                return;
            }
            panel=new Rect(edge,edge,Screen.width-2*edge,Screen.height-2*edge);
            float top=60*ui,foot=32*ui,side=244*ui;
            mapArea=new Rect(panel.x+2*ui,panel.y+top,panel.width-side-4*ui,panel.height-top-foot);
            rail=new Rect(mapArea.xMax+ui,mapArea.y,side,mapArea.height);
        }
        static void Fill(Rect r,Color c)
        {
            var color=GUI.color;GUI.color=c;GUI.DrawTexture(r,Texture2D.whiteTexture);GUI.color=color;
        }
        void Label(Rect r,string value,int size,Color color,TextAnchor alignment=TextAnchor.MiddleLeft,bool heading=false)
        {
            var style=heading?title:copy;
            style.fontSize=Mathf.RoundToInt(size*ui);style.alignment=alignment;style.normal.textColor=color;
            GUI.Label(r,value,style);
        }
        bool Button(Rect r,string value,bool chosen=false)
        {
            bool hover=r.Contains(Event.current.mousePosition);
            Fill(r,chosen?Brass:hover?new Color32(118,99,72,255):Line);
            Fill(new Rect(r.x+ui,r.y+ui,r.width-2*ui,r.height-2*ui),chosen?new Color32(87,67,41,255):hover?Panel:Ink);
            Label(r,value,12,chosen?Ivory:Muted,TextAnchor.MiddleCenter);
            return GUI.Button(r,GUIContent.none,GUIStyle.none);
        }
        void Fit(MapModel model,Rect rect)
        {
            var bounds=new Bounds(model.Player3D,Vector3.zero);
            foreach(var room in model.Rooms)if(room.Renderable)bounds.Encapsulate(room.WorldBounds);
            cameraVelocity=Vector3.zero;center=bounds.center;radius=Mathf.Max(3,bounds.extents.magnitude*Mathf.Max(1,rect.height/rect.width)*1.05f);
            follow=false;nextRender=0;
        }
        void Chrome(MapModel model,VisualInventory inventory,MapProjection mode,bool mini,Action<MapProjection> select,Action close)
        {
            string room=model.ActiveRoom?.Name??"World map";
            if(!mini)Fill(new Rect(0,0,Screen.width,Screen.height),new Color(0,0,0,.70f));
            Fill(panel,Brass);Fill(new Rect(panel.x+ui,panel.y+ui,panel.width-2*ui,panel.height-2*ui),Ink);
            if(mini)
            {
                Label(new Rect(panel.x+7*ui,mapArea.yMax,panel.width-60*ui,25*ui),room,12,Ivory);
                Label(new Rect(panel.xMax-58*ui,mapArea.yMax,50*ui,25*ui),FloorMode.Value==FloorView.AllFloors?"All":FloorLabel(selected),11,Muted,TextAnchor.MiddleRight);
                return;
            }
            Label(new Rect(panel.x+18*ui,panel.y+5*ui,panel.width*.5f,17*ui),editMini?"MINIMAP SETTINGS  /  WORLD MAP PREVIEW":"WORLD MAP",10,Muted);
            Label(new Rect(panel.x+17*ui,panel.y+19*ui,panel.width*.5f,34*ui),room,24,Ivory,TextAnchor.MiddleLeft,true);
            float bx=panel.xMax-384*ui;
            var projectionTarget=editMini?MiniControls:this;
            if(!Perspective || editMini)for(int i=0;i<Views.Length;i++)if(Button(new Rect(bx+i*69*ui,panel.y+15*ui,67*ui,29*ui),Views[i],i==(int)projectionTarget.ProjectionMode.Value))projectionTarget.SetProjection((MapProjection)i);
            if(Button(new Rect(panel.xMax-97*ui,panel.y+15*ui,82*ui,29*ui),"Close"))close();
            Fill(new Rect(panel.x+ui,mapArea.y-ui,panel.width-2*ui,ui),Line);
            Fill(rail,Panel);
            var surface=editMini?MiniControls:this;
            float contentWidth=rail.width-18*ui;
            railScroll=GUI.BeginScrollView(rail,railScroll,new Rect(0,0,contentWidth,Mathf.Max(rail.height,railContentHeight)));
            float x=10*ui,y=9*ui,w=contentWidth-20*ui,h=29*ui;
            Label(new Rect(x,y,w,h),"Map settings",20,Ivory,TextAnchor.MiddleLeft,true);y+=37*ui;
            if(Button(new Rect(x,y,w*.5f-ui,h),"World map",!editMini))editMini=false;
            if(Button(new Rect(x+w*.5f,y,w*.5f,h),"Minimap",editMini))editMini=true;
            surface=editMini?MiniControls:this;y+=38*ui;
            if(editMini)
            {
                if(Button(new Rect(x,y,w,h),"Move minimap")){Capture(false);MiniControls.BeginPlacement();}
                y+=h+4*ui;
            }
            if(Button(new Rect(x,y,w,h),model.ReadyMode?"Full dungeon map: ON":"Full dungeon map: OFF",model.ReadyMode))
            {
                model.ReadyMode=!model.ReadyMode;
                SwitchCamera(Viewpoint.Map,model);Fit(model,mapArea);nextRender=0;
                if(MiniControls!=null)MiniControls.nextRender=0;
            }
            y+=h+4*ui;
            if(!editMini)
            {
                if(Button(new Rect(x,y,w,h),CharacterMarkers.Value?"Character markers: ON":"Character markers: OFF",CharacterMarkers.Value))CharacterMarkers.Value=!CharacterMarkers.Value;
                y+=h+4*ui;
                if(Button(new Rect(x,y,w,h),MovementTrail.Value?"Movement trail: ON":"Movement trail: OFF",MovementTrail.Value))MovementTrail.Value=!MovementTrail.Value;
                y+=h+4*ui;
            }
            bool focus=surface.Focusing;
            if(Button(new Rect(x,y,w*.28f-ui,h),"All layers",surface.FloorMode.Value==FloorView.AllFloors))surface.SelectLayerMode(inventory.Elevations,FloorView.AllFloors);
            if(Button(new Rect(x+w*.28f,y,w*.34f-ui,h),"Focus layer",focus))surface.SelectLayerMode(inventory.Elevations,FloorView.Focus);
            if(Button(new Rect(x+w*.62f,y,w*.38f,h),"Only this layer",surface.FloorMode.Value==FloorView.FloorSelection || surface.FloorMode.Value==FloorView.Auto))surface.SelectLayerMode(inventory.Elevations,FloorView.FloorSelection);
            y+=h+6*ui;
            if(surface.Focusing)LensSlider(surface.BackgroundOpacity,"Other floors",0,40,x,ref y,w);
            if(Button(new Rect(x,y,32*ui,h),"−"))surface.Step(inventory.Elevations,-1);
            var shown=surface.FloorMode.Value==FloorView.Auto?inventory.Elevations.PlayerFloor:surface.selected;
            Label(new Rect(x+32*ui,y,w-64*ui,h),surface.FloorMode.Value==FloorView.AllFloors?"All layers":FloorLabel(shown),15,Ivory,TextAnchor.MiddleCenter);
            if(Button(new Rect(x+w-32*ui,y,32*ui,h),"+"))surface.Step(inventory.Elevations,1);
            y+=h+6*ui;if(Button(new Rect(x,y,w,h),"Follow player floor",surface.FloorMode.Value==FloorView.Auto)){surface.SelectLayerMode(inventory.Elevations,FloorView.Auto);}
            y+=h+19*ui;Fill(new Rect(x,y,w,ui),Line);y+=8*ui;
            Label(new Rect(x,y,w,h),"Camera",20,Ivory,TextAnchor.MiddleLeft,true);y+=34*ui;
            if(!editMini)
            {
                float third=w/3;
                if(Button(new Rect(x,y,third-ui,h),"Map",CameraMode==Viewpoint.Map))SwitchCamera(Viewpoint.Map,model);
                if(Button(new Rect(x+third,y,third-ui,h),"Free Cam",CameraMode==Viewpoint.Free))SwitchCamera(Viewpoint.Free,model,inventory.Elevations);
                if(Button(new Rect(x+2*third,y,third,h),"Player Cam",CameraMode==Viewpoint.Player))SwitchCamera(Viewpoint.Player,model,inventory.Elevations);
                y+=h+4*ui;
                if(Button(new Rect(x,y,w/2-ui,h),CameraMode==Viewpoint.Map?"Center on Player":"Return to Player"))ReturnToPlayer(model,inventory.Elevations);
                if(Button(new Rect(x+w/2,y,w/2,h),"Overview")){SwitchCamera(Viewpoint.Map,model);Fit(model,mapArea);}
                y+=h+4*ui;
                if(CameraMode!=Viewpoint.Map)
                {
                    if(Button(new Rect(x,y,w,h),Captured?"Release mouse (Esc)":"Lock mouse / hold RMB in view"))Capture(!Captured);
                    y+=h+3*ui;
                    Label(new Rect(x,y,w,19*ui),CameraMode==Viewpoint.Free?"Remembers your view in this dungeon":"Starts at your current player view",11,Muted);y+=19*ui;
                    Label(new Rect(x,y,w/2,h),"Speed "+FlySpeed.Value.ToString("0.0")+" m/s",11,Ivory);
                    float speed=GUI.HorizontalSlider(new Rect(x+w/2,y+9*ui,w/2,12*ui),FlySpeed.Value,1,20);
                    if(speed!=FlySpeed.Value)FlySpeed.Value=speed;y+=h;
                    Label(new Rect(x,y,w/2,h),"Look "+LookGain.Value.ToString("0.0")+"x",11,Ivory);
                    float gain=GUI.HorizontalSlider(new Rect(x+w/2,y+9*ui,w/2,12*ui),LookGain.Value,.2f,3f);
                    if(gain!=LookGain.Value)LookGain.Value=gain;y+=h;
                    if(Button(new Rect(x,y,w,h),SmoothMove.Value?"Smooth movement: ON":"Smooth movement: OFF",SmoothMove.Value))SmoothMove.Value=!SmoothMove.Value;
                    y+=h+4*ui;
                    Label(new Rect(x,y,w,19*ui),"WASD / arrows | Shift boost | Alt slow",11,Muted);y+=19*ui;
                    Label(new Rect(x,y,w,19*ui),"Q/E down/up | Wheel: speed",11,Muted);y+=19*ui;
                }
                else
                {
            if(Button(new Rect(x,y,w,h),model.ReadyMode?"Fit full dungeon":"Fit explored rooms"))surface.Fit(model,surface.mapArea);
            y+=h+4*ui;if(Button(new Rect(x,y,w,h),"Reset angle")){surface.yaw=32;surface.tilt=45;surface.nextRender=0;}
            y+=h+4*ui;
                }
            }
            if(Button(new Rect(x,y,w,h),surface.HideBlockers.Value?"Obstructions: hidden":"Obstructions: visible",surface.HideBlockers.Value))
            {
                surface.HideBlockers.Value=!surface.HideBlockers.Value;
                surface.nextRender=0;
            }
            if(surface.HideBlockers.Value)
            {
                y+=h+2*ui;
                if(Button(new Rect(x,y,w,19*ui),surface.CleanLens.Value?"Edit cutaway":"Finish editing",surface.CleanLens.Value))
                {
                    surface.CleanLens.Value=!surface.CleanLens.Value;
                    surface.nextRender=0;
                }
                y+=21*ui;
                if(Button(new Rect(x,y,w,19*ui),surface.TransparentLens.Value?"Obstruction: transparent":"Obstruction: remove"))
                {
                    surface.TransparentLens.Value=!surface.TransparentLens.Value;
                    surface.nextRender=0;
                }
                y+=20*ui;
                if(!surface.CleanLens.Value)
                {
                    LensSlider(surface.LensRadius,"Radius",4,45,x,ref y,w);
                    LensSlider(surface.LensFeather,"Feather",0,95,x,ref y,w);
                }
                if(surface.TransparentLens.Value)LensSlider(surface.LensStrength,"Transparency",5,95,x,ref y,w);
            }
            railContentHeight=y+h+8*ui;
            GUI.EndScrollView();
            float fy=mapArea.yMax;
            Fill(new Rect(panel.x+ui,fy,panel.width-2*ui,ui),Line);
            Label(new Rect(panel.x+15*ui,fy,panel.width*.65f,30*ui),CameraMode==Viewpoint.Map?"WASD / left drag: pan | Hold RMB: orbit | Wheel: zoom | Home: player":"Hold RMB to fly | WASD | Shift boost | Alt slow | Home: player",12,Muted);
            Label(new Rect(panel.xMax-212*ui,fy,196*ui,30*ui),model.DiscoveredCount+" explored",12,Muted,TextAnchor.MiddleRight);
        }
        void LensSlider(BepInEx.Configuration.ConfigEntry<float> entry,string name,float min,float max,float x,ref float y,float width)
        {
            Label(new Rect(x,y,width*.62f,17*ui),name+" "+Mathf.RoundToInt(entry.Value)+"%",10,Ivory);
            float value=Mathf.Round(GUI.HorizontalSlider(new Rect(x+width*.62f,y+4*ui,width*.38f,12*ui),entry.Value,min,max));
            if(value!=entry.Value)entry.Value=value;
            y+=17*ui;
        }
        static string FloorLabel(ElevationMap.Layer l)=>l==null?"—":l.Height.ToString("0.#")+" m";
        void Cartography(Rect local,bool mini)
        {
            if(!mini && !Perspective)
            {
                float metres=Mathf.Pow(10,Mathf.Floor(Mathf.Log10(Mathf.Max(.1f,100*ui*radius*2/local.height))));
                float px=metres*local.height/(2*radius);
                if(px<42*ui){metres*=2;px*=2;}
                float x=15*ui,y=local.height-14*ui;
                Fill(new Rect(x-4*ui,y-18*ui,Mathf.Max(px+10*ui,44*ui),24*ui),new Color(.10f,.09f,.08f,.85f));
                Fill(new Rect(x,y,px,ui),Muted);Fill(new Rect(x,y-4*ui,ui,5*ui),Muted);Fill(new Rect(x+px,y-4*ui,ui,5*ui),Muted);
                Label(new Rect(x,y-19*ui,74*ui,16*ui),metres.ToString("0.#")+" m",10,Ivory);
            }
        }
    }
}
