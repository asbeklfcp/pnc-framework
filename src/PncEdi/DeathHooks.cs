using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PncEdi;

[HarmonyPatch]
public static class DeathHooks
{
	[HarmonyPatch(typeof(PlayerStats), "Die")]
	[HarmonyPostfix]
	public static void Die_Postfix()
	{
		try
		{
			Plugin.PlayerDead = true;
			GrabScreen grabScreen = GrabScreen.Instance;
			GrappleScreenobject grappleScreenobject = GrappleScreenobject.Instance;
			bool grabbed = grabScreen != null && grabScreen.IsGrabbed;
			bool grappling = grappleScreenobject != null && grappleScreenobject.IsGrappling;
			bool active = CameraSwapHooks.Active;
			if (grabbed | grappling | active)
			{
				// The death itself is blocked while a scene runs (GameplayHooks.Die_Prefix
				// via ShouldBlockLethalHealth), but this postfix still runs - Harmony runs
				// postfixes even when a prefix returns false. Health has already reached 0
				// because TakeDamage writes currentHealth directly, so arm the deferred
				// game over here: without it, dying *during* a scene left the run with
				// PlayerDead set (enemies inert) and no game over ever presented, because
				// the arming sites are all scene-*entry* hooks that require the player to
				// be dead before the scene starts.
				// Skipped while CompleteSceneEntrySurvival is forcing its own Die(),
				// otherwise that call would immediately re-arm what it just consumed.
				if (!HeatLockSystem.ForcingPendingSceneEntryGameOver)
				{
					// An imp grapple plays the death out instead: health stays at 0, the
					// struggle is locked, and imps are called in until the trio scene takes
					// over as the death scene. Everything else keeps the revive-to-1-HP
					// path, which is the only way those scenes can finish at all.
					if (grabbed || !grappling || !GrappleDeathSequence.HandleDeath(grappleScreenobject))
					{
						HeatLockSystem.EnsureSceneEntrySurvival("death during " + (grabbed ? "grab" : (grappling ? "grapple" : "interact")));
					}
				}
				Plugin.DBG("PLAYER-DEATH", "during " + (grabbed ? "grab" : (grappling ? "grapple" : "interact")) + " — letting scene play out");
				return;
			}
			Plugin.DBG("PLAYER-DEATH", "force filler");
			Plugin.GoFiller();
		}
		catch (Exception ex)
		{
			Plugin.Log?.LogWarning($"Die patch error: {ex}");
		}
	}

	[HarmonyPatch(typeof(PlayerStats), "Revive")]
	[HarmonyPostfix]
	public static void Revive_Postfix()
	{
		try
		{
			Plugin.PlayerDead = false;
			// The death-grab handoff: a scene has claimed the dead player, so the game over
			// that was presenting stands down until this scene ends (§77).
			DragonGrabGameOverGuard.ReleaseGameOverPresentation("Revive", respectPrompt: true);
			// A grab scene revives the player *into* the scene: vanilla's Revive() lands here
			// about two milliseconds after GrabHooks has already dispatched the scene's
			// gallery, so forcing filler stomps it and the device plays filler_damage_* for
			// the whole death scene. Only fall back to filler when nothing else is playing.
			if (((OverkeksMode.UsesRecipe ? OverkeksConfig.CfgReviveKeepsSceneGallery : Plugin.CfgReviveKeepsSceneGallery)?.Value ?? true) && Plugin.IsGalleryPlaybackActive)
			{
				Plugin.DBG("PLAYER-REVIVE", "ok - keeping the scene gallery, no filler");
				return;
			}
			Plugin.DBG("PLAYER-REVIVE", "ok");
			Plugin.GoFiller();
		}
		catch (Exception ex)
		{
			Plugin.Log?.LogWarning($"Revive patch error: {ex}");
		}
	}
}
