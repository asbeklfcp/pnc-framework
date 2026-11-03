using System;
using System.Collections.Generic;

namespace PncEdi;

internal static class PeekGalleryMap
{
	private static readonly Dictionary<string, string> Map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
	private static bool _loaded;

	internal static void Reload()
	{
		Map.Clear();
		AddBuiltInDefaults();
		ParseInto(Plugin.CfgPeekGalleryMap?.Value);
		_loaded = true;
	}

	internal static bool IsPeekScript(string galleryName)
	{
		return !string.IsNullOrEmpty(galleryName) && galleryName.StartsWith("peek_", StringComparison.OrdinalIgnoreCase);
	}

	internal static string Resolve(string enemyId, string enemyName, string animationName)
	{
		return Resolve(enemyId, enemyName, animationName, null, null);
	}

	// assetName is the ScriptableObject's own name ("ImpThreeWayGalleryData") and
	// peepholeController the name of the controller the entry plays ("ImpThreeWayGallery").
	// Both are asset names the project set once; `enemyName` is a *display* string and 0.3.1
	// rewrote all eleven of them into jokes - "It'll Fit... See!", "Snussy Ray" - which is what
	// silently took every peek scene off the device (§80). `enemyID` is only "P1".."P11", the
	// trigger's position, so it is matched last of the four: a renumbered peephole would map to
	// a confidently wrong scene, and a wrong scene is worse than a missing one.
	internal static string Resolve(string enemyId, string enemyName, string animationName,
		string assetName, string peepholeController)
	{
		if (!_loaded)
		{
			Reload();
		}
		if (!string.IsNullOrEmpty(assetName) && Map.TryGetValue(assetName.Trim(), out var byAsset))
		{
			return byAsset;
		}
		if (!string.IsNullOrEmpty(peepholeController) && Map.TryGetValue(peepholeController.Trim(), out var byController))
		{
			return byController;
		}
		if (!string.IsNullOrEmpty(enemyName) && Map.TryGetValue(enemyName.Trim(), out var byEnemyName))
		{
			return byEnemyName;
		}
		if (!string.IsNullOrEmpty(enemyId) && Map.TryGetValue(enemyId.Trim(), out var byEnemyId))
		{
			return byEnemyId;
		}
		// The animation clip is the only thing that identifies a peephole. Every peek trigger
		// in the game is called "P1", "P2", "P3"... - no enemy name, nothing for the two exact
		// lookups above to match on - so the combined slug reads e.g.
		// "p3_peephole_plantasha_loop". Handing that to GalleryAliases lets its greedy
		// per-enemy substring matcher win on "plantasha" and return the *grab* gallery, and
		// the peek heuristic below never runs. Match the clip first.
		string byClip = ResolveByClip(animationName);
		if (!string.IsNullOrEmpty(byClip))
		{
			return byClip;
		}
		string slug = BuildCombinedSlug(enemyName, animationName);
		if (!string.IsNullOrEmpty(slug))
		{
			string alias = GalleryAliases.Resolve(slug, inGame: false, quiet: true);
			if (!string.IsNullOrEmpty(alias) && alias != "-" && !string.Equals(alias, slug, StringComparison.OrdinalIgnoreCase))
			{
				return alias;
			}
		}
		// No guess after this point, deliberately (§94). There used to be a keyword heuristic
		// here - "imp" plus "3" meant peek_imp_three_way, "zombie" plus "bj" meant peek_zombie_bj.
		// It has been unreachable since §80 gave all eleven entries an exact asset key, and it
		// was a trap if it ever ran again: `imp_3` is one of the imp *cling* rows and matches
		// the imp clause, and this method's result is also read as a predicate
		// (`IsPeekScript(Resolve(...))`) by the escape gate, the heat lock and the camera swap,
		// so a false positive there mislabels a grab as a peephole rather than merely playing
		// the wrong row. Returning null instead makes the miss inert, and `SendPeekGalleryStep`
		// prints all four keys it tried. A build this table has not seen is a `PeekClipMap`
		// config line, not a keyword rule.
		return null;
	}

	// Longest key first, so "peephole_nun_mimic" is tested before "peephole_nuns".
	private static List<KeyValuePair<string, string>> _clipMap;
	private static string _clipMapFrom;

