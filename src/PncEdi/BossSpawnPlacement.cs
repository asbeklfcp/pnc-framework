using UnityEngine;
namespace PncEdi;
// Bounded placement for manual boss tests only; natural spawn tables remain native.
internal static class BossSpawnPlacement {
    internal static bool TryGet(out Vector3 position,out Quaternion rotation) {
        position=Vector3.zero;rotation=Quaternion.identity;
        var player=GameObject.FindGameObjectWithTag("Player");if(player==null)return false;
        if(!Physics.Raycast(player.transform.position+Vector3.up*1.5f,Vector3.down,out var playerFloor,5f,-5,QueryTriggerInteraction.Ignore)) {
            Plugin.DBG("BOSS-PLACEMENT","rejected: no player floor");return false;
        }
        var camera=player.GetComponentInChildren<Camera>();
        var forward=camera!=null?camera.transform.forward:player.transform.forward;forward.y=0;
        if(forward.sqrMagnitude<.01f)forward=Vector3.forward;forward.Normalize();
        float distance=Mathf.Clamp(Plugin.CfgSpawnEnemyDistance.Value,2f,6f);
        var angles=new float[]{0,45,-45,90,-90,135,-135,180};
        int floor=0,clearance=0,path=0;
        foreach(float angle in angles) {
            var candidate=playerFloor.point+Quaternion.Euler(0,angle,0)*forward*distance;
            if(!Physics.Raycast(candidate+Vector3.up*2,Vector3.down,out var hit,4f,-5,QueryTriggerInteraction.Ignore) ||
                hit.normal.y<.65f || Mathf.Abs(hit.point.y-playerFloor.point.y)>.75f) {floor++;continue;}
            candidate=hit.point;
            if(!SpawnWalkability.TrySnapWalkable(candidate,playerFloor.point.y,.75f,out candidate)) {path++;continue;}
            // Require physical support at the snapped point, too; no hovering over channels.
            if(!Physics.Raycast(candidate+Vector3.up,Vector3.down,out hit,2f,-5,QueryTriggerInteraction.Ignore) ||
                hit.normal.y<.65f || Mathf.Abs(hit.point.y-playerFloor.point.y)>.75f) {floor++;continue;}
            candidate=hit.point;
            if(Physics.CheckCapsule(candidate+Vector3.up*.45f,candidate+Vector3.up*1.6f,.4f,-5,QueryTriggerInteraction.Ignore)) {clearance++;continue;}
            if(SpawnWalkability.IsPathBlocked(playerFloor.point,candidate)) {path++;continue;}
            position=candidate;
            var facing=player.transform.position-position;facing.y=0;
            if(facing.sqrMagnitude>.01f)rotation=Quaternion.LookRotation(facing.normalized);
            Plugin.DBG("BOSS-PLACEMENT","accepted="+position+"; angle="+angle+"; distance="+distance+"; rejected floor/clearance/path="+floor+"/"+clearance+"/"+path);
            return true;
        }
        Plugin.DBG("BOSS-PLACEMENT","no safe position; rejected floor/clearance/path="+floor+"/"+clearance+"/"+path+"; move to an open floor and retry");return false;
    }
}
