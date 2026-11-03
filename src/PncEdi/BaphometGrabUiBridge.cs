using UnityEngine;
using UnityEngine.UI;

namespace PncEdi;

/// <summary>
/// Copies animated SpriteRenderer frames onto the GrabScreen fullscreen UI Image.
/// Gallery_Baphomet_Grabbed drives world SpriteRenderers; grabImage is a UI Image that stays white without this bridge.
/// </summary>
internal sealed class BaphometGrabUiBridge : MonoBehaviour
{
	internal SpriteRenderer Source;

	internal Image Target;

	private void LateUpdate()
	{
		if (Source == null || Target == null)
		{
			return;
		}
		if (Source.sprite != null)
		{
			Target.sprite = Source.sprite;
		}
		Color color = Source.color;
		if (color.a < 0.01f)
		{
			color.a = 1f;
		}
		Target.color = color;
		Target.enabled = true;
		if (!Target.gameObject.activeSelf)
		{
			Target.gameObject.SetActive(value: true);
		}
	}
}
