using System;
namespace PncEdi;
// Eligibility overlay, not another coroutine writing the native bool.
internal sealed class GrabRecoveryDeadline {
    WeakReference<object> owner;
    int scene,epoch;
    float until;
    internal void Arm(object player,int sceneId,int profileEpoch,float now,float seconds) {
        if(player==null)return;
        if(float.IsNaN(seconds) || float.IsInfinity(seconds))seconds=4f;
        bool same=owner!=null && owner.TryGetTarget(out var old) && Equals(old,player) && scene==sceneId && epoch==profileEpoch;
        float end=now+Math.Max(.5f,seconds);
        until=same?Math.Max(until,end):end;
        owner=new WeakReference<object>(player);scene=sceneId;epoch=profileEpoch;
    }
    internal bool Protects(object player,int sceneId,int profileEpoch,float now,bool overkeks) {
        if(!overkeks || sceneId!=scene || profileEpoch!=epoch || now>=until){Clear();return false;}
        if(owner==null || !owner.TryGetTarget(out var saved)){Clear();return false;}
        return player!=null && Equals(player,saved);
    }
    internal void Clear(){owner=null;scene=0;epoch=0;until=0;}
}
