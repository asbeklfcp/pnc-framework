using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using Policy=PncFramework.ProtonUpdate.Policy;
namespace PncModManager;
public sealed partial class Plugin {
    void PrepareProtonUpdate(){
        byte[] envelope=_updateEnvelope;
        string root=Paths.GameRootPath,home=Path.Combine(root,"BepInEx","framework-updates");
        string pending=Path.Combine(home,"pending"),marker=Path.Combine(home,"proton-pending"),cancel=Path.Combine(home,"cancel"),request=Path.Combine(home,"request.xml");
        try{
            using(ProtonUpdate.Scope(root)){
                foreach(var path in new[]{pending,marker,cancel,request})Policy.SafeFile(path);
                if(File.Exists(pending)||File.Exists(marker)){_updateMessage="A prepared update is still pending.";return;}
                if(File.Exists(cancel))File.Delete(cancel);
            }
        }catch(Exception e){_updateMessage="Update was not prepared: "+e.Message;return;}
        _downloadCancel=new CancellationTokenSource();
        var token=_downloadCancel.Token;
        _downloadBytes=0;_downloadTotal=_updateOffer.PackageSize;_downloadStage="Downloading";
        _updateMessage="Downloading and verifying. Keep the game open until preparation finishes.";
        _updatePreparation=Task.Run(()=>{
            using(ProtonUpdate.Scope(root)){
                Policy.ValidateGame(root);
                var release=Policy.Verify(envelope,UpdateBuild.Trust,UpdateBuild.Channel,DateTime.UtcNow);
                Policy.Need(Policy.IsNewer(release,UpdateBuild.ReleaseId),"No newer release");
                var old=Policy.VerifyInstalled(root,UpdateBuild.Trust,UpdateBuild.Channel,UpdateBuild.ReleaseId);
                var url=Policy.ValidateUrl(release.PackageUrl,UpdateBuild.Channel,Policy.ValidateUrl(UpdateBuild.Feed,UpdateBuild.Channel,null));
                byte[] archive=Policy.Download(url,(int)Policy.MaxPackage,release.PackageSize,(done,total)=>{Interlocked.Exchange(ref _downloadBytes,done);Interlocked.Exchange(ref _downloadTotal,total);},()=>token.IsCancellationRequested);
                token.ThrowIfCancellationRequested();_downloadStage="Verifying";
                Policy.NoLinks(home);Directory.CreateDirectory(home);
                foreach(var path in new[]{pending,marker,cancel,request})Policy.SafeFile(path);
                Policy.Need(!File.Exists(pending)&&!File.Exists(marker),"Another update is pending");
                string stage=Path.Combine(home,"payload-"+Guid.NewGuid().ToString("N"));
                Policy.Extract(archive,release,stage);
                token.ThrowIfCancellationRequested();_downloadStage="Preparing";
                ProtonUpdate.PreparePatcher(root);
                File.WriteAllBytes(request,Policy.Request(root,UpdateBuild.ReleaseId,UpdateBuild.Channel,envelope,old));
                File.WriteAllText(pending,stage);
                try{
                    token.ThrowIfCancellationRequested();
                    File.WriteAllText(marker,"ready");
                    if(token.IsCancellationRequested){ProtonUpdate.Cancel(root);token.ThrowIfCancellationRequested();}
                }catch{if(File.Exists(marker))File.Delete(marker);if(File.Exists(pending))File.Delete(pending);throw;}
                return "Update verified. Exit and launch the game again to install before the framework loads.";
            }
        });
    }
}