	private static string ResolveByClip(string animationName)
	{
		if (string.IsNullOrEmpty(animationName))
		{
			return null;
		}
		string raw = Plugin.CfgPeekClipMap?.Value ?? "";
		if (_clipMap == null || !string.Equals(_clipMapFrom, raw))
		{
			_clipMapFrom = raw;
			_clipMap = new List<KeyValuePair<string, string>>();
			// The key is slugged, because it is matched against a slugged clip name. Slug()
			// collapses anything non-alphanumeric and trims, so slugging the trimmed key is the
			// same string the untrimmed one used to give.
			foreach (KeyValuePair<string, string> pair in ConfigMap.Pairs(raw))
			{
				string slugged = NameRemap.Slug(pair.Key);
				if (slugged.Length != 0)
				{
					_clipMap.Add(new KeyValuePair<string, string>(slugged, pair.Value));
				}
			}
			ConfigMap.SortLongestKeyFirst(_clipMap);
		}
		string slug = NameRemap.Slug(animationName);
		for (int j = 0; j < _clipMap.Count; j++)
		{
			if (slug.IndexOf(_clipMap[j].Key, StringComparison.Ordinal) >= 0)
			{
				return _clipMap[j].Value;
			}
		}
		return null;
	}

	internal static string BuildCombinedSlug(string enemyName, string animationName)
	{
		string enemyKey = NameRemap.ResolveEnemyKey(enemyName ?? "peek");
		return NameRemap.BuildGallerySlug(enemyKey, animationName);
	}

	private static void ParseInto(string raw)
	{
		foreach (KeyValuePair<string, string> pair in ConfigMap.Pairs(raw))
		{
			Map[pair.Key] = pair.Value;
		}
	}

	private static void AddBuiltInDefaults()
	{
		// 0.3.1's eleven peek entries, read out of sharedassets0 rather than guessed: the
		// ScriptableObject's own name and the peephole controller it plays. Both are asset
		// names, so they survive the display titles being rewritten, which is what broke
		// every peek scene in 0.3.1 (§80). Where the two are the same asset name only one
		// line appears.
		Map["ImpThreeWayGalleryData"] = "peek_imp_three_way";
		Map["ImpThreeWayGallery"] = "peek_imp_three_way";
		Map["Nun&MimicGalleryData"] = "peek_nun_mimic";
		Map["Mimic&NunGallery"] = "peek_nun_mimic";
		Map["PlantBJGalleryData"] = "peek_plant_bj";
		Map["PlantBJGallery"] = "peek_plant_bj";
		Map["WendigoRideGalleryData"] = "peek_wendigo_ride";
		Map["WendigoRidingGallery"] = "peek_wendigo_ride";
		Map["NunsThreewayGalleryData"] = "peek_nuns_threeway";
		Map["NunsDuoGallery"] = "peek_nuns_threeway";
		Map["GooperGloryHoleGalleryEntry"] = "peek_gooper_pillory";
		Map["GooperPilloaryGallery"] = "peek_gooper_pillory";
		Map["ZombieBJpeekscene"] = "peek_zombie_bj";
		Map["ZombieBJGallery"] = "peek_zombie_bj";
		Map["GravyBathingGallery"] = "peek_gravy_bath";
		Map["Gargoyle FuckfestGallery"] = "peek_gargoyle_fuck_fest";
		Map["GargoyleFuckFestGallery"] = "peek_gargoyle_fuck_fest";
		Map["Serpent Prison Style"] = "peek_serpent_prison";
		Map["Serpent Prison Style Gallery"] = "peek_serpent_prison";
		Map["BlindedBeastRide"] = "peek_werewolf_ride";
		Map["Blinded Beast Ride Gallery"] = "peek_werewolf_ride";
		Map["ImpThreeWay"] = "peek_imp_three_way";
		Map["ImpThree"] = "peek_imp_three_way";
		Map["Imp_Grab_One"] = "peek_imp_three_way";
		Map["Imp_Grab_Two"] = "peek_imp_three_way";
		Map["Imp_Grab_Three"] = "peek_imp_three_way";
		Map["ImpGrabOne"] = "peek_imp_three_way";
		Map["ImpGrabTwo"] = "peek_imp_three_way";
		Map["ImpGrabThree"] = "peek_imp_three_way";
		Map["ImpThreway"] = "peek_imp_three_way";
		Map["ImpThrewaay"] = "peek_imp_three_way";
		Map["ImpPeephole"] = "peek_imp_three_way";
		Map["ImpPeepholeScene"] = "peek_imp_three_way";
		Map["ImpThreeWayPeephole"] = "peek_imp_three_way";
	}
}
