using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PncEdi;

[HarmonyPatch]
internal static class OverkeksEnemySpawnShuffle
{
    internal static void WarmCurrentScene() => EnsurePool();
	private static int _cachedSceneHandle = -1;

	private static readonly Dictionary<string, List<GameObject>> PrefabsByKey = new Dictionary<string, List<GameObject>>(StringComparer.OrdinalIgnoreCase);

	private static readonly List<string> PoolKeys = new List<string>();

	private static List<GameObject> _blindedBeastPrefabs;

	private static float[] _weights;

	private static readonly HashSet<int> _loggedSpawners = new HashSet<int>();

	private static bool Enabled
	{
		get
		{
			if (OverkeksMode.Active)
			{
				return OverkeksConfig.CfgEnemySpawnShuffle.Value;
			}
			return false;
		}
	}

	private static float ImpWeight => Mathf.Max(1f, OverkeksConfig.CfgImpSpawnWeightMultiplier.Value);

	private static float BlindedBeastWeight => Mathf.Max(0.01f, OverkeksConfig.CfgBlindedBeastSpawnWeightMultiplier?.Value ?? 0.15f);

	internal static void ClearCache()
	{
		_loggedSpawners.Clear();
		_cachedSceneHandle = -1;
		PrefabsByKey.Clear();
		PoolKeys.Clear();
		_blindedBeastPrefabs = null;
		_weights = null;
	}

	private static string[] GrapplerKeys()
	{
		string[] array = (OverkeksConfig.CfgGrapplerSpawnKeys?.Value ?? "").Split(';');
		int newSize = 0;
		for (int i = 0; i < array.Length; i++)
		{
			string text = array[i].Trim();
			if (text.Length != 0)
			{
				array[newSize++] = text;
			}
		}
		Array.Resize(ref array, newSize);
		return array;
	}

