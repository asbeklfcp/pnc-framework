using System;
using System.IO;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Threading;
using BepInEx;
using PncFramework.Update;
using UnityEngine;

namespace PncModManager;
public sealed partial class Plugin {
    
    bool _updateStarted;
    bool _updatePrepared;
    bool _readyAttention;
    string _updateMessage="Checking for approved updates…";
    Release _updateOffer;
    byte[] _updateEnvelope;
    Task<UpdateCheck> _updateCheck;
    Task<string> _updatePreparation;
    const float AutomaticCheckSeconds=UpdateBuild.Channel=="private"?5f:30f;
    float _nextAutomaticCheck=AutomaticCheckSeconds;
    Vector2 _notesScroll;
    CancellationTokenSource _downloadCancel;
    long _downloadBytes,_downloadTotal;
    string _downloadStage="";
    sealed class UpdateCheck { public Release Offer; public byte[] Envelope; public string Message; }
    void TickUpdater() {
        if(!_updateStarted) { _updateStarted=true; CheckUpdates(); LoadNotesHistory(); }
        TickNotesHistory();
        if(_updateCheck==null && _updatePreparation==null && Time.unscaledTime>_nextAutomaticCheck ){_nextAutomaticCheck=Time.unscaledTime+AutomaticCheckSeconds;CheckUpdates();LoadNotesHistory();}
        if(_updateCheck!=null && _updateCheck.IsCompleted) {
            try { var result=_updateCheck.GetAwaiter().GetResult();_updateOffer=result.Offer;_updateEnvelope=result.Envelope;_updateMessage=result.Message;ObserveUpdateOffer(); }
            catch(Exception e){_updateOffer=null;_updateMessage=UpdateBuild.Channel=="private" && e.GetBaseException() is System.Net.WebException ? "Private staging feed is not available. No update is offered." : "Update check unavailable: "+e.GetBaseException().Message;}
            _updateCheck=null;
        }
        if(_updatePreparation!=null && _updatePreparation.IsCompleted) {
            try { string completed=_updatePreparation.GetAwaiter().GetResult();if(_downloadCancel!=null && _downloadCancel.IsCancellationRequested)throw new OperationCanceledException("Update cancelled");_updateMessage=completed;_updatePrepared=true;_downloadStage="Ready";_readyAttention=!_patchNotesOpen;Logger.LogInfo("[UPDATER] Ready; close-game confirmation available"); }
            catch(Exception e){_updateMessage=e.GetBaseException() is OperationCanceledException?"Download cancelled. Your game has not been changed.":"Update was not prepared: "+e.GetBaseException().Message;_downloadStage="";}
            _updatePreparation=null;
        }
        if(_readyAttention && _updatePrepared && !_patchNotesOpen && !_visible && _mainMenu!=null && IsMainMenuVisible()){_readyAttention=false;OpenPatchNotes();}
    }
    void CheckUpdates() {
        if(_updateCheck!=null || _updatePreparation!=null || _updatePrepared)return;
        if(String.IsNullOrEmpty(UpdateBuild.Feed)) {_updateMessage="No public release feed has been published yet.";return;}
        _updateMessage="Checking for approved updates…";
        _updateCheck=Task.Run(()=>{
            var url=Policy.ValidateUrl(UpdateBuild.Feed,UpdateBuild.Channel,null);
            var bytes=Policy.Download(url,256*1024,-1);
            var release=Policy.Verify(bytes,UpdateBuild.Trust,UpdateBuild.Channel,DateTime.UtcNow);
            Policy.ValidateUrl(release.PackageUrl,UpdateBuild.Channel,url);
            bool newer=Policy.IsNewer(release,UpdateBuild.ReleaseId);
            return new UpdateCheck{Offer=newer?release:null,Envelope=bytes,Message=newer?"An approved update is available.":"You have the latest approved release."};
        });
    }
    void PrepareUpdate() {
        if(_updateOffer==null || _updatePreparation!=null || _updatePrepared)return;
        var release=_updateOffer;var envelope=_updateEnvelope;
        string root=Paths.GameRootPath;
        string currentHome=Path.Combine(root,"BepInEx","framework-updates");
        if(File.Exists(Path.Combine(currentHome,"pending"))){_updateMessage="A prepared update is still pending. Cancel it or close the game first.";return;}
        string previousCancel=Path.Combine(currentHome,"cancel");if(File.Exists(previousCancel))File.Delete(previousCancel);
        _downloadCancel=new CancellationTokenSource();
        _downloadBytes=0;_downloadTotal=release.PackageSize;_downloadStage="Downloading";
        _updateMessage="Downloading and verifying. Keep the game open until preparation finishes.";
        _updatePreparation=Task.Run(()=>{
            Policy.ValidateGame(root);
            var old=Policy.VerifyInstalled(root,UpdateBuild.Trust,UpdateBuild.Channel,UpdateBuild.ReleaseId);
            string helper=Path.Combine(root,"BepInEx","framework-updater");Policy.NoLinks(helper);
            foreach(var pair in UpdateBuild.HelperHashes)Policy.Need(Policy.HashFile(Path.Combine(helper,pair.Key))==pair.Value,"Updater helper integrity check failed");
            var url=Policy.ValidateUrl(release.PackageUrl,UpdateBuild.Channel,Policy.ValidateUrl(UpdateBuild.Feed,UpdateBuild.Channel,null));
            byte[] archive=Policy.Download(url,(int)Policy.MaxPackage,release.PackageSize,(done,total)=>{Interlocked.Exchange(ref _downloadBytes,done);Interlocked.Exchange(ref _downloadTotal,total);},()=>_downloadCancel.IsCancellationRequested);
            _downloadCancel.Token.ThrowIfCancellationRequested();_downloadStage="Verifying";
            string home=Path.Combine(root,"BepInEx","framework-updates");Policy.NoLinks(home);Directory.CreateDirectory(home);
            Policy.Need(!File.Exists(Path.Combine(home,"pending")),"An update request is already pending; wait for its cancellation/completion");
            string payload=Path.Combine(home,"payload-"+Guid.NewGuid().ToString("N"));
            Policy.Extract(archive,release,payload);
            _downloadCancel.Token.ThrowIfCancellationRequested();_downloadStage="Preparing";
            // Request captures installed file hashes; helper rechecks after the game exits.
            string request=Path.Combine(home,"request.xml");
            string cancel=Path.Combine(home,"cancel");
            string ready=Path.Combine(home,"ready");if(File.Exists(ready))File.Delete(ready);
            File.WriteAllBytes(request,Policy.Request(root,UpdateBuild.ReleaseId,UpdateBuild.Channel,envelope,old));
            File.WriteAllText(Path.Combine(home,"pending"),payload);
            var psi=new ProcessStartInfo {
                FileName=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"System32","WindowsPowerShell","v1.0","powershell.exe"),
                Arguments="-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \""+Path.Combine(helper,"UpdateHelper.ps1")+"\" -RequestPath \""+request+"\"",
                UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden
            };
            try { _downloadCancel.Token.ThrowIfCancellationRequested();using(var process=Process.Start(psi)){
                Policy.Need(process!=null,"Could not start updater helper");
                var deadline=DateTime.UtcNow.AddSeconds(15);
                while(!File.Exists(ready) && !process.HasExited && DateTime.UtcNow<deadline)System.Threading.Thread.Sleep(100);
                if(!File.Exists(ready)){if(!process.HasExited)process.Kill();throw new IOException("Updater helper could not become ready; nothing installed");}
            } }
            catch {File.Delete(Path.Combine(home,"pending"));throw;}
            if(_downloadCancel.IsCancellationRequested){File.WriteAllText(cancel,"cancel");throw new OperationCanceledException("Update cancelled");}
            return "Update verified and ready. Exit the game normally. The updater will install it after the game closes. Wait for the completion message before restarting.";
        });
    }
}
