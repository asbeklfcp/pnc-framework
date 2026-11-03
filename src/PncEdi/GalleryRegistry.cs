using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;

namespace PncEdi;

public static class GalleryRegistry
{
	private static readonly HashSet<string> Known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		"ambient_gargoyle_ledge_fuck", "ambient_gooper_bed_blowjob", "ambient_imp_gangbang", "ambient_imp_gangbang_2", "ambient_mimic_wall_fuck", "ambient_nun_chair_fuck", "ambient_nun_wall_chain_head", "ambient_plantasha_blowjob", "ambient_zombie_bench_fuck", "ambient_wendigo_hole", "peek_imp_three_way",
		"peek_nun_mimic", "peek_plant_bj", "peek_wendigo_ride", "peek_nuns_threeway", "peek_gooper_pillory", "peek_zombie_bj", "filler", "filler_damage_25", "filler_damage_50", "filler_damage_75",
		"filler_cum_25", "filler_cum_50", "filler_cum_75", "Nun_Grab", "Nun_Cum", "Baphomet_Start", "Baphomet_Loop", "Baphomet_Cum", "Baphomet_Start2",
		"Baphomet_Loop2", "Baphomet_Cum2", "Dragon_Grabbed", "Dragon_Cum", "Gargoyle_Grabbed", "Gargoyle_Cum", "Gravy_Start", "Gravy_Loop", "Gravy_Cum", "Gravy_Start2",
		"Gravy_Loop2", "Gravy_Cum2", "Gravy_End2", "Gooper_Start", "Gooper_Cum", "Wendigo_Start", "Wendigo_Continued", "Mimic_Start", "Mimic_Loop", "Mimic_Cum",
		"imp_1", "imp_2", "imp_3", "imp_grab_loop", "imp_grab_cum", "Plantasha_Start", "Plantasha_Cum", "Zombie_Loop",
		"Zombie_Cum"
	};

	// Row name -> EndTime - StartTime, i.e. how long one loop of that row is. Only rows read out
	// of Definitions.csv are in here; the seeded names above carry no length, and every caller
	// treats a miss as "no length known" rather than as zero.
	private static readonly Dictionary<string, int> Durations = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
	private static bool _definitionsLoaded;
	private static string _definitionsSource;

	// What the "unknown row" messages should blame. `IsKnown` tests the registry, which is the
	// seed list above *plus* every row read from Definitions.csv - so "not in Definitions.csv" was
	// misleading twice over: it named one of the two sources, and it read as "the file lacks this
	// row" when the far more common cause is that no file was found at all (GetDefinitionPaths
	// tries three locations and all three are allowed to be absent). Naming the file that was
	// actually read, with its row count, tells those two apart in the log itself.
	internal static string Source
	{
		get
		{
			LoadDefinitions();
			return _definitionsSource ?? "no Definitions.csv found";
		}
	}

	// Milliseconds in one loop of `galleryName`, or 0 when the row is unknown or malformed.
	//
	// This exists so a ladder switch can be made phase-preserving: Edi restarts a row from the
	// top on every Play, so the only way for the incoming row to continue where the outgoing one
	// had got to is to POST `?seek=<phase>` - and the phase is elapsed time modulo *this*.
	public static int LoopMs(string galleryName)
	{
		if (string.IsNullOrEmpty(galleryName))
		{
			return 0;
		}
		LoadDefinitions();
		if (Durations.TryGetValue(StripQuery(galleryName), out var value))
		{
			return value;
		}
		return 0;
	}

	public static bool IsKnown(string galleryName)
	{
		if (string.IsNullOrEmpty(galleryName))
		{
			return false;
		}
		LoadDefinitions();
		return Known.Contains(StripQuery(galleryName));
	}

	/// <summary>
	/// Add a row that exists but is in no Definitions.csv this registry can find.
	///
	/// One caller: a custom-enemy package, whose funscripts and rows are copied into the gallery
	/// at startup. Edi re-reads Definitions.csv when it starts, but this process has already read
	/// it by then, so without this every row a package added would be reported unknown and its
	/// scenes silently skipped at the IsKnown gate.
	/// </summary>
	internal static void Register(string galleryName)
	{
		if (!string.IsNullOrWhiteSpace(galleryName))
		{
			// LoadDefinitions first, or a registration made before the first IsKnown would be the
			// thing that marks the file as read and the whole file would never be loaded at all.
			LoadDefinitions();
			Known.Add(StripQuery(galleryName.Trim()));
		}
	}

	private static void LoadDefinitions()
	{
		if (_definitionsLoaded)
		{
			return;
		}
		_definitionsLoaded = true;
		int added = 0;
		foreach (string definitionPath in GetDefinitionPaths())
		{
			if (!File.Exists(definitionPath))
			{
				continue;
			}
			_definitionsSource = (_definitionsSource == null) ? definitionPath : (_definitionsSource + " + " + definitionPath);
			using StreamReader streamReader = new StreamReader(definitionPath);
			while (!streamReader.EndOfStream)
			{
				string rawLine = streamReader.ReadLine();
				if (!string.IsNullOrWhiteSpace(rawLine))
				{
					string line = rawLine.Trim().Trim('\ufeff');
					if (line.Length != 0 && !line.StartsWith("#", StringComparison.Ordinal))
					{
						string[] fields = line.Split(',');
						string row = fields[0].Trim();
						if (Known.Add(row))
						{
							added++;
						}
						// Name,FileName,StartTime,EndTime,Type,Loop. The header row and any row
						// with unparsable bounds simply record no length.
						if (fields.Length >= 4 && int.TryParse(fields[2].Trim(), out var startMs) && int.TryParse(fields[3].Trim(), out var endMs) && endMs > startMs)
						{
							Durations[row] = endMs - startMs;
						}
					}
				}
			}
		}
		if (_definitionsSource != null)
		{
			_definitionsSource = $"{_definitionsSource} ({added} row(s) beyond the built-in seed, {Known.Count} known in total)";
		}
	}

	private static IEnumerable<string> GetDefinitionPaths()
	{
		if (!string.IsNullOrEmpty(Paths.GameRootPath))
		{
			yield return Path.Combine(Paths.GameRootPath, "Edi", "Gallery", "Definitions.csv");
		}
		if (!string.IsNullOrEmpty(Paths.BepInExRootPath))
		{
			yield return Path.Combine(Path.GetDirectoryName(Paths.BepInExRootPath) ?? Paths.BepInExRootPath, "Edi", "Gallery", "Definitions.csv");
		}
		yield return Path.Combine(AppContext.BaseDirectory, "Edi", "Gallery", "Definitions.csv");
	}

	public static string StripQuery(string galleryName)
	{
		int queryStart = galleryName.IndexOf('?');
		if (queryStart > 0)
		{
			return galleryName.Substring(0, queryStart);
		}
		return galleryName;
	}
}
