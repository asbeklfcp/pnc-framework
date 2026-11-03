using System;
using UnityEngine;
namespace PncEdi;
// Commit a selected variant to the live native session, not just the actor's next-grab data.
internal static class CaptureSessionMedia {
    internal static bool Bind(object screen,object actor,GrabAnimationData[] selected) {
        if(screen==null || actor==null || selected==null || selected.Length==0 ||
            !(OptionalMembers.Read(screen,"IsGrabbed") is bool grabbed && grabbed))return false;
        var owner=OptionalMembers.Read(screen,"GrabbingEnemy") ?? OptionalMembers.Read(screen,"hiddenEnemyToRestore");
        if(!ReferenceEquals(owner,actor))return false;
        // Validate the private native contract before committing either field.
        var tableField=OptionalMembers.Field(screen.GetType(),"currentGrabAnimations");
        var hashField=OptionalMembers.Field(screen.GetType(),"currentAnimStateHash");
        if(tableField==null || hashField==null || tableField.IsInitOnly || hashField.IsInitOnly ||
            tableField.FieldType!=typeof(GrabAnimationData[]) || hashField.FieldType!=typeof(int)) {
            Plugin.DBG("CAPTURE-MEDIA","session schema missing; no partial binding committed");return false;
        }
        var snapshot=new GrabAnimationData[selected.Length];
        int sounds=0;
        for(int i=0;i<selected.Length;i++) {
            var entry=selected[i];
            if(entry==null || string.IsNullOrEmpty(entry.animationName))return false;
            snapshot[i]=new GrabAnimationData{animationName=entry.animationName,sound=entry.sound};
            if(entry.sound!=null)sounds++;
        }
        OptionalMembers.Write(screen,"currentGrabAnimations",snapshot);
        // Native CheckAnimationStateSound ignores unchanged hashes, even after table replacement.
        OptionalMembers.Write(screen,"currentAnimStateHash",int.MinValue);
        GrabHooks.CurrentAnims=snapshot;
        // This is the capture-owned source, not a global or actor audio source.
        if(OptionalMembers.Read(screen,"audioSource") is AudioSource audio && audio.isPlaying)audio.Stop();
        Plugin.DBG("CAPTURE-MEDIA","session snapshot bound; states="+snapshot.Length+"; sounds="+sounds+"; first="+snapshot[0].animationName);
        return true;
    }
}
