using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using PixelCrushers.GridController;
using UnityEngine;

namespace PncEdi;

// The creator recipe is fixed; opt-in Custom features remain separately configurable.
[HarmonyPatch]
internal static class CommunityCombatRuntime
{
    static ConfigEntry<bool> enabled;
    static ConfigEntry<float> move, attack, damage, hpBudget, window;
    static ConfigEntry<int> minimum;
    static readonly BurstDamageBudget Budget = new BurstDamageBudget();
    static readonly Dictionary<Animator, float> Animators = new Dictionary<Animator, float>();
    static readonly Dictionary<int, UnityEngine.Object> Targets = new Dictionary<int, UnityEngine.Object>();
    static bool lastActive;
    static float lastPrune;
    static bool Active => OverkeksMode.Active || GameplayProfiles.Current == GameplayProfile.Custom && Plugin.GameplayTweaksEnabled && (enabled?.Value ?? false);
    static CombatSettings Settings => OverkeksMode.Active ? new CombatSettings() : new CombatSettings(move.Value, attack.Value, damage.Value, minimum.Value, hpBudget.Value, window.Value);

    internal static void Initialize(ConfigFile config) {
        const string section = "Custom - Overkeks player combat (staged)";
        enabled = config.Bind(section, "Enabled", false, "Opt-in player combat slice for Custom only. Not the complete Overkeks profile.");
        ConfigEntry<float> Bind(string key, float value, float min, float max, string text) =>
            config.Bind(section, key, value, new ConfigDescription(text, new AcceptableValueRange<float>(min,max)));
        move = Bind("Movement",1.45f,.1f,10,"Original player movement multiplier; original <=1.001 gate retained.");
        attack = Bind("Attack",1.55f,.1f,10,"Original attack speed multiplier.");
        damage = Bind("Damage",1.35f,.1f,10,"Outgoing damage multiplier, before shared volley cap.");
        minimum = config.Bind(section,"MinimumHits",2,new ConfigDescription("Minimum full-health volleys; 1 disables cap.",new AcceptableValueRange<int>(1,1000)));
        hpBudget = Bind("HpPerHitBudget",40,0,1000000,"Additional HP-scaled required hits; below 1 disables scaling.");
        window = Bind("BurstSeconds",.18f,.05f,2,"Hits inside this window share one enemy-specific damage budget.");
        void ResetOn<T>(ConfigEntry<T> entry) => entry.SettingChanged += (_,__) => {if(!OverkeksMode.UsesRecipe)Reset();};
        ResetOn(enabled); ResetOn(move); ResetOn(attack); ResetOn(damage);
        ResetOn(minimum); ResetOn(hpBudget); ResetOn(window);
    }

    internal static int ScaleOutgoingDamage(int damage) => Active ? Settings.ScaleDamage(damage) : damage;
    internal static int CapDamageForMinHits(int damage,int maximum,int id) {
        if(!Active) return damage;
        var s=Settings;
        return Budget.Apply(new CombatSettings(s.Movement,s.Attack,1,s.MinimumHits,s.HpBudget,s.BurstSeconds),id,maximum,damage,Time.time);
    }
    internal static void Tick() {
        bool active = Active;
        if (lastActive && !active) Reset();
        lastActive = active;
        if (Time.unscaledTime - lastPrune < 2) return;
        lastPrune = Time.unscaledTime;
        var removed = new List<int>(); foreach(var pair in Targets) if(pair.Value == null) removed.Add(pair.Key);
        foreach(int id in removed) { Targets.Remove(id); Budget.Forget(id); }
        var dead = new List<Animator>(); foreach(var pair in Animators) if(pair.Key == null) dead.Add(pair.Key);
        foreach(var animator in dead) Animators.Remove(animator);
    }
    internal static void Reset() {
        foreach(var pair in Animators) if(pair.Key != null) pair.Key.speed = pair.Value;
        Animators.Clear(); Targets.Clear(); Budget.Clear(); lastActive = false;
    }
    static void Boost(WeaponSlot slot) {
        if (!Active || slot?.handAnimator == null) return;
        var animator = slot.handAnimator;
        if (!Animators.TryGetValue(animator,out float original)) { original=animator.speed; Animators.Add(animator,original); }
        animator.speed=Settings.AnimatorSpeed(original);
    }
    static void Restore(WeaponSlot slot) {
        if(slot?.handAnimator == null) return;
        if(Animators.TryGetValue(slot.handAnimator,out float original)) { slot.handAnimator.speed=original; Animators.Remove(slot.handAnimator); }
    }
    static void Outgoing(UnityEngine.Object target, int maximum, ref int damage) {
        if(!Active || target == null) return;
        int id=target.GetInstanceID();
        if(Targets.TryGetValue(id,out var old) && !ReferenceEquals(old,target)) Budget.Forget(id);
        Targets[id]=target;
        damage=Budget.Apply(Settings,id,maximum,damage,Time.time);
    }
    [HarmonyPatch(typeof(PlayerStats),"CalculateTotalMovementMultiplier"), HarmonyPostfix]
    static void Movement(ref float __result) { if(Active) __result=Settings.MovementResult(__result); }
    [HarmonyPatch(typeof(WeaponData),"GetAttackCooldown"), HarmonyPostfix]
    static void Cooldown(ref float __result) { if(Active) __result=Settings.Cooldown(__result); }
    [HarmonyPatch(typeof(WeaponSlot),"StartAttack"), HarmonyPostfix]
    static void StartAttack(WeaponSlot __instance) => Boost(__instance);
    [HarmonyPatch(typeof(WeaponSlot),"StartSpecialAttack"), HarmonyPostfix]
    static void StartSpecial(WeaponSlot __instance) => Boost(__instance);
    [HarmonyPatch(typeof(WeaponSlot),"EndAttack"), HarmonyPostfix]
    static void EndAttack(WeaponSlot __instance) => Restore(__instance);
    [HarmonyPatch(typeof(WeaponSlot),"ForceStopAttack"), HarmonyPostfix]
    static void StopAttack(WeaponSlot __instance) => Restore(__instance);
    [HarmonyPatch(typeof(EnemyAI),"TakeDamage"), HarmonyPrefix]
    static void Enemy(EnemyAI __instance,ref int damage) => Outgoing(__instance,__instance.maxHealth,ref damage);
    [HarmonyPatch(typeof(SpinningEnemyAI),"TakeDamage"), HarmonyPrefix]
    static void Spinning(SpinningEnemyAI __instance,ref int damage) => Outgoing(__instance,__instance.maxHealth,ref damage);
    [HarmonyPatch(typeof(ProjectileEnemyAI),"TakeDamage"), HarmonyPrefix]
    static void Projectile(ProjectileEnemyAI __instance,ref int damage) => Outgoing(__instance,__instance.maxHealth,ref damage);
    [HarmonyPatch(typeof(BrawlerEnemyAI),"TakeDamage"), HarmonyPrefix]
    static void Brawler(BrawlerEnemyAI __instance,ref int damage) => Outgoing(__instance,__instance.maxHealth,ref damage);
    [HarmonyPatch(typeof(ChargingEnemyAI),"TakeDamage"), HarmonyPrefix]
    static void Charging(ChargingEnemyAI __instance,ref int damage) => Outgoing(__instance,__instance.maxHealth,ref damage);
}
