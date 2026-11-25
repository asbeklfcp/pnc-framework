using System;
using BepInEx.Configuration;
using UnityEngine;
namespace PncPrivateLivingMap
{
    internal sealed partial class WorldMapSurface
    {
        internal ConfigEntry<float> MiniX,MiniY;
        internal bool PlacementEditing {get;private set;}
        bool placementDragging;
        Vector2 placementDraft,placementGrab;
        internal Vector2 PlacementPosition=>PlacementEditing?placementDraft:new Vector2(MiniX?.Value??1f,MiniY?.Value??0f);
        static float Safe01(float value,float fallback)=>float.IsNaN(value)||float.IsInfinity(value)?fallback:Mathf.Clamp01(value);
        internal static Rect MiniPlacementRect(float width,float height,float size,float scale,Vector2 position)
        {
            float edge=18*scale;
            float maxX=Mathf.Max(edge,width-size-edge);
            float maxY=Mathf.Max(edge,height-size-edge-26*scale);
            return new Rect(Mathf.Lerp(edge,maxX,Safe01(position.x,1)),Mathf.Lerp(edge,maxY,Safe01(position.y,0)),size,size);
        }
        internal void BeginPlacement()
        {
            placementDraft=PlacementPosition;
            placementDragging=false;PlacementEditing=true;
        }
        internal void CancelPlacement(){placementDragging=false;PlacementEditing=false;}
        internal void EndPlacementDrag(){placementDragging=false;}
        internal void ResetPlacement(){placementDraft=new Vector2(1,0);}
        internal void SavePlacement()
        {
            if(!PlacementEditing)return;
            var value=placementDraft;
            MiniX.Value=Safe01(value.x,1);MiniY.Value=Safe01(value.y,0);
            CancelPlacement();
        }
        internal void MovePlacement(Vector2 topLeft,float width,float height,float size,float scale)
        {
            if(!PlacementEditing)return;
            var min=MiniPlacementRect(width,height,size,scale,Vector2.zero);
            var max=MiniPlacementRect(width,height,size,scale,Vector2.one);
            placementDraft=new Vector2(Safe01((topLeft.x-min.x)/Mathf.Max(1,max.x-min.x),1),Safe01((topLeft.y-min.y)/Mathf.Max(1,max.y-min.y),0));
        }
        internal void DrawPlacementEditor(MapModel model,VisualInventory inventory,MapProjection mode,bool markers,bool trail)
        {
            LayoutMap(true);
            var e=Event.current;
            float w=Mathf.Min(440*ui,Screen.width-20*ui),h=74*ui;
            var bar=new Rect((Screen.width-w)*.5f,Screen.height-h-12*ui,w,h);
            // Keep controls away from the preview, including at the bottom-center anchor.
            if(panel.Overlaps(bar))bar.y=12*ui;
            if(e.type==EventType.MouseDown && e.button==0 && panel.Contains(e.mousePosition) && !bar.Contains(e.mousePosition))
            {
                placementDragging=true;placementGrab=e.mousePosition-mapArea.position;e.Use();
            }
            else if(e.type==EventType.MouseDrag && e.button==0 && placementDragging)
            {
                MovePlacement(e.mousePosition-placementGrab,Screen.width,Screen.height,mapArea.width,ui);
                e.Use();
            }
            else if(e.type==EventType.MouseUp && e.button==0 && placementDragging)
            {
                MovePlacement(e.mousePosition-placementGrab,Screen.width,Screen.height,mapArea.width,ui);
                EndPlacementDrag();e.Use();
            }
            Fill(new Rect(0,0,Screen.width,Screen.height),new Color(0,0,0,.24f));
            Draw(model,inventory,mode,true,markers,trail,null,null);
            Fill(new Rect(panel.x,panel.y,panel.width,2*ui),Brass);
            Fill(new Rect(panel.x,panel.yMax-2*ui,panel.width,2*ui),Brass);
            Fill(new Rect(panel.x,panel.y,2*ui,panel.height),Brass);
            Fill(new Rect(panel.xMax-2*ui,panel.y,2*ui,panel.height),Brass);
            Fill(bar,Panel);
            Label(new Rect(bar.x+8*ui,bar.y+4*ui,bar.width-16*ui,26*ui),"Drag minimap anywhere",16,Ivory,TextAnchor.MiddleCenter,true);
            float gap=6*ui,bw=(bar.width-4*gap)/3;
            float y=bar.y+34*ui;
            if(Button(new Rect(bar.x+gap,y,bw,30*ui),"Reset"))ResetPlacement();
            if(Button(new Rect(bar.x+2*gap+bw,y,bw,30*ui),"Cancel"))CancelPlacement();
            if(Button(new Rect(bar.x+3*gap+2*bw,y,bw,30*ui),"Done",true))SavePlacement();
        }
    }
}
