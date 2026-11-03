using UnityEngine;

namespace PncEdi;

/// <summary>
/// One switch that says which rules are in force, in place of four booleans that had to agree.
///
/// Before this, "what is this mod doing to the game right now" was the conjunction of
/// `EnableGameplayTweaks`, `GodMode`, `EnableHeatLocks` and a dozen sub-toggles, and several of
/// their 2^n combinations are not a game anyone wants - god mode *and* heat locks, or locks with
/// tweaks off so nothing reads them. A profile is the answer to that: pick one row of the table,
/// and the mod is internally consistent by construction.
///
/// **Custom preserves individual settings.** The collected 3.0.0 Bind default is PressureAndRelease. Custom means "read the individual settings, as
/// before", so an existing `com.edi.pnc.cfg` keeps behaving exactly as it did - upgrading must not
/// silently retune someone's game. The other three are opt-in, from the mod manager or the config.
///
/// Every gameplay patch stays installed under every profile and gates on
/// <see cref="TweaksEnabled"/> instead. That is what makes switching safe while a run is going: no
/// patch is ever added or removed at runtime, so there is no half-patched state to land in. What
/// the switch does have to do is undo the *stats* the previous profile wrote onto the player, which
/// is <see cref="OnProfileChanged"/>'s whole job.
///
/// Deliberately **not** ported from the fork this came from, because each would have been a second
/// rule for something this tree already decides once (§124's "one rule where there were two"):
///
/// - a per-class `ClassLockCounts` table. `HeatLockScaleSource` + `HeatLockUnitsPerLock` already
///   derive the lock count from the character's own capacity, which is the same fix for the same
///   problem (classes wanting different lock counts) expressed as a rule rather than a list.
/// - `BaseSpawnMultiplier` / `MaxSpawnBonus`. `SpawnCountMultiplier` and
///   `SpawnCountMultiplierAtFullLock` already state both ends of that ramp directly, which
///   `GameplayHooks.SpawnMultiplier` explains at length is the point.
/// - `HealthRegenAtFullPressure`. `ApplyHeatScaledAutoHeal` already scales regeneration by
///   `(1 - heat)^2`, and locks raise the heat floor, so regeneration already falls away as pressure
///   builds. A second lerp on lock progress would count the same pressure twice.
/// </summary>
internal static class GameplayProfiles
{
	// The player whose untouched stats were last observed, and those stats. Kept so a switch can
	// put back what the game itself had before any profile wrote to it - reading them at switch
	// time would read whatever the outgoing profile left behind.
	private static PlayerStats _observedPlayer;
	private static int _vanillaMaxHealth;
	private static float _vanillaMaxHeat;

	internal static GameplayProfile Current => Plugin.CfgGameplayProfile?.Value ?? GameplayProfile.Custom;
    internal static void ReturnToVanilla() => Plugin.CfgGameplayProfile.Value=GameplayProfile.Vanilla;

	private static bool Tweaks => Plugin.CfgEnableGameplayTweaks?.Value ?? false;
	private static bool GodMode => Plugin.CfgGodMode?.Value ?? false;
	private static bool HeatLocks => Plugin.CfgEnableHeatLocks?.Value ?? false;

	// The table itself lives in GameplayProfileRules, which knows nothing about ConfigEntry or
	// Unity and is therefore the part `code/tests` can check row by row. These four read the live
	// settings and hand them over; they are the only callers.

	/// <summary>Is the mod allowed to change the game at all? The master gate every patch reads.</summary>
	internal static bool TweaksEnabled => GameplayProfileRules.TweaksEnabled(Current, Tweaks);

	/// <summary>Damage and death blocked.</summary>
	internal static bool GodModeEnabled => GameplayProfileRules.GodModeEnabled(Current, Tweaks, GodMode);

	/// <summary>Heat locks are live. This is what <see cref="HeatLockSystem.Enabled"/> resolves to.</summary>
	internal static bool ReleaseModeEnabled => GameplayProfileRules.ReleaseModeEnabled(Current, Tweaks, HeatLocks);

	/// <summary>Should class heat capacity be rescaled?</summary>
	internal static bool UsesClassHeatScaling => GameplayProfileRules.UsesClassHeatScaling(Current, Tweaks, HeatLocks);

	/// <summary>
	/// Remember what the game gave the player, before any profile writes to it.
	///
	/// Called from the PlayerStats hooks on every scene, and guarded against god mode's own
	/// `PlayerMaxHealth`: that value is written *onto* the player, so observing it would make it
	/// the "vanilla" figure to restore, and the god-mode health bar would then survive a switch
	/// back to Vanilla for the rest of the session.
	/// </summary>
	internal static void ObserveVanillaStats(PlayerStats playerStats)
	{
		if (playerStats == null)
		{
			return;
		}
		if (_observedPlayer != playerStats)
		{
			_observedPlayer = playerStats;
			_vanillaMaxHealth = 0;
			_vanillaMaxHeat = 0f;
		}
		int maxHealth = Mathf.Max(1, playerStats.MaxHealth);
		if (maxHealth < (Plugin.CfgPlayerMaxHealth?.Value ?? int.MaxValue))
		{
			_vanillaMaxHealth = maxHealth;
		}
		_vanillaMaxHeat = Mathf.Max(1f, playerStats.MaxHeat);
	}

