using System;
using System.Collections.Generic;
using System.Text;

namespace PncEdi;

public static class NameRemap
{
	private static List<KeyValuePair<string, string>> _entries = new List<KeyValuePair<string, string>>();

	public static void Reload()
	{
		_entries.Clear();
		// Both sides lowercased: the match is case-insensitive and doing it once here keeps
		// ResolveEnemyKey a plain substring test.
		foreach (KeyValuePair<string, string> pair in ConfigMap.Pairs(Plugin.CfgEnemyNameRemap?.Value))
		{
			_entries.Add(new KeyValuePair<string, string>(pair.Key.ToLowerInvariant(),
			                                             pair.Value.ToLowerInvariant()));
		}
		ConfigMap.SortLongestKeyFirst(_entries);
	}

	public static string ResolveEnemyKey(string gameObjectName)
	{
		// A custom-enemy package owns its own names, and it has to be asked first: its prefab is a
		// clone of a vanilla enemy, so the table below would happily match the enemy it was cloned
		// from and route the package's scenes to that enemy's funscripts.
		//
		// It is a delegate rather than a direct call to CustomEnemyRegistry because this file is
		// one of the eight `code/tests` compiles, and the registry drags in AssetBundle, the game's
		// EnemyData and half of Unity behind it. The registry installs itself at startup; in a test
		// the hook is simply null and the table is the whole answer, which is what those tests are
		// about.
		if (CustomEnemyResolver != null)
		{
			string customKey = CustomEnemyResolver(gameObjectName);
			if (!string.IsNullOrEmpty(customKey))
			{
				return customKey;
			}
		}
		if (_entries.Count == 0)
		{
			Reload();
		}
		string lower = StripCloneSuffix(gameObjectName ?? "").ToLowerInvariant();
		foreach (KeyValuePair<string, string> entry in _entries)
		{
			if (lower.Contains(entry.Key))
			{
				return entry.Value;
			}
		}
		return Slug(lower);
	}

	/// <summary>
	/// Installed by CustomEnemyRegistry at startup; returns a package's key for a name it owns, or
	/// null. Null when no packages are loaded, and always null under `code/tests`.
	/// </summary>
	internal static Func<string, string> CustomEnemyResolver;

	public static string StripCloneSuffix(string n)
	{
		if (string.IsNullOrEmpty(n))
		{
			return n;
		}
		int clone = n.IndexOf("(Clone)", StringComparison.OrdinalIgnoreCase);
		if (clone > 0)
		{
			n = n.Substring(0, clone);
		}
		return n.Trim();
	}

	public static string Slug(string raw)
	{
		if (string.IsNullOrEmpty(raw))
		{
			return "";
		}
		StringBuilder stringBuilder = new StringBuilder(raw.Length);
		char c = '_';
		foreach (char c2 in raw)
		{
			if (char.IsLetterOrDigit(c2))
			{
				stringBuilder.Append(char.ToLowerInvariant(c2));
				c = c2;
				continue;
			}
			if (c != '_')
			{
				stringBuilder.Append('_');
			}
			c = '_';
		}
		return stringBuilder.ToString().Trim('_');
	}

	public static string BuildGallerySlug(string enemyKey, string stepName)
	{
		string slug = Slug(stepName);
		if (string.IsNullOrEmpty(enemyKey))
		{
			return slug;
		}
		string lower = NormalizeEnemyKeyForGallery(enemyKey.Trim().ToLowerInvariant());
		if (string.IsNullOrEmpty(lower))
		{
			return slug;
		}
		slug = StripEmbeddedEnemyPrefix(lower, slug);
		slug = NormalizeGrabStepSuffix(lower, slug);
		return FinishGallerySlug(lower, slug);
	}

	private static string FinishGallerySlug(string key, string slug)
	{
		if (string.IsNullOrEmpty(slug))
		{
			return key;
		}
		if (slug.StartsWith(key + "_", StringComparison.Ordinal))
		{
			string remainder = slug.Substring(key.Length + 1);
			if (remainder.StartsWith(key, StringComparison.Ordinal))
			{
				return slug;
			}
			return slug;
		}
		if (slug.Length > key.Length && slug.StartsWith(key, StringComparison.Ordinal))
		{
			string remainder = slug.Substring(key.Length).Trim('_');
			if (remainder.Length > 0)
			{
				return key + "_" + key + remainder;
			}
		}
		string doubledPrefix = key + key + "_";
		if (slug.StartsWith(doubledPrefix, StringComparison.Ordinal))
		{
			return key + "_" + slug.Substring(doubledPrefix.Length);
		}
		return key + "_" + slug;
	}

	private static string StripEmbeddedEnemyPrefix(string key, string slug)
	{
		if (string.IsNullOrEmpty(slug))
		{
			return slug;
		}
		string remainder = null;
		if (slug.StartsWith(key + "_", StringComparison.Ordinal))
		{
			remainder = slug.Substring(key.Length + 1);
		}
		else if (slug.Length > key.Length && slug.StartsWith(key, StringComparison.Ordinal))
		{
			remainder = slug.Substring(key.Length).Trim('_');
		}
		if (remainder != null && ShouldStripEmbeddedEnemyPrefix(remainder))
		{
			return remainder;
		}
		return slug;
	}

