using System;
using BepInEx.Unity.Mono.Configuration;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace PncEdi;

internal static class SceneEscapeGate
{
	private static float _sceneStartedAt = -1f;
	private static bool _active;
	private static bool _lastWatchTimeSatisfied;
	private static float _escapeDelaySeconds;
	private static GameObject _hintOverlay;
	private static Text _hintText;
	private static Font _hintFont;
	private static bool _hintShowLogged;
	internal static bool GateEnabled => Plugin.GameplayTweaksEnabled && _escapeDelaySeconds > 0f;

	internal static bool CanEscape
	{
		get
		{
			if (!_active || !GateEnabled)
			{
				return true;
			}
			return Time.time >= _sceneStartedAt + _escapeDelaySeconds;
		}
	}

	internal static float RemainingSeconds
	{
		get
		{
			if (!_active || !GateEnabled)
			{
				return 0f;
			}
			return Mathf.Max(0f, _escapeDelaySeconds - (Time.time - _sceneStartedAt));
		}
	}

	internal static bool WatchTimeSatisfied => (!_active) ? _lastWatchTimeSatisfied : (!GateEnabled || CanEscape);


	internal static void BeginScene()
	{
		if (!_active)
		{
			_sceneStartedAt = Time.time;
			_escapeDelaySeconds = ResolveEscapeDelaySeconds();
			_active = true;
			_lastWatchTimeSatisfied = false;
		}
	}

	internal static void EndScene()
	{
		_lastWatchTimeSatisfied = !_active || !GateEnabled || CanEscape;
		_active = false;
		_sceneStartedAt = -1f;
		_escapeDelaySeconds = 0f;
		GalleryHooks.InGamePeekScene = false;
		HideHint();
	}

	internal static void Tick()
	{
		if (_active)
		{
			if (!IsFullHSceneActive())
			{
				EndScene();
			}
			else if (ShouldSuppressEscapeHint())
			{
				HideHint();
			}
			else
			{
				ShowHint(BuildHintText());
			}
		}
	}

	private static bool ShouldSuppressEscapeHint()
	{
		if (GalleryHooks.ViewingPeekScene || GalleryHooks.InGamePeekScene)
		{
			return true;
		}
		return IsPeekCameraSwapScene();
	}

	private static bool IsPeekCameraSwapScene()
	{
		if (!CameraSwapHooks.Active && !CameraSwapHooks.IsCinematicLive())
		{
			return false;
		}
		string galleryName = PeekGalleryMap.Resolve(CameraSwapHooks.GetCurrentGalleryId(), CameraSwapHooks.CurrentRawName, null);
		if (PeekGalleryMap.IsPeekScript(galleryName))
		{
			return true;
		}
		if (ContainsPeekToken(CameraSwapHooks.CurrentKey) || ContainsPeekToken(CameraSwapHooks.CurrentRawName))
		{
			return true;
		}
		return ContainsPeekToken(CameraSwapHooks.GetCurrentGalleryId());
	}

	private static bool ContainsPeekToken(string value)
	{
		return !string.IsNullOrEmpty(value) && value.IndexOf("peek", StringComparison.OrdinalIgnoreCase) >= 0;
	}

