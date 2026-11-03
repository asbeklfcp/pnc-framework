using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace PncCustomEnemies.Api;

/// <summary>
/// The public surface a custom-enemy package's own assembly compiles against.
///
/// Everything in this file is API the moment a third-party package references it, which is why it
/// is the only public surface this plugin has and why <see cref="PackageApi.Version"/> exists: a
/// package declares the version it was built against and the loader refuses a mismatch rather than
/// letting a renamed member surface as a MissingMethodException in the middle of a run.
///
/// The framework asks its questions through the interfaces below rather than naming a behaviour's
/// type, so a package's code is reachable without the loader knowing anything about it. That is the
/// whole point of the seam (§165): before it, "a charm-circle boss" and "a wall-picture trap" were
/// two compiled-in behaviours, and every new enemy that wanted anything else meant growing the
/// manifest vocabulary until packages stopped needing new behaviours - an endless chase, rejected
/// in §164.
/// </summary>
public static class PackageApi
{
	/// <summary>
	/// The API version a package's manifest must name in its `assembly` block. Bump it whenever
	/// anything in this file changes shape; the loader refuses a package built against another
	/// number, with a log line saying which side is behind.
	/// </summary>
	public const int Version = 3;
}

/// <summary>
/// The entry point of a package's assembly: one type named by the manifest's
/// `"assembly": { "module": "Ns.Type" }`, with a public parameterless constructor.
///
/// It is constructed and initialised once, during the plugin's own Awake and before any package
/// content is prepared, so a module may register gallery entries, patch the game and bind config
/// entries in the same window the framework does.
/// </summary>
public interface IPackageModule
{
	void Initialize(PackageContext context);
}

/// <summary>Release package subscriptions and non-object resources. API 3 modules
/// implement this; the loader closes admission and ends owned captures first.</summary>
public interface IPackageLifecycle
{
	void Deactivate();
}

/// <summary>
/// What a module is handed: its own identity and directory, its own manifest text, and the few
/// framework services it cannot get to on its own.
///
/// Note what is *not* here: any way to reach another package. A module gets its own package and
/// the game, and that is the whole of it.
/// </summary>
public sealed class PackageContext
{
	/// <summary>The package's id - its manifest's `id`, or its directory name where the manifest has none.</summary>
	public string Id { get; internal set; }

	/// <summary>The package's own directory, `BepInEx/custom-enemies/&lt;name&gt;`. Every file a package names is resolved against it.</summary>
	public string Directory { get; internal set; }

	/// <summary>The raw text of the manifest that declared this module, so a package parses its own vocabulary rather than the framework guessing at it.</summary>
	public string ManifestJson { get; internal set; }

	/// <summary>The manifest's file name (`enemy.json`, `wall-trap.json`, whatever a package invents).</summary>
	public string ManifestFileName { get; internal set; }

	/// <summary>This plugin's log source. A module's own lines are prefixed by the framework with the package id.</summary>
	public ManualLogSource Log { get; internal set; }

	/// <summary>`com.edi.pnc.customenemies.cfg`, shared with the framework and every other package. Bind under a section named for the package.</summary>
	public ConfigFile Config { get; internal set; }

	/// <summary>
	/// A loader-owned `DontDestroyOnLoad` host. Add components here for package
	/// coroutines; the shared lifecycle stops and removes it when disabled.
	/// </summary>
	public GameObject Host { get; internal set; }

	/// <summary>Whether the player has this package switched on. Read it, do not cache it: the switch moves mid-run from the mod manager.</summary>
	public Func<bool> IsEnabled { get; internal set; }

	/// <summary>
	/// That same switch as a config entry, for a module that wants to *react* when it moves rather
	/// than poll - removing what it placed in the level, say. Null for a package whose switch the
	/// framework binds elsewhere (an `enemy.json` package: the enemy registry owns that one). The
	/// framework binds this before any package code runs, so a blocked package still has a switch
	/// a player can find (§166).
	/// </summary>
	public ConfigEntry<bool> EnabledEntry { get; internal set; }

