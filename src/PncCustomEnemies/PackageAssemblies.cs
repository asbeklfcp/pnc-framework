using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using PncCustomEnemies.Api;
using PncEdi;
using UnityEngine;

namespace PncCustomEnemies;

/// <summary>The `"assembly"` block of a package manifest: which DLL, which type in it, and which API it was built against.</summary>
[Serializable]
public sealed class PackageAssemblyDeclaration
{
	public string file;
	public string module;
	public int api;
}

/// <summary>One package that declares an assembly, whether or not it was allowed to load.</summary>
internal sealed class PackageAssembly
{
	internal string Id;
	internal string Directory;
	internal string ManifestPath;
	internal string ManifestJson;
	internal PackageAssemblyDeclaration Declaration;
	internal ConfigEntry<bool> EnabledEntry;
	internal PackageContext Context;
	internal IPackageModule Module;
	internal string Refusal;

	internal bool Consented => EnabledEntry != null && EnabledEntry.Value;
	internal bool Loaded => Module != null;
}

/// <summary>
/// Loads the code a package ships, and refuses to until someone says so.
///
/// **There is no sandbox and there cannot be one.** BepInEx runs on Mono with the game's full
/// privileges, and an assembly loaded out of `BepInEx/custom-enemies/&lt;name&gt;/` has the
/// filesystem and the network like any other plugin. Nothing this loader does changes that, so what
/// it owes the player is disclosure and consent rather than containment (§164): a package that
/// ships a DLL is **off by default** in `com.edi.pnc.customenemies.cfg`, its switch says in so many
/// words that turning it on runs third-party code, and a log line names the assembly whether it
/// loads or is refused. A player who never turns one on never runs third-party code, and one who
/// does was asked first.
///
/// The switch is per package rather than global on purpose: "I trust this download" is the question
/// a player can actually answer, and a single master switch turns the next package's code on by a
/// decision made about a different package.
///
/// **It is the package's one switch, not a second one beside it (§167).** §165 bound a separate
/// `&lt;id&gt; code` bool, which meant two questions where a player has one - the answer to "do I
/// want this package" *is* the answer to "may it run", because a code package that is on and
/// blocked is an installed package that does nothing. Play found the seam exactly there: both
/// packages "worked" only after enabling them and then finding the second switch. So the consent
/// lives on the enable switch, whose default is `false` for a package that ships code and whatever
/// the manifest says for one that does not, and the warning is drawn where it is turned on.
/// </summary>
internal static class PackageAssemblies
{
	private static readonly List<PackageAssembly> Packages = new List<PackageAssembly>();
	private static readonly List<IPackageGalleryProvider> GalleryProviderList = new List<IPackageGalleryProvider>();
	private static bool _initialized;

	internal static IReadOnlyList<PackageAssembly> All => Packages;

	/// <summary>Modules that add their own gallery rows. Empty until something both declares one and is allowed to load.</summary>
	internal static IReadOnlyList<IPackageGalleryProvider> GalleryProviders => Packages
		.Where(p=>PackageLifecycle.Active(p.Id) && p.Module is IPackageGalleryProvider)
		.Select(p=>(IPackageGalleryProvider)p.Module).ToArray();
	internal static string GalleryOwner(IPackageGalleryProvider provider) => Packages.Find(p => ReferenceEquals(p.Module, provider))?.Id;
	internal static bool IsLoaded(string id) => Packages.Any(p=>string.Equals(p.Id,id,StringComparison.OrdinalIgnoreCase) && p.Loaded);

	/// <summary>
	/// Packages that ship code and are switched off - what the mod manager offers to enable, and
	/// the reason a package can look installed and do nothing.
	/// </summary>
	internal static IEnumerable<PackageAssembly> Blocked
	{
		get
		{
			foreach (PackageAssembly package in Packages)
			{
				if (!package.Loaded && package.Refusal == null)
				{
					yield return package;
				}
			}
		}
	}

