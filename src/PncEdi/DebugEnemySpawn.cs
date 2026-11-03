using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using BepInEx.Unity.Mono.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using Random = UnityEngine.Random;
using Object = UnityEngine.Object;

namespace PncEdi;

internal static class DebugEnemySpawn
{
	internal struct SpawnBinding
	{
		public ConfigEntry<KeyboardShortcut> Key;
		public ConfigEntry<string> Hint;
		public string Label;
	}

	private static readonly Dictionary<string, List<GameObject>> VariantCache = new Dictionary<string, List<GameObject>>(StringComparer.OrdinalIgnoreCase);
	private static readonly Dictionary<string, int> VariantIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
	private static SpawnBinding[] _bindings;
	internal const string DefaultGargoyleSpawnHint = "Gargoyle|Gargoyle Alt|gargoyle";

	internal static void Configure(SpawnBinding[] bindings)
	{
		_bindings = bindings;
	}

	internal static void ClearCache()
	{
		VariantCache.Clear();
		VariantIndex.Clear();
	}

	internal static void TickHotkeys()
	{
		if (_bindings == null)
		{
			return;
		}
		for (int i = 0; i < _bindings.Length; i++)
		{
			SpawnBinding spawnBinding = _bindings[i];
			if (spawnBinding.Key != null)
			{
				if (Hotkeys.IsDown(spawnBinding.Key))
				{
					SpawnEnemy(spawnBinding.Hint?.Value, spawnBinding.Label);
				}
			}
		}
	}

	internal static void SpawnEnemy(string nameHint, string logLabel)
	{
		SpawnEnemyInstance(nameHint, logLabel);
	}

	internal unsafe static GameObject SpawnEnemyInstance(string nameHint, string logLabel)
	{
		if (string.IsNullOrWhiteSpace(nameHint))
		{
			Plugin.DBG("SPAWN", logLabel + ": empty name hint");
			return null;
		}
		List<GameObject> variants = ResolveVariants(nameHint, logLabel);
		if (variants == null || variants.Count == 0)
		{
			LogSpawnFailure(logLabel, nameHint);
			return null;
		}
		int picked;
		GameObject prefab = PickNextVariant(logLabel, variants, out picked);
		if (prefab == null)
		{
			Plugin.DBG("SPAWN", logLabel + ": no prefab picked (hint='" + nameHint + "')");
			return null;
		}
		if (!TryGetSpawnTransform(out var position, out var rotation))
		{
			Plugin.DBG("SPAWN", logLabel + ": player not found");
			return null;
		}
		GameObject instance = Object.Instantiate<GameObject>(prefab, position, rotation);
		EnsureSpawnActive(instance);
		SeatOnGroundNextFrame(instance, position.y, logLabel);
		string variantNote = ((variants.Count > 1) ? (" variant " + (picked + 1) + "/" + variants.Count) : "");
		Plugin.DBG("SPAWN", logLabel + variantNote + " '" + prefab.name + "' at " + ((object)(*(Vector3*)(&position))/*cast due to constrained. prefix*/).ToString());
		return instance;
	}

	internal unsafe static GameObject SpawnPrefabInstance(GameObject template, string logLabel)
	{
		if (template == null)
		{
			return null;
		}
		if (!TryGetSpawnTransform(out var position, out var rotation))
		{
			Plugin.DBG("SPAWN", logLabel + ": player not found");
			return null;
		}
		GameObject instance = Object.Instantiate<GameObject>(template, position, rotation);
		EnsureSpawnActive(instance);
		SeatOnGroundNextFrame(instance, position.y, logLabel);
		Plugin.DBG("SPAWN", logLabel + " '" + NameRemap.StripCloneSuffix(template.name) + "' at " + ((object)(*(Vector3*)(&position))/*cast due to constrained. prefix*/).ToString());
		return instance;
	}

