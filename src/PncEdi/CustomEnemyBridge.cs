using System;
using System.Collections.Generic;
using UnityEngine;

namespace PncEdi;

/// <summary>
/// The whole seam between this mod and the custom-enemy plugin, and the reason that plugin can be
/// a separate DLL at all.
///
/// The dependency runs one way: `PncCustomEnemies` references `PncEdi` (it plays rows, registers
/// gallery names, takes heat locks - it is Edi content), and `PncEdi` must not reference it back or
/// neither could be built without the other. But a handful of core decisions genuinely need to know
/// whether a package is involved: the shuffle pool has to draw custom enemies, the escape gate has
/// to honour a package's own scene length, and the filler must not take the channel back while a
/// charm circle is playing.
///
/// So those questions are asked through delegates that the custom-enemy plugin installs in its own
/// Awake, and every one of them has an answer for "nothing installed them" - which is exactly what
/// a player who deleted `PncCustomEnemies.dll` gets. Read every accessor here as "and if that mod
/// is not loaded, the vanilla answer".
///
/// <see cref="NameRemap.CustomEnemyResolver"/> is the same idea and predates this file; it stays
/// where it is because naming is resolved before anything here exists.
/// </summary>
public static class CustomEnemyBridge
{
	public static Func<EnemyGalleryEntry, bool> GalleryPlaybackAdmissionTest;
	public static bool CanPlayGalleryEntry(EnemyGalleryEntry entry)
		=> entry != null && (GalleryPlaybackAdmissionTest?.Invoke(entry) ?? true);
	/// <summary>True only for a registered package-owned object whose lifecycle
	/// admission has closed. Explicit teardown outranks grab keep-alive.</summary>
	public static Func<UnityEngine.Object,bool> PackageTeardownTest;
	public static bool IsPackageTeardown(UnityEngine.Object target) => PackageTeardownTest?.Invoke(target) ?? false;
	/// <summary>Package key -> its spawn weight, where 1 is one ordinary enemy's share.</summary>
	public static Func<string, float> SpawnWeightResolver;

	/// <summary>Is this shuffle-pool key a custom package rather than a vanilla enemy?</summary>
	public static Func<string, bool> CustomKeyTest;

	/// <summary>Every package that wants to be drawn from the shuffle pool: key -> template.</summary>
	public static Func<IEnumerable<KeyValuePair<string, GameObject>>> ShufflePoolSource;

	/// <summary>
	/// A package's own minimum scene length for the enemy currently on screen, or a negative
	/// number when the scene is not one of theirs.
	/// </summary>
	public static Func<GameObject, float> SceneMinimumSecondsResolver;

	/// <summary>
	/// True when this enemy *is* the scene rather than being in front of it - a wall-picture trap,
	/// whose artwork is what the player is looking at, so hiding it would hide the scene.
	/// </summary>
	public static Func<GameObject, bool> SceneVisualOwnerTest;

	/// <summary>
	/// True when this "enemy" is a package driving the vanilla grab screen itself rather than an
	/// enemy the game grabbed the player with. A package captures the player by calling
	/// `GrabScreen.StartGrab(gameObject, null, ...)` and then owns everything inside that screen -
	/// its own artwork, its own sound, and its own Edi rows, dispatched per stage. The mod's grab
	/// pipeline has nothing correct to contribute to such a scene and several things to break in
	/// it, so it stands down instead.
	/// </summary>
	public static Func<GameObject, bool> GrabSceneOwnerTest;

	/// <summary>
	/// True while a package is playing its own row and nothing else may take the channel - the
	/// charm circle, which plays for as long as the player stands in it rather than for a scene.
	/// </summary>
	public static Func<bool> EdiChannelHeldTest;

	/// <summary>
	/// The gallery row a package declares for one of its own scenes, given the gallery entry the
	/// menu is showing and the animation name it just stepped to - or null when this is not a
	/// package's entry, or is one it has no row for.
	///
	/// This exists because the gallery viewer is the one dispatch path that cannot name a row by
	/// itself. Everywhere else the mod resolves an animator state, which a package registers; in
	/// the menu it has only a display name and an animation label, and slugging those gave
	/// `joker_massage` for a package whose row is `joker_wall_massage` (§138). A package's manifest
	/// is the only thing that knows the answer, so it is asked.
	/// </summary>
	public static Func<EnemyGalleryEntry, string, string> GalleryRowResolver;

	private static readonly KeyValuePair<string, GameObject>[] NoPool = Array.Empty<KeyValuePair<string, GameObject>>();

	public static float SpawnWeight(string key) => SpawnWeightResolver?.Invoke(key) ?? 1f;

	public static bool IsCustomKey(string key) => CustomKeyTest?.Invoke(key) ?? false;

	public static IEnumerable<KeyValuePair<string, GameObject>> ShufflePool() => ShufflePoolSource?.Invoke() ?? NoPool;

	public static float SceneMinimumSeconds(GameObject enemy) => enemy == null ? -1f : (SceneMinimumSecondsResolver?.Invoke(enemy) ?? -1f);

	public static bool OwnsSceneVisual(GameObject enemy) => enemy != null && (SceneVisualOwnerTest?.Invoke(enemy) ?? false);

	public static bool OwnsGrabScene(GameObject enemy) => enemy != null && (GrabSceneOwnerTest?.Invoke(enemy) ?? false);

	public static bool EdiChannelHeld => EdiChannelHeldTest?.Invoke() ?? false;

	public static string GalleryRow(EnemyGalleryEntry entry, string animationName)
	{
		if (entry == null || string.IsNullOrWhiteSpace(animationName))
		{
			return null;
		}
		string row = GalleryRowResolver?.Invoke(entry, animationName);
		return string.IsNullOrWhiteSpace(row) ? null : row.Trim();
	}
}
