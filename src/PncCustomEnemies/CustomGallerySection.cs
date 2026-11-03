using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using PncCustomEnemies;
using PncCustomEnemies.Api;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Video;

namespace PncEdi;

internal sealed class CustomGallerySectionMarker : MonoBehaviour
{
}

internal sealed class CustomGalleryContentActivator : MonoBehaviour
{
	private GalleryTabController _controller;

	internal void Initialize(GalleryTabController controller) => _controller = controller;

	private void OnEnable()
	{
		if (_controller != null) CustomGallerySectionHooks.ActivateCustom(_controller);
	}
}

[HarmonyPatch]
internal static class CustomGallerySectionHooks
{
	private sealed class WallViewerState
	{
		internal GameObject OriginalModel;
		internal Animator OriginalAnimator;
		internal GameObject CustomModel;
		internal bool RestoreCustomModelAfterScene;
		internal Image CustomDisplayImage;
		internal CustomGalleryUiDisplayDriver CustomUiDriver;
		internal GameObject OriginalGrabModel;
		// `grabDisplayModel` is null in the shipped prefab, so the field above never had anything to
		// hide. What actually draws vanilla's grab art is the `grabSceneAnimator`'s own object,
		// `Grabbed Animation Player` (§138) - these are what stand it down and put it back.
		// It is stood down component by component and never with `SetActive(false)`: the panel's
		// navigation - the arrows, the exit button and the animation-name text - are *children* of
		// it, so deactivating the object takes the whole UI with it, and an explicit
		// `SetActive(true)` on a child cannot win against an inactive parent (§139).
		internal GameObject HiddenGrabAnimatorObject;
		internal RuntimeAnimatorController HiddenGrabController;
		internal Graphic[] HiddenGrabGraphics;
		internal AudioSource HiddenGrabAudio;
		internal GameObject GrabOverlay;
		internal PackageStageGalleryDriver Driver;
		internal CustomEnemyGallerySceneDriver SceneDriver;
		internal Image PortraitImage;
	}

	private sealed class GallerySectionState
	{
		internal GameObject SourceContent;
		internal GameObject CustomContent;
		internal EnemyGalleryUI Gallery;
		internal List<EnemyGalleryEntry> VanillaEntries;
		internal int CustomIndex;
	}

	private static readonly Dictionary<EnemyGalleryUI, WallViewerState> ViewerStates = new Dictionary<EnemyGalleryUI, WallViewerState>();
	private static readonly Dictionary<GalleryTabController, GallerySectionState> SectionStates = new Dictionary<GalleryTabController, GallerySectionState>();
	private static readonly HashSet<GalleryTabController> ActivatingCustom = new HashSet<GalleryTabController>();
	private static readonly HashSet<EnemyGalleryUI> ActiveCustomGalleries = new HashSet<EnemyGalleryUI>();

	internal static void RefreshExisting()
	{
		foreach (GalleryTabController controller in UnityEngine.Object.FindObjectsByType<GalleryTabController>(FindObjectsInactive.Include, FindObjectsSortMode.None))
			EnsureCustomTab(controller);
	}

	internal static void ActivateCustom(GalleryTabController controller)
	{
		if (controller == null) return;
		if (!SectionStates.TryGetValue(controller, out GallerySectionState state))
		{
			EnsureCustomTab(controller);
			if (!SectionStates.TryGetValue(controller, out state))
			{
				Plugin.Log?.LogError("[CustomGallery] Custom Enemies was selected but the shared enemy panel is still unavailable");
				return;
			}
		}
		if (!ActivatingCustom.Add(controller)) return;
		try
		{
			GalleryTabController.Tab[] tabs = Traverse.Create(controller).Field("tabs").GetValue<GalleryTabController.Tab[]>();
			if (tabs != null)
			{
				foreach (GalleryTabController.Tab tab in tabs)
					if (tab?.content != null && tab.content != state.SourceContent && tab.content != state.CustomContent)
						tab.content.SetActive(false);
			}
			if (state.SourceContent != null) state.SourceContent.SetActive(true);
			if (state.CustomContent != null && !state.CustomContent.activeSelf) state.CustomContent.SetActive(true);
			Animator camera = Traverse.Create(controller).Field("cameraAnimator").GetValue<Animator>();
			if (camera != null)
			{
				camera.SetBool("EnemyGallery", true);
				camera.SetBool("DioramaGallery", false);
			}
			ShowCustomGallery(state);
		}
		catch (Exception exception)
		{
			Plugin.Log?.LogError("[CustomGallery] could not display Custom Enemies: " + exception);
		}
		finally
		{
			ActivatingCustom.Remove(controller);
		}
	}

	[HarmonyPatch(typeof(GalleryTabController), "OnEnable")]
	[HarmonyPostfix]
	private static void GalleryTabsOnEnablePostfix(GalleryTabController __instance)
	{
		EnsureCustomTab(__instance);
	}

	[HarmonyPatch(typeof(EnemyGalleryUI), "OpenGallery")]
	[HarmonyPostfix]
	private static void EnemyGalleryOpenPostfix(EnemyGalleryUI __instance)
	{
		GalleryTabController controller = __instance != null ? __instance.GetComponentInParent<GalleryTabController>(true) : null;
		if (controller == null)
			controller = UnityEngine.Object.FindFirstObjectByType<GalleryTabController>(FindObjectsInactive.Include);
		EnsureCustomTab(controller);
	}

	[HarmonyPatch(typeof(GalleryTabController), "SwitchTab")]
	[HarmonyPrefix]
	private static void GallerySwitchTabPrefix(GalleryTabController __instance)
	{
		EnsureCustomTab(__instance);
	}

	[HarmonyPatch(typeof(GalleryTabController), "SwitchTab")]
	[HarmonyPostfix]
	private static void GallerySwitchTabPostfix(GalleryTabController __instance, int index)
	{
		if (!SectionStates.TryGetValue(__instance, out GallerySectionState state)) return;
		Plugin.Log?.LogInfo("[CustomGallery] tab switch index=" + index + " customIndex=" + state.CustomIndex);
		if (index == state.CustomIndex)
		{
			ActivateCustom(__instance);
		}
		else if (index == 0)
		{
			ShowVanillaGallery(state);
		}
	}