	// TryGetSpawnTransform raycasts the floor and puts the prefab's ORIGIN there, which is only
	// the same thing as standing on it for a prefab whose art is drawn at its origin. Most are.
	// The two mimics are not: they are wall furniture whose sprite is drawn low inside its frame,
	// so dropping the origin to the floor buries the chest - the "mimic spawns mostly inside the
	// floor" from the 2026-08-24 run.
	//
	// Measuring the instance instead of tabulating an offset per prefab keeps this honest for
	// anything added later, including whatever a future game version reshapes. Lift only, never
	// lower: a prefab that deliberately floats (a gargoyle on a ledge, a serpent reared up) is not
	// a spawn to correct, and the 5 cm floor keeps an origin that is already right from being
	// nudged by rounding.
	//
	// WHAT VANILLA DOES, for comparison, because it is the target this is aiming at.
	// `ChestSpawner.SpawnMimicAtPosition` does `Instantiate(prefab, spawnPos.position, ...)` and
	// no seating maths at all: the height is hand-authored into the `ChestSpawnPoint` transform.
	// Read out of the shipped rooms, those points sit **+0.81 m** above the `FLOOR` objects beside
	// them (dungeon blood hall +0.810, CavernGasChoice +0.800), while an `Enemy Spawnpoint` sits
	// exactly *on* the floor. So the number to reproduce here is 0.81 m, not zero.
	//
	// One frame later, because `Instantiate` runs the prefab's `Awake` and nothing else: an enemy
	// that deactivates art in its own `Start` still has that art active when the same frame
	// measures it. The visible cost is one frame of the instance at the raycast height.
	private static void SeatOnGroundNextFrame(GameObject instance, float groundY, string logLabel)
	{
		if (instance == null || Plugin.Instance == null)
		{
			// No coroutine host: seat now and take the early measurement over none at all.
			SeatOnGround(instance, groundY, logLabel);
			return;
		}
		Plugin.Instance.StartCoroutine(SeatOnGroundRoutine(instance, groundY, logLabel));
	}

	private static IEnumerator SeatOnGroundRoutine(GameObject instance, float groundY, string logLabel)
	{
		yield return null;
		SeatOnGround(instance, groundY, logLabel);
	}

	// The bottom of the art this renderer actually draws, in world space.
	//
	// **`Renderer.bounds` on a SpriteRenderer is the sprite's whole `rect`, not its picture.**
	// That is the whole of the mimic bug and it is not obvious from either the C# or the log.
	// Both mimic sheets are 256x256 frames at 100 pixels-per-unit with a centred pivot, so the
	// quad Unity reports is 2.56 m tall and hangs exactly 1.28 m below the origin whatever is
	// drawn in it - which is why both variants reported the identical `lifted 1.28m` across three
	// runs, and why filtering hidden children (§120) and deferring a frame (§122) both changed
	// nothing. There is no hidden art: the prefab is one root and one `Sprite` child. 1.28 is
	// half a frame, a constant of the sheet.
	//
	// The picture inside that frame is what has to sit on the floor, and Unity does carry it:
	// `textureRectOffset` is the tight rect's corner within the full rect, in pixels from its
	// bottom-left, and `pivot` is the origin in the same pixels. The mimics read 46.08 and 128,
	// so the ink starts 0.819 m below the origin - which is the +0.81 m the level designers typed
	// into every ChestSpawnPoint, arrived at from the other direction.
	//
	// Rotation is ignored on purpose: these are upright billboards, and `Renderer.bounds` is kept
	// for everything that is not a sprite, where it is already the right answer.
	private static float VisibleBottom(Renderer renderer)
	{
		SpriteRenderer spriteRenderer = renderer as SpriteRenderer;
		if (spriteRenderer != null && spriteRenderer.sprite != null)
		{
			try
			{
				Sprite sprite = spriteRenderer.sprite;
				float pixelsPerUnit = sprite.pixelsPerUnit;
				if (pixelsPerUnit > 0f)
				{
					float inkBottom = (sprite.textureRectOffset.y - sprite.pivot.y) / pixelsPerUnit;
					float inkTop = inkBottom + sprite.textureRect.height / pixelsPerUnit;
					// flipY mirrors the quad about the pivot, so the ink's top edge becomes its
					// bottom one. Cheap to honour and silently wrong to skip.
					float local = (spriteRenderer.flipY ? (0f - inkTop) : inkBottom);
					return renderer.transform.position.y + local * renderer.transform.lossyScale.y;
				}
			}
			catch (Exception ex)
			{
				// textureRect is unreadable for a sprite packed at runtime. Falling back to the
				// full-rect bounds is the old behaviour, which is wrong for the mimics and right
				// for everything else - so say which renderer took it rather than seating quietly.
				Plugin.DBG("SPAWN", "could not read the sprite rect off '" + renderer.gameObject.name + "': " + ex.Message);
			}
		}
		return renderer.bounds.min.y;
	}

