using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Object=UnityEngine.Object;

namespace PncPrivateLivingMap
{
    internal sealed class PortraitLibrary : IDisposable
    {
        readonly Dictionary<string,Texture2D> _heads=new Dictionary<string,Texture2D>(StringComparer.OrdinalIgnoreCase);
        readonly List<Texture2D> _owned=new List<Texture2D>();
        static readonly Color32 Transparent=new Color32(0,0,0,0);
        internal PortraitLibrary(string root)
        {
            foreach(var name in new[]{"witch","joker","dragon","wendigo","baphomet","blinded"})
                _heads[name]=FallbackHead(name);
        }
        public Texture2D PortraitFor(MonoBehaviour enemy,string key,out Rect uv)
        {
            uv=new Rect(0,0,1,1);
            key=(key??"enemy").ToLowerInvariant();
            if(enemy!=null)
            {
                Sprite best=null;
                foreach(var renderer in enemy.GetComponentsInChildren<SpriteRenderer>(false))
                    if(renderer.enabled && renderer.sprite!=null && (best==null || renderer.sprite.rect.height>best.rect.height))
                        best=renderer.sprite;
                if(best!=null && best.texture!=null && (!best.packed || best.packingMode==SpritePackingMode.Rectangle && best.packingRotation==SpritePackingRotation.None))
                {
                    var region=best.textureRect;var texture=best.texture;
                    uv=new Rect((region.x+region.width*.20f)/texture.width,(region.y+region.height*.51f)/texture.height,
                        region.width*.60f/texture.width,region.height*.44f/texture.height);
                    return texture;
                }
            }
            if(key.Contains("witch")||key.Contains("femboy"))return _heads["witch"];
            if(key.Contains("joker")||key.Contains("wallpicture"))return _heads["joker"];
            if(key.Contains("dragon"))return _heads["dragon"];
            if(key.Contains("wendigo"))return _heads["wendigo"];
            if(key.Contains("baphomet")||key.Contains("gargoyle"))return _heads["baphomet"];
            if(key.Contains("blinded")||key.Contains("eyebeast"))return _heads["blinded"];
            if(!_heads.TryGetValue(key,out var fallback))_heads[key]=fallback=FallbackHead(key);
            return fallback;
        }

        private Texture2D FallbackHead(string name)
        {
            // This is still a real image texture, with an original illustrated
            // silhouette when the native game exposes no readable sprite/head.
            const int size=80;
            var tex=new Texture2D(size,size,TextureFormat.RGBA32,false);
            var pixel=new Color32[size*size];
            int hash=0;foreach(var c in name)hash=unchecked(hash*31+c);
            var halo=new Color32((byte)(108+Math.Abs(hash%60)),(byte)(98+Math.Abs((hash/7)%60)),(byte)(100+Math.Abs((hash/11)%55)),255);
            var edge=new Color32(221,189,136,255);var face=new Color32(238,210,162,255);
            var hair=new Color32(59,50,53,255);
            for(int y=0;y<size;y++)for(int x=0;x<size;x++)
            {
                float dx=x-39.5f,dy=y-39.5f,dist=dx*dx+dy*dy;
                Color32 c=Transparent;
                if(dist<39*39)c=dist>36*36?edge:halo;
                // Original little portrait: shoulders + head + dark hair, no letters.
                if(dist<36*36)
                {
                    float torso=(dx*dx/(31f*31f))+((y-16f)*(y-16f)/(28f*28f));
                    if(torso<1f&&y<35)c=hair;
                    float head=((dx+0f)*(dx+0f)/(20f*20f))+((y-46f)*(y-46f)/(22f*22f));
                    if(head<1f)c=face;
                    if(y>52&&head<1f&&Math.Abs(dx)>14)c=hair;
                    if(y>61&&head<1f)c=hair;
                }
                pixel[y*size+x]=c;
            }
            tex.SetPixels32(pixel);tex.Apply(false,true);_owned.Add(tex);return tex;
        }

        public void Dispose()
        {
            foreach(var image in _owned)if(image!=null)Object.Destroy(image);
            _owned.Clear();_heads.Clear();
        }
    }
}