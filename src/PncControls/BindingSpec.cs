using System;
using System.Collections.Generic;
using System.Linq;

namespace PncFramework.Controls;

/// <summary>Pure, stable binding representation. No Unity or persistence dependencies.</summary>
public sealed class BindingSpec
{
    public string Control { get; }
    public IReadOnlyList<string> Modifiers { get; }
    public string Canonical => string.Join("+", Modifiers.Concat(new[] { Control }));
    private BindingSpec(string control, IEnumerable<string> modifiers)
    {
        Control = control.ToLowerInvariant();
        Modifiers = modifiers.Select(x => x.ToLowerInvariant()).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToArray();
    }
    public static BindingSpec Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new FormatException("A binding cannot be empty.");
        var parts = value.Trim().Split('+').Select(x => x.Trim().ToLowerInvariant()).ToArray();
        var control = parts[parts.Length - 1];
        if (!control.Contains(":")) throw new FormatException("Control must name keyboard, mouse or gamepad.");
        var kind = control.Split(':')[0];
        if (kind != "keyboard" && kind != "mouse" && kind != "gamepad") throw new FormatException("Unsupported device.");
        if (control.EndsWith(":") || control.Count(c => c == ':') != 1) throw new FormatException("Invalid control path.");
        var mods = parts.Take(parts.Length - 1).ToArray();
        var allowed = new[] { "ctrl", "shift", "alt", "leftctrl", "rightctrl", "leftshift", "rightshift", "leftalt", "rightalt" };
        if (mods.Any(x => !allowed.Contains(x))) throw new FormatException("Unsupported modifier.");
        if (mods.Distinct().Count() != mods.Length) throw new FormatException("Duplicate modifier.");
        foreach (var group in new[] { "ctrl", "shift", "alt" })
            if (mods.Contains(group) && mods.Any(x => x != group && x.EndsWith(group))) throw new FormatException("Do not combine generic and side-specific modifiers.");
        if (kind != "keyboard" && mods.Length != 0) throw new FormatException("Cross-device modifier chords are not supported in this version.");
        return new BindingSpec(control, mods);
    }
    public string Display
    {
        get
        {
            var c = Control.Split(':'); var name = c[1];
            if (c[0] == "keyboard" && name.StartsWith("digit")) name = "Number row " + name.Substring(5);
            else if (c[0] == "keyboard" && name.StartsWith("numpad")) name = "Numpad " + name.Substring(6);
            else if (name.Length == 1 || name.StartsWith("f") && int.TryParse(name.Substring(1), out _)) name = name.ToUpperInvariant();
            return string.Join(" + ", Modifiers.Select(x => char.ToUpperInvariant(x[0]) + x.Substring(1)).Concat(new[] { name }));
        }
    }
    public bool SameChord(BindingSpec other) => Canonical == other.Canonical;
    // Native unmodified actions may also fire when a modifier is held.
    public bool OverlapsNative(BindingSpec other) => Control == other.Control;
    public bool OverlapsMod(BindingSpec other)
    {
        if (Control != other.Control) return false;
        foreach (var group in new[] { "ctrl", "shift", "alt" })
        {
            if ((ModifierStates(Modifiers, group) & ModifierStates(other.Modifiers, group)) == 0) return false;
        }
        return true;
    }
    private static int ModifierStates(IReadOnlyList<string> mods, string group)
    {
        if (mods.Contains(group)) return 14; // left, right or both
        bool left = mods.Contains("left" + group), right = mods.Contains("right" + group);
        if (left && right) return 8;
        if (left) return 10;
        if (right) return 12;
        return 1; // neither side held
    }
    public static BindingSpec FromInputPath(string path)
    {
        if (string.IsNullOrEmpty(path) || !path.StartsWith("<")) return null;
        var end = path.IndexOf(">/", StringComparison.Ordinal); if (end < 0) return null;
        var device = path.Substring(1, end - 1).ToLowerInvariant(); var key = path.Substring(end + 2);
        if (device == "keyboard" && key.Length == 1 && char.IsDigit(key[0])) key = "digit" + key;
        try { return Parse(device + ":" + key); } catch (FormatException) { return null; }
    }
}

[Flags] public enum ActionContext { Gameplay = 1, Dashboard = 2, Freecam = 4, Global = Gameplay | Dashboard | Freecam }

public sealed class ActionEntry
{
    public string Id { get; }
    public string Label { get; }
    public string Description { get; }
    public string Module { get; }
    public ActionContext Context { get; }
    public IReadOnlyList<BindingSpec> Defaults { get; }
    public IReadOnlyList<BindingSpec> Bindings { get; internal set; }
    public string MigrationNote { get; internal set; }
    public ActionEntry(string id, string label, string description, string module, ActionContext context, IEnumerable<BindingSpec> defaults)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Stable action ID is required.");
        Id = id; Label = label; Description = description; Module = module; Context = context;
        Defaults = defaults.ToArray(); Bindings = Defaults;
    }
}

public sealed class ActionCatalog
{
    private readonly Dictionary<string, ActionEntry> entries = new Dictionary<string, ActionEntry>(StringComparer.Ordinal);
    public IReadOnlyCollection<ActionEntry> Entries => entries.Values;
    public Dictionary<string, BindingSpec> NativeBindings { get; } = new Dictionary<string, BindingSpec>();
    public void Register(ActionEntry entry)
    {
        if (entries.ContainsKey(entry.Id)) throw new ArgumentException("Action already registered: " + entry.Id);
        entries.Add(entry.Id, entry);
    }
    public ActionEntry Get(string id) => entries[id];
    public IReadOnlyList<string> Conflicts(ActionEntry entry, IEnumerable<BindingSpec> proposed)
    {
        var bindings = proposed.ToArray(); var problems = new List<string>();
        if (bindings.Select(x => x.Canonical).Distinct().Count() != bindings.Length) problems.Add("Duplicate binding for this action.");
        foreach (var other in entries.Values)
        {
            if (entry.Id == other.Id || (entry.Context & other.Context) == 0) continue;
            if (bindings.Any(a => other.Bindings.Any(a.OverlapsMod))) problems.Add("Mod action: " + other.Label);
        }
        if ((entry.Context & ActionContext.Gameplay) != 0)
            foreach (var native in NativeBindings)
                if (bindings.Any(x => x.OverlapsNative(native.Value))) problems.Add("Game action: " + native.Key);
        return problems;
    }
}
