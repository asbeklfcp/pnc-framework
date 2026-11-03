using System;
using System.Collections.Generic;
using PncCustomEnemies.Api;
using UnityEngine;

namespace PncCustomEnemies;

/// <summary>
/// Behaviours published by package assemblies, by name, and the lookup a data-only manifest goes
/// through when it says `"behaviour": "charm-witch"`.
///
/// **This registry is what keeps the framework empty of enemy-specific code without taking the
/// no-code path away with it.** A behaviour lives in the package that ships it; a name makes it
/// reachable by any other package, none of which needs a compiler. The framework knows only that
/// some string maps to something implementing <see cref="IPackageBehaviourFactory"/>, and never
/// what a charm circle or a wall trap is.
///
/// A name that nothing provides is an ordinary, visible failure: the enemy still loads as its
/// reskin and the log says which package would have to be installed. That is the "inert and
/// visible, never silently plausible" rule the alias gap taught (§133) applied to behaviours.
/// </summary>
internal static class PackageBehaviours
{
	private sealed class Registration
	{
		internal string Provider;
		internal IPackageBehaviourFactory Factory;
	}

	private static readonly Dictionary<string, Registration> Factories = new Dictionary<string, Registration>(StringComparer.OrdinalIgnoreCase);

	internal static int Count => Factories.Count;

	internal static IEnumerable<string> Names => Factories.Keys;
	internal static void RemoveProvider(string id)
	{
		var keys=new List<string>();
		foreach(var pair in Factories) if(string.Equals(pair.Value.Provider,id,StringComparison.OrdinalIgnoreCase)) keys.Add(pair.Key);
		foreach(var key in keys) Factories.Remove(key);
	}

	internal static void Register(string providerId, string name, IPackageBehaviourFactory factory)
	{
		if (string.IsNullOrWhiteSpace(name) || factory == null)
		{
			CustomEnemyPlugin.Log?.LogError("[CustomEnemies] '" + providerId + "' tried to publish a behaviour with no name or no factory");
			return;
		}
		string key = name.Trim();
		if (Factories.TryGetValue(key, out Registration existing))
		{
			// First registration wins, and the loser is named. Two packages claiming one behaviour
			// name is a collision the player has to be able to see: whichever loads first would
			// otherwise silently decide what every data-only manifest naming it gets.
			CustomEnemyPlugin.Log?.LogError("[CustomEnemies] '" + providerId + "' publishes behaviour '" + key + "', which '" + existing.Provider + "' already published - keeping the first");
			return;
		}
		Factories[key] = new Registration { Provider = providerId, Factory = factory };
		CustomEnemyPlugin.Log?.LogInfo("[CustomEnemies] behaviour '" + key + "' published by '" + providerId + "'");
	}

	/// <summary>
	/// Attach the behaviour a manifest names to that manifest's template. Returns false when the
	/// manifest named one and nothing provides it, which is worth a log line and not an exception:
	/// the package is still a working reskin without it.
	/// </summary>
	internal static bool Attach(string behaviourName, GameObject template, string packageId, string packageDirectory, string settingsJson, string manifestJson)
	{
		if (string.IsNullOrWhiteSpace(behaviourName))
		{
			return true;
		}
		string key = behaviourName.Trim();
		if (!Factories.TryGetValue(key, out Registration registration))
		{
			CustomEnemyPlugin.Log?.LogWarning("[CustomEnemies] '" + packageId + "' asks for behaviour '" + key + "', which no installed package provides" +
				(Count == 0
					? " - no package has published a behaviour, so either the package that provides it is not installed or its code is switched off."
					: " - published behaviours are: " + string.Join(", ", new List<string>(Names).ToArray()) + ".") +
				" The enemy loads without it.");
			return false;
		}
		try
		{
			if(!PackageLifecycle.Active(packageId) || !PackageLifecycle.Active(registration.Provider)) return false;
			PackageLifecycle.Depend(packageId,registration.Provider);
			registration.Factory.Attach(template, new PackageBehaviourRequest
			{
				Id = packageId,
				Directory = packageDirectory,
				SettingsJson = settingsJson,
				ManifestJson = manifestJson,
				Log = CustomEnemyPlugin.Log
			});
			return true;
		}
		catch (Exception ex)
		{
			CustomEnemyPlugin.Log?.LogError("[CustomEnemies] behaviour '" + key + "' (from '" + registration.Provider + "') threw attaching to '" + packageId + "': " + ex);
			return false;
		}
	}
}
