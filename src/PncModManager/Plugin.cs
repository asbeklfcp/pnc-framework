using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Unity.Mono;
using BepInEx.Unity.Mono.Bootstrap;
using BepInEx.Unity.Mono.Configuration;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace PncModManager;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed partial class Plugin : BaseUnityPlugin
{
	public const string PluginGuid = "com.pnc.modmanager";
	public const string PluginName = "PNC Framework Dashboard";
	public const string PluginVersion = "1.8.12";
	private const string PncEditPluginGuid = "com.edi.pnc";

	/// <summary>
	/// True while the manager window has the screen.
	///
	/// Read by other plugins - PncEdi's SafeInput does, by reflection, so neither assembly has to
	/// reference the other - so that their hotkeys stand down while the user is typing into a
	/// settings field. Time.timeScale going to zero is not enough on its own: Update() still runs
	/// at timescale zero, so an unguarded Input.GetKeyDown in another mod fires on every keystroke
	/// that happens to land on its shortcut.
	/// </summary>
	public static bool IsOpen { get; private set; }

	private ConfigEntry<KeyboardShortcut> _toggleShortcut;
	private ConfigEntry<bool> _pauseWhileOpen;
	private ConfigEntry<bool> _showSettingsWithoutDescriptions;

	private readonly List<PluginView> _plugins = new List<PluginView>();
	private readonly Dictionary<string, string> _draftValues = new Dictionary<string, string>();
	private readonly HashSet<string> _dirtyValues = new HashSet<string>();
	private readonly Dictionary<string, string> _errors = new Dictionary<string, string>();

	private bool _visible;
	private int _selectedPlugin;
	private string _search = string.Empty;
	private Vector2 _pluginScroll;
	private Vector2 _settingsScroll;
	private Rect _windowRect;
	private bool _windowInitialized;
	private bool _previousCursorVisible;
	private CursorLockMode _previousCursorLock;
	private float _previousTimeScale;
	private bool _pausedByManager;
	private readonly List<EventSystem> _disabledEventSystems = new List<EventSystem>();
	private string _status = "F11 closes the manager";

	private GUIStyle _titleStyle;
	private GUIStyle _sectionStyle;
	private GUIStyle _descriptionStyle;
	private GUIStyle _selectedButtonStyle;
	private GUIStyle _errorStyle;
	private GUIStyle _fallbackMenuButtonStyle;
	private GUIStyle _profilePanelStyle;
	private GUIStyle _profileHeadingStyle;
	private GUIStyle _collapsibleHeaderStyle;
	private GUIStyle _activeProfileButtonStyle;
	private GUIStyle _modeBadgeStyle;
	private GUIStyle _modeBadgeTitleStyle;
	private GUIStyle _modeBadgeValueStyle;
	private ConfigEntryBase _gameplayProfileEntry;
	private bool _confirmGalleryReset;
	private readonly HashSet<string> _collapsedPanels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	private MainMenuManager _mainMenu;
	private GameObject _mainMenuButton;
	private float _nextMainMenuCheck;
	private bool _showFallbackMenuButton;
	private bool _menuButtonFailureLogged;
	private static readonly FieldInfo FirstSelectedField = typeof(MainMenuManager).GetField("firstSelected", BindingFlags.Instance | BindingFlags.NonPublic);
	private static readonly FieldInfo CameraAnimatorField = typeof(MainMenuManager).GetField("cameraAnimator", BindingFlags.Instance | BindingFlags.NonPublic);
	private static readonly FieldInfo ItemUnlocksScreenField = typeof(MainMenuManager).GetField("itemUnlocksScreen", BindingFlags.Instance | BindingFlags.NonPublic);
	private static readonly FieldInfo ItemUnlocksPanelField = typeof(ItemUnlocksScreenUI).GetField("screenPanel", BindingFlags.Instance | BindingFlags.NonPublic);

	private void Awake()
	{
        InstallModalInputGuard();
		_toggleShortcut = Config.Bind(
			"Interface",
			"ToggleShortcut",
			new KeyboardShortcut(KeyCode.F11),
			"Keyboard shortcut that opens or closes the in-game mod manager.");
		_pauseWhileOpen = Config.Bind(
			"Interface",
			"PauseWhileOpen",
			true,
			"Pause Time.timeScale while the manager is open. Disable this if a mod needs time to continue running while settings are edited.");
		_showSettingsWithoutDescriptions = Config.Bind(
			"Interface",
			"ShowUndocumentedSettings",
			true,
			"Show configuration entries that do not provide a description.");

		Logger.LogInfo("PNC Mod Manager loaded. Press " + _toggleShortcut.Value + " to configure loaded BepInEx mods.");
	}

	private void Update()
	{
        TickBackgroundInputRestore();
        TickUpdater();
        TickMainMenuUpdateNotice();
        NativeTypeface.Tick();
        if(TickStandaloneNotesInput()) return;
		TickManagerNavigation();
        if (UpdateControls()) return;
		UpdateMainMenuButton();
		try
		{
			if (PncFramework.Controls.ControlService.IsDown(_toggleShortcut, true))
			{
				SetVisible(!_visible);
			}
		}
		catch (Exception exception)
		{
			Logger.LogWarning("Could not read the mod-manager shortcut: " + exception.Message);
		}
	}

	private void LateUpdate()
	{
		if(_visible || _patchNotesOpen) { Cursor.visible=true;Cursor.lockState=CursorLockMode.None; }
	}

	private void UpdateMainMenuButton()
	{
		if (Time.unscaledTime < _nextMainMenuCheck)
		{
			return;
		}

		_nextMainMenuCheck = Time.unscaledTime + 0.5f;
		MainMenuManager current = UnityEngine.Object.FindAnyObjectByType<MainMenuManager>();
		if (current == null || !current.isActiveAndEnabled)
		{
			_mainMenu = null;
			_mainMenuButton = null;
			_showFallbackMenuButton = false;
			return;
		}

		_mainMenu = current;
		if (_mainMenuButton == null)
		{
			CreateMainMenuButton();
		}
	}

	private void CreateMainMenuButton()
	{
		try
		{
			GameObject template = FirstSelectedField?.GetValue(_mainMenu) as GameObject;
			if (template == null || template.transform.parent == null)
			{
				throw new InvalidOperationException("The main menu's first button was not available.");
			}

			_mainMenuButton = UnityEngine.Object.Instantiate(template, template.transform.parent, false);
			_mainMenuButton.name = "PNC Mod Manager Button";
			_mainMenuButton.transform.SetAsLastSibling();

			Button[] buttons = _mainMenuButton.GetComponentsInChildren<Button>(true);
			if (buttons.Length == 0)
			{
				throw new InvalidOperationException("The main-menu button template contains no Unity UI Button.");
			}
			foreach (Button button in buttons)
			{
				// Remove the template's serialized actions as well as its runtime listeners.
				// RemoveAllListeners() alone leaves persistent calls such as StartGame intact.
				button.onClick = new Button.ButtonClickedEvent();
				button.onClick.AddListener(OpenFromMainMenu);
				Navigation navigation = button.navigation;
				navigation.mode = Navigation.Mode.Automatic;
				button.navigation = navigation;
			}

			foreach (TMP_Text label in _mainMenuButton.GetComponentsInChildren<TMP_Text>(true))
			{
				label.text = "MOD MANAGER";
			}
			foreach (Text label in _mainMenuButton.GetComponentsInChildren<Text>(true))
			{
				label.text = "MOD MANAGER";
			}

			PositionClonedButton(template, _mainMenuButton);
			_showFallbackMenuButton = false;
			Logger.LogInfo("Added Mod Manager button to the main menu.");
		}
		catch (Exception exception)
		{
			if (_mainMenuButton != null)
			{
				UnityEngine.Object.Destroy(_mainMenuButton);
				_mainMenuButton = null;
			}
			_showFallbackMenuButton = true;
			if (!_menuButtonFailureLogged)
			{
				_menuButtonFailureLogged = true;
				Logger.LogWarning("Could not clone the native main-menu button; using the fallback button: " + exception.Message);
			}
		}
	}

	private static void PositionClonedButton(GameObject template, GameObject clone)
	{
		Transform parent = clone.transform.parent;
		if (parent.GetComponent<LayoutGroup>() != null)
		{
			return;
		}

		RectTransform cloneRect = clone.GetComponent<RectTransform>();
		RectTransform templateRect = template.GetComponent<RectTransform>();
		if (cloneRect == null || templateRect == null)
		{
			return;
		}

		float lowestY = templateRect.anchoredPosition.y;
		foreach (Transform sibling in parent)
		{
			if (sibling == clone.transform || sibling.GetComponentInChildren<Button>(true) == null)
			{
				continue;
			}
			if (sibling is RectTransform siblingRect)
			{
				lowestY = Mathf.Min(lowestY, siblingRect.anchoredPosition.y);
			}
		}
		cloneRect.anchoredPosition = new Vector2(templateRect.anchoredPosition.x, lowestY - Mathf.Max(8f, templateRect.rect.height + 8f));
	}

	private void OpenFromMainMenu()
	{
		SetVisible(true);
	}

	private void OnDestroy()
	{
		if(_patchNotesOpen)ClosePatchNotes();
        DestroyUpdateNotice();
        DestroyDashboardTheme();
        DestroyUpdateArrowTexture();
		if (_visible)
		{
			SetVisible(false);
		}
		OnControlsVisibility(false);
        RestoreBackgroundUiInput();
        _backgroundRestoreFrame=-1;
        RemoveModalInputGuard();
		IsOpen = false;
	}

	private bool _captureAtManagerOpen;
	private static bool CaptureIsActive() => (GrabScreen.Instance != null && GrabScreen.Instance.IsGrabbed)
		|| (GrappleScreenobject.Instance != null && GrappleScreenobject.Instance.IsGrappling);
	private void SetVisible(bool visible)
	{
		if (_visible == visible)
		{
			return;
		}

		_visible = visible;
		IsOpen = visible;
		if(visible)OnControlsVisibility(true);
		if (visible)
		{
			RefreshPlugins();
			DisableBackgroundUiInput();
			_previousCursorVisible = Cursor.visible;
			_captureAtManagerOpen = CaptureIsActive();
			_previousCursorLock = Cursor.lockState;
			_previousTimeScale = Time.timeScale;
			Cursor.visible = true;
			Cursor.lockState = CursorLockMode.None;
			if (_pauseWhileOpen.Value && Time.timeScale > 0f)
			{
				Time.timeScale = 0f;
				_pausedByManager = true;
			}
			_status = "Changes are applied live and saved to BepInEx/config";
		}
		else
		{
			_backgroundRestoreFrame=Time.frameCount;
			Cursor.visible = _previousCursorVisible;
			Cursor.lockState = _previousCursorLock;
			var pause = UnityEngine.Object.FindAnyObjectByType<PixelCrushers.GridController.PauseMenuManager>();
			var inventory = UnityEngine.Object.FindAnyObjectByType<PixelCrushers.GridController.InventoryUI>();
			var player = UnityEngine.Object.FindAnyObjectByType<PlayerStats>();
			if(_captureAtManagerOpen && !CaptureIsActive() && player!=null && player.isActiveAndEnabled && pause!=null && pause.IsPaused)
			{
				// Native pause also saved the now-ended capture's cursor state.
				// Repair its resume bookmark without stealing the cursor from its UI.
				var bookmark=HarmonyLib.Traverse.Create(pause);
				if(bookmark.Field("previousCursorVisible").FieldExists() && bookmark.Field("previousCursorLockState").FieldExists())
				{
					bookmark.Field("previousCursorVisible").SetValue(false);
					bookmark.Field("previousCursorLockState").SetValue(CursorLockMode.Locked);
					Logger.LogInfo("[CURSOR] Capture ended under pause; repaired gameplay resume bookmark");
				}
			}
			if(CursorRestorePolicy.UseGameplayCursor(_captureAtManagerOpen,CaptureIsActive(),player!=null && player.isActiveAndEnabled,
				pause!=null && pause.IsPaused,inventory!=null && inventory.IsInventoryOpen,Application.isFocused))
			{
				Cursor.visible=false;
				Cursor.lockState=CursorLockMode.Locked;
				Logger.LogInfo("[CURSOR] Capture ended while manager open; restored gameplay cursor");
			}
			if (_pausedByManager && Time.timeScale == 0f)
			{
				Time.timeScale = _previousTimeScale;
			}
			_pausedByManager = false;
		}
	}

	private void DisableBackgroundUiInput()
	{
		_backgroundRestoreFrame=-1;
        _nativeActionsRestored=false;
        if(_disabledEventSystems.Count>0)return;
		foreach (EventSystem eventSystem in UnityEngine.Object.FindObjectsByType<EventSystem>(FindObjectsSortMode.None))
		{
			if (eventSystem.enabled)
			{
				eventSystem.enabled = false;
				_disabledEventSystems.Add(eventSystem);
			}
		}
	}

    int _backgroundRestoreFrame=-1;
    bool _nativeActionsRestored;
    void TickBackgroundInputRestore(){
        if(_backgroundRestoreFrame<0 || _visible || _patchNotesOpen)return;
        var mouse=UnityEngine.InputSystem.Mouse.current;var keyboard=UnityEngine.InputSystem.Keyboard.current;var pad=UnityEngine.InputSystem.Gamepad.current;
        bool held=(mouse!=null && (mouse.leftButton.isPressed || mouse.rightButton.isPressed || mouse.middleButton.isPressed))
            || (keyboard!=null && (keyboard.enterKey.isPressed || keyboard.spaceKey.isPressed || keyboard.escapeKey.isPressed))
            || (pad!=null && (pad.buttonSouth.isPressed || pad.buttonEast.isPressed));
        if(held){_backgroundRestoreFrame=Time.frameCount;return;}
        if(!OverlayInputPolicy.CanRestore(_backgroundRestoreFrame,Time.frameCount,false))return;
        if(!_nativeActionsRestored){
            OnControlsVisibility(false);_nativeActionsRestored=true;_backgroundRestoreFrame=Time.frameCount;
            Logger.LogInfo("[UI-MODAL] Closing input released; native actions restored with menu still blocked");return;
        }
        RestoreBackgroundUiInput();_backgroundRestoreFrame=-1;
        Logger.LogInfo("[UI-MODAL] Menu input restored after native action settling");
    }
	private void RestoreBackgroundUiInput()
	{
		foreach (EventSystem eventSystem in _disabledEventSystems)
		{
			if (eventSystem != null)
			{
				eventSystem.enabled = true;
			}
		}
		_disabledEventSystems.Clear();
	}

	private void RefreshPlugins()
	{
		string selectedGuid = _plugins.Count > 0 && _selectedPlugin < _plugins.Count
			? _plugins[_selectedPlugin].Info.Metadata.GUID
			: null;

		_plugins.Clear();
		BaseChainloader<BaseUnityPlugin> chainloader = UnityChainloader.Instance;
		if (chainloader != null)
		{
			foreach (PluginInfo info in chainloader.Plugins.Values
				.OrderBy(info => info.Metadata.GUID.Equals(PncEditPluginGuid, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
				.ThenBy(info => info.Metadata.Name, StringComparer.OrdinalIgnoreCase))
			{
				BaseUnityPlugin instance = info.Instance as BaseUnityPlugin;
				if (instance != null)
				{
					_plugins.Add(new PluginView(info, instance.Config));
					PncFramework.Controls.ControlService.RegisterConfig(instance.Config);
				}
			}
		}

		_selectedPlugin = 0;
		if (selectedGuid != null)
		{
			int oldSelection = _plugins.FindIndex(view => view.Info.Metadata.GUID == selectedGuid);
			if (oldSelection >= 0)
			{
				_selectedPlugin = oldSelection;
			}
		}
		_status = _plugins.Count + " loaded BepInEx mod(s) discovered";
	}

	private void OnGUI()
	{
        DrawGameplayUpdateNotice();
        if (_patchNotesOpen) { DrawPatchNotesWindow(); return; }
		if (!_visible)
		{
			if (_mainMenu != null && _mainMenu.isActiveAndEnabled)
			{
				if (IsMainMenuVisible())
				{
                    EnsureDashboardTheme();var menuSkin=GUI.skin;GUI.skin=_dashboardSkin;
					DrawGameModeBadge();
                    DrawMainMenuUpdateNotice();
                    GUI.skin=menuSkin;
				}
				if (_showFallbackMenuButton)
				{
					DrawFallbackMainMenuButton();
				}
			}
			return;
		}

		EnsureDashboardTheme();
		var previousSkin = GUI.skin; var previousMatrix = GUI.matrix;
		GUI.skin = _dashboardSkin;
		float scale = Mathf.Clamp(Screen.height / 1080f, 1f, 2f);
		float viewportWidth = Screen.width / scale, viewportHeight = Screen.height / scale;
		GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
		EnsureStyles();
		if (!_windowInitialized)
		{
			float width = Mathf.Min(1180f, Mathf.Max(760f, viewportWidth - 80f));
			float height = Mathf.Min(820f, Mathf.Max(520f, viewportHeight - 80f));
			_windowRect = new Rect((viewportWidth - width) / 2f, (viewportHeight - height) / 2f, width, height);
			_windowInitialized = true;
		}

		_windowRect.width = Mathf.Min(_windowRect.width, viewportWidth - 20f);
		_windowRect.height = Mathf.Min(_windowRect.height, viewportHeight - 20f);
		_windowRect.x = Mathf.Clamp(_windowRect.x, 0f, Mathf.Max(0f, viewportWidth - _windowRect.width));
		_windowRect.y = Mathf.Clamp(_windowRect.y, 0f, Mathf.Max(0f, viewportHeight - _windowRect.height));
		ParchmentArt.Slice("PNC_ModManager_Background.png",_windowRect,.32f);
        try { _windowRect = GUI.Window(GetInstanceID(), _windowRect, DrawWindow, ""); }
		finally { GUI.skin = previousSkin; GUI.matrix = previousMatrix; }
	}

	private bool IsMainMenuVisible()
	{
		GameObject firstButton = FirstSelectedField?.GetValue(_mainMenu) as GameObject;
		if (firstButton == null || !firstButton.activeInHierarchy)
		{
			return false;
		}

		Animator cameraAnimator = CameraAnimatorField?.GetValue(_mainMenu) as Animator;
		if (cameraAnimator != null &&
			(cameraAnimator.GetBool("Options") ||
			 cameraAnimator.GetBool("DioramaGallery") ||
			 cameraAnimator.GetBool("EnemyGallery")))
		{
			return false;
		}

		ItemUnlocksScreenUI itemUnlocksScreen = ItemUnlocksScreenField?.GetValue(_mainMenu) as ItemUnlocksScreenUI;
		GameObject itemUnlocksPanel = itemUnlocksScreen == null ? null : ItemUnlocksPanelField?.GetValue(itemUnlocksScreen) as GameObject;
		if (itemUnlocksScreen != null && itemUnlocksPanel == null)
		{
			itemUnlocksPanel = itemUnlocksScreen.gameObject;
		}
		return itemUnlocksPanel == null || !itemUnlocksPanel.activeInHierarchy;
	}

	private void DrawFallbackMainMenuButton()
	{
		EnsureStyles();
		float width = Mathf.Clamp(Screen.width * 0.18f, 190f, 280f);
		float height = Mathf.Clamp(Screen.height * 0.055f, 44f, 64f);
		Rect rect = new Rect(Screen.width - width - 24f, Screen.height - height - 24f, width, height);
		if (GUI.Button(rect, "MOD MANAGER  [" + _toggleShortcut.Value + "]", _fallbackMenuButtonStyle))
		{
			OpenFromMainMenu();
		}
	}

	private void DrawGameModeBadge()
	{
		ConfigEntryBase profile = GetGameplayProfileEntry();
		if (profile == null)
		{
			return;
		}

		EnsureStyles();
		Rect rect = new Rect(18f, 18f, 260f, 72f);
		GUI.Box(rect, GUIContent.none, _modeBadgeStyle);
		GUI.Label(new Rect(rect.x + 12f, rect.y + 8f, rect.width - 24f, 20f), "ACTIVE GAME MODE", _modeBadgeTitleStyle);
		Rect valueRect = new Rect(rect.x + 12f, rect.y + 29f, rect.width - 24f, 34f);
		string value = Nicify(profile.BoxedValue.ToString());
		GUIContent content = new GUIContent(value);
		int originalFontSize = _modeBadgeValueStyle.fontSize;
		while (_modeBadgeValueStyle.fontSize > 14 && _modeBadgeValueStyle.CalcSize(content).x > valueRect.width)
		{
			_modeBadgeValueStyle.fontSize--;
		}
		GUI.Label(valueRect, content, _modeBadgeValueStyle);
		_modeBadgeValueStyle.fontSize = originalFontSize;
	}

	private ConfigEntryBase GetGameplayProfileEntry()
	{
		if (_gameplayProfileEntry != null)
		{
			return _gameplayProfileEntry;
		}

		BaseChainloader<BaseUnityPlugin> chainloader = UnityChainloader.Instance;
		if (chainloader != null && chainloader.Plugins.TryGetValue("com.edi.pnc", out PluginInfo info))
		{
			BaseUnityPlugin plugin = info.Instance as BaseUnityPlugin;
			_gameplayProfileEntry = plugin?.Config.Values.FirstOrDefault(IsGameplayProfile);
		}
		return _gameplayProfileEntry;
	}

	private void EnsureStyles()
	{
		if (_titleStyle != null)
		{
			return;
		}

		_titleStyle = new GUIStyle(GUI.skin.label)
		{
			fontSize = 26,
			fontStyle = FontStyle.Normal,
			wordWrap = true
		};
		_sectionStyle = new GUIStyle(GUI.skin.box)
		{
			alignment = TextAnchor.MiddleLeft,
			fontStyle = FontStyle.Normal,
			fontSize = 18,
			stretchWidth = true
		};
		_descriptionStyle = new GUIStyle(GUI.skin.label)
		{
			wordWrap = true,
			fontSize = 18
		};
		_selectedButtonStyle = new GUIStyle(GUI.skin.button)
		{
			fontStyle = FontStyle.Normal,
			alignment = TextAnchor.MiddleLeft
		};
		_errorStyle = new GUIStyle(_descriptionStyle);
		_errorStyle.normal.textColor = new Color(.6f,.12f,.035f);
		_fallbackMenuButtonStyle = new GUIStyle(GUI.skin.button)
		{
			fontSize = 18,
			fontStyle = FontStyle.Normal,
			alignment = TextAnchor.MiddleCenter
		};
		_profilePanelStyle = new GUIStyle(GUI.skin.box)
		{
			padding = new RectOffset(12, 12, 10, 12)
		};
		_profileHeadingStyle = new GUIStyle(_sectionStyle)
		{
			alignment = TextAnchor.MiddleCenter,
			fontSize = 18
		};
		_collapsibleHeaderStyle = new GUIStyle(_profileHeadingStyle);
		_collapsibleHeaderStyle.hover.textColor = new Color(.46f,.19f,.025f);
		_collapsibleHeaderStyle.active.textColor = new Color(.33f,.10f,.01f);
		_activeProfileButtonStyle = new GUIStyle(GUI.skin.button)
		{
			fontStyle = FontStyle.Normal,
			fontSize = 18,
			alignment = TextAnchor.MiddleCenter
		};
		_activeProfileButtonStyle.normal.textColor = new Color(.25f,.09f,.015f);
		_activeProfileButtonStyle.hover.textColor = new Color(.46f,.19f,.025f);
		_modeBadgeStyle = new GUIStyle(GUI.skin.box)
		{
			padding = new RectOffset(12, 12, 8, 8)
		};
		_modeBadgeTitleStyle = new GUIStyle(GUI.skin.label)
		{
			fontSize = 18,
			fontStyle = FontStyle.Normal,
			alignment = TextAnchor.UpperLeft
		};
		_modeBadgeTitleStyle.normal.textColor = new Color(0.72f, 0.72f, 0.72f);
		_modeBadgeValueStyle = new GUIStyle(GUI.skin.label)
		{
			fontSize = 22,
			fontStyle = FontStyle.Normal,
			alignment = TextAnchor.MiddleLeft,
			wordWrap = false,
			clipping = TextClipping.Clip
		};
		_modeBadgeValueStyle.normal.textColor = new Color(.25f,.09f,.015f);
	}

	private void DrawWindow(int windowId)
	{
        BeginManagerNavigation();
        GUI.Box(new Rect(85,100,_windowRect.width-170,48),"");
		GUILayout.BeginVertical();
		GUILayout.BeginHorizontal();
		GUILayout.Label("PNC Framework", _titleStyle, GUILayout.Width(250f));
		if (NavButton("Settings", GUILayout.Width(110f))) _showControls = false;
		if (NavButton("Controls", GUILayout.Width(110f))) _showControls = true;
        
		GUILayout.FlexibleSpace();
		if (NavButton("Refresh", GUILayout.Width(90f)))
		{
			RefreshPlugins();
		}
		if (NavButton("Save all", GUILayout.Width(90f)))
		{
			SaveAll();
		}
		if (NavButton("Close (" + _toggleShortcut.Value + ")", GUILayout.Width(130f)))
		{
			SetVisible(false);
		}
		GUILayout.EndHorizontal();

		GUILayout.Space(5f);
		GUILayout.BeginHorizontal();
		if (_showControls) DrawControls();
		else { DrawPluginList(); GUILayout.Space(8f); DrawSettings(); }
		GUILayout.EndHorizontal();

		GUILayout.Space(4f);
		GUILayout.Label(_status, _descriptionStyle);
		GUILayout.EndVertical();

		GUI.DragWindow(new Rect(75f, 38f, _windowRect.width - 150f, 40f));
	}

	private void DrawPluginList()
	{
		GUILayout.BeginVertical(GUI.skin.box, GUILayout.Width(250f), GUILayout.ExpandHeight(true));
		_pluginScroll = GUILayout.BeginScrollView(_pluginScroll);
		for (int index = 0; index < _plugins.Count; index++)
		{
			PluginView plugin = _plugins[index];
			string label = ModuleTitle(plugin.Info.Metadata.GUID,plugin.Info.Metadata.Name) + "\n" + plugin.Info.Metadata.Version + "  |  " + plugin.Config.Count + " settings";
			GUIStyle style = index == _selectedPlugin ? _selectedButtonStyle : GUI.skin.button;
			if (NavButton(label, style, GUILayout.MinHeight(46f)))
			{
				_selectedPlugin = index;
				_settingsScroll = Vector2.zero;
				_search = string.Empty;
			}
		}
		GUILayout.EndScrollView();
		GUILayout.EndVertical();
	}

	private void DrawSettings()
	{
		GUILayout.BeginVertical(GUI.skin.box, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
		if (_plugins.Count == 0)
		{
			GUILayout.Label("No loaded BepInEx plugins were found.", _titleStyle);
			GUILayout.EndVertical();
			return;
		}

		_selectedPlugin = Mathf.Clamp(_selectedPlugin, 0, _plugins.Count - 1);
		PluginView plugin = _plugins[_selectedPlugin];
		GUILayout.Label(ModuleTitle(plugin.Info.Metadata.GUID,plugin.Info.Metadata.Name), _titleStyle);
		GUILayout.Label(ModuleDescription(plugin.Info.Metadata.GUID), _descriptionStyle);
		ConfigEntryBase profileEntry = plugin.Config.Values.FirstOrDefault(IsGameplayProfile);
		if (profileEntry != null)
		{
			DrawGameplayProfileSelector(plugin, profileEntry);
		}
		if (plugin.Info.Metadata.GUID.Equals(PncEditPluginGuid, StringComparison.OrdinalIgnoreCase))
		{
			DrawGalleryActions(plugin);
		}
		DrawCustomEnemiesPanel(plugin);
		GUILayout.BeginHorizontal();
		GUILayout.Label("Search", GUILayout.Width(55f));
		_search = GUILayout.TextField(_search ?? string.Empty);
		if (!string.IsNullOrEmpty(_search) && NavButton("Ã—", GUILayout.Width(28f)))
		{
			_search = string.Empty;
		}
		GUILayout.EndHorizontal();

		_settingsScroll = GUILayout.BeginScrollView(_settingsScroll, GUILayout.ExpandHeight(true));
		IEnumerable<ConfigEntryBase> visibleEntries = plugin.Config.Values
			.Where(entry => entry.SettingType != typeof(KeyboardShortcut) && entry.Definition.Key != "DebugHotkeysIgnoreHeldKeys")
			.Where(entry => !IsGameplayProfile(entry))
			.Where(entry => !IsCustomEnemyToggle(entry))
			.Where(EntryMatchesSearch)
			.Where(entry => _showSettingsWithoutDescriptions.Value || !string.IsNullOrWhiteSpace(entry.Description.Description))
			.OrderBy(entry => entry.Definition.Section, StringComparer.OrdinalIgnoreCase)
			.ThenBy(entry => entry.Definition.Key.Equals("Profile", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
			.ThenBy(entry => entry.Definition.Key, StringComparer.OrdinalIgnoreCase);

		string currentSection = null;
		int shown = 0;
		foreach (ConfigEntryBase entry in visibleEntries)
		{
			if (!string.Equals(currentSection, entry.Definition.Section, StringComparison.Ordinal))
			{
				currentSection = entry.Definition.Section;
				GUILayout.Space(shown == 0 ? 4f : 12f);
				GUILayout.Label(currentSection, _sectionStyle, GUILayout.Height(28f));
			}
			DrawEntry(plugin, entry);
			shown++;
		}

		if (shown == 0)
		{
			GUILayout.Label(plugin.Config.Count == 0 ? "This mod has no registered settings." : "No settings match the search.");
		}
		GUILayout.EndScrollView();
		GUILayout.EndVertical();
	}

	private static bool IsGameplayProfile(ConfigEntryBase entry)
	{
		return entry.SettingType.IsEnum
			&& entry.Definition.Section.Equals("Gameplay", StringComparison.OrdinalIgnoreCase)
			&& entry.Definition.Key.Equals("Profile", StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsCustomEnemyToggle(ConfigEntryBase entry)
	{
		return entry.SettingType == typeof(bool)
			&& entry.Definition.Section.Equals("Custom Enemies", StringComparison.OrdinalIgnoreCase);
	}

	private void DrawGalleryActions(PluginView plugin)
	{
		ConfigEntryBase unlockAll = plugin.Config.Values.FirstOrDefault(entry =>
			entry.SettingType == typeof(bool)
			&& entry.Definition.Section.Equals("Gallery", StringComparison.OrdinalIgnoreCase)
			&& entry.Definition.Key.Equals("UnlockAll", StringComparison.OrdinalIgnoreCase));
		if (unlockAll == null)
		{
			return;
		}

		bool enabled = (bool)unlockAll.BoxedValue;
		GUILayout.Space(8f);
		GUILayout.BeginVertical(_profilePanelStyle);
		if (DrawPanelHeader("GALLERY PROGRESS", plugin.Info.Metadata.GUID + "|Panel|Gallery", enabled ? "Everything unlocked" : "Normal progress"))
		{
			GUILayout.Label("Unlock all applies immediately. Reset disables it and clears saved gallery discoveries.", _descriptionStyle);
			GUILayout.BeginHorizontal();
			if (NavButton(enabled ? "Everything unlocked" : "Unlock everything", GUILayout.MinHeight(36f), GUILayout.ExpandWidth(true)))
			{
				string id = plugin.Info.Metadata.GUID + "|Gallery|UnlockAll";
				ApplyValue(unlockAll, true, id);
				RefreshGalleryDisplays();
				_confirmGalleryReset = false;
				_status = "All gallery entries are unlocked";
			}

			if (!_confirmGalleryReset)
			{
				if (NavButton("Reset unlocked", GUILayout.MinHeight(36f), GUILayout.ExpandWidth(true)))
				{
					_confirmGalleryReset = true;
				}
			}
			else
			{
				if (NavButton("Confirm reset", GUILayout.MinHeight(36f), GUILayout.ExpandWidth(true)))
				{
					ResetGalleryProgress(unlockAll, plugin);
				}
				if (NavButton("Cancel", GUILayout.Width(70f), GUILayout.MinHeight(36f)))
				{
					_confirmGalleryReset = false;
				}
			}
			GUILayout.EndHorizontal();
		}
		GUILayout.EndVertical();
	}

	private void ResetGalleryProgress(ConfigEntryBase unlockAll, PluginView plugin)
	{
		try
		{
			string id = plugin.Info.Metadata.GUID + "|Gallery|UnlockAll";
			ApplyValue(unlockAll, false, id);
			GalleryProgressManager progress = GalleryProgressManager.Instance;
			if (progress == null)
			{
				throw new InvalidOperationException("Gallery progress is not loaded yet. Return to the main menu and try again.");
			}
			if (progress.CheatUnlockAll)
			{
				progress.ToggleCheatUnlockAll();
			}
			progress.ResetAllProgress();
			RefreshGalleryDisplays();
			_confirmGalleryReset = false;
			_status = "Gallery unlock progress reset";
		}
		catch (Exception exception)
		{
			_status = "Could not reset gallery progress: " + exception.Message;
			Logger.LogError("Could not reset gallery progress: " + exception);
		}
	}

	private static void RefreshGalleryDisplays()
	{
		foreach (EnemyGalleryUI gallery in UnityEngine.Object.FindObjectsByType<EnemyGalleryUI>(FindObjectsInactive.Include, FindObjectsSortMode.None))
		{
			gallery.RefreshDisplay();
		}
		foreach (PeekScenesUI gallery in UnityEngine.Object.FindObjectsByType<PeekScenesUI>(FindObjectsInactive.Include, FindObjectsSortMode.None))
		{
			gallery.RefreshDisplay();
		}
		foreach (DioramaGalleryUI gallery in UnityEngine.Object.FindObjectsByType<DioramaGalleryUI>(FindObjectsInactive.Include, FindObjectsSortMode.None))
		{
			gallery.RefreshDisplay();
		}
	}

	private void DrawCustomEnemiesPanel(PluginView plugin)
	{
		// One switch per package, and for a package that ships code that switch is also the consent
		// (Â§167). Â§165's separate `<package> code` bool made two questions out of one, and play
		// found it the hard way: both packages looked enabled and did nothing until the second
		// switch was found. What is left here is the warning, drawn under a package whose
		// description says it ships code - this window references neither of the other assemblies,
		// so it reads that off the text the framework writes, the same way the titles are.
		List<ConfigEntryBase> packages = plugin.Config.Values
			.Where(IsCustomEnemyToggle)
			.OrderBy(entry => entry.Definition.Key, StringComparer.OrdinalIgnoreCase)
			.ToList();
		if (packages.Count == 0)
		{
			return;
		}

		int enabledCount = packages.Count(entry => (bool)entry.BoxedValue);
		int codeOff = packages.Count(entry => ShipsCode(entry) && !(bool)entry.BoxedValue);
		string summary = enabledCount + " of " + packages.Count + " enabled"
			+ (codeOff > 0 ? ", " + codeOff + " shipping code and switched off" : string.Empty);
		GUILayout.Space(8f);
		GUILayout.BeginVertical(_profilePanelStyle);
		if (DrawPanelHeader("CUSTOM ENEMIES", plugin.Info.Metadata.GUID + "|Panel|CustomEnemies", summary))
		{
			GUILayout.Label("Disabling cleans tracked package content now. Enabling requires a restart. Check each package's status below.", _descriptionStyle);
			foreach (ConfigEntryBase entry in packages)
			{
				string id = plugin.Info.Metadata.GUID + "|" + entry.Definition.Section + "|" + entry.Definition.Key;
				bool enabled = (bool)entry.BoxedValue;
				GUILayout.BeginVertical(GUI.skin.box);
				GUILayout.BeginHorizontal();
				GUILayout.BeginVertical(GUILayout.ExpandWidth(true));
				GUILayout.Label(CustomEnemyTitle(entry), _titleStyle);
				GUILayout.Label(entry.Definition.Key, _descriptionStyle);
				GUILayout.EndVertical();
				bool shipsCode = ShipsCode(entry);
				GUIStyle toggleStyle = enabled ? _activeProfileButtonStyle : GUI.skin.button;
				if (NavButton(enabled ? "Enabled" : "Disabled", toggleStyle, GUILayout.Width(120f), GUILayout.MinHeight(44f)))
				{
					ApplyValue(entry, !enabled, id);
					if (shipsCode)
					{
						_status = enabled ? "Cleanup requested for " + CustomEnemyTitle(entry) : "Restart to enable " + CustomEnemyTitle(entry);
					}
				}
				GUILayout.EndHorizontal();

				if (shipsCode)
				{
					// Said before it is turned on, not after: this is the disclosure Â§164 owes, and
					// a warning a player reads once the code is already running is not one.
					GUILayout.Label(enabled
						? "Runs its own code. Disabling requests immediate cleanup."
						: "Enabling runs this package's code and requires a restart.",
						_descriptionStyle);
				}
				try
				{
					var api=Type.GetType("PncCustomEnemies.Api.PackageRuntime, PncCustomEnemies");
					var status=api?.GetMethod("GetPackageStatus")?.Invoke(null,new object[]{entry.Definition.Key}) as string;
					if(!string.IsNullOrEmpty(status)) GUILayout.Label("Status: "+status,_descriptionStyle);
				}
				catch { GUILayout.Label("Package status unavailable",_descriptionStyle); }
				GUILayout.EndVertical();
			}
		}
		GUILayout.EndVertical();
	}

	/// <summary>
	/// Does this package ship a .NET assembly, so that enabling it runs third-party code? Read off
	/// the sentence `PackageAssemblies.CodeSwitchDescription` writes into the switch's description,
	/// because this assembly deliberately references neither of the others - convention, like the
	/// title parsed out of the same text. A package that ships no code has no such sentence and
	/// gets no warning.
	/// </summary>
	private static bool ShipsCode(ConfigEntryBase entry)
	{
		string description = entry.Description?.Description;
		return description != null
			&& description.IndexOf("ships its own code", StringComparison.OrdinalIgnoreCase) >= 0;
	}

	private static string CustomEnemyTitle(ConfigEntryBase entry)
	{
		string description = entry.Description.Description ?? string.Empty;
		if (description.StartsWith("Enable ", StringComparison.OrdinalIgnoreCase))
		{
			int end = description.IndexOf('.');
			if (end > 7)
			{
				return description.Substring(7, end - 7).Trim();
			}
		}
		return Nicify(entry.Definition.Key.Replace('_', ' '));
	}

	private bool DrawPanelHeader(string title, string key, string collapsedSummary)
	{
		bool collapsed = _collapsedPanels.Contains(key);
		string label = (collapsed ? "\u25BA " : "\u25BC ") + title;
		if (collapsed && !string.IsNullOrEmpty(collapsedSummary))
		{
			label += "  \u2022  " + collapsedSummary;
		}
		if (NavButton(label, _collapsibleHeaderStyle, GUILayout.Height(28f)))
		{
			if (collapsed)
			{
				_collapsedPanels.Remove(key);
			}
			else
			{
				_collapsedPanels.Add(key);
			}
			collapsed = !collapsed;
		}
		return !collapsed;
	}

	private void DrawGameplayProfileSelector(PluginView plugin, ConfigEntryBase entry)
	{
		string id = plugin.Info.Metadata.GUID + "|" + entry.Definition.Section + "|" + entry.Definition.Key;
		object current = entry.BoxedValue;

		GUILayout.Space(8f);
		GUILayout.BeginVertical(_profilePanelStyle);
		if (DrawPanelHeader("GAME MODE", plugin.Info.Metadata.GUID + "|Panel|GameMode", Nicify(current.ToString())))
		{
			GUILayout.Label("Current: " + Nicify(current.ToString()), _titleStyle);
			GUILayout.Label(GetProfileDescription(current.ToString()), _descriptionStyle);
			GUILayout.Space(6f);
			GUILayout.BeginHorizontal();
			foreach (object value in Enum.GetValues(entry.SettingType).Cast<object>().OrderBy(value => value.ToString() == "Custom" ? 1 : 0))
			{
				bool selected = Equals(value, current);
				GUIStyle style = selected ? _activeProfileButtonStyle : GUI.skin.button;
				string label = (selected ? "[ACTIVE]  " : string.Empty) + Nicify(value.ToString());
				if (NavButton(label, style, GUILayout.MinHeight(42f), GUILayout.ExpandWidth(true)) && !selected)
				{
					ApplyValue(entry, value, id);
				}
			}
			GUILayout.EndHorizontal();
		}
		GUILayout.EndVertical();
		GUILayout.Space(5f);
	}

	private static string GetProfileDescription(string profile)
	{
		switch (profile)
		{
			case "Vanilla":
				return "Original game combat, health, heat and spawning.";
			case "PressureAndRelease":
				return "Balanced pressure progression with persistent locks and stronger enemies.";
			case "GodMode":
				return "No damage or death; intended for exploration and gallery discovery.";
			case "Custom":
				return "Uses the detailed gameplay settings below.";
			default:
				return "Select the ruleset used for gameplay.";
		}
	}

	private bool EntryMatchesSearch(ConfigEntryBase entry)
	{
		if (string.IsNullOrWhiteSpace(_search))
		{
			return true;
		}

		string needle = _search.Trim();
		return Contains(entry.Definition.Section, needle)
			|| Contains(entry.Definition.Key, needle)
			|| Contains(entry.Description.Description, needle)
			|| Contains(entry.GetSerializedValue(), needle);
	}

	private static bool Contains(string haystack, string needle)
	{
		return !string.IsNullOrEmpty(haystack)
			&& haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
	}

	private void DrawEntry(PluginView plugin, ConfigEntryBase entry)
	{
		string id = plugin.Info.Metadata.GUID + "|" + entry.Definition.Section + "|" + entry.Definition.Key;
		GUILayout.BeginVertical(GUI.skin.box);
		GUILayout.BeginHorizontal();
		GUILayout.Label(entry.Definition.Key, GUILayout.Width(245f));

		if (entry.SettingType == typeof(bool))
		{
			bool oldValue = (bool)entry.BoxedValue;
			bool newValue = NavToggle(oldValue, oldValue ? "Enabled" : "Disabled", GUILayout.Width(110f));
			if (newValue != oldValue)
			{
				ApplyValue(entry, newValue, id);
			}
			GUILayout.FlexibleSpace();
		}
		else if (entry.SettingType.IsEnum)
		{
			object current = entry.BoxedValue;
			if (NavButton(Nicify(current.ToString()), GUILayout.MinWidth(180f)))
			{
				Array values = Enum.GetValues(entry.SettingType);
				int currentIndex = Array.IndexOf(values, current);
				ApplyValue(entry, values.GetValue((currentIndex + 1) % values.Length), id);
			}
			GUILayout.FlexibleSpace();
		}
		else
		{
			string serialized = entry.GetSerializedValue();
			if (!_draftValues.ContainsKey(id) || !_dirtyValues.Contains(id))
			{
				_draftValues[id] = serialized;
			}
			string oldDraft = _draftValues[id];
			string newDraft = GUILayout.TextField(oldDraft, GUILayout.MinWidth(180f));
			if (newDraft != oldDraft)
			{
				_draftValues[id] = newDraft;
				_dirtyValues.Add(id);
				_errors.Remove(id);
			}
			GUI.enabled = _dirtyValues.Contains(id);
			if (NavButton("Apply", GUILayout.Width(58f)))
			{
				ApplySerialized(entry, id);
			}
			GUI.enabled = true;
		}

		if (NavButton("Reset", GUILayout.Width(58f)))
		{
			ApplyValue(entry, entry.DefaultValue, id);
		}
		GUILayout.EndHorizontal();

		if (!string.IsNullOrWhiteSpace(entry.Description.Description))
		{
			GUILayout.Label(NativeTypeface.Clean(entry.Description.Description), _descriptionStyle);
		}
		GUILayout.Label("Type: " + entry.SettingType.Name + "  -  Default: " + SerializeDefault(entry), _descriptionStyle);
		if (_errors.TryGetValue(id, out string error))
		{
			GUILayout.Label(error, _errorStyle);
		}
		GUILayout.EndVertical();
	}

	private static string SerializeDefault(ConfigEntryBase entry)
	{
		try
		{
			return TomlTypeConverter.ConvertToString(entry.DefaultValue, entry.SettingType);
		}
		catch
		{
			return entry.DefaultValue?.ToString() ?? "null";
		}
	}

	private static string Nicify(string value)
	{
		if (string.IsNullOrEmpty(value))
		{
			return value;
		}
		System.Text.StringBuilder builder = new System.Text.StringBuilder(value.Length + 8);
		for (int index = 0; index < value.Length; index++)
		{
			char current = value[index];
			if (index > 0 && char.IsUpper(current) && !char.IsUpper(value[index - 1]))
			{
				builder.Append(' ');
			}
			builder.Append(current);
		}
		return builder.ToString();
	}

	private void ApplySerialized(ConfigEntryBase entry, string id)
	{
		try
		{
			object parsed = TomlTypeConverter.ConvertToValue(_draftValues[id], entry.SettingType);
			ApplyValue(entry, parsed, id);
		}
		catch (Exception exception)
		{
			_errors[id] = "Invalid value: " + exception.Message;
			_status = "Could not apply " + entry.Definition;
		}
	}

	private void ApplyValue(ConfigEntryBase entry, object value, string id)
	{
		try
		{
			entry.BoxedValue = value;
			entry.ConfigFile.Save();
			_draftValues[id] = entry.GetSerializedValue();
			_dirtyValues.Remove(id);
			_errors.Remove(id);
			_status = "Saved " + entry.Definition.Section + " / " + entry.Definition.Key;
			if (entry.Definition.Section.Equals("Gallery", StringComparison.OrdinalIgnoreCase)
				&& entry.Definition.Key.Equals("UnlockAll", StringComparison.OrdinalIgnoreCase))
			{
				RefreshGalleryDisplays();
			}
		}
		catch (Exception exception)
		{
			_errors[id] = "Could not save: " + exception.Message;
			_status = "Could not save " + entry.Definition;
			Logger.LogError("Could not save " + entry.Definition + ": " + exception);
		}
	}

	private void SaveAll()
	{
		int saved = 0;
		foreach (PluginView plugin in _plugins)
		{
			try
			{
				plugin.Config.Save();
				saved++;
			}
			catch (Exception exception)
			{
				Logger.LogError("Could not save " + plugin.Info.Metadata.Name + ": " + exception);
			}
		}
		_status = "Saved configuration files for " + saved + " mod(s)";
	}

	private sealed class PluginView
	{
		internal PluginInfo Info { get; }
		internal ConfigFile Config { get; }

		internal PluginView(PluginInfo info, ConfigFile config)
		{
			Info = info;
			Config = config;
		}
	}

}






