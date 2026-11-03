using System;
using PncCustomEnemies;
using PncEdi;
using UnityEngine;

namespace PncCustomEnemies.Api;

/// <summary>
/// Media handling a package would otherwise have to reimplement, and one of them it would get
/// wrong.
///
/// The video URL is the one that matters: **Unity has no H.264 decoder outside Windows and macOS**,
/// so an MP4 a package names plays on Windows and is a black rectangle on the native Linux build.
/// The framework prefers a `.webm` sibling of whatever a manifest names, on every platform, and a
/// package that resolves its own path instead of asking here reintroduces that bug per package
/// (§127, and `learnings/unity-runtime.md`).
/// </summary>
public static class PackageMedia
{
	/// <summary>A 16-bit PCM WAV decoded into an AudioClip. The game's own loader is editor-only, so this is how a package gets a sound in at runtime.</summary>
	public static AudioClip LoadWav(string path, string clipName)
	{
		return RuntimeWav.Load(path, clipName);
	}

	/// <summary>
	/// The URL to hand a `VideoPlayer` for a file the package names, with a `.webm` sibling
	/// preferred over the named file. Always resolve through this rather than building a path.
	/// </summary>
	public static string ResolveVideoUrl(string packageDirectory, string file)
	{
		return PackageVideo.ResolveUrl(packageDirectory, file);
	}

	/// <summary>
	/// A PNG sprite sheet cut into frames, left to right then top to bottom. The same loader the
	/// framework uses for `spriteVisual`, so a package's own artwork behaves identically - including
	/// the containment check that a package cannot name a file outside its own directory.
	/// </summary>
	public static PackageSpriteAnimation LoadSpriteSheet(string packageDirectory, string file, string name,
		int columns, int rows, int frameCount, float fps, float pixelsPerUnit, string pivot, bool loop = true)
	{
		CustomEnemySpriteVisual visual = new CustomEnemySpriteVisual
		{
			pixelsPerUnit = pixelsPerUnit,
			pivot = string.IsNullOrWhiteSpace(pivot) ? "0.5,0.5" : pivot
		};
		CustomEnemySpriteAnimation spec = new CustomEnemySpriteAnimation
		{
			name = name, file = file, fps = fps, loop = loop, columns = columns, rows = rows, frameCount = frameCount
		};
		RuntimeSpriteAnimationData data = RuntimeSpriteVisual.LoadAnimation(packageDirectory, visual, spec);
		return new PackageSpriteAnimation { Name = data.Name, Frames = data.Frames, Fps = data.Fps, Loop = loop };
	}

	/// <summary>
	/// The material the mod's own sprites are drawn with. A package that builds a `SpriteRenderer`
	/// by hand wants this rather than Unity's default: the game's sprites are lit, and one drawn
	/// with the default material reads as a bright cut-out in a dark room.
	/// </summary>
	public static Material SpriteMaterial => CustomEnemyRegistry.GetSpriteMaterial();

	/// <summary>
	/// Hide vanilla's grab-screen artwork for a capture the package draws itself. Without it the
	/// vanilla art of whichever enemy was grabbed last draws underneath the package's own overlay -
	/// invisible behind an opaque one and plainly wrong behind a transparent one (§133).
	/// </summary>
	public static void HideVanillaGrabArt(GrabScreen screen)
	{
		PackageGrabArt.Hide(screen);
	}
}

/// <summary>One animation loaded from a package's sprite sheet: the frames, in order, and how fast to run them.</summary>
public sealed class PackageSpriteAnimation
{
	public string Name;
	public Sprite[] Frames = Array.Empty<Sprite>();
	public float Fps = 12f;
	/// <summary>Whether the frames restart at the end. The framework's gallery viewer always loops; this is for a package that plays the same frames itself.</summary>
	public bool Loop = true;
}
