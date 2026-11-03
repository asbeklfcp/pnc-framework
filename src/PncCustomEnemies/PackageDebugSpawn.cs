using System.Collections.Generic;
using PncCustomEnemies.Api;

namespace PncEdi;

using PncCustomEnemies;

/// <summary>
/// What the debug spawn key does, for every package kind (§181).
///
/// **One key and one id, whatever kind of package the id names.** Until §181 an `enemy.json`
/// package was spawned by the framework's `Tools / SpawnCustomEnemyKey` while the Joker Wall Trap
/// bound its own `Wall Picture Traps / PlaceTrapKey` — so a second trap package would have brought a
/// third key in a fourth config section, while ten enemy packages went on sharing one. The
/// framework has the key and the id selector; what it lacked was a way to say "place *your* thing"
/// to a kind it does not implement. <see cref="IPackageDebugSpawn"/> is that, and this is the
/// dispatcher over both halves.
///
/// The id resolution lives here rather than in either half, because "which package did you mean"
/// is one question across the whole install and answering it twice is how the two answers drift.
/// </summary>
internal static class PackageDebugSpawn
{
	/// <summary>Every id the debug key can act on, in a stable order: enemy packages, then packages that spawn themselves.</summary>
	private static List<string> SpawnableIds()
	{
		List<string> ids = new List<string>();
		foreach (CustomEnemyDefinition definition in CustomEnemyRegistry.LoadedEnemies)
		{
			if (!ids.Contains(definition.Id))
			{
				ids.Add(definition.Id);
			}
		}
		foreach (PackageAssembly package in PackageAssemblies.All)
		{
			// A package whose code is switched off has no module and cannot be asked to place
			// anything, so it is not offered - the log says the switch is why, not the id.
			if (package.Module is IPackageDebugSpawn && !ids.Contains(package.Id))
			{
				ids.Add(package.Id);
			}
		}
		return ids;
	}

	private static string Describe(List<string> ids)
	{
		return ids.Count == 0 ? "none" : string.Join(", ", ids.ToArray());
	}

	internal static void Run(string configuredId)
	{
		List<string> ids = SpawnableIds();
		string wanted = (configuredId ?? "").Trim();
		// An empty id is the shipped default, because the framework ships no packages and naming one
		// here would make a package the loader's own. With exactly one spawnable there is nothing to
		// choose between, so the key acts on it; with several, say which ones rather than pick.
		if (wanted.Length == 0)
		{
			if (ids.Count != 1)
			{
				Plugin.DBG("SPAWN", "custom: SpawnCustomEnemyId is unset and " + ids.Count
					+ " package(s) can be spawned - set it to one of: " + Describe(ids));
				return;
			}
			wanted = ids[0];
		}

		// A package that owns its own kind gets asked first: it is the one that knows what spawning
		// means for it, and an id can only belong to one package either way.
		foreach (PackageAssembly package in PackageAssemblies.All)
		{
			if (!string.Equals(package.Id, wanted, System.StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			if (!package.Consented || !PackageLifecycle.Active(package.Id))
			{
				Plugin.DBG("SPAWN", "custom:" + wanted + ": package is disabled; nothing spawned");
				return;
			}
			if (package.Module is IPackageDebugSpawn spawner)
			{
				bool placed;
				try
				{
					placed = spawner.DebugSpawn();
				}
				catch (System.Exception exception)
				{
					Plugin.DBG("SPAWN", "custom:" + wanted + ": the package threw placing itself: " + exception);
					return;
				}
				if (!placed)
				{
					Plugin.DBG("SPAWN", "custom:" + wanted + ": the package had nowhere to place anything");
				}
				return;
			}
			if (!package.Loaded)
			{
				Plugin.DBG("SPAWN", "custom:" + wanted + ": that package's code is switched off, so it "
					+ "cannot place anything - enable it in the mod manager (F11) and restart");
				return;
			}
		}

		if (CustomEnemyRegistry.SpawnById(wanted) == null)
		{
			// SpawnById logs the specific reason - unknown id, or a clone with no live base enemy in
			// the level yet - so this adds only what it cannot know: what else the key would accept.
			Plugin.DBG("SPAWN", "custom:" + wanted + ": spawnable ids are " + Describe(ids));
		}
	}
}
