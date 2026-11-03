using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PncEdi;

[HarmonyPatch]
public static class GalleryHooks
{
	public static bool MenuGalleryOpen;
	public static bool ViewingPeekScene;

	// True while a gallery step is playing. MenuGalleryOpen alone was not enough: the first
	// EndMenuGalleryView clears it, so viewing a second scene and leaving it again found the
	// guard false and never stopped anything - the script just kept running.
	public static bool GalleryPlaybackActive;
	public static bool InGamePeekScene;
	private static FieldInfo _enemyGalleryPanelField;
	private static FieldInfo _animsField;
	private static FieldInfo _indexField;
	private static FieldInfo _peekListField;
	private static FieldInfo _peekEntryIndexField;
	private static FieldInfo _peekAnimsField;
	private static FieldInfo _peekAnimIndexField;
	private static FieldInfo _peekViewingField;
	private static FieldInfo _entryEnemyIdField;
	private static FieldInfo _dioramasField;
	private static FieldInfo _dioramaIndexField;
	private static FieldInfo _dioramaEntryGalleryIdField;
	private static FieldInfo _dioramaEntryDioramaField;

	// GalleryTabController held three GameObject fields (dioramasTab/enemiesTab/peekScenesTab) up
	// to game 0.2.1. In 0.3.1 they are a single private `Tab[] tabs`, and each entry says what it
	// is rather than being identified by comparing GameObject references. `Tab` is a *private*
	// nested type, so it cannot be named in C# here - every read of one goes through reflection,
	// with the FieldInfos resolved off the first instance seen (CHANGELOG §64).
	private static FieldInfo _tabsField;
	private static FieldInfo _tabContentField;
	private static FieldInfo _tabIsDioramaField;

	internal static void SendMenuGalleryStep(string enemyName, string animationName, string logTag, string packageRow = null)
	{
		if (!string.IsNullOrEmpty(animationName))
		{
			bool fromPackage = !string.IsNullOrWhiteSpace(packageRow);
			string gallery = fromPackage ? packageRow : PeekGalleryMap.BuildCombinedSlug(enemyName ?? "Gallery", animationName);
			Plugin.DBG(logTag, "enemy='" + enemyName + "' key=" + NameRemap.ResolveEnemyKey(enemyName ?? "Gallery") + " anim='" + animationName + "' -> " + gallery
				+ (fromPackage ? " (the package's own row)" : ""));
			Plugin.SendPlay(gallery, loop: true, inGame: false);
			GalleryPlaybackActive = true;
		}
	}

	internal static void SendPeekGalleryStep(EnemyGalleryEntry entry, string animationName, string logTag)
	{
		string entryEnemyId = GetEntryEnemyId(entry);
		string enemyName = entry?.enemyName;
		string assetName = (entry != null) ? entry.name : null;
		string peepholeController = GetEntryPeepholeController(entry);
		string gallery = PeekGalleryMap.Resolve(entryEnemyId, enemyName, animationName, assetName, peepholeController);
		if (string.IsNullOrEmpty(gallery))
		{
			// Name every key that was tried, not just two of them: this is the line that has to
			// say why a peek scene is silent, and the display name is the least stable of the
			// four (§80).
			Plugin.DBG(logTag, "no peek map for asset='" + assetName + "' controller='" + peepholeController
				+ "' enemyID='" + entryEnemyId + "' name='" + enemyName + "'");
			if (!string.IsNullOrEmpty(animationName))
			{
				SendMenuGalleryStep(enemyName, animationName, logTag);
			}
		}
		else
		{
			ViewingPeekScene = true;
			Plugin.DBG(logTag, "asset='" + assetName + "' name='" + enemyName + "' anim='" + animationName + "' -> " + gallery);
			Plugin.SendPlay(gallery, loop: true, inGame: false);
			GalleryPlaybackActive = true;
		}
	}