	private static void SeatOnGround(GameObject instance, float groundY, string logLabel)
	{
		if (instance == null)
		{
			return;
		}
		Renderer[] renderers = instance.GetComponentsInChildren<Renderer>(true);
		if (renderers == null || renderers.Length == 0)
		{
			return;
		}
		bool any = false;
		float lowest = 0f;
		string lowestName = "";
		for (int i = 0; i < renderers.Length; i++)
		{
			Renderer renderer = renderers[i];
			if (renderer == null)
			{
				continue;
			}
			// Only art that is actually on screen may decide where the floor is. The `true`
			// above is deliberate - a prefab can spawn with its body renderer briefly disabled -
			// but it also hands back every hidden child.
			if (!renderer.enabled || !renderer.gameObject.activeInHierarchy)
			{
				continue;
			}
			float min = VisibleBottom(renderer);
			if (!any || min < lowest)
			{
				lowest = min;
				lowestName = renderer.gameObject.name;
				any = true;
			}
		}
		if (!any)
		{
			return;
		}
		float lift = groundY - lowest;
		if (lift <= 0.05f)
		{
			return;
		}
		instance.transform.position += Vector3.up * lift;
		// The deciding renderer is named because the size of the lift is not enough to tell a
		// correct seat from a measurement against art nobody can see - twice now the number
		// alone left that open (§120, §122).
		Plugin.DBG("SPAWN", logLabel + ": lifted " + lift.ToString("F2") + "m so its art sits on the floor rather than through it"
			+ " (lowest visible renderer '" + lowestName + "')");
	}

	internal static void EnsureSpawnActive(GameObject enemy)
	{
		if (!(enemy == null))
		{
			enemy.SetActive(true);
			EnemyReactivationHelper.Reactivate(enemy);
		}
	}

	// The index comes back with the prefab rather than being reconstructed afterwards, and that
	// is the whole of the fix. IndexOfVariant used to undo the advance - (stored - 1) % Count -
	// which is not an inverse once the counter wraps: after the last variant the stored value is
	// 0, indistinguishable from "no entry yet", and its `value <= 0` guard then reported the last
	// variant as the first. Every two-variant key was therefore stuck on "variant 1/2" for both
	// halves of its cycle, which is what the 2026-08-24 run shows for the chaser boss - Wendigo
	// and Dragon, both logged 1/2 - and for the mimic, gooper and nun besides. The spawn itself
	// was always correct; only the number beside it lied.
	private static GameObject PickNextVariant(string logLabel, List<GameObject> variants, out int picked)
	{
		if (variants.Count == 1)
		{
			picked = 0;
			return variants[0];
		}
		if (!VariantIndex.TryGetValue(logLabel, out var next))
		{
			next = Random.Range(0, variants.Count);
		}
		picked = next % variants.Count;
		VariantIndex[logLabel] = (picked + 1) % variants.Count;
		return variants[picked];
	}

	internal static GameObject ResolvePrefabTemplate(string nameHint, string logLabel = null)
	{
		if (string.IsNullOrWhiteSpace(nameHint))
		{
			return null;
		}
		List<GameObject> gameObjects = ResolveVariants(nameHint, logLabel ?? nameHint);
		if (gameObjects == null || gameObjects.Count == 0)
		{
			return null;
		}
		return gameObjects[Random.Range(0, gameObjects.Count)];
	}

	internal static List<GameObject> GetSpawnVariants(string nameHint, string logLabel = null)
	{
		if (string.IsNullOrWhiteSpace(nameHint))
		{
			return null;
		}
		return ResolveVariants(nameHint, logLabel ?? nameHint);
	}

	internal static List<GameObject> ResolveGargoyleVariants(string logLabel)
	{
		return ResolveVariants("Gargoyle|Gargoyle Alt|gargoyle", logLabel);
	}

	internal static bool IsBlockedGargoyleSpawnName(string rawName)
	{
		if (string.IsNullOrEmpty(rawName))
		{
			return true;
		}
		string lower = NameRemap.StripCloneSuffix(rawName).ToLowerInvariant();
		if (lower.Equals("gargoyle", StringComparison.Ordinal) || lower.Equals("gargoyle alt", StringComparison.Ordinal))
		{
			return false;
		}
		if (!lower.Contains("gargoyle"))
		{
			return false;
		}
		return lower.Contains("stairs") || lower.Contains("gargoylex") || lower.StartsWith("gallery_gargoyle", StringComparison.Ordinal) || lower.Contains("ledge") || lower.Contains("wall ride") || lower.Contains("wallride") || lower.Contains("grabscreen") || lower.Contains("cumscreen") || lower.Contains("projectile");
	}

	internal static bool IsGalleryOrDioramaGargoyleName(string rawName)
	{
		return IsBlockedGargoyleSpawnName(rawName);
	}

	private static bool MatchesGargoyleHint(string hint)
	{
		return !string.IsNullOrEmpty(hint) && hint.IndexOf("gargoyle", StringComparison.OrdinalIgnoreCase) >= 0 && hint.IndexOf("stairs", StringComparison.OrdinalIgnoreCase) < 0 && hint.IndexOf("ledge", StringComparison.OrdinalIgnoreCase) < 0 && hint.IndexOf("wall ride", StringComparison.OrdinalIgnoreCase) < 0;
	}

