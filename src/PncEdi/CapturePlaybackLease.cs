using System;
namespace PncEdi;
internal interface ICapturePlayback {
    bool Valid {get;} bool Playing {get;} object Clip {get;}
    void Pause();void Resume();void Stop();
}
// Only resumes audio this lease paused, for this still-active capture and clip.
internal sealed class CapturePlaybackLease {
    object owner,clip;ICapturePlayback source;bool paused;
    readonly Action<string> report;
    internal CapturePlaybackLease(Action<string> report=null){this.report=report;}
    internal void Tick(object activeOwner,ICapturePlayback playback,bool held) {
        if(activeOwner==null || playback==null || !playback.Valid){Clear();return;}
        if(!Equals(owner,activeOwner)||!ReferenceEquals(source,playback)) {
            Clear();owner=activeOwner;source=playback;
        }
        if(held) {
            if(source.Playing){clip=source.Clip;source.Pause();paused=true;report?.Invoke("paused capture source");}
            return;
        }
        if(paused) {
            // UnPause cannot restart a stopped source, unlike Play. Never restart a
            // replaced clip or an ended scene, or duplicate an independently resumed source.
            if(Equals(clip,source.Clip) && !source.Playing){source.Resume();report?.Invoke("resume requested for capture source");}
            paused=false;clip=null;
        }
    }
    internal void Clear() {
        if(paused && source!=null && Equals(clip,source.Clip) && !source.Playing)source.Stop();
        owner=null;source=null;clip=null;paused=false;
    }
}
