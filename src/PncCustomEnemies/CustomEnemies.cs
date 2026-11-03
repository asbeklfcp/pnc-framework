using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

using PncCustomEnemies;
using PncCustomEnemies.Api;

namespace PncEdi;

[Serializable]
public sealed class CustomEnemyManifest
{
	public string id;
	public string displayName;
	public string description = "A custom enemy.";
	public bool enabled = true;
	public bool alwaysUnlocked = false;
	public bool includeInRandomSpawns = true;
	public float spawnWeight = 1f;
	public string baseEnemy;
	public bool stripBaseEnemy;
	public string assetBundle;
	public string prefab;
	public string galleryPrefab;
	public string grabGalleryPrefab;
	public string galleryController;
	public string grabController;
	public string icon;
	public string[] galleryAnimations = Array.Empty<string>();
	public CustomEnemySpriteVisual spriteVisual;
	public CustomEnemyGalleryVideos galleryVideos;
	// The name of a behaviour some installed package published, e.g. "charm-witch". The framework
	// neither knows nor cares what one does: it looks the name up in PackageBehaviours and hands
	// over the template plus this manifest's block of the same name. A manifest naming a behaviour
	// nothing provides still loads as a reskin, and the log says so.
	public string behaviour;
	public CustomEnemyScene[] scenes = Array.Empty<CustomEnemyScene>();
	public CustomEnemyField[] fields = Array.Empty<CustomEnemyField>();
	public string[] nameAliases = Array.Empty<string>();
}

/// <summary>
/// Videos the custom gallery plays for this package, and the rows they send.
///
/// This is framework vocabulary rather than a behaviour's, because it has to work with a package's
/// code switched off: the videos are media, like the sprite sheets, and a player who never allows a
/// package to run its code still gets its gallery. A behaviour that shows the same clips in-game
/// reads this same block out of the manifest rather than the manifest listing them twice (§165 -
/// before it, these four fields lived in the compiled-in `witch` block and no other package could
/// have gallery videos at all).
/// </summary>
[Serializable]
public sealed class CustomEnemyGalleryVideos
{
	public string[] files = Array.Empty<string>();
	public string[] labels = Array.Empty<string>();
	public float volume = 1f;
	/// <summary>The gallery row every video sends, unless it is the last and <see cref="lastGallery"/> names another.</summary>
	public string gallery;
	/// <summary>The row the final video sends - the scene a package usually ends on.</summary>
	public string lastGallery;
}

[Serializable]
public sealed class CustomEnemySpriteVisual
{
	public string renderer;
	public float pixelsPerUnit = 100f;
	public string pivot = "0.5,0";
	public string offset = "0,0,0";
	public string scale = "1,1,1";
	public bool hideOriginalRenderers = true;
	public bool continuous;
	public string defaultAnimation = "idle";
	public CustomEnemySpriteAnimation[] animations = Array.Empty<CustomEnemySpriteAnimation>();
}

[Serializable]
public sealed class CustomEnemySpriteAnimation
{
	public string name;
	public string file;
	public float fps = 12f;
	public bool loop = true;
	public int columns = 1;
	public int rows = 1;
	public int frameCount;
	public string[] aliases = Array.Empty<string>();
}

[Serializable]
public sealed class CustomEnemyScene
{
	public string animation;
	public string gallery;
	public string file;
	public string sound;
	public int startTime;
	public int endTime;
	public bool oneShot;
	public string[] aliases = Array.Empty<string>();
}

[Serializable]
public sealed class CustomEnemyField
{
	public string component;
	public string field;
	public string value;
}

internal sealed class CustomEnemyDefinition
{
	internal CustomEnemyManifest Manifest;
	/// <summary>The manifest as it was written. A package's behaviour parses its own vocabulary out of this, which the framework never learns.</summary>
	internal string ManifestJson;
	internal string Directory;
	internal AssetBundle Bundle;
	internal GameObject Template;
	internal GameObject GalleryPrefab;
	internal GameObject GrabGalleryPrefab;
	internal EnemyData EnemyData;
	internal EnemyGalleryEntry GalleryEntry;
	internal ConfigEntry<bool> EnabledEntry;
	internal ConfigEntry<float> SpawnWeightEntry;
	internal bool Failed;

	internal string Id => Manifest.id;
	internal bool Enabled => (EnabledEntry?.Value ?? Manifest.enabled) && PncCustomEnemies.PackageLifecycle.Active(Id);
	internal bool IncludeInRandomSpawns => Manifest.includeInRandomSpawns;
	// The config wins over the manifest: retuning how often a package turns up is a player's
	// decision, and a package is usually third-party content nobody should have to edit to make
	// it rarer. It sits beside that package's own on/off switch rather than in a central table,
	// so an author ships a default in the manifest and everything else about it is where a player
	// already looks. Both spawn paths read this one property.
	internal float SpawnWeight => Mathf.Max(0.01f, SpawnWeightEntry?.Value ?? Manifest.spawnWeight);
}