	internal static void SendDioramaAmbientStep(string galleryId, string logTag, GameObject dioramaRoot = null)
	{
		if (string.IsNullOrEmpty(galleryId) && dioramaRoot == null)
		{
			return;
		}
		// The entry's own galleryID first, the audio scan only where there isn't one. These
		// answer the same question and the ID is the game's own label, where the audio route
		// matches a clip-name pattern kept by hand in this repo - so the ID is the one that
		// cannot silently be a version behind (§65). This order used to be the other way round.
		string gallery = null;
		if (!string.IsNullOrEmpty(galleryId))
		{
			gallery = DioramaGalleryMap.Resolve(galleryId);
			if (!string.IsNullOrEmpty(gallery))
			{
				Plugin.DBG(logTag, "id '" + galleryId + "' -> " + gallery);
			}
		}
		if (string.IsNullOrEmpty(gallery) && dioramaRoot != null)
		{
			gallery = AmbientProximity.TryResolveAmbientGalleryForHierarchy(dioramaRoot);
			if (!string.IsNullOrEmpty(gallery))
			{
				Plugin.DBG(logTag, "audio on '" + dioramaRoot.name + "' -> " + gallery);
			}
		}
		if (string.IsNullOrEmpty(gallery))
		{
			Plugin.DBG(logTag, "galleryID='" + galleryId + "' -> no ambient map");
			return;
		}
		Plugin.DBG(logTag, "galleryID='" + galleryId + "' -> " + gallery);
		Plugin.SendPlay(gallery, loop: true, inGame: false);
	}

	internal static void SetMenuGalleryOpen(bool open)
	{
		MenuGalleryOpen = open;
		if (!open)
		{
			ViewingPeekScene = false;
		}
	}

	internal static bool BlocksInGameEdiTracking()
	{
		if (!MenuGalleryOpen)
		{
			return false;
		}
		if (!IsAnyGalleryChromeVisible())
		{
			MenuGalleryOpen = false;
			ViewingPeekScene = false;
			Plugin.DBG("GALLERY", "cleared stuck MenuGalleryOpen");
			return false;
		}
		return true;
	}

	private static bool IsAnyGalleryChromeVisible()
	{
		EnemyGalleryUI[] enemyGalleryUIs = Object.FindObjectsByType<EnemyGalleryUI>((FindObjectsInactive)1, (FindObjectsSortMode)0);
		foreach (EnemyGalleryUI enemyGalleryUI in enemyGalleryUIs)
		{
			if (!(enemyGalleryUI == null) && enemyGalleryUI.isActiveAndEnabled)
			{
				if (_enemyGalleryPanelField == null)
				{
					_enemyGalleryPanelField = AccessTools.Field(typeof(EnemyGalleryUI), "galleryPanel");
				}
				object? obj = _enemyGalleryPanelField?.GetValue(enemyGalleryUI);
				GameObject gameObject = (GameObject)((obj is GameObject) ? obj : null);
				if (gameObject != null && gameObject.activeInHierarchy)
				{
					return true;
				}
			}
		}
		DioramaGalleryUI[] dioramaGalleryUIs = Object.FindObjectsByType<DioramaGalleryUI>((FindObjectsInactive)1, (FindObjectsSortMode)0);
		foreach (DioramaGalleryUI dioramaGalleryUI in dioramaGalleryUIs)
		{
			if (dioramaGalleryUI != null && dioramaGalleryUI.isActiveAndEnabled && dioramaGalleryUI.gameObject.activeInHierarchy)
			{
				return true;
			}
		}
		return false;
	}

	internal static void EndMenuGalleryView(string logTag)
	{
		if (MenuGalleryOpen || ViewingPeekScene || GalleryPlaybackActive)
		{
			SetMenuGalleryOpen(open: false);
			GalleryPlaybackActive = false;
			Plugin.EndGalleryPlayback(logTag);
		}
	}

