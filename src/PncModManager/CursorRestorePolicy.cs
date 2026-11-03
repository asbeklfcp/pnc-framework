namespace PncModManager;

internal static class CursorRestorePolicy
{
    internal static bool UseGameplayCursor(bool captureAtOpen,bool captureNow,bool hasPlayer,
        bool paused,bool inventoryOpen,bool focused)
        => captureAtOpen && !captureNow && hasPlayer && !paused && !inventoryOpen && focused;
}