	private static void EnsureCustomTab(GalleryTabController controller)
	{
		if (controller == null) return;
		GalleryTabController.Tab[] tabs = Traverse.Create(controller).Field("tabs").GetValue<GalleryTabController.Tab[]>();
		if (tabs == null || tabs.Length == 0 || tabs[0] == null || tabs[0].content == null || tabs[0].tabButton == null) return;
		if (AdoptExistingCustomTab(controller, tabs)) return;
		if (controller.GetComponent<CustomGallerySectionMarker>() != null) return;

		GalleryTabController.Tab source = tabs[0];
		GameObject customContent = new GameObject("CustomGalleryContent", typeof(RectTransform));
		customContent.transform.SetParent(source.content.transform.parent, false);
		customContent.name = "CustomGalleryContent";
		customContent.AddComponent<CustomGallerySectionMarker>();
		CustomGalleryContentActivator activator = customContent.AddComponent<CustomGalleryContentActivator>();
		activator.Initialize(controller);
		customContent.SetActive(false);

		Button customButton = UnityEngine.Object.Instantiate(source.tabButton, source.tabButton.transform.parent, false);
		customButton.name = "CustomGalleryTabButton";
		customButton.transform.SetAsLastSibling();
		SetButtonLabel(customButton, "Custom Enemies");
		FitCustomTab(customButton);
		customButton.onClick = new Button.ButtonClickedEvent();
		EnemyGalleryUI customUi = ResolveEnemyGallery(controller, source.content);

		GalleryTabController.Tab customTab = new GalleryTabController.Tab
		{
			tabButton = customButton,
			content = customContent,
			firstInContent = source.firstInContent,
			enemyGalleryBool = source.enemyGalleryBool,
			dioramaGalleryBool = false
		};
		GalleryTabController.Tab[] expanded = new GalleryTabController.Tab[tabs.Length + 1];
		Array.Copy(tabs, expanded, tabs.Length);
		int customIndex = tabs.Length;
		expanded[customIndex] = customTab;
		Traverse.Create(controller).Field("tabs").SetValue(expanded);
		WireHorizontalNavigation(tabs[tabs.Length - 1].tabButton, customButton);
		customButton.onClick.AddListener(delegate
		{
			Plugin.Log?.LogInfo("[CustomGallery] Custom Enemies button clicked");
			EnsureCustomTab(controller);
			ActivateCustom(controller);
		});
		RegisterSectionState(controller, source.content, customContent, customUi, customIndex);
		controller.gameObject.AddComponent<CustomGallerySectionMarker>();
		Plugin.Log?.LogInfo("[CustomGallery] added Custom Enemies tab with " + CountCustomEntries() + " entry/entries");
	}

	private static bool AdoptExistingCustomTab(GalleryTabController controller, GalleryTabController.Tab[] tabs)
	{
		int keep = -1;
		List<GalleryTabController.Tab> normalized = new List<GalleryTabController.Tab>(tabs.Length);
		for (int i = 0; i < tabs.Length; i++)
		{
			GalleryTabController.Tab tab = tabs[i];
			bool custom = tab != null && ((tab.tabButton != null && tab.tabButton.name.Equals("CustomGalleryTabButton", StringComparison.OrdinalIgnoreCase))
				|| (tab.content != null && tab.content.name.Equals("CustomGalleryContent", StringComparison.OrdinalIgnoreCase)));
			if (!custom)
			{
				normalized.Add(tab);
				continue;
			}
			if (keep < 0)
			{
				keep = normalized.Count;
				normalized.Add(tab);
				if (tab.content != null && tab.content.GetComponent<CustomGallerySectionMarker>() == null)
					tab.content.AddComponent<CustomGallerySectionMarker>();
				if (tab.tabButton != null)
				{
					SetButtonLabel(tab.tabButton, "Custom Enemies");
					FitCustomTab(tab.tabButton);
				}
			}
			else
			{
				if (tab.tabButton != null) UnityEngine.Object.Destroy(tab.tabButton.gameObject);
				if (tab.content != null) UnityEngine.Object.Destroy(tab.content);
			}
		}
		if (keep < 0) return false;
		if (normalized.Count != tabs.Length)
			Traverse.Create(controller).Field("tabs").SetValue(normalized.ToArray());
		GalleryTabController.Tab customTab = normalized[keep];
		GameObject sourceContent = normalized[0].content;
		if (customTab.content != null && customTab.content.GetComponentInChildren<EnemyGalleryUI>(true) != null)
		{
			GameObject obsoleteClone = customTab.content;
			GameObject placeholder = new GameObject("CustomGalleryContent", typeof(RectTransform));
			placeholder.transform.SetParent(sourceContent.transform.parent, false);
			placeholder.AddComponent<CustomGallerySectionMarker>();
			CustomGalleryContentActivator replacementActivator = placeholder.AddComponent<CustomGalleryContentActivator>();
			replacementActivator.Initialize(controller);
			placeholder.SetActive(obsoleteClone.activeSelf);
			customTab.content = placeholder;
			UnityEngine.Object.Destroy(obsoleteClone);
		}
		RegisterSectionState(controller, sourceContent, customTab.content,
			ResolveEnemyGallery(controller, sourceContent), keep);
		if (customTab.tabButton != null)
		{
			customTab.tabButton.onClick = new Button.ButtonClickedEvent();
			customTab.tabButton.onClick.AddListener(delegate
			{
				Plugin.Log?.LogInfo("[CustomGallery] adopted Custom Enemies button clicked");
				EnsureCustomTab(controller);
				ActivateCustom(controller);
			});
		}
		CustomGalleryContentActivator existingActivator = customTab.content != null ? customTab.content.GetComponent<CustomGalleryContentActivator>() : null;
		if (existingActivator == null && customTab.content != null) existingActivator = customTab.content.AddComponent<CustomGalleryContentActivator>();
		existingActivator?.Initialize(controller);
		if (controller.GetComponent<CustomGallerySectionMarker>() == null)
			controller.gameObject.AddComponent<CustomGallerySectionMarker>();
		if (customTab.content != null && customTab.content.activeInHierarchy) ActivateCustom(controller);
		return true;
	}

	private static void RegisterSectionState(GalleryTabController controller, GameObject sourceContent, GameObject customContent, EnemyGalleryUI gallery, int customIndex)
	{
		if (controller == null || gallery == null)
		{
			Plugin.Log?.LogError("[CustomGallery] could not register Custom Enemies tab: EnemyGalleryUI was not found");
			return;
		}
		if (SectionStates.TryGetValue(controller, out GallerySectionState existing))
		{
			existing.SourceContent = sourceContent;
			existing.CustomContent = customContent;
			existing.Gallery = gallery;
			existing.CustomIndex = customIndex;
			return;
		}
		FieldInfo field = AccessTools.Field(typeof(EnemyGalleryUI), "allEnemies");
		List<EnemyGalleryEntry> current = field?.GetValue(gallery) as List<EnemyGalleryEntry>;
		List<EnemyGalleryEntry> vanilla = current != null
			? current.FindAll(entry => !CustomEnemyRegistry.IsCustomGalleryEntry(entry) && !PackageGalleryEntries.IsProvided(entry))
			: new List<EnemyGalleryEntry>();
		SectionStates[controller] = new GallerySectionState
		{
			SourceContent = sourceContent,
			CustomContent = customContent,
			Gallery = gallery,
			VanillaEntries = vanilla,
			CustomIndex = customIndex
		};
		Plugin.Log?.LogInfo("[CustomGallery] registered shared enemy panel for custom index " + customIndex);
	}

	private static EnemyGalleryUI ResolveEnemyGallery(GalleryTabController controller, GameObject sourceContent)
	{
		EnemyGalleryUI gallery = sourceContent != null ? sourceContent.GetComponentInChildren<EnemyGalleryUI>(true) : null;
		if (gallery == null && controller != null) gallery = controller.GetComponentInChildren<EnemyGalleryUI>(true);
		if (gallery == null)
			gallery = UnityEngine.Object.FindFirstObjectByType<EnemyGalleryUI>(FindObjectsInactive.Include);
		return gallery;
	}

