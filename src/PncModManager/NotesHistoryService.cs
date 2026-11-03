using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using BepInEx;
using PncFramework.Update;
using PncFramework.UpdateUI;
namespace PncModManager;
public sealed partial class Plugin {
    ReleaseHistory _notesHistory=new ReleaseHistory();
    Task<ReleaseHistory> _historyTask;
    string _historyStatus="";
    void LoadNotesHistory(){
        string cache=Path.Combine(NativeTypeface.CacheDirectory,"history-"+UpdateBuild.Channel+".xml");
        try {if(File.Exists(cache))_notesHistory=ReleaseHistory.Parse(Policy.VerifySigned(File.ReadAllBytes(cache),UpdateBuild.Trust),UpdateBuild.Channel);}catch(Exception e){Logger.LogWarning("[NOTES] Ignored invalid cached history: "+e.Message);}
        if(_notesHistory.Releases.Count==0 && UpdateBuild.Channel=="private"){
            using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("PncUI.PrivatePreviewHistory.xml"))if(stream!=null){using(var bytes=new MemoryStream()){stream.CopyTo(bytes);_notesHistory=ReleaseHistory.Parse(bytes.ToArray(),"private");}}
        }
        if(String.IsNullOrEmpty(UpdateBuild.Feed) || _historyTask!=null)return;
        _historyTask=Task.Run(()=>{
            var feed=Policy.ValidateUrl(UpdateBuild.Feed,UpdateBuild.Channel,null);
            var historyUrl=new Uri(feed,"history.xml");Policy.ValidateUrl(historyUrl.ToString(),UpdateBuild.Channel,feed);
            byte[] envelope=Policy.Download(historyUrl,256*1024,-1);
            var result=ReleaseHistory.Parse(Policy.VerifySigned(envelope,UpdateBuild.Trust),UpdateBuild.Channel);
            Directory.CreateDirectory(NativeTypeface.CacheDirectory);File.WriteAllBytes(cache,envelope);return result;
        });
    }
    void TickNotesHistory(){
        if(_historyTask==null || !_historyTask.IsCompleted)return;
        try{_notesHistory=_historyTask.GetAwaiter().GetResult();_historyStatus="";}
        catch{_historyStatus=_notesHistory.Releases.Count>0?"Offline - showing saved announcements.":"No announcements are stored yet.";}
        _historyTask=null;
    }
}
