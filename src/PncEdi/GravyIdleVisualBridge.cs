using UnityEngine;
using UnityEngine.UI;

namespace PncEdi;

/// <summary>
/// Gallery_Gravy drives UI.Image.sprite (same binding style as grab). Copy those frames
/// onto a world SpriteRenderer so the walker shows the animated gallery Idle.
/// </summary>
internal sealed class GravyIdleVisualBridge : MonoBehaviour
{
	internal Image Source;

	internal SpriteRenderer Target;

	/// <summary>World Y of enemy feet — re-applied after each frame so scale/sprite changes stay planted.</summary>
	internal float GroundY;

	internal bool SnapFeetToGround;

	private void LateUpdate()
	{
		Sync();
	}

	internal void Sync()
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
		if (!SnapFeetToGround || Target.sprite == null)
		{
			return;
		}
		float dy = GroundY - Target.bounds.min.y;
		if (Mathf.Abs(dy) > 0.0001f)
		{
			transform.position += new Vector3(0f, dy, 0f);
		}
	}
}
