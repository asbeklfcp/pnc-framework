using UnityEngine;
using UnityEngine.UI;

namespace PncEdi;

/// <summary>
/// Brightens the animated grab Image via stacked additive sprite copies.
/// UI Image.color multipliers above 1 are often clamped — additive layers actually raise luminance.
/// (HSV sprite rewrite caused whitescreen on atlas/compressed textures — do not replace sprites.)
/// </summary>
internal sealed class BaphometGrabBrightener : MonoBehaviour
{
	private const int AdditiveLayers = 3;

	private const float AdditiveAlpha = 0.85f;

	internal Image Target;

	private readonly Image[] _additives = new Image[AdditiveLayers];

	internal void EnsureAdditive()
	{
		if (Target == null)
		{
			Target = GetComponent<Image>();
		}
		if (Target == null)
		{
			return;
		}
		Transform parent = Target.transform.parent;
		int baseIndex = Target.transform.GetSiblingIndex();
		for (int i = 0; i < AdditiveLayers; i++)
		{
			if (_additives[i] != null)
			{
				continue;
			}
			GameObject go = OverkeksMode.NewOwned("AdditiveBoost" + i);
			go.transform.SetParent(parent, worldPositionStays: false);
			RectTransform rect = OverkeksMode.AddOwnedComponent<RectTransform>(go);
			rect.anchorMin = Vector2.zero;
			rect.anchorMax = Vector2.one;
			rect.offsetMin = Vector2.zero;
			rect.offsetMax = Vector2.zero;
			rect.SetSiblingIndex(baseIndex + 1 + i);
			Image image = OverkeksMode.AddOwnedComponent<Image>(go);
			image.preserveAspect = false;
			image.raycastTarget = false;
			image.material = BaphometGrabOverlay.GetAdditiveMaterial();
			image.color = new Color(1f, 1f, 1f, AdditiveAlpha);
			_additives[i] = image;
		}
	}

	private void LateUpdate()
	{
		if (Target == null)
		{
			Target = GetComponent<Image>();
		}
		if (Target == null)
		{
			return;
		}
		Target.enabled = true;
		Target.preserveAspect = false;
		Target.material = null;
		Target.color = Color.white;
		Sprite sprite = Target.sprite;
		for (int i = 0; i < AdditiveLayers; i++)
		{
			Image layer = _additives[i];
			if (layer == null)
			{
				continue;
			}
			if (sprite == null)
			{
				layer.enabled = false;
				continue;
			}
			if (layer.sprite != sprite)
			{
				layer.sprite = sprite;
			}
			layer.enabled = true;
			layer.preserveAspect = false;
			Material mat = BaphometGrabOverlay.GetAdditiveMaterial();
			if (mat != null)
			{
				layer.material = mat;
			}
			layer.color = new Color(1f, 1f, 1f, AdditiveAlpha);
			Stretch(layer.rectTransform);
		}
	}

	private static void Stretch(RectTransform rect)
	{
		if (rect == null)
		{
			return;
		}
		rect.anchorMin = Vector2.zero;
		rect.anchorMax = Vector2.one;
		rect.offsetMin = Vector2.zero;
		rect.offsetMax = Vector2.zero;
	}
}
