using System;
namespace PncEdi;
// Editable scene state names retain their authored media references.
// Missing media stays explicit; never substitute another character's sound.
internal static class CaptureStateLibrary {
    internal static GrabAnimationData[] Select(string[] names,GrabAnimationData[] authored) {
        var selected=new GrabAnimationData[names.Length];
        for(int i=0;i<names.Length;i++) {
            selected[i]=new GrabAnimationData{animationName=names[i]};
            if(authored==null)continue;
            foreach(var item in authored)if(item!=null && string.Equals(item.animationName,names[i],StringComparison.OrdinalIgnoreCase) && item.sound!=null) {
                selected[i].sound=item.sound;break;
            }
        }
        return selected;
    }
}