	/// <summary>
	/// Hand the player over from one profile to the next, mid-run.
	///
	/// Order matters and is the reason this is not just "set a flag": clear the transient locks
	/// first (they belong to the outgoing ruleset), put the untouched stats back, and only then let
	/// the incoming profile write its own. Doing it the other way round leaves the new profile's
	/// max health as the figure the *next* switch restores.
	/// </summary>
	internal static void OnProfileChanged(GameplayProfile previous, GameplayProfile current)
	{
		if (previous == current)
		{
			return;
		}

		OverkeksMode.Exit("profile switch");
		CommunityCombatRuntime.Reset();
		OverkeksFeatureRuntime.Reset();
		OverkeksEnemyRuntime.Reset();
		Plugin.LogProfileState("transition-before-restoration " + previous + " -> " + current);
		PlayerStats playerStats = _observedPlayer ?? Object.FindAnyObjectByType<PlayerStats>();
		HeatLockSystem.ResetForScene();
		if (playerStats != null)
		{
			RestoreVanillaStats(playerStats);
			if (ReleaseModeEnabled)
			{
				ClassHeatMultipliers.ApplyToPlayerHeat(PlayerClassManager.Instance?.GetSelectedClass(), playerStats);
				HeatLockSystem.RecordBaseHealth(playerStats);
			}
			else if (current == GameplayProfile.Custom && GodModeEnabled)
			{
				playerStats.SetMaxHealth(Mathf.Max(1, Plugin.CfgPlayerMaxHealth.Value));
			}
		}

		if (current == GameplayProfile.Overkeks) OverkeksMode.Enter();
        if(Current!=current)return; // Entry failure already switched safely to Vanilla.
		EnsureNamedGodModeHealth(playerStats);
		Plugin.DBG("PROFILE", previous + " -> " + current + "; transient locks cleared and player stats reapplied");
		Plugin.LogProfileState("transition-after-restoration " + previous + " -> " + current);
	}

	internal static void TickNamedGodModeHealth()
	{
		if (Current != GameplayProfile.GodMode) return;
		EnsureNamedGodModeHealth(_observedPlayer);
	}

	internal static void EnsureNamedGodModeHealth(PlayerStats playerStats)
	{
		if (playerStats != null && GodModeHealthPolicy.ShouldRefill(Current, playerStats.IsAlive,
			playerStats.CurrentHealth, playerStats.MaxHealth))
			playerStats.SetHealth(playerStats.MaxHealth, false);
	}

	private static void RestoreVanillaStats(PlayerStats playerStats)
	{
		if (_vanillaMaxHealth > 0 && playerStats.MaxHealth != _vanillaMaxHealth)
		{
			playerStats.SetMaxHealth(_vanillaMaxHealth, false);
		}
		if (_vanillaMaxHeat > 0f && Mathf.Abs(playerStats.MaxHeat - _vanillaMaxHeat) > 0.01f)
		{
			playerStats.SetMaxHeat(_vanillaMaxHeat);
		}
		playerStats.ClearOverheatIfBelowMax();
	}

	/// <summary>
	/// The 0-1 figure `FillerCumGalleryMap` buckets into `filler_cum_25/50/75`.
	///
	/// Heat is the vanilla answer and stays the default. Under Pressure and Release it is a poor
	/// one on its own: locks hold the heat floor up, so heat stops being a signal of what is
	/// happening and becomes a readout of how many locks are held - which lock progress says more
	/// directly and more smoothly. `Highest` takes whichever is currently stronger so a heat spike
	/// still registers on a lightly locked run.
	/// </summary>
	internal static float ResolveHandyIntensity(float heatPercent)
	{
		float heat = Mathf.Clamp01(heatPercent);
		float pressure = HeatLockSystem.CurrentLockProgress;
		HandyIntensitySource source = Plugin.CfgHandyIntensitySource?.Value ?? HandyIntensitySource.Automatic;
		if (source == HandyIntensitySource.Automatic)
		{
			source = ReleaseModeEnabled ? HandyIntensitySource.Highest : HandyIntensitySource.Heat;
		}
		switch (source)
		{
			case HandyIntensitySource.ReleasePressure:
				return pressure;
			case HandyIntensitySource.Highest:
				return Mathf.Max(heat, pressure);
			default:
				return heat;
		}
	}
}
