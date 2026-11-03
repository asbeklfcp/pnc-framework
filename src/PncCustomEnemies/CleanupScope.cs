using System;
using System.Collections.Generic;
using System.Linq;

namespace PncCustomEnemies;

// Pure transaction used by the Unity adapter. Closing the gate comes first;
// failure in one cleanup never prevents the remaining resources from releasing.
internal sealed class CleanupScope
{
    private sealed class Entry { internal string Key; internal int Order; internal Action Release; }
    private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>();
    private bool cleaning;
    internal bool Active { get; private set; }
    internal int Count => entries.Count;
    internal CleanupScope(bool active) { Active = active; }
    internal bool Track(string key, int order, Action release)
    {
        if (!Active) return false;
        if (!entries.ContainsKey(key)) entries.Add(key, new Entry { Key=key, Order=order, Release=release });
        return true;
    }
    internal void Forget(string key) => entries.Remove(key);
    internal void CloseAdmission() => Active = false;
    internal List<string> Disable()
    {
        Active = false;
        var errors = new List<string>();
        if (cleaning) return errors;
        cleaning = true;
        try
        {
            foreach (var entry in entries.Values.OrderBy(x=>x.Order).ThenBy(x=>x.Key, StringComparer.Ordinal).ToArray())
            {
                entries.Remove(entry.Key);
                try { entry.Release(); }
                catch (Exception ex)
                {
                    entries[entry.Key] = entry; // retain failed ownership for diagnosis/retry
                    errors.Add(entry.Key + ": " + ex.Message);
                }
            }
        }
        finally { cleaning = false; }
        return errors;
    }
}
