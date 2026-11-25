using System;
using System.Collections.Generic;
using UnityEngine;
namespace PncPrivateLivingMap {
internal static class StableWaypoint {
    internal static float Length(List<Vector3> p){
        float sum=0;for(int i=1;i<p.Count;i++)sum+=Vector3.Distance(p[i-1],p[i]);return sum;
    }
    // A bounded forward projection avoids jumping to a later leg of a U-turn.
    // Accept only a locally validated connection to the existing path.
    internal static bool Trim(List<Vector3> path,Vector3 player,float maxAdvance,Func<Vector3,Vector3,bool> clear,out List<Vector3> result){
        return TrimInto(path,player,maxAdvance,clear,new List<Vector3>(),out result);
    }
    internal static bool TrimInto(List<Vector3> path,Vector3 player,float maxAdvance,Func<Vector3,Vector3,bool> clear,List<Vector3> output,out List<Vector3> result){
        result=null;output.Clear();if(path.Count<2)return false;
        float along=0,best=2.5f*2.5f,bestAlong=0;int index=-1;Vector3 point=default;
        for(int i=1;i<path.Count&&along<=maxAdvance;i++){
            var a=path[i-1];var d=path[i]-a;float len=d.magnitude;
            if(len<.001f)continue;
            float t=Mathf.Clamp(Vector3.Dot(player-a,d)/d.sqrMagnitude,0,Mathf.Clamp01((maxAdvance-along)/len));
            var p=a+d*t;float dist=(player-p).sqrMagnitude;
            // Prefer the earlier segment at a crossing unless distinctly closer.
            if(Mathf.Abs(player.y-p.y)<.85f&&dist<best-.0001f&&clear(player,p)){
                best=dist;index=i;point=p;bestAlong=along+len*t;
            }
            along+=len;
        }
        if(index<0)return false;
        output.Add(point);
        for(int i=index;i<path.Count;i++)if((output[output.Count-1]-path[i]).sqrMagnitude>.000001f)output.Add(path[i]);
        if(output.Count<2)return false;
        // Check the first several metres of the retained route for new obstacles.
        float checkedLength=0;
        for(int i=1;i<output.Count&&checkedLength<6f;i++){
            var a=output[i-1];var b=output[i];float len=Vector3.Distance(a,b);
            if(len<.001f)continue;
            var end=Vector3.Lerp(a,b,Mathf.Min(1,(6f-checkedLength)/len));
            if(!clear(a,end))return false;
            checkedLength+=len;
        }
        result=output;return true;
    }
    internal static bool PreferRoute(float remaining,bool locked,float candidate,bool candidateLocked){
        if(locked!=candidateLocked)return !candidateLocked;
        return PreferReplacement(remaining,candidate);
    }
    internal static bool PreferReplacement(float remaining,float candidate){
        // Hysteresis prevents route flapping from small navmesh changes.
        return candidate+Mathf.Max(2f,remaining*.025f)<remaining;
    }
}
}