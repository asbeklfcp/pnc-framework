using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using BepInEx;
using UnityEngine;
using PncFramework.Typography;
namespace PncModManager {
    internal static class NativeTypeface {
        [DllImport("gdi32.dll",CharSet=CharSet.Unicode)]static extern int AddFontResourceEx(string file,uint flags,IntPtr reserved);
        [DllImport("gdi32.dll",CharSet=CharSet.Unicode)]static extern bool RemoveFontResourceEx(string file,uint flags,IntPtr reserved);
        static Task<string> task;static string registered;static bool finished;
        internal static Font Font {get;private set;}
        internal static string Status {get;private set;}
        internal static string CacheDirectory {get{return Path.Combine(Paths.BepInExRootPath,"framework-ui","cache");}}
        internal static void Tick(){
            if(task==null && !finished){string root=Paths.GameRootPath;task=Task.Run(()=>{string path=Path.Combine(CacheDirectory,"PNC-Native-Alkhemikal-LOCAL-CACHE.ttf"),error;if(!NativeFontCache.TryEnsure(root,path,out error))throw new IOException(error);return path;});}
            if(task==null || !task.IsCompleted)return;
            finished=true;
            try{
                registered=task.GetAwaiter().GetResult();if(AddFontResourceEx(registered,0x10,IntPtr.Zero)==0)throw new IOException("Native font registration failed");
                // Unity family-name discovery can be cached. Load the verified local file directly.
                Font=new UnityEngine.Font(registered);
                if(Font==null || !Font.HasCharacter('A'))throw new IOException("Native font was not exposed to Unity IMGUI");
                Font.RequestCharactersInTexture("AWI",32,FontStyle.Normal);
                CharacterInfo a=default(CharacterInfo),w=default(CharacterInfo);if(!Font.GetCharacterInfo('A',out a,32) || !Font.GetCharacterInfo('W',out w,32) || Math.Abs(a.advance-16)>2 || Math.Abs(w.advance-20)>2)throw new IOException("Local font glyph metrics did not match: A="+a.advance+", W="+w.advance);
                if(Font.material!=null && Font.material.mainTexture!=null)Font.material.mainTexture.filterMode=FilterMode.Point;
                Status="Alkhemikal (native local glyph cache)";
                UnityEngine.Debug.Log("[PNC UI] Native Alkhemikal cache active; glyph advance check passed");
            }catch(Exception e){if(Font!=null){UnityEngine.Object.Destroy(Font);Font=null;}Status="Native typography unavailable: "+e.Message;UnityEngine.Debug.LogWarning("[PNC UI] "+Status);}
            task=null;
        }
        internal static string Clean(string s){return (s??"").Replace("—"," - ").Replace("–","-").Replace("•","-").Replace("‘","'").Replace("’","'").Replace("“","\"").Replace("”","\"").Replace("â€¢","-").Replace("â€”","-").Replace("↑","Up").Replace("↓","Down").Replace("←","Left").Replace("→","Right").Replace("×","X").Replace("·"," | ");}
        internal static void Dispose(){if(Font!=null)UnityEngine.Object.Destroy(Font);if(registered!=null)RemoveFontResourceEx(registered,0x10,IntPtr.Zero);}
    }
}