	private static void ShowCustomGallery(GallerySectionState state)
	{
		if (state?.Gallery == null) return;
		ActiveCustomGalleries.Add(state.Gallery);
		List<EnemyGalleryEntry> list = AccessTools.Field(typeof(EnemyGalleryUI), "allEnemies")?.GetValue(state.Gallery) as List<EnemyGalleryEntry>;
		if (list == null) return;
		list.Clear();
		foreach (EnemyGalleryEntry entry in CustomEnemyRegistry.GetGalleryEntries()) list.Add(entry);
		foreach (EnemyGalleryEntry entry in PackageGalleryEntries.All()) list.Add(entry);

		List<EnemyGalleryEntry> displayed = AccessTools.Field(typeof(EnemyGalleryUI), "displayedEnemies")?.GetValue(state.Gallery) as List<EnemyGalleryEntry>;
		if (displayed != null)
		{
			displayed.Clear();
			displayed.AddRange(list);
		}

		FieldInfo indexField = AccessTools.Field(typeof(EnemyGalleryUI), "currentEnemyIndex");
		indexField?.SetValue(state.Gallery, 0);

		Plugin.Log?.LogInfo("[CustomGallery] showing " + list.Count + " custom entry/entries on the original enemy panel"
			+ (list.Count > 0 ? ": " + list[0].enemyName : ""));

		SetEntryNavigationVisible(state.Gallery, "leftArrowButton", true);
		SetEntryNavigationVisible(state.Gallery, "rightArrowButton", true);

		Traverse.Create(state.Gallery).Method("DisplayCurrentEnemy").GetValue();
	}

	private static void ShowVanillaGallery(GallerySectionState state)
	{
		if (state?.Gallery == null) return;
		ActiveCustomGalleries.Remove(state.Gallery);
		DestroyGrabOverlay(state.Gallery);
		RestoreWallModel(state.Gallery);
		List<EnemyGalleryEntry> list = AccessTools.Field(typeof(EnemyGalleryUI), "allEnemies")?.GetValue(state.Gallery) as List<EnemyGalleryEntry>;
		if (list != null)
		{
			list.Clear();
			list.AddRange(state.VanillaEntries);
		}
		List<EnemyGalleryEntry> displayed = AccessTools.Field(typeof(EnemyGalleryUI), "displayedEnemies")?.GetValue(state.Gallery) as List<EnemyGalleryEntry>;
		if (displayed != null)
		{
			displayed.Clear();
			displayed.AddRange(state.VanillaEntries);
		}
		FieldInfo indexField = AccessTools.Field(typeof(EnemyGalleryUI), "currentEnemyIndex");
		indexField?.SetValue(state.Gallery, 0);

		SetEntryNavigationVisible(state.Gallery, "leftArrowButton", true);
		SetEntryNavigationVisible(state.Gallery, "rightArrowButton", true);
		state.Gallery.RefreshDisplay();
	}

	[HarmonyPatch(typeof(EnemyGalleryUI), "RefreshDisplayList")]
	[HarmonyPrefix]
	private static bool RefreshDisplayListPrefix(EnemyGalleryUI __instance)
	{
		if (ActiveCustomGalleries.Contains(__instance))
		{
			List<EnemyGalleryEntry> all = AccessTools.Field(typeof(EnemyGalleryUI), "allEnemies")?.GetValue(__instance) as List<EnemyGalleryEntry>;
			List<EnemyGalleryEntry> displayed = AccessTools.Field(typeof(EnemyGalleryUI), "displayedEnemies")?.GetValue(__instance) as List<EnemyGalleryEntry>;
			FieldInfo indexField = AccessTools.Field(typeof(EnemyGalleryUI), "currentEnemyIndex");
			if (all != null && displayed != null)
			{
				displayed.Clear();
				displayed.AddRange(all);
				int count = displayed.Count;
				int currentIndex = indexField != null ? (int)indexField.GetValue(__instance) : 0;
				currentIndex = count > 0 ? Mathf.Clamp(currentIndex, 0, count - 1) : 0;
				indexField?.SetValue(__instance, currentIndex);
				return false;
			}
		}
		return true;
	}

	private static void SetUiText(EnemyGalleryUI ui, string fieldName, string value)
	{
		Component component = Traverse.Create(ui).Field(fieldName).GetValue<Component>();
		if (component == null) return;
		if (component is Text legacy)
		{
			legacy.text = value;
			return;
		}
		PropertyInfo property = component.GetType().GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
		if (property != null && property.CanWrite && property.PropertyType == typeof(string)) property.SetValue(component, value);
	}

	private static void SetEntryNavigationVisible(EnemyGalleryUI ui, string fieldName, bool visible)
	{
		Button button = Traverse.Create(ui).Field(fieldName).GetValue<Button>();
		if (button != null) button.gameObject.SetActive(visible);
	}

	private static void WireHorizontalNavigation(Button previous, Button custom)
	{
		if (previous == null || custom == null) return;
		Navigation previousNavigation = previous.navigation;
		previousNavigation.selectOnRight = custom;
		previous.navigation = previousNavigation;
		Navigation customNavigation = custom.navigation;
		customNavigation.selectOnLeft = previous;
		customNavigation.selectOnRight = null;
		custom.navigation = customNavigation;
	}

	private static int CountCustomEntries()
	{
		int count = 0;
		foreach (EnemyGalleryEntry unused in CustomEnemyRegistry.GetGalleryEntries()) count++;
		foreach (EnemyGalleryEntry unused in PackageGalleryEntries.All()) count++;
		return count;
	}

	private static void SetButtonLabel(Button button, string text)
	{
		foreach (Component component in button.GetComponentsInChildren<Component>(true))
		{
			if (component is Text legacy)
			{
				legacy.text = text;
				legacy.horizontalOverflow = HorizontalWrapMode.Overflow;
				legacy.verticalOverflow = VerticalWrapMode.Overflow;
				continue;
			}
			PropertyInfo property = component.GetType().GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
			if (property != null && property.CanWrite && property.PropertyType == typeof(string)) property.SetValue(component, text);
			PropertyInfo wrapping = component.GetType().GetProperty("enableWordWrapping", BindingFlags.Instance | BindingFlags.Public);
			if (wrapping != null && wrapping.CanWrite && wrapping.PropertyType == typeof(bool)) wrapping.SetValue(component, false);
		}
	}

