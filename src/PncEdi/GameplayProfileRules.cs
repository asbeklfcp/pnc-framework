namespace PncEdi;

/// <summary>Which coherent set of gameplay rules is in force. See <see cref="GameplayProfiles"/>.</summary>
public enum GameplayProfile
{
	/// <summary>The game's own combat, health, heat and spawning. Scene detection and playback stay on.</summary>
	Vanilla,
	/// <summary>Heat locks, class heat scaling and lock-scaled spawn pressure, all on together.</summary>
	PressureAndRelease,
	/// <summary>Damage and death blocked, locks off. For exploring and finding gallery rows.</summary>
	GodMode,
	/// <summary>Obey the individual Gameplay settings, exactly as before profiles existed.</summary>
	Custom,
	/// <summary>Overkeks October 2026 combat/heat overhaul, isolated from Custom.</summary>
	Overkeks = 4
}

/// <summary>Where the filler intensity that picks `filler_cum_*` comes from.</summary>
public enum HandyIntensitySource
{
	/// <summary>Heat under Vanilla and God Mode; the stronger of heat and lock pressure under Pressure and Release.</summary>
	Automatic,
	/// <summary>The game's own heat bar, only.</summary>
	Heat,
	/// <summary>Lock progress, only.</summary>
	ReleasePressure,
	/// <summary>Whichever of heat and lock pressure is currently stronger.</summary>
	Highest
}

/// <summary>
/// The profile truth table, as four pure functions of the four settings.
///
/// Split out of <see cref="GameplayProfiles"/> for one reason: this is the part that decides what
/// the mod is doing to the game, and everything around it - `PlayerStats`, `HeatLockSystem`,
/// `Object.FindAnyObjectByType` - only exists inside a running Unity player. Keeping the table
/// free of all of that is what lets `code/tests` compile it and check every row, rather than the
/// rows only ever being verified by playing the game and noticing something wrong.
///
/// The properties on `GameplayProfiles` are the only callers; they read the live config entries
/// and pass them in. Nothing here reads a `ConfigEntry` itself, on purpose. The two enums live
/// here for the same reason: `PluginConfig` binds them, so they must be reachable from a file the
/// test project can compile without dragging a Unity player in behind them.
/// </summary>
internal static class GameplayProfileRules
{
	/// <summary>Is the mod allowed to change the game at all?</summary>
	internal static bool TweaksEnabled(GameplayProfile profile, bool tweaks)
	{
		switch (profile)
		{
			case GameplayProfile.Vanilla:
				return false;
			case GameplayProfile.Custom:
				return tweaks;
			default:
				// Pressure and Release and God Mode both change the game by definition, so neither
				// asks the legacy master switch - a config with `Enabled = false` still gets the
				// profile it picked.
				return true;
		}
	}

	/// <summary>Damage and death blocked.</summary>
	internal static bool GodModeEnabled(GameplayProfile profile, bool tweaks, bool godMode)
	{
		if (profile == GameplayProfile.GodMode || profile == GameplayProfile.Overkeks)
		{
			return true;
		}
		return profile == GameplayProfile.Custom && tweaks && godMode;
	}

	/// <summary>Heat locks are live.</summary>
	internal static bool ReleaseModeEnabled(GameplayProfile profile, bool tweaks, bool heatLocks)
	{
		if (profile == GameplayProfile.PressureAndRelease)
		{
			return true;
		}
		return profile == GameplayProfile.Custom && tweaks && heatLocks;
	}

	/// <summary>
	/// Should class heat capacity be rescaled?
	///
	/// Under Custom this is any run with tweaks on, which is what happened before profiles existed
	/// and is what keeps an existing config behaving identically. Under a named profile it is tied
	/// to the locks it feeds, so God Mode does not quietly resize a heat bar it is ignoring and
	/// Vanilla never touches it.
	/// </summary>
	internal static bool UsesClassHeatScaling(GameplayProfile profile, bool tweaks, bool heatLocks)
	{
		return ReleaseModeEnabled(profile, tweaks, heatLocks)
			|| (profile == GameplayProfile.Custom && TweaksEnabled(profile, tweaks));
	}
}