	internal static void StopDioramaAmbient(string logTag)
	{
		GalleryPlaybackActive = false;
		Plugin.EndGalleryPlayback(logTag);
	}

	private static GrabAnimationData[] GetCurrentAnims(EnemyGalleryUI ui)
	{
		if (_animsField == null)
		{
			_animsField = AccessTools.Field(typeof(EnemyGalleryUI), "currentGrabAnimations");
		}
		return _animsField?.GetValue(ui) as GrabAnimationData[];
	}

	private static int GetCurrentIndex(EnemyGalleryUI ui)
	{
		if (_indexField == null)
		{
			_indexField = AccessTools.Field(typeof(EnemyGalleryUI), "currentGrabAnimIndex");
		}
		return (int)(_indexField?.GetValue(ui) ?? ((object)0));
	}

	private static EnemyGalleryEntry GetCurrentPeekEntry(PeekScenesUI ui)
	{
		if (_peekListField == null)
		{
			_peekListField = AccessTools.Field(typeof(PeekScenesUI), "displayedPeekScenes");
		}
		if (_peekEntryIndexField == null)
		{
			_peekEntryIndexField = AccessTools.Field(typeof(PeekScenesUI), "currentEntryIndex");
		}
		if (ui == null || _peekListField == null || _peekEntryIndexField == null)
		{
			return null;
		}
		List<EnemyGalleryEntry> enemyGalleryEntries = _peekListField.GetValue(ui) as List<EnemyGalleryEntry>;
		int index = (int)_peekEntryIndexField.GetValue(ui);
		if (enemyGalleryEntries == null || index < 0 || index >= enemyGalleryEntries.Count)
		{
			return null;
		}
		return enemyGalleryEntries[index];
	}

	private static GrabAnimationData[] GetPeekAnims(PeekScenesUI ui)
	{
		if (_peekAnimsField == null)
		{
			_peekAnimsField = AccessTools.Field(typeof(PeekScenesUI), "currentAnimations");
		}
		return _peekAnimsField?.GetValue(ui) as GrabAnimationData[];
	}

	private static int GetPeekAnimIndex(PeekScenesUI ui)
	{
		if (_peekAnimIndexField == null)
		{
			_peekAnimIndexField = AccessTools.Field(typeof(PeekScenesUI), "currentAnimIndex");
		}
		return (int)(_peekAnimIndexField?.GetValue(ui) ?? ((object)0));
	}

	private static string GetEntryEnemyId(EnemyGalleryEntry entry)
	{
		if (entry == null)
		{
			return null;
		}
		if (_entryEnemyIdField == null)
		{
			_entryEnemyIdField = AccessTools.Field(typeof(EnemyGalleryEntry), "enemyID");
		}
		return _entryEnemyIdField?.GetValue(entry) as string;
	}

	// A peek entry leaves animatorController null and carries its peephole on
	// grabAnimatorController - the same field the grab entries use for their grab screen. Read
	// through Traverse so a build without the field is a null rather than a crash.
	private static string GetEntryPeepholeController(EnemyGalleryEntry entry)
	{
		if (entry == null)
		{
			return null;
		}
		RuntimeAnimatorController controller = entry.grabAnimatorController ?? entry.animatorController;
		return (controller != null) ? controller.name : null;
	}

	private static bool IsPeekEntryUnlocked(EnemyGalleryEntry entry)
	{
		if (entry == null)
		{
			return false;
		}
		if (entry.alwaysUnlocked || Plugin.CfgUnlockAllGallery.Value)
		{
			return true;
		}
		if (GalleryProgressManager.Instance == null)
		{
			return true;
		}
		string entryEnemyId = GetEntryEnemyId(entry);
		return !string.IsNullOrEmpty(entryEnemyId) && GalleryProgressManager.Instance.IsEnemyUnlocked(entryEnemyId);
	}

