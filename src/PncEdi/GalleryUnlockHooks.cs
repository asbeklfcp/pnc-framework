using HarmonyLib;

namespace PncEdi;

[HarmonyPatch]
public static class GalleryUnlockHooks
{
	[HarmonyPatch(typeof(GalleryProgressManager), "IsEnemyUnlocked")]
	[HarmonyPrefix]
	public static bool IsEnemyUnlocked_Prefix(ref bool __result)
	{
		if (Plugin.CfgUnlockAllGallery.Value)
		{
			__result = true;
			return false;
		}
		return true;
	}
}
