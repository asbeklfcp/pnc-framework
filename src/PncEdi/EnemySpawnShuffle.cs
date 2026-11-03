using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using Random = UnityEngine.Random;
using Object = UnityEngine.Object;

namespace PncEdi;

[HarmonyPatch]
internal static class EnemySpawnShuffle
{
	private static int _cachedSceneHandle = -1;
	private static readonly Dictionary<string, List<GameObject>> PrefabsByKey = new Dictionary<string, List<GameObject>>(StringComparer.OrdinalIgnoreCase);
	private static readonly List<string> PoolKeys = new List<string>();
	private static float[] _weights;
	private static bool Enabled => (Plugin.GameplayTweaksEnabled && !OverkeksMode.Active) && (OverkeksMode.UsesRecipe ? OverkeksConfig.CfgEnemySpawnShuffle : Plugin.CfgEnemySpawnShuffle).Value;
	private static float ImpWeight => Mathf.Max(1f, (OverkeksMode.UsesRecipe ? OverkeksConfig.CfgImpSpawnWeightMultiplier : Plugin.CfgImpSpawnWeightMultiplier).Value);

	internal static void ClearCache()
	{
		_loggedSpawners.Clear();
		_cachedSceneHandle = -1;
		PrefabsByKey.Clear();
		PoolKeys.Clear();
		_weights = null;
	}

	// ------------------------------------------------------------------------------------
	// Grappler bias: vanilla's own table, rolled with a thumb on the scale
	//
	// Vanilla has no biome enemy list. `EnemySpawner.GetEnemyPrefab` and its arena twin pick
	// uniformly from *that spawner's* serialized `enemyData[]` - a per-room composition authored
	// in the scene - and then uniformly from the chosen `EnemyData.prefabVariants[]`. Read out of
	// game 0.3.1's assets on 2026-08-23: 67 spawners carry a table, and those tables are
	// deliberately uneven. Some rooms are one enemy (`Imp`, `Nun`, `Zombie Enemy`), some are a
	// pure goonshroom nest (`GoonShroom x3`, and one of `GoonShroom, Black Serpent, GoonShroom x7`),
	// and a few are long weighted lists that repeat a name to raise its odds
	// (`Gargoyle, Gooper, Imp, Black Serpent, Gargoyle x4, Imp x2, Gooper`).
	//
	// The pool mode below *replaces* that pick, so every spawner in the game rolled the same
	// seven-key pool and every room composition the game shipped was thrown away - which is why
	// a run felt uniform and why a goonshroom missing from the pool could not spawn at all (§91).
	// This mode keeps vanilla's table and only re-weights it, so a room with no grappler in its
	// table is left exactly as the game authored it.
	private static string[] GrapplerKeys()
	{
		string raw = (OverkeksMode.UsesRecipe ? OverkeksConfig.CfgGrapplerSpawnKeys : Plugin.CfgGrapplerSpawnKeys)?.Value ?? "";
		string[] entries = raw.Split(';');
		int count = 0;
		for (int i = 0; i < entries.Length; i++)
		{
			string entry = entries[i].Trim();
			if (entry.Length != 0)
			{
				entries[count++] = entry;
			}
		}
		Array.Resize(ref entries, count);
		return entries;
	}

