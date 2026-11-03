using UnityEngine;
using UnityEngine.UI;

namespace PncEdi;

/// <summary>
/// Fullscreen Screen-Space Overlay for Baphomet grab frames (gallery-style, no circular hole).
/// </summary>
internal sealed class BaphometGrabOverlay : MonoBehaviour
{
	private const float Brightness = 3.5f;

	private static BaphometGrabOverlay _instance;

	private static Material _brightUiMat;

	private static Material _additiveMat;

    Canvas heatCanvas;
    int nativeHeatOrder;
    bool nativeHeatOverride;
    bool heatLease,heatLookupAttempted;
    bool? observedPause;
    void EnsureHeatLayer() {
        // Shared capture HUD owns this lease in Overkeks; avoid competing restores.
        if(OverkeksMode.Active)return;
        if(!heatLease) {
            if(heatLookupAttempted)return;heatLookupAttempted=true;
            var grab=GrabScreen.Instance;if(grab==null)return;
            var container=grab.transform.root.Find("Health Panel/HeatContainer");
            if(container==null)return;
            heatCanvas=container.GetComponent<Canvas>();if(heatCanvas==null)return;
            nativeHeatOrder=heatCanvas.sortingOrder;nativeHeatOverride=heatCanvas.overrideSorting;heatLease=true;
            Plugin.DBG("CAPTURE-HUD","heat layer leased; originalOrder="+nativeHeatOrder+"; enabled="+heatCanvas.enabled+"; active="+heatCanvas.gameObject.activeInHierarchy);
        }
        if(heatCanvas!=null){heatCanvas.overrideSorting=true;heatCanvas.sortingOrder=CreatorVisualPolicy.CaptureHudSortingOrder;}
    }
    void RestoreHeatLayer() {
        if(heatLease && heatCanvas!=null){heatCanvas.sortingOrder=nativeHeatOrder;heatCanvas.overrideSorting=nativeHeatOverride;}
        heatCanvas=null;heatLease=false;heatLookupAttempted=false;observedPause=null;
    }
    void OnDisable()=>RestoreHeatLayer();

	internal Image Target;

	internal Image Backdrop;

	internal Image AdditiveBoost;

	internal SpriteRenderer Source;

	internal Image ContentSource;

	/// <summary>When false (Gravy/Minotaur), skip additive / HDR color boost — natural gallery colors.</summary>
	internal bool BoostBrightness = true;

	internal static BaphometGrabOverlay Ensure()
	{
		if (_instance != null)
		{
			_instance.gameObject.SetActive(value: true);
			return _instance;
		}
		GameObject root = OverkeksMode.NewOwned("BaphometGrabOverlay");
		UnityEngine.Object.DontDestroyOnLoad(root);
		Canvas canvas = OverkeksMode.AddOwnedComponent<Canvas>(root);
		canvas.renderMode = RenderMode.ScreenSpaceOverlay;
		canvas.sortingOrder = CreatorVisualPolicy.SceneSortingOrder;
		CanvasScaler scaler = OverkeksMode.AddOwnedComponent<CanvasScaler>(root);
		scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
		scaler.referenceResolution = new Vector2(1920f, 1080f);
		scaler.matchWidthOrHeight = 0.5f;
		OverkeksMode.AddOwnedComponent<GraphicRaycaster>(root);

		Image backdrop = CreateFullImage(root.transform, "Backdrop", new Color(0f, 0f, 0f, 0f));
		backdrop.enabled = false;
		Image frame = CreateFullImage(root.transform, "Frame", Color.white);
		frame.preserveAspect = false;
		frame.enabled = false;
		frame.material = GetBrightUiMaterial();
		Image boost = CreateFullImage(root.transform, "AdditiveBoost", new Color(1f, 1f, 1f, 0.55f));
		boost.preserveAspect = false;
		boost.material = GetAdditiveMaterial();
		boost.enabled = false;

		BaphometGrabOverlay overlay = OverkeksMode.AddOwnedComponent<BaphometGrabOverlay>(root);
		overlay.Backdrop = backdrop;
		overlay.Target = frame;
		overlay.AdditiveBoost = boost;
		_instance = overlay;
		return overlay;
	}

	private static Image CreateFullImage(Transform parent, string name, Color color)
	{
		GameObject go = OverkeksMode.NewOwned(name);
		go.transform.SetParent(parent, worldPositionStays: false);
		RectTransform rect = OverkeksMode.AddOwnedComponent<RectTransform>(go);
		rect.anchorMin = Vector2.zero;
		rect.anchorMax = Vector2.one;
		rect.offsetMin = Vector2.zero;
		rect.offsetMax = Vector2.zero;
		rect.localScale = Vector3.one;
		Image image = OverkeksMode.AddOwnedComponent<Image>(go);
		image.color = color;
		image.preserveAspect = false;
		image.raycastTarget = false;
		return image;
	}

	internal static Material GetBrightUiMaterial()
	{
		if (_brightUiMat != null)
		{
			return _brightUiMat;
		}
		Shader shader = Shader.Find("Sprites/Default") ?? Shader.Find("UI/Default");
		_brightUiMat = shader != null ? OverkeksMode.TrackOwned(new Material(shader)) : null;
		return _brightUiMat;
	}

