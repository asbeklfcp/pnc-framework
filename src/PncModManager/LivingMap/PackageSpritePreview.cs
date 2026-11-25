using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using HarmonyLib;

namespace PncPrivateLivingMap
{
    internal sealed class PackageSpritePreview
    {
        sealed class Track
        {
            internal Sprite[] Frames;
            internal float Fps, Start, Time;
            internal bool Loop;
        }
        readonly Dictionary<SpriteRenderer,Track> tracks=new Dictionary<SpriteRenderer,Track>();
        readonly HashSet<SpriteRenderer> misses=new HashSet<SpriteRenderer>();
        Type type;
        FieldInfo renderer,animations,index,frame,elapsed,frames,fps,loop;
        bool resolved;
        void Resolve()
        {
            if(resolved)return;
            resolved=true;
            type=AccessTools.TypeByName("PncEdi.RuntimeSpriteVisual");
            if(type==null)return;
            renderer=AccessTools.Field(type,"_renderer");animations=AccessTools.Field(type,"_animations");
            index=AccessTools.Field(type,"_currentIndex");frame=AccessTools.Field(type,"_frame");elapsed=AccessTools.Field(type,"_elapsed");
            var item=animations?.FieldType.GetElementType();
            if(item!=null){frames=AccessTools.Field(item,"Frames");fps=AccessTools.Field(item,"Fps");loop=AccessTools.Field(item,"Loop");}
        }
        internal Sprite Sample(SpriteRenderer sprite)
        {
            if(misses.Contains(sprite))return null;
            if(!tracks.TryGetValue(sprite,out var t))
            {
                Resolve();
                try
                {
                    if(type!=null && frames!=null)
                    {
                        var driver=sprite.GetComponentInParent(type,true);
                        if(driver!=null && renderer.GetValue(driver) as SpriteRenderer==sprite)
                        {
                            var list=animations.GetValue(driver) as Array;
                            int i=(int)index.GetValue(driver);
                            if(list!=null&&i>=0&&i<list.Length)
                            {
                                var item=list.GetValue(i);
                                var f=frames.GetValue(item) as Sprite[];
                                float rate=(float)fps.GetValue(item);
                                if(f!=null&&f.Length>0&&rate>0&&!float.IsInfinity(rate))
                                {
                                    t=new Track{Frames=f,Fps=rate,Loop=(bool)loop.GetValue(item),Start=(int)frame.GetValue(driver)+(float)elapsed.GetValue(driver)*rate,Time=Time.unscaledTime};
                                    tracks.Add(sprite,t);
                                }
                            }
                        }
                    }
                }
                catch(Exception ex){Debug.LogWarning("[PNC MAP] Package sprite preview: "+ex.Message);}
                if(t==null){misses.Add(sprite);return null;}
            }
            float pos=t.Start+(Time.unscaledTime-t.Time)*t.Fps;
            int n=t.Loop?Mathf.FloorToInt(Mathf.Repeat(pos,t.Frames.Length)):Mathf.Clamp(Mathf.FloorToInt(pos),0,t.Frames.Length-1);
            return t.Frames[n];
        }
        internal void Clear(){tracks.Clear();misses.Clear();resolved=false;}
    }
}