using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace PncEdi;

/// <summary>
/// Debug: spawn a Black Serpent (BrawlerEnemyAI) reskinned with Gravy/Minotaur visuals
/// and wired to Gravy grab animations (random BJ vs Riding each spawn).
/// </summary>
[HarmonyPatch]
internal static class GravySerpentSpawn
{
	private const string LogLabel = "gravy_serpent";

	private static GameObject _cachedVisualTemplate;

	private static GameObject _cachedGrabModelTemplate;

	private static RuntimeAnimatorController _cachedGrabController;

	private static RuntimeAnimatorController _cachedIdleController;

	private static GrabAnimationData[] _cachedGrabAnims;

	private static string _cachedGalleryId;

	private static float _nextSpawnAllowedAt;

	private static float _nextCacheVisualAllowedAt;

	private static GameObject _liveGrabModel;

	private static GameObject _lastGrabEnemy;

	internal static void TickHotkey()
	{
		if (OverkeksConfig.CfgKeySpawnGravySerpent == null)
		{
			return;
		}
		if (!Hotkeys.IsDown(OverkeksConfig.CfgKeySpawnGravySerpent))
		{
			return;
		}
		if (Time.unscaledTime < _nextSpawnAllowedAt)
		{
			return;
		}
		_nextSpawnAllowedAt = Time.unscaledTime + 0.35f;
		Spawn();
	}

	internal static void TickCacheVisualHotkey()
	{
		if (OverkeksConfig.CfgKeyCacheGravyVisual == null)
		{
			return;
		}
		if (!Hotkeys.IsDown(OverkeksConfig.CfgKeyCacheGravyVisual))
		{
			return;
		}
		if (Time.unscaledTime < _nextCacheVisualAllowedAt)
		{
			return;
		}
		_nextCacheVisualAllowedAt = Time.unscaledTime + 0.5f;
		DebugCacheGravyVisual();
	}

	/// <summary>
	/// Force-scan memory for the shop Gravy Idle prop (no shop visit required if the prefab is already loaded).
	/// </summary>
	internal static void DebugCacheGravyVisual()
	{
		Plugin.DBG("SPAWN", LogLabel + ": === debug cache Gravy visual (N) ===");
		TryForceLoadGravyResources();
		DumpGravyNamedObjects();
		DumpGravyIdleControllers();
		DumpGravyIdleSprites();
		DumpGravyTriggers();
		bool had = HasUsableWalkerVisual(_cachedVisualTemplate);
		if (_cachedVisualTemplate != null)
		{
			UnityEngine.Object.Destroy(_cachedVisualTemplate);
			_cachedVisualTemplate = null;
		}
		Animator idle = FindBestGravyIdleAnimator(null);
		if (idle != null)
		{
			CacheVisualFromGameObject(idle.gameObject, "debug-N idle-prop:'" + NameRemap.StripCloneSuffix(idle.gameObject.name) + "' ctrl='" + (idle.runtimeAnimatorController != null ? idle.runtimeAnimatorController.name : "") + "'");
			if (idle.runtimeAnimatorController != null)
			{
				_cachedIdleController = idle.runtimeAnimatorController;
			}
		}
		else
		{
			CameraSwapTrigger trigger = FindGravyTrigger();
			if (trigger != null)
			{
				TryCacheVisualFromShopTrigger(trigger);
			}
			TryCacheVisualFromStatueController();
			TryCacheVisualFromNamedAssets();
			TryCacheVisualFromGalleryEntry();
			TryCacheVisualFromIdleSprites();
		}
		bool ok = HasUsableWalkerVisual(_cachedVisualTemplate);
		Plugin.DBG("SPAWN", LogLabel + ": debug cache result ok=" + ok + " body=" + (_cachedVisualTemplate != null ? CountWalkerBodyRenderers(_cachedVisualTemplate) : 0) + " worldMesh=" + (_cachedVisualTemplate != null ? CountWorldMeshRenderers(_cachedVisualTemplate) : 0) + " hadBefore=" + had + " idleCtrl=" + (_cachedIdleController != null ? _cachedIdleController.name : "<none>"));
		if (!ok)
		{
			Plugin.DBG("SPAWN", LogLabel + ": no Gravy body in memory yet — open Enemy Gallery → Gravy once, or visit the shop, then press N again");
		}
	}

	private static void TryForceLoadGravyResources()
	{
		try
		{
			// Touch Resources so more prefabs/controllers/sprites enter FindObjectsOfTypeAll.
			Resources.LoadAll<RuntimeAnimatorController>("");
			Resources.LoadAll<EnemyGalleryEntry>("");
			Resources.LoadAll<Sprite>("");
			Resources.LoadAll<GameObject>("");
			Resources.Load<EnemyGalleryEntry>("gallerydata/enemygallerydata/gravygalleryentry");
		}
		catch
		{
		}
	}

	private static void DumpGravyTriggers()
	{
		CameraSwapTrigger[] array = Resources.FindObjectsOfTypeAll<CameraSwapTrigger>();
		int hits = 0;
		StringBuilder sb = new StringBuilder();
		sb.Append(LogLabel).Append(" gravy-trigger dump:");
		for (int i = 0; i < array.Length; i++)
		{
			CameraSwapTrigger trigger = array[i];
			if (trigger == null || trigger is TrapCameraSwapTrigger)
			{
				continue;
			}
			int score = ScoreGravyTrigger(trigger);
			if (score <= 0 && !(trigger is HealingCameraSwapTrigger))
			{
				continue;
			}
			hits++;
			string name = NameRemap.StripCloneSuffix(trigger.gameObject.name);
			string scene = trigger.gameObject.scene.IsValid() ? trigger.gameObject.scene.name : "<asset>";
			Animator idle = FindBestGravyIdleAnimator(trigger);
			sb.Append(" | '").Append(name).Append("' score=").Append(score)
				.Append(" heal=").Append(trigger is HealingCameraSwapTrigger)
				.Append(" scene=").Append(scene)
				.Append(" nearIdle=").Append(idle != null ? NameRemap.StripCloneSuffix(idle.gameObject.name) : "<none>");
		}
		Plugin.DBG("SPAWN", sb.Append(" (hits=").Append(hits).Append(")").ToString());
	}

	internal static void Spawn()
	{
		GameObject template = ResolvePreferredSerpentTemplate();
		if (template == null)
		{
			Plugin.DBG("SPAWN", LogLabel + ": no usable Black Serpent template (need BrawlerEnemyAI)");
			return;
		}
		GameObject instance = DebugEnemySpawn.SpawnPrefabInstance(template, LogLabel);
		if (instance == null)
		{
			Plugin.DBG("SPAWN", LogLabel + ": serpent spawn failed");
			return;
		}
		if (!TryConvert(instance, out string detail))
		{
			Plugin.DBG("SPAWN", LogLabel + ": convert failed (" + detail + ") - destroying botched spawn");
			UnityEngine.Object.Destroy(instance);
			return;
		}
		OverkeksEnemyRuntime.RefreshConvertedActor(instance, "gravy");
		Plugin.DBG("SPAWN", LogLabel + ": ready '" + instance.name + "' from '" + NameRemap.StripCloneSuffix(template.name) + "' " + detail);
	}

