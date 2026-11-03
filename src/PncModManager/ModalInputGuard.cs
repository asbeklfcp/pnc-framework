using System;
using HarmonyLib;
using UnityEngine.UI;
namespace PncModManager;
public sealed partial class Plugin {
    static Plugin _modalInputOwner;
    Harmony _modalInputHarmony;
    void InstallModalInputGuard(){
        var press=AccessTools.Method(typeof(Button),"Press",Type.EmptyTypes);
        if(press==null)throw new MissingMethodException("Native button dispatch method unavailable");
        _modalInputOwner=this;
        _modalInputHarmony=new Harmony(PluginGuid+".modal-input");
        // Public entry methods prevent inlined private Press paths bypassing the guard.
        foreach(var method in new[]{"OnPointerClick","OnSubmit"})
            _modalInputHarmony.Patch(AccessTools.Method(typeof(Button),method),prefix:new HarmonyMethod(typeof(Plugin),nameof(AllowNativeButtonDispatch)));
        _modalInputHarmony.Patch(AccessTools.Method(typeof(MainMenuManager),"OpenOptions"),prefix:new HarmonyMethod(typeof(Plugin),nameof(TraceOptionsEntry)));
        Logger.LogInfo("[UI-MODAL] Native mouse/submit dispatch guard installed");
    }
    static bool AllowNativeButtonDispatch(Button __instance){
        var owner=_modalInputOwner;
        if(owner==null || !OverlayInputPolicy.BlockNativeDispatch(owner._visible || owner._patchNotesOpen,owner._backgroundRestoreFrame>=0))return true;
        return false;
    }
    static bool TraceOptionsEntry(){
        var owner=_modalInputOwner;if(owner==null)return true;
        bool blocked=OverlayInputPolicy.BlockNativeDispatch(owner._visible || owner._patchNotesOpen,owner._backgroundRestoreFrame>=0);
        return !blocked;
    }
    void RemoveModalInputGuard(){
        if(_modalInputOwner==this)_modalInputOwner=null;
        _modalInputHarmony?.UnpatchSelf();_modalInputHarmony=null;
    }
}