	private static void FitCustomTab(Button button)
	{
		RectTransform rect = button != null ? button.GetComponent<RectTransform>() : null;
		if (rect != null) rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, Mathf.Max(250f, rect.rect.width));
		LayoutElement layout = button != null ? button.GetComponent<LayoutElement>() : null;
		if (layout == null && button != null) layout = button.gameObject.AddComponent<LayoutElement>();
		if (layout != null)
		{
			layout.minWidth = Mathf.Max(220f, layout.minWidth);
			layout.preferredWidth = Mathf.Max(250f, layout.preferredWidth);
			layout.flexibleWidth = 0f;
		}
	}

	[HarmonyPatch(typeof(EnemyGalleryEntry), "HasGrabScene")]
	[HarmonyPostfix]
	private static void HasGrabScenePostfix(EnemyGalleryEntry __instance, ref bool __result)
	{
		if (PackageGalleryEntries.IsProvided(__instance))
		{
			__result = true;
			return;
		}
		CustomEnemyDefinition def = CustomEnemyRegistry.Find(__instance);
		if (def != null)
		{
			if ((def.GalleryEntry != null && def.GalleryEntry.grabAnimations != null && def.GalleryEntry.grabAnimations.Length > 0)
				|| (def.Manifest.galleryVideos != null && def.Manifest.galleryVideos.files != null && def.Manifest.galleryVideos.files.Length > 0)
				|| (def.Manifest.scenes != null && def.Manifest.scenes.Length > 0))
			{
				__result = true;
			}
		}
	}

	[HarmonyPatch(typeof(EnemyGalleryUI), "DisplayUnlockedEnemy")]
	[HarmonyPrefix]
	[HarmonyPriority(Priority.First)]
	private static void DisplayUnlockedEnemyPrefix(EnemyGalleryUI __instance)
	{
		WallViewerState state = GetState(__instance);
		GameObject currentModel = Traverse.Create(__instance).Field("displayModel").GetValue<GameObject>();
		Animator currentAnimator = Traverse.Create(__instance).Field("displayAnimator").GetValue<Animator>();
		if (state.OriginalModel == null && currentModel != null && !currentModel.name.StartsWith("CustomGalleryModel_", StringComparison.OrdinalIgnoreCase))
		{
			state.OriginalModel = currentModel;
			state.OriginalAnimator = currentAnimator;
		}
		Image icon = Traverse.Create(__instance).Field("enemyIconImage").GetValue<Image>();
		if (icon != null)
		{
			icon.enabled = true;
			icon.preserveAspect = true;
		}
		DestroyGrabOverlay(__instance);
	}

	[HarmonyPatch(typeof(EnemyGalleryUI), "DisplayUnlockedEnemy")]
	[HarmonyPostfix]
	private static void DisplayUnlockedEnemyPostfix(EnemyGalleryUI __instance, EnemyGalleryEntry enemy)
	{
		if (__instance == null || enemy == null) return;
		WallViewerState state = GetState(__instance);
		GameObject originalModel = state.OriginalModel ?? Traverse.Create(__instance).Field("displayModel").GetValue<GameObject>();
		if (state.OriginalModel == null && originalModel != null && !originalModel.name.StartsWith("CustomGalleryModel_", StringComparison.OrdinalIgnoreCase))
		{
			state.OriginalModel = originalModel;
			state.OriginalAnimator = Traverse.Create(__instance).Field("displayAnimator").GetValue<Animator>();
		}

		PackageGalleryPresentation provided = PackageGalleryEntries.Describe(enemy);
		CustomEnemyDefinition customDef = CustomEnemyRegistry.Find(enemy);

		if (provided == null && customDef == null) return;

		// 1. Enemy icon in header / UI
		Image icon = Traverse.Create(__instance).Field("enemyIconImage").GetValue<Image>();
		Sprite iconSprite = null;
		if (provided != null)
		{
			iconSprite = enemy.enemyIcon;
		}
		else if (customDef != null)
		{
			iconSprite = customDef.GalleryEntry?.enemyIcon;
		}

		if (icon != null && iconSprite != null)
		{
			icon.material = null;
			icon.color = Color.white;
			icon.type = Image.Type.Simple;
			icon.preserveAspect = true;
			icon.sprite = iconSprite;
			icon.enabled = true;
			state.PortraitImage = icon;
		}

		// 2. Prepare or reuse UI Image in the Screen Space Canvas
		if (state.CustomModel == null)
		{
			Transform parent = originalModel != null ? originalModel.transform.parent : __instance.transform;
			GameObject customUiObj = new GameObject("CustomGalleryModel_Display", typeof(RectTransform));
			customUiObj.transform.SetParent(parent, false);
			RectTransform rect = customUiObj.GetComponent<RectTransform>();
			RectTransform origRect = originalModel != null ? originalModel.GetComponent<RectTransform>() : null;
			if (origRect != null)
			{
				rect.anchorMin = origRect.anchorMin;
				rect.anchorMax = origRect.anchorMax;
				rect.pivot = origRect.pivot;
				rect.anchoredPosition = origRect.anchoredPosition;
				rect.sizeDelta = (origRect.sizeDelta.x > 50f && origRect.sizeDelta.y > 50f) ? origRect.sizeDelta : new Vector2(750f, 750f);
				rect.localScale = origRect.localScale;
			}
			else
			{
				rect.anchorMin = new Vector2(0.5f, 0.5f);
				rect.anchorMax = new Vector2(0.5f, 0.5f);
				rect.pivot = new Vector2(0.5f, 0.5f);
				rect.anchoredPosition = new Vector2(0f, 0f);
				rect.sizeDelta = new Vector2(750f, 750f);
				rect.localScale = Vector3.one;
			}
			Image displayImg = customUiObj.AddComponent<Image>();
			displayImg.preserveAspect = true;
			displayImg.raycastTarget = false;
			displayImg.color = Color.white;
			state.CustomModel = customUiObj;
			state.CustomDisplayImage = displayImg;
			state.CustomUiDriver = customUiObj.AddComponent<CustomGalleryUiDisplayDriver>();
		}

		if (originalModel != null) originalModel.SetActive(false);
		state.CustomModel.SetActive(true);
		state.CustomModel.transform.SetAsLastSibling();
		Traverse.Create(__instance).Field("displayModel").SetValue(state.CustomModel);
		Traverse.Create(__instance).Field("displayAnimator").SetValue(null);

		RuntimeSpriteAnimationData[] animsToPlay = null;
		if (provided != null)
		{
			// Scene stages are not overview animations. The provider already supplies the
			// overview portrait through its gallery entry; never substitute capture artwork.
			if (iconSprite != null)
				animsToPlay = new[] { new RuntimeSpriteAnimationData
				{
					Name = "Idle", Frames = new[] { iconSprite }, Fps = 0f
				} };
		}
		else if (customDef != null)
		{
			CustomEnemyRuntimeData data = CustomEnemyRuntimeData.Find(customDef.Id);
			if (data != null && data.Animations != null && data.Animations.Length > 0)
			{
				animsToPlay = data.Animations;
			}
			else if (customDef.Manifest.spriteVisual != null && customDef.Manifest.spriteVisual.animations != null)
			{
				try
				{
					List<RuntimeSpriteAnimationData> loaded = new List<RuntimeSpriteAnimationData>();
					foreach (var a in customDef.Manifest.spriteVisual.animations)
					{
						if (a != null && !string.IsNullOrWhiteSpace(a.name) && !string.IsNullOrWhiteSpace(a.file))
							loaded.Add(RuntimeSpriteVisual.LoadAnimation(customDef.Directory, customDef.Manifest.spriteVisual, a));
					}
					animsToPlay = loaded.ToArray();
					if (data != null) data.Animations = animsToPlay;
				}
				catch (Exception ex)
				{
					Plugin.Log?.LogWarning("[CustomGallery] could not load animation frames for " + customDef.Id + ": " + ex.Message);
				}
			}
		}

		if (animsToPlay != null && animsToPlay.Length > 0)
		{
			state.CustomUiDriver.Initialize(state.CustomDisplayImage, animsToPlay, 0);
		}
		else if (iconSprite != null)
		{
			state.CustomDisplayImage.sprite = iconSprite;
			state.CustomDisplayImage.enabled = true;
			state.CustomDisplayImage.preserveAspect = true;
		}

		SetUiText(__instance, "animationNameText", "Idle");
		Button grabBtn = Traverse.Create(__instance).Field("viewGrabSceneButton").GetValue<Button>();
		if (grabBtn != null)
		{
			bool hasGrab = (provided != null)
				|| (customDef != null && ((customDef.GalleryEntry != null && customDef.GalleryEntry.grabAnimations != null && customDef.GalleryEntry.grabAnimations.Length > 0)
					|| (customDef.Manifest.galleryVideos != null && customDef.Manifest.galleryVideos.files != null && customDef.Manifest.galleryVideos.files.Length > 0)
					|| (customDef.Manifest.scenes != null && customDef.Manifest.scenes.Length > 0)));
			grabBtn.gameObject.SetActive(hasGrab);
		}

		Plugin.Log?.LogInfo("[CustomGallery] Displayed custom UI sprite: " + enemy.enemyName
			+ " (sprite: " + (state.CustomDisplayImage.sprite != null ? state.CustomDisplayImage.sprite.name : "null")
			+ " rectSize: " + state.CustomModel.GetComponent<RectTransform>().sizeDelta
			+ " active: " + state.CustomModel.activeInHierarchy + ")");
	}

	[HarmonyPatch(typeof(EnemyGalleryUI), "PlayCurrentAnimation")]
	[HarmonyPostfix]
	private static void PlayCurrentAnimationPostfix(EnemyGalleryUI __instance)
	{
		if (ViewerStates.TryGetValue(__instance, out WallViewerState state) && state.CustomUiDriver != null)
		{
			int animIdx = Traverse.Create(__instance).Field("currentAnimationIndex").GetValue<int>();
			state.CustomUiDriver.SetAnimationIndex(animIdx);
		}
	}

	[HarmonyPatch(typeof(EnemyGalleryUI), "OnViewGrabSceneClicked")]
	[HarmonyPrefix]
	private static bool ViewSceneAdmissionPrefix(EnemyGalleryUI __instance) => GalleryPlaybackAllowed(__instance);

	[HarmonyPatch(typeof(EnemyGalleryUI), "PlayCurrentGrabAnimation")]
	[HarmonyPrefix]
	private static bool SceneStepAdmissionPrefix(EnemyGalleryUI __instance) => GalleryPlaybackAllowed(__instance);

	private static bool GalleryPlaybackAllowed(EnemyGalleryUI __instance)
	{
		bool allowed = __instance != null && PackageGalleryEntries.CanPlay(__instance.GetCurrentEnemy());
		if (!allowed) Plugin.Log?.LogInfo("[CustomGallery] blocked scene playback: entry missing or package disabled");
		return allowed;
	}

	[HarmonyPatch(typeof(EnemyGalleryUI), "OnViewGrabSceneClicked")]
	[HarmonyPostfix]
	private static void ViewGrabScenePostfix(EnemyGalleryUI __instance)
	{
		EnemyGalleryEntry currentEnemy = __instance.GetCurrentEnemy();
		if (!PackageGalleryEntries.CanPlay(currentEnemy)) return;
		PackageGalleryPresentation provided = PackageGalleryEntries.Describe(currentEnemy);
		CustomEnemyDefinition customDef = CustomEnemyRegistry.Find(currentEnemy);
		if (provided == null && customDef == null) return;

		WallViewerState state = GetState(__instance);
		GameObject panel = Traverse.Create(__instance).Field("grabScenePanel").GetValue<GameObject>();
		GameObject grabModel = Traverse.Create(__instance).Field("grabDisplayModel").GetValue<GameObject>();
		if (state.OriginalGrabModel == null) state.OriginalGrabModel = grabModel;
		if (panel == null) return;

		DestroyGrabOverlay(__instance);
		// This UI image is outside the native grab animator subtree. Hiding native
		// graphics alone leaves the overview drawn over the full-screen custom scene.
		state.RestoreCustomModelAfterScene = state.CustomModel != null && state.CustomModel.activeSelf;
		if (state.CustomModel != null) state.CustomModel.SetActive(false);
		if (grabModel != null) grabModel.SetActive(false);
		if (state.OriginalGrabModel != null) state.OriginalGrabModel.SetActive(false);

		// Vanilla has already assigned this entry's `grabAnimatorController` and called Play() with
		// one of *our* scene names on it. For a package that controller is the base enemy's, taken
		// by the `FindController(definition.Template, ...)` fallback in CustomEnemies, and the state
		// does not exist in it - so the animator sits on that controller's default state and draws
		// a goonshroom over the package's own scene, logging `Animator.GotoState: State could not be
		// found` once per step. Stand the whole object down for as long as the overlay owns the
		// panel; `grabAudioSource` rides on it, which is correct too, because a package brings its
		// own sound (§134).
		Animator grabAnimator = Traverse.Create(__instance).Field("grabSceneAnimator").GetValue<Animator>();
		if (grabAnimator != null)
		{
			state.HiddenGrabController = grabAnimator.runtimeAnimatorController;
			grabAnimator.runtimeAnimatorController = null;
			state.HiddenGrabAnimatorObject = grabAnimator.gameObject;

			// Components, never the object: the panel's navigation - exit, the two arrows, the
			// animation-name text - are children of this object, and an inactive parent beats any
			// `SetActive(true)` on a child, so deactivating it takes the whole UI with it (§139).
			// Vanilla's art is not only this object's own Image either; it continues into children,
			// which is why this descends. Everything in the subtree stops drawing except the four
			// navigation objects and whatever hangs under them - those are resolved from the UI's
			// own fields rather than by name, and they sit after our overlay, so they draw on top
			// of the package's art rather than under it.
			List<Graphic> hidden = new List<Graphic>();
			foreach (Graphic graphic in grabAnimator.gameObject.GetComponentsInChildren<Graphic>(true))
			{
				if (graphic == null || !graphic.enabled) continue;
				if (IsNavigation(graphic.transform, __instance)) continue;
				graphic.enabled = false;
				hidden.Add(graphic);
			}
			state.HiddenGrabGraphics = hidden.ToArray();

			// `grabAudioSource` rides on the same object. Deactivating the object used to silence it
			// as a side effect; now it has to be said, because a package brings its own sound (§134).
			AudioSource grabAudio = grabAnimator.gameObject.GetComponent<AudioSource>();
			if (grabAudio != null && grabAudio.enabled)
			{
				grabAudio.Stop();
				grabAudio.enabled = false;
				state.HiddenGrabAudio = grabAudio;
			}
		}
		// The panel's own Image - vanilla's black background - is deliberately left alone. §138
		// disabled it on the guess that it was letting a `Character Display` show through; the panel
		// stack says there is no such child, and the overlay's `Backdrop` below is an opaque
		// full-screen black *child*, so it already covers that background. Disabling it bought
		// nothing and was never restored, which is how a single custom entry left every later
		// vanilla grab view showing the menu behind it (§139).

		GameObject overlay = new GameObject("CustomGalleryAnimation", typeof(RectTransform));
		string ownerId=currentEnemy.enemyID;
		PackageRuntime.OwnObject(ownerId,overlay);
		PackageRuntime.RegisterCleanup(ownerId,"gallery:"+__instance.GetInstanceID(),()=>
		{
			if(__instance != null && __instance.GetCurrentEnemy()?.enemyID==ownerId)
			{
				DestroyGrabOverlay(__instance, restorePresentation: false);
				if (state.CustomModel != null) state.CustomModel.SetActive(false);
				if (state.PortraitImage != null) state.PortraitImage.enabled = false;
				GalleryHooks.EndMenuGalleryView("Package disabled");
			}
		});
		overlay.transform.SetParent(panel.transform, false);
		overlay.transform.SetAsFirstSibling();
		Stretch(overlay.GetComponent<RectTransform>());
		GameObject backdrop = UiObject("Backdrop", overlay.transform);
		Image backdropImage = backdrop.AddComponent<Image>();
		backdropImage.color = Color.black;
		backdropImage.raycastTarget = false;
		Stretch(backdrop.GetComponent<RectTransform>());

		Button exitBtn = Traverse.Create(__instance).Field("exitGrabViewButton").GetValue<Button>();
		if (exitBtn != null)
		{
			exitBtn.gameObject.SetActive(true);
			exitBtn.transform.SetAsLastSibling();
		}
		Button prevBtn = Traverse.Create(__instance).Field("prevGrabAnimButton").GetValue<Button>();
		if (prevBtn != null)
		{
			prevBtn.gameObject.SetActive(true);
			prevBtn.transform.SetAsLastSibling();
		}
		Button nextBtn = Traverse.Create(__instance).Field("nextGrabAnimButton").GetValue<Button>();
		if (nextBtn != null)
		{
			nextBtn.gameObject.SetActive(true);
			nextBtn.transform.SetAsLastSibling();
		}
		Component animText = Traverse.Create(__instance).Field("grabAnimNameText").GetValue<Component>();
		if (animText != null)
		{
			animText.gameObject.SetActive(true);
			animText.transform.SetAsLastSibling();
		}

		if (provided != null)
		{
			GameObject imageObject = UiObject("Animation", overlay.transform);
			Image image = imageObject.AddComponent<Image>();
			image.preserveAspect = true;
			image.raycastTarget = false;
			Stretch(imageObject.GetComponent<RectTransform>());
			PackageStageGalleryDriver driver = overlay.AddComponent<PackageStageGalleryDriver>();
			driver.Initialize(provided, image);
			state.GrabOverlay = overlay;
			state.Driver = driver;
			SetGalleryStage(__instance, state);
		}
		else if (customDef != null)
		{
			GameObject rawImageObject = UiObject("VideoDisplay", overlay.transform);
			RawImage rawImage = rawImageObject.AddComponent<RawImage>();
			rawImage.raycastTarget = false;
			Stretch(rawImageObject.GetComponent<RectTransform>());
			CustomEnemyGallerySceneDriver sceneDriver = overlay.AddComponent<CustomEnemyGallerySceneDriver>();
			sceneDriver.Initialize(customDef, rawImage);
			state.GrabOverlay = overlay;
			state.SceneDriver = sceneDriver;
			SetGalleryStage(__instance, state);
		}

		LogPanelStack(panel, __instance);
	}

	// What else is drawing on the grab panel, in sibling order, two levels deep - the part of this
	// screen that keeps being inferred instead of read (§138, §139). A package's scene is drawn by
	// an overlay inserted as the panel's first child, so anything listed *after* it, at any depth,
	// is painted on top of the package's own art. The navigation - exit, the two arrows, the
	// animation-name text - are children of `Grabbed Animation Player`, so that object cannot be
	// deactivated; the question this answers is which of its *other* children carry vanilla's art.
	private static void LogPanelStack(GameObject panel, EnemyGalleryUI ui)
	{
		if (panel == null) return;
		System.Text.StringBuilder line = new System.Text.StringBuilder();
		AppendNode(line, panel.transform, 0, ui);
		Plugin.Log?.LogInfo("[CustomGallery] panel stack: " + line);
	}

	private static void AppendNode(System.Text.StringBuilder line, Transform parent, int depth, EnemyGalleryUI ui)
	{
		for (int i = 0; i < parent.childCount; i++)
		{
			Transform child = parent.GetChild(i);
			if (line.Length > 0) line.Append(" | ");
			line.Append(new string('>', depth)).Append(i).Append(':').Append(child.name);
			if (!child.gameObject.activeInHierarchy) line.Append(" (inactive)");
			Graphic graphic = child.GetComponent<Graphic>();
			if (graphic != null)
			{
				line.Append(" <").Append(graphic.GetType().Name);
				if (!graphic.enabled) line.Append(" off");
				line.Append(" a=").Append(graphic.color.a.ToString("0.00"));
				RectTransform rect = child as RectTransform;
				if (rect != null) line.Append(" ").Append(rect.rect.width.ToString("0")).Append("x").Append(rect.rect.height.ToString("0"));
				line.Append('>');
			}
			string role = NavRole(child.gameObject, ui);
			if (role != null) line.Append(" [").Append(role).Append(']');
			if (depth < 2) AppendNode(line, child, depth + 1, ui);
		}
	}

	// Is this transform, or anything above it, one of the panel's navigation objects? Walking up
	// matters because a Button's art is usually a child of the Button itself.
	private static bool IsNavigation(Transform candidate, EnemyGalleryUI ui)
	{
		for (Transform node = candidate; node != null; node = node.parent)
			if (NavRole(node.gameObject, ui) != null) return true;
		return false;
	}

	// Which of these the mod must leave alone, resolved from the fields rather than by name.
	private static string NavRole(GameObject candidate, EnemyGalleryUI ui)
	{
		if (ui == null || candidate == null) return null;
		Button exitBtn = Traverse.Create(ui).Field("exitGrabViewButton").GetValue<Button>();
		Button prevBtn = Traverse.Create(ui).Field("prevGrabAnimButton").GetValue<Button>();
		Button nextBtn = Traverse.Create(ui).Field("nextGrabAnimButton").GetValue<Button>();
		Component animText = Traverse.Create(ui).Field("grabAnimNameText").GetValue<Component>();
		if (exitBtn != null && exitBtn.gameObject == candidate) return "exit";
		if (prevBtn != null && prevBtn.gameObject == candidate) return "prev";
		if (nextBtn != null && nextBtn.gameObject == candidate) return "next";
		if (animText != null && animText.gameObject == candidate) return "animText";
		return null;
	}

	[HarmonyPatch(typeof(EnemyGalleryUI), "PlayCurrentGrabAnimation")]
	[HarmonyPostfix]
	private static void PlayCurrentGrabAnimationPostfix(EnemyGalleryUI __instance)
	{
		if (PackageGalleryEntries.CanPlay(__instance.GetCurrentEnemy()) && ViewerStates.TryGetValue(__instance, out WallViewerState state)) SetGalleryStage(__instance, state);
	}

	[HarmonyPatch(typeof(EnemyGalleryUI), "ExitGrabView")]
	[HarmonyPrefix]
	private static void ExitGrabViewPrefix(EnemyGalleryUI __instance)
	{
		DestroyGrabOverlay(__instance);
		GalleryHooks.EndMenuGalleryView("CustomGallery");
	}

	[HarmonyPatch(typeof(EnemyGalleryUI), "CloseGallery")]
	[HarmonyPrefix]
	private static void CloseGalleryPrefix(EnemyGalleryUI __instance)
	{
		ActiveCustomGalleries.Remove(__instance);
		DestroyGrabOverlay(__instance);
		RestoreWallModel(__instance);
	}

	private static void SetGalleryStage(EnemyGalleryUI ui, WallViewerState state)
	{
		int index = Traverse.Create(ui).Field("currentGrabAnimIndex").GetValue<int>();
		if (state?.Driver != null) state.Driver.SetStage(index);
		if (state?.SceneDriver != null) state.SceneDriver.SetStage(index);
	}

	private static WallViewerState GetState(EnemyGalleryUI ui)
	{
		if (!ViewerStates.TryGetValue(ui, out WallViewerState state))
		{
			state = new WallViewerState();
			ViewerStates[ui] = state;
		}
		return state;
	}

	private static void RestoreWallModel(EnemyGalleryUI ui)
	{
		if (!ViewerStates.TryGetValue(ui, out WallViewerState state)) return;
		if (state.CustomModel != null) state.CustomModel.SetActive(false);
		if (state.OriginalModel != null) state.OriginalModel.SetActive(true);
		Traverse.Create(ui).Field("displayModel").SetValue(state.OriginalModel);
		Traverse.Create(ui).Field("displayAnimator").SetValue(state.OriginalAnimator);
	}

	private static void DestroyGrabOverlay(EnemyGalleryUI ui, bool restorePresentation = true)
	{
		if (!ViewerStates.TryGetValue(ui, out WallViewerState state)) return;
		if (state.GrabOverlay != null) UnityEngine.Object.Destroy(state.GrabOverlay);
		state.GrabOverlay = null;
		if (restorePresentation && state.RestoreCustomModelAfterScene && state.CustomModel != null)
			state.CustomModel.SetActive(true);
		state.RestoreCustomModelAfterScene = false;
		state.Driver = null;
		state.SceneDriver = null;
		// Package removal leaves the existing black scene panel and navigation intact.
		// Keep native art/controller/audio suppressed until Back, tab switch or close
		// calls this method with restoration enabled. No package media is retained.
		if (!restorePresentation) return;
		if (state.OriginalGrabModel != null) state.OriginalGrabModel.SetActive(true);
		if (state.HiddenGrabGraphics != null)
		{
			foreach (Graphic graphic in state.HiddenGrabGraphics)
			{
				if (graphic != null) graphic.enabled = true;
			}
			state.HiddenGrabGraphics = null;
		}
		if (state.HiddenGrabAudio != null)
		{
			state.HiddenGrabAudio.enabled = true;
			state.HiddenGrabAudio = null;
		}
		if (state.HiddenGrabAnimatorObject != null)
		{
			Animator restored = state.HiddenGrabAnimatorObject.GetComponent<Animator>();
			if (restored != null) restored.runtimeAnimatorController = state.HiddenGrabController;
			state.HiddenGrabAnimatorObject = null;
			state.HiddenGrabController = null;
		}
	}

	private static GameObject UiObject(string name, Transform parent)
	{
		GameObject result = new GameObject(name, typeof(RectTransform));
		result.transform.SetParent(parent, false);
		return result;
	}

	private static void SetLayerRecursively(GameObject root, int layer)
	{
		if (root == null) return;
		root.layer = layer;
		foreach (Transform child in root.transform) SetLayerRecursively(child.gameObject, layer);
	}

	private static void Stretch(RectTransform rect)
	{
		rect.anchorMin = Vector2.zero;
		rect.anchorMax = Vector2.one;
		rect.offsetMin = Vector2.zero;
		rect.offsetMax = Vector2.zero;
	}
}

