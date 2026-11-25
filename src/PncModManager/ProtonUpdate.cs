using System;
using System.IO;
using System.Runtime.InteropServices;
using PncFramework.ProtonUpdate;
namespace PncModManager {
    internal static class ProtonUpdate {
        [DllImport("ntdll.dll",CallingConvention=CallingConvention.Cdecl)] static extern IntPtr wine_get_version();
        internal static readonly bool IsWine=Detect();
        static bool Detect(){try{return wine_get_version()!=IntPtr.Zero;}catch{return false;}}
        internal static IDisposable Scope(string root)=>Policy.WineGameRoot(root);
        internal static void PreparePatcher(string root){
            string target=Path.Combine(root,"BepInEx","patchers","PncProtonUpdate.dll");
            Policy.SafeFile(target);
            byte[] bytes;
            using(var stream=typeof(ProtonUpdate).Assembly.GetManifestResourceStream("PNC.Proton.Patcher")){
                if(stream==null)throw new IOException("Proton update component is unavailable");
                using(var output=new MemoryStream()){stream.CopyTo(output);bytes=output.ToArray();}
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            Policy.SafeFile(target);
            if(File.Exists(target)&&Policy.HashFile(target)==Policy.Hash(bytes))return;
            string temp=target+"."+Guid.NewGuid().ToString("N")+".tmp";
            using(var f=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))f.Write(bytes,0,bytes.Length);
            if(File.Exists(target))File.Replace(temp,target,null);else File.Move(temp,target);
            Policy.Need(Policy.HashFile(target)==Policy.Hash(bytes),"Proton updater component verification failed");
        }
        internal static void Cancel(string root){
            using(Scope(root)){
                string home=Path.Combine(root,"BepInEx","framework-updates");
                if(!Directory.Exists(home))return;
                string cancel=Path.Combine(home,"cancel");Policy.SafeFile(cancel);File.WriteAllText(cancel,"cancel");
                if(IsWine){
                    string marker=Path.Combine(home,"proton-pending"),pending=Path.Combine(home,"pending");
                    Policy.SafeFile(marker);Policy.SafeFile(pending);
                    if(File.Exists(marker)){File.Delete(marker);if(File.Exists(pending))File.Delete(pending);}
                }
            }
        }
    }
}