	private static void EnsureDioramaReflection()
	{
		if (!(_dioramasField != null))
		{
			_dioramasField = AccessTools.Field(typeof(DioramaGalleryUI), "dioramas");
			_dioramaIndexField = AccessTools.Field(typeof(DioramaGalleryUI), "currentIndex");
			Type type = AccessTools.Inner(typeof(DioramaGalleryUI), "DioramaEntry");
			if (type != null)
			{
				_dioramaEntryGalleryIdField = AccessTools.Field(type, "galleryID");
				_dioramaEntryDioramaField = AccessTools.Field(type, "diorama");
			}
		}
	}

	private static object GetDioramaListEntry(DioramaGalleryUI ui, int index)
	{
		if (ui == null)
		{
			return null;
		}
		EnsureDioramaReflection();
		object obj = _dioramasField?.GetValue(ui);
		if (obj == null)
		{
			return null;
		}
		if (obj is IList list)
		{
			if (index < 0 || index >= list.Count)
			{
				return null;
			}
			return list[index];
		}
		PropertyInfo property = obj.GetType().GetProperty("Item", BindingFlags.Instance | BindingFlags.Public);
		if (property == null)
		{
			return null;
		}
		return property.GetValue(obj, new object[1] { index });
	}

	private static string GetCurrentDioramaGalleryId(DioramaGalleryUI ui)
	{
		if (ui == null)
		{
			return null;
		}
		try
		{
			EnsureDioramaReflection();
			if (_dioramasField == null || _dioramaIndexField == null || _dioramaEntryGalleryIdField == null)
			{
				Plugin.DBG("DIORAMA-ID", "reflection fields missing");
				return null;
			}
			int index = (int)_dioramaIndexField.GetValue(ui);
			object dioramaListEntry = GetDioramaListEntry(ui, index);
			if (dioramaListEntry == null)
			{
				Plugin.DBG("DIORAMA-ID", "no entry at index " + index);
				return null;
			}
			return _dioramaEntryGalleryIdField.GetValue(dioramaListEntry) as string;
		}
		catch (Exception ex)
		{
			Plugin.DBG("DIORAMA-ID", "reflection failed: " + ex.Message);
			return null;
		}
	}

	private static string GetCurrentDioramaResolveKey(DioramaGalleryUI ui)
	{
		string currentDioramaGalleryId = GetCurrentDioramaGalleryId(ui);
		if (!string.IsNullOrEmpty(currentDioramaGalleryId))
		{
			return currentDioramaGalleryId;
		}
		EnsureDioramaReflection();
		int index = (int)(_dioramaIndexField?.GetValue(ui) ?? ((object)0));
		object dioramaListEntry = GetDioramaListEntry(ui, index);
		if (dioramaListEntry != null && _dioramaEntryDioramaField != null)
		{
			object? value = _dioramaEntryDioramaField.GetValue(dioramaListEntry);
			GameObject gameObject = (GameObject)((value is GameObject) ? value : null);
			if (gameObject != null && !string.IsNullOrEmpty(gameObject.name))
			{
				Plugin.DBG("DIORAMA-ID", "using diorama object name '" + gameObject.name + "'");
				return gameObject.name;
			}
		}
		return null;
	}

	private static GameObject GetCurrentDioramaGameObject(DioramaGalleryUI ui)
	{
		EnsureDioramaReflection();
		int index = (int)(_dioramaIndexField?.GetValue(ui) ?? ((object)0));
		object dioramaListEntry = GetDioramaListEntry(ui, index);
		if (dioramaListEntry == null || _dioramaEntryDioramaField == null)
		{
			return null;
		}
		object? value = _dioramaEntryDioramaField.GetValue(dioramaListEntry);
		return (GameObject)((value is GameObject) ? value : null);
	}

	private static Array GetTabs(GalleryTabController controller)
	{
		if (controller == null)
		{
			return null;
		}
		if (_tabsField == null)
		{
			_tabsField = AccessTools.Field(typeof(GalleryTabController), "tabs");
		}
		return _tabsField?.GetValue(controller) as Array;
	}

