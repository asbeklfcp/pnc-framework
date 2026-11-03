using System;
using System.Reflection;
using BepInEx;
using UnityEngine;

namespace PncEdi;

// Game 0.3.1 switched Player Settings' active input handling to the Input System package alone.
// Two separate things break because of it, and the second one hides behind the first.
//
// 1. Every read of the legacy `UnityEngine.Input` class now throws:
//
//      InvalidOperationException: You are trying to read Input using the UnityEngine.Input class,
//      but you have switched active Input handling to Input System package in Player Settings.
//
//    That is a throw, not a warning. `Plugin.Update` ran it three calls upstream of the escape
//    gate, so five subsystems died every frame (§72). Nothing here calls `UnityEngine.Input`.
//
// 2. BepInEx's own abstraction was poisoned at startup. `UnityInput.Current` resolves the backend
//    **once**, on first access, and caches it forever:
//
//      try { LegacyInputSystem }            // throws under Input System - expected
//      catch { NewInputSystem }             // reads UnityEngine.InputSystem.Keyboard.current
//      catch (Exception) { NullInputSystem } // every key reads false, permanently
//
//    `NewInputSystem`'s constructor resolves a control for every KeyCode immediately, and at
//    plugin-load time `Keyboard.current` is still null, so it NREs and BepInEx settles on
//    `NullInputSystem`. That is what the fixed build did on the first run: no legacy throw left,
//    `[INPUT] backend NullInputSystem`, and still not one key working - including the *configured*
//    shortcuts, because `KeyboardShortcut.IsDown()` goes through the same cached backend.
//
//    The device list is populated a moment later. So the repair is to re-run the probe once the
//    keyboard exists and write the working backend back into `UnityInput.current`. Doing it there
//    rather than keeping a private backend of our own is deliberate: every `KeyboardShortcut` in
//    the plugin reads that field, and it is the only place a fix reaches all of them.
internal static class SafeInput
{
	private static FieldInfo _backendField;
	private static Type _newInputSystemType;
	private static PropertyInfo _keyboardCurrent;
	private static bool _reflectionResolved;
	private static bool _repaired;
	private static bool _repairFailureLogged;

	// Every key this mod reads goes through these three, which is why the mod-manager stand-down
	// sits here rather than in Hotkeys.IsDown: FreeCam and the heat-lock keys read raw KeyCodes
	// without going near a configured shortcut, and typing a number into a settings field must
	// not also fly the camera. See ModManagerBridge for why the check is late-bound.
	internal static bool GetKey(KeyCode key)
	{
		return !ModManagerBridge.WindowOpen && UnityInput.Current.GetKey(key);
	}

	internal static bool GetKeyDown(KeyCode key)
	{
		return !ModManagerBridge.WindowOpen && UnityInput.Current.GetKeyDown(key);
	}

	internal static bool GetKeyUp(KeyCode key)
	{
		return !ModManagerBridge.WindowOpen && UnityInput.Current.GetKeyUp(key);
	}

	internal static string BackendName
	{
		get
		{
			try
			{
				return UnityInput.Current.GetType().Name;
			}
			catch (Exception ex)
			{
				return "unavailable (" + ex.GetType().Name + ")";
			}
		}
	}

	// Called every frame from Plugin.Update, before anything reads a key. A no-op after the first
	// success, and after the backend turns out not to need repairing at all.
	internal static void EnsureBackend()
	{
		if (_repaired)
		{
			return;
		}
		try
		{
			if (!IsNullBackend(UnityInput.Current))
			{
				// Legacy or NewInputSystem resolved normally - nothing to do, ever.
				_repaired = true;
				return;
			}
			ResolveReflection();
			if (_backendField == null || _newInputSystemType == null || _keyboardCurrent == null)
			{
				MarkRepairFailed("reflection: BepInEx or Input System layout not as expected");
				return;
			}
			// The constructor NREs while this is null, which is the whole bug. Wait it out.
			if (_keyboardCurrent.GetValue(null) == null)
			{
				return;
			}
			object backend = Activator.CreateInstance(_newInputSystemType);
			_backendField.SetValue(null, backend);
			_repaired = true;
			Plugin.DBG("INPUT", "backend repaired -> " + BackendName + " (was NullInputSystem; BepInEx probed before the keyboard existed)");
		}
		catch (Exception ex)
		{
			MarkRepairFailed(ex.GetType().Name + ": " + ex.Message);
		}
	}

	private static void MarkRepairFailed(string detail)
	{
		if (!_repairFailureLogged)
		{
			_repairFailureLogged = true;
			Plugin.DBG("INPUT", "backend repair failed, hotkeys will not work: " + detail);
		}
	}

	private static void ResolveReflection()
	{
		if (_reflectionResolved)
		{
			return;
		}
		_reflectionResolved = true;
		Assembly bepInExUnity = typeof(UnityInput).Assembly;
		_backendField = typeof(UnityInput).GetField("current", BindingFlags.NonPublic | BindingFlags.Static);
		_newInputSystemType = bepInExUnity.GetType("BepInEx.NewInputSystem");
		Type keyboard = FindType("UnityEngine.InputSystem.Keyboard");
		if (keyboard != null)
		{
			_keyboardCurrent = keyboard.GetProperty("current", BindingFlags.Public | BindingFlags.Static);
		}
	}

	private static Type FindType(string fullName)
	{
		foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
		{
			try
			{
				Type type = assembly.GetType(fullName);
				if (type != null)
				{
					return type;
				}
			}
			catch (Exception)
			{
			}
		}
		return null;
	}

	// `BepInEx.NullInputSystem` is internal, so it cannot be named in an `is` test. Compare by
	// type name instead - it is the one backend that means "no key will ever read true".
	private static bool IsNullBackend(object backend)
	{
		return backend != null && backend.GetType().Name == "NullInputSystem";
	}
}