	/// <summary>
	/// Called by the framework once the package's enemy template exists, for a package whose
	/// manifest is an `enemy.json` with a `baseEnemy`. A module attaches its behaviour here. Never
	/// raised for a package that has no template - a wall trap, or anything else that is not a
	/// clone of a vanilla enemy.
	/// </summary>
	public event Action<GameObject> TemplatePrepared;

	internal void RaiseTemplatePrepared(GameObject template)
	{
		TemplatePrepared?.Invoke(template);
	}

	/// <summary>
	/// Publish a behaviour under a name, so **any** package's `enemy.json` can select it with
	/// `"behaviour": "&lt;name&gt;"` and a tuning block, with no code of its own.
	///
	/// This is what keeps the no-code path whole. The framework's own vocabulary is a reskin of a
	/// vanilla enemy - sprites, scenes, gallery rows, fields - and everything past that used to mean
	/// one of two behaviours compiled into the framework. Moving those behaviours into the packages
	/// that use them would have quietly taken "a charm-circle boss with my own art, written in
	/// JSON" away from every author who is not a programmer. A registered behaviour is installed
	/// once and available to every data-only package after it.
	/// </summary>
	public Action<string, IPackageBehaviourFactory> RegisterBehaviour { get; internal set; }
}

/// <summary>
/// A behaviour published under a name by one package and attachable by any package's manifest.
/// Attach is called once per consuming package, on that package's own enemy template, before any
/// clone of it is spawned.
/// </summary>
public interface IPackageBehaviourFactory
{
	void Attach(GameObject template, PackageBehaviourRequest request);
}

/// <summary>Everything a behaviour is told about the package that asked for it - which is never the package that published the behaviour.</summary>
public sealed class PackageBehaviourRequest
{
	/// <summary>The requesting package's id.</summary>
	public string Id { get; internal set; }

	/// <summary>The requesting package's directory. Every file the settings name is resolved against this, not against the behaviour's own package.</summary>
	public string Directory { get; internal set; }

	/// <summary>The manifest object named by the behaviour, or null when the manifest has none. A behaviour that keeps an older block name reads <see cref="ManifestJson"/> instead.</summary>
	public string SettingsJson { get; internal set; }

	/// <summary>The requesting manifest in full, so a behaviour parses its own vocabulary out of it rather than the framework guessing at the shape.</summary>
	public string ManifestJson { get; internal set; }

	/// <summary>The plugin's log source.</summary>
	public ManualLogSource Log { get; internal set; }
}

/// <summary>
/// Implemented by a MonoBehaviour a package puts on an enemy it owns. The framework asks these of
/// the object rather than testing for a type it knows, which is what lets `CustomEnemyBridge`
/// answer PncEdi's questions about a package whose code the framework has never seen.
///
/// Unity's `GetComponent&lt;T&gt;` resolves interfaces, so a package implements this on whatever
/// component it already has; there is nothing to register.
/// </summary>
public interface IPackageSceneOwner
{
	/// <summary>How long this package's own scene runs before the escape gate will end it. Negative means "not one of ours - apply the mod's configured delay".</summary>
	float MinimumSceneSeconds { get; }

	/// <summary>True while this component's own capture is what vanilla's `GrabScreen` is showing, so PncEdi does not read it as a goonshroom grab.</summary>
	bool OwnsGrabScene { get; }

	/// <summary>True when the package draws the scene's visuals itself and the mod's vanilla art handling must stand down.</summary>
	bool OwnsSceneVisual { get; }
}

/// <summary>
/// Implemented by a package component that holds the Edi channel while no scene is playing - an
/// aura, a proximity effect, anything that owns the device between grabs. A component answering
/// true keeps the mod's filler off the channel.
///
/// Register with <see cref="PackageRuntime"/> while live; the framework asks the registered set
/// several times a second and never scans for you.
/// </summary>
public interface IPackageEdiChannelOwner
{
	bool HoldsEdiChannel { get; }
}