internal static class CustomEnemyRegistry
{
	private static readonly List<CustomEnemyDefinition> Enemies = new List<CustomEnemyDefinition>();
	private static readonly Dictionary<string, string> EnemyKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
	private static readonly Regex SafeId = new Regex("^[a-z0-9][a-z0-9_-]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
	private static readonly Regex FunscriptAt = new Regex("\\\"at\\\"\\s*:\\s*(\\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
	private static bool _initialized;

	internal static IReadOnlyList<CustomEnemyDefinition> LoadedEnemies
	{
		get
		{
			ResolvePending();
			return Enemies;
		}
	}

	internal static void Initialize()
	{
		if (_initialized)
		{
			return;
		}
		_initialized = true;
		// NameRemap asks through a delegate rather than calling here directly, so that file stays
		// compilable by `code/tests` without this one behind it. See NameRemap.CustomEnemyResolver.
		NameRemap.CustomEnemyResolver = name => TryResolveEnemyKey(name, out string key) ? key : null;
		string root = Path.Combine(Paths.BepInExRootPath, "custom-enemies");
		Directory.CreateDirectory(root);
		string[] manifests = Directory.GetFiles(root, "enemy.json", SearchOption.AllDirectories);
		Array.Sort(manifests, StringComparer.OrdinalIgnoreCase);
		foreach (string manifestPath in manifests)
		{
			// `_example/` holds the format's templates and is skipped by name, the same way
			// PackageGalleryImport and PackageAssemblies skip it. Their globs are `*.json` and would
			// not match a template anyway, since every file in there ends `.example`; this one asks
			// for `enemy.json` exactly and recurses, so it is the path that would load a template
			// somebody renamed in place - a package the other two would then refuse to give a
			// gallery or a config switch. Skipping here is what keeps the three answers the same.
			if (IsExampleTemplate(root, manifestPath))
			{
				continue;
			}
			LoadManifest(manifestPath);
		}
		ResolvePending();
		Plugin.Log?.LogInfo("[CustomEnemies] loaded " + Enemies.Count + " package(s) from " + root);
	}

	/// <summary>True for a manifest under the `_example/` template directory, at any depth below <paramref name="root"/>.</summary>
	private static bool IsExampleTemplate(string root, string manifestPath)
	{
		string stop = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
		for (string dir = Path.GetDirectoryName(Path.GetFullPath(manifestPath));
			 !string.IsNullOrEmpty(dir) && !string.Equals(dir.TrimEnd(Path.DirectorySeparatorChar), stop, StringComparison.OrdinalIgnoreCase);
			 dir = Path.GetDirectoryName(dir))
		{
			if (string.Equals(Path.GetFileName(dir), "_example", StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}
		return false;
	}

	private static void LoadManifest(string path)
	{
		try
		{
			string json = File.ReadAllText(path);
			CustomEnemyManifest manifest = JsonUtility.FromJson<CustomEnemyManifest>(json);
			if (manifest == null)
			{
				return;
			}
			HydrateNestedManifestFields(manifest, json);
			manifest.id = (manifest.id ?? "").Trim().ToLowerInvariant();
			if (!SafeId.IsMatch(manifest.id))
			{
				throw new InvalidDataException("id must use only letters, numbers, '-' or '_' and cannot be empty");
			}
			if (Enemies.Exists(x => x.Id.Equals(manifest.id, StringComparison.OrdinalIgnoreCase)))
			{
				throw new InvalidDataException("duplicate id '" + manifest.id + "'");
			}
			if (string.IsNullOrWhiteSpace(manifest.displayName))
			{
				manifest.displayName = manifest.id;
			}
			CustomEnemyDefinition definition = new CustomEnemyDefinition
			{
				Manifest = manifest,
				ManifestJson = json,
				Directory = Path.GetDirectoryName(path)
			};
			// A package that ships code already has this switch, bound by PackageAssemblies before
			// anything loaded, because the switch *is* the consent (§167) and the loader needed to
			// read it. Binding a second one here would be a different default over the same key.
			definition.EnabledEntry = PackageAssemblies.EnabledEntryFor(manifest.id)
				?? CustomEnemyPlugin.Instance.Config.Bind(
					"Custom Enemies",
					manifest.id,
					manifest.enabled,
					"Enable " + manifest.displayName + ". Disabled enemies are removed from future random spawns and the custom gallery; enemies already alive remain until the scene changes.");
			// **A code package that is switched off runs none of itself, and still hands Edi its
			// funscripts** (§170, narrowed in §173).
			//
			// Nothing below this point happens for it: no sprite sheets, no base-enemy strip, no
			// prefab, no gallery entry, no spawn-table share, no behaviour. §167 put the consent on
			// this switch because BepInEx cannot sandbox a plugin, and loading a refused package's
			// code or content is that decision taken for the player. Until §170 all of it ran and
			// the player just could not see it.
			//
			// The gallery is the exception, and it is not a compromise on consent - it is what the
			// switch costs if the exception is not made. **Edi reads `Definitions.csv` and the
			// variant folders once, at its own startup.** A player leaves Edi running and plays; if
			// a package's rows arrive only when it is switched on, then turning one on means
			// restarting the game *and* restarting Edi, or re-saving its settings to force a
			// reload - and until they do, the package is on, its scenes play, and the device is
			// silent. A funscript is inert data in a file Edi parses, not something the package
			// gets to run, so importing it costs the player nothing they refused. What they refused
			// was the code, and the code stays refused.
			//
			// Only code packages take this path at all. A data-only package loads normally whatever
			// its switch says, because its switch takes effect live - `ApplyEnabledState` filters
			// spawns and the gallery on the spot - which its own description promises.
			PackageLifecycle.Bind(manifest.id,definition.EnabledEntry);
			ConfigEntry<bool> codeSwitch = PackageAssemblies.EnabledEntryFor(manifest.id);
			if (codeSwitch != null && (!codeSwitch.Value || !PackageAssemblies.IsLoaded(manifest.id)))
			{
				// PackageAssemblies has already named this package and said why it is inert; a
				// second line here would only repeat it. Its funscripts and rows are already
				// installed - PackageGalleryImport ran before any of this, for every package of
				// every kind, on or off (§175).
				return;
			}
			definition.EnabledEntry.SettingChanged += (_, __) => ApplyEnabledState(definition);
			definition.SpawnWeightEntry = CustomEnemyPlugin.Instance.Config.Bind(
				"Custom Enemies",
				manifest.id + " spawn weight",
				manifest.spawnWeight,
				"How often " + manifest.displayName + " turns up, where 1 is one ordinary enemy's share of a room's spawn table: 0.5 is half as likely as a zombie, 2 is twice. Overrides the package's own spawnWeight. Takes effect in the next room that spawns.");
			definition.SpawnWeightEntry.SettingChanged += (_, __) => ReinjectSpawners();
			Enemies.Add(definition);
			RegisterEnemyKey(manifest.id, manifest.id);
			RegisterEnemyKey(manifest.displayName, manifest.id);
			RegisterEnemyKey(manifest.prefab, manifest.id);
			if (manifest.nameAliases != null)
			{
				foreach (string alias in manifest.nameAliases)
				{
					RegisterEnemyKey(alias, manifest.id);
				}
			}
			LoadBundle(definition);
			CustomEnemyRuntimeData.For(definition);
			CreateGalleryAssets(definition);
			CreateGalleryEntry(definition);
			RegisterScenes(definition);
		}
		catch (Exception ex)
		{
			Plugin.Log?.LogError("[CustomEnemies] " + path + ": " + ex.Message);
		}
	}

	private static void HydrateNestedManifestFields(CustomEnemyManifest manifest, string json)
	{
		// Unity 6 JsonUtility may leave arrays and nested plugin-defined objects at their
		// field initializers. Parse each child object independently as a reliable fallback.
		if (manifest.fields == null || manifest.fields.Length == 0)
			manifest.fields = ParseObjectArray<CustomEnemyField>(json, "fields");
		if (manifest.scenes == null || manifest.scenes.Length == 0)
			manifest.scenes = ParseScenes(json);
		if (manifest.nameAliases == null || manifest.nameAliases.Length == 0)
			manifest.nameAliases = ParseStringArray(json, "nameAliases");
		if (manifest.galleryAnimations == null || manifest.galleryAnimations.Length == 0)
			manifest.galleryAnimations = ParseStringArray(json, "galleryAnimations");

		string spriteJson = ExtractObject(json, "spriteVisual");
		if (manifest.spriteVisual == null && spriteJson != null)
			manifest.spriteVisual = JsonUtility.FromJson<CustomEnemySpriteVisual>(spriteJson);
		if (manifest.spriteVisual != null && (manifest.spriteVisual.animations == null || manifest.spriteVisual.animations.Length == 0))
			manifest.spriteVisual.animations = ParseSpriteAnimations(spriteJson);

		string videosJson = ExtractObject(json, "galleryVideos");
		if (manifest.galleryVideos == null && videosJson != null)
			manifest.galleryVideos = JsonUtility.FromJson<CustomEnemyGalleryVideos>(videosJson);
		if (manifest.galleryVideos != null && (manifest.galleryVideos.files == null || manifest.galleryVideos.files.Length == 0))
			manifest.galleryVideos.files = ParseStringArray(videosJson, "files");
		if (manifest.galleryVideos != null && (manifest.galleryVideos.labels == null || manifest.galleryVideos.labels.Length == 0))
			manifest.galleryVideos.labels = ParseStringArray(videosJson, "labels");
	}

	/// <summary>
	/// What one gallery video is called in the viewer's step list: the package's own label where it
	/// gave one, and `Video n` where it did not. The label is also what the gallery asks
	/// `GalleryRowResolver` about, so a package that renames one renames what its row is looked up
	/// by (§138).
	/// </summary>
	internal static string GalleryVideoLabel(CustomEnemyGalleryVideos videos, int index)
	{
		string[] labels = videos?.labels;
		if (labels != null && index >= 0 && index < labels.Length && !string.IsNullOrWhiteSpace(labels[index]))
		{
			return labels[index].Trim();
		}
		return "Video " + (index + 1);
	}

	/// <summary>The gallery row one video sends: the package's `lastGallery` for the final one where it named it, otherwise `gallery`.</summary>
	internal static string GalleryVideoRow(CustomEnemyGalleryVideos videos, int index)
	{
		if (videos == null || videos.files == null || videos.files.Length == 0)
		{
			return null;
		}
		bool last = index == videos.files.Length - 1;
		return last && !string.IsNullOrWhiteSpace(videos.lastGallery) ? videos.lastGallery : videos.gallery;
	}

	/// <summary>
	/// The manifest block a named behaviour is tuned by: the object named after the behaviour
	/// itself. Null is a legitimate answer - a behaviour with nothing to tune, or one that keeps an
	/// older block name and reads the whole manifest instead.
	/// </summary>
	internal static string BehaviourSettings(string json, string behaviour)
	{
		return string.IsNullOrWhiteSpace(behaviour) ? null : ExtractObject(json, behaviour.Trim());
	}

	private static CustomEnemySpriteAnimation[] ParseSpriteAnimations(string json)
	{
		List<CustomEnemySpriteAnimation> result = new List<CustomEnemySpriteAnimation>();
		foreach (string itemJson in ExtractObjectArrayItems(json, "animations"))
		{
			CustomEnemySpriteAnimation item = JsonUtility.FromJson<CustomEnemySpriteAnimation>(itemJson);
			if (item == null) continue;
			if (item.aliases == null || item.aliases.Length == 0) item.aliases = ParseStringArray(itemJson, "aliases");
			result.Add(item);
		}
		return result.ToArray();
	}

	/// <summary>
	/// The three manifest reads <see cref="PackageGalleryImport"/> needs, which is the only caller
	/// outside this class. They are exposed rather than duplicated because the importer builds the
	/// same rows this registry used to build alone, and two parsers of one manifest is exactly the
	/// drift §128 found between the two row builders.
	/// </summary>
	internal static CustomEnemyScene[] ParseScenesFor(string json) => ParseScenes(json);

	internal static PackageManifestStage[] ParseStagesFor(string json) => ParseObjectArray<PackageManifestStage>(json, "animations");

	/// <summary>A manifest's `id`, without deserialising a manifest kind this assembly may not have a type for.</summary>
	internal static string ReadManifestId(string json)
	{
		CustomEnemyManifest manifest = JsonUtility.FromJson<CustomEnemyManifest>(json);
		string id = (manifest?.id ?? "").Trim().ToLowerInvariant();
		return string.IsNullOrEmpty(id) ? null : id;
	}

	private static CustomEnemyScene[] ParseScenes(string json)
	{
		List<CustomEnemyScene> result = new List<CustomEnemyScene>();
		foreach (string itemJson in ExtractObjectArrayItems(json, "scenes"))
		{
			CustomEnemyScene item = JsonUtility.FromJson<CustomEnemyScene>(itemJson);
			if (item == null) continue;
			if (item.aliases == null || item.aliases.Length == 0) item.aliases = ParseStringArray(itemJson, "aliases");
			result.Add(item);
		}
		return result.ToArray();
	}

	private static T[] ParseObjectArray<T>(string json, string field) where T : class
	{
		List<T> result = new List<T>();
		foreach (string itemJson in ExtractObjectArrayItems(json, field))
		{
			T item = JsonUtility.FromJson<T>(itemJson);
			if (item != null) result.Add(item);
		}
		return result.ToArray();
	}

	private static List<string> ExtractObjectArrayItems(string json, string field)
	{
		List<string> result = new List<string>();
		string array = ExtractArray(json, field);
		if (array == null) return result;
		bool inString = false;
		bool escaped = false;
		int depth = 0;
		int objectStart = -1;
		for (int i = 1; i < array.Length - 1; i++)
		{
			char c = array[i];
			if (inString)
			{
				if (escaped) escaped = false;
				else if (c == '\\') escaped = true;
				else if (c == '"') inString = false;
				continue;
			}
			if (c == '"') { inString = true; continue; }
			if (c == '{')
			{
				if (depth == 0) objectStart = i;
				depth++;
			}
			else if (c == '}')
			{
				depth--;
				if (depth == 0 && objectStart >= 0)
				{
					result.Add(array.Substring(objectStart, i - objectStart + 1));
					objectStart = -1;
				}
			}
		}
		return result;
	}

	[Serializable]
	private sealed class JsonStringValue
	{
		public string value = "";
	}

	private static string[] ParseStringArray(string json, string field)
	{
		List<string> result = new List<string>();
		string array = ExtractArray(json, field);
		if (array == null) return result.ToArray();
		for (int i = 1; i < array.Length - 1; i++)
		{
			if (array[i] != '"') continue;
			int start = i;
			bool escaped = false;
			for (i++; i < array.Length; i++)
			{
				char c = array[i];
				if (escaped) { escaped = false; continue; }
				if (c == '\\') { escaped = true; continue; }
				if (c != '"') continue;
				string quoted = array.Substring(start, i - start + 1);
				JsonStringValue parsed = JsonUtility.FromJson<JsonStringValue>("{\"value\":" + quoted + "}");
				if (parsed != null && parsed.value != null) result.Add(parsed.value);
				break;
			}
		}
		return result.ToArray();
	}

	private static string ExtractObject(string json, string field)
	{
		return ExtractDelimited(json, field, '{', '}');
	}

	private static string ExtractArray(string json, string field)
	{
		return ExtractDelimited(json, field, '[', ']');
	}

	private static string ExtractDelimited(string json, string field, char open, char close)
	{
		return PncCustomEnemies.ManifestJson.Delimited(json, field, open, close);
	}

	private static void RegisterEnemyKey(string name, string id)
	{
		if (!string.IsNullOrWhiteSpace(name))
		{
			EnemyKeys[NameRemap.StripCloneSuffix(name.Trim())] = id;
		}
	}

	internal static bool TryResolveEnemyKey(string rawName, out string key)
	{
		key = null;
		if (!_initialized || string.IsNullOrWhiteSpace(rawName))
		{
			return false;
		}
		string clean = NameRemap.StripCloneSuffix(rawName);
		if (EnemyKeys.TryGetValue(clean, out key))
		{
			return true;
		}
		int bestLength = -1;
		foreach (KeyValuePair<string, string> pair in EnemyKeys)
		{
			if (clean.IndexOf(pair.Key, StringComparison.OrdinalIgnoreCase) >= 0 && pair.Key.Length > bestLength)
			{
				bestLength = pair.Key.Length;
				key = pair.Value;
			}
		}
		return key != null;
	}

	/// <summary>
	/// Packages that want to be drawn from the shuffle pool, as key -> template. Only the ones
	/// that asked for it, and only once a template exists - a clone package has no prefab until
	/// its base enemy is in the level. PncEdi calls this through CustomEnemyBridge.
	/// </summary>
	internal static IEnumerable<KeyValuePair<string, GameObject>> ShufflePoolContributions()
	{
		ResolvePending();
		foreach (CustomEnemyDefinition definition in Enemies)
		{
			if (definition.Enabled && definition.IncludeInRandomSpawns && definition.Template != null)
			{
				yield return new KeyValuePair<string, GameObject>(definition.Id, definition.Template);
			}
		}
	}

	internal static bool IsCustomKey(string key)
	{
		return Enemies.Exists(x => x.Id.Equals(key, StringComparison.OrdinalIgnoreCase));
	}

	/// <summary>
	/// Whether a package is switched on, by id. A package that ships code asks this through its
	/// <see cref="Api.PackageContext.IsEnabled"/>; a package kind the framework does not know has no
	/// enemy definition here, and answers true rather than pretending to a switch it never bound.
	/// </summary>
	internal static bool IsPackageEnabled(string id)
	{
		CustomEnemyDefinition definition = Enemies.Find(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
		return definition == null || definition.Enabled;
	}

	internal static GameObject SpawnById(string id, string logLabel = null)
	{
		// An empty id is the shipped default, because the framework ships no packages and naming one
		// here would make a package the loader's own. With exactly one installed there is nothing to
		// choose between, so the debug key spawns it; with several, say which ones rather than pick.
		if (string.IsNullOrWhiteSpace(id))
		{
			IReadOnlyList<CustomEnemyDefinition> loaded = LoadedEnemies;
			if (loaded.Count == 1)
			{
				id = loaded[0].Id;
			}
			else
			{
				Plugin.DBG("SPAWN", "custom: SpawnCustomEnemyId is unset and " + loaded.Count + " package(s) are installed - set it to one of: " + DescribeLoadedIds());
				return null;
			}
		}
		string wanted = id.Trim();
		string label = logLabel ?? ("custom:" + wanted);
		CustomEnemyDefinition definition = null;
		foreach (CustomEnemyDefinition candidate in LoadedEnemies)
		{
			if (candidate.Id.Equals(wanted, StringComparison.OrdinalIgnoreCase))
			{
				definition = candidate;
				break;
			}
		}
		if (definition == null)
		{
			Plugin.DBG("SPAWN", label + ": no custom enemy package with id '" + wanted + "' (loaded: " + DescribeLoadedIds() + ")");
			return null;
		}
		if (!definition.Enabled)
		{
			Plugin.DBG("SPAWN", label + ": package is disabled; nothing spawned");
			return null;
		}
		if (definition.Failed || definition.Template == null)
		{
			Plugin.DBG("SPAWN", label + ": package '" + wanted + "' has no usable prefab yet (baseEnemy='" + definition.Manifest.baseEnemy + "')");
			return null;
		}
		GameObject spawned = DebugEnemySpawn.SpawnPrefabInstance(definition.Template, label);
		if (spawned != null && spawned.scene != SceneManager.GetActiveScene())
		{
			SceneManager.MoveGameObjectToScene(spawned, SceneManager.GetActiveScene());
		}
		return spawned;
	}

	private static string DescribeLoadedIds()
	{
		if (Enemies.Count == 0)
		{
			return "none";
		}
		StringBuilder builder = new StringBuilder();
		foreach (CustomEnemyDefinition definition in Enemies)
		{
			if (builder.Length > 0) builder.Append(", ");
			builder.Append(definition.Id);
		}
		return builder.ToString();
	}

	internal static float GetSpawnWeight(string key)
	{
		CustomEnemyDefinition definition = Enemies.Find(x => x.Id.Equals(key, StringComparison.OrdinalIgnoreCase));
		return definition?.SpawnWeight ?? 1f;
	}

	private static void LoadBundle(CustomEnemyDefinition definition)
	{
		if (string.IsNullOrWhiteSpace(definition.Manifest.assetBundle))
		{
			return;
		}
		string path = Path.GetFullPath(Path.Combine(definition.Directory, definition.Manifest.assetBundle));
		if (!File.Exists(path))
		{
			throw new FileNotFoundException("assetBundle was not found", path);
		}
		definition.Bundle = AssetBundle.LoadFromFile(path);
		if (definition.Bundle == null)
		{
			throw new InvalidDataException("Unity could not load AssetBundle '" + path + "'");
		}
		definition.Template = LoadAsset<GameObject>(definition.Bundle, definition.Manifest.prefab);
		definition.GalleryPrefab = LoadAsset<GameObject>(definition.Bundle, definition.Manifest.galleryPrefab);
		definition.GrabGalleryPrefab = LoadAsset<GameObject>(definition.Bundle, definition.Manifest.grabGalleryPrefab);
	}

	private static T LoadAsset<T>(AssetBundle bundle, string name) where T : UnityEngine.Object
	{
		if (bundle == null || string.IsNullOrWhiteSpace(name))
		{
			return null;
		}
		T direct = bundle.LoadAsset<T>(name);
		if (direct != null)
		{
			return direct;
		}
		foreach (string assetName in bundle.GetAllAssetNames())
		{
			string fileName = Path.GetFileNameWithoutExtension(assetName);
			if (fileName.Equals(name, StringComparison.OrdinalIgnoreCase) || assetName.Equals(name, StringComparison.OrdinalIgnoreCase))
			{
				return bundle.LoadAsset<T>(assetName);
			}
		}
		return null;
	}

	private static Material _cachedSpriteMaterial;

	internal static Material GetSpriteMaterial()
	{
		if (_cachedSpriteMaterial != null && _cachedSpriteMaterial.shader != null)
		{
			return _cachedSpriteMaterial;
		}
		Shader shader = Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default")
			?? Shader.Find("Universal Render Pipeline/Unlit")
			?? Shader.Find("Sprites/Default")
			?? Shader.Find("Unlit/Texture");
		if (shader == null)
		{
			foreach (SpriteRenderer r in Resources.FindObjectsOfTypeAll<SpriteRenderer>())
			{
				if (r != null && r.sharedMaterial != null && r.sharedMaterial.shader != null && !r.sharedMaterial.shader.name.StartsWith("Hidden/", StringComparison.OrdinalIgnoreCase))
				{
					shader = r.sharedMaterial.shader;
					break;
				}
			}
		}
		if (shader != null)
		{
			_cachedSpriteMaterial = new Material(shader);
			_cachedSpriteMaterial.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
			_cachedSpriteMaterial.SetFloat("_Cull", 0f);
			_cachedSpriteMaterial.color = Color.white;
			return _cachedSpriteMaterial;
		}
		return null;
	}

	private static void ResolvePending()
	{
		if (!_initialized)
		{
			return;
		}
		foreach (CustomEnemyDefinition definition in Enemies)
		{
			if (definition.Failed || !definition.Enabled)
			{
				continue;
			}
			try
			{
				if (definition.GalleryPrefab == null)
				{
					CreateGalleryAssets(definition);
				}
				if (definition.GalleryEntry == null)
				{
					CreateGalleryEntry(definition);
				}
				if (definition.Template == null && !string.IsNullOrWhiteSpace(definition.Manifest.baseEnemy))
				{
					GameObject source = DebugEnemySpawn.ResolvePrefabTemplate(definition.Manifest.baseEnemy, "custom:" + definition.Id);
					if (source != null)
					{
						definition.Template = UnityEngine.Object.Instantiate(source);
						definition.Template.name = "CustomEnemy_" + definition.Id;
						definition.Template.SetActive(false);
						UnityEngine.Object.DontDestroyOnLoad(definition.Template);
					}
				}
				if (definition.Template != null && definition.EnemyData == null)
				{
					PrepareTemplate(definition);
				}
			}
			catch (Exception ex)
			{
				definition.Failed = true;
				Plugin.Log?.LogError("[CustomEnemies] could not prepare '" + definition.Id + "': " + ex);
			}
		}
	}

	private static void PrepareTemplate(CustomEnemyDefinition definition)
	{
		GameObject template = definition.Template;
		template.name = "CustomEnemy_" + definition.Id;
		PackageRuntime.OwnObject(definition.Id,template);
		SetGalleryId(template, definition.Id);
		ApplyOverrides(template, definition.Manifest.fields);
		ResetBaseEnemyState(template);
		// Must exist before Attach runs: the sprite visual stores its loaded animation set here
		// and the witch behaviour stores its settings, because clones of the template cannot
		// carry those custom-class fields themselves (see CustomEnemyRuntimeData).
		CustomEnemyRuntimeData.For(definition);
		RuntimeSpriteVisual.Attach(template, definition.Directory, definition.Manifest.spriteVisual, definition.Id);
		BaseEnemyStripper.Apply(template, definition.Manifest.stripBaseEnemy, definition.Id);
		// A behaviour comes from a package, never from here. The name is looked up among what the
		// installed packages published; the tuning is this manifest's own block; and a package that
		// ships the behaviour it uses is just the case where those two are the same package.
		PackageBehaviours.Attach(
			definition.Manifest.behaviour,
			template,
			definition.Id,
			definition.Directory,
			BehaviourSettings(definition.ManifestJson, definition.Manifest.behaviour),
			definition.ManifestJson);
		PackageAssemblies.RaiseTemplatePrepared(definition.Id, template);
		CreateGalleryAssets(definition);
		definition.EnemyData = ScriptableObject.CreateInstance<EnemyData>();
		definition.EnemyData.name = "CustomEnemyData_" + definition.Id;
		definition.EnemyData.enemyName = definition.Manifest.displayName;
		definition.EnemyData.prefabVariants = new[] { template };
		RegisterEnemyKey(template.name, definition.Id);
		Plugin.Log?.LogInfo("[CustomEnemies] ready '" + definition.Id + "' using prefab '" + template.name + "'");
	}

	// The base enemy for a clone package is resolved from live scene content as well as prefab
	// assets, and resolution can happen right after the picked variant died in front of the
	// player. A dead source bakes isDead, a Dead AI state and empty health into the template, and
	// the game's pooled spawners instantiate clones without ever running the reactivation helper,
	// so every copy would stand there inert with its boss behaviour disabled. Reset the transient
	// state here so each clone starts as a fresh enemy no matter which source was copied.
	private static void ResetBaseEnemyState(GameObject root)
	{
		foreach (MonoBehaviour ai in EnemyAiTypes.On(root))
		{
			if (ai == null)
			{
				continue;
			}
			bool wasDead = Traverse.Create(ai).Field("isDead").GetValue<bool>();
			EnemyKeepAliveHelper.ClearDeadState(ai);
			Traverse traverse = Traverse.Create(ai);
			if (traverse.Field("currentHealth").FieldExists() && traverse.Field("maxHealth").FieldExists())
			{
				int maxHealth = traverse.Field("maxHealth").GetValue<int>();
				if (maxHealth > 0 && traverse.Field("currentHealth").GetValue<int>() <= 0)
				{
					traverse.Field("currentHealth").SetValue(maxHealth);
				}
			}
			if (wasDead)
			{
				Plugin.Log?.LogInfo("[CustomEnemies] template source was dead: reset " + ai.GetType().Name + " state for fresh spawns");
			}
		}
	}

	internal static void CreateGalleryAssets(CustomEnemyDefinition definition)
	{
		if (definition == null || definition.GalleryPrefab != null) return;
		if (definition.Manifest.spriteVisual != null)
		{
			definition.GalleryPrefab = CreateSafeGalleryPreview(definition);
			PackageRuntime.OwnObject(definition.Id,definition.GalleryPrefab);
		}
	}

	private static GameObject CreateSafeGalleryPreview(CustomEnemyDefinition definition)
	{
		GameObject preview = new GameObject("CustomGalleryPreview_" + definition.Id);
		preview.SetActive(false);
		SpriteRenderer renderer = preview.AddComponent<SpriteRenderer>();
		Material spriteMat = GetSpriteMaterial();
		if (spriteMat != null)
		{
			renderer.sharedMaterial = spriteMat;
		}
		renderer.color = Color.white;
		renderer.sortingOrder = 100;

		CustomEnemyRuntimeData data = CustomEnemyRuntimeData.Find(definition.Id);
		RuntimeSpriteAnimationData[] anims = data?.Animations;
		if (anims == null || anims.Length == 0)
		{
			try
			{
				List<RuntimeSpriteAnimationData> loaded = new List<RuntimeSpriteAnimationData>();
				foreach (CustomEnemySpriteAnimation animation in definition.Manifest.spriteVisual?.animations ?? Array.Empty<CustomEnemySpriteAnimation>())
				{
					if (animation != null && !string.IsNullOrWhiteSpace(animation.name) && !string.IsNullOrWhiteSpace(animation.file))
					{
						loaded.Add(RuntimeSpriteVisual.LoadAnimation(definition.Directory, definition.Manifest.spriteVisual, animation));
					}
				}
				anims = loaded.ToArray();
				if (data != null) data.Animations = anims;
			}
			catch (Exception ex)
			{
				Plugin.Log?.LogWarning("[CustomEnemies] could not load sprite animations for preview '" + definition.Id + "': " + ex.Message);
			}
		}

		if (anims != null && anims.Length > 0)
		{
			renderer.sprite = anims[0].Frames != null && anims[0].Frames.Length > 0 ? anims[0].Frames[0] : null;
			RuntimeSpriteVisual visual = preview.AddComponent<RuntimeSpriteVisual>();
			visual.InitializeGallery(renderer, anims, 0);
		}

		UnityEngine.Object.DontDestroyOnLoad(preview);
		return preview;
	}

	private static void SetGalleryId(GameObject root, string id)
	{
		foreach (MonoBehaviour behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
		{
			if (behaviour == null)
			{
				continue;
			}
			// Not AccessTools.Field: it logs a HarmonyX warning for every type that does not have the
			// field, and this asks every MonoBehaviour on the prefab - six warnings in the first
			// thirty lines of every log, in exactly the place a reader starts (§138). Reflection
			// answers the same question silently. BindingFlags include the base classes' public
			// fields, which is where `galleryEnemyID` lives on every vanilla AI.
			FieldInfo field = behaviour.GetType().GetField("galleryEnemyID",
				BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
			if (field != null && field.FieldType == typeof(string))
			{
				field.SetValue(behaviour, id);
			}
		}
	}

	private static void ApplyOverrides(GameObject root, CustomEnemyField[] overrides)
	{
		if (overrides == null)
		{
			return;
		}
		foreach (CustomEnemyField item in overrides)
		{
			if (item == null || string.IsNullOrWhiteSpace(item.component) || string.IsNullOrWhiteSpace(item.field))
			{
				continue;
			}
			bool applied = false;
			foreach (Component component in root.GetComponentsInChildren<Component>(true))
			{
				if (component == null || (!component.GetType().Name.Equals(item.component, StringComparison.OrdinalIgnoreCase) && !component.GetType().FullName.Equals(item.component, StringComparison.OrdinalIgnoreCase)))
				{
					continue;
				}
				FieldInfo field = AccessTools.Field(component.GetType(), item.field);
				if (field == null)
				{
					continue;
				}
				field.SetValue(component, ConvertValue(item.value, field.FieldType));
				applied = true;
			}
			if (!applied)
			{
				Plugin.Log?.LogWarning("[CustomEnemies] override not found: " + item.component + "." + item.field);
			}
		}
	}

	private static object ConvertValue(string raw, Type type)
	{
		if (type == typeof(string)) return raw ?? "";
		if (type == typeof(bool)) return bool.Parse(raw);
		if (type == typeof(int)) return int.Parse(raw, CultureInfo.InvariantCulture);
		if (type == typeof(float)) return float.Parse(raw, CultureInfo.InvariantCulture);
		if (type == typeof(double)) return double.Parse(raw, CultureInfo.InvariantCulture);
		if (type.IsEnum) return Enum.Parse(type, raw, true);
		if (type == typeof(Vector2) || type == typeof(Vector3))
		{
			string[] parts = (raw ?? "").Split(',');
			float x = parts.Length > 0 ? float.Parse(parts[0], CultureInfo.InvariantCulture) : 0f;
			float y = parts.Length > 1 ? float.Parse(parts[1], CultureInfo.InvariantCulture) : 0f;
			float z = parts.Length > 2 ? float.Parse(parts[2], CultureInfo.InvariantCulture) : 0f;
			return type == typeof(Vector2) ? (object)new Vector2(x, y) : new Vector3(x, y, z);
		}
		throw new NotSupportedException("field type " + type.FullName + " is not supported by manifest overrides");
	}

	private static void CreateGalleryEntry(CustomEnemyDefinition definition)
	{
		CustomEnemyManifest manifest = definition.Manifest;
		EnemyGalleryEntry entry = ScriptableObject.CreateInstance<EnemyGalleryEntry>();
		entry.name = "CustomGalleryEntry_" + definition.Id;
		entry.enemyID = definition.Id;
		entry.alwaysUnlocked = true;
		entry.enemyName = manifest.displayName;
		entry.enemyDescription = manifest.description;
		entry.animatorController = LoadAsset<RuntimeAnimatorController>(definition.Bundle, manifest.galleryController) ?? FindController(definition.Template, "runtimeAnimatorController");
		entry.grabAnimatorController = LoadAsset<RuntimeAnimatorController>(definition.Bundle, manifest.grabController) ?? FindController(definition.Template, "grabAnimatorController");
		entry.enemyIcon = LoadAsset<Sprite>(definition.Bundle, manifest.icon);
		if (entry.enemyIcon == null && manifest.spriteVisual != null && manifest.spriteVisual.animations != null && manifest.spriteVisual.animations.Length > 0)
		{
			CustomEnemyRuntimeData data = CustomEnemyRuntimeData.Find(definition.Id);
			if (data != null && data.Animations != null && data.Animations.Length > 0 && data.Animations[0].Frames != null && data.Animations[0].Frames.Length > 0)
			{
				entry.enemyIcon = data.Animations[0].Frames[0];
			}
			else
			{
				try
				{
					RuntimeSpriteAnimationData anim = RuntimeSpriteVisual.LoadAnimation(definition.Directory, manifest.spriteVisual, manifest.spriteVisual.animations[0]);
					if (anim != null && anim.Frames != null && anim.Frames.Length > 0)
					{
						entry.enemyIcon = anim.Frames[0];
					}
				}
				catch (Exception ex)
				{
					Plugin.Log?.LogWarning("[CustomEnemies] could not load sprite icon for '" + definition.Id + "': " + ex.Message);
				}
			}
		}
		entry.availableAnimations = (manifest.galleryAnimations != null && manifest.galleryAnimations.Length > 0) ? manifest.galleryAnimations : new[] { "Idle" };
		List<GrabAnimationData> grabAnimations = new List<GrabAnimationData>();
		string[] galleryVideos = manifest.galleryVideos?.files;
		if (galleryVideos != null && galleryVideos.Length > 0)
		{
			for (int i = 0; i < galleryVideos.Length; i++)
			{
				grabAnimations.Add(new GrabAnimationData { animationName = GalleryVideoLabel(manifest.galleryVideos, i) });
			}
		}
		else if (manifest.scenes != null)
		{
			foreach (CustomEnemyScene scene in manifest.scenes)
			{
				if (scene != null && !string.IsNullOrWhiteSpace(scene.animation))
				{
					grabAnimations.Add(new GrabAnimationData
					{
						animationName = scene.animation,
						sound = LoadAsset<AudioClip>(definition.Bundle, scene.sound)
					});
				}
			}
		}
		entry.grabAnimations = grabAnimations.ToArray();
		entry.hasGrabScene = entry.grabAnimations.Length > 0;
		definition.GalleryEntry = entry;
	}

	private static RuntimeAnimatorController FindController(GameObject root, string fieldName)
	{
		if (root == null)
		{
			return null;
		}
		if (fieldName == "runtimeAnimatorController")
		{
			Animator animator = root.GetComponentInChildren<Animator>(true);
			return animator?.runtimeAnimatorController;
		}
		foreach (MonoBehaviour behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
		{
			FieldInfo field = behaviour == null ? null : AccessTools.Field(behaviour.GetType(), fieldName);
			if (field != null && typeof(RuntimeAnimatorController).IsAssignableFrom(field.FieldType))
			{
				RuntimeAnimatorController controller = field.GetValue(behaviour) as RuntimeAnimatorController;
				if (controller != null) return controller;
			}
		}
		return null;
	}

	private static void RegisterScenes(CustomEnemyDefinition definition)
	{
		if (definition.Manifest.scenes == null)
		{
			return;
		}
		foreach (CustomEnemyScene scene in definition.Manifest.scenes)
		{
			if (scene == null || string.IsNullOrWhiteSpace(scene.animation))
			{
				continue;
			}
			string gallery = string.IsNullOrWhiteSpace(scene.gallery) ? definition.Id + "_" + NameRemap.Slug(scene.animation) : scene.gallery.Trim();
			GalleryRegistry.Register(gallery);
			GalleryAliases.Register(NameRemap.BuildGallerySlug(definition.Id, scene.animation), gallery);
			if (scene.aliases != null)
			{
				foreach (string alias in scene.aliases)
				{
					GalleryAliases.Register(alias, gallery);
				}
			}
		}
	}

	/// <summary>
	/// Merge one package's gallery rows into Definitions.csv. Every package kind goes through here,
	/// and since §175 every caller is <see cref="PackageGalleryImport"/> or a package's own code.
	///
	/// The wall-trap registry used to carry its own copy of this, and the copy had drifted: it
	/// overwrote any row whose name matched, and it wrote the file with a bare
	/// <c>File.WriteAllLines</c>. Either one can cost the user the gallery - the first by quietly
	/// retargeting a measured row at a package's funscript, the second by truncating a
	/// hundred-row file it may not finish rewriting. One writer, one rule, so a second package
	/// kind cannot reintroduce either (§128 found the drift; the row-building rules disagreeing is
	/// what `deploy.py --check` kept reporting as stale).
	///
	/// <paramref name="packageDirectory"/> is what decides ownership - see the comment below.
	/// </summary>
	internal static void MergeDefinitions(string tag, string id, string packageDirectory, string galleryRoot, List<string> newRows)
	{
		if (newRows == null || newRows.Count == 0) return;
		string csvPath = Path.Combine(galleryRoot, "Definitions.csv");
		List<string> lines = File.Exists(csvPath) ? new List<string>(File.ReadAllLines(csvPath)) : new List<string> { "Name,FileName,StartTime,EndTime,Type,Loop" };
		Dictionary<string, int> rows = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
		for (int i = 1; i < lines.Count; i++)
		{
			string[] columns = lines[i].Split(',');
			if (columns.Length > 0) rows[columns[0].Trim()] = i;
		}
		// Which rows this package is allowed to rewrite.
		//
		// Not "the ones we added this run" - that set is empty on every launch after the first, so
		// a manifest the author had edited could never update its own row. Ownership is read off
		// the file instead: a row whose FileName column names a funscript this package ships is
		// this package's row, whoever wrote it and whenever. Everything else in Definitions.csv is
		// the project's own gallery and is not the package's to touch.
		HashSet<string> shipped = ShippedScriptNames(packageDirectory);
		bool changed = false;
		foreach (string row in newRows)
		{
			string gallery = row.Split(',')[0];
			if (rows.TryGetValue(gallery, out int index))
			{
				// A name collision with an existing row is not an update, it is two different
				// scenes claiming one gallery name - and silently rewriting the other one would
				// retarget a real, measured row at a package's funscript for the rest of the
				// install's life. The package loses; its scene simply does not play, loudly.
				if (lines[index] == row)
				{
					// Already exactly right - the normal case, since deploy.py writes these rows
					// from the repo side too.
				}
				else if (OwnsRow(lines[index], shipped))
				{
					lines[index] = row;
					changed = true;
				}
				else
				{
					Plugin.Log?.LogWarning("[" + tag + "] '" + id + "' wants gallery row '"
						+ gallery + "', which Definitions.csv already defines differently - kept the "
						+ "existing row, so this scene will not play. Rename it in the manifest.");
				}
			}
			else
			{
				rows[gallery] = lines.Count;
				lines.Add(row);
				changed = true;
			}
		}
		if (changed)
		{
			WriteDefinitions(csvPath, lines);
			Plugin.Log?.LogInfo("[" + tag + "] updated Edi definitions for '" + id + "' (restart Edi if it was already running)");
		}
	}

	/// <summary>
	/// Replace Definitions.csv, without being able to destroy it.
	///
	/// This file is the whole gallery - a hundred-odd rows, each measured against a specific game
	/// asset - and a plain `File.WriteAllLines` truncates it the instant it opens the handle. A
	/// crash, a full disk or an exception between truncate and flush leaves the user with a
	/// shorter file and no way back. Two guards, both cheap:
	///
	/// - a one-time `.pre-custom-enemies` backup, written before the first modification and never
	///   overwritten after, so the pre-package file always exists;
	/// - write to a sibling temp file and move it into place, so the real file is only ever
	///   replaced by a complete one.
	///
	/// `File.Replace` is not used: it needs both paths on one volume and throws where the game
	/// directory is a symlink onto another, which is exactly this repo's layout.
	/// </summary>
	private static void WriteDefinitions(string csvPath, List<string> lines)
	{
		string backup = csvPath + ".pre-custom-enemies";
		if (File.Exists(csvPath) && !File.Exists(backup))
		{
			File.Copy(csvPath, backup);
			Plugin.Log?.LogInfo("[CustomEnemies] kept the pre-package gallery as " + Path.GetFileName(backup));
		}
		// Keep the byte-order mark the file arrived with. The repo's Definitions.csv has one, and
		// writing it back without changes the first three bytes of a file `deploy.py --check`
		// compares byte for byte - a permanent "stale" earned by touching nothing that matters.
		// `File.ReadAllLines` strips a BOM on the way in, so it has to be restored on the way out.
		string temp = csvPath + ".tmp";
		bool bom = HasByteOrderMark(csvPath);
		File.WriteAllLines(temp, lines, new UTF8Encoding(bom));
		if (File.Exists(csvPath))
		{
			File.Delete(csvPath);
		}
		File.Move(temp, csvPath);
	}

	private static bool HasByteOrderMark(string path)
	{
		try
		{
			if (!File.Exists(path)) return false;
			using (FileStream stream = File.OpenRead(path))
			{
				byte[] head = new byte[3];
				return stream.Read(head, 0, 3) == 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF;
			}
		}
		catch (IOException)
		{
			return false;
		}
	}

	/// <summary>Byte-identical? Unreadable counts as "not safe to replace".</summary>
	internal static bool SameFileContent(string left, string right)
	{
		try
		{
			byte[] a = File.ReadAllBytes(left);
			byte[] b = File.ReadAllBytes(right);
			if (a.Length != b.Length)
			{
				return false;
			}
			for (int i = 0; i < a.Length; i++)
			{
				if (a[i] != b[i])
				{
					return false;
				}
			}
			return true;
		}
		catch (Exception)
		{
			// Unreadable means "do not assume it is safe to replace".
			return false;
		}
	}

	/// <summary>Every funscript basename, without extension, that this package ships.</summary>
	private static HashSet<string> ShippedScriptNames(string packageDirectory)
	{
		HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		string root = Path.Combine(packageDirectory, "funscripts");
		if (!Directory.Exists(root))
		{
			return names;
		}
		foreach (string path in Directory.GetFiles(root, "*.funscript", SearchOption.AllDirectories))
		{
			names.Add(Path.GetFileNameWithoutExtension(path));
		}
		return names;
	}

	/// <summary>Does this Definitions.csv line point at a funscript the package ships?</summary>
	private static bool OwnsRow(string line, HashSet<string> shipped)
	{
		string[] columns = (line ?? "").Split(',');
		return columns.Length >= 2 && shipped.Contains(columns[1].Trim());
	}

	internal static int FindFunscriptEnd(string root, string file)
	{
		if (!Directory.Exists(root)) return 0;
		foreach (string path in Directory.GetFiles(root, file + ".funscript", SearchOption.AllDirectories))
		{
			int maximum = 0;
			foreach (Match match in FunscriptAt.Matches(File.ReadAllText(path)))
			{
				if (int.TryParse(match.Groups[1].Value, out int at) && at > maximum) maximum = at;
			}
			if (maximum > 0) return maximum;
		}
		return 0;
	}

	internal static void InjectSpawner(MonoBehaviour spawner)
	{
		ResolvePending();
		FieldInfo field = AccessTools.Field(spawner.GetType(), "enemyData");
		EnemyData[] existing = field?.GetValue(spawner) as EnemyData[];
		if (field == null || existing == null) return;
		List<EnemyData> vanilla = new List<EnemyData>(existing);
		vanilla.RemoveAll(data => Enemies.Exists(definition => definition.EnemyData != null && definition.EnemyData == data));
		// A spawner with no authored enemies of its own spawns nothing, and that is a decision the
		// room's author made. Injecting into it does not add a package to a mix - it makes that
		// spawner spawn the package and nothing else. Two of the twenty spawners in the 2026-08-26
		// run were like this and reported "1 of 1 table entries (100% per roll)" (§132).
		if (vanilla.Count == 0)
		{
			return;
		}
		// `EnemySpawner.GetEnemyPrefab` picks **uniformly** from this array, and vanilla's own
		// tables weight an enemy by listing its name more than once (code/spawntables.py). So a
		// weight is a count of entries, and the old `RoundToInt(weight)` clamped to [1, 10] could
		// not express one below 1: the witch's authored 0.2 rounded to 0, clamped back up to 1,
		// and put a boss in every table on the same footing as a zombie - one entry in nine, when
		// what the manifest asked for was a fifth of one enemy's share. That is what the
		// 2026-08-26 run saw as "the spawn rate seems way too high" (§130).
		//
		// A fraction of an entry does not exist, so the table is scaled up until it does: every
		// vanilla entry is repeated `scale` times and the custom one gets `weight * scale`. The
		// ratios of the vanilla table are untouched by construction, and `scale` is 1 - the array
		// left exactly as authored - unless some enabled package actually asks for a fraction.
		int scale = 1;
		foreach (CustomEnemyDefinition definition in Enemies)
		{
			if (!definition.Enabled || !definition.IncludeInRandomSpawns || definition.EnemyData == null) continue;
			if (definition.SpawnWeight < 1f) scale = Mathf.Max(scale, Mathf.CeilToInt(1f / definition.SpawnWeight));
		}
		// A cap, because this multiplies the length of a table the game rolls on every spawn, and
		// past a point the extra resolution buys nothing a player could notice: at 20 the finest
		// weight that means anything is 0.05, one spawn in twenty of a single enemy's share.
		scale = Mathf.Clamp(scale, 1, 20);
		List<EnemyData> result = new List<EnemyData>(vanilla.Count * scale + 4);
		foreach (EnemyData data in vanilla)
			for (int i = 0; i < scale; i++) result.Add(data);
		foreach (CustomEnemyDefinition definition in Enemies)
		{
			if (!definition.Enabled || !definition.IncludeInRandomSpawns || definition.EnemyData == null || result.Contains(definition.EnemyData)) continue;
			int copies = Mathf.Clamp(Mathf.RoundToInt(definition.SpawnWeight * scale), 1, 10 * scale);
			for (int i = 0; i < copies; i++) result.Add(definition.EnemyData);
			Plugin.DBG("SPAWN", "custom:" + definition.Id + " weight " + definition.SpawnWeight.ToString("0.##")
				+ " -> " + copies + " of " + (result.Count) + " table entries ("
				+ (100f * copies / Mathf.Max(1, result.Count)).ToString("0.#") + "% per roll)");
		}
		field.SetValue(spawner, result.ToArray());
	}

	internal static void InjectGallery(object target)
	{
		ResolvePending();
		FieldInfo field = AccessTools.Field(target.GetType(), "allEnemies");
		if (field?.GetValue(target) is not List<EnemyGalleryEntry> list) return;
		if (target is EnemyGalleryUI ui)
		{
			bool customSection = IsCustomGallerySection(ui);
			list.RemoveAll(entry => IsCustomGalleryEntry(entry) || PackageGalleryEntries.IsProvided(entry));
			if (!customSection) return;
			list.Clear();
			foreach (EnemyGalleryEntry entry in GetGalleryEntries()) list.Add(entry);
			foreach (EnemyGalleryEntry entry in PackageGalleryEntries.All()) list.Add(entry);
			return;
		}
		list.RemoveAll(entry => IsCustomGalleryEntry(entry) && Find(entry)?.Enabled != true);
		// A provided entry's on/off state is the provider's business: `GalleryEntries` returns what
		// that package wants shown, so a switched-off package simply lists nothing. The framework
		// used to filter these itself, back when it also owned the registry behind them (§165).
		foreach (CustomEnemyDefinition definition in Enemies)
		{
			if (definition.Enabled && definition.GalleryEntry != null && !list.Exists(x => x != null && string.Equals(x.enemyID, definition.Id, StringComparison.OrdinalIgnoreCase)))
			{
				list.Add(definition.GalleryEntry);
			}
		}
		foreach (EnemyGalleryEntry entry in PackageGalleryEntries.All())
			if (!list.Contains(entry)) list.Add(entry);
	}

	private static bool IsCustomGallerySection(EnemyGalleryUI ui)
	{
		if (ui == null) return false;
		if (ui.GetComponentInParent<CustomGallerySectionMarker>(true) != null) return true;
		Transform current = ui.transform;
		while (current != null)
		{
			if (current.name.Equals("CustomGalleryContent", StringComparison.OrdinalIgnoreCase)) return true;
			current = current.parent;
		}
		return false;
	}

	internal static CustomEnemyDefinition Find(EnemyGalleryEntry entry)
	{
		return entry == null ? null : Enemies.Find(x => x.GalleryEntry == entry || x.Id.Equals(entry.enemyID, StringComparison.OrdinalIgnoreCase));
	}

	internal static bool IsCustomGalleryEntry(EnemyGalleryEntry entry) => entry != null && Enemies.Exists(x => x.GalleryEntry == entry);

	internal static IEnumerable<EnemyGalleryEntry> GetGalleryEntries()
	{
		ResolvePending();
		foreach (CustomEnemyDefinition definition in Enemies)
			if (definition.Enabled && definition.GalleryEntry != null) yield return definition.GalleryEntry;
	}

	// The second table a spawn decision is read from, and the one nothing used to tell.
	//
	// `EnemySpawnShuffle` in PncEdi keeps its own pool of prefabs and weights, rebuilt only when
	// the scene handle changes, and its `GetEnemyPrefab` prefixes *replace* the spawner's pick
	// rather than biasing it. So while shuffle mode is on, the injected `enemyData[]` table below
	// decides nothing: a package switched off mid-run stayed in the stale pool and kept being
	// drawn until the next level load, and a weight change did nothing at all. Both switches
	// therefore looked broken while reading as applied, and only for packages that go through the
	// pool - a wall-picture trap never does, so its switch worked and the difference between the
	// two looked like a fault in the package rather than in the cache. Dropping the cache makes
	// the next draw rebuild it from the current config.
	private static void ClearShuffleCache()
	{
		try
		{
			EnemySpawnShuffle.ClearCache();
		}
		catch (Exception ex)
		{
			Plugin.Log?.LogError("[CustomEnemies] could not clear the shuffle pool: " + ex);
		}
	}

	// A weight change has to reach the tables that were injected when the level loaded, or it
	// means nothing until the next scene. Re-injecting is idempotent: InjectSpawner strips our
	// own entries before it rebuilds, so it recomputes the table from the authored one.
	private static void ReinjectSpawners()
	{
		try
		{
			ClearShuffleCache();
			ResolvePending();
			foreach (EnemySpawner spawner in UnityEngine.Object.FindObjectsByType<EnemySpawner>(FindObjectsInactive.Include, FindObjectsSortMode.None))
			{
				InjectSpawner(spawner);
			}
			foreach (ArenaEnemySpawner spawner in UnityEngine.Object.FindObjectsByType<ArenaEnemySpawner>(FindObjectsInactive.Include, FindObjectsSortMode.None))
			{
				InjectSpawner(spawner);
			}
		}
		catch (Exception ex)
		{
			Plugin.Log?.LogError("[CustomEnemies] could not re-inject spawners: " + ex);
		}
	}

	private static void ApplyEnabledState(CustomEnemyDefinition definition)
	{
		try
		{
			ClearShuffleCache();
			ResolvePending();
			foreach (EnemySpawner spawner in UnityEngine.Object.FindObjectsByType<EnemySpawner>(FindObjectsInactive.Include, FindObjectsSortMode.None))
			{
				InjectSpawner(spawner);
			}
			foreach (ArenaEnemySpawner spawner in UnityEngine.Object.FindObjectsByType<ArenaEnemySpawner>(FindObjectsInactive.Include, FindObjectsSortMode.None))
			{
				InjectSpawner(spawner);
			}
			if (GalleryProgressManager.Instance != null)
			{
				InjectGallery(GalleryProgressManager.Instance);
			}
			foreach (EnemyGalleryUI gallery in UnityEngine.Object.FindObjectsByType<EnemyGalleryUI>(FindObjectsInactive.Include, FindObjectsSortMode.None))
			{
				InjectGallery(gallery);
			}
			Plugin.Log?.LogInfo("[CustomEnemies] '" + definition.Id + "' " + (definition.Enabled ? "enabled" : "disabled") + " from configuration");
		}
		catch (Exception ex)
		{
			Plugin.Log?.LogError("[CustomEnemies] could not apply enabled state for '" + definition.Id + "': " + ex);
		}
	}
}

[HarmonyPatch]
internal static class CustomEnemyHooks
{
	[HarmonyPatch(typeof(EnemySpawner), "Awake")]
	[HarmonyPrefix]
	private static void EnemySpawnerAwakePrefix(EnemySpawner __instance) => CustomEnemyRegistry.InjectSpawner(__instance);

	[HarmonyPatch(typeof(ArenaEnemySpawner), "Awake")]
	[HarmonyPrefix]
	private static void ArenaSpawnerAwakePrefix(ArenaEnemySpawner __instance) => CustomEnemyRegistry.InjectSpawner(__instance);

	[HarmonyPatch(typeof(EnemyGalleryUI), "Start")]
	[HarmonyPrefix]
	private static void GalleryStartPrefix(EnemyGalleryUI __instance) => CustomEnemyRegistry.InjectGallery(__instance);

	[HarmonyPatch(typeof(EnemyGalleryUI), "OpenGallery")]
	[HarmonyPrefix]
	private static void GalleryOpenPrefix(EnemyGalleryUI __instance) => CustomEnemyRegistry.InjectGallery(__instance);

	[HarmonyPatch(typeof(GalleryProgressManager), "Awake")]
	[HarmonyPrefix]
	private static void ProgressAwakePrefix(GalleryProgressManager __instance) => CustomEnemyRegistry.InjectGallery(__instance);
}
