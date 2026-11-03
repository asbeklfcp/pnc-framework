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
/// Debug: spawn a Gargoyle (ProjectileEnemyAI ranged) reskinned with the Baphomet statue mesh
/// and wired to Baphomet grab animations (random BJ vs Riding each spawn).
/// </summary>
[HarmonyPatch]
internal static class BaphometGargoyleSpawn
{
	private const string LogLabel = "baphomet_gargoyle";

	private static GameObject _cachedVisualTemplate;

	private static GameObject _cachedGrabModelTemplate;

	private static RuntimeAnimatorController _cachedGrabController;

	private static RuntimeAnimatorController _cachedIdleController;

	private static GrabAnimationData[] _cachedGrabAnims;

	private static string _cachedGalleryId;

	private static float _nextSpawnAllowedAt;

	private static GameObject _liveGrabModel;

	private static GameObject _lastGrabEnemy;

	internal static void TickHotkey()
	{
		if (OverkeksConfig.CfgKeySpawnBaphometGargoyle == null)
		{
			return;
		}
		if (!Hotkeys.IsDown(OverkeksConfig.CfgKeySpawnBaphometGargoyle))
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

	internal static void Spawn()
	{
		GameObject template = ResolvePreferredGargoyleTemplate();
		if (template == null)
		{
			Plugin.DBG("SPAWN", LogLabel + ": no usable Gargoyle template (need the native ProjectileEnemyAI Gargoyle, not Gargoyle Alt)");
			return;
		}
		GameObject instance = DebugEnemySpawn.SpawnPrefabInstance(template, LogLabel);
		if (instance == null)
		{
			Plugin.DBG("SPAWN", LogLabel + ": gargoyle spawn failed");
			return;
		}
		if (!TryConvert(instance, out string detail))
		{
			Plugin.DBG("SPAWN", LogLabel + ": convert failed (" + detail + ") — destroying botched spawn");
			UnityEngine.Object.Destroy(instance);
			return;
		}
		OverkeksEnemyRuntime.RefreshConvertedActor(instance, "baphomet");
        BaphometAttackTrace.AttachManual(instance);
		Plugin.DBG("SPAWN", LogLabel + ": ready '" + instance.name + "' from '" + NameRemap.StripCloneSuffix(template.name) + "' " + detail);
	}

	/// <summary>
	/// Prefer exact "Gargoyle" — ProjectileEnemyAI ranged fighter (separate damage and grab projectile paths).
	/// Skip "Gargoyle Alt" only: vanilla wires BlackSerpent GrabScreen on that variant.
	/// </summary>
	private static GameObject ResolvePreferredGargoyleTemplate()
	{
		string hint = OverkeksConfig.CfgSpawnGargoyleNameHint?.Value ?? DebugEnemySpawn.DefaultGargoyleSpawnHint;
		List<GameObject> variants = DebugEnemySpawn.GetSpawnVariants(hint, LogLabel);
		if (variants == null || variants.Count == 0)
		{
			variants = DebugEnemySpawn.ResolveGargoyleVariants(LogLabel);
		}
		if (variants == null || variants.Count == 0)
		{
			Plugin.DBG("SPAWN", LogLabel + ": no gargoyle variants resolved at all");
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
			if (!IsUsableGargoyleBase(gameObject))
			{
				continue;
			}
			if (text.Equals("Gargoyle", StringComparison.OrdinalIgnoreCase))
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
		Plugin.DBG("SPAWN", LogLabel + ": gargoyle template '" + NameRemap.StripCloneSuffix(gameObject2.name) + "' ai=" + SummarizeAi(gameObject2) + " from [" + diag + "]");
		return gameObject2;
	}

	private static bool IsUsableGargoyleBase(GameObject prefab)
	{
		if (prefab == null || prefab.GetComponent<CameraSwapTrigger>() != null || prefab.GetComponent<MimicEnemy>() != null)
		{
			return false;
		}
		string text = NameRemap.StripCloneSuffix(prefab.name);
		// Alt variant: vanilla grab controller is BlackSerpent — do not use as Baphomet base.
		if (text.IndexOf("alt", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			return false;
		}
		if (prefab.GetComponent<ProjectileEnemyAI>() != null || prefab.GetComponent<EnemyAI>() != null || prefab.GetComponent<SpinningEnemyAI>() != null)
		{
			return true;
		}
		return EnemyAiTypes.Any(prefab);
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
		string galleryId = string.IsNullOrEmpty(_cachedGalleryId) ? "baphomet" : _cachedGalleryId;
		// Wire every AI type present — GrabScreen.ResolveGrabAnimations reads EnemyAI first and returns.
		List<string> kinds = new List<string>();
		EnemyAI enemyAI = enemy.GetComponent<EnemyAI>();
		if (enemyAI != null)
		{
			enemyAI.galleryEnemyID = galleryId;
			enemyAI.grabAnimatorController = _cachedGrabController;
			enemyAI.grabScreenAnimations = variantAnims;
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
        if(!CreatorVisualHost.TryWorld("Gallery_Baphomet",out _,out _,_cachedIdleController)) {detail="native gallery idle host unavailable; actor left unchanged";return false;}
        OverkeksMode.Own(enemy);
 
		if (_cachedGrabController == null)
		{
			detail = "no Baphomet grab controller (need gallery entry or glory-hole tile loaded once)";
			return false;
		}
		string controllerName = _cachedGrabController.name ?? "";
		if (controllerName.IndexOf("serpent", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			detail = "refusing grab controller '" + controllerName + "' (serpent leak)";
			_cachedGrabController = null;
			_cachedGrabAnims = null;
			return false;
		}
		// 50/50 at spawn; Grab re-rolls again so each H-scene is independent.
		bool set2 = RollSet2Variant(out float roll);
		ApplyVariantToMarker(enemy, set2, out string aiKind, out string detailCore);
		detail = detailCore + " roll=" + roll.ToString("0.00");
		// Native root-Image host replaces live-gallery cloning.
		BaphometGargoyleMarker marker = enemy.GetComponent<BaphometGargoyleMarker>();
		bool visualOk = TryAttachStatueVisual(enemy);
		if (visualOk)
		{
			StripBaseRenderers(enemy);
			marker?.StripBaseNow();
		}
		else
		{
			Plugin.DBG("SPAWN", LogLabel + ": keeping gargoyle mesh visible (no usable statue visual)");
		}
		detail += " visual=" + (visualOk ? "statue" : "gargoyle-fallback");
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
		GrabAnimationData[] variantAnims = BuildSynthetic(new[] { start, loop, cum });
		if (!TryWireGrabAi(enemy, variantAnims, out aiKind))
		{
			detail = "no EnemyAI/Spinning/Projectile to wire";
			aiKind = null;
			return;
		}
		enemy.name = "Baphomet_Statue";
		BaphometGargoyleMarker marker = enemy.GetComponent<BaphometGargoyleMarker>() ?? OverkeksMode.AddOwnedComponent<BaphometGargoyleMarker>(enemy);
		marker.VariantLabel = set2 ? "start2" : "start";
		marker.IsSet2 = set2;
		marker.GrabStartState = start;
		marker.GrabLoopState = loop;
		marker.GrabCumState = cum;
		marker.EdiStart = set2 ? "Baphomet_Start2" : "Baphomet_Start";
		marker.EdiLoop = set2 ? "Baphomet_Loop2" : "Baphomet_Loop";
		marker.EdiCum = set2 ? "Baphomet_Cum2" : "Baphomet_Cum";
		marker.GrabStateNames = CollectStateNames(variantAnims);
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
		// Clip asset names like Gallery_Baphomet_Grabbed_BJ_Start are not Animator state names.
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
		string start = set2 ? "Baphomet_Grabbed_Riding_Start" : "Baphomet_Grabbed_BJ_Start";
		string loop = set2 ? "Baphomet_Grabbed_Riding_Loop" : "Baphomet_Grabbed_BJ_Loop";
		string cum = set2 ? "Baphomet_Grabbed_Riding_Cum" : "Baphomet_Grabbed_BJ_Cum";
		if (variantAnims != null && variantAnims.Length != 0)
		{
			bool alreadyFriendly = false;
			for (int i = 0; i < variantAnims.Length; i++)
			{
				string text = variantAnims[i]?.animationName ?? "";
				if (text.IndexOf("Baphomet_Grabbed", StringComparison.OrdinalIgnoreCase) >= 0 && text.IndexOf("Gallery_", StringComparison.OrdinalIgnoreCase) < 0)
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
		if (!string.IsNullOrEmpty(stripped) && stripped.IndexOf("Baphomet_Grabbed", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			return stripped;
		}
		return set2 ? "Baphomet_Grabbed_Riding_Start" : "Baphomet_Grabbed_BJ_Start";
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
			if (text.Contains("baphomet") || text.Contains("grabbed") || text.Contains("glory"))
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
				? new[] { "Baphomet_Grabbed_Riding_Start", "Baphomet_Grabbed_Riding_Loop", "Baphomet_Grabbed_Riding_Cum" }
				: new[] { "Baphomet_Grabbed_BJ_Start", "Baphomet_Grabbed_BJ_Loop", "Baphomet_Grabbed_BJ_Cum" };
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
        if(TryLoadFromGallery(out var ctrl,out var anims,out var id) && ctrl!=null && ctrl.name=="Gallery_Baphomet_Grabbed") {
            _cachedGrabController=ctrl;_cachedGrabAnims=anims;_cachedGalleryId=id;return;
        }
        ctrl=CreatorVisualHost.Controller("Gallery_Baphomet_Grabbed");
        if(ctrl!=null){_cachedGrabController=ctrl;_cachedGrabAnims=BuildAnimsFromController(ctrl);_cachedGalleryId="baphomet";}
 
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
			if (runtimeAnimatorController == null || IsSerpentController(runtimeAnimatorController))
			{
				continue;
			}
			if (preferInGame && IsMenuOnlyGalleryController(runtimeAnimatorController))
			{
				continue;
			}
			string text = runtimeAnimatorController.name ?? "";
			int num = 0;
			if (text.IndexOf("baphomet", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				num += 30;
			}
			else if (text.IndexOf("baph", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				num += 15;
			}
			else if (text.IndexOf("gloryhole", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("glory hole", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				num += 25;
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
			// World Idle controller for the statue mesh — not GrabScreen.
			if (text.Equals("Baphomet Statue", StringComparison.OrdinalIgnoreCase) || (text.IndexOf("statue", StringComparison.OrdinalIgnoreCase) >= 0 && text.IndexOf("grab", StringComparison.OrdinalIgnoreCase) < 0))
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
			anims = BuildSynthetic(new[] { "Baphomet_Grabbed_BJ_Start", "Baphomet_Grabbed_BJ_Loop", "Baphomet_Grabbed_BJ_Cum", "Baphomet_Grabbed_Riding_Start", "Baphomet_Grabbed_Riding_Loop", "Baphomet_Grabbed_Riding_Cum" });
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
			int num = ScoreBaphometGallery(enemyGalleryEntry);
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

	private static int ScoreBaphometGallery(EnemyGalleryEntry entry)
	{
		string text = ((entry.enemyID ?? "") + " " + (entry.enemyName ?? "")).ToLowerInvariant();
		string ctrl = (entry.grabAnimatorController != null) ? (entry.grabAnimatorController.name ?? "") : "";
		if (IsSerpentController(entry.grabAnimatorController))
		{
			return 0;
		}
		bool nameHit = text.Contains("baphomet") || text.Contains("baph");
		bool ctrlHit = ctrl.IndexOf("baphomet", StringComparison.OrdinalIgnoreCase) >= 0 || ctrl.IndexOf("baph", StringComparison.OrdinalIgnoreCase) >= 0 || ctrl.IndexOf("glory", StringComparison.OrdinalIgnoreCase) >= 0;
		bool animHit = false;
		if (entry.grabAnimations != null)
		{
			for (int i = 0; i < entry.grabAnimations.Length; i++)
			{
				string text2 = entry.grabAnimations[i]?.animationName ?? "";
				if (text2.IndexOf("baphomet", StringComparison.OrdinalIgnoreCase) >= 0 || text2.IndexOf("gloryhole", StringComparison.OrdinalIgnoreCase) >= 0)
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

	private static CameraSwapTrigger FindBaphometTrigger()
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
			int num2 = ScoreBaphometTrigger(cameraSwapTrigger2);
			if (num2 > num)
			{
				num = num2;
				cameraSwapTrigger = cameraSwapTrigger2;
			}
		}
		return cameraSwapTrigger;
	}

	private static int ScoreBaphometTrigger(CameraSwapTrigger trigger)
	{
		string galleryId = GetGalleryEntryId(trigger);
		string text = (trigger.gameObject.name + " " + (galleryId ?? "")).ToLowerInvariant();
		Animator animator = GetTriggerAnimator(trigger);
		string clipKey = "";
		if (animator != null && animator.runtimeAnimatorController != null)
		{
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
		bool flag = text.Contains("baph") || text.Contains("baphomet") || text.Contains("glory") || clipKey.Contains("baph") || clipKey.Contains("baphomet") || clipKey.Contains("gloryhole");
		if (!flag)
		{
			return 0;
		}
		if (clipKey.Contains("minotaur") || clipKey.Contains("gravy") || clipKey.Contains("minothaur"))
		{
			if (!clipKey.Contains("baph") && !clipKey.Contains("baphomet") && !clipKey.Contains("gloryhole"))
			{
				return 0;
			}
		}
		int num = 10;
		if (clipKey.Contains("baphomet") || clipKey.Contains("gloryhole"))
		{
			num += 20;
		}
		return num;
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

	private static void CacheVisualFromTrigger(CameraSwapTrigger trigger, Animator animator)
	{
		if (trigger == null || (_cachedVisualTemplate != null && HasAnyMesh(_cachedVisualTemplate)))
		{
			return;
		}
		GameObject source = ((animator != null) ? animator.gameObject : trigger.gameObject);
		if (!HasAnyMesh(source) && trigger.gameObject != null && trigger.gameObject != source)
		{
			source = trigger.gameObject;
		}
		if (!HasAnyMesh(source))
		{
			Transform parent = trigger.transform;
			while (parent != null && !HasAnyMesh(parent.gameObject))
			{
				parent = parent.parent;
			}
			if (parent != null)
			{
				source = parent.gameObject;
			}
		}
		if (!HasAnyMesh(source))
		{
			Plugin.DBG("SPAWN", LogLabel + ": glory-hole trigger '" + NameRemap.StripCloneSuffix(trigger.gameObject.name) + "' has no mesh");
			return;
		}
		CacheVisualFromGameObject(source, "trigger:'" + NameRemap.StripCloneSuffix(source.name) + "'");
	}

	private static bool TryAttachStatueVisual(GameObject enemy)
	{
        var existingPresentation=enemy.GetComponent<ReskinPresentation>();
        if(existingPresentation!=null && existingPresentation.HasVisual && enemy.GetComponentInChildren<BaphometStatueVisual>(true)!=null) {existingPresentation.Enforce();return true;}


        if(!CreatorVisualHost.TryWorld("Gallery_Baphomet",out var template,out var idle,_cachedIdleController))return false;
        var gameObject=OverkeksMode.InstantiateOwned(template,enemy.transform);gameObject.name="BaphometStatueVisual";
        OverkeksMode.AddOwnedComponent<BaphometStatueVisual>(gameObject);
        gameObject.SetActive(true);MatchLayerRecursive(gameObject,enemy.layer);
        var animator=gameObject.GetComponent<Animator>();animator.runtimeAnimatorController=idle;
        animator.Play("Idle",0,0f);animator.Update(.001f);
        var bridge=gameObject.GetComponent<GravyIdleVisualBridge>();
        bridge.Source=gameObject.GetComponent<Image>();bridge.Target=gameObject.GetComponentInChildren<SpriteRenderer>(true);bridge.Sync();
        if(!CreatorVisualHost.Valid(bridge.Source)){gameObject.SetActive(false);UnityEngine.Object.Destroy(gameObject);return false;}
        FitVisualToEnemy(enemy, gameObject);gameObject.transform.localScale*=0.6f;
        BrightenStatueVisual(gameObject);
        float ground=CombinedRendererBounds(enemy,includeOwnedVisual:false).min.y;
        float dy=ground-bridge.Target.bounds.min.y;gameObject.transform.position+=Vector3.up*dy;
        bridge.GroundY=ground;bridge.SnapFeetToGround=true;
        var presentation=enemy.GetComponent<ReskinPresentation>() ?? OverkeksMode.AddOwnedComponent<ReskinPresentation>(enemy);presentation.Configure(gameObject);
        return true;
 
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
	/// Gallery shows Baphomet on shared displayModel/grabDisplayModel (controller swap only) —
	/// same pattern Custom Enemies replace for their packages. Steal those meshes.
	/// Must be called while EnemyGalleryUI exists (main menu) — dungeon unloads it.
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
			Plugin.DBG("SPAWN", LogLabel + ": EnemyGalleryUI not loaded yet — no displayModel to steal");
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
		gameObject.name = "BaphometGrabModelTemplate";
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

	private static void DumpBaphometNamedObjects()
	{
		int named = 0;
		GameObject[] array = Resources.FindObjectsOfTypeAll<GameObject>();
		StringBuilder stringBuilder = new StringBuilder();
		stringBuilder.Append(LogLabel).Append(" named-baphomet dump:");
		for (int i = 0; i < array.Length; i++)
		{
			GameObject gameObject = array[i];
			if (gameObject == null)
			{
				continue;
			}
			string text = NameRemap.StripCloneSuffix(gameObject.name);
			if (text.IndexOf("baph", StringComparison.OrdinalIgnoreCase) < 0 && text.IndexOf("glory", StringComparison.OrdinalIgnoreCase) < 0)
			{
				continue;
			}
			named++;
			if (named <= 12)
			{
				string types = SummarizeRendererTypes(gameObject);
				stringBuilder.Append(" | '").Append(text).Append("' r=").Append(CountAnyRenderers(gameObject)).Append(" mf=").Append(CountMeshFilters(gameObject)).Append(" solid=").Append(CountSolidRenderers(gameObject)).Append(" types=").Append(types);
			}
		}
		stringBuilder.Append(" (totalNamed=").Append(named).Append(")");
		Plugin.DBG("SPAWN", stringBuilder.ToString());
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
	/// World statue uses AnimatorController "Baphomet Statue" (Idle) — find that Animator and clone its mesh.
	/// </summary>
	private static void TryCacheVisualFromStatueController()
	{
		if (_cachedVisualTemplate != null && HasAnyMesh(_cachedVisualTemplate))
		{
			return;
		}
		if (_cachedVisualTemplate != null)
		{
			UnityEngine.Object.Destroy(_cachedVisualTemplate);
			_cachedVisualTemplate = null;
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
			string goName = NameRemap.StripCloneSuffix(animator.gameObject.name);
			int num = 0;
			if (text.Equals("Baphomet Statue", StringComparison.OrdinalIgnoreCase) || text.Equals("Baphomet_Statue", StringComparison.OrdinalIgnoreCase))
			{
				num += 80;
			}
			else if (text.IndexOf("baphomet", StringComparison.OrdinalIgnoreCase) >= 0 && text.IndexOf("grab", StringComparison.OrdinalIgnoreCase) < 0)
			{
				num += 40;
			}
			else if (goName.IndexOf("baphomet", StringComparison.OrdinalIgnoreCase) >= 0 && goName.IndexOf("grab", StringComparison.OrdinalIgnoreCase) < 0)
			{
				num += 30;
			}
			if (num == 0)
			{
				continue;
			}
			int solid = CountSolidRenderers(animator.gameObject);
			int meshOnly = CountMeshFilters(animator.gameObject);
			if (solid == 0 && meshOnly == 0)
			{
				continue;
			}
			num += Math.Min(30, solid * 5 + meshOnly * 3);
			if (animator.gameObject.scene.IsValid() && animator.gameObject.scene.isLoaded)
			{
				num += 15;
			}
			if (num > bestScore)
			{
				bestScore = num;
				best = animator;
			}
		}
		if (best == null)
		{
			Plugin.DBG("SPAWN", LogLabel + ": no Animator with Baphomet Statue mesh among " + array.Length + " Animator(s)");
			return;
		}
		CacheVisualFromGameObject(best.gameObject, "animator:'" + NameRemap.StripCloneSuffix(best.gameObject.name) + "' ctrl='" + best.runtimeAnimatorController.name + "'");
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
		if (source == null || (_cachedVisualTemplate != null && HasAnyMesh(_cachedVisualTemplate)))
		{
			return;
		}
		if (_cachedVisualTemplate != null)
		{
			UnityEngine.Object.Destroy(_cachedVisualTemplate);
			_cachedVisualTemplate = null;
		}
		GameObject gameObject = OverkeksMode.InstantiateOwned(source);
		gameObject.name = "BaphometStatueVisualTemplate";
		gameObject.SetActive(value: false);
		StripInteractJunk(gameObject);
		EnableAllRenderers(gameObject);
		if (gameObject.GetComponent<BaphometStatueVisual>() == null)
		{
			OverkeksMode.AddOwnedComponent<BaphometStatueVisual>(gameObject);
		}
		int solid = CountSolidRenderers(gameObject);
		int meshOnly = CountMeshFilters(gameObject);
		int any = CountAnyRenderers(gameObject);
		LogRendererDump(gameObject, "cache");
		if (solid == 0 && meshOnly == 0 && any == 0)
		{
			UnityEngine.Object.Destroy(gameObject);
			Plugin.DBG("SPAWN", LogLabel + ": visual clone via " + via + " has no renderers");
			return;
		}
		// Accept mesh-only / sprite clones — materials may resolve once active in scene.
		UnityEngine.Object.DontDestroyOnLoad(gameObject);
		_cachedVisualTemplate = gameObject;
		Plugin.DBG("SPAWN", LogLabel + ": cached statue visual via " + via + " solid=" + solid + " meshFilters=" + meshOnly + " any=" + any);
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
				// Sprites count — many PNC enemies (and possibly the statue preview) are sprite-based.
				if (renderer != null && renderer.GetType().Name == "SpriteRenderer")
				{
					num++;
				}
				continue;
			}
			if (!HasUsableMaterial(renderer))
			{
				// Still count skinned/mesh renderers even without materials yet — clone may resolve later.
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
		if (_cachedVisualTemplate != null && HasAnyMesh(_cachedVisualTemplate))
		{
			return;
		}
		GameObject best = null;
		int bestScore = 0;
		GameObject[] array = Resources.FindObjectsOfTypeAll<GameObject>();
		for (int i = 0; i < array.Length; i++)
		{
			GameObject gameObject = array[i];
			if (gameObject == null)
			{
				continue;
			}
			string text = NameRemap.StripCloneSuffix(gameObject.name).ToLowerInvariant();
			int num = 0;
			if (text.Equals("baphomet_statue") || text.Equals("baphomet statue"))
			{
				num += 80;
			}
			else if (text.Contains("baphomet") && text.Contains("statue"))
			{
				num += 60;
			}
			else if (text.Contains("baphomet"))
			{
				num += 30;
			}
			else if (text.Contains("gloryhole") || text.Contains("glory hole"))
			{
				num += 25;
			}
			else if (text.Contains("baph"))
			{
				num += 10;
			}
			if (text.StartsWith("gallery_") || text.Contains("gallery"))
			{
				num -= 40;
			}
			if (num == 0)
			{
				continue;
			}
			int solid = CountSolidRenderers(gameObject);
			int meshOnly = CountMeshFilters(gameObject);
			int any = CountAnyRenderers(gameObject);
			if (solid == 0 && meshOnly == 0 && any == 0)
			{
				continue;
			}
			num += Math.Min(20, solid * 3 + meshOnly * 2 + any);
			if (gameObject.GetComponentInChildren<SkinnedMeshRenderer>(includeInactive: true) != null)
			{
				num += 15;
			}
			if (gameObject.GetComponentInChildren<CameraSwapTrigger>(includeInactive: true) != null)
			{
				num += 10;
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
			Plugin.DBG("SPAWN", LogLabel + ": no Baphomet mesh among " + array.Length + " GameObject asset(s)");
			return;
		}
		CacheVisualFromGameObject(best, "asset:'" + NameRemap.StripCloneSuffix(best.name) + "'");
	}

	private static void FitVisualToEnemy(GameObject enemy, GameObject visual)
	{
		Bounds enemyBounds = CombinedRendererBounds(enemy, includeOwnedVisual: false);
		Bounds visualBounds = CombinedRendererBounds(visual, includeOwnedVisual: true);
		visual.transform.localPosition = Vector3.zero;
		visual.transform.localRotation = Quaternion.identity;
		visual.transform.localScale = Vector3.one;
		if (enemyBounds.size.y > 0.01f && visualBounds.size.y > 0.01f)
		{
			float num = enemyBounds.size.y / visualBounds.size.y;
			num = Mathf.Clamp(num, 0.35f, 3.5f);
			visual.transform.localScale = Vector3.one * num;
			visualBounds = CombinedRendererBounds(visual, includeOwnedVisual: true);
		}
		Vector3 vector = enemyBounds.center - visualBounds.center;
		vector.y = enemyBounds.min.y - visualBounds.min.y;
		visual.transform.position += vector;
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
			bool owned = renderer.GetComponentInParent<BaphometStatueVisual>() != null;
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
			if (!(renderer == null) && !(renderer.GetComponentInParent<BaphometStatueVisual>() != null) && renderer.enabled)
			{
				renderer.enabled = false;
				num++;
			}
		}
		Plugin.DBG("SPAWN", LogLabel + ": hid " + num + " base gargoyle renderer(s)");
	}

	private static void EnemyGallery_OpenGallery_CacheBaphometModels(EnemyGalleryUI __instance)
	{
 if(!OverkeksMode.Active) return;
		CacheModelsFromUi(__instance);
		if ((UnityEngine.Object)(object)Plugin.Instance != null)
		{
			OverkeksMode.StartCoroutine(CacheModelsNextFrames(__instance));
		}
	}

	private static void EnemyGallery_ViewGrab_CacheBaphometModels(EnemyGalleryUI __instance)
	{
 if(!OverkeksMode.Active) return;
		CacheModelsFromUi(__instance);
	}

	private static void MainMenu_Start_CacheBaphometModels(MainMenuManager __instance)
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
	private static void StartGrab_ForceBaphometVariant(GrabScreen __instance, GameObject enemy)
	{
 if(!OverkeksMode.Active) return;
		if (__instance == null || enemy == null || !__instance.IsGrabbed)
		{
			return;
		}
		BaphometGargoyleMarker component = enemy.GetComponent<BaphometGargoyleMarker>();
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
		string play = component.GrabStartState ?? (component.IsSet2 ? "Start2" : "Start");
		try
		{
			animator.Play(play, 0, 0f);
			animator.Update(0f);
		}
		catch
		{
		}
		Plugin.DBG("GRAB-START", LogLabel + " host forced '" + play + "' (" + component.VariantLabel + ") nowClip='" + GetCurrentClipName(animator) + "' isName=" + animator.GetCurrentAnimatorStateInfo(0).IsName(play));
	}

	/// <summary>
	/// Gallery_Baphomet_Grabbed animates UI.Image.sprite (not the idle world SpriteRenderer).
	/// Host the gallery grab UI hierarchy on our overlay canvas and Play the grab clip there.
	/// </summary>
	private static void InjectGalleryGrabModel(GrabScreen grabScreen, Animator grabScreenAnimator, BaphometGargoyleMarker marker)
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
		RuntimeAnimatorController grabCtrl = grabScreenAnimator.runtimeAnimatorController ?? _cachedGrabController;
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
			? grabScreen.GrabbingEnemy.GetComponent<BaphometGargoyleMarker>()
			: marker;
		set2 = marker != null && marker.IsSet2;
		string play = set2 ? "Start2" : "Start";
		string loop = set2 ? "Loop2" : "Loop";
		string cum = set2 ? "Cum2" : "Cum";
		if (marker != null)
		{
			play = FirstNonEmpty(marker.GrabStartState, play);
			loop = FirstNonEmpty(marker.GrabLoopState, loop);
			cum = FirstNonEmpty(marker.GrabCumState, cum);
		}
		string ediStart = set2 ? "Baphomet_Start2" : "Baphomet_Start";
		string ediLoop = set2 ? "Baphomet_Loop2" : "Baphomet_Loop";
		string ediCum = set2 ? "Baphomet_Cum2" : "Baphomet_Cum";
		if (marker != null)
		{
			ediStart = FirstNonEmpty(marker.EdiStart, ediStart);
			ediLoop = FirstNonEmpty(marker.EdiLoop, ediLoop);
			ediCum = FirstNonEmpty(marker.EdiCum, ediCum);
		}
		if (_liveGrabModel != null)
		{
			UnityEngine.Object.Destroy(_liveGrabModel);
			_liveGrabModel = null;
		}

        var overlay=BaphometGrabOverlay.Ensure();overlay.ClearContentBinding();
        if(!CreatorVisualHost.TryBuild(grabCtrl,play,overlay.transform,false,out var live,out var content,out var drive)) {CreatorVisualHost.ReleaseInvalidScene(grabScreen);return;}
        live.name="BaphometGrabModel";live.transform.SetAsLastSibling();
        var driver=OverkeksMode.AddOwnedComponent<BaphometGrabDriver>(live);
        driver.Begin(drive,grabScreenAnimator,play,loop,cum,ediStart,ediLoop,ediCum);
        drive.Update(0f);
        if(!CreatorVisualHost.Valid(content)){live.SetActive(false);UnityEngine.Object.Destroy(live);CreatorVisualHost.ReleaseInvalidScene(grabScreen);return;}
        overlay.ShowWorldBackdrop();
        Plugin.DBG("CREATOR-VISUAL","background=existing-camera; sceneOrder="+CreatorVisualPolicy.SceneSortingOrder+"; escapeHintOrder="+CreatorVisualPolicy.EscapeHintSortingOrder);
        Plugin.DBG("CREATOR-VISUAL","Baphomet scene verified root-Image state="+play+" sprite="+content.sprite.name);
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
		// TMPro without hard reference — gallery grabAnimNameText lives here ("name" bottom-right).
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
			|| name.Contains("baphomet") || name.Contains("grab") || name.Contains("cum")
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
			if (t.name == "BaphometGrabModel" || t.name.StartsWith("BaphometGrab", StringComparison.Ordinal))
			{
				break;
			}
			t = t.parent;
		}
		content.material = null;
		content.color = Color.white;
		BaphometGrabBrightener brightener = content.GetComponent<BaphometGrabBrightener>() ?? OverkeksMode.AddOwnedComponent<BaphometGrabBrightener>(content.gameObject);
		brightener.Target = content;
		brightener.EnsureAdditive();
	}

	/// <summary>
	/// One fullscreen Image named "Image" (matches Gallery_Baphomet_Grabbed sprite bindings), bright, no hole mask.
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
		image.material = BaphometGrabOverlay.GetBrightUiMaterial();
		image.color = BaphometGrabOverlay.BrightColor();
		BaphometGrabBrightener brightener = image.GetComponent<BaphometGrabBrightener>() ?? OverkeksMode.AddOwnedComponent<BaphometGrabBrightener>(image.gameObject);
		brightener.Target = image;
		brightener.EnsureAdditive();
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
	/// but hide it — BaphometGrabOverlay blits the sprite fullscreen without hole chrome.
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
		image.color = BaphometGrabOverlay.BrightColor();
		image.material = BaphometGrabOverlay.GetBrightUiMaterial();
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

	/// <summary>Find any loaded Animator already wired to Gallery_Baphomet_Grabbed that has UI Images (gallery grab avatar).</summary>
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
			if (text.IndexOf("Baphomet_Grabbed", StringComparison.OrdinalIgnoreCase) < 0 && text.IndexOf("Gallery_Baphomet_Grabbed", StringComparison.OrdinalIgnoreCase) < 0)
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
			Plugin.DBG("SPAWN", LogLabel + ": no Animator with Gallery_Baphomet_Grabbed + UI among " + array.Length);
			return;
		}
		CacheGrabUiTemplate(best.gameObject, "animator:'" + NameRemap.StripCloneSuffix(best.gameObject.name) + "' ctrl='" + best.runtimeAnimatorController.name + "'");
	}

	private static string[] BuildPlayCandidates(RuntimeAnimatorController controller, BaphometGargoyleMarker marker, bool riding)
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
		Add(riding ? "Gallery_Baphomet_Grabbed_Riding_Start" : "Gallery_Baphomet_Grabbed_BJ_Start");
		Add(riding ? "Baphomet_Grabbed_Riding_Start" : "Baphomet_Grabbed_BJ_Start");
		Add(riding ? "Baphomet_Grabbed_Start2" : "Baphomet_Grabbed_Start");
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
	private static void EndGrab_CleanupBaphometGrabModel(GrabScreen __instance)
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
			// Baphomet zeros grabImage alphas — restore so the next vanilla H-scene is visible.
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

	private static bool LooksLikeBaphometGrabClip(string clipName)
	{
		if (string.IsNullOrEmpty(clipName))
		{
			return false;
		}
		string text = clipName.ToLowerInvariant();
		return text.Contains("baphomet") || text.Contains("gloryhole") || text.Contains("grabbed");
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
		if (!LooksLikeBaphometGrabClip(clipName))
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