/// <summary>
/// The viewer for a gallery entry a package contributed: its stages of sprites, its sound, and the
/// row each stage plays.
///
/// **The framework draws it, not the package** (§165). A package describes what its entry looks
/// like through `PackageGalleryPresentation`; everything here - the stepping, the frame clock on
/// unscaled time so it survives a pause, the audio, the Edi dispatch - is the mod's, so every
/// package's gallery behaves the same way and a package cannot get between the viewer and the
/// device.
/// </summary>
internal sealed class PackageStageGalleryDriver : MonoBehaviour
{
	private PackageGalleryPresentation _presentation;
	private Image _image;
	private int _stage;
	private int _frame;
	private float _clock;
	private AudioSource _audio;

	internal void Initialize(PackageGalleryPresentation presentation, Image image)
	{
		_presentation = presentation;
		_image = image;
		if (presentation?.Sound != null)
		{
			_audio = gameObject.AddComponent<AudioSource>();
			_audio.clip = presentation.Sound;
			_audio.loop = presentation.SoundLoops;
			_audio.volume = Mathf.Clamp01(presentation.SoundVolume);
			_audio.playOnAwake = false;
			_audio.spatialBlend = 0f;
			_audio.ignoreListenerPause = true;
			_audio.Play();
		}
		SetStage(0);
	}