/// <summary>Framework services a package's own components call at runtime, as opposed to what its module is handed once at load.</summary>
public static class PackageRuntime
{
	/// <summary>Track spawned roots, reinforcements, effects and detached media.
	/// All owned roots are removed when the package is disabled.</summary>
	public static void OwnObject(string packageId, GameObject root)
	{
		if(string.IsNullOrWhiteSpace(packageId)) throw new ArgumentException("Package ID required",nameof(packageId));
		if(root == null) return;
		var owner=root.GetComponent<PackageOwnedObject>() ?? root.AddComponent<PackageOwnedObject>();
		owner.SetOwner(packageId);
	}
	public static void RegisterCleanup(string packageId,string key,Action cleanup)
	{
		if(cleanup == null) throw new ArgumentNullException(nameof(cleanup));
		PackageLifecycle.Cleanup(packageId,key,cleanup);
	}
	public static bool IsPackageActive(string packageId) => PackageLifecycle.Active(packageId);
	public static string GetPackageStatus(string packageId) => PackageLifecycle.Status(packageId);
	/// <summary>Start being asked whether the Edi channel is held. Call from `OnEnable`; the framework drops a destroyed component by itself, but an unregister in `OnDisable` is the honest pair.</summary>
	public static void RegisterEdiChannelOwner(IPackageEdiChannelOwner owner)
	{
		PackageSceneOwners.Register(owner);
	}

	/// <summary>Stop being asked. Safe to call for something never registered.</summary>
	public static void UnregisterEdiChannelOwner(IPackageEdiChannelOwner owner)
	{
		PackageSceneOwners.Unregister(owner);
	}
}

/// <summary>
/// Implemented by a module whose package can be put into the level on demand, for the debug spawn
/// key.
///
/// **The framework can already spawn an `enemy.json` package itself** — it built the template, so
/// it knows what "spawn this" means. It knows nothing of the sort about a kind a package invented:
/// a wall trap is not spawned in front of the player, it is *placed* on the wall being aimed at,
/// and only the package's own code can do that. Before §181 the answer was for such a package to
/// bind a debug key of its own, which meant one key and one id selector for every enemy package and
/// a new key in a new config section for every package of any other kind.
///
/// So the framework keeps the key and the selector, and asks the package what spawning means. One
/// hotkey, one `Tools / SpawnCustomEnemyId`, every package kind.
/// </summary>
public interface IPackageDebugSpawn
{
	/// <summary>
	/// Put this package's content in the level, however that reads for this kind. Return false when
	/// nothing could be placed — no wall in range, no live base enemy to clone — so the framework
	/// can say so in the log rather than leaving the key looking dead. Never throw: the caller is a
	/// hotkey, and the whole debug surface is off in a normal install.
	/// </summary>
	bool DebugSpawn();
}

/// <summary>
/// A marker for a component a package built as a child of a cloned enemy. The base-enemy stripper
/// walks the clone tearing vanilla parts off it, and anything wearing this is kept.
/// </summary>
public interface IPackageOwnedVisual
{
}

/// <summary>
/// Implemented by a module that adds its own rows to the in-game gallery - a package kind that is
/// not an `enemy.json` clone and therefore has no gallery entry built for it.
/// </summary>
public interface IPackageGalleryProvider
{
	/// <summary>The gallery entries this package owns, in display order. Called every time the gallery list is rebuilt, so return the live set rather than a snapshot.</summary>
	IEnumerable<EnemyGalleryEntry> GalleryEntries { get; }

	/// <summary>The gallery row name for one of this package's entries at one animation label, or null when the entry is not this package's or has no row.</summary>
	string ResolveGalleryRow(EnemyGalleryEntry entry, string animationName);

	/// <summary>
	/// How to display one of this package's entries, or null when the entry is not this package's.
	/// The framework owns the viewer and drives it from what this describes, which is what lets a
	/// package's gallery keep working with that package's code switched off.
	/// </summary>
	PackageGalleryPresentation Describe(EnemyGalleryEntry entry);
}