	private static void CollectGargoylePrefabAssets(List<GameObject> output, HashSet<string> seen)
	{
		EnemyData[] enemyDatas = Resources.FindObjectsOfTypeAll<EnemyData>();
		foreach (EnemyData enemyData in enemyDatas)
		{
			if (enemyData == null)
			{
				continue;
			}
			string name = enemyData.enemyName + " " + enemyData.name;
			if (name.IndexOf("gargoyle", StringComparison.OrdinalIgnoreCase) < 0 || name.IndexOf("stairs", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				continue;
			}
			GameObject[] prefabVariants = enemyData.prefabVariants;
			if (prefabVariants != null)
			{
				for (int j = 0; j < prefabVariants.Length; j++)
				{
					TryAddGargoyleVariant(output, seen, prefabVariants[j]);
				}
			}
		}
		GameObject[] gameObjects = Resources.FindObjectsOfTypeAll<GameObject>();
		foreach (GameObject candidate in gameObjects)
		{
			if (!(candidate == null))
			{
				string name = NameRemap.StripCloneSuffix(candidate.name);
				if (name.Equals("Gargoyle", StringComparison.OrdinalIgnoreCase) || name.Equals("Gargoyle Alt", StringComparison.OrdinalIgnoreCase))
				{
					TryAddGargoyleVariant(output, seen, candidate);
				}
			}
		}
		EnemyAI[] enemyAIs = Resources.FindObjectsOfTypeAll<EnemyAI>();
		foreach (EnemyAI enemyAI in enemyAIs)
		{
			if (!(enemyAI == null))
			{
				GameObject spawnTemplateRoot = GetSpawnTemplateRoot(enemyAI.gameObject);
				if (MatchesSpawnHint(spawnTemplateRoot.name, "gargoyle") || MatchesSpawnHint(enemyAI.galleryEnemyID, "gargoyle"))
				{
					TryAddGargoyleVariant(output, seen, spawnTemplateRoot);
				}
			}
		}
	}

	private static void TryAddGargoyleVariant(List<GameObject> output, HashSet<string> seen, GameObject prefab)
	{
		if (!(prefab == null) && !IsBlockedGargoyleSpawnName(prefab.name) && !(prefab.GetComponent<CameraSwapTrigger>() != null) && !(prefab.GetComponent<ProjectileEnemyAI>() != null) && !(prefab.GetComponent<MimicEnemy>() != null))
		{
			TryAddVariant(output, seen, GetSpawnTemplateRoot(prefab));
		}
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
			if (parent.GetComponentInChildren<EnemyAI>(true) != null)
			{
				root = parent;
				continue;
			}
			break;
		}
		return root;
	}

	private static List<GameObject> ResolveVariants(string nameHint, string logLabel = null)
	{
		string key = nameHint + "|" + logLabel;
		if (VariantCache.TryGetValue(key, out var cached) && cached != null && cached.Count > 0)
		{
			return cached;
		}
		List<GameObject> gameObjects = new List<GameObject>();
		HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		if (MatchesGargoyleHint(nameHint))
		{
			CollectGargoylePrefabAssets(gameObjects, seen);
		}
		EnemySpawner[] enemySpawners = Object.FindObjectsByType<EnemySpawner>((FindObjectsSortMode)0);
		for (int i = 0; i < enemySpawners.Length; i++)
		{
			CollectInSpawner((MonoBehaviour)(object)enemySpawners[i], nameHint, gameObjects, seen);
		}
		ArenaEnemySpawner[] arenaEnemySpawners = Object.FindObjectsByType<ArenaEnemySpawner>((FindObjectsSortMode)0);
		for (int j = 0; j < arenaEnemySpawners.Length; j++)
		{
			CollectInSpawner((MonoBehaviour)(object)arenaEnemySpawners[j], nameHint, gameObjects, seen);
		}
		SweeperEnemySpawner[] sweeperEnemySpawners = Object.FindObjectsByType<SweeperEnemySpawner>((FindObjectsSortMode)0);
		for (int k = 0; k < sweeperEnemySpawners.Length; k++)
		{
			CollectInSweeper(sweeperEnemySpawners[k], nameHint, gameObjects, seen);
		}
		CollectLiveEnemyPrefabs(nameHint, gameObjects, seen);
		CollectFromAllLoadedAssets(nameHint, gameObjects, seen);
		if (MatchesGargoyleHint(nameHint))
		{
			CollectGargoylePrefabAssets(gameObjects, seen);
			Plugin.DBG("SPAWN", "gargoyle variants (" + (logLabel ?? "?") + "): " + FormatVariantNames(gameObjects));
		}
		if (gameObjects.Count > 0)
		{
			VariantCache[key] = gameObjects;
		}
		return gameObjects;
	}

