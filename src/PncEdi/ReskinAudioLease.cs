using System.Collections.Generic;
using UnityEngine;
namespace PncEdi;
// Audio belongs to presentation roles, not the donor model's species.
// Suppress only the explicitly unwanted donor movement identity loop.
// Native ability cues stay intact until a content definition supplies a replacement.
internal sealed class ReskinAudioLease {
    readonly MonoBehaviour actor;
    readonly Dictionary<string,AudioClip> original=new Dictionary<string,AudioClip>();
    internal ReskinAudioLease(MonoBehaviour owner) {
        actor=owner;
        foreach(var field in new[]{"idleMovingSound"})
            if(OptionalMembers.Read(actor,field) is AudioClip clip)original[field]=clip;
    }
    internal void Apply() {
        if(actor==null)return;
        var loop=OptionalMembers.Read(actor,"loopingAudioSource") as AudioSource;
        foreach(var pair in original) {
            // Stop only an identified donor loop, never a scene or attack source.
            if(loop!=null && loop.clip==pair.Value && loop.isPlaying)loop.Stop();
            OptionalMembers.Write(actor,pair.Key,null);
        }
    }
    internal void Restore() {
        if(actor==null)return;
        foreach(var pair in original)if(OptionalMembers.Read(actor,pair.Key)==null)OptionalMembers.Write(actor,pair.Key,pair.Value);
    }
}