	internal void SetStage(int stage)
	{
		if (_presentation?.Stages == null || _presentation.Stages.Length == 0) return;
		_stage = Mathf.Clamp(stage, 0, _presentation.Stages.Length - 1);
		_frame = 0;
		_clock = 0f;
		Draw();
		string gallery = _presentation.Stages[_stage]?.Gallery;
		if (!string.IsNullOrWhiteSpace(gallery))
		{
			Plugin.SendPlay(gallery, loop: true, inGame: false);
			GalleryHooks.GalleryPlaybackActive = true;
		}
	}

	private void Update()
	{
		PackageSpriteAnimation animation = PackageGalleryEntries.StageAnimation(_presentation, _stage);
		if (animation?.Frames == null || animation.Frames.Length == 0 || _image == null) return;
		_clock += Time.unscaledDeltaTime;
		float duration = 1f / Mathf.Max(0.01f, animation.Fps);
		while (_clock >= duration)
		{
			_clock -= duration;
			_frame = (_frame + 1) % animation.Frames.Length;
			Draw();
		}
	}

	private void Draw()
	{
		PackageSpriteAnimation animation = PackageGalleryEntries.StageAnimation(_presentation, _stage);
		if (animation?.Frames == null || animation.Frames.Length == 0 || _image == null) return;
		_image.sprite = animation.Frames[Mathf.Clamp(_frame, 0, animation.Frames.Length - 1)];
		_image.SetAllDirty();
	}