	private static bool IsGrapplerData(EnemyData data, string[] grapplers)
	{
		if (data == null)
		{
			return false;
		}
		string enemyName = data.enemyName;
		if (string.IsNullOrEmpty(enemyName))
		{
			return false;
		}
		for (int i = 0; i < grapplers.Length; i++)
		{
			if (enemyName.Trim().Equals(grapplers[i], StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}
		return false;
	}

	private static EnemyData[] SpawnerTable(MonoBehaviour spawner)
	{
		return Traverse.Create((object)spawner).Field("enemyData").GetValue<EnemyData[]>();
	}

	internal static GameObject PickBiasedPrefab(MonoBehaviour spawner)
	{
		EnemyData[] array = SpawnerTable(spawner);
		if (array == null || array.Length == 0)
		{
			return null;
		}
		string[] grapplers = GrapplerKeys();
		float num = Mathf.Max(0.01f, OverkeksConfig.CfgGrapplerSpawnWeight.Value);
		float num2 = 0f;
		int num3 = 0;
		for (int i = 0; i < array.Length; i++)
		{
			if (!(array[i] == null))
			{
				bool flag = IsGrapplerData(array[i], grapplers);
				if (flag)
				{
					num3++;
				}
				num2 += (flag ? num : 1f);
			}
		}
		if (num3 == 0 || num2 <= 0f)
		{
			return null;
		}
		float num4 = UnityEngine.Random.Range(0f, num2);
		EnemyData enemyData = null;
		for (int j = 0; j < array.Length; j++)
		{
			if (!(array[j] == null))
			{
				num4 -= (IsGrapplerData(array[j], grapplers) ? num : 1f);
				if (num4 <= 0f)
				{
					enemyData = array[j];
					break;
				}
			}
		}
		if (enemyData == null)
		{
			return null;
		}
		GameObject randomPrefab = enemyData.GetRandomPrefab();
		if (randomPrefab == null)
		{
			return null;
		}
		LogBiasOnce(spawner, array, num3, num);
		if (randomPrefab.name.IndexOf("gargoyle", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			OverkeksMode.StartCoroutine(ActivateGargoyleAfterSpawn(spawner, randomPrefab));
		}
		return randomPrefab;
	}

	private static void LogBiasOnce(MonoBehaviour spawner, EnemyData[] table, int grapplerCount, float weight)
	{
		int instanceID = spawner.GetInstanceID();
		if (!_loggedSpawners.Add(instanceID))
		{
			return;
		}
		StringBuilder stringBuilder = new StringBuilder();
		for (int i = 0; i < table.Length; i++)
		{
			if (stringBuilder.Length > 0)
			{
				stringBuilder.Append(", ");
			}
			stringBuilder.Append((table[i] == null) ? "<null>" : table[i].enemyName);
		}
		Plugin.DBG("SPAWN", "table [" + stringBuilder.ToString() + "] " + grapplerCount + " grappler(s) at x" + weight);
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
		string text = PickWeightedKey();
		if (string.IsNullOrEmpty(text) || !PrefabsByKey.TryGetValue(text, out var value) || value.Count == 0)
		{
			return null;
		}
		GameObject gameObject = value[UnityEngine.Random.Range(0, value.Count)];
		if (text.Equals("gargoyle", StringComparison.OrdinalIgnoreCase))
		{
			OverkeksMode.StartCoroutine(ActivateGargoyleAfterSpawn(spawner, gameObject));
		}
		else if (text.Equals("baphomet", StringComparison.OrdinalIgnoreCase))
		{
			OverkeksMode.StartCoroutine(ConvertBaphometAfterSpawn(spawner, gameObject));
		}
		else if (text.Equals("gravy", StringComparison.OrdinalIgnoreCase))
		{
			OverkeksMode.StartCoroutine(ConvertGravyAfterSpawn(spawner, gameObject));
		}
		return gameObject;
	}

	private static void EnsurePool()
	{
		int num = SceneManager.GetActiveScene().handle;
		if (_cachedSceneHandle != num || PoolKeys.Count <= 0)
		{
			RebuildPool(num);
		}
	}

	private static void RebuildPool(int sceneHandle)
	{
		PrefabsByKey.Clear();
		PoolKeys.Clear();
		_weights = null;
		_cachedSceneHandle = sceneHandle;
		string[] array = PoolFamilies();
		for (int i = 0; i < array.Length; i++)
		{
			string family = array[i];
			string text = HintForFamily(family);
			if (string.IsNullOrEmpty(text))
			{
				Plugin.DBG("SPAWN", "shuffle family '" + family + "': no Tools/Spawn<Key>NameHint - skipped");
			}
			else if (family.Equals("baphomet", StringComparison.OrdinalIgnoreCase))
			{
				CollectDebugHint(text, "baphomet");
			}
			else if (family.Equals("gravy", StringComparison.OrdinalIgnoreCase))
			{
				CollectDebugHint(text, "gravy");
			}
			else if (family.Equals("dragon", StringComparison.OrdinalIgnoreCase))
			{
				CollectDebugHint(text, "dragon");
			}
			else if (family.Equals("wendigo", StringComparison.OrdinalIgnoreCase))
			{
				CollectDebugHint(text, "wendigo");
			}
			else
			{
				CollectDebugHint(text);
			}
		}
		foreach (KeyValuePair<string, GameObject> item in CustomEnemyBridge.ShufflePool())
		{
			TryAddPrefab(item.Value, item.Key);
		}
		if (!PrefabsByKey.ContainsKey("gargoyle"))
		{
			DebugEnemySpawn.ClearCache();
			CollectDebugHint(OverkeksConfig.CfgSpawnGargoyleNameHint?.Value ?? "Gargoyle|Gargoyle Alt|gargoyle");
		}
		PoolKeys.AddRange(PrefabsByKey.Keys);
		_weights = new float[PoolKeys.Count];
		for (int j = 0; j < PoolKeys.Count; j++)
		{
			string key = PoolKeys[j];
			if (key.Equals("imp", StringComparison.OrdinalIgnoreCase))
			{
				_weights[j] = ImpWeight;
			}
			else if (key.Equals("blinded_beast", StringComparison.OrdinalIgnoreCase))
			{
				_weights[j] = BlindedBeastWeight;
			}
			else
			{
				_weights[j] = CustomEnemyBridge.SpawnWeight(key);
			}
		}
		if (PoolKeys.Count > 0)
		{
			Plugin.DBG("SPAWN", "shuffle pool: " + FormatPoolSummary() + " (equal full-pool picks; weighted only for pool-mode/debug)");
		}
		else
		{
			Plugin.DBG("SPAWN", "shuffle pool: empty");
		}
	}

	private static string[] PoolFamilies()
	{
		string raw = OverkeksConfig.CfgEnemySpawnShufflePool?.Value ?? "";
		const string fullDefault = "imp;gargoyle;gooper;nun;zombie;plantasha;goonshroom;serpent;blinded_beast;baphomet;gravy;dragon;wendigo";
		const string oldDefault = "imp;gargoyle;gooper;nun;zombie;plantasha;goonshroom;serpent;blinded_beast";
		const string midDefault = "imp;gargoyle;gooper;nun;zombie;plantasha;goonshroom;serpent;blinded_beast;baphomet";
		const string gravyDefault = "imp;gargoyle;gooper;nun;zombie;plantasha;goonshroom;serpent;blinded_beast;baphomet;gravy";
		if (OverkeksConfig.CfgEnemySpawnShufflePool != null)
		{
			if (string.Equals(raw.Trim(), oldDefault, StringComparison.OrdinalIgnoreCase)
				|| string.Equals(raw.Trim(), midDefault, StringComparison.OrdinalIgnoreCase)
				|| string.Equals(raw.Trim(), gravyDefault, StringComparison.OrdinalIgnoreCase)
				|| string.IsNullOrEmpty(raw.Trim()))
			{
				OverkeksConfig.CfgEnemySpawnShufflePool.Value = fullDefault;
				raw = OverkeksConfig.CfgEnemySpawnShufflePool.Value;
			}
			else
			{
				string lower = ";" + raw.ToLowerInvariant().Replace(" ", "") + ";";
				string next = raw.Trim().TrimEnd(';');
				bool changed = false;
				if (lower.IndexOf(";baphomet;", StringComparison.Ordinal) < 0)
				{
					next += ";baphomet";
					changed = true;
				}
				if (lower.IndexOf(";gravy;", StringComparison.Ordinal) < 0)
				{
					next += ";gravy";
					changed = true;
				}
				if (lower.IndexOf(";dragon;", StringComparison.Ordinal) < 0)
				{
					next += ";dragon";
					changed = true;
				}
				if (lower.IndexOf(";wendigo;", StringComparison.Ordinal) < 0)
				{
					next += ";wendigo";
					changed = true;
				}
				if (changed)
				{
					OverkeksConfig.CfgEnemySpawnShufflePool.Value = next;
					raw = next;
				}
			}
		}
		string[] array = raw.Split(';');
		int newSize = 0;
		for (int i = 0; i < array.Length; i++)
		{
			string text = array[i].Trim();
			if (text.Length != 0)
			{
				array[newSize++] = text;
			}
		}
		Array.Resize(ref array, newSize);
		return array;
	}

	private static string HintForFamily(string family)
	{
		if (family.Equals("imp", StringComparison.OrdinalIgnoreCase))
		{
			return OverkeksConfig.CfgSpawnImpNameHint?.Value ?? "imp";
		}
		if (family.Equals("gargoyle", StringComparison.OrdinalIgnoreCase))
		{
			return OverkeksConfig.CfgSpawnGargoyleNameHint?.Value ?? "Gargoyle|Gargoyle Alt|gargoyle";
		}
		if (family.Equals("gooper", StringComparison.OrdinalIgnoreCase))
		{
			return OverkeksConfig.CfgSpawnGooperNameHint?.Value ?? "gooper";
		}
		if (family.Equals("nun", StringComparison.OrdinalIgnoreCase))
		{
			return OverkeksConfig.CfgSpawnNunNameHint?.Value ?? "hood|nun";
		}
		if (family.Equals("zombie", StringComparison.OrdinalIgnoreCase))
		{
			return OverkeksConfig.CfgSpawnZombieNameHint?.Value ?? "zombie";
		}
		if (family.Equals("plantasha", StringComparison.OrdinalIgnoreCase))
		{
			return OverkeksConfig.CfgSpawnPlantashaNameHint?.Value ?? "plantasha";
		}
		if (family.Equals("goonshroom", StringComparison.OrdinalIgnoreCase))
		{
			return OverkeksConfig.CfgSpawnGoonShroomNameHint?.Value ?? "goonshroom";
		}
		if (family.Equals("blinded_beast", StringComparison.OrdinalIgnoreCase))
		{
			return OverkeksConfig.CfgSpawnBlindedBeastNameHint?.Value ?? "blindedbeast|blinded beast|blinded";
		}
		if (family.Equals("serpent", StringComparison.OrdinalIgnoreCase))
		{
			return OverkeksConfig.CfgSpawnSerpentNameHint?.Value ?? "black serpent|blackserpent|serpent";
		}
		if (family.Equals("baphomet", StringComparison.OrdinalIgnoreCase))
		{
			// Same base prefab as gargoyle; converted after spawn.
			return OverkeksConfig.CfgSpawnGargoyleNameHint?.Value ?? "Gargoyle|Gargoyle Alt|gargoyle";
		}
		if (family.Equals("gravy", StringComparison.OrdinalIgnoreCase))
		{
			// Same base prefab as serpent; converted after spawn.
			return OverkeksConfig.CfgSpawnSerpentNameHint?.Value ?? "black serpent|blackserpent|serpent";
		}
		if (family.Equals("dragon", StringComparison.OrdinalIgnoreCase))
		{
			return OverkeksConfig.CfgSpawnDragonNameHint?.Value ?? "dragon";
		}
		if (family.Equals("wendigo", StringComparison.OrdinalIgnoreCase))
		{
			return OverkeksConfig.CfgSpawnWendigoNameHint?.Value ?? "wendigo";
		}
		return null;
	}

	private static void CollectDebugHint(string nameHint, string forcedKey = null)
	{
		if (string.IsNullOrWhiteSpace(nameHint))
		{
			return;
		}
		string text = forcedKey;
		if (string.IsNullOrEmpty(text))
		{
			text = ResolveShuffleKeyFromHint(nameHint);
			if (text == null && MatchesGargoyleShuffleHint(nameHint))
			{
				text = "gargoyle";
			}
		}
		List<GameObject> spawnVariants = DebugEnemySpawn.GetSpawnVariants(nameHint, "shuffle");
		if (spawnVariants == null || spawnVariants.Count == 0)
		{
			Plugin.DBG("SPAWN", "shuffle hint '" + nameHint + "': no prefabs");
			return;
		}
		for (int i = 0; i < spawnVariants.Count; i++)
		{
			TryAddPrefab(GetSpawnTemplateRoot(spawnVariants[i]), text);
		}
	}

	private static bool MatchesGargoyleShuffleHint(string nameHint)
	{
		if (!string.IsNullOrWhiteSpace(nameHint))
		{
			return nameHint.IndexOf("gargoyle", StringComparison.OrdinalIgnoreCase) >= 0;
		}
		return false;
	}

	private static string ResolveShuffleKeyFromHint(string nameHint)
	{
		if (string.IsNullOrWhiteSpace(nameHint))
		{
			return null;
		}
		string[] array = nameHint.Split('|');
		for (int i = 0; i < array.Length; i++)
		{
			string text = array[i].Trim();
			if (text.Length != 0)
			{
				string text2 = NameRemap.ResolveEnemyKey(text);
				if (IsNormalEnemyKey(text2))
				{
					return text2;
				}
				text2 = ClassifyFromRawName(text);
				if (IsNormalEnemyKey(text2))
				{
					return text2;
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
		GameObject gameObject = prefab;
		while (gameObject.transform.parent != null)
		{
			GameObject gameObject2 = gameObject.transform.parent.gameObject;
			if (!(gameObject2.GetComponentInChildren<EnemyAI>(includeInactive: true) != null)
				&& !(gameObject2.GetComponentInChildren<ChargingEnemyAI>(includeInactive: true) != null)
				&& !(gameObject2.GetComponentInChildren<SpinningEnemyAI>(includeInactive: true) != null)
				&& !(gameObject2.GetComponentInChildren<DragonEnemyAI>(includeInactive: true) != null)
				&& !(gameObject2.GetComponentInChildren<ProximityDragonEnemyAI>(includeInactive: true) != null))
			{
				break;
			}
			gameObject = gameObject2;
		}
		return gameObject;
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
		string text = forcedKey ?? ClassifyPrefabKey(prefab);
		if (string.IsNullOrEmpty(text))
		{
			Plugin.DBG("SPAWN", "shuffle skip unclassified prefab '" + NameRemap.StripCloneSuffix(prefab.name) + "'");
			return;
		}
		if (!PrefabsByKey.TryGetValue(text, out var value))
		{
			value = new List<GameObject>();
			PrefabsByKey[text] = value;
		}
		for (int i = 0; i < value.Count; i++)
		{
			if (value[i] == prefab)
			{
				return;
			}
		}
		value.Add(prefab);
	}

	private static bool IsTrustedShufflePrefab(GameObject prefab, string forcedKey)
	{
		if (prefab == null || string.IsNullOrEmpty(forcedKey))
		{
			return false;
		}
		if (CustomEnemyBridge.IsCustomKey(forcedKey))
		{
			return true;
		}
		if (forcedKey.Equals("dragon", StringComparison.OrdinalIgnoreCase)
			|| forcedKey.Equals("wendigo", StringComparison.OrdinalIgnoreCase))
		{
			if (!OverkeksChaserBossHelper.IsChaserBossPrefab(prefab))
			{
				return false;
			}
			string name = NameRemap.StripCloneSuffix(prefab.name);
			bool isWendigo = name.IndexOf("wendigo", StringComparison.OrdinalIgnoreCase) >= 0;
			if (forcedKey.Equals("wendigo", StringComparison.OrdinalIgnoreCase))
			{
				return isWendigo;
			}
			// dragon: chaser prefab that is not a wendigo reskin
			return !isWendigo;
		}
		if (OverkeksChaserBossHelper.IsChaserBossPrefab(prefab))
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
		if ((forcedKey.Equals("gargoyle", StringComparison.OrdinalIgnoreCase) || forcedKey.Equals("baphomet", StringComparison.OrdinalIgnoreCase))
			&& DebugEnemySpawn.IsBlockedGargoyleSpawnName(prefab.name))
		{
			return false;
		}
		if (forcedKey.Equals("gravy", StringComparison.OrdinalIgnoreCase))
		{
			string name = NameRemap.StripCloneSuffix(prefab.name);
			if (prefab.GetComponent<BrawlerEnemyAI>() == null)
			{
				return false;
			}
			if (name.IndexOf("serpent", StringComparison.OrdinalIgnoreCase) < 0
				&& name.IndexOf("gravy", StringComparison.OrdinalIgnoreCase) < 0
				&& name.IndexOf("minotaur", StringComparison.OrdinalIgnoreCase) < 0)
			{
				return false;
			}
			if (name.IndexOf("blinded", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return false;
			}
		}
		if (forcedKey.Equals("baphomet", StringComparison.OrdinalIgnoreCase))
		{
			string name = NameRemap.StripCloneSuffix(prefab.name);
			if (name.IndexOf("gargoyle", StringComparison.OrdinalIgnoreCase) < 0
				&& name.IndexOf("baphomet", StringComparison.OrdinalIgnoreCase) < 0)
			{
				return false;
			}
			if (name.IndexOf("blinded", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return false;
			}
		}
		return true;
	}

	private static bool IsNormalSpawnPrefab(GameObject prefab)
	{
		if (prefab == null)
		{
			return false;
		}
		string text = NameRemap.StripCloneSuffix(prefab.name);
		if (text.Equals("Gargoyle", StringComparison.OrdinalIgnoreCase) || text.Equals("Gargoyle Alt", StringComparison.OrdinalIgnoreCase) || text.Equals("Gooper", StringComparison.OrdinalIgnoreCase) || text.StartsWith("Gooper ", StringComparison.OrdinalIgnoreCase))
		{
			if (!OverkeksChaserBossHelper.IsChaserBossPrefab(prefab) && prefab.GetComponent<CameraSwapTrigger>() == null)
			{
				return prefab.GetComponent<MimicEnemy>() == null;
			}
			return false;
		}
		if (DebugEnemySpawn.IsBlockedGargoyleSpawnName(prefab.name))
		{
			return false;
		}
		if (OverkeksChaserBossHelper.IsChaserBossPrefab(prefab))
		{
			return false;
		}
		if (prefab.GetComponent<ProjectileEnemyAI>() != null && prefab.GetComponentInChildren<EnemyAI>(includeInactive: true) == null && prefab.GetComponentInChildren<ChargingEnemyAI>(includeInactive: true) == null && prefab.GetComponentInChildren<SpinningEnemyAI>(includeInactive: true) == null)
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
		if (!(prefab.GetComponentInChildren<EnemyAI>(includeInactive: true) != null) && !(prefab.GetComponentInChildren<ChargingEnemyAI>(includeInactive: true) != null) && !(prefab.GetComponentInChildren<SpinningEnemyAI>(includeInactive: true) != null))
		{
			return !string.IsNullOrEmpty(ClassifyPrefabKey(prefab));
		}
		return true;
	}

	private static string ClassifyPrefabKey(GameObject prefab)
	{
		EnemyAI componentInChildren = prefab.GetComponentInChildren<EnemyAI>(includeInactive: true);
		if (componentInChildren != null)
		{
			string text = ClassifyEnemyKey(prefab.name, componentInChildren.galleryEnemyID);
			if (!string.IsNullOrEmpty(text))
			{
				return text;
			}
		}
		ChargingEnemyAI componentInChildren2 = prefab.GetComponentInChildren<ChargingEnemyAI>(includeInactive: true);
		if (componentInChildren2 != null)
		{
			string text2 = ClassifyEnemyKey(prefab.name, componentInChildren2.galleryEnemyID);
			if (!string.IsNullOrEmpty(text2))
			{
				return text2;
			}
		}
		SpinningEnemyAI componentInChildren3 = prefab.GetComponentInChildren<SpinningEnemyAI>(includeInactive: true);
		if (componentInChildren3 != null)
		{
			string text3 = ClassifyEnemyKey(prefab.name, componentInChildren3.galleryEnemyID);
			if (!string.IsNullOrEmpty(text3))
			{
				return text3;
			}
		}
		return ClassifyEnemyKey(prefab.name, null);
	}

	private static string ClassifyEnemyKey(string name, string galleryId)
	{
		if (!string.IsNullOrEmpty(galleryId))
		{
			string text = NameRemap.ResolveEnemyKey(galleryId);
			if (IsNormalEnemyKey(text))
			{
				return text;
			}
		}
		if (!string.IsNullOrEmpty(name))
		{
			string text2 = NameRemap.ResolveEnemyKey(name);
			if (IsNormalEnemyKey(text2))
			{
				return text2;
			}
			text2 = ClassifyFromRawName(name);
			if (IsNormalEnemyKey(text2))
			{
				return text2;
			}
		}
		return null;
	}

	private static string ClassifyFromRawName(string rawName)
	{
		string text = NameRemap.StripCloneSuffix(rawName).ToLowerInvariant();
		if (text.StartsWith("gargoyle", StringComparison.Ordinal))
		{
			return "gargoyle";
		}
		if (text.StartsWith("imp", StringComparison.Ordinal) || text.Contains("imp_enemy"))
		{
			return "imp";
		}
		if (text.StartsWith("gooper", StringComparison.Ordinal))
		{
			return "gooper";
		}
		if (text.StartsWith("plantasha", StringComparison.Ordinal) || text.StartsWith("plantica", StringComparison.Ordinal))
		{
			return "plantasha";
		}
		if (text.StartsWith("zombie", StringComparison.Ordinal))
		{
			return "zombie";
		}
		if (text.StartsWith("hood", StringComparison.Ordinal) || text.Contains("nun"))
		{
			return "nun";
		}
		if (text.StartsWith("goonshroom", StringComparison.Ordinal) || text.Contains("goonshroom"))
		{
			return "goonshroom";
		}
		if (text.Contains("blinded") || text.Contains("werewolf"))
		{
			return "blinded_beast";
		}
		if (text.Contains("serpent"))
		{
			return "serpent";
		}
		if (text.Contains("wendigo"))
		{
			return "wendigo";
		}
		if (text.Contains("dragon"))
		{
			return "dragon";
		}
		return null;
	}

	internal static GameObject PickNormalReinforcementPrefab()
	{
		EnsurePool();
		List<string> list = new List<string>();
		for (int i = 0; i < PoolKeys.Count; i++)
		{
			if (!IsNormalEnemyKey(PoolKeys[i])
				|| PoolKeys[i].Equals("blinded_beast", StringComparison.OrdinalIgnoreCase)
				|| ChaserFodderSpawn.IsChaserFodderKey(PoolKeys[i]))
			{
				continue;
			}
			if (PrefabsByKey.TryGetValue(PoolKeys[i], out var value) && value.Count > 0)
			{
				list.Add(PoolKeys[i]);
			}
		}
		if (list.Count == 0)
		{
			return null;
		}
		List<GameObject> list2 = PrefabsByKey[list[UnityEngine.Random.Range(0, list.Count)]];
		return list2[UnityEngine.Random.Range(0, list2.Count)];
	}

	private static bool IsNormalEnemyKey(string key)
	{
		if (string.IsNullOrEmpty(key))
		{
			return false;
		}
		string[] array = PoolFamilies();
		for (int i = 0; i < array.Length; i++)
		{
			if (key.Equals(array[i], StringComparison.OrdinalIgnoreCase))
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
		string text = PickWeightedKey();
		if (string.IsNullOrEmpty(text) || !PrefabsByKey.TryGetValue(text, out var value) || value.Count == 0)
		{
			Plugin.DBG("SPAWN", "shuffle debug: no prefab for key '" + text + "'");
			return;
		}
		GameObject template = value[UnityEngine.Random.Range(0, value.Count)];
		Plugin.DBG("SPAWN", "shuffle debug pool: " + FormatPoolSummary());
		GameObject spawned = DebugEnemySpawn.SpawnPrefabInstance(template, "shuffle debug [" + text + "]");
		if (spawned == null)
		{
			return;
		}
		if (text.Equals("baphomet", StringComparison.OrdinalIgnoreCase))
		{
			if (BaphometGargoyleSpawn.TryConvert(spawned, out string detail))
			{
				OverkeksEnemyRuntime.RefreshConvertedActor(spawned, "baphomet");
				Plugin.DBG("SPAWN", "shuffle debug baphomet convert ok: " + detail);
			}
			else
			{
				Plugin.DBG("SPAWN", "shuffle debug baphomet convert failed: " + detail);
			}
		}
		else if (text.Equals("gravy", StringComparison.OrdinalIgnoreCase))
		{
			if (GravySerpentSpawn.TryConvert(spawned, out string detail))
			{
				OverkeksEnemyRuntime.RefreshConvertedActor(spawned, "gravy");
				Plugin.DBG("SPAWN", "shuffle debug gravy convert ok: " + detail);
			}
			else
			{
				Plugin.DBG("SPAWN", "shuffle debug gravy convert failed: " + detail);
			}
		}
		else if (ChaserFodderSpawn.IsChaserFodderKey(text))
		{
			OverkeksMode.AddOwnedComponent<OverkeksSpawnVariant>(spawned).Fodder=true;
			ChaserFodderSpawn.TryConsumePendingAndConvert(spawned);
		}
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
			string text = PoolKeys[i];
			List<GameObject> value2;
			int value = (PrefabsByKey.TryGetValue(text, out value2) ? value2.Count : 0);
			stringBuilder.Append(text).Append('(').Append(value)
				.Append(')');
		}
		return stringBuilder.ToString();
	}

	private static string PickWeightedKey()
	{
		float num = 0f;
		for (int i = 0; i < _weights.Length; i++)
		{
			num += _weights[i];
		}
		if (num <= 0f)
		{
			return PoolKeys[UnityEngine.Random.Range(0, PoolKeys.Count)];
		}
		float num2 = UnityEngine.Random.Range(0f, num);
		for (int j = 0; j < _weights.Length; j++)
		{
			num2 -= _weights[j];
			if (num2 <= 0f)
			{
				return PoolKeys[j];
			}
		}
		return PoolKeys[PoolKeys.Count - 1];
	}

	internal static GameObject PickForSpawner(MonoBehaviour spawner)
	{
		if (!Enabled || spawner == null)
		{
			return null;
		}
		string text = (OverkeksConfig.CfgEnemySpawnMode?.Value ?? "grappler-bias").Trim();
		if (text.Equals("vanilla", StringComparison.OrdinalIgnoreCase))
		{
			return null;
		}
		bool poolMode = text.Equals("pool", StringComparison.OrdinalIgnoreCase);
		GameObject picked = null;
		if (!poolMode)
		{
			picked = TryPickCrossAreaPrefab(spawner);
		}
		if (picked == null && (!poolMode || !PoolConfigContainsBlindedBeast()))
		{
			picked = TryPickRareBlindedBeast();
		}
		if (picked == null && poolMode)
		{
			picked = PickShuffledPrefab(spawner);
		}
		if (picked == null && !poolMode)
		{
			picked = PickBiasedPrefab(spawner);
		}
		// Arena boss tables keep full-strength beasts; only shuffle/rare/cross-area get weakened.
		if (picked != null && !(spawner is ArenaEnemySpawner) && BlindedBeastWeakSpawn.IsBlindedBeast(picked))
		{
			picked=OverkeksMode.PrepareVariant(picked,weak:true,fodder:false);
		}
		if (picked != null && !(spawner is ArenaEnemySpawner) && ChaserFodderSpawn.IsChaserFodderPrefab(picked))
		{
			picked=OverkeksMode.PrepareVariant(picked,weak:false,fodder:true);
		}
		return picked;
	}

	private static GameObject TryPickCrossAreaPrefab(MonoBehaviour spawner)
	{
		float chance = OverkeksConfig.CfgCrossAreaSpawnChance?.Value ?? 0f;
		if (chance <= 0f)
		{
			return null;
		}
		if (UnityEngine.Random.value > Mathf.Clamp01(chance))
		{
			return null;
		}
		EnsurePool();
		List<string> candidates = new List<string>();
		for (int i = 0; i < PoolKeys.Count; i++)
		{
			if (PrefabsByKey.TryGetValue(PoolKeys[i], out var prefabs) && prefabs != null && prefabs.Count > 0)
			{
				candidates.Add(PoolKeys[i]);
			}
		}
		if (candidates.Count == 0)
		{
			Plugin.DBG("SPAWN", "full-pool roll won but pool empty");
			return null;
		}
		string pickedKey = candidates[UnityEngine.Random.Range(0, candidates.Count)];
		List<GameObject> variants = PrefabsByKey[pickedKey];
		GameObject gameObject = variants[UnityEngine.Random.Range(0, variants.Count)];
		Plugin.DBG("SPAWN", "full-pool override -> " + pickedKey + " (" + chance + ")");
		if (pickedKey.Equals("gargoyle", StringComparison.OrdinalIgnoreCase))
		{
			OverkeksMode.StartCoroutine(ActivateGargoyleAfterSpawn(spawner, gameObject));
		}
		else if (pickedKey.Equals("baphomet", StringComparison.OrdinalIgnoreCase))
		{
			OverkeksMode.StartCoroutine(ConvertBaphometAfterSpawn(spawner, gameObject));
		}
		else if (pickedKey.Equals("gravy", StringComparison.OrdinalIgnoreCase))
		{
			OverkeksMode.StartCoroutine(ConvertGravyAfterSpawn(spawner, gameObject));
		}
		return gameObject;
	}

	private static bool PoolConfigContainsBlindedBeast()
	{
		string[] array = PoolFamilies();
		for (int i = 0; i < array.Length; i++)
		{
			if (array[i].Equals("blinded_beast", StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}
		return false;
	}

	private static GameObject TryPickRareBlindedBeast()
	{
		float chance = OverkeksConfig.CfgBlindedBeastSpawnChance?.Value ?? 0f;
		if (chance <= 0f)
		{
			return null;
		}
		if (UnityEngine.Random.value > Mathf.Clamp01(chance))
		{
			return null;
		}
		GameObject gameObject = ResolveBlindedBeastPrefab();
		if (gameObject == null)
		{
			Plugin.DBG("SPAWN", "rare blinded_beast roll won but prefab missing");
			return null;
		}
		Plugin.DBG("SPAWN", "rare blinded_beast override (" + chance + ")");
		return gameObject;
	}

	private static GameObject ResolveBlindedBeastPrefab()
	{
		if (_blindedBeastPrefabs != null && _blindedBeastPrefabs.Count > 0)
		{
			return _blindedBeastPrefabs[UnityEngine.Random.Range(0, _blindedBeastPrefabs.Count)];
		}
		if (PrefabsByKey.TryGetValue("blinded_beast", out var cached) && cached != null && cached.Count > 0)
		{
			_blindedBeastPrefabs = cached;
			return cached[UnityEngine.Random.Range(0, cached.Count)];
		}
		string hint = OverkeksConfig.CfgSpawnBlindedBeastNameHint?.Value ?? "blindedbeast|blinded beast|blinded";
		List<GameObject> spawnVariants = DebugEnemySpawn.GetSpawnVariants(hint, "blinded_beast-rare");
		if (spawnVariants == null || spawnVariants.Count == 0)
		{
			return null;
		}
		List<GameObject> list = new List<GameObject>();
		for (int i = 0; i < spawnVariants.Count; i++)
		{
			GameObject root = GetSpawnTemplateRoot(spawnVariants[i]);
			if (root == null || !IsTrustedShufflePrefab(root, "blinded_beast"))
			{
				continue;
			}
			bool already = false;
			for (int j = 0; j < list.Count; j++)
			{
				if (list[j] == root)
				{
					already = true;
					break;
				}
			}
			if (!already)
			{
				list.Add(root);
			}
		}
		if (list.Count == 0)
		{
			return null;
		}
		_blindedBeastPrefabs = list;
		return list[UnityEngine.Random.Range(0, list.Count)];
	}

	[HarmonyPatch(typeof(EnemySpawner), "GetEnemyPrefab")]
	[HarmonyPrefix]
	private static bool EnemySpawner_GetEnemyPrefab_Prefix(EnemySpawner __instance, ref GameObject __result)
	{
 if(!OverkeksMode.Active) return true;
		GameObject gameObject = PickForSpawner(__instance);
		if (gameObject == null)
		{
			return true;
		}
		__result = gameObject;
		return false;
	}

	[HarmonyPatch(typeof(ArenaEnemySpawner), "GetEnemyPrefab")]
	[HarmonyPrefix]
	private static bool ArenaEnemySpawner_GetEnemyPrefab_Prefix(ArenaEnemySpawner __instance, ref GameObject __result)
	{
 if(!OverkeksMode.Active) return true;
		GameObject gameObject = PickForSpawner(__instance);
		if (gameObject == null)
		{
			return true;
		}
		__result = gameObject;
		return false;
	}

	private static IEnumerator ActivateGargoyleAfterSpawn(MonoBehaviour spawner, GameObject prefabTemplate)
	{
		yield return new WaitForSeconds(0.15f);
		if (spawner == null || prefabTemplate == null)
		{
			yield break;
		}
		List<GameObject> value = Traverse.Create((object)spawner).Field("spawnedEnemies").GetValue<List<GameObject>>();
		if (value == null || value.Count == 0)
		{
			yield break;
		}
		string value2 = NameRemap.StripCloneSuffix(prefabTemplate.name);
		for (int num = value.Count - 1; num >= 0; num--)
		{
			GameObject gameObject = value[num];
			if (!(gameObject == null) && OverkeksEnemyRuntime.SpawnedInMode(gameObject))
			{
				string text = NameRemap.StripCloneSuffix(gameObject.name);
				if (text.StartsWith(value2, StringComparison.OrdinalIgnoreCase) || text.StartsWith("Gargoyle", StringComparison.OrdinalIgnoreCase))
				{
					DebugEnemySpawn.EnsureSpawnActive(gameObject);
					Plugin.DBG("SPAWN", "shuffle gargoyle activated '" + text + "'");
					break;
				}
			}
		}
	}

	private static IEnumerator ConvertBaphometAfterSpawn(MonoBehaviour spawner, GameObject prefabTemplate)
	{
		yield return new WaitForSeconds(0.15f);
		if (spawner == null || prefabTemplate == null)
		{
			yield break;
		}
		List<GameObject> value = Traverse.Create((object)spawner).Field("spawnedEnemies").GetValue<List<GameObject>>();
		if (value == null || value.Count == 0)
		{
			yield break;
		}
		string value2 = NameRemap.StripCloneSuffix(prefabTemplate.name);
		for (int num = value.Count - 1; num >= 0; num--)
		{
			GameObject gameObject = value[num];
			if (gameObject == null || !OverkeksEnemyRuntime.SpawnedInMode(gameObject))
			{
				continue;
			}
			string text = NameRemap.StripCloneSuffix(gameObject.name);
			if (!text.StartsWith(value2, StringComparison.OrdinalIgnoreCase) && !text.StartsWith("Gargoyle", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			if (gameObject.GetComponent<BaphometGargoyleMarker>() != null)
			{
				break;
			}
			DebugEnemySpawn.EnsureSpawnActive(gameObject);
			var conversionClock=System.Diagnostics.Stopwatch.StartNew();
			if (BaphometGargoyleSpawn.TryConvert(gameObject, out string detail))
			{
				OverkeksEnemyRuntime.RefreshConvertedActor(gameObject, "baphomet");
				conversionClock.Stop();
				Plugin.DBG("SPAWN-PERF", "phase=baphomet-conversion; totalMs="+conversionClock.Elapsed.TotalMilliseconds.ToString("0.###",System.Globalization.CultureInfo.InvariantCulture));
				Plugin.DBG("SPAWN", "shuffle baphomet convert ok '" + text + "' " + detail);
			}
			else
			{
				Plugin.DBG("SPAWN", "shuffle baphomet convert failed '" + text + "': " + detail);
			}
			break;
		}
	}

	private static IEnumerator ConvertGravyAfterSpawn(MonoBehaviour spawner, GameObject prefabTemplate)
	{
		yield return new WaitForSeconds(0.15f);
		if (spawner == null || prefabTemplate == null)
		{
			yield break;
		}
		List<GameObject> value = Traverse.Create((object)spawner).Field("spawnedEnemies").GetValue<List<GameObject>>();
		if (value == null || value.Count == 0)
		{
			yield break;
		}
		string value2 = NameRemap.StripCloneSuffix(prefabTemplate.name);
		for (int num = value.Count - 1; num >= 0; num--)
		{
			GameObject gameObject = value[num];
			if (gameObject == null || !OverkeksEnemyRuntime.SpawnedInMode(gameObject))
			{
				continue;
			}
			string text = NameRemap.StripCloneSuffix(gameObject.name);
			if (!text.StartsWith(value2, StringComparison.OrdinalIgnoreCase)
				&& text.IndexOf("serpent", StringComparison.OrdinalIgnoreCase) < 0
				&& text.IndexOf("Gravy", StringComparison.OrdinalIgnoreCase) < 0)
			{
				continue;
			}
			if (gameObject.GetComponent<GravySerpentMarker>() != null)
			{
				break;
			}
			DebugEnemySpawn.EnsureSpawnActive(gameObject);
			if (GravySerpentSpawn.TryConvert(gameObject, out string detail))
			{
				OverkeksEnemyRuntime.RefreshConvertedActor(gameObject, "gravy");
				Plugin.DBG("SPAWN", "shuffle gravy convert ok '" + text + "' " + detail);
			}
			else
			{
				Plugin.DBG("SPAWN", "shuffle gravy convert failed '" + text + "': " + detail);
			}
			break;
		}
	}
}
