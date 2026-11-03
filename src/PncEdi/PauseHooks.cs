using System;
using BepInEx.Logging;
using HarmonyLib;
using PixelCrushers.GridController;

namespace PncEdi;

[HarmonyPatch]
public static class PauseHooks
{
	public static bool GamePaused;

	// Whether the pause actually stopped the device. Under FillerWhilePaused it does not, and
	// the resume then has nothing to undo - sending Edi/Resume against a playback that was never
	// paused is the same contradictory instruction ResetForNewScene avoids below.
	private static bool _devicePaused;

	// Exposed so a focus-regain can tell "menu is open and holding the device paused" apart from
	// "menu is open but FillerWhilePaused left the device running" - only the former should defer
	// its Resume to the eventual menu close.
	public static bool DevicePausedByMenu => _devicePaused;

	/// <summary>
	/// Should a scene the mod is driving itself stop advancing?
	///
	/// True while the game's own pause menu is up, and while the mod manager's window has the
	/// screen - it holds `Time.timeScale` at zero, but `Update` runs regardless, so anything
	/// clocked on `unscaledDeltaTime` carries on behind it.
	///
	/// The custom-enemy scenes need this because they are clocked that way on purpose: a grab
	/// scene's overlay has to keep animating through whatever the game does to `timeScale` during
	/// a grab, so they cannot simply use `Time.deltaTime`. Without the check a paused game kept
	/// advancing a wall trap's animation stages - and each stage POSTs a new row to Edi, which is
	/// the pause/resume desync §104-§106 was about, arriving from a direction those did not cover.
	/// </summary>
	public static bool SceneClockHeld => GamePaused || ModManagerBridge.WindowOpen;

	// Quitting to the main menu from the pause menu never calls ResumeGame, so without this the
	// flag stays true into the next run: the next real pause is swallowed by PauseGame's
	// `if (!GamePaused)` guard, and the next unpause sends a Resume nothing asked for. The tell in
	// a log is a "[PAUSE] ... closed" with no "opened" before it (§99).
	//
	// The device still needs a Resume when the paused scene leaves through a menu: §148 made a
	// menu *play the filler* rather than stop Edi, and a Play does not lift the Pause
	// (`untilResume=true`) sent when the game paused. Call sites must run this before dispatching
	// the menu filler, or the Resume below would follow the Play it is meant to unblock.
	public static void ResetForNewScene()
	{
		if (GamePaused)
		{
			GamePaused = false;
			Plugin.ClearSavedFillerForMenu();
			if (_devicePaused)
			{
				_devicePaused = false;
				Plugin.DBG("PAUSE", "scene changed while paused -> Edi/Resume");
				Plugin.SendResume();
			}
			else
			{
				Plugin.DBG("PAUSE", "scene changed while paused (quit to menu?) -> pause state cleared");
			}
		}
	}

	[HarmonyPatch(typeof(PauseMenuManager), "PauseGame")]
	[HarmonyPostfix]
	public static void PauseGame_Postfix()
	{
		try
		{
			if (!GamePaused)
			{
				GamePaused = true;
				// Under FillerWhilePaused the menu always plays the base filler, whatever the
				// device was doing when it opened - the filler itself, or a real gallery row (a
				// grab, an interact scene). PauseFillerForMenu saves either kind and restores it
				// on close; with the setting off, anything playing just pauses with the game.
				if (Plugin.CfgFillerWhilePaused.Value && (Plugin.FillerPlaybackActive || Plugin.IsGalleryPlaybackActive))
				{
					_devicePaused = false;
					Plugin.PauseFillerForMenu();
					return;
				}
				_devicePaused = true;
				Plugin.DBG("PAUSE", "in-game pause menu opened -> Edi/Pause");
				Plugin.SendPause();
			}
		}
		catch (Exception ex)
		{
			Plugin.Log?.LogWarning($"PauseGame patch error: {ex}");
		}
	}

	[HarmonyPatch(typeof(PauseMenuManager), "ResumeGame")]
	[HarmonyPostfix]
	public static void ResumeGame_Postfix()
	{
		try
		{
			if (GamePaused)
			{
				GamePaused = false;
				if (!_devicePaused)
				{
					Plugin.ResumeFillerFromMenu();
					return;
				}
				_devicePaused = false;
				Plugin.DBG("PAUSE", "in-game pause menu closed -> Edi/Resume");
				Plugin.SendResume();
				if (Plugin.ShouldBeStopped)
				{
					Plugin.DBG("PAUSE", "was stopped before pause, re-stopping after resume");
					Plugin.SendStop();
				}
			}
		}
		catch (Exception ex)
		{
			Plugin.Log?.LogWarning($"ResumeGame patch error: {ex}");
		}
	}
}
