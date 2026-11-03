using System.Collections.Generic;
using UnityEngine;
namespace PncEdi;
internal static class OwnedBossHudCleanup {
    internal static HashSet<IMiniBoss> Capture(IEnumerable<Object> owned) {
        var bosses=new HashSet<IMiniBoss>();
        foreach(var value in owned) {
            if(value==null)continue;
            if(value is IMiniBoss direct)bosses.Add(direct);
            if(value is GameObject go)
                foreach(var component in go.GetComponentsInChildren<MonoBehaviour>(true))
                    if(component is IMiniBoss boss)bosses.Add(boss);
        }
        return bosses;
    }
    internal static void Remove(IEnumerable<IMiniBoss> bosses) {
        // Native Unregister removes only this owner and adopts any remaining native boss.
        // Run after presentation snapshots restore, so stale owned snapshots cannot return.
        foreach(var boss in bosses)MiniBossHealthBarUI.Unregister(boss);
    }
}
