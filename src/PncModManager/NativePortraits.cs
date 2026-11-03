using System;
using System.Collections.Generic;
using System.Reflection;
using System.Xml;
using System.Globalization;
using UnityEngine;
namespace PncModManager {
    internal static class NativePortraits {
        internal sealed class Portrait {internal Sprite Sprite;internal Rect UV;}
        static readonly Dictionary<string,Portrait> Cache=new Dictionary<string,Portrait>();
        static readonly Dictionary<string,UnityEngine.Object> GalleryEntries=new Dictionary<string,UnityEngine.Object>();
        sealed class Definition {internal string Resource,Sprite;internal Rect Crop;}
        static Dictionary<string,Definition> definitions;
        static Definition DefinitionFor(string id){
            if(definitions==null){
                definitions=new Dictionary<string,Definition>();var doc=new XmlDocument();doc.XmlResolver=null;
                using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("PncUI.NativePortraits.xml"))doc.Load(stream);
                foreach(XmlElement e in doc.DocumentElement.GetElementsByTagName("portrait")){
                    var crop=e.GetAttribute("crop").Split(',');if(crop.Length!=4)continue;var values=Array.ConvertAll(crop,x=>float.Parse(x,CultureInfo.InvariantCulture));
                    if(values[0]<0 || values[1]<0 || values[2]<=0 || values[3]<=0 || values[0]+values[2]>1.001f || values[1]+values[3]>1.001f)continue;
                    string resource=e.GetAttribute("resource");if(!resource.StartsWith("gallerydata/enemygallerydata/") || resource.Contains(".."))continue;
                    definitions[e.GetAttribute("id")]=new Definition{Resource=resource,Sprite=e.GetAttribute("sprite"),Crop=new Rect(values[0],values[1],values[2],values[3])};
                }
            }
            Definition found;return definitions.TryGetValue(id,out found)?found:null;
        }
        internal static Portrait Resolve(string id,string resource=null,string spriteName=null,string crop=null){
            if(String.IsNullOrEmpty(id) && String.IsNullOrEmpty(resource))return null;
            string cacheKey=id+"|"+resource+"|"+spriteName+"|"+crop;Portrait found;if(Cache.TryGetValue(cacheKey,out found))return found;
            id=id??"";
            string hint=id.Replace('_',' ').Replace('-',' ');
            var definition=DefinitionFor(id);string exact=definition?.Sprite;
            if(!String.IsNullOrEmpty(resource) && !String.IsNullOrEmpty(spriteName) && !String.IsNullOrEmpty(crop)){
                var values=Array.ConvertAll(crop.Split(','),x=>float.Parse(x,CultureInfo.InvariantCulture));definition=new Definition{Resource=resource,Sprite=spriteName,Crop=new Rect(values[0],values[1],values[2],values[3])};exact=spriteName;
            }
            if(exact!=null && !GalleryEntries.ContainsKey(definition.Resource))GalleryEntries[definition.Resource]=Resources.Load<UnityEngine.Object>(definition.Resource);
            // Scene bosses/NPCs do not necessarily have EnemyData. Prefer known native idle art.
            foreach(var sprite in Resources.FindObjectsOfTypeAll<Sprite>()){
                if(sprite==null)continue;
                string name=sprite.name.ToLowerInvariant();
                bool matches=exact!=null ? String.Equals(sprite.name,exact,StringComparison.OrdinalIgnoreCase) :
                    (!String.IsNullOrEmpty(hint) && name.IndexOf(hint,StringComparison.OrdinalIgnoreCase)>=0 && name.Contains("idle") && (name.EndsWith("_0") || name.EndsWith("idle")));
                if(!matches)continue;
                found=FromSprite(sprite,definition);if(found!=null){Cache[cacheKey]=found;return found;}
            }
            if(exact!=null || String.IsNullOrEmpty(hint)){Cache[cacheKey]=null;return null;}
            foreach(var data in Resources.FindObjectsOfTypeAll<EnemyData>()){
                if(data==null || (data.name+" "+data.enemyName).IndexOf(hint,StringComparison.OrdinalIgnoreCase)<0 || data.prefabVariants==null)continue;
                foreach(var prefab in data.prefabVariants){
                    if(prefab==null)continue;
                    foreach(var renderer in prefab.GetComponentsInChildren<SpriteRenderer>(true)){
                        var sprite=renderer.sprite;if(sprite==null || renderer.name.IndexOf("shadow",StringComparison.OrdinalIgnoreCase)>=0)continue;
                        string name=sprite.name.ToLowerInvariant();if(name.Contains("grab") || name.Contains("sex") || name.Contains("cum"))continue;
                        try{
                            found=FromSprite(sprite,null);if(found==null)continue;
                            Cache[cacheKey]=found;return found;
                        }catch{ /* Tight-packed sprites may not expose a rectangle; use the text-only entry. */ }
                    }
                }
            }
            // Missing native art is optional; no loading/spawning of encounters just to obtain a portrait.
            Cache[cacheKey]=null;return null;
        }
        static Portrait FromSprite(Sprite sprite,Definition definition){try{
            Rect region;
            try{region=sprite.textureRect;}catch{
                var uvs=sprite.uv;float minX=1,minY=1,maxX=0,maxY=0;foreach(var uv in uvs){minX=Mathf.Min(minX,uv.x);minY=Mathf.Min(minY,uv.y);maxX=Mathf.Max(maxX,uv.x);maxY=Mathf.Max(maxY,uv.y);}region=new Rect(minX*sprite.texture.width,minY*sprite.texture.height,(maxX-minX)*sprite.texture.width,(maxY-minY)*sprite.texture.height);
            }
            if(definition!=null){var c=definition.Crop;return new Portrait{Sprite=sprite,UV=new Rect((region.x+c.x*region.width)/sprite.texture.width,(region.y+(1-c.y-c.height)*region.height)/sprite.texture.height,c.width*region.width/sprite.texture.width,c.height*region.height/sprite.texture.height)};}
            float side=Mathf.Min(region.width*.8f,region.height*.44f);
            Rect face=new Rect(region.x+(region.width-side)*.5f,region.yMax-side,side,side);
            return new Portrait{Sprite=sprite,UV=new Rect(face.x/sprite.texture.width,face.y/sprite.texture.height,face.width/sprite.texture.width,face.height/sprite.texture.height)};
        }catch{return null;}}
    }
}
