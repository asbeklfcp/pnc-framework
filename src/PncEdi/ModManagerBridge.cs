using System;
using System.Reflection;

namespace PncEdi;

/// <summary>
/// Is the in-game mod manager's window open?
///
/// `PncModManager` is a second, separate BepInEx plugin - it configures whatever mods happen to be
/// loaded, so it deliberately does not belong to this one. That means this assembly cannot
/// reference it: BepInEx loads plugin DLLs independently and in no guaranteed order, so a hard
/// reference would make PncEdi fail to load on any install where the manager is absent or older.
///
/// So the link is one late-bound read of a public static property, resolved once and cached. If
/// the type is not there - manager not installed, or a version that predates the property - every
/// call returns false and nothing else changes. This is the standard shape of an optional
/// soft dependency between two plugins, and the reason `IsOpen` is a plain `bool` property rather
/// than anything richer: the contract has to survive both sides being versioned separately.
///
/// Why it exists: <see cref="SafeInput"/> reads keys straight from BepInEx's input backend, which
/// knows nothing about an IMGUI window having focus. Unity's `Update` keeps running while the
/// manager holds `Time.timeScale` at zero, so without this every keystroke typed into a settings
/// field also lands on whatever hotkey shares that letter - and F-keys aside, FreeCam's bare
/// WASD would fly the camera around while the user edits a number.
/// </summary>
internal static class ModManagerBridge
{
	private static PropertyInfo _isOpen;
	private static float _nextScan;
	private static bool _missingLogged;

	/// <summary>How long to wait before looking for the manager again, once a scan found nothing.</summary>
	private const float RescanSeconds = 2f;

	/// <summary>True only while the manager is installed *and* showing its window.</summary>
	internal static bool WindowOpen
	{
		get
		{
			try
			{
				Resolve();
				return _isOpen != null && (bool)_isOpen.GetValue(null);
			}
			catch (Exception)
			{
				// A manager that throws must not take the game's input with it.
				_isOpen = null;
				return false;
			}
		}
	}

	// Deliberately not "scan once and give up". BepInEx does not order plugin loads, so PncEdi can
	// easily be constructed before PncModManager's assembly is in the domain - latching a miss on
	// the first read would mean the bridge is dead for the whole session on exactly the installs
	// where it is wanted. Instead a miss just schedules another look.
	private static void Resolve()
	{
		if (_isOpen != null || UnityEngine.Time.unscaledTime < _nextScan)
		{
			return;
		}
		_nextScan = UnityEngine.Time.unscaledTime + RescanSeconds;
		// The manager loads as its own plugin, so by the time anything reads a key its assembly is
		// in the domain under its own name. Walking the domain rather than `Type.GetType` avoids
		// depending on the assembly's file name or its load context.
		foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
		{
			try
			{
				Type type = assembly.GetType("PncModManager.Plugin");
				if (type == null)
				{
					continue;
				}
				PropertyInfo property = type.GetProperty("IsOpen", BindingFlags.Public | BindingFlags.Static);
				if (property != null && property.PropertyType == typeof(bool))
				{
					_isOpen = property;
					Plugin.DBG("MODMGR", "found PncModManager.Plugin.IsOpen; hotkeys will stand down while its window is open");
					return;
				}
			}
			catch (Exception)
			{
			}
		}
		if (!_missingLogged)
		{
			_missingLogged = true;
			Plugin.DBG("MODMGR", "no PncModManager.Plugin.IsOpen found - hotkeys are always live");
		}
	}
}
