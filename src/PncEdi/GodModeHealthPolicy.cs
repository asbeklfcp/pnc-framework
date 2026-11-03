namespace PncEdi;

internal static class GodModeHealthPolicy
{
    // Named God Mode heals living players to their actual maximum; never inflates
    // maximum health, revives a dead player, or changes another profile's healing.
    internal static bool ShouldRefill(GameplayProfile profile, bool alive, int current, int maximum)
        => profile == GameplayProfile.GodMode && alive && current > 0 && maximum > 0 && current < maximum;
}