	private void OnDestroy()
	{
		if (_audio != null)
		{
			_audio.Stop();
			UnityEngine.Object.Destroy(_audio);
		}
	}
}

internal sealed class CustomEnemyGallerySceneDriver : MonoBehaviour
{
	private CustomEnemyDefinition _definition;
	private RawImage _rawImage;
	private VideoPlayer _videoPlayer;
	private RenderTexture _videoTexture;
	private AudioSource _videoAudio;
	private int _stage;

	internal void Initialize(CustomEnemyDefinition definition, RawImage rawImage)
	{
		_definition = definition;
		_rawImage = rawImage;
		_videoTexture = new RenderTexture(1280, 720, 0, RenderTextureFormat.ARGB32);
		_videoTexture.Create();
		_rawImage.texture = _videoTexture;

		_videoAudio = gameObject.AddComponent<AudioSource>();
		_videoAudio.playOnAwake = false;
		_videoAudio.loop = true;
		_videoAudio.spatialBlend = 0f;
		_videoAudio.ignoreListenerPause = true;
		_videoAudio.volume = Mathf.Clamp01(definition?.Manifest.galleryVideos?.volume ?? 1f);

		_videoPlayer = gameObject.AddComponent<VideoPlayer>();
		_videoPlayer.playOnAwake = false;
		_videoPlayer.isLooping = true;
		_videoPlayer.skipOnDrop = true;
		_videoPlayer.waitForFirstFrame = false;
		_videoPlayer.audioOutputMode = VideoAudioOutputMode.AudioSource;
		_videoPlayer.controlledAudioTrackCount = 1;
		_videoPlayer.EnableAudioTrack(0, true);
		_videoPlayer.SetTargetAudioSource(0, _videoAudio);
		_videoPlayer.renderMode = VideoRenderMode.RenderTexture;
		_videoPlayer.targetTexture = _videoTexture;

		SetStage(0);
	}