	/// <summary>Resolve Tab's fields off a live instance - the type itself is private.</summary>
	private static void EnsureTabFields(object tab)
	{
		if (tab == null || _tabContentField != null)
		{
			return;
		}
		Type type = tab.GetType();
		_tabContentField = AccessTools.Field(type, "content");
		_tabIsDioramaField = AccessTools.Field(type, "dioramaGalleryBool");
	}

	private static bool IsDioramaTab(object tab)
	{
		EnsureTabFields(tab);
		return tab != null && _tabIsDioramaField?.GetValue(tab) is bool isDiorama && isDiorama;
	}

	private static GameObject GetTabContent(object tab)
	{
		EnsureTabFields(tab);
		object? obj = ((tab != null) ? _tabContentField?.GetValue(tab) : null);
		return (GameObject)((obj is GameObject) ? obj : null);
	}

	private static GameObject GetDioramasTab(GalleryTabController controller)
	{
		Array tabs = GetTabs(controller);
		if (tabs == null)
		{
			return null;
		}
		// The dioramas tab is whichever entry declares itself one. Nothing here assumes a slot
		// index: `defaultTabIndex` exists and the order is authored in the inspector.
		for (int i = 0; i < tabs.Length; i++)
		{
			object tab = tabs.GetValue(i);
			if (IsDioramaTab(tab))
			{
				return GetTabContent(tab);
			}
		}
		return null;
	}

	private static DioramaGalleryUI GetDioramaUiOnTab(GameObject dioramasTab)
	{
		if (dioramasTab == null)
		{
			return null;
		}
		DioramaGalleryUI dioramaGalleryUI = dioramasTab.GetComponent<DioramaGalleryUI>();
		if (dioramaGalleryUI != null)
		{
			return dioramaGalleryUI;
		}
		return dioramasTab.GetComponentInChildren<DioramaGalleryUI>(true);
	}

	[HarmonyPatch(typeof(EnemyGalleryUI), "PlayCurrentGrabAnimation")]
	[HarmonyPostfix]
	public static void PlayCurrentGrabAnimation_Postfix(EnemyGalleryUI __instance)
	{
		try
		{
			if (__instance == null)
			{
				return;
			}
			GrabAnimationData[] currentAnims = GetCurrentAnims(__instance);
			int currentIndex = GetCurrentIndex(__instance);
			if (currentAnims != null && currentIndex >= 0 && currentIndex < currentAnims.Length)
			{
				GrabAnimationData grabAnimationData = currentAnims[currentIndex];
				if (grabAnimationData != null)
				{
					EnemyGalleryEntry entry = __instance.GetCurrentEnemy();
					if (!CustomEnemyBridge.CanPlayGalleryEntry(entry)) return;
					// A package names its own rows; slugging its display name would invent one (§138).
					string packageRow = CustomEnemyBridge.GalleryRow(entry, grabAnimationData.animationName);
					SendMenuGalleryStep(entry?.enemyName, grabAnimationData.animationName, "GALLERY-STEP", packageRow);
				}
			}
		}
		catch (Exception ex)
		{
			LogGalleryWarning("Gallery PlayGrab patch error: ", ex);
		}
	}

	[HarmonyPatch(typeof(PeekScenesUI), "DisplayCurrent")]
	[HarmonyPostfix]
	public static void PeekDisplayCurrent_Postfix(PeekScenesUI __instance)
	{
		try
		{
			if (!(__instance == null))
			{
				// DisplayCurrent also fires while merely scrolling the list, so this used to
				// start a peek script before "View Scene" was ever pressed. Only dispatch when
				// the scene is actually being viewed - PlayCurrentAnimation covers that, and
				// this postfix then handles switching scene while still inside the view.
				if (IsViewingPeekScene(__instance))
				{
					EnemyGalleryEntry currentPeekEntry = GetCurrentPeekEntry(__instance);
					if (IsPeekEntryUnlocked(currentPeekEntry) && currentPeekEntry != null)
					{
						SendPeekGalleryStep(currentPeekEntry, null, "PEEK-BROWSE");
					}
				}
			}
		}
		catch (Exception ex)
		{
			LogGalleryWarning("Peek DisplayCurrent patch error: ", ex);
		}
	}

