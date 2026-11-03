using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace PncEdi;

/// <summary>
/// Turning a manifest's video filename into a URL Unity can actually play, on this platform.
///
/// **Unity's VideoPlayer does not decode H.264 on Linux.** It has no decoder of its own for it:
/// on Windows it hands the file to Media Foundation and on macOS to AVFoundation, and the Linux
/// standalone player has no equivalent to hand it to. What it *does* carry on every platform is
/// libvpx, so VP8 (and VP9) in a WebM container plays everywhere. The custom-enemy packages ship
/// H.264 MP4, which is why the witch's dream-cloud overlays are a black rectangle on Linux while
/// being fine on Windows - and why the failure is so quiet: `Prepare()` simply never completes,
/// there is no exception, and a transparent overlay with nothing in it looks like a design choice.
///
/// So: prefer a `.webm` sibling of whatever the manifest names, on every platform. WebM plays
/// everywhere, which makes one package work on both without the manifest having to say so and
/// without a package author having to think about it. Where no sibling exists the declared file is
/// used unchanged - correct on Windows, and on Linux it logs what is wrong and how to fix it
/// rather than leaving a black rectangle to be diagnosed from scratch.
///
/// `code/webmify.py` does the conversion.
/// </summary>
internal static class PackageVideo
{
	// Logged once per file, not once per play: the witch reopens her overlay every time the player
	// walks into the circle, and a warning per entry would bury everything else in the log.
	private static readonly HashSet<string> Warned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// True on the platform whose VideoPlayer has no H.264 decoder.
	///
	/// `Application.platform` rather than a compile-time symbol: the plugin is one DLL, built once
	/// and shipped to both installs, so this has to be a runtime question.
	/// </summary>
	private static bool NeedsWebm =>
		Application.platform == RuntimePlatform.LinuxPlayer
		|| Application.platform == RuntimePlatform.LinuxEditor;

	/// <summary>
	/// The absolute path of the video to play for <paramref name="file"/> in a package, preferring
	/// a WebM sibling. Returns null when nothing playable is there.
	/// </summary>
	internal static string ResolvePath(string packageDirectory, string file)
	{
		if (string.IsNullOrWhiteSpace(packageDirectory) || string.IsNullOrWhiteSpace(file))
		{
			return null;
		}
		string declared;
		string root;
		try
		{
			root = Path.GetFullPath(packageDirectory).TrimEnd(Path.DirectorySeparatorChar)
				+ Path.DirectorySeparatorChar;
			declared = Path.GetFullPath(Path.Combine(packageDirectory, file));
		}
		catch (Exception ex)
		{
			Plugin.DBG("VIDEO", "bad package video path '" + file + "': " + ex.Message);
			return null;
		}
		// A package may not name a file outside its own directory. The sprite-sheet loader has
		// always checked this and this one did not, which made it the one way in - and it is the
		// loader a package's own code reaches through `PackageMedia.ResolveVideoUrl`.
		if (!declared.StartsWith(root, StringComparison.OrdinalIgnoreCase))
		{
			WarnOnce(declared, "package video '" + file + "' is outside its package directory");
			return null;
		}

		string webm = Path.ChangeExtension(declared, ".webm");
		if (File.Exists(webm))
		{
			return webm;
		}
		if (!File.Exists(declared))
		{
			WarnOnce(declared, "package video is missing: " + declared);
			return null;
		}
		if (NeedsWebm && !declared.EndsWith(".webm", StringComparison.OrdinalIgnoreCase))
		{
			WarnOnce(declared,
				"'" + Path.GetFileName(declared) + "' cannot play on Linux - Unity's VideoPlayer has no "
				+ "H.264 decoder outside Windows and macOS, and there is no WebM beside it. The overlay "
				+ "will be blank. Convert it with: python3 code/webmify.py \"" + packageDirectory + "\"");
		}
		return declared;
	}

	/// <summary>The same, as the `file://` URL `VideoPlayer.url` wants. Null when nothing is playable.</summary>
	internal static string ResolveUrl(string packageDirectory, string file)
	{
		string path = ResolvePath(packageDirectory, file);
		return path == null ? null : new Uri(path).AbsoluteUri;
	}

	private static void WarnOnce(string key, string message)
	{
		if (Warned.Add(key))
		{
			Plugin.DBG("VIDEO", message);
		}
	}
}