	internal void SetStage(int stage)
	{
		if (_definition == null) return;
		_stage = stage;
		string videoPath = null;
		string galleryName = null;

		CustomEnemyGalleryVideos videos = _definition.Manifest.galleryVideos;
		if (videos != null && videos.files != null && videos.files.Length > 0)
		{
			int vidIndex = Mathf.Clamp(stage, 0, videos.files.Length - 1);
			videoPath = Path.GetFullPath(Path.Combine(_definition.Directory, videos.files[vidIndex]));
			galleryName = CustomEnemyRegistry.GalleryVideoRow(videos, vidIndex);
			if (string.IsNullOrWhiteSpace(galleryName)) galleryName = _definition.Id + "_scene_" + stage;
		}
		else if (_definition.Manifest.scenes != null && _definition.Manifest.scenes.Length > 0)
		{
			CustomEnemyScene scene = _definition.Manifest.scenes[Mathf.Clamp(stage, 0, _definition.Manifest.scenes.Length - 1)];
			if (!string.IsNullOrWhiteSpace(scene.file) && (scene.file.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) || scene.file.EndsWith(".webm", StringComparison.OrdinalIgnoreCase)))
			{
				videoPath = Path.GetFullPath(Path.Combine(_definition.Directory, scene.file));
			}
			galleryName = string.IsNullOrWhiteSpace(scene.gallery) ? _definition.Id + "_" + NameRemap.Slug(scene.animation) : scene.gallery.Trim();
		}

		if (!string.IsNullOrWhiteSpace(videoPath) && _videoPlayer != null)
		{
			// See PackageVideo: prefers a WebM sibling, since Unity cannot decode H.264 on Linux.
			string videoUrl = PackageVideo.ResolveUrl(Path.GetDirectoryName(videoPath), Path.GetFileName(videoPath));
			if (!string.IsNullOrEmpty(videoUrl))
			{
				_videoPlayer.Stop();
				_videoPlayer.url = videoUrl;
				_videoPlayer.Prepare();
				_videoPlayer.Play();
			}
		}

		if (!string.IsNullOrWhiteSpace(galleryName))
		{
			Plugin.SendPlay(galleryName, loop: true, inGame: false);
			GalleryHooks.GalleryPlaybackActive = true;
		}
	}

	private void OnDestroy()
	{
		if (_videoPlayer != null)
		{
			_videoPlayer.Stop();
			_videoPlayer.targetTexture = null;
		}
		if (_videoAudio != null)
		{
			_videoAudio.Stop();
		}
		if (_videoTexture != null)
		{
			_videoTexture.Release();
			Destroy(_videoTexture);
		}
	}
}

internal sealed class CustomGalleryUiDisplayDriver : MonoBehaviour
{
	private Image _image;
	private RuntimeSpriteAnimationData[] _animations;
	private int _animationIndex;
	private int _frame;
	private float _elapsed;

	internal void Initialize(Image image, RuntimeSpriteAnimationData[] animations, int defaultIndex = 0)
	{
		_image = image;
		_animations = animations;
		_animationIndex = defaultIndex >= 0 && defaultIndex < (animations?.Length ?? 0) ? defaultIndex : 0;
		_frame = 0;
		_elapsed = 0f;
		UpdateSprite();
	}

	internal void SetAnimationIndex(int index)
	{
		if (_animations == null || _animations.Length == 0) return;
		_animationIndex = Mathf.Clamp(index, 0, _animations.Length - 1);
		_frame = 0;
		_elapsed = 0f;
		UpdateSprite();
	}

	private void Update()
	{
		if (_image == null || _animations == null || _animations.Length == 0) return;
		if (_animationIndex < 0 || _animationIndex >= _animations.Length) return;
		RuntimeSpriteAnimationData current = _animations[_animationIndex];
		if (current.Frames == null || current.Frames.Length <= 1 || current.Fps <= 0f) return;

		_elapsed += Time.unscaledDeltaTime;
		float duration = 1f / current.Fps;
		while (_elapsed >= duration)
		{
			_elapsed -= duration;
			if (_frame + 1 < current.Frames.Length)
			{
				_frame++;
			}
			else if (current.Loop)
			{
				_frame = 0;
			}
			else
			{
				_elapsed = 0f;
				break;
			}
			UpdateSprite();
		}
	}

	private void UpdateSprite()
	{
		if (_image == null || _animations == null || _animations.Length == 0) return;
		if (_animationIndex < 0 || _animationIndex >= _animations.Length) return;
		RuntimeSpriteAnimationData current = _animations[_animationIndex];
		if (current.Frames != null && current.Frames.Length > 0)
		{
			int frameIdx = Mathf.Clamp(_frame, 0, current.Frames.Length - 1);
			_image.sprite = current.Frames[frameIdx];
			_image.enabled = true;
			_image.preserveAspect = true;
			_image.SetAllDirty();
		}
	}
}