	private static float ResolveEscapeDelaySeconds()
	{
		if (!Plugin.GameplayTweaksEnabled)
		{
			return 0f;
		}
		GrabScreen grabScreen = GrabScreen.Instance;
		// A custom package sets its own minimum watch time in its manifest, and it wins over every
		// rule below: those are this mod's opinions about the game's own scenes, and a package's
		// scene is not one of them. Checked first for that reason, not for speed.
		if (grabScreen != null && GrabEndHelper.IsGrabSessionLive(grabScreen) && grabScreen.GrabbingEnemy != null)
		{
			// A package declares its own scene length in its manifest, and the witch and the wall
			// trap both use it. Asked through the bridge because that code is another plugin now.
			float packageMinimum = CustomEnemyBridge.SceneMinimumSeconds(grabScreen.GrabbingEnemy);
			if (packageMinimum >= 0f)
			{
				return packageMinimum;
			}
		}
		if (grabScreen != null && GrabEndHelper.IsGrabSessionLive(grabScreen) && grabScreen.GrabbingEnemy != null && GrabStruggleHooks.IsChaserBossGrabEnemy(grabScreen.GrabbingEnemy))
		{
			return Mathf.Max(0f, (OverkeksMode.UsesRecipe ? OverkeksConfig.CfgEndGrabDelaySecondsChaserBoss : Plugin.CfgEndGrabDelaySecondsChaserBoss).Value);
		}
		string currentKey = CameraSwapHooks.CurrentKey;
		if (!string.IsNullOrEmpty(currentKey) && (currentKey.Equals("dragon", StringComparison.OrdinalIgnoreCase) || currentKey.Equals("wendigo", StringComparison.OrdinalIgnoreCase)))
		{
			return Mathf.Max(0f, (OverkeksMode.UsesRecipe ? OverkeksConfig.CfgEndGrabDelaySecondsChaserBoss : Plugin.CfgEndGrabDelaySecondsChaserBoss).Value);
		}
		// The miniboss tier, between an ordinary grab and a chaser boss. Checked the same two
		// ways the chaser-boss tier above is - the live grab enemy first, then the interact
		// scene's key - so a Blinded Beast holds you for its own time whichever path is running.
		if (grabScreen != null && GrabEndHelper.IsGrabSessionLive(grabScreen) && grabScreen.GrabbingEnemy != null && HeatLockSystem.IsMinibossEnemy(grabScreen.GrabbingEnemy))
		{
			return Mathf.Max(0f, (OverkeksMode.UsesRecipe ? OverkeksConfig.CfgEndGrabDelaySecondsMiniboss : Plugin.CfgEndGrabDelaySecondsMiniboss).Value);
		}
		if (HeatLockSystem.IsMinibossKey(currentKey))
		{
			return Mathf.Max(0f, (OverkeksMode.UsesRecipe ? OverkeksConfig.CfgEndGrabDelaySecondsMiniboss : Plugin.CfgEndGrabDelaySecondsMiniboss).Value);
		}
		return Mathf.Max(0f, (OverkeksMode.UsesRecipe ? OverkeksConfig.CfgEndGrabDelaySeconds : Plugin.CfgEndGrabDelaySeconds).Value);
	}

	private static bool IsFullHSceneActive()
	{
		GrabScreen grabScreen = GrabScreen.Instance;
		if (grabScreen != null && GrabEndHelper.IsGrabSessionLive(grabScreen))
		{
			return true;
		}
		return CameraSwapHooks.IsCinematicLive();
	}

	internal static void HandleEscapeKeys()
	{
		if (!IsFullHSceneActive())
		{
			return;
		}
		if (IsDebugEscapePressed())
		{
			GrabEndHelper.TryEndActiveHScene(forceImmediate: true);
			EndScene();
			Plugin.DBG("ESCAPE", "debug instant (+)");
			return;
		}
		KeyboardShortcut keyEndGrab = Plugin.CfgKeyEndGrab.Value;
		if (PncFramework.Controls.ControlService.IsDown(Plugin.CfgKeyEndGrab))
		{
			if (!CanEscape)
			{
				Plugin.DBG("ESCAPE", $"blocked — {Mathf.CeilToInt(RemainingSeconds)}s left");
				return;
			}
			GrabEndHelper.TryEndActiveHScene(forceImmediate: true);
			EndScene();
		}
	}

	private static bool IsDebugEscapePressed()
	{
		return PncFramework.Controls.ControlService.IsDown(Plugin.CfgKeyEndGrabDebug);
	}

	internal static string BuildHintText()
	{
		string binding = PncFramework.Controls.ControlService.Display(Plugin.CfgKeyEndGrab);
		if (string.IsNullOrEmpty(binding)) return "Scene exit is unbound; open Controls in the mod manager.";
		return "Press " + binding + " to escape" + ((!GateEnabled || CanEscape) ? "" : " in " + Mathf.CeilToInt(RemainingSeconds) + "s");
	}

	internal static string AppendHintLine(string baseText)
	{
		if (ShouldSuppressEscapeHint())
		{
			return baseText ?? string.Empty;
		}
		if (string.IsNullOrEmpty(baseText))
		{
			return BuildHintText();
		}
		return baseText + "\n" + BuildHintText();
	}

