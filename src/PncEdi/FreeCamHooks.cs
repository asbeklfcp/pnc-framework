using HarmonyLib;
using PixelCrushers.GridController;

namespace PncEdi;

[HarmonyPatch]
internal static class FreeCamHooks
{
	[HarmonyPatch(typeof(InventoryUI), "OpenInventory")]
	[HarmonyPrefix]
	private static void OpenInventory_Prefix()
	{
		if (FreeCam.Active)
		{
			FreeCam.Disable();
			Plugin.DBG("FREECAM", "OFF (inventory opened)");
		}
	}
}
