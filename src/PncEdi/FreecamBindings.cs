using BepInEx.Configuration;
using BepInEx.Unity.Mono.Configuration;
using UnityEngine;

namespace PncEdi;

public static partial class FreeCam
{
    private static ConfigEntry<KeyboardShortcut> ForwardKey, BackwardKey, LeftKey, RightKey, UpKey, DownKey, FastKey;
    private static ConfigEntry<float> FlightSpeed, FastMultiplier;
    internal static void InitializeBindings(ConfigFile config)
    {
        ConfigEntry<KeyboardShortcut> Bind(string name, KeyCode key) => config.Bind("Freecam controls", name, new KeyboardShortcut(key), "Freecam-only " + name.ToLowerInvariant() + " action. Edit keyboard, mouse or controller bindings on the Controls page.");
        ForwardKey = Bind("Forward", KeyCode.W); BackwardKey = Bind("Backward", KeyCode.S);
        LeftKey = Bind("Left", KeyCode.A); RightKey = Bind("Right", KeyCode.D);
        UpKey = Bind("Up", KeyCode.Space); DownKey = Bind("Down", KeyCode.LeftControl); FastKey = Bind("Fast", KeyCode.LeftAlt);
        FlightSpeed = config.Bind("Freecam", "FlightSpeed", 10f, new ConfigDescription("Base freecam flight speed in world units per second.", new AcceptableValueRange<float>(0.1f, 100f)));
        FastMultiplier = config.Bind("Freecam", "FastMultiplier", 2.5f, new ConfigDescription("Speed multiplier while the Fast action is held.", new AcceptableValueRange<float>(1f, 10f)));
    }
}
