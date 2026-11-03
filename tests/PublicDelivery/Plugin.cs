using System;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Unity.Mono;
using HarmonyLib;
namespace PncUpdaterValidation {
 [BepInPlugin("com.pnc.framework.updater-validation", "PNC Updater Validation Route (remove after test)", "1.0.0")]
 [BepInDependency("com.pnc.modmanager")]
 public sealed class Plugin : BaseUnityPlugin {
  Harmony patch;
  void Awake() {
   string marker=Path.Combine(Paths.GameRootPath,"BepInEx","framework-updates","updater-validation-0.1.1.optin");
   if(!File.Exists(marker) || File.ReadAllText(marker).Trim()!="PNC updater public delivery validation 0.1.1") { Logger.LogWarning("[UPDATER-VALIDATION] No explicit test marker; inactive.");return; }
   var policy=typeof(PncFramework.Update.Policy);
   string version=policy.Assembly.GetName().Version.ToString();
   if(version!="1.8.11.0" && version!="1.8.12.0") { Logger.LogWarning("[UPDATER-VALIDATION] Unsupported manager version; inactive.");return; }
   var method=policy.GetMethod("Download",new[]{typeof(Uri),typeof(int),typeof(long),typeof(Action<long,long>),typeof(Func<bool>)});
   if(method==null)throw new MissingMethodException("Exact shipped download method not found");
   patch=new Harmony("com.pnc.framework.updater-validation");
   patch.Patch(method,prefix:new HarmonyMethod(typeof(Plugin).GetMethod("BeforeDownload",BindingFlags.NonPublic|BindingFlags.Static)));
   Logger.LogWarning("[UPDATER-VALIDATION] Controlled metadata route active on this PC. TLS, pinned signature, archive verification, GitHub package URL and installer are unchanged. Remove the validation DLL and marker after acceptance.");
  }
  static void BeforeDownload(ref Uri uri) { uri=MetadataRoute.Apply(uri); }
  void OnDestroy() { if(patch!=null)patch.UnpatchSelf(); }
 }
}
