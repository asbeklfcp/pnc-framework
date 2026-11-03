using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BepInEx;
using PncCustomEnemies;
using UnityEngine;

namespace PncEdi;

/// <summary>
/// Every installed package's funscripts and `Definitions.csv` rows, imported before any package
/// code runs and without caring what kind of manifest declares them (§175).
///
/// **Why this is not in the enemy registry, where it started.** Edi reads `Definitions.csv` and the
/// variant folders exactly once, at its own startup, so a row that only appears when a package is
/// switched on costs the player an Edi restart they were never told about - §173's reasoning, which
/// made the registry import a switched-off package's scripts anyway. That fixed one package kind:
/// the registry only ever sees `enemy.json`, so a wall trap's rows still came from the trap's own
/// assembly, which does not load while the package is off. Two kinds, two importers, two answers to
/// one question.
///
/// This is the one importer. It reads **manifests, not kinds**: any `*.json` at the top of a package
/// directory, `scenes[]` and `animations[]` alike, whatever the file is called and whatever code (if
/// any) the package ships. It runs before <see cref="PncCustomEnemies.PackageAssemblies"/>, so a
/// package whose assembly is refused, missing or switched off still hands Edi its gallery.
///
/// **It is deliberately the whole of what a switched-off package gets.** No sprite sheets, no
/// prefab, no gallery entry, no spawn share, no behaviour, no assembly - §167 put the consent on
/// that switch and a funscript is inert data in a file Edi parses, not something the package gets
/// to run. What the switch refuses is code, and the code stays refused.
///
/// **The rows it builds must stay byte-identical to `release.custom_enemy_gallery`'s**, which
/// produces the same rows from the repo side so a deployed install is already correct and this
/// finds nothing to do. They disagreeing is what `deploy.py --check` reports as a permanently
/// stale install (§129), and §164 is the standing decision to keep both writers and keep them in
/// step.
/// </summary>
internal static class PackageGalleryImport
{
	internal static void Run()
	{
		string root = Path.Combine(Paths.BepInExRootPath, "custom-enemies");
		Directory.CreateDirectory(root);
		string galleryRoot = Path.Combine(Paths.GameRootPath, "Edi", "Gallery");
		if (!Directory.Exists(galleryRoot))
		{
			CustomEnemyPlugin.Log?.LogWarning("[CustomEnemies] Edi/Gallery not found; no package funscripts were installed");
			return;
		}
		string[] directories = Directory.GetDirectories(root);
		Array.Sort(directories, StringComparer.OrdinalIgnoreCase);
		foreach (string directory in directories)
		{
			if (string.Equals(Path.GetFileName(directory), "_example", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			string[] manifests = Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly);
			if (manifests.Length == 0)
			{
				continue;
			}
			Array.Sort(manifests, StringComparer.OrdinalIgnoreCase);
			// The scripts are the directory's, not any one manifest's: copy them once, then let
			// each manifest in the directory contribute the rows that point at them.
			CopyFunscripts(directory, galleryRoot);
			foreach (string manifestPath in manifests)
			{
				try
				{
					string json = File.ReadAllText(manifestPath);
					List<string> rows = Rows(json, directory);
					CustomEnemyRegistry.MergeDefinitions(
						"CustomEnemies",
						CustomEnemyRegistry.ReadManifestId(json) ?? Path.GetFileName(directory),
						directory,
						galleryRoot,
						rows);
				}
				catch (Exception ex)
				{
					CustomEnemyPlugin.Log?.LogError("[CustomEnemies] could not import the gallery of " + manifestPath + ": " + ex.Message);
				}
			}
		}
	}

	/// <summary>
	/// One manifest's rows, from whichever vocabulary it uses.
	///
	/// `scenes[]` is the framework's own - a gallery name, a file, and a measured slice of it.
	/// `animations[]` is a wall trap's - a sprite-sheet stage with a funscript beside it and no
	/// slice at all, so the end is the last `at` in the script. A manifest may use either; reading
	/// both here is what makes this importer kind-agnostic, and it is the same pair
	/// `release.custom_enemy_gallery` reads on the repo side.
	/// </summary>
	private static List<string> Rows(string json, string directory)
	{
		string funscripts = Path.Combine(directory, "funscripts");
		List<string> rows = new List<string>();
		foreach (CustomEnemyScene scene in CustomEnemyRegistry.ParseScenesFor(json))
		{
			if (scene == null || string.IsNullOrWhiteSpace(scene.animation)) continue;
			string gallery = string.IsNullOrWhiteSpace(scene.gallery)
				? CustomEnemyRegistry.ReadManifestId(json) + "_" + NameRemap.Slug(scene.animation)
				: scene.gallery.Trim();
			string file = string.IsNullOrWhiteSpace(scene.file) ? gallery : Path.GetFileNameWithoutExtension(scene.file.Trim());
			int end = scene.endTime > 0 ? scene.endTime : CustomEnemyRegistry.FindFunscriptEnd(funscripts, file);
			if (end <= scene.startTime) end = scene.startTime + 1000;
			rows.Add(string.Join(",", gallery, file, scene.startTime.ToString(CultureInfo.InvariantCulture), end.ToString(CultureInfo.InvariantCulture), "gallery", scene.oneShot ? "false" : "true"));
		}
		foreach (PackageManifestStage stage in CustomEnemyRegistry.ParseStagesFor(json))
		{
			if (stage == null) continue;
			string gallery = string.IsNullOrWhiteSpace(stage.gallery)
				? CustomEnemyRegistry.ReadManifestId(json) + "_" + NameRemap.Slug(stage.name)
				: stage.gallery.Trim();
			if (string.IsNullOrWhiteSpace(gallery)) continue;
			string file = string.IsNullOrWhiteSpace(stage.funscript) ? gallery : Path.GetFileNameWithoutExtension(stage.funscript.Trim());
			// A stage with no funscript to measure contributes no row - a trap stage is a picture
			// first and a scene second, and one that plays nothing must not leave Edi a row whose
			// slice was invented here.
			int end = CustomEnemyRegistry.FindFunscriptEnd(funscripts, file);
			if (end <= 0) continue;
			rows.Add(string.Join(",", gallery, file, "0", end.ToString(CultureInfo.InvariantCulture), "gallery", "true"));
		}
		return rows;
	}

	/// <summary>
	/// A package's scripts into `Edi/Gallery/&lt;variant&gt;/`, never over a different script of the
	/// same name. The gallery is this project's own, hand-authored and measured against specific
	/// game assets; a package that happens to ship `imp_grab_loop.funscript` must not be able to
	/// replace the real one. Identical content copies silently, which is the normal case -
	/// `deploy.py` has put these here already.
	/// </summary>
	private static void CopyFunscripts(string directory, string galleryRoot)
	{
		string sourceRoot = Path.Combine(directory, "funscripts");
		if (!Directory.Exists(sourceRoot))
		{
			return;
		}
		foreach (string variantDir in Directory.GetDirectories(sourceRoot))
		{
			string targetDir = Path.Combine(galleryRoot, Path.GetFileName(variantDir));
			Directory.CreateDirectory(targetDir);
			foreach (string source in Directory.GetFiles(variantDir, "*.funscript", SearchOption.TopDirectoryOnly))
			{
				string target = Path.Combine(targetDir, Path.GetFileName(source));
				if (File.Exists(target) && !CustomEnemyRegistry.SameFileContent(source, target))
				{
					CustomEnemyPlugin.Log?.LogWarning("[CustomEnemies] '" + Path.GetFileName(directory) + "' ships "
						+ Path.GetFileName(source) + ", but a different script of that name is already in "
						+ Path.GetFileName(targetDir) + " - kept the existing one; rename the package's script");
					continue;
				}
				try
				{
					File.Copy(source, target, true);
				}
				catch (Exception ex)
				{
					CustomEnemyPlugin.Log?.LogWarning("[CustomEnemies] could not install " + Path.GetFileName(source) + ": " + ex.Message);
				}
			}
		}
	}
}

/// <summary>
/// One `animations[]` entry, read for its gallery row alone.
///
/// A wall trap's stage carries a sprite sheet, a frame count, an fps and a funscript; the three
/// fields here are the only ones a row is built from, and the package's own assembly parses the
/// rest into its own type. The framework deliberately learns the *row* vocabulary of a manifest
/// kind it otherwise knows nothing about - that is the whole of what this importer is.
/// </summary>
[Serializable]
internal sealed class PackageManifestStage
{
	// Assigned by JsonUtility through reflection, which the compiler cannot see - hence the pragma.
#pragma warning disable CS0649
	public string name;
	public string gallery;
	public string funscript;
#pragma warning restore CS0649
}
