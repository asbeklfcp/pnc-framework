using System.Collections.Generic;
using PncEdi;
using UnityEngine;

namespace PncCustomEnemies.Api;

/// <summary>
/// Getting a package's own rows into the gallery Edi actually reads.
///
/// **Edi reads exactly one gallery** - `Edi/Gallery/&lt;variant&gt;/` plus one `Definitions.csv` -
/// so a package's scripts are copied out of the package and its rows merged into that file at
/// startup. That duplication is deliberate and was re-decided in §164: a player has no repo and no
/// Python, so the mod has to be able to do from inside the game what `deploy.py` does from a
/// working tree. `MergeRows` is the same writer the framework uses for its own packages, with
/// ownership read off the file, so a package cannot quietly repoint a row the mod already defines.
/// </summary>
public static class PackageGallery
{
	/// <summary>Tell the registry a row name exists, so the mod's `IsKnown` gate stops treating it as a typo.</summary>
	public static void RegisterRow(string galleryName)
	{
		GalleryRegistry.Register(galleryName);
	}

	/// <summary>
	/// Merge rows into `Definitions.csv`, tagged with the package that owns them. Rows the mod
	/// itself defines are never overwritten - that measurement was made against a specific game
	/// asset - and rows this package wrote before are replaced rather than duplicated.
	/// </summary>
	public static void MergeRows(string logTag, string packageId, string packageDirectory, string galleryRoot, List<string> rows)
	{
		CustomEnemyRegistry.MergeDefinitions(logTag, packageId, packageDirectory, galleryRoot, rows);
	}

	/// <summary>Whether two files are byte-identical - what a funscript copy tests before overwriting an install's copy of its own script.</summary>
	public static bool SameFileContent(string left, string right)
	{
		return CustomEnemyRegistry.SameFileContent(left, right);
	}

	/// <summary>Push the current custom entry list into a live gallery UI or the progress manager, after a package's set has changed.</summary>
	public static void RefreshGallery(object target)
	{
		CustomEnemyRegistry.InjectGallery(target);
	}
}

/// <summary>
/// How the gallery should present one entry a package contributed: the stages it steps through,
/// what each stage looks like, and what each plays.
///
/// **The framework keeps the UI and the package keeps the content**, deliberately: the viewer, the
/// stage stepping and the audio are the framework's, driven by this description rather than by a
/// package's own MonoBehaviour (§165), so a package describes its gallery and does not draw it.
/// </summary>
public sealed class PackageGalleryPresentation
{
	/// <summary>The stages, in the order the viewer steps through them.</summary>
	public PackageGalleryStage[] Stages = System.Array.Empty<PackageGalleryStage>();

	/// <summary>An optional sound to play for as long as the entry is open.</summary>
	public AudioClip Sound;

	public bool SoundLoops = true;

	public float SoundVolume = 1f;
}

/// <summary>One step of a package's gallery entry: an animation to draw and a row to send.</summary>
public sealed class PackageGalleryStage
{
	/// <summary>What the stage is called in the viewer's step list, and what a row is looked up by.</summary>
	public string Name;

	/// <summary>The frames to draw, in order.</summary>
	public PackageSpriteAnimation Animation;

	/// <summary>The gallery row this stage plays on the device, or null for silence.</summary>
	public string Gallery;
}
