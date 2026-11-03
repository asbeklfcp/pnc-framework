using System.Collections.Generic;
using UnityEngine;
namespace PncEdi;
// Native Dragon's existing damage collider is on layer 19, outside the player's
// authored melee mask (8 | 18). Lease only that actor's collider object to layer 8.
// No size/shape, damage, weapon mask or global collision-matrix changes.
internal static class ChaserHitLayerLease {
    sealed class Entry {internal GameObject Actor,Object;internal int Original;}
    static readonly Dictionary<GameObject,Entry> Entries=new Dictionary<GameObject,Entry>();
    static readonly List<GameObject> Removed=new List<GameObject>();
    internal static void Register(GameObject actor) {
        if(!OverkeksMode.Active || actor==null || actor.GetComponent<DragonEnemyAI>()==null)return;
        if(!ChaserSweeperHooks.IsHuntingChaser(actor) && !ChaserFodderSpawn.IsFodder(actor))return;
        foreach(var collider in actor.GetComponentsInChildren<Collider>(true)) {
            var target=collider.gameObject;
            if(target.layer!=19 || Entries.ContainsKey(target))continue;
            Entries.Add(target,new Entry{Actor=actor,Object=target,Original=target.layer});target.layer=8;
        }
    }
    internal static void Tick() {
        Removed.Clear();
        foreach(var pair in Entries) {
            var e=pair.Value;
            bool keep=OverkeksMode.Active && e.Actor!=null && e.Actor.activeInHierarchy &&
                (ChaserSweeperHooks.IsHuntingChaser(e.Actor)||ChaserFodderSpawn.IsFodder(e.Actor));
            if(keep && e.Actor.GetComponent<DragonEnemyAI>() is DragonEnemyAI ai &&
                OptionalMembers.Read(ai,"isDead") is bool dead && dead)keep=false;
            if(keep)continue;
            if(e.Object!=null && e.Object.layer==8)e.Object.layer=e.Original;
            Removed.Add(pair.Key);
        }
        foreach(var key in Removed)Entries.Remove(key);
    }
}
