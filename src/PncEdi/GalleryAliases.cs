using System;
using System.Collections.Generic;
using BepInEx.Logging;

namespace PncEdi;

public static class GalleryAliases
{
	private static readonly Dictionary<string, string> Shared = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
	private static readonly Dictionary<string, string> InGameOnly = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

	// Aliases registered from code rather than read from the config - custom-enemy packages, which
	// declare their own animation-state names in their manifest. Kept apart from the two tables
	// because `Reload` clears those, and a package is loaded once at startup: folding its entries
	// straight in would mean any later reload silently dropped every custom enemy's scenes.
	private static readonly List<KeyValuePair<string, string>> RegisteredShared = new List<KeyValuePair<string, string>>();
	private static readonly List<KeyValuePair<string, string>> RegisteredInGame = new List<KeyValuePair<string, string>>();
	private static bool _loaded;

	public static void Reload()
	{
		Shared.Clear();
		InGameOnly.Clear();
		// Base table first, user overrides on top - so an override wins and an update lands.
		ParseInto(GalleryTable.Shared, Shared);
		ParseInto(Plugin.CfgGalleryAliases?.Value, Shared);
		ParseInto(GalleryTable.InGame, InGameOnly);
		ParseInto(Plugin.CfgInGameAliases?.Value, InGameOnly);
		// Last, so a user's config entry still wins over a package's own default.
		foreach (KeyValuePair<string, string> pair in RegisteredShared)
		{
			if (!Shared.ContainsKey(pair.Key))
			{
				Shared[pair.Key] = pair.Value;
			}
		}
		foreach (KeyValuePair<string, string> pair in RegisteredInGame)
		{
			if (!InGameOnly.ContainsKey(pair.Key))
			{
				InGameOnly[pair.Key] = pair.Value;
			}
		}
		_loaded = true;
		ValidateTargets(Shared, "GalleryAliases");
		ValidateTargets(InGameOnly, "InGameAliases");
		Plugin.Log?.LogInfo($"[GalleryAliases] loaded {Shared.Count} shared + {InGameOnly.Count} in-game-only mappings");
	}

	private static void ParseInto(string raw, Dictionary<string, string> table)
	{
		// Newlines count as separators here: this one is hand-edited in the config file and
		// wraps across lines.
		foreach (KeyValuePair<string, string> pair in ConfigMap.Pairs(raw, ConfigMap.SemicolonOrNewline))
		{
			table[pair.Key] = pair.Value;
		}
	}

	private static void ValidateTargets(Dictionary<string, string> table, string label)
	{
		foreach (KeyValuePair<string, string> item in table)
		{
			string target = GalleryRegistry.StripQuery(item.Value);
			if (!(target == "-") && !target.Equals("__SKIP__", StringComparison.OrdinalIgnoreCase) && !GalleryRegistry.IsKnown(target))
			{
				Plugin.LogMissingDefinition(item.Key, target);
				ManualLogSource log = Plugin.Log;
				if (log != null)
				{
					log.LogWarning((object)("[GalleryAliases] " + label + " target '" + target + "' (from '" + item.Key + "') is in no gallery row; definitions: " + GalleryRegistry.Source));
				}
			}
		}
	}

	/// <summary>
	/// Add an alias from code, as a custom-enemy package does for its own animation states.
	///
	/// It survives a later <see cref="Reload"/> - see RegisteredShared - and it loses to a config
	/// entry with the same key, so a user can still redirect a package's scene without editing the
	/// package.
	/// </summary>
	internal static void Register(string source, string target, bool inGameOnly = false)
	{
		if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
		{
			return;
		}
		if (!_loaded)
		{
			Reload();
		}
		string key = source.Trim();
		string value = target.Trim();
		(inGameOnly ? RegisteredInGame : RegisteredShared).Add(new KeyValuePair<string, string>(key, value));
		Dictionary<string, string> table = inGameOnly ? InGameOnly : Shared;
		if (!table.ContainsKey(key))
		{
			table[key] = value;
		}
	}

	public static string Resolve(string gallery, bool inGame)
	{
		return Resolve(gallery, inGame, quiet: false);
	}

	// quiet: PeekGalleryMap probes this speculatively and recovers via its own heuristic,
	// so a miss there is not a gap worth reporting.
	public static string Resolve(string gallery, bool inGame, bool quiet)
	{
		if (string.IsNullOrEmpty(gallery))
		{
			return gallery;
		}
		if (!_loaded)
		{
			Reload();
		}
		if (inGame && InGameOnly.TryGetValue(gallery, out var inGameTarget))
		{
			return inGameTarget;
		}
		if (!Shared.TryGetValue(gallery, out var shared))
		{
			if (GalleryRegistry.IsKnown(gallery))
			{
				return gallery;
			}
			// Nothing matched. This used to fall through to a per-enemy substring matcher that
			// returned *something* for any string containing an enemy name - which is how peek
			// scenes ended up on grab galleries, and why gaps in the table were invisible for
			// months. An unmapped slug is now returned unchanged: SendPlay's IsKnown gate drops
			// it, logs it to PncEdi-missing-definitions.log, and the running script keeps going.
			if (!quiet)
			{
				NoteUnmapped(gallery);
			}
			return gallery;
		}
		return shared;
	}

	private static readonly HashSet<string> LoggedUnmapped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	private static void NoteUnmapped(string gallery)
	{
		if (LoggedUnmapped.Add(gallery))
		{
			Plugin.DBG("ALIAS-GAP", "no mapping for '" + gallery + "' - add it to GalleryAliases");
		}
	}




}
