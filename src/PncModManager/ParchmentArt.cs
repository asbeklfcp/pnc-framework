using System;
using System.IO;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
namespace PncModManager {
    // Original PNG bytes are embedded unchanged. Source rectangles only remove transparent margins at draw time.
    internal static class ParchmentArt {
        static readonly Dictionary<string,Texture2D> Textures=new Dictionary<string,Texture2D>();
        internal static Texture2D Get(string name) {
            Texture2D t;if(Textures.TryGetValue(name,out t))return t;
            using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("PncUI."+name)){
                if(stream==null)throw new FileNotFoundException("Missing UI artwork",name);
                var bytes=new byte[stream.Length];int n=0,read;while((read=stream.Read(bytes,n,bytes.Length-n))>0)n+=read;
                t=new Texture2D(2,2,TextureFormat.RGBA32,false);ImageConversion.LoadImage(t,bytes,true);t.filterMode=FilterMode.Point;t.wrapMode=TextureWrapMode.Clamp;t.hideFlags=HideFlags.HideAndDontSave;
            }Textures.Add(name,t);return t;
        }
        internal static Rect Source(string name) {
            switch(name){
                case "PNC_ModManager_Background.png":return new Rect(133,90,2287,1526);
                case "PNC_PatchNotes_Scroll.png":return new Rect(43,62,1719,2404);
                case "PNC_Updater_Notification.png":return new Rect(50,215,2470,531);
                case "PNC_UpdateArrow_Available.png":return new Rect(57,41,1619,1714);
                default:return new Rect(60,44,1685,1683);
            }
        }
        static void Part(Texture2D t,Rect dst,Rect source){if(dst.width<=0 || dst.height<=0)return;GUI.DrawTextureWithTexCoords(dst,t,new Rect(source.x/t.width,1f-(source.y+source.height)/t.height,source.width/t.width,source.height/t.height),true);}
        internal static void Draw(string name,Rect rect){var source=Source(name);float ratio=source.width/source.height;if(name.StartsWith("PNC_UpdateArrow")){float w=Mathf.Min(rect.width,rect.height*ratio),h=w/ratio;rect=new Rect(rect.center.x-w*.5f,rect.center.y-h*.5f,w,h);}Part(Get(name),rect,source);}
        internal static void Slice(string name,Rect target,float edgeScale) {
            var source=Source(name);float left,right,top,bottom;
            if(name=="PNC_ModManager_Background.png"){left=270;right=275;top=240;bottom=240;}
            else {left=200;right=200;top=350;bottom=330;}
            float dl=left*edgeScale,dr=right*edgeScale,dt=top*edgeScale,db=bottom*edgeScale;
            float squeeze=Mathf.Min(1f,Mathf.Min(target.width/(dl+dr),target.height/(dt+db)));
            dl*=squeeze;dr*=squeeze;dt*=squeeze;db*=squeeze;
            float[] sx={source.x,source.x+left,source.xMax-right,source.xMax};float[] sy={source.y,source.y+top,source.yMax-bottom,source.yMax};
            float[] dx={target.x,target.x+dl,target.xMax-dr,target.xMax};float[] dy={target.y,target.y+dt,target.yMax-db,target.yMax};
            var t=Get(name);for(int y=0;y<3;y++)for(int x=0;x<3;x++)Part(t,new Rect(dx[x],dy[y],dx[x+1]-dx[x],dy[y+1]-dy[y]),new Rect(sx[x],sy[y],sx[x+1]-sx[x],sy[y+1]-sy[y]));
        }
        internal static void Dispose(){foreach(var texture in Textures.Values)UnityEngine.Object.Destroy(texture);Textures.Clear();}
    }
}

