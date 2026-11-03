using System;
using System.Collections.Generic;

namespace PncEdi;

internal static class DioramaGalleryMap
{
	private static readonly Dictionary<string, string> Map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

	// Built-in fallback for a config that has been blanked or had entries deleted. Parsed from
	// the setting's own shipped default rather than a parallel array, so the two cannot drift:
	// a hand-maintained copy silently kept D5=ambient_wendigo_hole for a day after the config
	// default was corrected. This also drops the old fixed 9-slot ceiling — whatever D-slots
	// the default string names are the ones that resolve.
	private static readonly Dictionary<string, string> Fallback = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
	private static bool _loaded;

	internal static void Reload()
	{
		Map.Clear();
		Fallback.Clear();
		ParseInto(Plugin.CfgDioramaGalleryMap?.DefaultValue as string, Fallback);
		ParseInto(Plugin.CfgDioramaGalleryMap?.Value, Map);
		_loaded = true;
	}

	internal static string Resolve(string galleryId)
	{
		if (string.IsNullOrEmpty(galleryId))
		{
			return null;
		}
		if (!_loaded)
		{
			Reload();
		}
		if (Map.TryGetValue(galleryId, out var value))
		{
			return value;
		}
		if (Fallback.TryGetValue(galleryId, out value))
		{
			return value;
		}
		string lower = galleryId.ToLowerInvariant();
		if (lower.Contains("gargoyle"))
		{
			return "ambient_gargoyle_ledge_fuck";
		}
		if (lower.Contains("gooper") && lower.Contains("bed"))
		{
			return "ambient_gooper_bed_blowjob";
		}
		if (lower.Contains("imp") && (lower.Contains("gang") || lower.Contains("gangbang")))
		{
			return "ambient_imp_gangbang";
		}
		if (lower.Contains("mimic") && lower.Contains("fuck"))
		{
			return "ambient_mimic_wall_fuck";
		}
		if (lower.Contains("gooper") && (lower.Contains("bed") || lower.Contains("blow")))
		{
			return "ambient_gooper_bed_blowjob";
		}
		if (lower.Contains("mimic") && lower.Contains("wall"))
		{
			return "ambient_mimic_wall_fuck";
		}
		if (lower.Contains("nun") && lower.Contains("chair"))
		{
			return "ambient_nun_chair_fuck";
		}
		if (lower.Contains("nun") && (lower.Contains("wall") || lower.Contains("bj") || lower.Contains("chain")))
		{
			return "ambient_nun_wall_chain_head";
		}
		if (lower.Contains("plant"))
		{
			return "ambient_plantasha_blowjob";
		}
		if (lower.Contains("zombie"))
		{
			return "ambient_zombie_bench_fuck";
		}
		if (lower.Contains("wendigo"))
		{
			return "ambient_wendigo_hole";
		}
		return null;
	}

	private static void ParseInto(string raw, Dictionary<string, string> into)
	{
		foreach (KeyValuePair<string, string> pair in ConfigMap.Pairs(raw))
		{
			into[pair.Key] = pair.Value;
		}
	}
}
