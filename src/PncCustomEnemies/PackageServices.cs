using System;
using BepInEx.Configuration;
using BepInEx.Unity.Mono.Configuration;
using PncEdi;
using UnityEngine;

namespace PncCustomEnemies.Api;

/// <summary>
/// What the mod does for a package's behaviour: play a row, take a lock, ask what the gallery
/// knows, log.
///
/// **This class exists because `InternalsVisibleTo` cannot be given to a package.** The framework
/// holds it from PncEdi and can call anything; a third-party assembly has to compile against a
/// public surface, and one that grew by accident - "make this member public, the witch needs it" -
/// would end up exposing the whole mod to anything that referenced the DLL. So the surface is this
/// file, deliberately small, and every member is here because a behaviour that was already written
/// needed it (§165). The pattern is the one `learnings/modding-harmony-bepinex.md` records for the
/// plugin split: grant the narrowest thing that keeps the moved code compiling.
///
/// Everything here is a forward. The mod's own rules - the Intensity owners, the row hold, the
/// filler's schedule - stay on the mod's side, so a package cannot get between them by calling in a
/// different order than the framework would have.
/// </summary>
public static class ModServices
{
	/// <summary>Play a gallery row on the device. `preservePhase` carries the current playback phase into the new row, which is what a set of rows switched between mid-scene wants.</summary>
	public static void PlayGallery(string galleryName, bool loop = true, bool inGame = true, bool preservePhase = false, int seekOverrideMs = -1)
	{
		Plugin.SendPlay(galleryName, loop, inGame, false, preservePhase, -1f, 0f, seekOverrideMs);
	}

	/// <summary>Hand the channel back to the mod's filler. A package's scene ends by calling this rather than by stopping the device, so whatever the mod would be playing resumes.</summary>
	public static void ReleaseToFiller()
	{
		Plugin.GoFiller();
	}

	/// <summary>Whether a row name exists - the gallery registry, which is the seed list plus every row of the `Definitions.csv` actually loaded. A behaviour picking between rows tests this rather than assuming its package's rows were installed.</summary>
	public static bool IsGalleryRow(string galleryName)
	{
		return GalleryRegistry.IsKnown(galleryName);
	}

	/// <summary>How many heat locks the player is holding, or 0 when the heat-lock profile is off. Read it, never cache it.</summary>
	public static int CurrentHeatLocks => HeatLockSystem.CurrentLocks;

	/// <summary>
	/// Take a lock for an effect that is not a scene - an aura, a field, anything that charges the
	/// player for standing somewhere. Returns whether a lock was actually gained, which is false
	/// when the counter is already full or the profile is off; a lock cue plays on true only.
	/// </summary>
	public static bool AddAuraLock(string label)
	{
		return HeatLockSystem.AddTimedAuraLock(label);
	}

	/// <summary>The escape hint the mod shows during its own scenes, so a package's scene shows the same words and the same key.</summary>
	public static string EscapeHintText()
	{
		return SceneEscapeGate.BuildHintText();
	}

	/// <summary>Every enemy AI component type the game has. A behaviour that borrows a vanilla enemy for its movement suppresses these rather than naming one class and missing the other six.</summary>
	public static Type[] EnemyAiTypes => PncEdi.EnemyAiTypes.All;

	/// <summary>Unity's `(Clone)` suffix removed, the mod's way, so a package's name matching agrees with the mod's.</summary>
	public static string StripCloneSuffix(string name)
	{
		return NameRemap.StripCloneSuffix(name);
	}

	/// <summary>
	/// Whether the game is paused, as the mod sees it. A package that draws its own overlay or
	/// plays its own audio has to answer to this: `Time.timeScale` is zero but `Update` keeps
	/// running, and a canvas left in front of the pause menu is the visible failure
	/// (`learnings/modding-harmony-bepinex.md`).
	/// </summary>
	public static bool GamePaused => PauseHooks.GamePaused;

	/// <summary>
	/// Paused *or* the mod manager is open. A package's own scene clocks read this rather than
	/// `GamePaused`: an IMGUI window does not stop `Update`, so a stage timer that only watches the
	/// pause flag runs on while someone edits a setting (`learnings/modding-harmony-bepinex.md`).
	/// </summary>
	public static bool SceneClockHeld => PauseHooks.SceneClockHeld;

	/// <summary>
	/// Whether one of the mod's own escape scenes is running. A package that owns a scene tests this
	/// before starting another: two scenes at once means two owners of one device channel, and the
	/// second one wins by accident rather than by rule.
	/// </summary>
	public static bool EscapeSceneActive => GrabEndHelper.IsEscapeSceneActive();

	/// <summary>Stop the device. A package ending its own scene usually wants <see cref="ReleaseToFiller"/> instead, which hands the channel back rather than leaving it silent.</summary>
	public static void StopDevice()
	{
		Plugin.SendStop();
	}

	/// <summary>The mod's own slug rule - what turns a display name into a row-name fragment. Use it so a package's generated names agree with the mod's.</summary>
	public static string Slug(string raw)
	{
		return NameRemap.Slug(raw);
	}

	/// <summary>
	/// Whether debug spawn keys are allowed at all. This gate lives in the *core* mod's config and
	/// covers every spawn hotkey in the install, deliberately: two settings of that name in two
	/// files is how they end up disagreeing (§131). A package with a placement key of its own reads
	/// this before acting on it.
	/// </summary>
	public static bool DebugSpawnEnabled => Plugin.CfgEnableDebugEnemySpawn == null || Plugin.CfgEnableDebugEnemySpawn.Value;

	/// <summary>
	/// A hotkey test that behaves like the mod's own. BepInEx's `KeyboardShortcut.IsDown()`
	/// additionally requires that no other key is held, so a bare debug key never fires while the
	/// player is walking; this keeps configured modifiers and drops that rule, and stands down
	/// while the mod manager has the keyboard.
	/// </summary>
	public static bool HotkeyPressed(ConfigEntry<KeyboardShortcut> shortcut)
	{
		return Hotkeys.IsDown(shortcut);
	}

	/// <summary>
	/// One ordinary enemy prefab from the mod's spawn pool - what a behaviour calling in
	/// reinforcements should spawn. It deliberately never returns a custom enemy: a boss whose
	/// reinforcements can be bosses compounds without limit.
	/// </summary>
	public static GameObject PickReinforcementPrefab()
	{
		return EnemySpawnShuffle.PickNormalReinforcementPrefab();
	}

	/// <summary>Make an enemy the mod spawned live and visible, the same way the debug spawner does. A behaviour that calls in reinforcements needs it.</summary>
	public static void EnsureSpawnActive(GameObject enemy)
	{
		DebugEnemySpawn.EnsureSpawnActive(enemy);
	}

	/// <summary>A line in the mod's log, gated by the mod's own `Debug` setting and stamped with the time like every other diagnostic. `tag` is the bracketed event, e.g. `CHARM`.</summary>
	public static void Debug(string tag, string message)
	{
		Plugin.DBG(tag, message);
	}

	/// <summary>A line in the log that is not gated by `Debug` - for what a player must see whether or not they turned diagnostics on.</summary>
	public static void Log(string message)
	{
		Plugin.Log?.LogInfo(message);
	}

	/// <summary>A warning in the log.</summary>
	public static void LogWarning(string message)
	{
		Plugin.Log?.LogWarning(message);
	}

	/// <summary>An error in the log.</summary>
	public static void LogError(string message)
	{
		Plugin.Log?.LogError(message);
	}
}
