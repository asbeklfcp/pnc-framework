using System;
namespace PncEdi;
// A late animation callback may outlive IsGrabbed. Retain only this accepted
// capture's screen/scope until another accepted capture replaces it.
internal sealed class CaptureTeleportLease {
    WeakReference<object> screen;
    int scene,epoch;
    internal void Accepted(object owner,int sceneId,int profileEpoch,bool persistentChaser) {
        Clear();
        if(owner==null || !persistentChaser)return;
        screen=new WeakReference<object>(owner);scene=sceneId;epoch=profileEpoch;
    }
    internal bool ShouldBlock(object owner,int sceneId,int profileEpoch,bool overkeks,bool blackRoomTarget) {
        if(!overkeks || sceneId!=scene || profileEpoch!=epoch){Clear();return false;}
        if(screen==null || !screen.TryGetTarget(out var saved)){Clear();return false;}
        return owner!=null && Equals(saved,owner) && blackRoomTarget;
    }
    internal void Clear(){screen=null;scene=0;epoch=0;}
}