	private static void EnsureHintOverlay()
	{
		if (_hintText != null)
		{
			return;
		}
		if (_hintFont == null)
		{
			_hintFont = ResolveHintFont();
		}
		_hintOverlay = new GameObject("PncEdi_EscapeHint");
		Object.DontDestroyOnLoad((Object)(object)_hintOverlay);
		Canvas canvas = _hintOverlay.AddComponent<Canvas>();
		canvas.renderMode = (RenderMode)0;
		canvas.sortingOrder = CreatorVisualPolicy.EscapeHintSortingOrder;
		CanvasScaler canvasScaler = _hintOverlay.AddComponent<CanvasScaler>();
		canvasScaler.uiScaleMode = (CanvasScaler.ScaleMode)1;
		canvasScaler.referenceResolution = new Vector2(1920f, 1080f);
		_hintOverlay.AddComponent<GraphicRaycaster>();
		GameObject gameObject = new GameObject("Text");
		gameObject.transform.SetParent(_hintOverlay.transform, false);
		_hintText = gameObject.AddComponent<Text>();
		_hintText.font = _hintFont;
		_hintText.fontSize = 22;
		((Graphic)_hintText).color = Color.white;
		_hintText.alignment = (TextAnchor)7;
		_hintText.horizontalOverflow = (HorizontalWrapMode)1;
		_hintText.verticalOverflow = (VerticalWrapMode)1;
		Shadow shadow = gameObject.AddComponent<Shadow>();
		shadow.effectColor = new Color(0f, 0f, 0f, 0.85f);
		shadow.effectDistance = new Vector2(1f, -1f);
		RectTransform rectTransform = ((Graphic)_hintText).rectTransform;
		rectTransform.anchorMin = new Vector2(0f, 0f);
		rectTransform.anchorMax = new Vector2(1f, 0f);
		rectTransform.pivot = new Vector2(0.5f, 0f);
		rectTransform.sizeDelta = new Vector2(0f, 48f);
		rectTransform.anchoredPosition = new Vector2(0f, 28f);
		_hintOverlay.SetActive(false);
	}

	// The escape hint is a legacy uGUI Text, and a Text with no font draws nothing at all — no
	// error, no warning, an empty screen while every gate behind it is working. That is what
	// 0.3.1 produced: Q released the grab on schedule and the countdown was never on screen, so
	// the failure looked like the gate and was the font.
	//
	// `Resources.GetBuiltinResource<Font>` only returns a font a player build actually contains,
	// and this one is Unity 6000.3 — so ask the game for the font it is already drawing its own
	// grab text with, and keep the builtin names as the fallback rather than the first choice.
	private static Font ResolveHintFont()
	{
		GrabScreen grabScreen = GrabScreen.Instance;
		if (grabScreen != null)
		{
			Text grabText = grabScreen.GrabText;
			if (grabText != null && grabText.font != null)
			{
				Plugin.DBG("HINT", "font from the game's grab text: " + grabText.font.name);
				return grabText.font;
			}
		}
		Font builtin = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
		if (builtin == null)
		{
			builtin = Resources.GetBuiltinResource<Font>("Arial.ttf");
		}
		if (builtin != null)
		{
			Plugin.DBG("HINT", "font from Resources.GetBuiltinResource: " + builtin.name);
			return builtin;
		}
		// Anything the build has loaded will do; a wrong-looking hint beats an invisible one.
		Font[] loaded = Resources.FindObjectsOfTypeAll<Font>();
		foreach (Font font in loaded)
		{
			if (font != null)
			{
				Plugin.DBG("HINT", "font from the loaded set: " + font.name);
				return font;
			}
		}
		Plugin.DBG("HINT", "no font found - the escape hint cannot draw, though the gate still works");
		return null;
	}

	private static void ShowHint(string message)
	{
		if (string.IsNullOrEmpty(message))
		{
			HideHint();
			return;
		}
		EnsureHintOverlay();
		// The font can only be asked for once the grab screen exists, and EnsureHintOverlay may
		// have built the overlay before that. Re-ask while it is still missing rather than living
		// with an invisible hint for the rest of the run.
		if (_hintText.font == null)
		{
			_hintFont = ResolveHintFont();
			_hintText.font = _hintFont;
		}
		_hintText.text = message;
		_hintOverlay.SetActive(true);
		if (!_hintShowLogged)
		{
			_hintShowLogged = true;
			// Once per run: it separates "never asked to draw" from "asked, with a font" if the
			// hint is still not on screen — which is the distinction the last session did not have.
			Plugin.DBG("HINT", "escape hint drawn: \"" + message + "\" font="
				+ ((_hintText.font != null) ? _hintText.font.name : "null"));
			// Where it landed, not just that it was drawn (§76).
			Plugin.DBG("HINT", ScreenDiag.Describe());
			Plugin.DBG("HINT", ScreenDiag.DescribeRect(((Graphic)_hintText).rectTransform));
		}
	}

	private static void HideHint()
	{
		if (_hintOverlay != null)
		{
			_hintOverlay.SetActive(false);
		}
	}
}