	private static bool ShouldStripEmbeddedEnemyPrefix(string remainder)
	{
		if (string.IsNullOrEmpty(remainder))
		{
			return false;
		}
		if (remainder == "grab")
		{
			return true;
		}
		return remainder.StartsWith("grabscreen", StringComparison.Ordinal) || remainder.StartsWith("plantica", StringComparison.Ordinal) || remainder.StartsWith("grabinit", StringComparison.Ordinal) || remainder.StartsWith("grabloop", StringComparison.Ordinal) || remainder.StartsWith("grabbed_", StringComparison.Ordinal) || remainder.StartsWith("ghoulgrab", StringComparison.Ordinal) || remainder.StartsWith("kiss", StringComparison.Ordinal) || remainder.StartsWith("sex", StringComparison.Ordinal) || remainder.StartsWith("facesit", StringComparison.Ordinal);
	}

	private static string NormalizeGrabStepSuffix(string key, string slug)
	{
		if (key == "plantasha" && slug.StartsWith("plantica", StringComparison.Ordinal))
		{
			slug = slug.Substring("plantica".Length).Trim('_');
		}
		slug = StripGrabScreenPrefix(slug);
		slug = NormalizeMimicGrabTokens(slug);
		slug = NormalizeGhoulGrabTokens(slug);
		slug = StripEnemyNameDuplication(key, slug);
		slug = StripAltGrabSuffix(slug);
		return slug;
	}

	private static string NormalizeEnemyKeyForGallery(string key)
	{
		if (string.IsNullOrEmpty(key))
		{
			return key;
		}
		if (key.EndsWith("_alt1", StringComparison.Ordinal))
		{
			return key.Substring(0, key.Length - 5);
		}
		if (key.EndsWith("_alt", StringComparison.Ordinal))
		{
			return key.Substring(0, key.Length - 4);
		}
		return key;
	}

	private static string NormalizeMimicGrabTokens(string slug)
	{
		if (slug == "grabbed_start" || slug.EndsWith("_grabbed_start", StringComparison.Ordinal))
		{
			return "grab";
		}
		if (slug == "grabbed_loop" || slug.EndsWith("_grabbed_loop", StringComparison.Ordinal))
		{
			return "loop";
		}
		if (slug == "grabbed_cum" || slug.EndsWith("_grabbed_cum", StringComparison.Ordinal))
		{
			return "cum";
		}
		if (slug == "grabinit" || slug.StartsWith("grabinit_", StringComparison.Ordinal) || slug.EndsWith("grabinit", StringComparison.Ordinal))
		{
			return "grab";
		}
		if (slug == "grabloop" || slug.StartsWith("grabloop_", StringComparison.Ordinal) || slug.EndsWith("grabloop", StringComparison.Ordinal))
		{
			return "loop";
		}
		return slug;
	}

	private static string NormalizeGhoulGrabTokens(string slug)
	{
		if (!slug.StartsWith("ghoulgrab", StringComparison.Ordinal))
		{
			return slug;
		}
		string remainder = slug.Substring("ghoulgrab".Length).Trim('_');
		if (string.IsNullOrEmpty(remainder))
		{
			return "grab";
		}
		if (remainder.StartsWith("cumcontinue", StringComparison.Ordinal) || remainder.StartsWith("continue", StringComparison.Ordinal))
		{
			return "grab";
		}
		if (remainder.StartsWith("start", StringComparison.Ordinal))
		{
			return "grab";
		}
		if (remainder.StartsWith("screen", StringComparison.Ordinal) || remainder.StartsWith("grabscreen", StringComparison.Ordinal))
		{
			return "grab";
		}
		if (remainder.StartsWith("cum", StringComparison.Ordinal))
		{
			return "cum";
		}
		return slug;
	}

	private static string StripGrabScreenPrefix(string slug)
	{
		if (slug.StartsWith("grabscreen_", StringComparison.Ordinal))
		{
			return slug.Substring("grabscreen_".Length);
		}
		if (slug == "grabscreen")
		{
			return "grab";
		}
		if (slug.StartsWith("grabscreen", StringComparison.Ordinal))
		{
			string remainder = slug.Substring("grabscreen".Length).Trim('_');
			return string.IsNullOrEmpty(remainder) ? "grab" : remainder;
		}
		return slug;
	}

	private static string StripEnemyNameDuplication(string key, string slug)
	{
		if (string.IsNullOrEmpty(slug) || string.IsNullOrEmpty(key))
		{
			return slug;
		}
		string doubledKey = key + key;
		if (slug.StartsWith(doubledKey, StringComparison.Ordinal))
		{
			return slug.Substring(doubledKey.Length).Trim('_');
		}
		if (slug.StartsWith(key + "_", StringComparison.Ordinal))
		{
			string remainder = slug.Substring(key.Length + 1);
			if (remainder.StartsWith(key, StringComparison.Ordinal))
			{
				return remainder.Substring(key.Length).Trim('_');
			}
		}
		return slug;
	}

	private static string StripAltGrabSuffix(string slug)
	{
		if (string.IsNullOrEmpty(slug))
		{
			return slug;
		}
		if (slug.EndsWith("_alt1", StringComparison.Ordinal))
		{
			return slug.Substring(0, slug.Length - 5);
		}
		if (slug.EndsWith("_alt", StringComparison.Ordinal))
		{
			return slug.Substring(0, slug.Length - 4);
		}
		if (slug.EndsWith("alt1", StringComparison.Ordinal) && slug.Length > 4)
		{
			return slug.Substring(0, slug.Length - 4).TrimEnd('_');
		}
		if (slug.EndsWith("alt", StringComparison.Ordinal) && slug.Length > 3)
		{
			return slug.Substring(0, slug.Length - 3).TrimEnd('_');
		}
		return slug;
	}
}