	private static bool IsGrapplerData(EnemyData data, string[] grapplers)
	{
		if (data == null)
		{
			return false;
		}
		string name = data.enemyName;
		if (string.IsNullOrEmpty(name))
		{
			return false;
		}
		for (int i = 0; i < grapplers.Length; i++)
		{
			if (name.Trim().Equals(grapplers[i], StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}
		return false;
	}

	// The spawner's own `enemyData` is private on both spawner classes, and the two classes are
	// unrelated - no shared base, no interface - so this reads the field by name rather than
	// casting. Traverse returns an empty Traverse for a field that is gone, which reads back as
	// null here and falls through to vanilla, so a renamed field degrades to vanilla spawning
	// rather than to no spawning.
	private static EnemyData[] SpawnerTable(MonoBehaviour spawner)
	{
		return Traverse.Create((object)spawner).Field("enemyData").GetValue<EnemyData[]>();
	}

	internal static GameObject PickBiasedPrefab(MonoBehaviour spawner)
	{
		EnemyData[] table = SpawnerTable(spawner);
		if (table == null || table.Length == 0)
		{
			return null;
		}
		string[] grapplers = GrapplerKeys();
		float weight = Mathf.Max(0.01f, (OverkeksMode.UsesRecipe ? OverkeksConfig.CfgGrapplerSpawnWeight : Plugin.CfgGrapplerSpawnWeight).Value);
		float total = 0f;
		int grapplerCount = 0;
		for (int i = 0; i < table.Length; i++)
		{
			if (table[i] == null)
			{
				continue;
			}
			bool isGrappler = IsGrapplerData(table[i], grapplers);
			if (isGrappler)
			{
				grapplerCount++;
			}
			total += (isGrappler ? weight : 1f);
		}
		// Nothing to bias: hand the roll back to vanilla untouched rather than reimplementing a
		// uniform pick. Identical behaviour by construction beats identical behaviour by care.
		if (grapplerCount == 0 || total <= 0f)
		{
			return null;
		}
		float roll = Random.Range(0f, total);
		EnemyData picked = null;
		for (int i = 0; i < table.Length; i++)
		{
			if (table[i] == null)
			{
				continue;
			}
			roll -= (IsGrapplerData(table[i], grapplers) ? weight : 1f);
			if (roll <= 0f)
			{
				picked = table[i];
				break;
			}
		}
		if (picked == null)
		{
			return null;
		}
		GameObject prefab = picked.GetRandomPrefab();
		if (prefab == null)
		{
			return null;
		}
		LogBiasOnce(spawner, table, grapplerCount, weight);
		if (prefab.name.IndexOf("gargoyle", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			Plugin.Instance.StartCoroutine(ActivateGargoyleAfterSpawn(spawner, prefab));
		}
		return prefab;
	}

	// One line per spawner per scene, not per spawn: a room spawns three enemies and the table is
	// the same for all three, so logging every pick buries the run's own events.
	private static readonly HashSet<int> _loggedSpawners = new HashSet<int>();

	private static void LogBiasOnce(MonoBehaviour spawner, EnemyData[] table, int grapplerCount, float weight)
	{
		int id = spawner.GetInstanceID();
		if (!_loggedSpawners.Add(id))
		{
			return;
		}
		StringBuilder sb = new StringBuilder();
		for (int i = 0; i < table.Length; i++)
		{
			if (sb.Length > 0)
			{
				sb.Append(", ");
			}
			sb.Append((table[i] == null) ? "<null>" : table[i].enemyName);
		}
		Plugin.DBG("SPAWN", "table [" + sb.ToString() + "] " + grapplerCount + " grappler(s) at x" + weight);
	}

	internal static GameObject PickShuffledPrefab(MonoBehaviour spawner)
	{
		if (!Enabled || spawner == null)
		{
			return null;
		}
		EnsurePool();
		if (PoolKeys.Count == 0)
		{
			return null;
		}
		string key = PickWeightedKey();
		if (string.IsNullOrEmpty(key) || !PrefabsByKey.TryGetValue(key, out var prefabs) || prefabs.Count == 0)
		{
			return null;
		}
		GameObject prefab = prefabs[Random.Range(0, prefabs.Count)];
		if (key.Equals("gargoyle", StringComparison.OrdinalIgnoreCase))
		{
			Plugin.Instance.StartCoroutine(ActivateGargoyleAfterSpawn(spawner, prefab));
		}
		return prefab;
	}

	private static void EnsurePool()
	{
		Scene activeScene = SceneManager.GetActiveScene();
		int sceneHandle = (int)(activeScene.handle);
		if (_cachedSceneHandle != sceneHandle || PoolKeys.Count <= 0)
		{
			RebuildPool(sceneHandle);
		}
	}

	private static void RebuildPool(int sceneHandle)
	{
		PrefabsByKey.Clear();
		PoolKeys.Clear();
		_weights = null;
		_cachedSceneHandle = sceneHandle;
		string[] families = PoolFamilies();
		for (int j = 0; j < families.Length; j++)
		{
			string hint = HintForFamily(families[j]);
			if (string.IsNullOrEmpty(hint))
			{
				// A family nothing can find a prefab for is a gap, and a gap says so.
				Plugin.DBG("SPAWN", "shuffle family '" + families[j] + "': no Tools/Spawn<Key>NameHint - skipped");
				continue;
			}
			CollectDebugHint(hint);
		}
		// Custom-enemy packages join the same pool rather than getting a spawner of their own, so
		// one weighted draw decides between vanilla and custom and a package that is switched off
		// simply stops being drawn. Only packages that asked for it, and only once their template
		// exists - a clone package has no prefab until its base enemy is in the level.
		foreach (KeyValuePair<string, GameObject> package in CustomEnemyBridge.ShufflePool())
		{
			TryAddPrefab(package.Value, package.Key);
		}
		if (!PrefabsByKey.ContainsKey("gargoyle"))
		{
			DebugEnemySpawn.ClearCache();
			CollectDebugHint(Plugin.CfgSpawnGargoyleNameHint?.Value ?? "Gargoyle|Gargoyle Alt|gargoyle");
		}
		PoolKeys.AddRange(PrefabsByKey.Keys);
		_weights = new float[PoolKeys.Count];
		for (int i = 0; i < PoolKeys.Count; i++)
		{
			_weights[i] = PoolKeys[i].Equals("imp", StringComparison.OrdinalIgnoreCase)
				? ImpWeight
				: CustomEnemyBridge.SpawnWeight(PoolKeys[i]);
		}
		if (PoolKeys.Count > 0)
		{
			Plugin.DBG("SPAWN", "shuffle pool: " + FormatPoolSummary() + " imp weight x" + ImpWeight);
		}
		else
		{
			Plugin.DBG("SPAWN", "shuffle pool: empty");
		}
	}

	// Which enemy families the shuffle may spawn.
	//
	// This list is load-bearing in a way it does not look: the two `GetEnemyPrefab` prefixes
	// below *replace* the spawner's own pick rather than biasing it, so an enemy that is not in
	// this pool cannot be spawned by a spawner at all. Game 0.3.1 added the goonshroom and this
	// stayed at the six pre-0.3.1 types, so a 2026-08-22 session found no goonshrooms anywhere
	// across several runs - not a spawn-rate problem but an absolute one, and caused by the mod.
	// Config rather than a literal, so the next enemy a game version adds is a config line.
	private static string[] PoolFamilies()
	{
		string raw = (OverkeksMode.UsesRecipe ? OverkeksConfig.CfgEnemySpawnShufflePool : Plugin.CfgEnemySpawnShufflePool)?.Value ?? "";
		string[] entries = raw.Split(';');
		int count = 0;
		for (int i = 0; i < entries.Length; i++)
		{
			string entry = entries[i].Trim();
			if (entry.Length != 0)
			{
				entries[count++] = entry;
			}
		}
		Array.Resize(ref entries, count);
		return entries;
	}

	// The prefab search string for a family, taken from the same Tools/Spawn*NameHint the debug
	// spawn keys use, so a hint fixed for one is fixed for both.
	private static string HintForFamily(string family)
	{
		if (family.Equals("imp", StringComparison.OrdinalIgnoreCase))
		{
			return Plugin.CfgSpawnImpNameHint?.Value ?? "imp";
		}
		if (family.Equals("gargoyle", StringComparison.OrdinalIgnoreCase))
		{
			return Plugin.CfgSpawnGargoyleNameHint?.Value ?? "Gargoyle|Gargoyle Alt|gargoyle";
		}
		if (family.Equals("gooper", StringComparison.OrdinalIgnoreCase))
		{
			return Plugin.CfgSpawnGooperNameHint?.Value ?? "gooper";
		}
		if (family.Equals("nun", StringComparison.OrdinalIgnoreCase))
		{
			return Plugin.CfgSpawnNunNameHint?.Value ?? "hood|nun";
		}
		if (family.Equals("zombie", StringComparison.OrdinalIgnoreCase))
		{
			return Plugin.CfgSpawnZombieNameHint?.Value ?? "zombie";
		}
		if (family.Equals("plantasha", StringComparison.OrdinalIgnoreCase))
		{
			return Plugin.CfgSpawnPlantashaNameHint?.Value ?? "plantasha";
		}
		if (family.Equals("goonshroom", StringComparison.OrdinalIgnoreCase))
		{
			return Plugin.CfgSpawnGoonShroomNameHint?.Value ?? "goonshroom";
		}
		if (family.Equals("blinded_beast", StringComparison.OrdinalIgnoreCase))
		{
			return Plugin.CfgSpawnBlindedBeastNameHint?.Value ?? "blindedbeast|blinded beast|blinded";
		}
		return null;
	}

	private static void CollectDebugHint(string nameHint)
	{
		if (string.IsNullOrWhiteSpace(nameHint))
		{
			return;
		}
		string key = ResolveShuffleKeyFromHint(nameHint);
		if (key == null && MatchesGargoyleShuffleHint(nameHint))
		{
			key = "gargoyle";
		}
		List<GameObject> spawnVariants = DebugEnemySpawn.GetSpawnVariants(nameHint, "shuffle");
		if (spawnVariants == null || spawnVariants.Count == 0)
		{
			Plugin.DBG("SPAWN", "shuffle hint '" + nameHint + "': no prefabs");
			return;
		}
		for (int i = 0; i < spawnVariants.Count; i++)
		{
			TryAddPrefab(GetSpawnTemplateRoot(spawnVariants[i]), key);
		}
	}

	private static bool MatchesGargoyleShuffleHint(string nameHint)
	{
		return !string.IsNullOrWhiteSpace(nameHint) && nameHint.IndexOf("gargoyle", StringComparison.OrdinalIgnoreCase) >= 0;
	}

	private static string ResolveShuffleKeyFromHint(string nameHint)
	{
		if (string.IsNullOrWhiteSpace(nameHint))
		{
			return null;
		}
		string[] hints = nameHint.Split('|');
		for (int i = 0; i < hints.Length; i++)
		{
			string hint = hints[i].Trim();
			if (hint.Length != 0)
			{
				string key = NameRemap.ResolveEnemyKey(hint);
				if (IsNormalEnemyKey(key))
				{
					return key;
				}
				key = ClassifyFromRawName(hint);
				if (IsNormalEnemyKey(key))
				{
					return key;
				}
			}
		}
		return null;
	}

	private static GameObject GetSpawnTemplateRoot(GameObject prefab)
	{
		if (prefab == null)
		{
			return null;
		}
		GameObject root = prefab;
		while (root.transform.parent != null)
		{
			GameObject parent = root.transform.parent.gameObject;
			if (parent.GetComponentInChildren<EnemyAI>(true) != null || parent.GetComponentInChildren<ChargingEnemyAI>(true) != null || parent.GetComponentInChildren<SpinningEnemyAI>(true) != null)
			{
				root = parent;
				continue;
			}
			break;
		}
		return root;
	}

	private static void TryAddPrefab(GameObject prefab, string forcedKey = null)
	{
		if (prefab == null)
		{
			return;
		}
		if (forcedKey != null)
		{
			if (!IsTrustedShufflePrefab(prefab, forcedKey))
			{
				Plugin.DBG("SPAWN", "shuffle skip '" + NameRemap.StripCloneSuffix(prefab.name) + "' (trusted/" + forcedKey + ")");
				return;
			}
		}
		else if (!IsNormalSpawnPrefab(prefab))
		{
			Plugin.DBG("SPAWN", "shuffle skip filtered prefab '" + NameRemap.StripCloneSuffix(prefab.name) + "'");
			return;
		}
		string key = forcedKey ?? ClassifyPrefabKey(prefab);
		if (string.IsNullOrEmpty(key))
		{
			Plugin.DBG("SPAWN", "shuffle skip unclassified prefab '" + NameRemap.StripCloneSuffix(prefab.name) + "'");
			return;
		}
		if (!PrefabsByKey.TryGetValue(key, out var prefabs))
		{
			prefabs = new List<GameObject>();
			PrefabsByKey[key] = prefabs;
		}
		for (int i = 0; i < prefabs.Count; i++)
		{
			if ((Object)(object)prefabs[i] == (Object)(object)prefab)
			{
				return;
			}
		}
		prefabs.Add(prefab);
	}

	private static bool IsTrustedShufflePrefab(GameObject prefab, string forcedKey)
	{
		if (prefab == null || string.IsNullOrEmpty(forcedKey))
		{
			return false;
		}
		// A custom package vouches for itself. The filters below reject prefabs that the shuffle
		// cannot place safely - a boss, a mimic, a camera-swap trigger - and a package built on
		// one of those bases would be rejected for what it was cloned from rather than for what it
		// is. Declaring `includeInRandomSpawns` is the author saying they have handled that.
		if (CustomEnemyBridge.IsCustomKey(forcedKey))
		{
			return true;
		}
		if (ChaserBossHelper.IsChaserBossPrefab(prefab))
		{
			return false;
		}
		if (prefab.GetComponent<MimicEnemy>() != null)
		{
			return false;
		}
		if (prefab.GetComponent<CameraSwapTrigger>() != null)
		{
			return false;
		}
		if (forcedKey.Equals("gargoyle", StringComparison.OrdinalIgnoreCase) && DebugEnemySpawn.IsBlockedGargoyleSpawnName(prefab.name))
		{
			return false;
		}
		return true;
	}

	private static bool IsNormalSpawnPrefab(GameObject prefab)
	{
		if (prefab == null)
		{
			return false;
		}
		string name = NameRemap.StripCloneSuffix(prefab.name);
		if (name.Equals("Gargoyle", StringComparison.OrdinalIgnoreCase) || name.Equals("Gargoyle Alt", StringComparison.OrdinalIgnoreCase) || name.Equals("Gooper", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Gooper ", StringComparison.OrdinalIgnoreCase))
		{
			return !ChaserBossHelper.IsChaserBossPrefab(prefab) && prefab.GetComponent<CameraSwapTrigger>() == null && prefab.GetComponent<MimicEnemy>() == null;
		}
		if (DebugEnemySpawn.IsBlockedGargoyleSpawnName(prefab.name))
		{
			return false;
		}
		if (ChaserBossHelper.IsChaserBossPrefab(prefab))
		{
			return false;
		}
		if (prefab.GetComponent<ProjectileEnemyAI>() != null && prefab.GetComponentInChildren<EnemyAI>(true) == null && prefab.GetComponentInChildren<ChargingEnemyAI>(true) == null && prefab.GetComponentInChildren<SpinningEnemyAI>(true) == null)
		{
			return false;
		}
		if (prefab.GetComponent<MimicEnemy>() != null)
		{
			return false;
		}
		if (prefab.GetComponent<CameraSwapTrigger>() != null)
		{
			return false;
		}
		return prefab.GetComponentInChildren<EnemyAI>(true) != null || prefab.GetComponentInChildren<ChargingEnemyAI>(true) != null || prefab.GetComponentInChildren<SpinningEnemyAI>(true) != null || !string.IsNullOrEmpty(ClassifyPrefabKey(prefab));
	}

	private static string ClassifyPrefabKey(GameObject prefab)
	{
		EnemyAI enemyAI = prefab.GetComponentInChildren<EnemyAI>(true);
		if (enemyAI != null)
		{
			string key = ClassifyEnemyKey(prefab.name, enemyAI.galleryEnemyID);
			if (!string.IsNullOrEmpty(key))
			{
				return key;
			}
		}
		ChargingEnemyAI charging = prefab.GetComponentInChildren<ChargingEnemyAI>(true);
		if (charging != null)
		{
			string chargingKey = ClassifyEnemyKey(prefab.name, charging.galleryEnemyID);
			if (!string.IsNullOrEmpty(chargingKey))
			{
				return chargingKey;
			}
		}
		SpinningEnemyAI spinning = prefab.GetComponentInChildren<SpinningEnemyAI>(true);
		if (spinning != null)
		{
			string spinningKey = ClassifyEnemyKey(prefab.name, spinning.galleryEnemyID);
			if (!string.IsNullOrEmpty(spinningKey))
			{
				return spinningKey;
			}
		}
		return ClassifyEnemyKey(prefab.name, null);
	}

	private static string ClassifyEnemyKey(string name, string galleryId)
	{
		if (!string.IsNullOrEmpty(galleryId))
		{
			string fromGalleryId = NameRemap.ResolveEnemyKey(galleryId);
			if (IsNormalEnemyKey(fromGalleryId))
			{
				return fromGalleryId;
			}
		}
		if (!string.IsNullOrEmpty(name))
		{
			string fromName = NameRemap.ResolveEnemyKey(name);
			if (IsNormalEnemyKey(fromName))
			{
				return fromName;
			}
			fromName = ClassifyFromRawName(name);
			if (IsNormalEnemyKey(fromName))
			{
				return fromName;
			}
		}
		return null;
	}

	private static string ClassifyFromRawName(string rawName)
	{
		string lower = NameRemap.StripCloneSuffix(rawName).ToLowerInvariant();
		if (lower.StartsWith("gargoyle", StringComparison.Ordinal))
		{
			return "gargoyle";
		}
		if (lower.StartsWith("imp", StringComparison.Ordinal) || lower.Contains("imp_enemy"))
		{
			return "imp";
		}
		if (lower.StartsWith("gooper", StringComparison.Ordinal))
		{
			return "gooper";
		}
		if (lower.StartsWith("plantasha", StringComparison.Ordinal) || lower.StartsWith("plantica", StringComparison.Ordinal))
		{
			return "plantasha";
		}
		if (lower.StartsWith("zombie", StringComparison.Ordinal))
		{
			return "zombie";
		}
		if (lower.StartsWith("hood", StringComparison.Ordinal) || lower.Contains("nun"))
		{
			return "nun";
		}
		if (lower.StartsWith("goonshroom", StringComparison.Ordinal) || lower.Contains("goonshroom"))
		{
			return "goonshroom";
		}
		return null;
	}

	/// <summary>
	/// One ordinary enemy prefab from the scene's own pool, for a custom boss calling in help.
	///
	/// Deliberately restricted to `IsNormalEnemyKey`: reinforcements come from the pool the level
	/// already uses, so a boss cannot summon another boss, and a scene with nothing ordinary in it
	/// gets null and simply does not reinforce.
	/// </summary>
	internal static GameObject PickNormalReinforcementPrefab()
	{
		EnsurePool();
		List<string> normalKeys = new List<string>();
		for (int i = 0; i < PoolKeys.Count; i++)
		{
			if (IsNormalEnemyKey(PoolKeys[i]) && PrefabsByKey.TryGetValue(PoolKeys[i], out var prefabs) && prefabs.Count > 0)
			{
				normalKeys.Add(PoolKeys[i]);
			}
		}
		if (normalKeys.Count == 0)
		{
			return null;
		}
		List<GameObject> variants = PrefabsByKey[normalKeys[Random.Range(0, normalKeys.Count)]];
		return variants[Random.Range(0, variants.Count)];
	}

	private static bool IsNormalEnemyKey(string key)
	{
		if (string.IsNullOrEmpty(key))
		{
			return false;
		}
		string[] families = PoolFamilies();
		for (int i = 0; i < families.Length; i++)
		{
			if (key.Equals(families[i], StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}
		return false;
	}

	internal static void SpawnRandomFromPoolDebug()
	{
		EnsurePool();
		if (PoolKeys.Count == 0)
		{
			Plugin.DBG("SPAWN", "shuffle debug: pool empty");
			return;
		}
		string key = PickWeightedKey();
		if (string.IsNullOrEmpty(key) || !PrefabsByKey.TryGetValue(key, out var prefabs) || prefabs.Count == 0)
		{
			Plugin.DBG("SPAWN", "shuffle debug: no prefab for key '" + key + "'");
			return;
		}
		GameObject template = prefabs[Random.Range(0, prefabs.Count)];
		Plugin.DBG("SPAWN", "shuffle debug pool: " + FormatPoolSummary());
		DebugEnemySpawn.SpawnPrefabInstance(template, "shuffle debug [" + key + "]");
	}

	private static string FormatPoolSummary()
	{
		StringBuilder stringBuilder = new StringBuilder();
		for (int i = 0; i < PoolKeys.Count; i++)
		{
			if (i > 0)
			{
				stringBuilder.Append(", ");
			}
			string key = PoolKeys[i];
			int count = (PrefabsByKey.TryGetValue(key, out var prefabs) ? prefabs.Count : 0);
			stringBuilder.Append(key).Append('(').Append(count)
				.Append(')');
		}
		return stringBuilder.ToString();
	}

	private static string PickWeightedKey()
	{
		float total = 0f;
		for (int i = 0; i < _weights.Length; i++)
		{
			total += _weights[i];
		}
		if (total <= 0f)
		{
			return PoolKeys[Random.Range(0, PoolKeys.Count)];
		}
		float roll = Random.Range(0f, total);
		for (int j = 0; j < _weights.Length; j++)
		{
			roll -= _weights[j];
			if (roll <= 0f)
			{
				return PoolKeys[j];
			}
		}
		return PoolKeys[PoolKeys.Count - 1];
	}

	// Which of the two spawn behaviours is in force. Returning null from either means "vanilla",
	// and both prefixes then return true so the game's own GetEnemyPrefab runs.
	internal static GameObject PickForSpawner(MonoBehaviour spawner)
	{
		if (!Enabled || spawner == null)
		{
			return null;
		}
		string mode = ((OverkeksMode.UsesRecipe ? OverkeksConfig.CfgEnemySpawnMode : Plugin.CfgEnemySpawnMode)?.Value ?? "grappler-bias").Trim();
		if (mode.Equals("pool", StringComparison.OrdinalIgnoreCase))
		{
			return PickShuffledPrefab(spawner);
		}
		if (mode.Equals("vanilla", StringComparison.OrdinalIgnoreCase))
		{
			return null;
		}
		return PickBiasedPrefab(spawner);
	}

	[HarmonyPatch(typeof(EnemySpawner), "GetEnemyPrefab")]
	[HarmonyPrefix]
	private static bool EnemySpawner_GetEnemyPrefab_Prefix(EnemySpawner __instance, ref GameObject __result)
	{
		GameObject prefab = PickForSpawner((MonoBehaviour)(object)__instance);
		if (prefab == null)
		{
			return true;
		}
		__result = prefab;
		return false;
	}

	[HarmonyPatch(typeof(ArenaEnemySpawner), "GetEnemyPrefab")]
	[HarmonyPrefix]
	private static bool ArenaEnemySpawner_GetEnemyPrefab_Prefix(ArenaEnemySpawner __instance, ref GameObject __result)
	{
		GameObject prefab = PickForSpawner((MonoBehaviour)(object)__instance);
		if (prefab == null)
		{
			return true;
		}
		__result = prefab;
		return false;
	}

	private static IEnumerator ActivateGargoyleAfterSpawn(MonoBehaviour spawner, GameObject prefabTemplate)
	{
		yield return (object)new WaitForSeconds(0.15f);
		if (spawner == null || prefabTemplate == null)
		{
			yield break;
		}
		List<GameObject> spawned = Traverse.Create((object)spawner).Field("spawnedEnemies").GetValue<List<GameObject>>();
		if (spawned == null || spawned.Count == 0)
		{
			yield break;
		}
		string templateName = NameRemap.StripCloneSuffix(prefabTemplate.name);
		for (int i = spawned.Count - 1; i >= 0; i--)
		{
			GameObject enemy = spawned[i];
			if (!(enemy == null))
			{
				string enemyName = NameRemap.StripCloneSuffix(enemy.name);
				if (enemyName.StartsWith(templateName, StringComparison.OrdinalIgnoreCase) || enemyName.StartsWith("Gargoyle", StringComparison.OrdinalIgnoreCase))
				{
					DebugEnemySpawn.EnsureSpawnActive(enemy);
					Plugin.DBG("SPAWN", "shuffle gargoyle activated '" + enemyName + "'");
					break;
				}
			}
		}
	}
}
