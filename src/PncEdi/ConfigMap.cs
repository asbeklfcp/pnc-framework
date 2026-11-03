using System.Collections.Generic;

namespace PncEdi;

/// <summary>
/// The `key=value;key=value` config format, parsed in one place.
///
/// Nine settings are written this way - `EnemyRemap`, `GalleryAliases`, `InGameAliases`,
/// `DioramaAmbientMap`, `PeekGalleryMap`, `PeekClipMap`, `ClassHeatMultipliers`,
/// `HeatPotionLockRemoval`, `GrabVariantSuffixes` - and before §117 six classes each carried
/// their own copy of the same fifteen-line loop. The copies did not differ in how they parsed;
/// they differed in what they did with the pair afterwards, which is the caller's business and
/// stays there. What was duplicated was the format, and a format with six implementations is a
/// format that can drift in five of them.
///
/// The contract, matching what every copy already did:
///
/// - entries are separated by `;` (and by newlines where the caller asks, because
///   `GalleryAliases` is edited by hand and wraps),
/// - an entry splits on its **first** `=`; later ones belong to the target,
/// - key and target are both trimmed, and an entry with either side empty is dropped,
/// - an entry with no `=` at all is dropped.
///
/// Casing, slugging and number parsing are deliberately not here. Each caller wants something
/// different (`EnemyRemap` lowercases both sides, `PeekClipMap` slugs the key,
/// `ClassHeatMultipliers` wants a float), and folding those in would be the abstraction this is
/// meant to avoid.
/// </summary>
internal static class ConfigMap
{
	private static readonly char[] Semicolon = { ';' };

	/// <summary>Entry separators for a setting a human edits over several lines.</summary>
	internal static readonly char[] SemicolonOrNewline = { ';', '\n', '\r' };

	/// <summary>Every well-formed `key=target` pair in <paramref name="raw"/>, in order.</summary>
	internal static IEnumerable<KeyValuePair<string, string>> Pairs(string raw, char[] separators = null)
	{
		if (string.IsNullOrEmpty(raw))
		{
			yield break;
		}
		foreach (string field in raw.Split(separators ?? Semicolon))
		{
			string entry = field.Trim();
			int eq = entry.IndexOf('=');
			if (eq <= 0)
			{
				continue;
			}
			string key = entry.Substring(0, eq).Trim();
			string target = entry.Substring(eq + 1).Trim();
			if (key.Length != 0 && target.Length != 0)
			{
				yield return new KeyValuePair<string, string>(key, target);
			}
		}
	}

	/// <summary>
	/// Order a table so the longest key is tried first.
	///
	/// Several of these settings are matched as *substrings* - an enemy called
	/// `Hood_Enemy NoTape` has to beat the `Hood_Enemy` entry, and `Blinded Beast Ride Gallery`
	/// has to beat `BlindedBeastRide`. "Longest matching key wins" is what the config
	/// descriptions promise the user, so it is worth having one implementation of it.
	/// </summary>
	internal static void SortLongestKeyFirst(List<KeyValuePair<string, string>> table)
	{
		table.Sort((a, b) => b.Key.Length.CompareTo(a.Key.Length));
	}
}