	/// <summary>
	/// Prefer Black Serpent (BrawlerEnemyAI). That is the walkable hypnotist base we reskin.
	/// </summary>
	private static GameObject ResolvePreferredSerpentTemplate()
	{
		string hint = OverkeksConfig.CfgSpawnSerpentNameHint?.Value ?? "black serpent|blackserpent|serpent";
		List<GameObject> variants = DebugEnemySpawn.GetSpawnVariants(hint, LogLabel);
		if (variants == null || variants.Count == 0)
		{
			Plugin.DBG("SPAWN", LogLabel + ": no serpent variants resolved at all");
			return null;
		}
		GameObject exact = null;
		GameObject anyUsable = null;
		StringBuilder diag = new StringBuilder();
		for (int i = 0; i < variants.Count; i++)
		{
			GameObject gameObject = variants[i];
			if (gameObject == null)
			{
				continue;
			}
			string text = NameRemap.StripCloneSuffix(gameObject.name);
			string aiSummary = SummarizeAi(gameObject);
			if (diag.Length > 0)
			{
				diag.Append("; ");
			}
			diag.Append(text).Append("[").Append(aiSummary).Append("]");
			if (!IsUsableSerpentBase(gameObject))
			{
				continue;
			}
			if (text.IndexOf("black", StringComparison.OrdinalIgnoreCase) >= 0 && text.IndexOf("serpent", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				exact = gameObject;
				break;
			}
			if (anyUsable == null)
			{
				anyUsable = gameObject;
			}
		}
		GameObject gameObject2 = exact ?? anyUsable;
		if (gameObject2 == null)
		{
			Plugin.DBG("SPAWN", LogLabel + ": no usable base among: " + diag);
			return null;
		}
		Plugin.DBG("SPAWN", LogLabel + ": serpent template '" + NameRemap.StripCloneSuffix(gameObject2.name) + "' ai=" + SummarizeAi(gameObject2) + " from [" + diag + "]");
		return gameObject2;
	}

	private static bool IsUsableSerpentBase(GameObject prefab)
	{
		if (prefab == null || prefab.GetComponent<CameraSwapTrigger>() != null || prefab.GetComponent<MimicEnemy>() != null)
		{
			return false;
		}
		if (prefab.GetComponent<BrawlerEnemyAI>() != null)
		{
			return true;
		}
		string text = NameRemap.StripCloneSuffix(prefab.name);
		return text.IndexOf("serpent", StringComparison.OrdinalIgnoreCase) >= 0 && EnemyAiTypes.Any(prefab);
	}

	private static string SummarizeAi(GameObject go)
	{
		if (go == null)
		{
			return "null";
		}
		List<string> list = new List<string>();
		if (go.GetComponent<EnemyAI>() != null)
		{
			list.Add("EnemyAI");
		}
		if (go.GetComponent<SpinningEnemyAI>() != null)
		{
			list.Add("Spinning");
		}
		if (go.GetComponent<ProjectileEnemyAI>() != null)
		{
			list.Add("Projectile");
		}
		if (go.GetComponent<ChargingEnemyAI>() != null)
		{
			list.Add("Charging");
		}
		if (go.GetComponent<BrawlerEnemyAI>() != null)
		{
			list.Add("Brawler");
		}
		return list.Count == 0 ? "none" : string.Join("+", list);
	}

	private static bool TryWireGrabAi(GameObject enemy, GrabAnimationData[] variantAnims, out string aiKind)
	{
		aiKind = null;
		string galleryId = string.IsNullOrEmpty(_cachedGalleryId) ? "gravy" : _cachedGalleryId;
		List<string> kinds = new List<string>();
		BrawlerEnemyAI brawler = enemy.GetComponent<BrawlerEnemyAI>();
		if (brawler != null)
		{
			brawler.galleryEnemyID = galleryId;
			brawler.grabAnimatorController = _cachedGrabController;
			brawler.grabScreenAnimations = variantAnims;
			// Keep hypnosis so LandGrab still fires; H-scene content is Gravy either way.
			brawler.canHypnotise = true;
			kinds.Add("BrawlerEnemyAI");
		}
		EnemyAI enemyAI = enemy.GetComponent<EnemyAI>();
		if (enemyAI != null)
		{
			enemyAI.galleryEnemyID = galleryId;
			enemyAI.grabAnimatorController = _cachedGrabController;
			enemyAI.grabScreenAnimations = variantAnims;
			// Never inherit Blinded Beast mini-boss bar / stage names from a wrong base.
			enemyAI.isMiniBoss = false;
			kinds.Add("EnemyAI");
		}
		ProjectileEnemyAI projectileEnemyAI = enemy.GetComponent<ProjectileEnemyAI>();
		if (projectileEnemyAI != null)
		{
			projectileEnemyAI.galleryEnemyID = galleryId;
			projectileEnemyAI.grabAnimatorController = _cachedGrabController;
			projectileEnemyAI.grabScreenAnimations = variantAnims;
			kinds.Add("ProjectileEnemyAI");
		}
		SpinningEnemyAI spinningEnemyAI = enemy.GetComponent<SpinningEnemyAI>();
		if (spinningEnemyAI != null)
		{
			spinningEnemyAI.galleryEnemyID = galleryId;
			spinningEnemyAI.grabAnimatorController = _cachedGrabController;
			spinningEnemyAI.grabScreenAnimations = variantAnims;
			kinds.Add("SpinningEnemyAI");
		}
		if (kinds.Count == 0)
		{
			return false;
		}
		aiKind = string.Join("+", kinds);
		return true;
	}

	internal static bool TryConvert(GameObject enemy, out string detail)
	{
		detail = "";
		EnsureGrabAssetsCached();
        if(!CreatorVisualHost.TryWorld("Gallery_Gravy",out _,out _,_cachedIdleController)) {detail="native gallery idle host unavailable; actor left unchanged";return false;}
        OverkeksMode.Own(enemy);
 
		if (_cachedGrabController == null)
		{
			detail = "no Gravy grab controller (need gallery entry loaded once)";
			return false;
		}
		string controllerName = _cachedGrabController.name ?? "";
		if (controllerName.IndexOf("serpent", StringComparison.OrdinalIgnoreCase) >= 0
			|| controllerName.IndexOf("baphomet", StringComparison.OrdinalIgnoreCase) >= 0
			|| controllerName.IndexOf("glory", StringComparison.OrdinalIgnoreCase) >= 0
			|| !LooksLikeGrabController(_cachedGrabController))
		{
			detail = "refusing grab controller '" + controllerName + "' (wrong/idle leak)";
			_cachedGrabController = null;
			_cachedGrabAnims = null;
			return false;
		}
		// 50/50 at spawn; Grab re-rolls again so each H-scene is independent.
		bool set2 = RollSet2Variant(out float roll);
		ApplyVariantToMarker(enemy, set2, out string aiKind, out string detailCore);
		detail = detailCore + " roll=" + roll.ToString("0.00");
		// Native root-Image host replaces live-gallery cloning.
		GravySerpentMarker marker = enemy.GetComponent<GravySerpentMarker>();
		bool visualOk = TryAttachStatueVisual(enemy);
		if (visualOk && marker != null)
		{
			marker.StripBaseRenderers = true;
			StripBaseRenderers(enemy);
			marker.StripBaseNow();
		}
		else
		{
			if (marker != null)
			{
				marker.StripBaseRenderers = false;
			}
			// Re-enable in case a previous strip or bad attach left serpent dark.
			EnableAllRenderers(enemy);
			Plugin.DBG("SPAWN", LogLabel + ": keeping serpent mesh visible (no usable gravy world mesh)");
		}
		detail += " visual=" + (visualOk ? "gravy" : "serpent-fallback");
		return !string.IsNullOrEmpty(aiKind);
	}

	private static float _lastVariantRoll;

	private static int _variantSpawnCount;

	private static bool RollSet2Variant(out float roll)
	{
		bool set2 = UnityEngine.Random.Range(0, 2) == 1;
		roll = set2 ? 0.75f : 0.25f;
		_lastVariantRoll = roll;
		_lastSet2 = set2;
		_variantSpawnCount++;
		Plugin.DBG("SPAWN", LogLabel + ": variant coin-flip -> " + (set2 ? "start2" : "start") + " (n=" + _variantSpawnCount + ")");
		return set2;
	}

	private static bool _lastSet2;

	private static void ApplyVariantToMarker(GameObject enemy, bool set2, out string aiKind, out string detail)
	{
		aiKind = null;
		detail = "";
		string start = set2 ? "Start2" : "Start";
		string loop = set2 ? "Loop2" : "Loop";
		string cum = set2 ? "Cum2" : "Cum";
		// Prefer exact gallery names when present (e.g. Riding_*); never fall back across sets.
		string fromGalleryStart = set2
			? (PickNamedState(_cachedGrabAnims, "Start2") ?? PickNamedContaining(_cachedGrabAnims, "riding", "start") ?? start)
			: (PickNamedState(_cachedGrabAnims, "Start", excludeSuffix2: true) ?? PickNamedContaining(_cachedGrabAnims, "bj", "start") ?? start);
		string fromGalleryLoop = set2
			? (PickNamedState(_cachedGrabAnims, "Loop2") ?? PickNamedContaining(_cachedGrabAnims, "riding", "loop") ?? loop)
			: (PickNamedState(_cachedGrabAnims, "Loop", excludeSuffix2: true) ?? PickNamedContaining(_cachedGrabAnims, "bj", "loop") ?? loop);
		string fromGalleryCum = set2
			? (PickNamedState(_cachedGrabAnims, "Cum2") ?? PickNamedContaining(_cachedGrabAnims, "riding", "cum") ?? cum)
			: (PickNamedState(_cachedGrabAnims, "Cum", excludeSuffix2: true) ?? PickNamedContaining(_cachedGrabAnims, "bj", "cum") ?? cum);
		if (IsLikelyStateName(fromGalleryStart))
		{
			start = fromGalleryStart;
		}
		if (IsLikelyStateName(fromGalleryLoop))
		{
			loop = fromGalleryLoop;
		}
		if (IsLikelyStateName(fromGalleryCum))
		{
			cum = fromGalleryCum;
		}
		// Prefer short Animator state names for wiring; long Gallery_* clip names are not states.
		string playableStart = FindClipName(_cachedGrabController, set2, wantStart: true);
		string playableLoop = FindClipName(_cachedGrabController, set2, wantStart: false, wantLoop: true);
		string playableCum = FindClipName(_cachedGrabController, set2, wantStart: false, wantCum: true);
		// Keep Start/Loop/Cum as the playable Animator states; store clip hints only when short.
		if (!string.IsNullOrEmpty(playableStart) && !IsShortGalleryStub(playableStart)
			&& playableStart.IndexOf("Gallery_", StringComparison.OrdinalIgnoreCase) < 0
			&& playableStart.Length < 24)
		{
			start = playableStart;
		}
		if (!string.IsNullOrEmpty(playableLoop) && !IsShortGalleryStub(playableLoop)
			&& playableLoop.IndexOf("Gallery_", StringComparison.OrdinalIgnoreCase) < 0
			&& playableLoop.Length < 24)
		{
			loop = playableLoop;
		}
		if (!string.IsNullOrEmpty(playableCum) && !IsShortGalleryStub(playableCum)
			&& playableCum.IndexOf("Gallery_", StringComparison.OrdinalIgnoreCase) < 0
			&& playableCum.Length < 24)
		{
			cum = playableCum;
		}
		GrabAnimationData[] variantAnims = BuildSynthetic(new[] { start, loop, cum });
		if (!TryWireGrabAi(enemy, variantAnims, out aiKind))
		{
			detail = "no Brawler/EnemyAI to wire";
			aiKind = null;
			return;
		}
		enemy.name = "Gravy_Minotaur";
		GravySerpentMarker marker = enemy.GetComponent<GravySerpentMarker>() ?? OverkeksMode.AddOwnedComponent<GravySerpentMarker>(enemy);
		// Keep strip state across grab variant re-rolls — clearing it lets serpent bleed back after EndGrab.
		bool hadStrip = marker.StripBaseRenderers;
		marker.VariantLabel = set2 ? "start2" : "start";
		marker.IsSet2 = set2;
		marker.GrabStartState = start;
		marker.GrabLoopState = loop;
		marker.GrabCumState = cum;
		marker.EdiStart = set2 ? "Gravy_Start2" : "Gravy_Start";
		marker.EdiLoop = set2 ? "Gravy_Loop2" : "Gravy_Loop";
		marker.EdiCum = set2 ? "Gravy_Cum2" : "Gravy_Cum";
		marker.GrabStateNames = CollectStateNames(variantAnims);
		if (hadStrip || enemy.GetComponentInChildren<GravyVisual>(includeInactive: true) != null)
		{
			marker.StripBaseRenderers = true;
			marker.StripBaseNow();
		}
		string controllerName = _cachedGrabController != null ? (_cachedGrabController.name ?? "") : "";
		detail = "ai=" + aiKind + " variant=" + marker.VariantLabel + " edi=" + marker.EdiStart + "/" + marker.EdiLoop + "/" + marker.EdiCum + " start='" + start + "' loop='" + loop + "' cum='" + cum + "' ctrl='" + controllerName + "'";
	}

	private static string PickNamedContaining(GrabAnimationData[] all, string mustHave, string step)
	{
		if (all == null)
		{
			return null;
		}
		for (int i = 0; i < all.Length; i++)
		{
			string text = all[i]?.animationName;
			if (string.IsNullOrEmpty(text) || !IsLikelyStateName(text))
			{
				continue;
			}
			string lower = text.ToLowerInvariant();
			if (!lower.Contains(mustHave))
			{
				continue;
			}
			if (step == "start" && lower.Contains("start") && !lower.Contains("cum"))
			{
				return text;
			}
			if (step == "loop" && (lower.Contains("loop") || lower.Contains("middle")) && !lower.Contains("cum") && !lower.Contains("start"))
			{
				return text;
			}
			if (step == "cum" && lower.Contains("cum"))
			{
				return text;
			}
		}
		return null;
	}

	private static bool IsLikelyStateName(string name)
	{
		if (string.IsNullOrEmpty(name))
		{
			return false;
		}
		// Clip asset names like Gallery_Gravy_Grabbed_BJ_Start are not Animator state names.
		if (name.StartsWith("Gallery_", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		return true;
	}

	private static string PickNamedState(GrabAnimationData[] all, string exact, bool excludeSuffix2 = false)
	{
		if (all == null)
		{
			return null;
		}
		for (int i = 0; i < all.Length; i++)
		{
			string text = all[i]?.animationName;
			if (string.IsNullOrEmpty(text))
			{
				continue;
			}
			if (excludeSuffix2 && text.EndsWith("2", StringComparison.Ordinal))
			{
				continue;
			}
			if (text.Equals(exact, StringComparison.OrdinalIgnoreCase))
			{
				return text;
			}
		}
		return null;
	}

	private static string PickStepFromAnims(GrabAnimationData[] anims, bool wantStart, bool wantLoop, bool wantCum)
	{
		if (anims == null)
		{
			return null;
		}
		for (int i = 0; i < anims.Length; i++)
		{
			string text = anims[i]?.animationName;
			if (string.IsNullOrEmpty(text) || !IsLikelyStateName(text))
			{
				continue;
			}
			string lower = text.ToLowerInvariant();
			if (wantStart && lower.Contains("start") && !lower.Contains("cum"))
			{
				return text;
			}
			if (wantLoop && (lower.Contains("loop") || lower.Contains("middle")) && !lower.Contains("cum") && !lower.Contains("start"))
			{
				return text;
			}
			if (wantCum && lower.Contains("cum"))
			{
				return text;
			}
		}
		return null;
	}

	private static GrabAnimationData[] PreferEdiFriendlyVariantAnims(GrabAnimationData[] variantAnims, bool set2)
	{
		string start = set2 ? "Gravy_Grabbed_Riding_Start" : "Gravy_Grabbed_BJ_Start";
		string loop = set2 ? "Gravy_Grabbed_Riding_Loop" : "Gravy_Grabbed_BJ_Loop";
		string cum = set2 ? "Gravy_Grabbed_Riding_Cum" : "Gravy_Grabbed_BJ_Cum";
		if (variantAnims != null && variantAnims.Length != 0)
		{
			bool alreadyFriendly = false;
			for (int i = 0; i < variantAnims.Length; i++)
			{
				string text = variantAnims[i]?.animationName ?? "";
				if (text.IndexOf("Gravy_Grabbed", StringComparison.OrdinalIgnoreCase) >= 0 && text.IndexOf("Gallery_", StringComparison.OrdinalIgnoreCase) < 0)
				{
					alreadyFriendly = true;
					break;
				}
			}
			if (alreadyFriendly)
			{
				return StripGalleryPrefixes(variantAnims);
			}
		}
		return BuildSynthetic(new[] { start, loop, cum });
	}

	private static GrabAnimationData[] StripGalleryPrefixes(GrabAnimationData[] anims)
	{
		GrabAnimationData[] array = new GrabAnimationData[anims.Length];
		for (int i = 0; i < anims.Length; i++)
		{
			string text = anims[i]?.animationName ?? "";
			array[i] = new GrabAnimationData
			{
				animationName = StripGalleryPrefix(text)
			};
		}
		return array;
	}

	private static string StripGalleryPrefix(string name)
	{
		if (string.IsNullOrEmpty(name))
		{
			return name;
		}
		if (name.StartsWith("Gallery_", StringComparison.OrdinalIgnoreCase))
		{
			return name.Substring("Gallery_".Length);
		}
		return name;
	}

	private static string PreferEdiFriendlyStateName(string startState, bool set2)
	{
		string stripped = StripGalleryPrefix(startState);
		if (!string.IsNullOrEmpty(stripped) && stripped.IndexOf("Gravy_Grabbed", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			return stripped;
		}
		return set2 ? "Gravy_Grabbed_Riding_Start" : "Gravy_Grabbed_BJ_Start";
	}

	private static string ResolvePlayableStartState(RuntimeAnimatorController controller, GrabAnimationData[] variantAnims, bool set2)
	{
		string fromClips = FindClipName(controller, set2, wantStart: true);
		if (!string.IsNullOrEmpty(fromClips))
		{
			return fromClips;
		}
		return PickStartState(variantAnims);
	}

	private static GrabAnimationData[] AlignVariantAnimsToController(RuntimeAnimatorController controller, GrabAnimationData[] variantAnims, bool set2)
	{
		string start = FindClipName(controller, set2, wantStart: true);
		string loop = FindClipName(controller, set2, wantStart: false, wantLoop: true);
		string cum = FindClipName(controller, set2, wantStart: false, wantCum: true);
		if (string.IsNullOrEmpty(start) && string.IsNullOrEmpty(loop) && string.IsNullOrEmpty(cum))
		{
			return variantAnims;
		}
		List<GrabAnimationData> list = new List<GrabAnimationData>();
		AddIfNamed(list, start);
		AddIfNamed(list, loop);
		AddIfNamed(list, cum);
		if (list.Count == 0)
		{
			return variantAnims;
		}
		return list.ToArray();
	}

	private static void AddIfNamed(List<GrabAnimationData> list, string name)
	{
		if (!string.IsNullOrEmpty(name))
		{
			list.Add(new GrabAnimationData
			{
				animationName = name
			});
		}
	}

	private static string FindClipName(RuntimeAnimatorController controller, bool set2, bool wantStart, bool wantLoop = false, bool wantCum = false)
	{
		if (controller == null)
		{
			return null;
		}
		AnimationClip[] animationClips = controller.animationClips;
		if (animationClips == null)
		{
			return null;
		}
		string best = null;
		int bestScore = 0;
		for (int i = 0; i < animationClips.Length; i++)
		{
			AnimationClip animationClip = animationClips[i];
			if (animationClip == null || string.IsNullOrEmpty(animationClip.name))
			{
				continue;
			}
			string text = animationClip.name.ToLowerInvariant();
			if (text.Contains("idle") || text.Contains("fade") || text.Contains("laugh") || text.Contains("walk"))
			{
				continue;
			}
			// set2 = Start2/Loop2/Cum2 / Riding; set1 = Start/Loop/Cum / BJ
			bool isSet2 = text.Contains("riding") || text.Contains("start2") || text.Contains("loop2") || text.Contains("cum2") || text.Contains("sex") || text.Contains("_2");
			bool isSet1 = text.Contains("bj") || (!isSet2 && (text.Contains("start") || text.Contains("loop") || text.Contains("middle") || text.Contains("cum") || text.Contains("grabbed")));
			if (set2 && !isSet2)
			{
				continue;
			}
			if (!set2 && isSet2)
			{
				continue;
			}
			if (!set2 && !isSet1 && !text.Contains("start") && !text.Contains("loop") && !text.Contains("cum") && !text.Contains("middle"))
			{
				continue;
			}
			int num = 1;
			if (wantStart && (text.Contains("start") || text.EndsWith("_start")) && !text.Contains("cum"))
			{
				num += 50;
			}
			if (wantLoop && (text.Contains("loop") || text.Contains("middle")) && !text.Contains("cum") && !text.Contains("start"))
			{
				num += 50;
			}
			if (wantCum && text.Contains("cum"))
			{
				num += 50;
			}
			if (text.Contains("gravy") || text.Contains("grabbed") || text.Contains("glory"))
			{
				num += 10;
			}
			if (num > bestScore)
			{
				bestScore = num;
				best = animationClip.name;
			}
		}
		return bestScore > 0 ? best : null;
	}

	private static string[] CollectStateNames(GrabAnimationData[] anims)
	{
		if (anims == null || anims.Length == 0)
		{
			return Array.Empty<string>();
		}
		List<string> list = new List<string>(anims.Length);
		for (int i = 0; i < anims.Length; i++)
		{
			if (anims[i] != null && !string.IsNullOrEmpty(anims[i].animationName))
			{
				list.Add(anims[i].animationName);
			}
		}
		return list.ToArray();
	}

	private static bool TryPickVariant(GrabAnimationData[] all, bool preferSet2, out GrabAnimationData[] picked, out string startState, out string label)
	{
		picked = null;
		startState = null;
		label = preferSet2 ? "start2" : "start";
		if (all == null || all.Length == 0)
		{
			string[] names = preferSet2
				? new[] { "Gravy_Grabbed_Riding_Start", "Gravy_Grabbed_Riding_Loop", "Gravy_Grabbed_Riding_Cum" }
				: new[] { "Gravy_Grabbed_BJ_Start", "Gravy_Grabbed_BJ_Loop", "Gravy_Grabbed_BJ_Cum" };
			picked = BuildSynthetic(names);
			startState = names[0];
			return _cachedGrabController != null;
		}
		List<GrabAnimationData> set2 = new List<GrabAnimationData>();
		List<GrabAnimationData> set1 = new List<GrabAnimationData>();
		for (int i = 0; i < all.Length; i++)
		{
			GrabAnimationData grabAnimationData = all[i];
			if (grabAnimationData == null || string.IsNullOrEmpty(grabAnimationData.animationName))
			{
				continue;
			}
			string text = grabAnimationData.animationName.ToLowerInvariant();
			if (text.Contains("riding") || text.Contains("start2") || text.Contains("loop2") || text.Contains("cum2") || text.Contains("sex") || text.EndsWith("2"))
			{
				set2.Add(grabAnimationData);
			}
			else if (text.Contains("bj") || text.Contains("grabbed") || text.Contains("start") || text.Contains("loop") || text.Contains("cum") || text.Contains("middle"))
			{
				set1.Add(grabAnimationData);
			}
		}
		List<GrabAnimationData> list = preferSet2 ? set2 : set1;
		List<GrabAnimationData> fallback = preferSet2 ? set1 : set2;
		if (list.Count == 0)
		{
			list = fallback;
			label = preferSet2 ? "start-fallback" : "start2-fallback";
		}
		if (list.Count == 0)
		{
			list = new List<GrabAnimationData>(all);
			label = "all";
		}
		picked = list.ToArray();
		startState = PickStartState(picked);
		return !string.IsNullOrEmpty(startState);
	}

	private static GrabAnimationData[] BuildSynthetic(string[] names)
    {
        var selected=CaptureStateLibrary.Select(names,_cachedGrabAnims);
        Plugin.DBG("CAPTURE-MEDIA","controller="+(_cachedGrabController!=null?_cachedGrabController.name:"<none>")+"; states="+selected.Length+"; sounds="+System.Array.FindAll(selected,item=>item.sound!=null).Length);
        return selected;
    }

	private static string PickStartState(GrabAnimationData[] anims)
	{
		if (anims == null || anims.Length == 0)
		{
			return null;
		}
		for (int i = 0; i < anims.Length; i++)
		{
			string text = anims[i]?.animationName ?? "";
			string text2 = text.ToLowerInvariant();
			if (text2.Contains("start") && !text2.Contains("cum"))
			{
				return text;
			}
		}
		return anims[0].animationName;
	}

	private static void EnsureGrabAssetsCached()
	{

        if(_cachedGrabController!=null)return;
        if(TryLoadFromGallery(out var ctrl,out var anims,out var id) && ctrl!=null && ctrl.name=="Gallery_Gravy_Grabbed") {
            _cachedGrabController=ctrl;_cachedGrabAnims=anims;_cachedGalleryId=id;return;
        }
        ctrl=CreatorVisualHost.Controller("Gallery_Gravy_Grabbed");
        if(ctrl!=null){_cachedGrabController=ctrl;_cachedGrabAnims=BuildAnimsFromController(ctrl);_cachedGalleryId="gravy";}
 
	}

	private static bool LooksLikeGrabController(RuntimeAnimatorController controller)
	{
		if (controller == null)
		{
			return false;
		}
		string text = controller.name ?? "";
		if (text.IndexOf("grab", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			return true;
		}
		AnimationClip[] animationClips = controller.animationClips;
		if (animationClips == null || animationClips.Length == 0)
		{
			return false;
		}
		int grabish = 0;
		int idleish = 0;
		for (int i = 0; i < animationClips.Length; i++)
		{
			AnimationClip animationClip = animationClips[i];
			if (animationClip == null || string.IsNullOrEmpty(animationClip.name))
			{
				continue;
			}
			string text2 = animationClip.name.ToLowerInvariant();
			if (text2.Contains("grab") || text2.Contains("bj") || text2.Contains("riding") || text2.Contains("cum") || text2.Contains("sex") || text2.Contains("glory"))
			{
				grabish++;
			}
			if (text2.Contains("idle") || text2.Contains("walk") || text2.Contains("run"))
			{
				idleish++;
			}
		}
		return grabish > 0 && grabish >= idleish;
	}

	private static bool IsMenuOnlyGalleryController(RuntimeAnimatorController controller)
	{
		if (controller == null)
		{
			return false;
		}
		string text = controller.name ?? "";
		return text.StartsWith("Gallery_", StringComparison.OrdinalIgnoreCase);
	}

	private static bool TryLoadFromControllerAssets(out RuntimeAnimatorController controller, out GrabAnimationData[] anims, bool preferInGame)
	{
		controller = null;
		anims = null;
		RuntimeAnimatorController[] array = Resources.FindObjectsOfTypeAll<RuntimeAnimatorController>();
		RuntimeAnimatorController best = null;
		int bestScore = 0;
		for (int i = 0; i < array.Length; i++)
		{
			RuntimeAnimatorController runtimeAnimatorController = array[i];
			if (runtimeAnimatorController == null || IsSerpentController(runtimeAnimatorController) || IsBaphometController(runtimeAnimatorController))
			{
				continue;
			}
			if (preferInGame && IsMenuOnlyGalleryController(runtimeAnimatorController))
			{
				continue;
			}
			string text = runtimeAnimatorController.name ?? "";
			int num = 0;
			if (text.IndexOf("gravy", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				num += 30;
			}
			else if (text.IndexOf("minotaur", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("minothaur", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				num += 28;
			}
			if (num == 0)
			{
				continue;
			}
			if (text.IndexOf("grabbed", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				num += 50;
			}
			else if (text.IndexOf("grab", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				num += 30;
			}
			// World Idle controller for the statue mesh - not GrabScreen.
			if (text.Equals("Gravy/Minotaur", StringComparison.OrdinalIgnoreCase) || (text.IndexOf("statue", StringComparison.OrdinalIgnoreCase) >= 0 && text.IndexOf("grab", StringComparison.OrdinalIgnoreCase) < 0))
			{
				num -= 100;
			}
			if (!LooksLikeGrabController(runtimeAnimatorController))
			{
				num -= 80;
			}
			if (IsMenuOnlyGalleryController(runtimeAnimatorController) && text.IndexOf("grab", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				num += 10;
			}
			else if (IsMenuOnlyGalleryController(runtimeAnimatorController))
			{
				num -= 25;
			}
			if (text.IndexOf("idle", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("walk", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				num -= 40;
			}
			if (num > bestScore)
			{
				bestScore = num;
				best = runtimeAnimatorController;
			}
		}
		if (best == null || bestScore < 20 || !LooksLikeGrabController(best))
		{
			return false;
		}
		controller = best;
		anims = BuildAnimsFromController(best);
		if (anims == null || anims.Length == 0)
		{
			anims = BuildSynthetic(new[] { "Gravy_Grabbed_BJ_Start", "Gravy_Grabbed_BJ_Loop", "Gravy_Grabbed_BJ_Cum", "Gravy_Grabbed_Riding_Start", "Gravy_Grabbed_Riding_Loop", "Gravy_Grabbed_Riding_Cum" });
		}
		return true;
	}

	private static bool IsSerpentController(RuntimeAnimatorController controller)
	{
		if (controller == null)
		{
			return false;
		}
		string text = controller.name ?? "";
		return text.IndexOf("serpent", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("snake", StringComparison.OrdinalIgnoreCase) >= 0;
	}

	private static bool IsBaphometController(RuntimeAnimatorController controller)
	{
		if (controller == null)
		{
			return false;
		}
		string text = controller.name ?? "";
		return text.IndexOf("baphomet", StringComparison.OrdinalIgnoreCase) >= 0
			|| text.IndexOf("baph", StringComparison.OrdinalIgnoreCase) >= 0
			|| text.IndexOf("glory", StringComparison.OrdinalIgnoreCase) >= 0;
	}

	private static bool TryLoadFromGallery(out RuntimeAnimatorController controller, out GrabAnimationData[] anims, out string galleryId)
	{
		controller = null;
		anims = null;
		galleryId = null;
		EnemyGalleryEntry[] array = Resources.FindObjectsOfTypeAll<EnemyGalleryEntry>();
		if (array == null || array.Length == 0)
		{
			return false;
		}
		EnemyGalleryEntry best = null;
		int bestScore = 0;
		for (int i = 0; i < array.Length; i++)
		{
			EnemyGalleryEntry enemyGalleryEntry = array[i];
			if (enemyGalleryEntry == null)
			{
				continue;
			}
			int num = ScoreGravyGallery(enemyGalleryEntry);
			if (num > bestScore)
			{
				bestScore = num;
				best = enemyGalleryEntry;
			}
		}
		if (best == null || best.grabAnimatorController == null)
		{
			return false;
		}
		controller = best.grabAnimatorController;
		anims = best.grabAnimations;
		galleryId = best.enemyID;
		if (best.animatorController != null)
		{
			_cachedIdleController = best.animatorController;
			Plugin.DBG("SPAWN", LogLabel + ": gallery idle ctrl='" + best.animatorController.name + "' for entry '" + galleryId + "'");
		}
		return true;
	}

	private static int ScoreGravyGallery(EnemyGalleryEntry entry)
	{
		string text = ((entry.enemyID ?? "") + " " + (entry.enemyName ?? "")).ToLowerInvariant();
		string ctrl = (entry.grabAnimatorController != null) ? (entry.grabAnimatorController.name ?? "") : "";
		if (IsSerpentController(entry.grabAnimatorController) || IsBaphometController(entry.grabAnimatorController))
		{
			return 0;
		}
		bool nameHit = text.Contains("gravy") || text.Contains("minotaur") || text.Contains("minothaur");
		bool ctrlHit = ctrl.IndexOf("gravy", StringComparison.OrdinalIgnoreCase) >= 0
			|| ctrl.IndexOf("minotaur", StringComparison.OrdinalIgnoreCase) >= 0
			|| ctrl.IndexOf("minothaur", StringComparison.OrdinalIgnoreCase) >= 0;
		bool animHit = false;
		if (entry.grabAnimations != null)
		{
			for (int i = 0; i < entry.grabAnimations.Length; i++)
			{
				string text2 = entry.grabAnimations[i]?.animationName ?? "";
				string lower = text2.ToLowerInvariant();
				if (lower.Contains("gravy") || lower.Contains("minotaur") || lower.Contains("minothaur"))
				{
					animHit = true;
					break;
				}
			}
		}
		if (!nameHit && !ctrlHit && !animHit)
		{
			return 0;
		}
		int num = 10;
		if (nameHit)
		{
			num += 20;
		}
		if (ctrlHit)
		{
			num += 15;
		}
		if (animHit)
		{
			num += 10;
		}
		if (entry.grabAnimatorController != null)
		{
			num += 5;
		}
		return num;
	}

	private static GrabAnimationData[] BuildAnimsFromController(RuntimeAnimatorController controller)
	{
		if (controller == null)
		{
			return Array.Empty<GrabAnimationData>();
		}
		AnimationClip[] animationClips = controller.animationClips;
		if (animationClips == null || animationClips.Length == 0)
		{
			return Array.Empty<GrabAnimationData>();
		}
		List<GrabAnimationData> list = new List<GrabAnimationData>();
		for (int i = 0; i < animationClips.Length; i++)
		{
			AnimationClip animationClip = animationClips[i];
			if (!(animationClip == null) && !string.IsNullOrEmpty(animationClip.name))
			{
				string text = animationClip.name.ToLowerInvariant();
				if (!text.Contains("idle") && !text.Contains("fade") && !text.Contains("laugh") && !text.Contains("walk"))
				{
					list.Add(new GrabAnimationData
					{
						animationName = animationClip.name
					});
				}
			}
		}
		return list.ToArray();
	}

	internal static void TryCacheVisualFromShopTrigger(CameraSwapTrigger trigger)
	{
		// Floor zone in front of the counter only marks which shop we're in — the body is the
		// separate Idle prop behind the counter (often not under the trigger hierarchy).
		Animator idle = FindBestGravyIdleAnimator(trigger);
		if (idle != null)
		{
			CacheVisualFromGameObject(idle.gameObject, "shop-prop:'" + NameRemap.StripCloneSuffix(idle.gameObject.name) + "' ctrl='" + (idle.runtimeAnimatorController != null ? idle.runtimeAnimatorController.name : "") + "'");
			if (_cachedIdleController == null && idle.runtimeAnimatorController != null)
			{
				_cachedIdleController = idle.runtimeAnimatorController;
				Plugin.DBG("SPAWN", LogLabel + ": cached idle ctrl='" + idle.runtimeAnimatorController.name + "' from shop prop");
			}
			return;
		}
		if (trigger != null)
		{
			CacheVisualFromTrigger(trigger, GetTriggerAnimator(trigger));
		}
	}

	private static void CacheVisualFromTrigger(CameraSwapTrigger trigger, Animator animator)
	{
		if (trigger == null || HasUsableWalkerVisual(_cachedVisualTemplate))
		{
			return;
		}
		GameObject source = ResolveGravyVisualRoot(trigger);
		if (source == null || !HasUsableWalkerVisual(source))
		{
			Plugin.DBG("SPAWN", LogLabel + ": no Gravy Idle prop near shop trigger '" + NameRemap.StripCloneSuffix(trigger.gameObject.name) + "' (look for controller Gravy/Minotaur behind the counter)");
			return;
		}
		CacheVisualFromGameObject(source, "shop-prop:'" + NameRemap.StripCloneSuffix(source.name) + "'");
	}

	private static CameraSwapTrigger FindGravyTrigger()
	{
		// Include prefab assets already loaded in memory (same idea as chaser prefabs sitting on SweeperEnemySpawner).
		CameraSwapTrigger[] array = Resources.FindObjectsOfTypeAll<CameraSwapTrigger>();
		CameraSwapTrigger cameraSwapTrigger = null;
		int num = 0;
		for (int i = 0; i < array.Length; i++)
		{
			CameraSwapTrigger cameraSwapTrigger2 = array[i];
			if (cameraSwapTrigger2 == null || cameraSwapTrigger2 is TrapCameraSwapTrigger)
			{
				continue;
			}
			int num2 = ScoreGravyTrigger(cameraSwapTrigger2);
			if (num2 > num)
			{
				num = num2;
				cameraSwapTrigger = cameraSwapTrigger2;
			}
		}
		return cameraSwapTrigger;
	}

	private static int ScoreGravyTrigger(CameraSwapTrigger trigger)
	{
		string galleryId = GetGalleryEntryId(trigger);
		string text = (trigger.gameObject.name + " " + (galleryId ?? "")).ToLowerInvariant();
		Animator animator = GetTriggerAnimator(trigger);
		string clipKey = "";
		string ctrlName = "";
		if (animator != null && animator.runtimeAnimatorController != null)
		{
			ctrlName = animator.runtimeAnimatorController.name ?? "";
			AnimationClip[] animationClips = animator.runtimeAnimatorController.animationClips;
			if (animationClips != null)
			{
				for (int i = 0; i < animationClips.Length; i++)
				{
					if (animationClips[i] != null)
					{
						clipKey = clipKey + " " + animationClips[i].name;
					}
				}
			}
		}
		clipKey = clipKey.ToLowerInvariant();
		bool flag = text.Contains("gravy") || text.Contains("minotaur") || text.Contains("minothaur")
			|| clipKey.Contains("gravy") || clipKey.Contains("minotaur") || clipKey.Contains("minothaur")
			|| ctrlName.IndexOf("gravy", StringComparison.OrdinalIgnoreCase) >= 0
			|| ctrlName.IndexOf("minotaur", StringComparison.OrdinalIgnoreCase) >= 0;
		if (!flag)
		{
			return 0;
		}
		// Pure Baphomet glory-hole — not shop Gravy. Allow gravy clips even if a sibling has "hole" in the name.
		if ((clipKey.Contains("baphomet") || clipKey.Contains("gloryholecamera"))
			&& !clipKey.Contains("gravy") && !clipKey.Contains("minotaur"))
		{
			return 0;
		}
		int num = 10;
		if (trigger is HealingCameraSwapTrigger)
		{
			// Between-level shop service (heals) — the animated Gravy/Minotaur prop.
			num += 40;
		}
		if (clipKey.Contains("gravy_minotaur") || clipKey.Contains("4_minotaur"))
		{
			num += 25;
		}
		else if (clipKey.Contains("minotaur") || clipKey.Contains("gravy"))
		{
			num += 20;
		}
		if (!string.IsNullOrEmpty(galleryId) && NameRemap.ResolveEnemyKey(galleryId) == "gravy")
		{
			num += 10;
		}
		if (trigger.gameObject.scene.IsValid() && trigger.gameObject.scene.isLoaded)
		{
			num += 15;
			string sceneName = trigger.gameObject.scene.name ?? "";
			if (sceneName.IndexOf("shop", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				num += 20;
			}
		}
		GameObject visualRoot = ResolveGravyVisualRoot(trigger);
		if (visualRoot != null && HasUsableWalkerVisual(visualRoot))
		{
			num += 20;
		}
		return num;
	}

	/// <summary>
	/// Shop layout: empty floor zone = HealingCameraSwapTrigger (no mesh). Gravy stands behind the
	/// counter as a separate animated Idle prop (controller "Gravy/Minotaur") — not interactable.
	/// Search the loaded shop scene (prefer near the trigger), not only the trigger hierarchy.
	/// </summary>
	private static GameObject ResolveGravyVisualRoot(CameraSwapTrigger trigger)
	{
		Animator best = FindBestGravyIdleAnimator(trigger);
		if (best != null)
		{
			return best.gameObject;
		}
		return null;
	}

	private static Animator FindBestGravyIdleAnimator(CameraSwapTrigger nearTrigger)
	{
		Animator best = null;
		int bestScore = 0;
		Vector3 anchor = nearTrigger != null ? nearTrigger.transform.position : Vector3.zero;
		bool hasAnchor = nearTrigger != null;
		Animator triggerAnim = nearTrigger != null ? GetTriggerAnimator(nearTrigger) : null;
		UnityEngine.SceneManagement.Scene preferScene = default;
		bool preferSceneValid = nearTrigger != null && nearTrigger.gameObject.scene.IsValid() && nearTrigger.gameObject.scene.isLoaded;
		if (preferSceneValid)
		{
			preferScene = nearTrigger.gameObject.scene;
		}
		Animator[] array = Resources.FindObjectsOfTypeAll<Animator>();
		for (int i = 0; i < array.Length; i++)
		{
			Animator animator = array[i];
			if (animator == null || animator.runtimeAnimatorController == null)
			{
				continue;
			}
			// Floor trigger / cinematic animator is not the body behind the counter.
			if (triggerAnim != null && animator == triggerAnim)
			{
				continue;
			}
			if (IsOurGravyWalkerOrTemplate(animator.gameObject))
			{
				continue;
			}
			if (animator.GetComponentInParent<CameraSwapTrigger>() != null && CountWalkerBodyRenderers(animator.gameObject) == 0)
			{
				continue;
			}
			string ctrl = animator.runtimeAnimatorController.name ?? "";
			if (IsEnemyOrGrabControllerName(ctrl))
			{
				continue;
			}
			string goName = NameRemap.StripCloneSuffix(animator.gameObject.name);
			int score = ScoreGravyIdleAnimator(ctrl, goName);
			if (score == 0)
			{
				continue;
			}
			int body = CountWalkerBodyRenderers(animator.gameObject);
			if (body == 0)
			{
				continue;
			}
			// Name-only hits need a real Idle controller hint or strong body signal.
			if (score < 50 && body < 2 && CountSolidRenderers(animator.gameObject) < 1)
			{
				continue;
			}
			score += body * 5;
			if (animator.gameObject.scene.IsValid() && animator.gameObject.scene.isLoaded)
			{
				score += 20;
				if (preferSceneValid && animator.gameObject.scene == preferScene)
				{
					score += 40;
					string sceneName = preferScene.name ?? "";
					if (sceneName.IndexOf("shop", StringComparison.OrdinalIgnoreCase) >= 0)
					{
						score += 15;
					}
				}
			}
			if (hasAnchor && animator.gameObject.scene.IsValid() && animator.gameObject.scene.isLoaded)
			{
				float dist = Vector3.Distance(animator.transform.position, anchor);
				// Behind the counter: typically a few metres from the floor zone.
				if (dist < 25f)
				{
					score += Mathf.RoundToInt(Mathf.Lerp(30f, 0f, dist / 25f));
				}
			}
			if (score > bestScore)
			{
				bestScore = score;
				best = animator;
			}
		}
		return best;
	}

	private static bool IsOurGravyWalkerOrTemplate(GameObject go)
	{
		if (go == null)
		{
			return true;
		}
		if (go.GetComponentInParent<GravySerpentMarker>() != null
			|| go.GetComponentInParent<GravyVisual>() != null
			|| go.GetComponentInParent<GravyGrabDriver>() != null)
		{
			return true;
		}
		string name = NameRemap.StripCloneSuffix(go.name);
		if (name.Equals("GravyVisualTemplate", StringComparison.OrdinalIgnoreCase)
			|| name.Equals("GravyVisual", StringComparison.OrdinalIgnoreCase)
			|| name.Equals("GravyGrabModelTemplate", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		// Our debug spawn renames the serpent — never re-cache it as the shop Idle prop.
		if (name.Equals("Gravy_Minotaur", StringComparison.OrdinalIgnoreCase)
			&& go.GetComponentInParent<EnemyAI>() != null)
		{
			return true;
		}
		return false;
	}

	private static bool IsEnemyOrGrabControllerName(string ctrl)
	{
		if (string.IsNullOrEmpty(ctrl))
		{
			return false;
		}
		return ctrl.IndexOf("serpent", StringComparison.OrdinalIgnoreCase) >= 0
			|| ctrl.IndexOf("enemy", StringComparison.OrdinalIgnoreCase) >= 0
			|| ctrl.IndexOf("brawler", StringComparison.OrdinalIgnoreCase) >= 0
			|| ctrl.IndexOf("grab", StringComparison.OrdinalIgnoreCase) >= 0
			|| ctrl.IndexOf("gallery", StringComparison.OrdinalIgnoreCase) >= 0
			|| ctrl.IndexOf("gloryhole", StringComparison.OrdinalIgnoreCase) >= 0;
	}

	private static int ScoreGravyIdleAnimator(string ctrl, string goName)
	{
		if (string.IsNullOrEmpty(ctrl))
		{
			ctrl = "";
		}
		if (IsEnemyOrGrabControllerName(ctrl))
		{
			return 0;
		}
		if (ctrl.Equals("Gravy/Minotaur", StringComparison.OrdinalIgnoreCase)
			|| (ctrl.IndexOf("gravy", StringComparison.OrdinalIgnoreCase) >= 0
				&& ctrl.IndexOf("minotaur", StringComparison.OrdinalIgnoreCase) >= 0))
		{
			return 100;
		}
		if (ctrl.Equals("Gravy", StringComparison.OrdinalIgnoreCase) || ctrl.Equals("Minotaur", StringComparison.OrdinalIgnoreCase))
		{
			return 80;
		}
		if (ctrl.IndexOf("gravy", StringComparison.OrdinalIgnoreCase) >= 0 || ctrl.IndexOf("minotaur", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			return 50;
		}
		if (!string.IsNullOrEmpty(goName)
			&& (goName.IndexOf("gravy", StringComparison.OrdinalIgnoreCase) >= 0 || goName.IndexOf("minotaur", StringComparison.OrdinalIgnoreCase) >= 0)
			&& goName.IndexOf("grab", StringComparison.OrdinalIgnoreCase) < 0
			&& goName.IndexOf("peep", StringComparison.OrdinalIgnoreCase) < 0
			&& goName.IndexOf("camera", StringComparison.OrdinalIgnoreCase) < 0
			&& goName.IndexOf("bath", StringComparison.OrdinalIgnoreCase) < 0
			&& goName.IndexOf("template", StringComparison.OrdinalIgnoreCase) < 0)
		{
			return 35;
		}
		return 0;
	}

	private static string GetGalleryEntryId(CameraSwapTrigger trigger)
	{
		if (trigger == null)
		{
			return null;
		}
		FieldInfo field = typeof(CameraSwapTrigger).GetField("galleryEntryID", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		return field?.GetValue(trigger) as string;
	}

	private static Animator GetTriggerAnimator(CameraSwapTrigger trigger)
	{
		if (trigger == null)
		{
			return null;
		}
		FieldInfo field = typeof(CameraSwapTrigger).GetField("animatorToTrigger", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		object obj = field?.GetValue(trigger);
		return obj as Animator;
	}

	private static bool TryAttachStatueVisual(GameObject enemy)
	{
        var existingPresentation=enemy.GetComponent<ReskinPresentation>();
        if(existingPresentation!=null && existingPresentation.HasVisual && enemy.GetComponentInChildren<GravyVisual>(true)!=null) {existingPresentation.Enforce();return true;}


        if(!CreatorVisualHost.TryWorld("Gallery_Gravy",out var template,out var idle,_cachedIdleController))return false;
        var gameObject=OverkeksMode.InstantiateOwned(template,enemy.transform);gameObject.name="GravyVisual";
        OverkeksMode.AddOwnedComponent<GravyVisual>(gameObject);
        gameObject.SetActive(true);MatchLayerRecursive(gameObject,enemy.layer);
        var animator=gameObject.GetComponent<Animator>();animator.runtimeAnimatorController=idle;
        animator.Play("Idle",0,0f);animator.Update(.001f);
        var bridge=gameObject.GetComponent<GravyIdleVisualBridge>();
        bridge.Source=gameObject.GetComponent<Image>();bridge.Target=gameObject.GetComponentInChildren<SpriteRenderer>(true);bridge.Sync();
        if(!CreatorVisualHost.Valid(bridge.Source)){gameObject.SetActive(false);UnityEngine.Object.Destroy(gameObject);return false;}
        FitVisualToEnemy(enemy, gameObject, scaleMul:0.8f);
        float ground=CombinedRendererBounds(enemy,includeOwnedVisual:false).min.y;
        float dy=ground-bridge.Target.bounds.min.y;gameObject.transform.position+=Vector3.up*dy;
        bridge.GroundY=ground;bridge.SnapFeetToGround=true;
        var presentation=enemy.GetComponent<ReskinPresentation>() ?? OverkeksMode.AddOwnedComponent<ReskinPresentation>(enemy);presentation.Configure(gameObject);
        return true;
 
	}

	/// <summary>True mesh/skinned body pieces — excludes TMP text quads and UI canvas meshes.</summary>
	private static int CountWorldMeshRenderers(GameObject root)
	{
		if (root == null)
		{
			return 0;
		}
		int num = 0;
		Renderer[] componentsInChildren = root.GetComponentsInChildren<Renderer>(includeInactive: true);
		for (int i = 0; i < componentsInChildren.Length; i++)
		{
			Renderer renderer = componentsInChildren[i];
			if (renderer == null || IsUiOrTextMesh(renderer))
			{
				continue;
			}
			if (!(renderer is SkinnedMeshRenderer) && renderer.GetComponent<MeshFilter>() == null)
			{
				continue;
			}
			string typeName = renderer.GetType().Name;
			if (typeName == "ParticleSystemRenderer" || typeName == "TrailRenderer" || typeName == "LineRenderer")
			{
				continue;
			}
			num++;
		}
		return num;
	}

	/// <summary>Walker-visible body: real meshes and/or SpriteRenderers (PNC enemies are often sprites).</summary>
	private static int CountWalkerBodyRenderers(GameObject root)
	{
		if (root == null)
		{
			return 0;
		}
		int num = CountWorldMeshRenderers(root);
		SpriteRenderer[] sprites = root.GetComponentsInChildren<SpriteRenderer>(includeInactive: true);
		for (int i = 0; i < sprites.Length; i++)
		{
			SpriteRenderer spriteRenderer = sprites[i];
			if (spriteRenderer == null || spriteRenderer.sprite == null)
			{
				continue;
			}
			if (spriteRenderer.GetComponentInParent<Canvas>() != null)
			{
				continue;
			}
			if (GetRendererExtentSqr(spriteRenderer) < 0.01f)
			{
				continue;
			}
			num++;
		}
		return num;
	}

	private static bool HasUsableWalkerVisual(GameObject root)
	{
		return CountWalkerBodyRenderers(root) > 0;
	}

	private static bool IsUiOrTextMesh(Renderer renderer)
	{
		if (renderer == null)
		{
			return true;
		}
		if (renderer.GetComponentInParent<Canvas>() != null)
		{
			return true;
		}
		string goName = renderer.gameObject.name ?? "";
		if (goName.StartsWith("Text", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		Component[] components = renderer.GetComponents<Component>();
		for (int i = 0; i < components.Length; i++)
		{
			if (components[i] == null)
			{
				continue;
			}
			string typeName = components[i].GetType().Name;
			if (typeName.IndexOf("TextMeshPro", StringComparison.OrdinalIgnoreCase) >= 0
				|| typeName.Equals("TMP_Text", StringComparison.OrdinalIgnoreCase)
				|| typeName.Equals("TextMesh", StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}
		Material[] mats = renderer.sharedMaterials;
		if (mats != null)
		{
			for (int j = 0; j < mats.Length; j++)
			{
				if (mats[j] != null && mats[j].name != null
					&& mats[j].name.IndexOf("SDF", StringComparison.OrdinalIgnoreCase) >= 0)
				{
					return true;
				}
			}
		}
		if (!(renderer is SkinnedMeshRenderer) && GetRendererExtentSqr(renderer) < 0.0001f)
		{
			return true;
		}
		return false;
	}

	private static void BrightenStatueVisual(GameObject visual)
	{
		if (visual == null)
		{
			return;
		}
		const float boost = 1.35f;
		Color bright = new Color(boost, boost, boost, 1f);
		SpriteRenderer[] srs = visual.GetComponentsInChildren<SpriteRenderer>(includeInactive: true);
		for (int i = 0; i < srs.Length; i++)
		{
			if (srs[i] != null)
			{
				srs[i].color = bright;
			}
		}
		Image[] images = visual.GetComponentsInChildren<Image>(includeInactive: true);
		for (int i = 0; i < images.Length; i++)
		{
			if (images[i] != null)
			{
				images[i].color = bright;
			}
		}
	}

	/// <summary>
	/// Gallery shows Baphomet on shared displayModel/grabDisplayModel (controller swap only) -
	/// same pattern Custom Enemies replace for their packages. Steal those meshes.
	/// Must be called while EnemyGalleryUI exists (main menu) - dungeon unloads it.
	/// </summary>
	internal static void TryCacheVisualFromGalleryUi()
	{
		if (_cachedVisualTemplate != null && HasAnyMesh(_cachedVisualTemplate) && _cachedGrabModelTemplate != null && HasAnyMesh(_cachedGrabModelTemplate))
		{
			return;
		}
		EnemyGalleryUI ui = FindEnemyGalleryUi();
		if (ui == null)
		{
			Plugin.DBG("SPAWN", LogLabel + ": EnemyGalleryUI not loaded yet - no displayModel to steal");
			return;
		}
		CacheModelsFromUi(ui);
	}

	internal static void CacheModelsFromUi(EnemyGalleryUI ui)
	{
		if (ui == null)
		{
			return;
		}
		Traverse t = Traverse.Create((object)ui);
		GameObject displayModel = t.Field("displayModel").GetValue<GameObject>();
		GameObject grabDisplayModel = t.Field("grabDisplayModel").GetValue<GameObject>();
		GameObject grabScenePanel = t.Field("grabScenePanel").GetValue<GameObject>();
		Animator displayAnimator = t.Field("displayAnimator").GetValue<Animator>();
		Animator grabSceneAnimator = t.Field("grabSceneAnimator").GetValue<Animator>();
		bool displayWas = displayModel != null && displayModel.activeSelf;
		bool grabWas = grabDisplayModel != null && grabDisplayModel.activeSelf;
		bool panelWas = grabScenePanel != null && grabScenePanel.activeSelf;
		try
		{
			if (displayModel != null)
			{
				ForceActivateHierarchy(displayModel);
			}
			if (grabScenePanel != null)
			{
				grabScenePanel.SetActive(value: true);
			}
			if (grabDisplayModel != null)
			{
				ForceActivateHierarchy(grabDisplayModel);
			}
			if (grabSceneAnimator != null)
			{
				ForceActivateHierarchy(grabSceneAnimator.gameObject);
				if (_cachedGrabController != null)
				{
					grabSceneAnimator.runtimeAnimatorController = _cachedGrabController;
					grabSceneAnimator.enabled = true;
					try
					{
						grabSceneAnimator.Play(0, 0, 0f);
					}
					catch
					{
					}
					grabSceneAnimator.Update(0f);
				}
			}
			if (displayAnimator != null && _cachedIdleController != null)
			{
				ForceActivateHierarchy(displayAnimator.gameObject);
				displayAnimator.runtimeAnimatorController = _cachedIdleController;
				displayAnimator.enabled = true;
				displayAnimator.Update(0f);
			}
			if (_cachedVisualTemplate == null || !HasAnyMesh(_cachedVisualTemplate))
			{
				GameObject source = PickRichestVisual(displayModel, displayAnimator != null ? displayAnimator.gameObject : null);
				if (source != null && HasAnyMesh(source))
				{
					CacheVisualFromGameObject(source, "gallery.display:'" + NameRemap.StripCloneSuffix(source.name) + "'");
				}
				else
				{
					Plugin.DBG("SPAWN", LogLabel + ": gallery displayModel missing/empty (r=" + CountAnyRenderers(displayModel) + " img=" + CountUiImages(displayModel) + "/" + CountUiImages(displayAnimator != null ? displayAnimator.gameObject : null) + ")");
				}
			}
			if (_cachedGrabModelTemplate == null || !HasAnyMesh(_cachedGrabModelTemplate))
			{
				GameObject grabSource = PickRichestVisual(grabDisplayModel, grabSceneAnimator != null ? grabSceneAnimator.gameObject : null);
				if (grabSource != null && HasAnyMesh(grabSource))
				{
					CacheGrabUiTemplate(grabSource, "gallery.grab:'" + NameRemap.StripCloneSuffix(grabSource.name) + "'");
				}
				else
				{
					Plugin.DBG("SPAWN", LogLabel + ": gallery grab model missing/empty (r=" + CountAnyRenderers(grabDisplayModel) + " img=" + CountUiImages(grabDisplayModel) + "/" + CountUiImages(grabSceneAnimator != null ? grabSceneAnimator.gameObject : null) + ")");
				}
			}
		}
		finally
		{
			if (displayModel != null)
			{
				displayModel.SetActive(displayWas);
			}
			if (grabDisplayModel != null)
			{
				grabDisplayModel.SetActive(grabWas);
			}
			if (grabScenePanel != null)
			{
				grabScenePanel.SetActive(panelWas);
			}
		}
	}

	private static void CacheGrabUiTemplate(GameObject source, string via)
	{
		if (source == null || (_cachedGrabModelTemplate != null && HasAnyMesh(_cachedGrabModelTemplate)))
		{
			return;
		}
		GameObject gameObject = OverkeksMode.InstantiateOwned(source);
		gameObject.name = "GravyGrabModelTemplate";
		gameObject.SetActive(value: false);
		StripInteractJunk(gameObject);
		EnableAllRenderers(gameObject);
		EnableAllUiImages(gameObject);
		UnityEngine.Object.DontDestroyOnLoad(gameObject);
		_cachedGrabModelTemplate = gameObject;
		Plugin.DBG("SPAWN", LogLabel + ": cached grab UI via " + via + " img=" + CountUiImages(gameObject) + " r=" + CountAnyRenderers(gameObject));
	}

	private static void EnableAllUiImages(GameObject root)
	{
		if (root == null)
		{
			return;
		}
		Image[] images = root.GetComponentsInChildren<Image>(includeInactive: true);
		for (int i = 0; i < images.Length; i++)
		{
			if (images[i] != null)
			{
				images[i].enabled = true;
				images[i].gameObject.SetActive(value: true);
			}
		}
		Graphic[] graphics = root.GetComponentsInChildren<Graphic>(includeInactive: true);
		for (int j = 0; j < graphics.Length; j++)
		{
			if (graphics[j] != null)
			{
				graphics[j].enabled = true;
			}
		}
	}

	private static GameObject PickRichestVisual(GameObject a, GameObject b)
	{
		int scoreA = CountAnyRenderers(a) * 10 + CountMeshFilters(a) * 5 + CountSolidRenderers(a) + CountUiImages(a) * 20;
		int scoreB = CountAnyRenderers(b) * 10 + CountMeshFilters(b) * 5 + CountSolidRenderers(b) + CountUiImages(b) * 20;
		if (scoreA <= 0 && scoreB <= 0)
		{
			return null;
		}
		return scoreB > scoreA ? b : a;
	}

	private static void ForceActivateHierarchy(GameObject root)
	{
		if (root == null)
		{
			return;
		}
		root.SetActive(value: true);
		Transform[] transforms = root.GetComponentsInChildren<Transform>(includeInactive: true);
		for (int i = 0; i < transforms.Length; i++)
		{
			if (transforms[i] != null)
			{
				transforms[i].gameObject.SetActive(value: true);
			}
		}
	}

	private static EnemyGalleryUI FindEnemyGalleryUi()
	{
		EnemyGalleryUI[] array = Resources.FindObjectsOfTypeAll<EnemyGalleryUI>();
		if (array == null || array.Length == 0)
		{
			// Fall back to MainMenuManager reference (present in menu scene even if UI inactive).
			MainMenuManager[] menus = Resources.FindObjectsOfTypeAll<MainMenuManager>();
			for (int m = 0; m < menus.Length; m++)
			{
				if (menus[m] == null)
				{
					continue;
				}
				EnemyGalleryUI fromMenu = Traverse.Create((object)menus[m]).Field("enemyGalleryUI").GetValue<EnemyGalleryUI>();
				if (fromMenu != null)
				{
					return fromMenu;
				}
			}
			return null;
		}
		EnemyGalleryUI best = null;
		int bestScore = -1;
		for (int i = 0; i < array.Length; i++)
		{
			EnemyGalleryUI enemyGalleryUI = array[i];
			if (enemyGalleryUI == null)
			{
				continue;
			}
			int num = 0;
			if (enemyGalleryUI.gameObject.scene.IsValid() && enemyGalleryUI.gameObject.scene.isLoaded)
			{
				num += 20;
			}
			Traverse t = Traverse.Create((object)enemyGalleryUI);
			if (t.Field("displayModel").GetValue<GameObject>() != null)
			{
				num += 10;
			}
			if (t.Field("grabDisplayModel").GetValue<GameObject>() != null)
			{
				num += 10;
			}
			if (num > bestScore)
			{
				bestScore = num;
				best = enemyGalleryUI;
			}
		}
		return best;
	}

	private static void DumpGravyNamedObjects()
	{
		int named = 0;
		GameObject[] array = Resources.FindObjectsOfTypeAll<GameObject>();
		StringBuilder stringBuilder = new StringBuilder();
		stringBuilder.Append(LogLabel).Append(" named-gravy dump:");
		for (int i = 0; i < array.Length; i++)
		{
			GameObject gameObject = array[i];
			if (gameObject == null)
			{
				continue;
			}
			string text = NameRemap.StripCloneSuffix(gameObject.name);
			if (text.IndexOf("gravy", StringComparison.OrdinalIgnoreCase) < 0 && text.IndexOf("minotaur", StringComparison.OrdinalIgnoreCase) < 0 && text.IndexOf("minothaur", StringComparison.OrdinalIgnoreCase) < 0)
			{
				continue;
			}
			named++;
			if (named <= 12)
			{
				string types = SummarizeRendererTypes(gameObject);
				stringBuilder.Append(" | '").Append(text).Append("' r=").Append(CountAnyRenderers(gameObject)).Append(" mf=").Append(CountMeshFilters(gameObject)).Append(" solid=").Append(CountSolidRenderers(gameObject)).Append(" body=").Append(CountWalkerBodyRenderers(gameObject)).Append(" types=").Append(types);
			}
		}
		stringBuilder.Append(" (totalNamed=").Append(named).Append(")");
		Plugin.DBG("SPAWN", stringBuilder.ToString());
	}

	private static void DumpGravyIdleControllers()
	{
		RuntimeAnimatorController[] array = Resources.FindObjectsOfTypeAll<RuntimeAnimatorController>();
		int hits = 0;
		StringBuilder sb = new StringBuilder();
		sb.Append(LogLabel).Append(" gravy-ctrl dump:");
		for (int i = 0; i < array.Length; i++)
		{
			RuntimeAnimatorController ctrl = array[i];
			if (ctrl == null || string.IsNullOrEmpty(ctrl.name))
			{
				continue;
			}
			string name = ctrl.name;
			if (name.IndexOf("gravy", StringComparison.OrdinalIgnoreCase) < 0
				&& name.IndexOf("minotaur", StringComparison.OrdinalIgnoreCase) < 0
				&& name.IndexOf("minothaur", StringComparison.OrdinalIgnoreCase) < 0)
			{
				continue;
			}
			hits++;
			if (hits <= 16)
			{
				sb.Append(" | '").Append(name).Append("'");
			}
		}
		Plugin.DBG("SPAWN", sb.Append(" (hits=").Append(hits).Append(")").ToString());
	}

	private static void DumpGravyIdleSprites()
	{
		Sprite[] array = Resources.FindObjectsOfTypeAll<Sprite>();
		int hits = 0;
		StringBuilder sb = new StringBuilder();
		sb.Append(LogLabel).Append(" gravy-sprite dump:");
		for (int i = 0; i < array.Length; i++)
		{
			Sprite sprite = array[i];
			if (sprite == null || string.IsNullOrEmpty(sprite.name))
			{
				continue;
			}
			int score = ScoreIdleSprite(sprite.name, sprite.rect);
			string lower = sprite.name.ToLowerInvariant();
			if (score <= 0 && !(lower.Contains("gravy") || lower.Contains("minotaur")))
			{
				continue;
			}
			hits++;
			if (hits <= 14)
			{
				sb.Append(" | '").Append(sprite.name).Append("' ").Append(sprite.rect.width).Append("x").Append(sprite.rect.height).Append(" sc=").Append(score);
			}
		}
		Plugin.DBG("SPAWN", sb.Append(" (hits=").Append(hits).Append("/").Append(array.Length).Append(")").ToString());
	}

	/// <summary>
	/// Prefer Gallery_Gravy Idle (one looping anim in the enemy gallery) as the walker body.
	/// Shop prop is optional — gallery already has the animated Gravy/Minotaur.
	/// </summary>
	private static void TryCacheVisualFromGalleryEntry()
	{
		if (HasUsableWalkerVisual(_cachedVisualTemplate))
		{
			return;
		}
		EnemyGalleryEntry entry = FindBestGravyGalleryEntry();
		if (entry == null)
		{
			Plugin.DBG("SPAWN", LogLabel + ": no GravyGalleryEntry in memory for gallery idle visual");
			return;
		}
		if (entry.animatorController != null)
		{
			_cachedIdleController = entry.animatorController;
		}
		if (_cachedIdleController == null)
		{
			Plugin.DBG("SPAWN", LogLabel + ": gallery entry '" + (entry.enemyID ?? entry.name) + "' has no idle controller");
			return;
		}
		_cachedIdlePlayState = PickGalleryIdleState(entry);
		Plugin.DBG("SPAWN", LogLabel + ": gallery entry '" + (entry.enemyID ?? entry.name) + "' idleCtrl='" + _cachedIdleController.name + "' play='" + _cachedIdlePlayState + "' clips=" + SummarizeControllerClips(_cachedIdleController));
		if (BuildAnimatedGalleryIdleVisual(_cachedIdleController, _cachedIdlePlayState, "gallery-idle:'" + _cachedIdleController.name + "'"))
		{
			return;
		}
		TryBuildAnimatedIdleFromGrabUiHost(_cachedIdleController, _cachedIdlePlayState);
	}

	private static string _cachedIdlePlayState = "Idle";

	private static string PickGalleryIdleState(EnemyGalleryEntry entry)
	{
		if (entry != null && entry.availableAnimations != null)
		{
			for (int i = 0; i < entry.availableAnimations.Length; i++)
			{
				string text = entry.availableAnimations[i];
				if (!string.IsNullOrEmpty(text))
				{
					return text;
				}
			}
		}
		return "Idle";
	}

	private static string SummarizeControllerClips(RuntimeAnimatorController controller)
	{
		if (controller == null || controller.animationClips == null)
		{
			return "<none>";
		}
		StringBuilder sb = new StringBuilder();
		int limit = Math.Min(controller.animationClips.Length, 8);
		for (int i = 0; i < limit; i++)
		{
			if (i > 0)
			{
				sb.Append(',');
			}
			sb.Append(controller.animationClips[i] != null ? controller.animationClips[i].name : "?");
		}
		return sb.ToString();
	}

	/// <summary>
	/// Host matching gallery grab bindings: Animator + child Image "Image", bridge frames to world SpriteRenderer.
	/// </summary>
	private static bool BuildAnimatedGalleryIdleVisual(RuntimeAnimatorController controller, string playState, string via)
	{
		if (controller == null || HasUsableWalkerVisual(_cachedVisualTemplate))
		{
			return HasUsableWalkerVisual(_cachedVisualTemplate);
		}
		if (_cachedVisualTemplate != null)
		{
			UnityEngine.Object.Destroy(_cachedVisualTemplate);
			_cachedVisualTemplate = null;
		}
		GameObject root = OverkeksMode.NewOwned("GravyVisualTemplate");
		root.SetActive(value: true);
		SpriteRenderer worldSprite = OverkeksMode.AddOwnedComponent<SpriteRenderer>(root);
		worldSprite.enabled = true;
		Animator animator = OverkeksMode.AddOwnedComponent<Animator>(root);
		animator.runtimeAnimatorController = controller;
		animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
		animator.enabled = true;
		GameObject canvasGo = OverkeksMode.NewOwned("IdleCanvas", typeof(RectTransform));
		canvasGo.transform.SetParent(root.transform, worldPositionStays: false);
		Canvas canvas = OverkeksMode.AddOwnedComponent<Canvas>(canvasGo);
		canvas.renderMode = RenderMode.WorldSpace;
		canvas.enabled = false;
		GameObject imageGo = OverkeksMode.NewOwned("Image", typeof(RectTransform));
		imageGo.transform.SetParent(canvasGo.transform, worldPositionStays: false);
		Image image = OverkeksMode.AddOwnedComponent<Image>(imageGo);
		image.raycastTarget = false;
		image.color = Color.white;
		image.enabled = true;
		// Also try Image as direct child of animator root (common gallery binding path).
		GameObject imageDirect = OverkeksMode.NewOwned("Image", typeof(RectTransform));
		imageDirect.transform.SetParent(root.transform, worldPositionStays: false);
		Image image2 = OverkeksMode.AddOwnedComponent<Image>(imageDirect);
		image2.raycastTarget = false;
		image2.color = Color.white;
		image2.enabled = true;
		GravyIdleVisualBridge bridge = OverkeksMode.AddOwnedComponent<GravyIdleVisualBridge>(root);
		bridge.Target = worldSprite;
		bridge.Source = image2;
		if (root.GetComponent<GravyVisual>() == null)
		{
			OverkeksMode.AddOwnedComponent<GravyVisual>(root);
		}
		PlayIdleStates(animator, playState);
		animator.Update(0.02f);
		Image driven = PickDrivenImage(image2, image);
		bridge.Source = driven;
		bridge.Sync();
		if (worldSprite.sprite == null)
		{
			// Fallback: binding may target SpriteRenderer on a child named Sprite.
			GameObject spriteChild = OverkeksMode.NewOwned("Sprite");
			spriteChild.transform.SetParent(root.transform, worldPositionStays: false);
			SpriteRenderer childSr = OverkeksMode.AddOwnedComponent<SpriteRenderer>(spriteChild);
			PlayIdleStates(animator, playState);
			animator.Update(0.02f);
			if (childSr.sprite != null)
			{
				worldSprite.sprite = childSr.sprite;
				bridge.Source = null;
				UnityEngine.Object.Destroy(spriteChild);
			}
			else
			{
				UnityEngine.Object.Destroy(spriteChild);
			}
			bridge.Sync();
		}
		LogRendererDump(root, "cache");
		Plugin.DBG("SPAWN", LogLabel + ": animated idle build via " + via + " imgSprite=" + (driven != null && driven.sprite != null ? driven.sprite.name : "<null>") + " worldSprite=" + (worldSprite.sprite != null ? worldSprite.sprite.name : "<null>") + " body=" + CountWalkerBodyRenderers(root));
		if (!HasUsableWalkerVisual(root))
		{
			UnityEngine.Object.Destroy(root);
			Plugin.DBG("SPAWN", LogLabel + ": gallery idle host got no frames from '" + controller.name + "'");
			return false;
		}
		root.SetActive(value: false);
		UnityEngine.Object.DontDestroyOnLoad(root);
		_cachedVisualTemplate = root;
		Plugin.DBG("SPAWN", LogLabel + ": cached statue visual via " + via + " solid=" + CountSolidRenderers(root) + " body=" + CountWalkerBodyRenderers(root) + " animated=True");
		return true;
	}

	private static void TryBuildAnimatedIdleFromGrabUiHost(RuntimeAnimatorController controller, string playState)
	{
		if (controller == null || HasUsableWalkerVisual(_cachedVisualTemplate))
		{
			return;
		}
		if (_cachedGrabModelTemplate == null || CountUiImages(_cachedGrabModelTemplate) == 0)
		{
			Plugin.DBG("SPAWN", LogLabel + ": no grab UI host to reuse for gallery idle");
			return;
		}
		if (_cachedVisualTemplate != null)
		{
			UnityEngine.Object.Destroy(_cachedVisualTemplate);
			_cachedVisualTemplate = null;
		}
		GameObject root = OverkeksMode.InstantiateOwned(_cachedGrabModelTemplate);
		root.name = "GravyVisualTemplate";
		root.SetActive(value: true);
		ForceActivateHierarchy(root);
		EnableAllUiImages(root);
		SpriteRenderer worldSprite = root.GetComponent<SpriteRenderer>();
		if (worldSprite == null)
		{
			worldSprite = OverkeksMode.AddOwnedComponent<SpriteRenderer>(root);
		}
		worldSprite.enabled = true;
		Animator animator = root.GetComponentInChildren<Animator>(includeInactive: true);
		if (animator == null)
		{
			animator = OverkeksMode.AddOwnedComponent<Animator>(root);
		}
		animator.runtimeAnimatorController = controller;
		animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
		animator.enabled = true;
		Image image = FindBestUiImage(root);
		if (image != null)
		{
			image.gameObject.name = "Image";
			image.enabled = true;
			image.color = Color.white;
		}
		GravyIdleVisualBridge bridge = root.GetComponent<GravyIdleVisualBridge>();
		if (bridge == null)
		{
			bridge = OverkeksMode.AddOwnedComponent<GravyIdleVisualBridge>(root);
		}
		bridge.Source = image;
		bridge.Target = worldSprite;
		if (root.GetComponent<GravyVisual>() == null)
		{
			OverkeksMode.AddOwnedComponent<GravyVisual>(root);
		}
		// Hide canvases so only the world sprite shows in-level.
		Canvas[] canvases = root.GetComponentsInChildren<Canvas>(includeInactive: true);
		for (int i = 0; i < canvases.Length; i++)
		{
			if (canvases[i] != null)
			{
				canvases[i].enabled = false;
			}
		}
		PlayIdleStates(animator, playState);
		animator.Update(0.02f);
		bridge.Sync();
		LogRendererDump(root, "cache");
		Plugin.DBG("SPAWN", LogLabel + ": grab-host idle img=" + (image != null && image.sprite != null ? image.sprite.name : "<null>") + " world=" + (worldSprite.sprite != null ? worldSprite.sprite.name : "<null>") + " body=" + CountWalkerBodyRenderers(root));
		if (!HasUsableWalkerVisual(root))
		{
			UnityEngine.Object.Destroy(root);
			Plugin.DBG("SPAWN", LogLabel + ": grab-host gallery idle produced no world sprite");
			return;
		}
		root.SetActive(value: false);
		UnityEngine.Object.DontDestroyOnLoad(root);
		_cachedVisualTemplate = root;
		Plugin.DBG("SPAWN", LogLabel + ": cached statue visual via grab-host-idle:'" + controller.name + "' solid=" + CountSolidRenderers(root) + " body=" + CountWalkerBodyRenderers(root) + " animated=True");
	}

	private static Image PickDrivenImage(Image a, Image b)
	{
		if (a != null && a.sprite != null)
		{
			return a;
		}
		if (b != null && b.sprite != null)
		{
			return b;
		}
		return a != null ? a : b;
	}

	private static void PlayIdleStates(Animator animator, string preferred)
	{
		if (animator == null)
		{
			return;
		}
		string[] tries = { preferred, "Idle", "Gallery_Gravy_Idle", "idle", "Stand", "Walk" };
		for (int i = 0; i < tries.Length; i++)
		{
			if (string.IsNullOrEmpty(tries[i]))
			{
				continue;
			}
			try
			{
				animator.Play(tries[i], 0, 0f);
				animator.Update(0f);
				return;
			}
			catch
			{
			}
		}
		try
		{
			animator.Play(0, 0, 0f);
			animator.Update(0f);
		}
		catch
		{
		}
	}

	private static EnemyGalleryEntry FindBestGravyGalleryEntry()
	{
		TryForceLoadGravyResources();
		EnemyGalleryEntry[] array = Resources.FindObjectsOfTypeAll<EnemyGalleryEntry>();
		EnemyGalleryEntry best = null;
		int bestScore = 0;
		for (int i = 0; i < array.Length; i++)
		{
			EnemyGalleryEntry entry = array[i];
			if (entry == null)
			{
				continue;
			}
			int score = ScoreGravyGallery(entry);
			if (score > bestScore)
			{
				bestScore = score;
				best = entry;
			}
		}
		return best;
	}

	/// <summary>Last-resort static sprite — prefer animated gallery idle above.</summary>
	private static void TryCacheVisualFromIdleSprites()
	{
		if (HasUsableWalkerVisual(_cachedVisualTemplate))
		{
			return;
		}
		Plugin.DBG("SPAWN", LogLabel + ": skipping static sprite fallback (need animated Gallery_Gravy idle)");
	}

	private static int ScoreIdleSprite(string name, Rect rect)
	{
		string text = (name ?? "").ToLowerInvariant();
		if (!(text.Contains("gravy") || text.Contains("minotaur") || text.Contains("minothaur")))
		{
			return 0;
		}
		// H-scene / peep sheets are wrong for the walker.
		if (text.Contains("sex") || text.Contains("cum") || text.Contains("grab") || text.Contains("bath")
			|| text.Contains("peep") || text.Contains("bj") || text.Contains("ride") || text.Contains("pipe_hole"))
		{
			return 0;
		}
		int score = 20;
		if (text.Contains("idle") || text.Contains("stand") || text.Contains("icon") || text.Contains("gallery"))
		{
			score += 40;
		}
		if (text.EndsWith("_0") || text.Contains("sheet_0"))
		{
			score += 10;
		}
		float area = rect.width * rect.height;
		if (area >= 64f * 64f)
		{
			score += 15;
		}
		if (area >= 128f * 128f)
		{
			score += 10;
		}
		return score;
	}

	private static string SummarizeRendererTypes(GameObject root)
	{
		Renderer[] componentsInChildren = root.GetComponentsInChildren<Renderer>(includeInactive: true);
		if (componentsInChildren == null || componentsInChildren.Length == 0)
		{
			return "<none>";
		}
		StringBuilder stringBuilder = new StringBuilder();
		int limit = Math.Min(componentsInChildren.Length, 4);
		for (int i = 0; i < limit; i++)
		{
			if (i > 0)
			{
				stringBuilder.Append(',');
			}
			Renderer renderer = componentsInChildren[i];
			stringBuilder.Append(renderer != null ? renderer.GetType().Name : "?");
			if (renderer != null)
			{
				Material[] mats = renderer.sharedMaterials;
				stringBuilder.Append('{').Append(mats != null ? mats.Length : 0).Append('}');
			}
		}
		return stringBuilder.ToString();
	}

	private static bool HasAnyMesh(GameObject root)
	{
		return CountSolidRenderers(root) > 0 || CountMeshFilters(root) > 0 || CountAnyRenderers(root) > 0 || CountUiImages(root) > 0;
	}

	private static int CountUiImages(GameObject root)
	{
		if (root == null)
		{
			return 0;
		}
		Image[] images = root.GetComponentsInChildren<Image>(includeInactive: true);
		return images != null ? images.Length : 0;
	}

	private static int CountAnyRenderers(GameObject root)
	{
		if (root == null)
		{
			return 0;
		}
		int num = 0;
		Renderer[] componentsInChildren = root.GetComponentsInChildren<Renderer>(includeInactive: true);
		for (int i = 0; i < componentsInChildren.Length; i++)
		{
			if (componentsInChildren[i] != null && componentsInChildren[i].GetType().Name != "ParticleSystemRenderer")
			{
				num++;
			}
		}
		return num;
	}

	/// <summary>
	/// World prop behind the shop counter: AnimatorController "Gravy/Minotaur" (Idle) — not the floor trigger.
	/// </summary>
	private static void TryCacheVisualFromStatueController()
	{
		if (HasUsableWalkerVisual(_cachedVisualTemplate))
		{
			return;
		}
		if (_cachedVisualTemplate != null)
		{
			UnityEngine.Object.Destroy(_cachedVisualTemplate);
			_cachedVisualTemplate = null;
		}
		Animator best = FindBestGravyIdleAnimator(null);
		if (best == null)
		{
			Plugin.DBG("SPAWN", LogLabel + ": no Gravy Idle prop (controller Gravy/Minotaur) with body in memory");
			return;
		}
		CacheVisualFromGameObject(best.gameObject, "idle-prop:'" + NameRemap.StripCloneSuffix(best.gameObject.name) + "' ctrl='" + best.runtimeAnimatorController.name + "'");
	}

	private static int CountMeshFilters(GameObject root)
	{
		if (root == null)
		{
			return 0;
		}
		int num = 0;
		MeshFilter[] componentsInChildren = root.GetComponentsInChildren<MeshFilter>(includeInactive: true);
		for (int i = 0; i < componentsInChildren.Length; i++)
		{
			if (componentsInChildren[i] != null && componentsInChildren[i].sharedMesh != null)
			{
				num++;
			}
		}
		SkinnedMeshRenderer[] skinned = root.GetComponentsInChildren<SkinnedMeshRenderer>(includeInactive: true);
		for (int j = 0; j < skinned.Length; j++)
		{
			if (skinned[j] != null && skinned[j].sharedMesh != null)
			{
				num++;
			}
		}
		return num;
	}

	private static void CacheVisualFromGameObject(GameObject source, string via)
	{
		if (source == null || HasUsableWalkerVisual(_cachedVisualTemplate))
		{
			return;
		}
		if (_cachedVisualTemplate != null)
		{
			UnityEngine.Object.Destroy(_cachedVisualTemplate);
			_cachedVisualTemplate = null;
		}
		GameObject gameObject = OverkeksMode.InstantiateOwned(source);
		gameObject.name = "GravyVisualTemplate";
		gameObject.SetActive(value: false);
		StripInteractJunk(gameObject);
		EnableAllRenderers(gameObject);
		if (gameObject.GetComponent<GravyVisual>() == null)
		{
			OverkeksMode.AddOwnedComponent<GravyVisual>(gameObject);
		}
		int solid = CountSolidRenderers(gameObject);
		int meshOnly = CountMeshFilters(gameObject);
		int any = CountAnyRenderers(gameObject);
		int body = CountWalkerBodyRenderers(gameObject);
		LogRendererDump(gameObject, "cache");
		if (!HasUsableWalkerVisual(gameObject))
		{
			UnityEngine.Object.Destroy(gameObject);
			Plugin.DBG("SPAWN", LogLabel + ": visual clone via " + via + " has no walker body (solid=" + solid + " mesh=" + meshOnly + " any=" + any + ")");
			return;
		}
		UnityEngine.Object.DontDestroyOnLoad(gameObject);
		_cachedVisualTemplate = gameObject;
		Plugin.DBG("SPAWN", LogLabel + ": cached statue visual via " + via + " solid=" + solid + " meshFilters=" + meshOnly + " body=" + body + " any=" + any);
	}

	private static int CountSolidRenderers(GameObject root)
	{
		if (root == null)
		{
			return 0;
		}
		int num = 0;
		Renderer[] componentsInChildren = root.GetComponentsInChildren<Renderer>(includeInactive: true);
		for (int i = 0; i < componentsInChildren.Length; i++)
		{
			Renderer renderer = componentsInChildren[i];
			if (renderer == null || IsNonMeshRenderer(renderer))
			{
				// Sprites count - many PNC enemies (and possibly the statue preview) are sprite-based.
				if (renderer != null && renderer.GetType().Name == "SpriteRenderer")
				{
					num++;
				}
				continue;
			}
			if (!HasUsableMaterial(renderer))
			{
				// Still count skinned/mesh renderers even without materials yet - clone may resolve later.
				if (renderer is SkinnedMeshRenderer || renderer.GetComponent<MeshFilter>() != null)
				{
					num++;
				}
				continue;
			}
			if (GetRendererExtentSqr(renderer) < 0.0001f && !(renderer is SkinnedMeshRenderer))
			{
				continue;
			}
			num++;
		}
		return num;
	}

	private static bool IsNonMeshRenderer(Renderer renderer)
	{
		if (renderer == null)
		{
			return true;
		}
		string name = renderer.GetType().Name;
		return name == "ParticleSystemRenderer" || name == "TrailRenderer" || name == "LineRenderer" || name == "BillboardRenderer";
	}

	private static bool HasUsableMaterial(Renderer renderer)
	{
		Material[] sharedMaterials = renderer.sharedMaterials;
		if (sharedMaterials == null || sharedMaterials.Length == 0)
		{
			return false;
		}
		for (int i = 0; i < sharedMaterials.Length; i++)
		{
			if (sharedMaterials[i] != null)
			{
				return true;
			}
		}
		return false;
	}

	private static float GetRendererExtentSqr(Renderer renderer)
	{
		if (renderer is SkinnedMeshRenderer skinned)
		{
			Mesh sharedMesh = skinned.sharedMesh;
			if (sharedMesh != null)
			{
				return sharedMesh.bounds.size.sqrMagnitude;
			}
		}
		MeshFilter meshFilter = renderer.GetComponent<MeshFilter>();
		if (meshFilter != null && meshFilter.sharedMesh != null)
		{
			return meshFilter.sharedMesh.bounds.size.sqrMagnitude;
		}
		try
		{
			return renderer.localBounds.size.sqrMagnitude;
		}
		catch
		{
			return renderer.bounds.size.sqrMagnitude;
		}
	}

	private static void EnableAllRenderers(GameObject root)
	{
		Renderer[] componentsInChildren = root.GetComponentsInChildren<Renderer>(includeInactive: true);
		for (int i = 0; i < componentsInChildren.Length; i++)
		{
			if (componentsInChildren[i] != null)
			{
				componentsInChildren[i].enabled = true;
				componentsInChildren[i].gameObject.SetActive(value: true);
			}
		}
		LODGroup[] lodGroups = root.GetComponentsInChildren<LODGroup>(includeInactive: true);
		for (int j = 0; j < lodGroups.Length; j++)
		{
			if (lodGroups[j] != null)
			{
				lodGroups[j].enabled = true;
				lodGroups[j].ForceLOD(0);
			}
		}
	}

	private static void MatchLayerRecursive(GameObject root, int layer)
	{
		if (root == null)
		{
			return;
		}
		Transform[] transforms = root.GetComponentsInChildren<Transform>(includeInactive: true);
		for (int i = 0; i < transforms.Length; i++)
		{
			if (transforms[i] != null)
			{
				transforms[i].gameObject.layer = layer;
			}
		}
	}

	private static void LogRendererDump(GameObject root, string tag)
	{
		if (root == null)
		{
			return;
		}
		StringBuilder stringBuilder = new StringBuilder();
		stringBuilder.Append(LogLabel).Append(" visual[").Append(tag).Append("] '").Append(NameRemap.StripCloneSuffix(root.name)).Append("'");
		Renderer[] componentsInChildren = root.GetComponentsInChildren<Renderer>(includeInactive: true);
		stringBuilder.Append(" count=").Append(componentsInChildren.Length);
		int limit = Math.Min(componentsInChildren.Length, 8);
		for (int i = 0; i < limit; i++)
		{
			Renderer renderer = componentsInChildren[i];
			if (renderer == null)
			{
				continue;
			}
			string mat = "<none>";
			Material[] sharedMaterials = renderer.sharedMaterials;
			if (sharedMaterials != null && sharedMaterials.Length != 0 && sharedMaterials[0] != null)
			{
				mat = sharedMaterials[0].name;
			}
			stringBuilder.Append(" | ").Append(renderer.GetType().Name).Append(":'").Append(renderer.gameObject.name).Append("' on=").Append(renderer.enabled).Append(" active=").Append(renderer.gameObject.activeInHierarchy).Append(" layer=").Append(renderer.gameObject.layer).Append(" mat='").Append(mat).Append("' ext=").Append(GetRendererExtentSqr(renderer).ToString("0.###"));
		}
		Plugin.DBG("SPAWN", stringBuilder.ToString());
	}

	private static void TryCacheVisualFromNamedAssets()
	{
		if (HasUsableWalkerVisual(_cachedVisualTemplate))
		{
			return;
		}
		GameObject best = null;
		int bestScore = 0;
		GameObject[] array = Resources.FindObjectsOfTypeAll<GameObject>();
		for (int i = 0; i < array.Length; i++)
		{
			GameObject gameObject = array[i];
			if (gameObject == null || IsOurGravyWalkerOrTemplate(gameObject))
			{
				continue;
			}
			string text = NameRemap.StripCloneSuffix(gameObject.name).ToLowerInvariant();
			int num = 0;
			if (text.Equals("gravy") || text.Equals("minotaur") || text.Equals("minothaur") || text.Equals("gravy_minotaur") || text.Equals("gravy/minotaur"))
			{
				num += 80;
			}
			else if (text.Contains("gravy") || text.Contains("minotaur") || text.Contains("minothaur"))
			{
				num += 40;
			}
			if (num == 0)
			{
				continue;
			}
			if (text.StartsWith("gallery_") || text.Contains("gallery") || text.Contains("template") || text.Contains("grabmodel"))
			{
				num -= 40;
			}
			// Peep/bath camera zones are text labels — never the Idle body.
			if (text.Contains("peep") || text.Contains("camera") || text.Contains("bath"))
			{
				continue;
			}
			if (!HasUsableWalkerVisual(gameObject))
			{
				continue;
			}
			int solid = CountSolidRenderers(gameObject);
			int meshOnly = CountMeshFilters(gameObject);
			int body = CountWalkerBodyRenderers(gameObject);
			num += Math.Min(20, solid * 3 + meshOnly * 2 + body * 2);
			if (gameObject.GetComponentInChildren<SkinnedMeshRenderer>(includeInactive: true) != null)
			{
				num += 15;
			}
			if (gameObject.GetComponentInChildren<SpriteRenderer>(includeInactive: true) != null)
			{
				num += 10;
			}
			if (gameObject.GetComponentInChildren<CameraSwapTrigger>(includeInactive: true) != null)
			{
				num -= 20;
			}
			if (gameObject.transform.parent == null)
			{
				num += 5;
			}
			if (num > bestScore)
			{
				bestScore = num;
				best = gameObject;
			}
		}
		if (best == null)
		{
			Plugin.DBG("SPAWN", LogLabel + ": no Gravy body among " + array.Length + " GameObject asset(s)");
			return;
		}
		CacheVisualFromGameObject(best, "asset:'" + NameRemap.StripCloneSuffix(best.name) + "'");
	}

	private static void FitVisualToEnemy(GameObject enemy, GameObject visual, float scaleMul = 1f)
	{
		Bounds enemyBounds = CombinedRendererBounds(enemy, includeOwnedVisual: false);
		Bounds visualBounds = CombinedRendererBounds(visual, includeOwnedVisual: true);
		visual.transform.localPosition = Vector3.zero;
		visual.transform.localRotation = Quaternion.identity;
		visual.transform.localScale = Vector3.one;
		if (enemyBounds.size.y > 0.01f && visualBounds.size.y > 0.01f)
		{
			float num = enemyBounds.size.y / visualBounds.size.y;
			num = Mathf.Clamp(num, 0.35f, 3.5f) * Mathf.Clamp(scaleMul, 0.1f, 3f);
			visual.transform.localScale = Vector3.one * num;
			visualBounds = CombinedRendererBounds(visual, includeOwnedVisual: true);
		}
		else if (!Mathf.Approximately(scaleMul, 1f))
		{
			visual.transform.localScale = Vector3.one * Mathf.Clamp(scaleMul, 0.1f, 3f);
			visualBounds = CombinedRendererBounds(visual, includeOwnedVisual: true);
		}
		// Align horizontally to enemy center, then plant feet on the enemy ground line.
		Vector3 delta = enemyBounds.center - visualBounds.center;
		delta.y = enemyBounds.min.y - visualBounds.min.y;
		visual.transform.position += delta;
	}

	private static void SnapVisualToGround(GameObject visual, float groundY)
	{
		if (visual == null)
		{
			return;
		}
		Bounds visualBounds = CombinedRendererBounds(visual, includeOwnedVisual: true);
		float dy = groundY - visualBounds.min.y;
		if (Mathf.Abs(dy) > 0.0001f)
		{
			visual.transform.position += new Vector3(0f, dy, 0f);
		}
	}

	private static Bounds CombinedRendererBounds(GameObject root, bool includeOwnedVisual)
	{
		Renderer[] componentsInChildren = root.GetComponentsInChildren<Renderer>(includeInactive: true);
		bool flag = false;
		Bounds result = default(Bounds);
		for (int i = 0; i < componentsInChildren.Length; i++)
		{
			Renderer renderer = componentsInChildren[i];
			if (renderer == null)
			{
				continue;
			}
			bool owned = renderer.GetComponentInParent<GravyVisual>() != null;
			if (includeOwnedVisual != owned)
			{
				continue;
			}
			if (!flag)
			{
				result = renderer.bounds;
				flag = true;
			}
			else
			{
				result.Encapsulate(renderer.bounds);
			}
		}
		if (!flag)
		{
			return new Bounds(root.transform.position, Vector3.one);
		}
		return result;
	}

	private static void StripInteractJunk(GameObject root)
	{
		CameraSwapTrigger[] componentsInChildren = root.GetComponentsInChildren<CameraSwapTrigger>(includeInactive: true);
		for (int i = 0; i < componentsInChildren.Length; i++)
		{
			if (componentsInChildren[i] != null)
			{
				UnityEngine.Object.Destroy(componentsInChildren[i]);
			}
		}
		Camera[] componentsInChildren2 = root.GetComponentsInChildren<Camera>(includeInactive: true);
		for (int j = 0; j < componentsInChildren2.Length; j++)
		{
			if (componentsInChildren2[j] != null)
			{
				componentsInChildren2[j].enabled = false;
			}
		}
		AudioListener[] componentsInChildren3 = root.GetComponentsInChildren<AudioListener>(includeInactive: true);
		for (int k = 0; k < componentsInChildren3.Length; k++)
		{
			if (componentsInChildren3[k] != null)
			{
				UnityEngine.Object.Destroy(componentsInChildren3[k]);
			}
		}
	}

	private static void StripBaseRenderers(GameObject enemy)
	{
		Renderer[] componentsInChildren = enemy.GetComponentsInChildren<Renderer>(includeInactive: true);
		int num = 0;
		for (int i = 0; i < componentsInChildren.Length; i++)
		{
			Renderer renderer = componentsInChildren[i];
			if (!(renderer == null) && !(renderer.GetComponentInParent<GravyVisual>() != null) && renderer.enabled)
			{
				renderer.enabled = false;
				num++;
			}
		}
		Plugin.DBG("SPAWN", LogLabel + ": hid " + num + " base serpent renderer(s)");
	}

	private static void EnemyGallery_OpenGallery_CacheGravyModels(EnemyGalleryUI __instance)
	{
 if(!OverkeksMode.Active) return;
		CacheModelsFromUi(__instance);
		if ((UnityEngine.Object)(object)Plugin.Instance != null)
		{
			OverkeksMode.StartCoroutine(CacheModelsNextFrames(__instance));
		}
	}

	private static void EnemyGallery_ViewGrab_CacheGravyModels(EnemyGalleryUI __instance)
	{
 if(!OverkeksMode.Active) return;
		CacheModelsFromUi(__instance);
	}

	private static void MainMenu_Start_CacheGravyModels(MainMenuManager __instance)
	{
 if(!OverkeksMode.Active) return;
		if (__instance == null)
		{
			return;
		}
		EnemyGalleryUI ui = Traverse.Create((object)__instance).Field("enemyGalleryUI").GetValue<EnemyGalleryUI>();
		if (ui != null)
		{
			Plugin.DBG("SPAWN", LogLabel + ": caching gallery models from MainMenuManager");
			CacheModelsFromUi(ui);
			if ((UnityEngine.Object)(object)Plugin.Instance != null)
			{
				OverkeksMode.StartCoroutine(WarmGalleryModelsFromMenu(__instance, ui));
			}
		}
		else
		{
			Plugin.DBG("SPAWN", LogLabel + ": MainMenuManager.enemyGalleryUI is null");
		}
	}

	private static IEnumerator CacheModelsNextFrames(EnemyGalleryUI ui)
	{
		yield return null;
		yield return null;
		CacheModelsFromUi(ui);
	}

	private static IEnumerator WarmGalleryModelsFromMenu(MainMenuManager menu, EnemyGalleryUI ui)
	{
		yield return null;
		if (menu != null)
		{
			try
			{
				menu.OpenEnemyGallery();
			}
			catch
			{
			}
		}
		yield return null;
		yield return null;
		CacheModelsFromUi(ui);
		if (ui != null)
		{
			try
			{
				Traverse.Create((object)ui).Method("OnViewGrabSceneClicked", Array.Empty<object>()).GetValue();
			}
			catch
			{
			}
		}
		yield return null;
		CacheModelsFromUi(ui);
		if (menu != null)
		{
			try
			{
				menu.CloseEnemyGallery();
			}
			catch
			{
			}
		}
		Plugin.DBG("SPAWN", LogLabel + ": warm-cache done grabTemplate=" + (_cachedGrabModelTemplate != null) + " visualTemplate=" + (_cachedVisualTemplate != null));
	}

	[HarmonyPatch(typeof(GrabScreen), "StartGrab")]
	[HarmonyPostfix]
	[HarmonyPriority(200)]
	private static void StartGrab_ForceGravyVariant(GrabScreen __instance, GameObject enemy)
	{
 if(!OverkeksMode.Active) return;
		if (__instance == null || enemy == null || !__instance.IsGrabbed)
		{
			return;
		}
		GravySerpentMarker component = enemy.GetComponent<GravySerpentMarker>();
		if (component == null)
		{
			return;
		}
		_lastGrabEnemy = enemy;
		FieldInfo field = typeof(GrabScreen).GetField("grabScreenAnimator", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		Animator animator = field?.GetValue(__instance) as Animator;
		if (animator == null)
		{
			return;
		}
		// Native root-Image host replaces live-gallery cloning.
		InjectGalleryGrabModel(__instance, animator, component);
		if (!animator.isActiveAndEnabled)
		{
			animator.enabled = true;
		}
		// Overlay drive owns the visible frames. Blind host Play("Start"/"Start2") whites GrabScreen
		// after a real Gallery_Gravy_* clip already started — only nudge host when clip is idle/wrong.
		string nowClip = GetCurrentClipName(animator);
		if (LooksLikeGravyGrabClip(nowClip) && !IsShortGalleryStub(nowClip) && !IsIdleClip(nowClip))
		{
			Plugin.DBG("GRAB-START", LogLabel + " keep clip '" + nowClip + "' (" + component.VariantLabel + ")");
			return;
		}
		string play = FindClipName(animator.runtimeAnimatorController ?? _cachedGrabController, component.IsSet2, wantStart: true)
			?? component.GrabStartState
			?? (component.IsSet2 ? "Gravy_Grabbed_Riding_Start" : "Gravy_Grabbed_BJ_Start");
		if (IsShortGalleryStub(play))
		{
			Plugin.DBG("GRAB-START", LogLabel + " skip stub host play '" + play + "' nowClip='" + nowClip + "'");
			return;
		}
		try
		{
			if (_cachedGrabController != null && LooksLikeGrabController(_cachedGrabController))
			{
				animator.runtimeAnimatorController = _cachedGrabController;
			}
			animator.Play(play, 0, 0f);
			animator.Update(0f);
		}
		catch
		{
		}
		Plugin.DBG("GRAB-START", LogLabel + " host forced '" + play + "' (" + component.VariantLabel + ") nowClip='" + GetCurrentClipName(animator) + "'");
	}

	/// <summary>
	/// Gallery_Gravy_Grabbed animates UI.Image.sprite (not the idle world SpriteRenderer).
	/// Host the gallery grab UI hierarchy on our overlay canvas and Play the grab clip there.
	/// </summary>
	private static void InjectGalleryGrabModel(GrabScreen grabScreen, Animator grabScreenAnimator, GravySerpentMarker marker)
	{

		if (grabScreenAnimator == null)
		{
			return;
		}
		HideVanillaGrabBackdrop(grabScreen);
		HideVanillaGrabHoleChrome(grabScreen);
		if ((UnityEngine.Object)(object)Plugin.Instance != null)
		{
			OverkeksMode.StartCoroutine(KeepHoleChromeHiddenNextFrames(grabScreen));
		}
		// Native root-Image host replaces live-gallery cloning.
		RuntimeAnimatorController grabCtrl = _cachedGrabController;
		if (grabCtrl == null || !LooksLikeGrabController(grabCtrl))
		{
			grabCtrl = grabScreenAnimator.runtimeAnimatorController ?? _cachedGrabController;
		}
		if (grabCtrl != null && LooksLikeGrabController(grabCtrl) && grabScreenAnimator.runtimeAnimatorController != grabCtrl)
		{
			grabScreenAnimator.runtimeAnimatorController = grabCtrl;
		}
		// Re-roll 50/50 on every grab so Start2 is not stuck from a unlucky spawn.
		bool set2 = RollSet2Variant(out _);
		ApplyVariantToMarker(grabScreen.GrabbingEnemy != null ? grabScreen.GrabbingEnemy : marker.gameObject, set2, out _, out string reDetail);
		Plugin.DBG("GRAB-START", LogLabel + " grab variant re-roll: " + reDetail);
        var captureActor=grabScreen.GrabbingEnemy!=null?grabScreen.GrabbingEnemy:marker.gameObject;
        var variantAi=EnemyAiTypes.First(captureActor);
        var selectedMedia=OptionalMembers.Read(variantAi,"grabScreenAnimations") as GrabAnimationData[];
        if(!CaptureSessionMedia.Bind(grabScreen,captureActor,selectedMedia)) {
            Plugin.Log?.LogWarning("[CAPTURE-MEDIA] Selected variant could not bind to the active session; releasing safely.");
            CreatorVisualHost.ReleaseInvalidScene(grabScreen);return;
        }
		marker = grabScreen.GrabbingEnemy != null
			? grabScreen.GrabbingEnemy.GetComponent<GravySerpentMarker>()
			: marker;
		set2 = marker != null && marker.IsSet2;
		// Gallery_Gravy_Grabbed uses short Animator states Start/Loop/Cum (and *2). Long clip
		// asset names white out / stall the driver — keep them for logs only.
		string play = set2 ? "Start2" : "Start";
		string loop = set2 ? "Loop2" : "Loop";
		string cum = set2 ? "Cum2" : "Cum";
		string ediStart = set2 ? "Gravy_Start2" : "Gravy_Start";
		string ediLoop = set2 ? "Gravy_Loop2" : "Gravy_Loop";
		string ediCum = set2 ? "Gravy_Cum2" : "Gravy_Cum";
		if (marker != null)
		{
			ediStart = FirstNonEmpty(marker.EdiStart, ediStart);
			ediLoop = FirstNonEmpty(marker.EdiLoop, ediLoop);
			ediCum = FirstNonEmpty(marker.EdiCum, ediCum);
			if (!string.IsNullOrEmpty(marker.GrabStartState) && !IsShortGalleryStub(marker.GrabStartState)
				&& marker.GrabStartState.IndexOf("Gallery_", StringComparison.OrdinalIgnoreCase) < 0
				&& marker.GrabStartState.Length < 24)
			{
				play = marker.GrabStartState;
			}
			if (!string.IsNullOrEmpty(marker.GrabLoopState) && !IsShortGalleryStub(marker.GrabLoopState)
				&& marker.GrabLoopState.IndexOf("Gallery_", StringComparison.OrdinalIgnoreCase) < 0
				&& marker.GrabLoopState.Length < 24)
			{
				loop = marker.GrabLoopState;
			}
			if (!string.IsNullOrEmpty(marker.GrabCumState) && !IsShortGalleryStub(marker.GrabCumState)
				&& marker.GrabCumState.IndexOf("Gallery_", StringComparison.OrdinalIgnoreCase) < 0
				&& marker.GrabCumState.Length < 24)
			{
				cum = marker.GrabCumState;
			}
		}
		if (_liveGrabModel != null)
		{
			UnityEngine.Object.Destroy(_liveGrabModel);
			_liveGrabModel = null;
		}

        var overlay=BaphometGrabOverlay.Ensure();overlay.ClearContentBinding();
        if(!CreatorVisualHost.TryBuild(grabCtrl,play,overlay.transform,false,out var live,out var content,out var drive)) {CreatorVisualHost.ReleaseInvalidScene(grabScreen);return;}
        live.name="GravyGrabModel";live.transform.SetAsLastSibling();
        var driver=OverkeksMode.AddOwnedComponent<GravyGrabDriver>(live);
        driver.Begin(drive,grabScreenAnimator,play,loop,cum,ediStart,ediLoop,ediCum);
        drive.Update(0f);
        if(!CreatorVisualHost.Valid(content)){live.SetActive(false);UnityEngine.Object.Destroy(live);CreatorVisualHost.ReleaseInvalidScene(grabScreen);return;}
        overlay.ShowWorldBackdrop();
        Plugin.DBG("CREATOR-VISUAL","background=existing-camera; sceneOrder="+CreatorVisualPolicy.SceneSortingOrder+"; escapeHintOrder="+CreatorVisualPolicy.EscapeHintSortingOrder);
        Plugin.DBG("CREATOR-VISUAL","Gravy scene verified root-Image state="+play+" sprite="+content.sprite.name);
        _liveGrabModel=live;
 
	}

	/// <summary>
	/// Kill solid black panel Images from the gallery grab chrome (not the animated sheet).
	/// </summary>
	private static void DisableSolidBlackFillImages(GameObject root)
	{
		if (root == null)
		{
			return;
		}
		Image[] images = root.GetComponentsInChildren<Image>(includeInactive: true);
		for (int i = 0; i < images.Length; i++)
		{
			Image image = images[i];
			if (image == null || IsAnimatedSheetSprite(image.sprite))
			{
				continue;
			}
			if (IsUiChromeImage(image) || LooksLikeHoleChrome((image.gameObject.name ?? "").ToLowerInvariant(), image))
			{
				image.enabled = false;
				image.gameObject.SetActive(value: false);
				continue;
			}
			// Empty Image with dark/opaque color = solid fill panel.
			if (image.sprite == null)
			{
				Color c = image.color;
				if (c.a > 0.05f && c.r < 0.15f && c.g < 0.15f && c.b < 0.15f)
				{
					image.enabled = false;
					image.gameObject.SetActive(value: false);
				}
			}
		}
	}

	private static void StripAllTextLabels(GameObject root)
	{
		if (root == null)
		{
			return;
		}
		Text[] uiTexts = root.GetComponentsInChildren<Text>(includeInactive: true);
		for (int i = 0; i < uiTexts.Length; i++)
		{
			if (uiTexts[i] != null)
			{
				uiTexts[i].text = "";
				uiTexts[i].enabled = false;
				uiTexts[i].gameObject.SetActive(value: false);
			}
		}
		// TMPro without hard reference - gallery grabAnimNameText lives here ("name" bottom-right).
		Component[] components = root.GetComponentsInChildren<Component>(includeInactive: true);
		for (int i = 0; i < components.Length; i++)
		{
			Component component = components[i];
			if (component == null)
			{
				continue;
			}
			string typeName = component.GetType().Name;
			if (typeName != "TextMeshProUGUI" && typeName != "TextMeshPro" && typeName != "TMP_Text")
			{
				continue;
			}
			try
			{
				Traverse.Create((object)component).Property("text").SetValue((object)"");
			}
			catch
			{
			}
			if (component is Behaviour behaviour)
			{
				behaviour.enabled = false;
			}
			component.gameObject.SetActive(value: false);
		}
	}

	private static void DisableUiChromeImages(GameObject root)
	{
		if (root == null)
		{
			return;
		}
		Image[] images = root.GetComponentsInChildren<Image>(includeInactive: true);
		for (int i = 0; i < images.Length; i++)
		{
			Image image = images[i];
			if (image == null)
			{
				continue;
			}
			if (IsUiChromeImage(image))
			{
				image.enabled = false;
				image.gameObject.SetActive(value: false);
			}
		}
	}

	private static bool IsUiChromeImage(Image image)
	{
		string name = (image.gameObject.name ?? "").ToLowerInvariant();
		string sprite = image.sprite != null ? image.sprite.name.ToLowerInvariant() : "";
		return name.Contains("arrow") || name.Contains("button") || name.Contains("btn")
			|| name.Contains("logo") || name.Contains("icon") || name.Contains("close")
			|| name.Contains("next") || name.Contains("prev") || name.Contains("title")
			|| sprite.Contains("arrow") || sprite.Contains("logo") || sprite.Contains("button")
			|| sprite.Contains("icon") || sprite.Contains("ui_");
	}

	private static bool IsAnimatedSheetSprite(Sprite sprite)
	{
		if (sprite == null)
		{
			return false;
		}
		string name = sprite.name.ToLowerInvariant();
		if (name.Contains("arrow") || name.Contains("logo") || name.Contains("button") || name.Contains("icon"))
		{
			return false;
		}
		return name.Contains("sheet") || name.Contains("blowjob") || name.Contains("riding")
			|| name.Contains("gravy") || name.Contains("grab") || name.Contains("cum")
			|| name.Contains("start") || name.Contains("loop") || sprite.rect.width >= 128f;
	}

	private static Image FindAnimatedGrabContentImage(GameObject root)
	{
		if (root == null)
		{
			return null;
		}
		Image[] images = root.GetComponentsInChildren<Image>(includeInactive: true);
		Image bestSheet = null;
		float bestSheetArea = 0f;
		Image bestNamed = null;
		Image bestAny = null;
		float bestAnyArea = 0f;
		for (int i = 0; i < images.Length; i++)
		{
			Image image = images[i];
			if (image == null || IsUiChromeImage(image))
			{
				continue;
			}
			string goName = image.gameObject.name ?? "";
			if (goName.Equals("Image", StringComparison.OrdinalIgnoreCase))
			{
				bestNamed = image;
			}
			float area = image.sprite != null ? image.sprite.rect.width * image.sprite.rect.height : 0f;
			if (IsAnimatedSheetSprite(image.sprite) && area >= bestSheetArea)
			{
				bestSheetArea = area;
				bestSheet = image;
			}
			if (area >= bestAnyArea)
			{
				bestAnyArea = area;
				bestAny = image;
			}
		}
		return bestSheet ?? bestNamed ?? bestAny;
	}

	private static void PrepareExistingContentFullscreen(Image content)
	{
		if (content == null)
		{
			return;
		}
		content.enabled = true;
		content.gameObject.SetActive(value: true);
		content.preserveAspect = false;
		content.raycastTarget = false;
		StretchFull(content.rectTransform);
		// Also stretch parents so Mask/layout cannot keep a circular viewport.
		Transform t = content.transform;
		while (t != null)
		{
			if (t is RectTransform rect && t.GetComponent<Canvas>() == null)
			{
				StretchFull(rect);
			}
			if (t.name == "GravyGrabModel" || t.name.StartsWith("BaphometGrab", StringComparison.Ordinal))
			{
				break;
			}
			t = t.parent;
		}
		content.material = null;
		content.color = Color.white;
		BaphometGrabBrightener existing = content.GetComponent<BaphometGrabBrightener>();
		if (existing != null)
		{
			UnityEngine.Object.Destroy(existing);
		}
	}

	/// <summary>
	/// One fullscreen Image named "Image" (matches Gallery_Gravy_Grabbed sprite bindings), normal brightness, no hole mask.
	/// </summary>
	private static Image PrepareFullscreenBrightImage(GameObject root)
	{
		if (root == null)
		{
			return null;
		}
		Image image = root.GetComponentInChildren<Image>(includeInactive: true);
		if (image == null)
		{
			GameObject imageGo = OverkeksMode.NewOwned("Image", typeof(RectTransform));
			imageGo.transform.SetParent(root.transform, worldPositionStays: false);
			image = OverkeksMode.AddOwnedComponent<Image>(imageGo);
		}
		image.gameObject.name = "Image";
		image.enabled = true;
		image.gameObject.SetActive(value: true);
		image.preserveAspect = false;
		image.raycastTarget = false;
		StretchFull(image.rectTransform);
		image.material = null;
		image.color = Color.white;
		BaphometGrabBrightener existing = image.GetComponent<BaphometGrabBrightener>();
		if (existing != null)
		{
			UnityEngine.Object.Destroy(existing);
		}
		return image;
	}

	/// <summary>
	/// Gallery grab template / GrabScreen often ship a circular glory-hole mask.
	/// Strip masks/frames and keep only the animated content Image (hidden; overlay blits it fullscreen+bright).
	/// </summary>
	private static void StripHolePresentation(GameObject root)
	{
		if (root == null)
		{
			return;
		}
		Mask[] masks = root.GetComponentsInChildren<Mask>(includeInactive: true);
		for (int i = 0; i < masks.Length; i++)
		{
			if (masks[i] != null)
			{
				masks[i].enabled = false;
				UnityEngine.Object.Destroy(masks[i]);
			}
		}
		RectMask2D[] rectMasks = root.GetComponentsInChildren<RectMask2D>(includeInactive: true);
		for (int i = 0; i < rectMasks.Length; i++)
		{
			if (rectMasks[i] != null)
			{
				rectMasks[i].enabled = false;
				UnityEngine.Object.Destroy(rectMasks[i]);
			}
		}
		Image[] images = root.GetComponentsInChildren<Image>(includeInactive: true);
		for (int i = 0; i < images.Length; i++)
		{
			Image image = images[i];
			if (image == null)
			{
				continue;
			}
			string name = image.gameObject.name ?? "";
			string lower = name.ToLowerInvariant();
			if (LooksLikeHoleChrome(lower, image))
			{
				image.enabled = false;
				image.gameObject.SetActive(value: false);
			}
		}
	}

	private static bool LooksLikeHoleChrome(string lowerName, Image image)
	{
		if (lowerName.Contains("mask") || lowerName.Contains("hole") || lowerName.Contains("circle")
			|| lowerName.Contains("frame") || lowerName.Contains("border") || lowerName.Contains("vignette")
			|| lowerName.Contains("ring") || lowerName.Contains("glory") || lowerName.Contains("cutout")
			|| lowerName.Contains("stencil") || lowerName.Contains("backdrop") || lowerName.Contains("bg"))
		{
			return true;
		}
		if (image.sprite != null)
		{
			string sprite = image.sprite.name.ToLowerInvariant();
			if (sprite.Contains("mask") || sprite.Contains("hole") || sprite.Contains("circle")
				|| sprite.Contains("frame") || sprite.Contains("vignette") || sprite.Contains("glory"))
			{
				return true;
			}
		}
		return false;
	}

	/// <summary>
	/// Keep the gallery Image hierarchy for the Animator to write sprites into,
	/// but hide it - BaphometGrabOverlay blits the sprite fullscreen without hole chrome.
	/// </summary>
	private static void MakeContentDriveHiddenFullscreen(GameObject root)
	{
		if (root == null)
		{
			return;
		}
		CanvasGroup group = root.GetComponent<CanvasGroup>() ?? OverkeksMode.AddOwnedComponent<CanvasGroup>(root);
		group.alpha = 0f;
		group.blocksRaycasts = false;
		group.interactable = false;
		StretchFull(root.transform as RectTransform);
		Image content = FindBestUiImage(root);
		if (content != null)
		{
			StretchFull(content.rectTransform);
			content.preserveAspect = false;
			content.raycastTarget = false;
		}
	}

	private static IEnumerator KeepHoleChromeHiddenNextFrames(GrabScreen grabScreen)
	{
		for (int i = 0; i < 3; i++)
		{
			yield return null;
			if (grabScreen == null || !grabScreen.IsGrabbed)
			{
				yield break;
			}
			HideVanillaGrabHoleChrome(grabScreen);
		}
	}

	private static void HideVanillaGrabHoleChrome(GrabScreen grabScreen)
	{
		if (grabScreen == null)
		{
			return;
		}
		Traverse t = Traverse.Create((object)grabScreen);
		HideLayer(t, "grabImage");
		HideLayer(t, "grabOverlay");
		// Keep grabUI for struggle button / heat, but kill circular image children.
		GameObject grabUi = t.Field("grabUI").GetValue<GameObject>();
		if (grabUi == null)
		{
			return;
		}
		Mask[] masks = grabUi.GetComponentsInChildren<Mask>(includeInactive: true);
		for (int i = 0; i < masks.Length; i++)
		{
			if (masks[i] != null)
			{
				masks[i].enabled = false;
			}
		}
		RectMask2D[] rectMasks = grabUi.GetComponentsInChildren<RectMask2D>(includeInactive: true);
		for (int i = 0; i < rectMasks.Length; i++)
		{
			if (rectMasks[i] != null)
			{
				rectMasks[i].enabled = false;
			}
		}
	}

	private static void HideLayer(Traverse traverse, string fieldName)
	{
		GameObject layer = traverse.Field(fieldName).GetValue<GameObject>();
		if (layer != null)
		{
			layer.SetActive(value: false);
		}
	}

	private static string FirstNonEmpty(params string[] values)
	{
		if (values == null)
		{
			return null;
		}
		for (int i = 0; i < values.Length; i++)
		{
			if (!string.IsNullOrEmpty(values[i]))
			{
				return values[i];
			}
		}
		return null;
	}

	private static GameObject BuildSyntheticGrabUi(Transform parent, RuntimeAnimatorController grabCtrl)
	{
		GameObject root = OverkeksMode.NewOwned("BaphometGrabSynthetic", typeof(RectTransform));
		root.transform.SetParent(parent, worldPositionStays: false);
		StretchFull(root.GetComponent<RectTransform>());
		root.transform.SetAsLastSibling();
		Animator animator = OverkeksMode.AddOwnedComponent<Animator>(root);
		animator.runtimeAnimatorController = grabCtrl;
		// Gallery grab clips bind UI.Image.sprite on a child named "Image".
		GameObject imageGo = OverkeksMode.NewOwned("Image", typeof(RectTransform));
		imageGo.transform.SetParent(root.transform, worldPositionStays: false);
		StretchFull(imageGo.GetComponent<RectTransform>());
		Image image = OverkeksMode.AddOwnedComponent<Image>(imageGo);
		image.color = Color.white;
		image.material = null;
		image.preserveAspect = false;
		image.raycastTarget = false;
		return root;
	}

	private static void StretchFull(RectTransform rect)
	{
		if (rect == null)
		{
			return;
		}
		rect.anchorMin = Vector2.zero;
		rect.anchorMax = Vector2.one;
		rect.offsetMin = Vector2.zero;
		rect.offsetMax = Vector2.zero;
		rect.localScale = Vector3.one;
		rect.localRotation = Quaternion.identity;
	}

	private static Image FindBestUiImage(GameObject root)
	{
		if (root == null)
		{
			return null;
		}
		Image best = null;
		Image[] images = root.GetComponentsInChildren<Image>(includeInactive: true);
		for (int i = 0; i < images.Length; i++)
		{
			Image image = images[i];
			if (image == null)
			{
				continue;
			}
			if (best == null || (image.sprite != null && (best.sprite == null || image.sprite.rect.size.sqrMagnitude > best.sprite.rect.size.sqrMagnitude)))
			{
				best = image;
			}
		}
		return best;
	}

	private static void HideVanillaGrabBackdrop(GrabScreen grabScreen)
	{
		if (grabScreen == null)
		{
			return;
		}
		Traverse t = Traverse.Create((object)grabScreen);
		GameObject grabOverlay = t.Field("grabOverlay").GetValue<GameObject>();
		if (grabOverlay != null)
		{
			Image overlayImage = grabOverlay.GetComponent<Image>();
			if (overlayImage != null)
			{
				Color color = overlayImage.color;
				color.a = 0f;
				overlayImage.color = color;
			}
		}
		GameObject grabImage = t.Field("grabImage").GetValue<GameObject>();
		if (grabImage == null)
		{
			return;
		}
		// Keep grabImage active for layout, but clear solid white fills so our overlay is visible.
		Image[] images = grabImage.GetComponentsInChildren<Image>(includeInactive: true);
		for (int i = 0; i < images.Length; i++)
		{
			if (images[i] == null)
			{
				continue;
			}
			if (images[i].sprite == null)
			{
				Color color = images[i].color;
				color.a = 0f;
				images[i].color = color;
			}
		}
	}

	private static SpriteRenderer FindBestSpriteRenderer(GameObject root)
	{
		if (root == null)
		{
			return null;
		}
		SpriteRenderer best = null;
		float bestArea = 0f;
		SpriteRenderer[] array = root.GetComponentsInChildren<SpriteRenderer>(includeInactive: true);
		for (int i = 0; i < array.Length; i++)
		{
			SpriteRenderer spriteRenderer = array[i];
			if (spriteRenderer == null)
			{
				continue;
			}
			spriteRenderer.enabled = true;
			spriteRenderer.gameObject.SetActive(value: true);
			float area = spriteRenderer.bounds.size.x * spriteRenderer.bounds.size.y;
			if (spriteRenderer.sprite != null)
			{
				area += 100f;
			}
			if (area >= bestArea)
			{
				bestArea = area;
				best = spriteRenderer;
			}
		}
		return best;
	}

	private static Image ResolveGrabUiImage(GrabScreen grabScreen)
	{
		if (grabScreen == null)
		{
			return null;
		}
		Traverse t = Traverse.Create((object)grabScreen);
		GameObject grabImage = t.Field("grabImage").GetValue<GameObject>();
		if (grabImage != null)
		{
			grabImage.SetActive(value: true);
			Image image = grabImage.GetComponent<Image>() ?? grabImage.GetComponentInChildren<Image>(includeInactive: true);
			if (image != null)
			{
				return image;
			}
		}
		GameObject grabUI = t.Field("grabUI").GetValue<GameObject>();
		if (grabUI != null)
		{
			return grabUI.GetComponentInChildren<Image>(includeInactive: true);
		}
		return null;
	}

	/// <summary>Find any loaded Animator already wired to Gallery_Gravy_Grabbed that has UI Images (gallery grab avatar).</summary>
	private static void TryCacheGrabModelFromControllerAssets()
	{
		if (_cachedGrabModelTemplate != null && CountUiImages(_cachedGrabModelTemplate) > 0)
		{
			return;
		}
		if (_cachedGrabController == null)
		{
			return;
		}
		Animator best = null;
		int bestScore = 0;
		Animator[] array = Resources.FindObjectsOfTypeAll<Animator>();
		for (int i = 0; i < array.Length; i++)
		{
			Animator animator = array[i];
			if (animator == null || animator.runtimeAnimatorController == null)
			{
				continue;
			}
			string text = animator.runtimeAnimatorController.name ?? "";
			if (text.IndexOf("Gravy_Grabbed", StringComparison.OrdinalIgnoreCase) < 0 && text.IndexOf("Gallery_Gravy_Grabbed", StringComparison.OrdinalIgnoreCase) < 0)
			{
				continue;
			}
			int imgs = CountUiImages(animator.gameObject);
			int any = CountAnyRenderers(animator.gameObject);
			if (imgs == 0 && any == 0)
			{
				continue;
			}
			int num = imgs * 20 + any * 5;
			if (num > bestScore)
			{
				bestScore = num;
				best = animator;
			}
		}
		if (best == null)
		{
			Plugin.DBG("SPAWN", LogLabel + ": no Animator with Gallery_Gravy_Grabbed + UI among " + array.Length);
			return;
		}
		CacheGrabUiTemplate(best.gameObject, "animator:'" + NameRemap.StripCloneSuffix(best.gameObject.name) + "' ctrl='" + best.runtimeAnimatorController.name + "'");
	}

	private static string[] BuildPlayCandidates(RuntimeAnimatorController controller, GravySerpentMarker marker, bool riding)
	{
		List<string> list = new List<string>();
		void Add(string s)
		{
			if (!string.IsNullOrEmpty(s) && !list.Contains(s))
			{
				list.Add(s);
			}
		}
		Add(FindClipName(controller, riding, wantStart: true));
		Add(marker != null ? marker.GrabStartState : null);
		if (marker != null && marker.GrabStateNames != null)
		{
			for (int i = 0; i < marker.GrabStateNames.Length; i++)
			{
				Add(marker.GrabStateNames[i]);
			}
		}
		Add(riding ? "Gallery_Gravy_Grabbed_Riding_Start" : "Gallery_Gravy_Grabbed_BJ_Start");
		Add(riding ? "Gravy_Grabbed_Riding_Start" : "Gravy_Grabbed_BJ_Start");
		Add(riding ? "Gravy_Grabbed_Start2" : "Gravy_Grabbed_Start");
		if (controller != null && controller.animationClips != null)
		{
			for (int j = 0; j < controller.animationClips.Length; j++)
			{
				AnimationClip animationClip = controller.animationClips[j];
				if (animationClip != null && IsMatchingVariantClip(animationClip.name, riding))
				{
					Add(animationClip.name);
				}
			}
		}
		return list.ToArray();
	}

	private static bool IsIdleClip(string clipName)
	{
		if (string.IsNullOrEmpty(clipName))
		{
			return true;
		}
		string text = clipName.ToLowerInvariant();
		return text == "idle" || text.EndsWith("_idle") || text.Contains("idle");
	}

	[HarmonyPatch(typeof(GrabScreen), "EndGrab")]
	[HarmonyPostfix]
	[HarmonyPriority(50)]
	private static void EndGrab_CleanupGravyGrabModel(GrabScreen __instance)
	{
 if(!OverkeksMode.HooksAllowed) return;
		GameObject enemy = __instance != null ? __instance.GrabbingEnemy : null;
		if (enemy == null)
		{
			enemy = _lastGrabEnemy;
		}
		if (_liveGrabModel != null)
		{
			UnityEngine.Object.Destroy(_liveGrabModel);
			_liveGrabModel = null;
			Plugin.DBG("GRAB-START", LogLabel + " destroyed injected grabDisplayModel");
		}
		BaphometGrabOverlay.Hide();
		if (__instance != null)
		{
			BaphometGrabUiBridge[] bridges = __instance.GetComponentsInChildren<BaphometGrabUiBridge>(includeInactive: true);
			for (int i = 0; i < bridges.Length; i++)
			{
				if (bridges[i] != null)
				{
					UnityEngine.Object.Destroy(bridges[i]);
				}
			}
			// Gravy zeros grabImage alphas — restore so the next vanilla H-scene is visible.
			GrabViewHelper.RestoreVanillaGrabPresentation(__instance);
		}
		ReskinBaseStrip.Enforce(enemy);
		_lastGrabEnemy = null;
	}

	private static bool IsRidingVariant(string label)
	{
		if (string.IsNullOrEmpty(label))
		{
			return false;
		}
		return label.IndexOf("riding", StringComparison.OrdinalIgnoreCase) >= 0
			|| label.IndexOf("start2", StringComparison.OrdinalIgnoreCase) >= 0
			|| label.Equals("set2", StringComparison.OrdinalIgnoreCase);
	}

	private static string GetCurrentClipName(Animator animator)
	{
		if (animator == null)
		{
			return null;
		}
		AnimatorClipInfo[] clips = animator.GetCurrentAnimatorClipInfo(0);
		if (clips != null && clips.Length != 0 && clips[0].clip != null)
		{
			return clips[0].clip.name;
		}
		return null;
	}

	private static bool LooksLikeGravyGrabClip(string clipName)
	{
		if (string.IsNullOrEmpty(clipName))
		{
			return false;
		}
		string text = clipName.ToLowerInvariant();
		return text.Contains("gravy") || text.Contains("minotaur") || text.Contains("minothaur") || text.Contains("grabbed");
	}

	private static bool IsShortGalleryStub(string stateOrClip)
	{
		if (string.IsNullOrEmpty(stateOrClip))
		{
			return false;
		}
		string text = stateOrClip.Trim();
		return text.Equals("Start", StringComparison.OrdinalIgnoreCase)
			|| text.Equals("Start2", StringComparison.OrdinalIgnoreCase)
			|| text.Equals("Loop", StringComparison.OrdinalIgnoreCase)
			|| text.Equals("Loop2", StringComparison.OrdinalIgnoreCase)
			|| text.Equals("Cum", StringComparison.OrdinalIgnoreCase)
			|| text.Equals("Cum2", StringComparison.OrdinalIgnoreCase)
			|| text.Equals("Middle", StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsMatchingVariantClip(string clipName, bool riding)
	{
		if (!LooksLikeGravyGrabClip(clipName))
		{
			return false;
		}
		string text = clipName.ToLowerInvariant();
		bool isRiding = text.Contains("riding") || text.Contains("start2") || text.Contains("loop2") || text.Contains("cum2") || text.Contains("sex") || text.Contains("startsex") || text.Contains("sexmiddle");
		bool isBj = text.Contains("bj") || (!isRiding && (text.Contains("start") || text.Contains("loop") || text.Contains("cum") || text.Contains("middle") || text.Contains("grabbed")));
		if (riding)
		{
			return isRiding;
		}
		return isBj && !isRiding;
	}
}