	private static void TryAddVariant(List<GameObject> output, HashSet<string> seen, GameObject prefab)
	{
		if (!(prefab == null))
		{
			string item = NameRemap.StripCloneSuffix(prefab.name);
			if (seen.Add(item))
			{
				output.Add(prefab);
			}
		}
	}

	private static string FormatVariantNames(List<GameObject> variants)
	{
		if (variants == null || variants.Count == 0)
		{
			return "(none)";
		}
		StringBuilder stringBuilder = new StringBuilder();
		for (int i = 0; i < variants.Count; i++)
		{
			if (i > 0)
			{
				stringBuilder.Append(", ");
			}
			stringBuilder.Append(NameRemap.StripCloneSuffix(variants[i].name));
		}
		return stringBuilder.ToString();
	}

	private static void CollectLiveEnemyPrefabs(string hint, List<GameObject> output, HashSet<string> seen)
	{
		if (hint.IndexOf("dragon", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			DragonEnemyAI[] dragonEnemyAIs = Object.FindObjectsByType<DragonEnemyAI>((FindObjectsSortMode)0);
			for (int i = 0; i < dragonEnemyAIs.Length; i++)
			{
				if (dragonEnemyAIs[i] != null && MatchesSpawnHint(dragonEnemyAIs[i].gameObject.name, hint))
				{
					TryAddVariant(output, seen, dragonEnemyAIs[i].gameObject);
				}
			}
			ProximityDragonEnemyAI[] proximityDragonEnemyAIs = Object.FindObjectsByType<ProximityDragonEnemyAI>((FindObjectsSortMode)0);
			for (int j = 0; j < proximityDragonEnemyAIs.Length; j++)
			{
				if (proximityDragonEnemyAIs[j] != null && MatchesHint(proximityDragonEnemyAIs[j].gameObject.name, hint))
				{
					TryAddVariant(output, seen, proximityDragonEnemyAIs[j].gameObject);
				}
			}
		}
		if (hint.IndexOf("plantasha", StringComparison.OrdinalIgnoreCase) >= 0 || hint.IndexOf("spinning", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			SpinningEnemyAI[] spinningEnemyAIs = Object.FindObjectsByType<SpinningEnemyAI>((FindObjectsSortMode)0);
			for (int k = 0; k < spinningEnemyAIs.Length; k++)
			{
				if (spinningEnemyAIs[k] != null && MatchesHint(spinningEnemyAIs[k].gameObject.name, hint))
				{
					TryAddVariant(output, seen, spinningEnemyAIs[k].gameObject);
				}
			}
		}
		if (hint.IndexOf("zombie", StringComparison.OrdinalIgnoreCase) >= 0 || hint.IndexOf("charging", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			ChargingEnemyAI[] chargingEnemyAIs = Object.FindObjectsByType<ChargingEnemyAI>((FindObjectsSortMode)0);
			for (int l = 0; l < chargingEnemyAIs.Length; l++)
			{
				if (chargingEnemyAIs[l] != null && MatchesHint(chargingEnemyAIs[l].gameObject.name, hint))
				{
					TryAddVariant(output, seen, chargingEnemyAIs[l].gameObject);
				}
			}
		}
		if (MatchesChestHint(hint))
		{
			MimicEnemy[] mimicEnemies = Object.FindObjectsByType<MimicEnemy>((FindObjectsInactive)1, (FindObjectsSortMode)0);
			for (int m = 0; m < mimicEnemies.Length; m++)
			{
				if (!(mimicEnemies[m] == null))
				{
					string name = mimicEnemies[m].gameObject.name;
					if (MatchesSpawnHint(name, hint) || LooksLikeChestMimic(name))
					{
						TryAddVariant(output, seen, mimicEnemies[m].gameObject);
					}
				}
			}
		}
		else if (hint.IndexOf("mimic", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			MimicEnemy[] mimics = Object.FindObjectsByType<MimicEnemy>((FindObjectsInactive)1, (FindObjectsSortMode)0);
			for (int n = 0; n < mimics.Length; n++)
			{
				if (mimics[n] != null && MatchesSpawnHint(mimics[n].gameObject.name, hint) && !LooksLikeChestMimic(mimics[n].gameObject.name))
				{
					TryAddVariant(output, seen, mimics[n].gameObject);
				}
			}
		}
		if (MatchesGravyHint(hint))
		{
			ProjectileEnemyAI[] projectileEnemyAIs = Object.FindObjectsByType<ProjectileEnemyAI>((FindObjectsInactive)1, (FindObjectsSortMode)0);
			foreach (ProjectileEnemyAI projectileEnemyAI in projectileEnemyAIs)
			{
				if (!(projectileEnemyAI == null) && (MatchesSpawnHint(projectileEnemyAI.gameObject.name, hint) || MatchesSpawnHint(projectileEnemyAI.galleryEnemyID, hint)))
				{
					TryAddVariant(output, seen, projectileEnemyAI.gameObject);
				}
			}
		}
		EnemyAI[] enemyAIs = Object.FindObjectsByType<EnemyAI>((FindObjectsSortMode)0);
		foreach (EnemyAI enemyAI in enemyAIs)
		{
			if (!(enemyAI == null) && (MatchesSpawnHint(enemyAI.gameObject.name, hint) || MatchesSpawnHint(enemyAI.galleryEnemyID, hint)))
			{
				TryAddVariant(output, seen, enemyAI.gameObject);
			}
		}
	}

	private static bool LooksLikeChestMimic(string name)
	{
		if (string.IsNullOrEmpty(name))
		{
			return false;
		}
		return name.IndexOf("chest", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("truhe", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("trap", StringComparison.OrdinalIgnoreCase) >= 0;
	}

	private static bool MatchesGravyHint(string hint)
	{
		return hint.IndexOf("gravy", StringComparison.OrdinalIgnoreCase) >= 0 || hint.IndexOf("minotaur", StringComparison.OrdinalIgnoreCase) >= 0 || hint.IndexOf("minothaur", StringComparison.OrdinalIgnoreCase) >= 0;
	}

	private static bool MatchesChestHint(string hint)
	{
		return hint.IndexOf("chest", StringComparison.OrdinalIgnoreCase) >= 0 || hint.IndexOf("trap", StringComparison.OrdinalIgnoreCase) >= 0 || hint.IndexOf("truhe", StringComparison.OrdinalIgnoreCase) >= 0;
	}

	// `Resources.FindObjectsOfTypeAll` is "everything currently in memory", not "everything in the
	// build" - and an asset nothing references is never in memory. The Blinded Beast has an
	// `EnemyData` of its own (`EnemyData/Blinded Beast EnemyData`, prefabVariants = ["Blinded
	// Beast"]), but no spawner's flat enemyData[] lists it, so on an ordinary floor nothing has
	// ever loaded it and every search here came up empty. Loading the folder makes the assets
	// visible to the search that was already written for them; the game has nine of them, so
	// this is cheap, and it runs once.
	//
	// The array has to be KEPT, not just loaded. `Resources.LoadAll` hands back references and
	// nothing else holds them, so Unity's next unused-asset unload collects the lot again - and
	// with a latch saying the load has happened, nothing ever reloads them. That is exactly what
	// the 2026-08-24 run shows: key 9 spawned a Blinded Beast at 15:30:44 and then reported
	// `no prefab found` eight times from 15:36:35 on, in one unbroken session. Holding the array
	// in a static field is the whole fix; the assets are nine ScriptableObjects.
	private static EnemyData[] _resourcesEnemyData;

	private static void EnsureResourcesEnemyDataLoaded()
	{
		if (_resourcesEnemyData != null && _resourcesEnemyData.Length > 0)
		{
			return;
		}
		try
		{
			_resourcesEnemyData = Resources.LoadAll<EnemyData>("");
			Plugin.DBG("SPAWN", "loaded " + ((_resourcesEnemyData != null) ? _resourcesEnemyData.Length : 0) + " EnemyData asset(s) from Resources");
		}
		catch (Exception ex)
		{
			Plugin.DBG("SPAWN", "could not load EnemyData from Resources: " + ex.Message);
		}
	}

	private static void CollectFromAllLoadedAssets(string hint, List<GameObject> output, HashSet<string> seen)
	{
		EnsureResourcesEnemyDataLoaded();
		EnemyData[] data = Resources.FindObjectsOfTypeAll<EnemyData>();
		CollectInEnemyData(data, hint, output, seen);
		CollectByEnemyAi(hint, output, seen);
		if (MatchesChestHint(hint) || hint.IndexOf("mimic", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			MimicEnemy[] mimicEnemies = Resources.FindObjectsOfTypeAll<MimicEnemy>();
			foreach (MimicEnemy mimicEnemy in mimicEnemies)
			{
				if (!IsUsableSpawnSource((Object)(object)mimicEnemy))
				{
					continue;
				}
				string name = mimicEnemy.gameObject.name;
				if (MatchesChestHint(hint))
				{
					if (MatchesSpawnHint(name, hint) || MatchesSpawnHint(mimicEnemy.galleryEnemyID, hint) || LooksLikeChestMimic(name))
					{
						TryAddVariant(output, seen, mimicEnemy.gameObject);
					}
				}
				else if ((MatchesSpawnHint(name, hint) || MatchesSpawnHint(mimicEnemy.galleryEnemyID, hint) || NameRemap.ResolveEnemyKey(name) == "mimic") && !LooksLikeChestMimic(name))
				{
					TryAddVariant(output, seen, mimicEnemy.gameObject);
				}
			}
		}
		if (MatchesGravyHint(hint))
		{
			ProjectileEnemyAI[] projectileEnemyAIs = Resources.FindObjectsOfTypeAll<ProjectileEnemyAI>();
			foreach (ProjectileEnemyAI projectileEnemyAI in projectileEnemyAIs)
			{
				if (IsUsableSpawnSource((Object)(object)projectileEnemyAI) && (MatchesSpawnHint(projectileEnemyAI.gameObject.name, hint) || MatchesSpawnHint(projectileEnemyAI.galleryEnemyID, hint) || NameRemap.ResolveEnemyKey(projectileEnemyAI.gameObject.name) == "gravy"))
				{
					TryAddVariant(output, seen, projectileEnemyAI.gameObject);
				}
			}
		}
		EnemyAI[] enemyAIs = Resources.FindObjectsOfTypeAll<EnemyAI>();
		foreach (EnemyAI enemyAI in enemyAIs)
		{
			if (!(enemyAI == null) && !IsBlockedGargoyleSpawnName(enemyAI.gameObject.name) && (MatchesGargoyleHint(hint) || IsUsableSpawnSource((Object)(object)enemyAI)) && (MatchesSpawnHint(enemyAI.gameObject.name, hint) || MatchesSpawnHint(enemyAI.galleryEnemyID, hint) || NameRemap.ResolveEnemyKey(enemyAI.gameObject.name) == NameRemap.ResolveEnemyKey(hint.Split('|')[0].Trim())))
			{
				TryAddVariant(output, seen, enemyAI.gameObject);
			}
		}
	}

	private static bool IsUsableSpawnSource(Object obj)
	{
		if (obj == (Object)null)
		{
			return false;
		}
		GameObject asGameObject = (GameObject)(object)((obj is GameObject) ? obj : null);
		object resolved;
		if (asGameObject == null)
		{
			Component component = (Component)(object)((obj is Component) ? obj : null);
			resolved = ((component != null) ? component.gameObject : null);
		}
		else
		{
			resolved = asGameObject;
		}
		GameObject gameObject = (GameObject)resolved;
		if (gameObject == null)
		{
			return false;
		}
		if (((int)gameObject.hideFlags & 0x3D) > 0)
		{
			return false;
		}
		return true;
	}

	// The CameraSwapTrigger census this used to print went with the interactive search: the
	// counts only ever explained why that search came up empty, and there is no such search now.
	private static void LogSpawnFailure(string logLabel, string nameHint)
	{
		Plugin.DBG("SPAWN", logLabel + ": no prefab found (hint='" + nameHint + "')");
	}

	private static void CollectInSpawner(MonoBehaviour spawner, string hint, List<GameObject> output, HashSet<string> seen)
	{
		EnemyData[] data = Traverse.Create((object)spawner).Field("enemyData").GetValue<EnemyData[]>();
		CollectInEnemyData(data, hint, output, seen);
	}

	private static void CollectInSweeper(SweeperEnemySpawner spawner, string hint, List<GameObject> output, HashSet<string> seen)
	{
		SweeperEnemySpawner.EnemySpawnData[] spawnData = Traverse.Create((object)spawner).Field("enemySpawnData").GetValue<SweeperEnemySpawner.EnemySpawnData[]>();
		if (spawnData == null)
		{
			return;
		}
		foreach (SweeperEnemySpawner.EnemySpawnData entry in spawnData)
		{
			if (entry?.enemyPrefab != null && MatchesSpawnHint(entry.enemyPrefab.name, hint))
			{
				TryAddVariant(output, seen, entry.enemyPrefab);
			}
		}
	}

	// Every other route into this resolver goes through something that already knows about the
	// enemy: a spawner's own table, an EnemyData asset, or one of the five AI subclasses
	// CollectLiveEnemyPrefabs searches by name. The Blinded Beast is in none of them. It appears
	// in zero spawner tables (which is why no room can roll one), it has no EnemyData, and its AI
	// component is the *base* EnemyAI rather than a subclass - so before this pass the debug key
	// could not find it either, and the hint fell through to the interactive search, which handed
	// back a glory hole.
	//
	// Matching on the base class covers every enemy in the game at once, subclasses included, and
	// keeps working for whatever a later game version adds. It is last in the chain so the
	// specific routes still decide when they can.
	private static void CollectByEnemyAi(string hint, List<GameObject> output, HashSet<string> seen)
	{
		EnemyAI[] enemyAIs = Resources.FindObjectsOfTypeAll<EnemyAI>();
		if (enemyAIs == null)
		{
			return;
		}
		for (int i = 0; i < enemyAIs.Length; i++)
		{
			EnemyAI ai = enemyAIs[i];
			if (!IsUsableSpawnSource((Object)(object)ai))
			{
				continue;
			}
			string name = ai.gameObject.name;
			if (MatchesSpawnHint(name, hint) || MatchesSpawnHint(ai.galleryEnemyID, hint))
			{
				TryAddVariant(output, seen, ai.gameObject);
			}
		}
	}

	private static void CollectInEnemyData(EnemyData[] data, string hint, List<GameObject> output, HashSet<string> seen)
	{
		if (data == null)
		{
			return;
		}
		foreach (EnemyData enemyData in data)
		{
			if (enemyData == null)
			{
				continue;
			}
			bool nameMatches = MatchesSpawnHint(enemyData.enemyName, hint);
			GameObject[] prefabVariants = enemyData.prefabVariants;
			if (prefabVariants != null)
			{
				foreach (GameObject variant in prefabVariants)
				{
					if (variant != null && (nameMatches || MatchesSpawnHint(variant.name, hint)))
					{
						TryAddVariant(output, seen, variant);
					}
				}
			}
			if (nameMatches && (prefabVariants == null || prefabVariants.Length == 0))
			{
				GameObject randomPrefab = enemyData.GetRandomPrefab();
				TryAddVariant(output, seen, randomPrefab);
			}
		}
	}

	private static bool MatchesSpawnHint(string name, string hint)
	{
		if (MatchesHint(name, hint))
		{
			return true;
		}
		if (string.IsNullOrEmpty(name))
		{
			return false;
		}
		string key = NameRemap.ResolveEnemyKey(name);
		if (string.IsNullOrEmpty(key))
		{
			return false;
		}
		string[] hintParts = hint.Split('|');
		for (int i = 0; i < hintParts.Length; i++)
		{
			string hintPart = hintParts[i].Trim();
			if (hintPart.Length != 0 && key.Equals(hintPart, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}
		return false;
	}

	private static bool MatchesHint(string name, string hint)
	{
		if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(hint))
		{
			return false;
		}
		string[] hintParts = hint.Split('|');
		for (int i = 0; i < hintParts.Length; i++)
		{
			string hintPart = hintParts[i].Trim();
			if (hintPart.Length != 0 && name.IndexOf(hintPart, StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return true;
			}
		}
		return false;
	}

	private static bool TryGetSpawnTransform(out Vector3 position, out Quaternion rotation)
	{
        if(BossTestTools.PlacementRequested)return BossSpawnPlacement.TryGet(out position,out rotation);
		position = Vector3.zero;
		rotation = Quaternion.identity;
		GameObject player = GameObject.FindGameObjectWithTag("Player");
		if (player == null)
		{
			return false;
		}
		Camera camera = player.GetComponentInChildren<Camera>();
		Vector3 forward = ((camera != null) ? camera.transform.forward : player.transform.forward);
		forward.y = 0f;
		if (forward.sqrMagnitude < 0.01f)
		{
			forward = player.transform.forward;
			forward.y = 0f;
		}
		forward.Normalize();
		float distance = Plugin.CfgSpawnEnemyDistance.Value;
		position = player.transform.position + forward * distance;
		position.y = player.transform.position.y;
		// Snap to the floor: the player's Y is not valid ground at the spawn point,
		// so reinforcements could land below the surface and fall through.
		//
		// But only to a floor on the player's own level. The ray is 12 m long and takes the
		// first thing it meets, and in the sewers the point a few metres ahead is often over the
		// water channel rather than the walkway - so the 2026-08-24 run put a Dragon at y=-1.80
		// with the player at -0.60, under the floor they were standing on and invisible from it,
		// which read in the chair as the key not working. A drop bigger than one step is not the
		// ground you asked to spawn on; the player's own Y is the better answer there, and it is
		// what this did before the snap was added.
		RaycastHit groundHit = default(RaycastHit);
		if (Physics.Raycast(position + Vector3.up * 3f, Vector3.down, out groundHit, 12f, -5, (QueryTriggerInteraction)1)
			&& groundHit.point.y >= position.y - 1f)
		{
			position.y = groundHit.point.y;
		}
		Vector3 toPlayer = player.transform.position - position;
		Vector3 normalized = toPlayer.normalized;
		normalized.y = 0f;
		if (normalized.sqrMagnitude > 0.01f)
		{
			rotation = Quaternion.LookRotation(normalized);
		}
		return true;
	}
}