	internal static void Initialize()
	{
		if (_initialized)
		{
			return;
		}
		_initialized = true;
		string root = Path.Combine(Paths.BepInExRootPath, "custom-enemies");
		System.IO.Directory.CreateDirectory(root);
		foreach (string directory in System.IO.Directory.GetDirectories(root))
		{
			if (string.Equals(Path.GetFileName(directory), "_example", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			foreach (string manifestPath in System.IO.Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
			{
				TryDeclare(manifestPath);
			}
		}
		foreach (PackageAssembly package in Packages)
		{
			Load(package);
			if(package.Consented && !package.Loaded) PackageLifecycle.Disable(package.Id);
		}
	}

	private static void TryDeclare(string manifestPath)
	{
		try
		{
			string json = File.ReadAllText(manifestPath);
			string block = JsonBlock(json, "assembly");
			if (block == null)
			{
				return;
			}
			PackageAssemblyDeclaration declaration = JsonUtility.FromJson<PackageAssemblyDeclaration>(block);
			if (declaration == null || string.IsNullOrWhiteSpace(declaration.file) || string.IsNullOrWhiteSpace(declaration.module))
			{
				CustomEnemyPlugin.Log?.LogError("[CustomEnemies] " + Path.GetFileName(manifestPath) + " declares an assembly without both 'file' and 'module' - ignored");
				return;
			}
			string directory = Path.GetDirectoryName(manifestPath);
			PackageAssembly package = new PackageAssembly
			{
				Id = ReadId(json) ?? Path.GetFileName(directory),
				Directory = directory,
				ManifestPath = manifestPath,
				ManifestJson = json,
				Declaration = declaration
			};
			// **A code package's on/off switch is bound here, by every kind of manifest, and it is
			// the consent** (§167). It has to be bound before anything loads, and it has to be the
			// same entry `CustomEnemyRegistry` uses for an `enemy.json` package - which binds later
			// and therefore asks for it through `EnabledEntryFor` rather than binding a second one
			// with a different default. A package kind the framework does not know - a wall trap,
			// anything a package invents - has no other binder at all, so without this it would have
			// no switch until its own code ran, which is the switch that decides whether its code
			// runs (§166).
			package.EnabledEntry = CustomEnemyPlugin.Instance.Config.Bind(
				"Custom Enemies",
				package.Id,
				false,
				CodeSwitchDescription(ReadDisplayName(json, package.Id), declaration.file));
			Packages.Add(package);
			PackageLifecycle.Bind(package.Id,package.EnabledEntry);
		}
		catch (Exception ex)
		{
			CustomEnemyPlugin.Log?.LogError("[CustomEnemies] could not read " + manifestPath + ": " + ex.Message);
		}
	}

	private static void Load(PackageAssembly package)
	{
		string assemblyPath = Path.Combine(package.Directory, package.Declaration.file);
		if (!File.Exists(assemblyPath))
		{
			package.Refusal = "the file it names is not there";
			CustomEnemyPlugin.Log?.LogError("[CustomEnemies] '" + package.Id + "' declares " + package.Declaration.file + ", which is not in the package - the package's code will not run");
			return;
		}
		if (package.Declaration.api != PackageApi.Version)
		{
			// A mismatch is refused rather than tried: the members the module binds to are gone or
			// renamed, and the failure would otherwise land as a MissingMethodException somewhere in
			// the middle of a run, in a stack that names the package rather than the mismatch.
			package.Refusal = "it was built against API " + package.Declaration.api + ", this framework speaks " + PackageApi.Version;
			CustomEnemyPlugin.Log?.LogError("[CustomEnemies] '" + package.Id + "' was built against custom-enemy API " + package.Declaration.api +
				" and this framework speaks " + PackageApi.Version + " - " +
				(package.Declaration.api < PackageApi.Version ? "the package needs rebuilding against this version." : "the mod is older than the package; update the mod."));
			return;
		}
		if (!package.Consented)
		{
			CustomEnemyPlugin.Log?.LogWarning("[CustomEnemies] '" + package.Id + "' ships code (" + package.Declaration.file + ") and is switched off, so none of it runs. " +
				"Set 'Custom Enemies / " + package.Id + "' in com.edi.pnc.customenemies.cfg, or turn it on in the mod manager (F11), and restart. " +
				"Until then it runs nothing: no enemy, no traps, no gallery entry. Its funscripts and its gallery rows are handed to Edi either way, so turning it on later needs no Edi restart.");
			return;
		}
		try
		{
			// LoadFrom rather than Load(byte[]): Mono resolves the assembly's own dependencies
			// relative to the file, and a package that ships more than one DLL then works without
			// the loader knowing about the second.
			Assembly assembly = Assembly.LoadFrom(assemblyPath);
			Type moduleType = assembly.GetType(package.Declaration.module, false);
			if (moduleType == null)
			{
				package.Refusal = "the module type it names is not in the assembly";
				CustomEnemyPlugin.Log?.LogError("[CustomEnemies] '" + package.Id + "': " + package.Declaration.file + " has no type '" + package.Declaration.module + "'");
				return;
			}
			if (!typeof(IPackageModule).IsAssignableFrom(moduleType))
			{
				package.Refusal = "the module type does not implement IPackageModule";
				CustomEnemyPlugin.Log?.LogError("[CustomEnemies] '" + package.Id + "': '" + package.Declaration.module + "' does not implement IPackageModule");
				return;
			}
			GameObject host = new GameObject("CustomEnemyPackage_" + package.Id);
			if(!typeof(IPackageLifecycle).IsAssignableFrom(moduleType))
			{
				UnityEngine.Object.Destroy(host);
				package.Refusal="API 3 requires IPackageLifecycle cleanup";
				CustomEnemyPlugin.Log?.LogError("[CustomEnemies] "+package.Id+": "+package.Refusal);
				return;
			}
			UnityEngine.Object.DontDestroyOnLoad(host);
			host.SetActive(true);
			PackageRuntime.OwnObject(package.Id,host);
			PackageContext context = new PackageContext
			{
				Id = package.Id,
				Directory = package.Directory,
				ManifestJson = package.ManifestJson,
				ManifestFileName = Path.GetFileName(package.ManifestPath),
				Log = CustomEnemyPlugin.Log,
				Config = CustomEnemyPlugin.Instance.Config,
				Host = host,
				IsEnabled = () => PackageLifecycle.Active(package.Id),
				EnabledEntry = package.EnabledEntry,
				RegisterBehaviour = (name, factory) => PackageBehaviours.Register(package.Id, name, factory)
			};
			IPackageModule module = (IPackageModule)Activator.CreateInstance(moduleType);
			PackageRuntime.RegisterCleanup(package.Id,"module",()=>((IPackageLifecycle)module).Deactivate());
			module.Initialize(context);
			package.Context = context;
			package.Module = module;
			if (module is IPackageGalleryProvider provider)
			{
				GalleryProviderList.Add(provider);
			}
			CustomEnemyPlugin.Log?.LogWarning("[CustomEnemies] '" + package.Id + "' is running its own code: " + package.Declaration.file + " -> " + package.Declaration.module + " (allowed by configuration)");
		}
		catch (Exception ex)
		{
			package.Refusal = ex.GetType().Name;
			CustomEnemyPlugin.Log?.LogError("[CustomEnemies] '" + package.Id + "' failed to load " + package.Declaration.file + ": " + ex);
		}
	}

	/// <summary>Tells one package's module that its enemy template now exists. Silent for a package with no assembly, which is every package that is pure data.</summary>
	internal static void RaiseTemplatePrepared(string id, GameObject template)
	{
		foreach (PackageAssembly package in Packages)
		{
			if (package.Loaded && string.Equals(package.Id, id, StringComparison.OrdinalIgnoreCase))
			{
				try
				{
					package.Context.RaiseTemplatePrepared(template);
				}
				catch (Exception ex)
				{
					CustomEnemyPlugin.Log?.LogError("[CustomEnemies] '" + package.Id + "' threw preparing its template: " + ex);
				}
			}
		}
	}

	/// <summary>
	/// The one switch a code package has, worded so that the config file alone tells a player what
	/// turning it on means. **`PncModManager` matches on "ships its own code"** to know which
	/// packages get the warning in the settings window: it references neither of the other
	/// assemblies, so the marker is this sentence, the same way the title it prints is read out of
	/// the `Enable &lt;name&gt;.` this starts with. Change the wording here and change it there.
	/// </summary>
	internal static string CodeSwitchDescription(string displayName, string file)
	{
		return "Enable " + displayName + ". This package ships its own code (" + file + "), which runs like any other mod, so only turn it on if you trust where you got it. "
			+ "Enabling requires a restart. Disabling closes spawning and cleans tracked instances, captures and media; check Package status for cleanup failures. "
			+ "though its funscripts and its gallery rows still reach Edi, so turning it on later does not also mean restarting Edi.";
	}

	/// <summary>A package's own display name, for a switch a player reads. Falls back to the id.</summary>
	private static string ReadDisplayName(string json, string fallback)
	{
		string name = ReadString(json, "displayName");
		return string.IsNullOrWhiteSpace(name) ? fallback : name;
	}

	private static string ReadId(string json)
	{
		return ReadString(json, "id");
	}

	private static string ReadString(string json, string field)
	{
		int key = json.IndexOf("\"" + field + "\"", StringComparison.OrdinalIgnoreCase);
		if (key < 0) return null;
		int colon = json.IndexOf(':', key);
		if (colon < 0) return null;
		int open = json.IndexOf('"', colon);
		if (open < 0) return null;
		int close = json.IndexOf('"', open + 1);
		if (close < 0) return null;
		string value = json.Substring(open + 1, close - open - 1).Trim();
		return string.IsNullOrEmpty(value) ? null : value;
	}

	/// <summary>
	/// A package's on/off switch - for a module that wants to watch it change rather than poll, and
	/// for `CustomEnemyRegistry`, which asks before binding its own so that an `enemy.json` package
	/// shipping a DLL has one entry rather than two defaults over one key (§167). Null for a package
	/// that declares no assembly, which is every package this file never saw.
	/// </summary>
	internal static ConfigEntry<bool> EnabledEntryFor(string id)
	{
		foreach (PackageAssembly package in Packages)
		{
			if (string.Equals(package.Id, id, StringComparison.OrdinalIgnoreCase))
			{
				return package.EnabledEntry;
			}
		}
		return null;
	}

	// The framework's own manifest reader is a set of private helpers on CustomEnemyRegistry and
	// this file is deliberately standalone - it has to read a manifest whose vocabulary it does not
	// know, from a package kind it has never heard of, before anything else parses it.
	private static string JsonBlock(string json, string field)
	{
		if (string.IsNullOrEmpty(json)) return null;
		int key = json.IndexOf("\"" + field + "\"", StringComparison.OrdinalIgnoreCase);
		int start = key < 0 ? -1 : json.IndexOf('{', key);
		if (start < 0) return null;
		bool inString = false;
		bool escaped = false;
		int depth = 0;
		for (int i = start; i < json.Length; i++)
		{
			char c = json[i];
			if (inString)
			{
				if (escaped) escaped = false;
				else if (c == '\\') escaped = true;
				else if (c == '"') inString = false;
				continue;
			}
			if (c == '"') { inString = true; continue; }
			if (c == '{') depth++;
			else if (c == '}' && --depth == 0) return json.Substring(start, i - start + 1);
		}
		return null;
	}
}
