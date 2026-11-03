using System;
using System.Collections.Generic;

namespace PncEdi;

internal sealed class CombatSettings
{
    internal readonly float Movement, Attack, Damage, HpBudget, BurstSeconds;
    internal readonly int MinimumHits;
    internal CombatSettings(float movement = 1.45f, float attack = 1.55f, float damage = 1.35f,
        int minimumHits = 2, float hpBudget = 40f, float burstSeconds = .18f)
    {
        static void Range(float value, float min, float max) {
            if (float.IsNaN(value) || float.IsInfinity(value) || value < min || value > max)
                throw new ArgumentOutOfRangeException(nameof(value));
        }
        Range(movement, .1f, 10); Range(attack, .1f, 10); Range(damage, .1f, 10);
        Range(hpBudget, 0, 1000000); Range(burstSeconds, .05f, 2);
        if (minimumHits < 1 || minimumHits > 1000) throw new ArgumentOutOfRangeException(nameof(minimumHits));
        Movement = movement; Attack = attack; Damage = damage; MinimumHits = minimumHits;
        HpBudget = hpBudget; BurstSeconds = burstSeconds;
    }
    // Fidelity: original move/attack gates ignore values <=1.001, including slowdowns.
    internal float MovementResult(float native) => Movement > 1.001f ? native * Movement : native;
    internal float Cooldown(float native) => Attack > 1.001f && native > 0 ? Math.Max(.05f, native / Attack) : native;
    internal float AnimatorSpeed(float native) => Attack > 1.001f ? native * Attack : native;
    internal int ScaleDamage(int native) => native <= 0 ? native :
        (int)Math.Min(int.MaxValue, Math.Max(1d, Math.Ceiling(native * Damage)));
    internal int VolleyCap(int maximumHealth) {
        if (MinimumHits < 2 || maximumHealth <= 0) return int.MaxValue;
        double hits = HpBudget < 1 ? MinimumHits : Math.Max(MinimumHits, Math.Ceiling(maximumHealth / HpBudget));
        return Math.Max(1, (int)Math.Ceiling(maximumHealth / (float)hits));
    }
}

internal sealed class BurstDamageBudget
{
    struct Spend { internal double Start; internal int Amount; }
    readonly Dictionary<int, Spend> spent = new Dictionary<int, Spend>();
    internal int Count => spent.Count;
    internal int Apply(CombatSettings settings, int target, int maximumHealth, int damage, double now) {
        if (double.IsNaN(now) || double.IsInfinity(now)) throw new ArgumentOutOfRangeException(nameof(now));
        int scaled = settings.ScaleDamage(damage);
        if (scaled <= 0 || settings.MinimumHits < 2 || maximumHealth <= 0) return scaled;
        int cap = settings.VolleyCap(maximumHealth);
        if (!spent.TryGetValue(target, out var row) || now < row.Start || now - row.Start > settings.BurstSeconds)
            row = new Spend { Start = now };
        int allowed = Math.Min(scaled, Math.Max(0, cap - row.Amount));
        row.Amount += allowed; spent[target] = row;
        return allowed;
    }
    internal void Forget(int target) => spent.Remove(target);
    internal void Clear() => spent.Clear();
}
