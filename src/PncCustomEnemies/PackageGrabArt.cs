using HarmonyLib;
using UnityEngine;

namespace PncEdi;

/// <summary>
/// Hides vanilla's own grab-screen artwork for the length of a package's capture.
///
/// A package captures the player by calling `GrabScreen.StartGrab(gameObject, null, ...)`, which
/// runs vanilla's whole presentation - including `ShowGrabUI`, which switches on `grabImage`, the
/// object the grab-screen animator draws the enemy's scene into. Nobody swaps the controller for a
/// package, because a package has no vanilla grab animations, so what that animator draws is
/// whichever enemy was grabbed last. The 2026-08-26 23:58 run caught it: the witch's capture ran
/// with `BlackSerpent GrabScreen_Cum` playing underneath her dream video.
///
/// The wall trap never showed it only because its overlay is opaque; the witch's is a 76%-alpha
/// backdrop, so the serpent came through the remaining quarter.
///
/// So the art is switched off at capture, and **never switched back on here**. Vanilla owns that
/// field in both directions - `ShowGrabUI` enables it at the start of a real grab, `HideGrabUI`
/// disables it at EndGrab - so there is nothing for a package to restore.
///
/// Restoring it was tried and was a bug (2026-08-27). A capture ends by calling `EndGrab`, which
/// runs `HideGrabUI` and switches the art off; the package's own teardown runs *after* that, so a
/// restore switched it straight back on with no grab to contain it. The serpent scene stayed on
/// screen with the player free to walk around behind it, until the next grab replaced it.
/// </summary>
internal static class PackageGrabArt
{
	internal static void Hide(GrabScreen screen)
	{
		GameObject art = Read(screen);
		if (art != null && art.activeSelf)
		{
			art.SetActive(false);
		}
	}

	private static GameObject Read(GrabScreen screen)
	{
		return screen == null ? null : Traverse.Create((object)screen).Field("grabImage").GetValue<GameObject>();
	}
}
