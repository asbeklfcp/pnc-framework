using System.Collections.Generic;
using UnityEngine;

namespace PncEdi;

/// <summary>
/// Per-pixel HSV saturation boost for UI sprites (cached). UI Image.color cannot raise chroma.
/// </summary>
internal static class BaphometSpriteSaturate
{
	private const float SatMultiplier = 1.65f;

	private static readonly Dictionary<int, Sprite> Cache = new Dictionary<int, Sprite>(64);

	private static readonly HashSet<int> SaturatedIds = new HashSet<int>();

	internal static Sprite Get(Sprite source)
	{
		if (source == null)
		{
			return null;
		}
		int id = source.GetInstanceID();
		if (SaturatedIds.Contains(id))
		{
			return source;
		}
		if (Cache.TryGetValue(id, out Sprite cached) && cached != null)
		{
			return cached;
		}
		Sprite built = Build(source);
		if (built == null)
		{
			return source;
		}
		Cache[id] = built;
		SaturatedIds.Add(built.GetInstanceID());
		return built;
	}

	private static Sprite Build(Sprite source)
	{
		Texture2D readable = CopySpriteRegion(source);
		if (readable == null)
		{
			return null;
		}
		Color[] pixels = readable.GetPixels();
		for (int i = 0; i < pixels.Length; i++)
		{
			Color c = pixels[i];
			if (c.a < 0.004f)
			{
				continue;
			}
			Color.RGBToHSV(c, out float h, out float s, out float v);
			s = Mathf.Clamp01(s * SatMultiplier);
			Color n = Color.HSVToRGB(h, s, v);
			n.a = c.a;
			pixels[i] = n;
		}
		readable.SetPixels(pixels);
		readable.Apply(updateMipmaps: false, makeNoLongerReadable: false);
		Vector2 pivot = new Vector2(
			source.pivot.x / Mathf.Max(1f, source.rect.width),
			source.pivot.y / Mathf.Max(1f, source.rect.height));
		return OverkeksMode.TrackOwned(Sprite.Create(
			readable,
			new Rect(0f, 0f, readable.width, readable.height),
			pivot,
			source.pixelsPerUnit));
	}

	private static Texture2D CopySpriteRegion(Sprite sprite)
	{
		Texture2D src = sprite.texture;
		if (src == null)
		{
			return null;
		}
		Rect rect = sprite.textureRect;
		int w = Mathf.Max(1, Mathf.RoundToInt(rect.width));
		int h = Mathf.Max(1, Mathf.RoundToInt(rect.height));
		RenderTexture rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
		RenderTexture prev = RenderTexture.active;
		try
		{
			float sx = rect.width / src.width;
			float sy = rect.height / src.height;
			float ox = rect.x / src.width;
			float oy = rect.y / src.height;
			Graphics.Blit(src, rt, new Vector2(sx, sy), new Vector2(ox, oy));
			RenderTexture.active = rt;
			Texture2D tex = OverkeksMode.TrackOwned(new Texture2D(w, h, TextureFormat.RGBA32, mipChain: false));
			tex.name = "BaphSat_" + sprite.name;
			tex.ReadPixels(new Rect(0f, 0f, w, h), 0, 0);
			tex.Apply(updateMipmaps: false, makeNoLongerReadable: false);
			return tex;
		}
		catch
		{
			return null;
		}
		finally
		{
			RenderTexture.active = prev;
			RenderTexture.ReleaseTemporary(rt);
		}
	}
}
