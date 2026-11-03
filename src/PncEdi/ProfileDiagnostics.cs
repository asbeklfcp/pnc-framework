using UnityEngine;

namespace PncEdi;

public partial class Plugin
{
    private float _nextProfileDiagnostic;

    private void TickProfileDiagnostics()
    {
        if (Time.realtimeSinceStartup < _nextProfileDiagnostic) return;
        _nextProfileDiagnostic = Time.realtimeSinceStartup + 10f;
        LogProfileState("sample");
    }

    internal static void LogProfileState(string reason)
    {
        var player = Object.FindAnyObjectByType<PlayerStats>();
        string stats = player == null ? "player=none" :
            $"health={player.CurrentHealth}/{player.MaxHealth}; maxHeat={player.MaxHeat}; lockProgress={HeatLockSystem.CurrentLockProgress}";
        DBG("PROFILE-STATE", $"reason={reason}; profile={GameplayProfiles.Current}; tweaks={GameplayProfiles.TweaksEnabled}; god={GameplayProfiles.GodModeEnabled}; locks={GameplayProfiles.ReleaseModeEnabled}; classScaling={GameplayProfiles.UsesClassHeatScaling}; {stats}");
    }
}
