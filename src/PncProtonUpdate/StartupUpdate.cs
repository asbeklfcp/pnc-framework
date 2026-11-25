using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Preloader.Core.Patching;
using PncFramework.ProtonUpdate;
using PncModManager;

namespace PncProtonUpdate {
    [PatcherPluginInfo("com.pnc.proton.update","PNC Startup Updater","1.0.0")]
    public sealed class StartupUpdate : BasePatcher {
        [DllImport("ntdll.dll",CallingConvention=CallingConvention.Cdecl)] static extern IntPtr wine_get_version();
        static bool Wine(){try{return wine_get_version()!=IntPtr.Zero;}catch{return false;}}
        static bool FrameworkLoaded(){
            var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase){"PncModManager","PncEdi","PncControls","PncCustomEnemies"};
            return AppDomain.CurrentDomain.GetAssemblies().Any(a=>names.Contains(a.GetName().Name));
        }
        static bool UnsafeToReplace(){
            if(FrameworkLoaded())return true;
            using(var self=Process.GetCurrentProcess()){
                foreach(var p in Process.GetProcessesByName(self.ProcessName)){
                    using(p){if(p.Id!=self.Id)return true;}
                }
            }
            return false;
        }
        public override void Initialize(){
            string root=Path.GetFullPath(Paths.GameRootPath);
            string home=Path.Combine(root,"BepInEx","framework-updates");
            string marker=Path.Combine(home,"proton-pending"),pending=Path.Combine(home,"pending"),cancel=Path.Combine(home,"cancel"),request=Path.Combine(home,"request.xml");
            if(!Wine() || !File.Exists(marker))return;
            using(Policy.WineGameRoot(root)){
                string installed=null,channel=null;
                bool safeToClear=false,startedInstall=false;
                try{
                    foreach(var path in new[]{marker,pending,cancel,request,Path.Combine(home,"proton-result.txt")})Policy.SafeFile(path);
                    safeToClear=true;
                    if(File.Exists(cancel)){Log.LogInfo("PNC update cancelled; no plugin changes");return;}
                    Policy.Need(!UnsafeToReplace(),"Another game instance or loaded framework prevents startup update");
                    Policy.ReadRequest(File.ReadAllBytes(request),out var requestedRoot,out installed,out channel,out var envelope,out var old);
                    Policy.Need(String.Equals(Path.GetFullPath(requestedRoot),root,StringComparison.OrdinalIgnoreCase),"Update belongs to a different game folder");
                    Policy.Need(channel==UpdateBuild.Channel,"Wrong update channel");
                    var release=Policy.Verify(envelope,UpdateBuild.Trust,channel,DateTime.UtcNow);
                    Policy.Need(Policy.IsNewer(release,installed),"Update is not newer than installed version");
                    Policy.ValidateGame(root);
                    Policy.VerifyInstalled(root,UpdateBuild.Trust,channel,installed);
                    Policy.CheckUnchanged(root,old);
                    string stage=Path.GetFullPath(File.ReadAllText(pending).Trim());
                    Policy.Need(String.Equals(Path.GetDirectoryName(stage),home,StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(stage).StartsWith("payload-",StringComparison.Ordinal),"Unsafe staged payload location");
                    Policy.NoLinks(stage);
                    foreach(var f in release.Files)Policy.ValidateAssembly(Path.Combine(stage,f.Name),f);
                    Policy.Need(!File.Exists(cancel),"Update cancelled");
                    startedInstall=true;
                    Policy.Install(root,release,stage,old,UnsafeToReplace,null,envelope);
                    Policy.VerifyInstalled(root,UpdateBuild.Trust,channel,release.Id);
                    File.WriteAllText(Path.Combine(home,"proton-result.txt"),"Installed "+release.Id+". Settings retained.");
                    Log.LogInfo("PNC startup update installed "+release.Id+" before framework load");
                }catch(Exception ex){
                    Log.LogError("PNC startup update failed: "+ex.Message);
                    if(safeToClear)try{File.WriteAllText(Path.Combine(home,"proton-result.txt"),"Update failed: "+ex.Message);}catch{}
                    if(startedInstall){
                        try{Policy.VerifyInstalled(root,UpdateBuild.Trust,channel,installed);}
                        catch(Exception check){Log.LogError("Cannot authenticate original framework after failure: "+check.Message);Environment.Exit(1);}
                    }
                }finally{
                    if(safeToClear){if(File.Exists(marker))File.Delete(marker);if(File.Exists(pending))File.Delete(pending);}
                }
            }
        }
    }
}
