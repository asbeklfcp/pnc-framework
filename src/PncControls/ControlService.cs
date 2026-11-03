using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.Mono.Configuration;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace PncFramework.Controls;

/// <summary>Shared input owner. Gameplay modes do not own or replace binding persistence.</summary>
public static class ControlService
{
    public static readonly ActionCatalog Catalog = new ActionCatalog();
    private static readonly Dictionary<ConfigEntry<KeyboardShortcut>, ActionEntry> legacy = new Dictionary<ConfigEntry<KeyboardShortcut>, ActionEntry>();
    private static readonly Dictionary<string, ConfigEntry<string>> storage = new Dictionary<string, ConfigEntry<string>>();
    private static ConfigFile file;
    public static bool BlockGameplay { get; set; }
    public static ActionContext ActiveContext { get; set; } = ActionContext.Gameplay;
    public static bool CaptureEscape { get; set; }
    public static string Status { get; private set; } = "Select a binding to capture keyboard, mouse or controller input.";
    public static string LastDetected { get; private set; } = "No input observed in this session";
    public static ActionEntry Capturing { get; private set; }
    private static int slot, captureFrame, lastTick = -1;
    private static float deadline;
    private static bool armed;
    public static bool IsCapturing => Capturing != null;
    /// <summary>Entry point for new modules. IDs and defaults are shared by UI and runtime.</summary>
    public static void RegisterAction(ActionEntry action)
    {
        if (file == null) file = new ConfigFile(Path.Combine(Paths.ConfigPath, "com.pnc.framework.controls.cfg"), true);
        Catalog.Register(action);
        var saved = file.Bind("Bindings", action.Id.Replace('/', '.'), string.Join(";", action.Defaults.Select(x => x.Canonical)), action.Description);
        storage[action.Id] = saved;
        try { action.Bindings = ParseSlots(saved.Value); }
        catch (FormatException ex) { action.MigrationNote = "Invalid saved binding: " + ex.Message; }
    }
    public static void RegisterConfig(ConfigFile config)
    {
        foreach (var entry in config.Values.OfType<ConfigEntry<KeyboardShortcut>>()) Register(entry);
    }
    public static ActionEntry Register(ConfigEntry<KeyboardShortcut> entry)
    {
        if (legacy.TryGetValue(entry, out var existing)) return existing;
        if (file == null) file = new ConfigFile(Path.Combine(Paths.ConfigPath, "com.pnc.framework.controls.cfg"), true);
        string owner = Path.GetFileNameWithoutExtension(entry.ConfigFile.ConfigFilePath);
        string id = owner + "/" + entry.Definition.Section + "/" + entry.Definition.Key;
        bool manager = entry.Definition.Key == "ToggleShortcut" && owner == "com.pnc.modmanager";
        bool freecam = entry.Definition.Section == "Freecam controls";
        string recommendation = manager ? "keyboard:F11" : freecam ? FromLegacy(entry.Value)?.Canonical ?? "" : Recommended(entry.Definition.Key);
        var defaults = ParseSlots(recommendation);
        var action = new ActionEntry(id, Regex.Replace(entry.Definition.Key, "(?<=[a-z])([A-Z])", " $1"), entry.Description.Description, owner,
            freecam ? ActionContext.Freecam : manager || entry.Definition.Key == "ToggleFreecamKey" ? ActionContext.Global : ActionContext.Gameplay, defaults);
        Catalog.Register(action); legacy[entry] = action;
        string initial = string.Join(";", defaults.Select(x => x.Canonical));
        if (!entry.Value.Equals(entry.DefaultValue))
        {
            var imported = FromLegacy(entry.Value);
            if (imported != null && !imported.Control.Contains("numpad") && Catalog.Conflicts(action, new[] { imported }).Count == 0) initial = imported.Canonical;
            else action.MigrationNote = "Legacy binding needs review; keypad or conflicting control was not activated.";
        }
        var saved = file.Bind("Bindings", id.Replace('/', '.'), initial, action.Description);
        storage[id] = saved;
        try { action.Bindings = ParseSlots(saved.Value); }
        catch (FormatException ex) { action.Bindings = defaults; action.MigrationNote = "Invalid saved binding: " + ex.Message; }
        return action;
    }
    private static string Recommended(string key)
    {
        switch (key)
        {
            case "EndGrabKey": return "keyboard:F6";
            case "RemoveHeatKey": return "keyboard:F7";
            case "AddHeatKey": return "Shift+keyboard:F7";
            case "EndGrabDebugKey": return "Ctrl+keyboard:F12";
            case "ToggleFreecamKey": return "keyboard:F10";
            default: return ""; // Optional spawn/developer actions are deliberately unassigned.
        }
    }
    private static BindingSpec[] ParseSlots(string text) => string.IsNullOrWhiteSpace(text) ? Array.Empty<BindingSpec>() : text.Split(';').Select(BindingSpec.Parse).ToArray();
    private static string KeyName(KeyCode key)
    {
        string name = key.ToString();
        if (name.StartsWith("Alpha")) name = "Digit" + name.Substring(5);
        if (name.StartsWith("Keypad")) name = "Numpad" + name.Substring(6);
        return name.Replace("Control", "Ctrl");
    }
    public static BindingSpec FromLegacy(KeyboardShortcut shortcut)
    {
        if (shortcut.MainKey == KeyCode.None) return null;
        var mods = shortcut.Modifiers.Select(KeyName).ToArray();
        try { return BindingSpec.Parse(string.Join("+", mods.Concat(new[] { "keyboard:" + KeyName(shortcut.MainKey) }))); }
        catch (FormatException) { return null; }
    }
    public static string Display(ConfigEntry<KeyboardShortcut> entry) => string.Join(" / ", Register(entry).Bindings.Select(x => x.Display));
    public static bool IsDown(ConfigEntry<KeyboardShortcut> entry, bool dashboardAction = false)
    {
        if (entry == null || !Application.isFocused || IsCapturing || BlockGameplay && !dashboardAction) return false;
        return IsActionDown(Register(entry).Id);
    }
    public static bool IsActionDown(string actionId)
    {
        if (!Application.isFocused || IsCapturing) return false;
        var action = Catalog.Get(actionId);
        if ((action.Context & (BlockGameplay ? ActionContext.Dashboard : ActiveContext)) == 0) return false;
        if (Catalog.Conflicts(action, action.Bindings).Count > 0) return false;
        return action.Bindings.Any(x => Resolve(x)?.wasPressedThisFrame == true && ModifiersMatch(x));
    }
    public static bool IsHeld(ConfigEntry<KeyboardShortcut> entry)
    {
        if (entry == null || !Application.isFocused || IsCapturing || BlockGameplay) return false;
        var action = Register(entry);
        if ((action.Context & ActiveContext) == 0 || Catalog.Conflicts(action, action.Bindings).Count > 0) return false;
        return action.Bindings.Any(x => Resolve(x)?.isPressed == true && x.Modifiers.All(Held));
    }
    private static ButtonControl Resolve(BindingSpec binding)
    {
        var p = binding.Control.Split(':');
        try
        {
            if (p[0] == "keyboard" && Keyboard.current != null && Enum.TryParse<Key>(p[1], true, out var key)) return Keyboard.current[key];
            if (p[0] == "mouse" && Mouse.current != null) return Mouse.current[p[1]] as ButtonControl;
            if (p[0] == "gamepad" && Gamepad.current != null) return Gamepad.current[p[1]] as ButtonControl;
        }
        catch (Exception) { }
        return null;
    }
    public static bool Available(BindingSpec binding) => Resolve(binding) != null;
    private static bool Held(string modifier)
    {
        var k = Keyboard.current; if (k == null) return false;
        switch (modifier)
        {
            case "ctrl": return k.ctrlKey.isPressed;
            case "shift": return k.shiftKey.isPressed;
            case "alt": return k.altKey.isPressed;
            default: return Enum.TryParse<Key>(modifier, true, out var key) && k[key].isPressed;
        }
    }
    private static bool ModifiersMatch(BindingSpec binding)
    {
        foreach (var group in new[] { "ctrl", "shift", "alt" })
            if (Held(group) != (binding.Modifiers.Any(x => x.EndsWith(group)) || binding.Control.EndsWith("left" + group) || binding.Control.EndsWith("right" + group))) return false;
        return binding.Modifiers.All(Held);
    }
    public static bool Apply(ActionEntry action, IEnumerable<BindingSpec> proposed, bool requireConnected = true)
    {
        var next = proposed.ToArray(); var conflicts = Catalog.Conflicts(action, next);
        if (action.Id.EndsWith("/ToggleShortcut", StringComparison.Ordinal) && next.Length == 0) { Status = "Keep at least one dashboard shortcut."; return false; }
        if (conflicts.Count != 0) { Status = string.Join("; ", conflicts); return false; }
        if (requireConnected && next.Any(x => !Available(x))) { Status = "Control is not exposed by a connected input device."; return false; }
        var old = storage[action.Id].Value;
        bool oldAutoSave = file.SaveOnConfigSet;
        try
        {
            file.SaveOnConfigSet = false;
            storage[action.Id].Value = string.Join(";", next.Select(x => x.Canonical)); file.Save(); action.Bindings = next;
            Status = "Saved " + action.Label; return true;
        }
        catch (Exception ex) { storage[action.Id].Value = old; Status = "Could not save binding: " + ex.Message; return false; }
        finally { file.SaveOnConfigSet = oldAutoSave; }
    }
    public static bool ResetAll()
    {
        var previous = Catalog.Entries.ToDictionary(x => x.Id, x => x.Bindings);
        foreach (var action in Catalog.Entries) action.Bindings = action.Defaults;
        var problems = Catalog.Entries.SelectMany(x => Catalog.Conflicts(x, x.Bindings)).Distinct().ToArray();
        foreach (var action in Catalog.Entries) action.Bindings = previous[action.Id];
        if (problems.Length > 0) { Status = "Defaults conflict: " + string.Join("; ", problems); return false; }
        var saved = storage.ToDictionary(x => x.Key, x => x.Value.Value);
        bool oldAutoSave = file.SaveOnConfigSet;
        try
        {
            file.SaveOnConfigSet = false;
            foreach (var action in Catalog.Entries) storage[action.Id].Value = string.Join(";", action.Defaults.Select(x => x.Canonical));
            file.Save(); foreach (var action in Catalog.Entries) action.Bindings = action.Defaults;
            Status = "Restored all binding defaults."; return true;
        }
        catch (Exception ex) { foreach (var pair in saved) storage[pair.Key].Value = pair.Value; Status = "Reset failed: " + ex.Message; return false; }
        finally { file.SaveOnConfigSet = oldAutoSave; }
    }
    public static void BeginCapture(ActionEntry action, int bindingSlot)
    {
        Capturing = action; slot = bindingSlot; captureFrame = Time.frameCount; deadline = Time.unscaledTime + 20f; armed = false;
        Status = "Listening for " + action.Label + ". Escape cancels. Release the activating button first.";
    }
    public static void CancelCapture(string reason = "Capture cancelled.") { Capturing = null; Status = reason; }
    public static void TickCapture()
    {
        if (lastTick == Time.frameCount) return; lastTick = Time.frameCount;
        if (!Application.isFocused) { if (IsCapturing) CancelCapture("Capture cancelled on focus loss."); return; }
        var buttons = new List<Tuple<string, ButtonControl>>();
        if (Keyboard.current != null)
            foreach (var key in Keyboard.current.allKeys) buttons.Add(Tuple.Create("keyboard:" + key.keyCode, (ButtonControl)key));
        if (Mouse.current != null)
            foreach (var name in new[] { "leftButton", "rightButton", "middleButton", "backButton", "forwardButton" })
                if (Mouse.current[name] is ButtonControl b) buttons.Add(Tuple.Create("mouse:" + name, b));
        if (Gamepad.current != null)
            foreach (var name in new[] { "buttonSouth", "buttonNorth", "buttonEast", "buttonWest", "leftShoulder", "rightShoulder", "leftTrigger", "rightTrigger", "leftStickPress", "rightStickPress", "start", "select", "dpad/up", "dpad/down", "dpad/left", "dpad/right" })
            { try { if (Gamepad.current[name] is ButtonControl b) buttons.Add(Tuple.Create("gamepad:" + name, b)); } catch (Exception) { } }
        var pressed = buttons.FirstOrDefault(x => x.Item2.wasPressedThisFrame && !IsModifier(x.Item1));
        if (pressed != null) LastDetected = pressed.Item1;
        if (!IsCapturing) return;
        if (Time.unscaledTime > deadline) { CancelCapture("Capture timed out; old binding kept."); return; }
        if (!CaptureEscape && Keyboard.current?.escapeKey.wasPressedThisFrame == true) { CancelCapture(); return; }
        if (!armed)
        {
            if (Time.frameCount > captureFrame && buttons.All(x => IsModifier(x.Item1) || !x.Item2.isPressed)) armed = true;
            return;
        }
        if (pressed == null) pressed = buttons.FirstOrDefault(x => IsModifier(x.Item1) && x.Item2.wasReleasedThisFrame && !x.Item1.EndsWith("meta", StringComparison.OrdinalIgnoreCase));
        if (pressed == null) return;
        var mods = new List<string>();
        if (pressed.Item1.StartsWith("keyboard:")) foreach (var name in new[] { "ctrl", "shift", "alt" }) if (Held(name) && !pressed.Item1.EndsWith(name, StringComparison.OrdinalIgnoreCase)) mods.Add(name);
        var binding = BindingSpec.Parse(string.Join("+", mods.Concat(new[] { pressed.Item1 })));
        LastDetected = binding.Canonical;
        var next = Capturing.Bindings.ToList();
        if (slot < next.Count) next[slot] = binding; else next.Add(binding);
        var action = Capturing; Capturing = null; Apply(action, next);
    }
    private static bool IsModifier(string path) => new[] { "leftctrl", "rightctrl", "leftshift", "rightshift", "leftalt", "rightalt", "leftmeta", "rightmeta" }.Any(x => path.EndsWith(":" + x, StringComparison.OrdinalIgnoreCase));
}