	private static bool IsViewingPeekScene(PeekScenesUI ui)
	{
		if (ui == null)
		{
			return false;
		}
		if (_peekViewingField == null)
		{
			_peekViewingField = AccessTools.Field(typeof(PeekScenesUI), "isViewingPeekScene");
		}
		return _peekViewingField != null && (bool)_peekViewingField.GetValue(ui);
	}

	[HarmonyPatch(typeof(PeekScenesUI), "PlayCurrentAnimation")]
	[HarmonyPostfix]
	public static void PeekPlayCurrentAnimation_Postfix(PeekScenesUI __instance)
	{
		try
		{
			if (__instance == null)
			{
				return;
			}
			EnemyGalleryEntry currentPeekEntry = GetCurrentPeekEntry(__instance);
			GrabAnimationData[] peekAnims = GetPeekAnims(__instance);
			int peekAnimIndex = GetPeekAnimIndex(__instance);
			if (peekAnims != null && peekAnimIndex >= 0 && peekAnimIndex < peekAnims.Length)
			{
				GrabAnimationData grabAnimationData = peekAnims[peekAnimIndex];
				if (grabAnimationData != null)
				{
					SendPeekGalleryStep(currentPeekEntry, grabAnimationData.animationName, "PEEK-STEP");
				}
			}
		}
		catch (Exception ex)
		{
			LogGalleryWarning("Peek PlayAnimation patch error: ", ex);
		}
	}

	[HarmonyPatch(typeof(PeekScenesUI), "ExitPeekView")]
	[HarmonyPrefix]
	public static void PeekExitPeekView_Prefix(PeekScenesUI __instance, out bool __state)
	{
		if (_peekViewingField == null)
		{
			_peekViewingField = AccessTools.Field(typeof(PeekScenesUI), "isViewingPeekScene");
		}
		__state = __instance != null && _peekViewingField != null && (bool)_peekViewingField.GetValue(__instance);
	}

	[HarmonyPatch(typeof(EnemyGalleryUI), "OpenGallery")]
	[HarmonyPrefix]
	public static void EnemyGallery_OpenGallery_Prefix()
	{
		SetMenuGalleryOpen(open: true);
		ViewingPeekScene = false;
		Plugin.Instance?.ResetImpGrappleTrackingOnly();
	}

	[HarmonyPatch(typeof(EnemyGalleryUI), "OpenGallery")]
	[HarmonyPostfix]
	public static void EnemyGallery_OpenGallery_Postfix()
	{
		// Was GoFiller, which contradicted the log line by starting the filler loop.
		Plugin.EndGalleryPlayback("GALLERY-OPEN");
	}

	[HarmonyPatch(typeof(DioramaGalleryUI), "OnEnable")]
	[HarmonyPostfix]
	public static void DioramaGallery_OnEnable_Postfix(DioramaGalleryUI __instance)
	{
		SetMenuGalleryOpen(open: true);
		QueueDioramaAmbient(__instance, "DIORAMA-ENABLE");
	}

	[HarmonyPatch(typeof(DioramaGalleryUI), "DisplayCurrent")]
	[HarmonyPostfix]
	public static void DioramaDisplayCurrent_Postfix(DioramaGalleryUI __instance)
	{
		try
		{
			SendDioramaAmbientForUi(__instance, "DIORAMA-STEP");
		}
		catch (Exception ex)
		{
			LogGalleryWarning("Diorama DisplayCurrent patch error: ", ex);
		}
	}