	internal static Material GetAdditiveMaterial()
	{
		if (_additiveMat != null)
		{
			return _additiveMat;
		}
		Shader shader = Shader.Find("Mobile/Particles/Additive")
			?? Shader.Find("Particles/Additive")
			?? Shader.Find("Legacy Shaders/Particles/Additive");
		_additiveMat = shader != null ? OverkeksMode.TrackOwned(new Material(shader)) : null;
		return _additiveMat;
	}

	internal static Color BrightColor(float alpha = 1f)
	{
		return new Color(Brightness, Brightness, Brightness, alpha);
	}

	internal void ClearContentBinding()
	{
		Source = null;
		ContentSource = null;
		if (Target != null)
		{
			Target.enabled = false;
			Target.sprite = null;
		}
		if (AdditiveBoost != null)
		{
			AdditiveBoost.enabled = false;
			AdditiveBoost.sprite = null;
		}
		if (Backdrop != null)
		{
			Backdrop.enabled = false;
			Backdrop.sprite = null;
			Backdrop.color = new Color(0f, 0f, 0f, 0f);
		}
		gameObject.SetActive(value: true);
	}

	/// <summary>Leave transparent parts of scene artwork open to the existing game camera.</summary>
	internal void ShowWorldBackdrop()
	{
        EnsureHeatLayer();
		if (Backdrop == null)
		{
			return;
		}
		Backdrop.sprite = null;
		Backdrop.color = Color.clear;
		Backdrop.enabled = false;
		Backdrop.transform.SetAsFirstSibling();
	}

	internal void BindContent(Image content)
	{
		ContentSource = content;
		Source = null;
		if (Target != null)
		{
			Target.enabled = true;
			Target.preserveAspect = false;
			Target.material = BoostBrightness ? GetBrightUiMaterial() : null;
			Target.color = BoostBrightness ? BrightColor() : Color.white;
		}
		if (AdditiveBoost != null)
		{
			AdditiveBoost.enabled = BoostBrightness && content != null;
			AdditiveBoost.preserveAspect = false;
			AdditiveBoost.material = GetAdditiveMaterial();
			AdditiveBoost.color = new Color(1f, 1f, 1f, BoostBrightness ? 0.55f : 0f);
		}
		gameObject.SetActive(value: true);
		SyncFromContent();
	}

	internal void Bind(SpriteRenderer source)
	{
		Source = source;
		ContentSource = null;
		if (Target != null)
		{
			Target.enabled = true;
			Target.color = BoostBrightness ? BrightColor() : Color.white;
			Target.material = BoostBrightness ? GetBrightUiMaterial() : null;
			if (source != null && source.sprite != null)
			{
				Target.sprite = source.sprite;
			}
		}
		gameObject.SetActive(value: true);
	}

	internal static void Hide()
	{
		if (_instance != null)
		{
			_instance.Source = null;
			_instance.ContentSource = null;
			if (_instance.Target != null)
			{
				_instance.Target.sprite = null;
			}
			if (_instance.AdditiveBoost != null)
			{
				_instance.AdditiveBoost.sprite = null;
				_instance.AdditiveBoost.enabled = false;
			}
			_instance.gameObject.SetActive(value: false);
		}
	}

	private void LateUpdate()
	{
        var canvas=GetComponent<Canvas>();
        EnsureHeatLayer();
        bool paused=PauseHooks.SceneClockHeld || Time.timeScale<=.0001f;
        if(canvas!=null) {canvas.enabled=true;canvas.sortingOrder=paused ? CreatorVisualPolicy.PausedSceneSortingOrder : CreatorVisualPolicy.SceneSortingOrder;}
        if(observedPause!=paused) {
            observedPause=paused;
            Plugin.DBG("CAPTURE-HUD","paused="+paused+"; heatCanvas="+(heatCanvas!=null)+"; active="+(heatCanvas!=null && heatCanvas.gameObject.activeInHierarchy)+"; enabled="+(heatCanvas!=null && heatCanvas.enabled)+"; order="+(heatCanvas!=null ? heatCanvas.sortingOrder : -1));
        }
        if(paused)return; // retain the frozen frame behind native pause controls
		SyncFromContent();
	}

	private void SyncFromContent()
	{
		Sprite sprite = null;
		if (ContentSource != null && ContentSource.sprite != null)
		{
			sprite = ContentSource.sprite;
		}
		else if (Source != null && Source.sprite != null)
		{
			sprite = Source.sprite;
		}
		if (sprite == null || Target == null)
		{
			return;
		}
		if (Target.sprite != sprite)
		{
			Target.sprite = sprite;
		}
		Target.color = BoostBrightness ? BrightColor() : Color.white;
		Target.enabled = true;
		Target.preserveAspect = false;
		if (AdditiveBoost != null)
		{
			if (!BoostBrightness)
			{
				AdditiveBoost.enabled = false;
			}
			else
			{
				if (AdditiveBoost.sprite != sprite)
				{
					AdditiveBoost.sprite = sprite;
				}
				AdditiveBoost.enabled = true;
				AdditiveBoost.color = new Color(1f, 1f, 1f, 0.55f);
			}
		}
	}

	private void OnDestroy()
	{
        RestoreHeatLayer();
		if (_instance == this)
		{
			_instance = null;
		}
	}
}
