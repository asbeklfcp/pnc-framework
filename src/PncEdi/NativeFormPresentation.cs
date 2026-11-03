using System;
using System.Reflection;
using UnityEngine;
namespace PncEdi;
// Recover native animator presentation after a disable/reveal cycle, without changing stage or HP.
internal static class NativeFormPresentation {
    static readonly MethodInfo Apply=typeof(EnemyAI).GetMethod("ApplyTransformedAnimatorState",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
    internal static bool Restore(MonoBehaviour ai) {
        if(ai is not EnemyAI || Apply==null || !(OptionalMembers.Read(ai,"isTransformed") is bool transformed && transformed) ||
            OptionalMembers.Read(ai,"isDead") is bool dead && dead || OptionalMembers.Read(ai,"currentHealth") is int hp && hp<=0)return false;
        try {Apply.Invoke(ai,null);Plugin.DBG("NATIVE-FORM","reapplied transformed presentation; actor="+ai.gameObject.name+"; id="+ai.GetInstanceID());return true;}
        catch(Exception ex){Plugin.Log?.LogWarning("[NATIVE-FORM] presentation restore failed: "+ex.Message);return false;}
    }
}