	[HarmonyPatch(typeof(DioramaGalleryUI), "ShowPrevious")]
	[HarmonyPostfix]
	public static void DioramaShowPrevious_Postfix(DioramaGalleryUI __instance)
	{
		SendDioramaAmbientForUi(__instance, "DIORAMA-PREV");
	}

	[HarmonyPatch(typeof(DioramaGalleryUI), "ShowNext")]
	[HarmonyPostfix]
	public static void DioramaShowNext_Postfix(DioramaGalleryUI __instance)
	{
		SendDioramaAmbientForUi(__instance, "DIORAMA-NEXT");
	}

	private static void SendDioramaAmbientForUi(DioramaGalleryUI ui, string logTag)
	{
		if (!(ui == null))
		{
			string currentDioramaResolveKey = GetCurrentDioramaResolveKey(ui);
			GameObject currentDioramaGameObject = GetCurrentDioramaGameObject(ui);
			if (string.IsNullOrEmpty(currentDioramaResolveKey) && currentDioramaGameObject == null)
			{
				Plugin.DBG(logTag, "no galleryID/object name for current diorama entry");
			}
			else
			{
				SendDioramaAmbientStep(currentDioramaResolveKey, logTag, currentDioramaGameObject);
			}
		}
	}

	private static void QueueDioramaAmbient(DioramaGalleryUI ui, string logTag)
	{
		if (!(ui == null) && !(Plugin.Instance == null))
		{
			Plugin.Instance.StartCoroutine(DioramaAmbientBurst(ui, logTag));
		}
	}

	private static IEnumerator DioramaAmbientBurst(DioramaGalleryUI ui, string logTag)
	{
		for (int frame = 0; frame < 3; frame++)
		{
			yield return null;
			if (ui != null && ui.gameObject.activeInHierarchy)
			{
				SendDioramaAmbientForUi(ui, logTag + "+" + frame);
			}
		}
	}

	[HarmonyPatch(typeof(DioramaGalleryUI), "OnDisable")]
	[HarmonyPostfix]
	public static void DioramaOnDisable_Postfix()
	{
		try
		{
			StopDioramaAmbient("DIORAMA-EXIT");
		}
		catch (Exception ex)
		{
			LogGalleryWarning("Diorama OnDisable patch error: ", ex);
		}
	}

	[HarmonyPatch(typeof(MainMenuManager), "OpenDioramaGallery")]
	[HarmonyPostfix]
	public static void OpenDioramaGallery_Postfix()
	{
		try
		{
			SetMenuGalleryOpen(open: true);
			DioramaGalleryUI ui = Object.FindObjectOfType<DioramaGalleryUI>(true);
			QueueDioramaAmbient(ui, "DIORAMA-OPEN");
		}
		catch (Exception ex)
		{
			LogGalleryWarning("Diorama OpenGallery patch error: ", ex);
		}
	}

	[HarmonyPatch(typeof(MainMenuManager), "CloseDioramaGallery")]
	[HarmonyPostfix]
	public static void CloseDioramaGallery_Postfix()
	{
		try
		{
			SetMenuGalleryOpen(open: false);
			StopDioramaAmbient("DIORAMA-CLOSE");
		}
		catch (Exception ex)
		{
			LogGalleryWarning("Diorama CloseGallery patch error: ", ex);
		}
	}

	[HarmonyPatch(typeof(EnemyGalleryUI), "ExitGrabView")]
	[HarmonyPostfix]
	public static void ExitGrabView_Postfix()
	{
		try
		{
			EndMenuGalleryView("GALLERY-EXIT");
		}
		catch (Exception ex)
		{
			LogGalleryWarning("Gallery ExitGrab patch error: ", ex);
		}
	}

	[HarmonyPatch(typeof(EnemyGalleryUI), "CloseGallery")]
	[HarmonyPostfix]
	public static void CloseGallery_Postfix()
	{
		try
		{
			EndMenuGalleryView("GALLERY-CLOSE");
		}
		catch (Exception ex)
		{
			LogGalleryWarning("Gallery CloseGallery patch error: ", ex);
		}
	}

