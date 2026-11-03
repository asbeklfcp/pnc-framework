using System;
using System.Collections.Generic;
using System.Linq;
using PncFramework.Controls;
using UnityEngine;
using UnityEngine.InputSystem;

namespace PncModManager;

public sealed partial class Plugin
{
    private bool _showControls = true;
    private string _controlsSearch = "";
    private Vector2 _controlsScroll;
    private int _controlSelected;
    private float _nextControlDiscovery;
    private readonly HashSet<InputAction> _suspendedActions = new HashSet<InputAction>();
    private bool _confirmBindingReset;

    private List<ActionEntry> VisibleControlActions() => ControlService.Catalog.Entries
        .Where(x => string.IsNullOrWhiteSpace(_controlsSearch) || (x.Label + " " + x.Module + " " + x.Description).IndexOf(_controlsSearch, StringComparison.OrdinalIgnoreCase) >= 0)
        .OrderBy(x => x.Module).ThenBy(x => x.Label).ToList();

    private bool UpdateControls()
    {
        bool wasCapturing = ControlService.IsCapturing;
        ControlService.TickCapture();
        if (Time.unscaledTime >= _nextControlDiscovery)
        {
            _nextControlDiscovery = Time.unscaledTime + 1f;
            RefreshControlDiscovery();
        }
        if (_visible) SuspendNativeActions();
        if (wasCapturing) return true;
        var pad = Gamepad.current;
        if (_visible && pad != null && _showControls)
        {
            var entries = VisibleControlActions();
            if (pad.dpad.down.wasPressedThisFrame) _controlSelected++;
            if (pad.dpad.up.wasPressedThisFrame) _controlSelected--;
            _controlSelected = Mathf.Clamp(_controlSelected, 0, Mathf.Max(0, entries.Count - 1));
            if (pad.dpad.down.wasPressedThisFrame || pad.dpad.up.wasPressedThisFrame) _controlsScroll.y = _controlSelected * 115f;
            if (pad.buttonSouth.wasPressedThisFrame && entries.Count > 0) ControlService.BeginCapture(entries[_controlSelected], 0);
            if (pad.buttonEast.wasPressedThisFrame) SetVisible(false);
        }
        return false;
    }
    private void RefreshControlDiscovery()
    {
        var loader = BepInEx.Unity.Mono.Bootstrap.UnityChainloader.Instance;
        if (loader != null)
            foreach (var p in loader.Plugins.Values)
                if (p.Instance is BepInEx.Unity.Mono.BaseUnityPlugin instance) ControlService.RegisterConfig(instance.Config);
        // Discover the actual action asset, including disabled maps and effective user overrides.
        var native = new HashSet<InputAction>();
        if (InputManager.I != null)
            foreach (var prop in typeof(InputManager).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (prop.PropertyType != typeof(InputAction)) continue;
                try { if (prop.GetValue(InputManager.I) is InputAction a) native.Add(a); } catch (Exception) { }
            }
        foreach (var a in InputSystem.ListEnabledActions()) native.Add(a);
        if (native.Count == 0) return;
        ControlService.Catalog.NativeBindings.Clear();
        foreach (var a in native)
        {
            var maps = a.actionMap?.asset;
            var all = maps != null ? maps.ToArray() : new[] { a };
            foreach (var action in all)
                for (int i = 0; i < action.bindings.Count; i++)
                {
                    var spec = BindingSpec.FromInputPath(action.bindings[i].effectivePath);
                    if (spec != null) ControlService.Catalog.NativeBindings[(action.actionMap?.name ?? "") + "/" + action.name + " #" + i] = spec;
                }
        }
    }
    private void SuspendNativeActions()
    {
        foreach (var action in InputSystem.ListEnabledActions())
        {
            _suspendedActions.Add(action);
            action.Disable();
        }
    }
    private void OnControlsVisibility(bool visible)
    {
        ControlService.BlockGameplay = visible;
        if (visible) { RefreshControlDiscovery(); SuspendNativeActions(); }
        else
        {
            if (ControlService.IsCapturing) ControlService.CancelCapture();
            foreach (var action in _suspendedActions)
            { try { action.Enable(); } catch (Exception ex) { Logger.LogWarning("Could not restore native input: " + ex.Message); } }
            _suspendedActions.Clear();
        }
    }
    private void DrawControls()
    {
        GUILayout.BeginVertical(GUI.skin.box, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
        GUILayout.Label("Controls", _titleStyle);
        GUILayout.Label("Click a binding, then press a key, mouse button or controller button. Number row and Numpad are separate. F13 must be detected before use.", _descriptionStyle);
        GUILayout.BeginHorizontal();
        GUILayout.Label("Search", GUILayout.Width(55)); _controlsSearch = GUILayout.TextField(_controlsSearch);
        if (GUILayout.Button("Restore defaults", GUILayout.Width(135))) _confirmBindingReset = true;
        GUILayout.EndHorizontal();
        if (_confirmBindingReset)
        {
            GUILayout.BeginHorizontal(); GUILayout.Label("Replace all mod bindings with Evo80 defaults?", _descriptionStyle);
            if (GUILayout.Button("Restore", GUILayout.Width(80))) { ControlService.ResetAll(); _confirmBindingReset = false; }
            if (GUILayout.Button("Cancel", GUILayout.Width(80))) _confirmBindingReset = false;
            GUILayout.EndHorizontal();
        }
        if (ControlService.IsCapturing)
        {
            GUILayout.BeginHorizontal(GUI.skin.box); GUILayout.Label(ControlService.Status, _titleStyle);
            if (GUILayout.Button("Cancel capture", GUILayout.Width(130))) ControlService.CancelCapture();
            GUILayout.EndHorizontal();
        }
        GUILayout.Label("Last detected: " + ControlService.LastDetected + "  |  Controller: " + (Gamepad.current?.displayName ?? "not connected"), _descriptionStyle);
        ControlService.CaptureEscape = GUILayout.Toggle(ControlService.CaptureEscape, "Allow Escape as a captured binding (use Cancel capture to abort)");
        _controlsScroll = GUILayout.BeginScrollView(_controlsScroll);
        var entries = VisibleControlActions();
        for (int index = 0; index < entries.Count; index++)
        {
            var action = entries[index];
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(action.Label, index == _controlSelected ? _selectedButtonStyle : GUI.skin.button, GUILayout.Width(230))) _controlSelected = index;
            GUI.enabled = !ControlService.IsCapturing;
            for (int b = 0; b < action.Bindings.Count; b++)
            {
                if (GUILayout.Button(NativeTypeface.Clean(action.Bindings[b].Display), GUILayout.MinWidth(85))) ControlService.BeginCapture(action, b);
                if (GUILayout.Button("X", GUILayout.Width(26))) ControlService.Apply(action, action.Bindings.Where((x, n) => n != b), false);
            }
            if (GUILayout.Button(action.Bindings.Count == 0 ? "Assign" : "+ alternate", GUILayout.Width(100))) ControlService.BeginCapture(action, action.Bindings.Count);
            if (GUILayout.Button("Reset", GUILayout.Width(60))) ControlService.Apply(action, action.Defaults, false);
            GUI.enabled = true; GUILayout.EndHorizontal();
            GUILayout.Label(action.Module + "  -  " + action.Context + "  -  " + action.Description, _descriptionStyle);
            if (!string.IsNullOrEmpty(action.MigrationNote)) GUILayout.Label(action.MigrationNote, _descriptionStyle);
            var conflicts = ControlService.Catalog.Conflicts(action, action.Bindings);
            if (conflicts.Count > 0) GUILayout.Label("Inactive - " + string.Join("; ", conflicts), _errorStyle);
            if (action.Bindings.Any(x => !ControlService.Available(x))) GUILayout.Label("One or more controls have no matching connected device.", _descriptionStyle);
            GUILayout.EndVertical();
        }
        GUILayout.Space(8); GUILayout.Label("Native game controls (effective bindings)", _sectionStyle);
        GUILayout.Label("Native controls remain owned by the game. Use its Controls menu to rebind them; this view protects them from mod conflicts.", _descriptionStyle);
        foreach (var pair in ControlService.Catalog.NativeBindings.OrderBy(x => x.Key)) GUILayout.Label(pair.Key + "  -  " + NativeTypeface.Clean(pair.Value.Display), _descriptionStyle);
        GUILayout.EndScrollView();
        GUILayout.Label(ControlService.Status, _descriptionStyle);
        GUILayout.Label("Controller: D-pad selects action / South rebinds primary / East closes. Mouse can edit alternate slots and resets.", _descriptionStyle);
        GUILayout.EndVertical();
    }
}


