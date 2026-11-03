using System.Collections.Generic;
using PncCustomEnemies.Api;
using PncEdi;

namespace PncCustomEnemies;

/// <summary>
/// The gallery entries package modules contribute, asked of every loaded provider rather than of
/// one registry the framework happens to contain.
///
/// Before §165 this was `WallPictureTrapRegistry` called by name from six places in
/// `CustomGallerySection`, which is what made the wall trap - a whole package kind, with its own
/// manifest and its own display - impossible to move out of the framework. Now a module says what
/// it owns and how it should look, and the framework keeps the viewer.
///
/// **A provider disappears when its package's code is switched off, and that is the intended
/// behaviour for the entries but not for the media**: an entry contributed by a module is gone from
/// the gallery until the code is allowed to run, because nothing else knows the entry exists. A
/// package that wants its gallery to work without its code should declare it in `enemy.json`
/// (`scenes`, `galleryVideos`), which the framework reads on its own.
/// </summary>
internal static class PackageGalleryEntries
{
	private static readonly GalleryPlaybackAdmission Admission = new GalleryPlaybackAdmission();
	internal static bool CanPlay(EnemyGalleryEntry entry)
	{
		if (entry == null) return false;
		CustomEnemyDefinition definition = CustomEnemyRegistry.Find(entry);
		if (definition != null) return definition.Enabled;
		return Admission.Allows(entry, PackageLifecycle.Active);
	}
	internal static IEnumerable<EnemyGalleryEntry> All()
	{
		foreach (IPackageGalleryProvider provider in PackageAssemblies.GalleryProviders)
		{
			IEnumerable<EnemyGalleryEntry> entries = provider.GalleryEntries;
			if (entries == null)
			{
				continue;
			}
			foreach (EnemyGalleryEntry entry in entries)
			{
				if (entry != null)
				{
					Admission.Remember(entry, PackageAssemblies.GalleryOwner(provider));
					yield return entry;
				}
			}
		}
	}

	internal static bool IsProvided(EnemyGalleryEntry entry)
	{
		return entry != null && Describe(entry) != null;
	}

	/// <summary>How a provided entry should be displayed, or null when no provider claims it.</summary>
	internal static PackageGalleryPresentation Describe(EnemyGalleryEntry entry)
	{
		if (entry == null)
		{
			return null;
		}
		foreach (IPackageGalleryProvider provider in PackageAssemblies.GalleryProviders)
		{
			PackageGalleryPresentation presentation = null;
			try
			{
				presentation = provider.Describe(entry);
			}
			catch (System.Exception ex)
			{
				CustomEnemyPlugin.Log?.LogError("[CustomGallery] a package threw describing a gallery entry: " + ex);
			}
			if (presentation != null)
			{
				Admission.Remember(entry, PackageAssemblies.GalleryOwner(provider));
				return presentation;
			}
		}
		return null;
	}

	/// <summary>The row one provided entry plays at one animation label, or null when nothing claims it.</summary>
	internal static string ResolveRow(EnemyGalleryEntry entry, string animationName)
	{
		if (entry == null)
		{
			return null;
		}
		foreach (IPackageGalleryProvider provider in PackageAssemblies.GalleryProviders)
		{
			try
			{
				string row = provider.ResolveGalleryRow(entry, animationName);
				if (!string.IsNullOrWhiteSpace(row))
				{
					return row;
				}
			}
			catch (System.Exception ex)
			{
				CustomEnemyPlugin.Log?.LogError("[CustomGallery] a package threw resolving a gallery row: " + ex);
			}
		}
		return null;
	}

	/// <summary>One stage's animation, or null - the shape the viewer wants without every caller re-checking three levels of array.</summary>
	internal static PackageSpriteAnimation StageAnimation(PackageGalleryPresentation presentation, int index)
	{
		if (presentation?.Stages == null || presentation.Stages.Length == 0)
		{
			return null;
		}
		int clamped = UnityEngine.Mathf.Clamp(index, 0, presentation.Stages.Length - 1);
		return presentation.Stages[clamped]?.Animation;
	}
}