	[HarmonyPatch(typeof(PeekScenesUI), "ExitPeekView")]
	[HarmonyPostfix]
	public static void PeekExitPeekView_Postfix(bool __state)
	{
		try
		{
			if (__state)
			{
				EndMenuGalleryView("PEEK-EXIT");
			}
		}
		catch (Exception ex)
		{
			LogGalleryWarning("Peek ExitPeekView patch error: ", ex);
		}
	}

	[HarmonyPatch(typeof(PeekScenesUI), "CloseScreen")]
	[HarmonyPostfix]
	public static void PeekCloseScreen_Postfix()
	{
		try
		{
			EndMenuGalleryView("PEEK-CLOSE");
		}
		catch (Exception ex)
		{
			LogGalleryWarning("Peek CloseScreen patch error: ", ex);
		}
	}

	[HarmonyPatch(typeof(GalleryTabController), "ShowPeekScenesTab")]
	[HarmonyPostfix]
	public static void ShowPeekScenesTab_Postfix(GalleryTabController __instance)
	{
		try
		{
			PeekScenesUI componentInChildren = __instance.GetComponentInChildren<PeekScenesUI>(true);
			if (componentInChildren != null && componentInChildren.isActiveAndEnabled)
			{
				EnemyGalleryEntry currentPeekEntry = GetCurrentPeekEntry(componentInChildren);
				if (IsPeekEntryUnlocked(currentPeekEntry) && currentPeekEntry != null)
				{
					SendPeekGalleryStep(currentPeekEntry, null, "PEEK-TAB");
				}
			}
		}
		catch (Exception ex)
		{
			LogGalleryWarning("Peek tab patch error: ", ex);
		}
	}

	[HarmonyPatch(typeof(GalleryTabController), "SwitchTab")]
	[HarmonyPostfix]
	public static void GallerySwitchTab_Postfix(GalleryTabController __instance, int index)
	{
		try
		{
			if (__instance == null)
			{
				return;
			}
			Array tabs = GetTabs(__instance);
			if (tabs == null || index < 0 || index >= tabs.Length)
			{
				return;
			}
			if (IsDioramaTab(tabs.GetValue(index)))
			{
				ViewingPeekScene = false;
				DioramaGalleryUI dioramaUiOnTab = GetDioramaUiOnTab(GetTabContent(tabs.GetValue(index)));
				SendDioramaAmbientForUi(dioramaUiOnTab, "DIORAMA-TAB");
				QueueDioramaAmbient(dioramaUiOnTab, "DIORAMA-TAB");
			}
			else
			{
				StopDioramaAmbient("GALLERY-TAB-LEAVE");
			}
		}
		catch (Exception ex)
		{
			LogGalleryWarning("Gallery SwitchTab patch error: ", ex);
		}
	}

	[HarmonyPatch(typeof(GalleryTabController), "ShowDioramasTab")]
	[HarmonyPostfix]
	public static void ShowDioramasTab_Postfix(GalleryTabController __instance)
	{
		try
		{
			if (!(__instance == null))
			{
				GameObject dioramasTab = GetDioramasTab(__instance);
				DioramaGalleryUI dioramaUiOnTab = GetDioramaUiOnTab(dioramasTab);
				ViewingPeekScene = false;
				SendDioramaAmbientForUi(dioramaUiOnTab, "DIORAMA-TAB");
				QueueDioramaAmbient(dioramaUiOnTab, "DIORAMA-TAB");
			}
		}
		catch (Exception ex)
		{
			LogGalleryWarning("Diorama tab patch error: ", ex);
		}
	}

	private static void LogGalleryWarning(string message, Exception ex)
	{
		Plugin.Log?.LogWarning($"{message}{ex}");
	}
}
