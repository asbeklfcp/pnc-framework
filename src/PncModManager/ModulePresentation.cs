namespace PncModManager;
public sealed partial class Plugin {
    static string ModuleTitle(string id,string fallback){switch(id){
        case "com.edi.pnc":return "Gameplay & integrations";
        case "com.edi.pnc.customenemies":return "Optional characters";
        case "com.pnc.modmanager":return "Mod manager";
        case "com.pnc.roomcontent":return "Experimental rooms";
        default:return NativeTypeface.Clean(fallback);
    }}
    static string ModuleDescription(string id){switch(id){
        case "com.edi.pnc":return "Gameplay profiles, capture settings and optional integrations.";
        case "com.edi.pnc.customenemies":return "Optional character packages and gallery settings.";
        case "com.pnc.modmanager":return "Interface preferences and manager controls.";
        case "com.pnc.roomcontent":return "Experimental room content. Separate from the core release.";
        default:return "Settings provided by this module.";
    }}
}
