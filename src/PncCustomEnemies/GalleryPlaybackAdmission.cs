using System;
using System.Collections.Generic;

namespace PncCustomEnemies;

// Ownership survives provider removal. A disabled custom entry must never become
// an unrecognized native entry and fall through to name-based playback.
internal sealed class GalleryPlaybackAdmission
{
    private readonly Dictionary<object, string> owners = new Dictionary<object, string>();
    internal void Remember(object entry, string owner)
    {
        if (entry == null || string.IsNullOrWhiteSpace(owner))
            throw new ArgumentException("Gallery entry and owner are required");
        if (owners.TryGetValue(entry, out var existing) && !string.Equals(existing, owner, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Gallery entry already belongs to another package");
        owners[entry] = owner;
    }
    internal bool Allows(object entry, Func<string, bool> active)
        => entry != null && (!owners.TryGetValue(entry, out var owner) || active(owner));
}
