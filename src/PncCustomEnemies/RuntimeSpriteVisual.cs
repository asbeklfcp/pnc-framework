using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace PncEdi;

// Sprites and the other fields are plain UnityEngine object references, so the whole animation
// set survives Object.Instantiate: spawned enemies are clones of the prepared template, and Unity
// copies only serialized fields. Without these attributes every clone would lose its renderer and
// frames and fall back to a frozen first frame.
[Serializable]
internal sealed class RuntimeSpriteAnimationData
{
	[SerializeField]
	internal string Name;

	[SerializeField]
	internal Sprite[] Frames;

	[SerializeField]
	internal float Fps;

	[SerializeField]
	internal bool Loop;

	[SerializeField]
	internal string[] MatchKeys;
}

internal sealed class RuntimeSpriteVisual : MonoBehaviour
{
	[SerializeField]
	private SpriteRenderer _renderer;

	[SerializeField]
	private SpriteRenderer _mirrorSource;

	[SerializeField]
	private SpriteRenderer[] _hiddenRenderers = Array.Empty<SpriteRenderer>();

	[SerializeField]
	private Animator _animator;

	[SerializeField]
	private bool _continuous;

	[SerializeField]
	private RuntimeSpriteAnimationData[] _animations;

	[SerializeField]
	private int _defaultIndex;

	[SerializeField]
	private CustomEnemyRuntimeData _data;

	private int _currentIndex = -1;
	private int _frame;
	private float _elapsed;
	private string _lastSourceClip = "";

	internal static void Attach(GameObject root, string packageDirectory, CustomEnemySpriteVisual settings, string enemyId)
	{
		if (root == null || settings == null || settings.animations == null || settings.animations.Length == 0) return;
		SpriteRenderer source = FindSourceRenderer(root, settings.renderer);
		if (source == null) throw new InvalidDataException("spriteVisual could not find a SpriteRenderer");

		List<RuntimeSpriteAnimationData> loaded = new List<RuntimeSpriteAnimationData>();
		foreach (CustomEnemySpriteAnimation animation in settings.animations)
		{
			if (animation == null || string.IsNullOrWhiteSpace(animation.name) || string.IsNullOrWhiteSpace(animation.file))
				throw new InvalidDataException("spriteVisual animations require name and file");
			loaded.Add(LoadAnimation(packageDirectory, settings, animation));
		}

		GameObject visualObject = new GameObject("RuntimeSpriteVisual_" + enemyId);
		visualObject.transform.SetParent(source.transform, false);
		visualObject.transform.localPosition = ParseVector3(settings.offset, Vector3.zero) + new Vector3(0f, GroundAlignY(source, loaded, settings), 0f);
		visualObject.transform.localScale = ParseVector3(settings.scale, Vector3.one);
		SpriteRenderer renderer = visualObject.AddComponent<SpriteRenderer>();
		renderer.sharedMaterial = source.sharedMaterial;
		renderer.sortingLayerID = source.sortingLayerID;
		renderer.sortingOrder = source.sortingOrder;
		renderer.color = source.color;
		renderer.maskInteraction = source.maskInteraction;

		List<SpriteRenderer> hidden = new List<SpriteRenderer>();
		if (settings.hideOriginalRenderers)
		{
			foreach (SpriteRenderer original in root.GetComponentsInChildren<SpriteRenderer>(true))
				if (original != renderer) { original.enabled = false; hidden.Add(original); }
		}

		RuntimeSpriteVisual driver = visualObject.AddComponent<RuntimeSpriteVisual>();
		driver._renderer = renderer;
		driver._mirrorSource = source;
		driver._hiddenRenderers = hidden.ToArray();
		driver._continuous = settings.continuous;
		driver._animator = root.GetComponentInChildren<Animator>(true);
		driver._animations = loaded.ToArray();
		driver._defaultIndex = FindAnimation(driver._animations, settings.defaultAnimation);
		if (driver._defaultIndex < 0) driver._defaultIndex = 0;
		driver._data = CustomEnemyRuntimeData.Find(enemyId);
		if (driver._data != null) driver._data.Animations = driver._animations;
		driver.Select(driver._defaultIndex, true);
		Plugin.Log?.LogInfo("[CustomEnemies] loaded " + loaded.Count + " runtime sprite animation(s) for '" + enemyId + "'");
	}

	internal void InitializeGallery(SpriteRenderer renderer, RuntimeSpriteAnimationData[] animations, int defaultIndex = 0)
	{
		_renderer = renderer;
		_animations = animations;
		_defaultIndex = defaultIndex >= 0 && defaultIndex < (animations?.Length ?? 0) ? defaultIndex : 0;
		_continuous = true;
		Select(_defaultIndex, true);
	}

	// Clones made by the game's spawners, the debug spawner and the gallery lose the custom-class
	// _animations field (see CustomEnemyRuntimeData), so it is restored from the runtime data
	// holder the moment a clone activates.
	private void OnEnable()
	{
		if (_data != null && (_animations == null || _animations.Length == 0))
		{
			_animations = _data.Animations;
			if (_data.SpriteVisual != null)
			{
				_continuous = _data.SpriteVisual.continuous;
				_defaultIndex = FindAnimation(_animations, _data.SpriteVisual.defaultAnimation);
			}
			if (_defaultIndex < 0) _defaultIndex = 0;
			_currentIndex = -1;
			Select(Mathf.Clamp(_defaultIndex, 0, _animations.Length - 1), restart: true);
			Plugin.DBG("CUSTOM", "restored " + _animations.Length + " sprite animation(s) from runtime data on a clone");
		}
		else if (_animations != null && _animations.Length > 0 && (_currentIndex < 0 || _currentIndex >= _animations.Length))
		{
			Select(Mathf.Clamp(_defaultIndex, 0, _animations.Length - 1), restart: true);
		}
	}

	private void Update()
	{
		if (_renderer == null || _animations == null || _animations.Length == 0) return;
		if (_mirrorSource != null)
		{
			_renderer.flipX = _mirrorSource.flipX;
			_renderer.flipY = _mirrorSource.flipY;
		}

		// The base enemy's animator keeps switching clips as it walks, attacks and flinches. A
		// continuous visual ignores that entirely: one loop plays uninterrupted for the enemy's
		// whole life, which is what a single-loop source animation needs to look right.
		foreach (SpriteRenderer hidden in _hiddenRenderers)
			if (hidden != null && hidden.enabled) hidden.enabled = false;

		if (!_continuous)
		{
			string sourceClip = CurrentClipName();
			if (!string.Equals(sourceClip, _lastSourceClip, StringComparison.OrdinalIgnoreCase))
			{
				_lastSourceClip = sourceClip;
				int next = MatchAnimation(sourceClip);
				if (next < 0) next = _defaultIndex;
				Select(next, next != _currentIndex);
			}
		}

		// The configured index is serialized with the component, but a clone created before this
		// fix or a partially applied template could still hold -1, which would index out of range.
		if (_currentIndex < 0 || _currentIndex >= _animations.Length)
		{
			Select(Mathf.Clamp(_defaultIndex, 0, _animations.Length - 1), restart: true);
		}

		RuntimeSpriteAnimationData current = _animations[_currentIndex];
		if (current.Frames.Length <= 1 || current.Fps <= 0f) return;
		// Unscaled, so the enemy keeps animating through whatever the game does to timeScale
		// during a grab - but not while the game is actually paused. See PauseHooks.SceneClockHeld.
		if (PauseHooks.SceneClockHeld) return;
		_elapsed += Time.unscaledDeltaTime;
		float frameDuration = 1f / current.Fps;
		while (_elapsed >= frameDuration)
		{
			_elapsed -= frameDuration;
			if (_frame + 1 < current.Frames.Length) _frame++;
			else if (current.Loop) _frame = 0;
			else { _elapsed = 0f; break; }
			_renderer.sprite = current.Frames[_frame];
		}
	}

	private void Select(int index, bool restart)
	{
		if (index < 0 || index >= _animations.Length) return;
		if (_currentIndex == index && !restart) return;
		_currentIndex = index;
		_frame = 0;
		_elapsed = 0f;
		_renderer.sprite = _animations[index].Frames[0];
	}

	private string CurrentClipName()
	{
		if (_animator == null || !_animator.isActiveAndEnabled) return "";
		AnimatorClipInfo[] clips = _animator.GetCurrentAnimatorClipInfo(0);
		return clips != null && clips.Length > 0 && clips[0].clip != null ? clips[0].clip.name : "";
	}

	private int MatchAnimation(string sourceClip)
	{
		if (string.IsNullOrWhiteSpace(sourceClip)) return -1;
		string key = Normalize(sourceClip);
		for (int i = 0; i < _animations.Length; i++)
		{
			foreach (string match in _animations[i].MatchKeys)
				if (key.IndexOf(match, StringComparison.OrdinalIgnoreCase) >= 0) return i;
		}
		return -1;
	}

	internal static RuntimeSpriteAnimationData LoadAnimation(string directory, CustomEnemySpriteVisual visual, CustomEnemySpriteAnimation animation)
	{
		string path = Path.GetFullPath(Path.Combine(directory, animation.file));
		string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
		if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
			throw new FileNotFoundException("sprite animation image was not found inside its package", path);
		int columns = Math.Max(1, animation.columns);
		int rows = Math.Max(1, animation.rows);
		Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
		texture.name = "CustomEnemy_" + animation.name;
		texture.filterMode = FilterMode.Point;
		texture.wrapMode = TextureWrapMode.Clamp;
		if (!ImageConversion.LoadImage(texture, File.ReadAllBytes(path), false))
			throw new InvalidDataException("Unity could not decode sprite sheet '" + path + "'");
		if (texture.width % columns != 0 || texture.height % rows != 0)
			throw new InvalidDataException("sprite sheet dimensions must be divisible by columns and rows: " + path);
		int available = columns * rows;
		int count = animation.frameCount <= 0 ? available : Math.Min(animation.frameCount, available);
		float ppu = visual.pixelsPerUnit > 0f ? visual.pixelsPerUnit : 100f;
		Vector2 pivot = ParseVector2(visual.pivot, new Vector2(0.5f, 0f));
		int width = texture.width / columns;
		int height = texture.height / rows;
		Sprite[] frames = new Sprite[count];
		for (int i = 0; i < count; i++)
		{
			int column = i % columns;
			int rowFromTop = i / columns;
			Rect rect = new Rect(column * width, texture.height - (rowFromTop + 1) * height, width, height);
			frames[i] = Sprite.Create(texture, rect, pivot, ppu, 0, SpriteMeshType.FullRect);
			frames[i].name = animation.name + "_" + i.ToString("D3", CultureInfo.InvariantCulture);
		}
		List<string> keys = new List<string> { Normalize(animation.name) };
		foreach (string alias in animation.aliases ?? Array.Empty<string>())
			if (!string.IsNullOrWhiteSpace(alias)) keys.Add(Normalize(alias));
		return new RuntimeSpriteAnimationData { Name = animation.name, Frames = frames, Fps = Math.Max(0.01f, animation.fps), Loop = animation.loop, MatchKeys = keys.ToArray() };
	}

	// A bottom-pivot replacement sprite parked at the source renderer's origin floats when the
	// base enemy draws from a centre pivot on a raised child (Plantasha: origin 2.5u up, sprite
	// bounds +/-2.56u). The visual is therefore planted so its first frame's bottom sits where
	// the source sprite's bottom sits; the manifest offset fine-tunes on top of that.
	private static float GroundAlignY(SpriteRenderer source, List<RuntimeSpriteAnimationData> loaded, CustomEnemySpriteVisual settings)
	{
		Sprite sourceSprite = source.sprite;
		if (sourceSprite == null || loaded.Count == 0 || loaded[0].Frames.Length == 0 || loaded[0].Frames[0] == null)
		{
			return 0f;
		}
		Bounds sourceBounds = sourceSprite.bounds;
		Bounds frameBounds = loaded[0].Frames[0].bounds;
		float scale = Mathf.Abs(ParseVector3(settings.scale, Vector3.one).y);
		float sourceBottom = source.flipY ? (0f - sourceBounds.max.y) : sourceBounds.min.y;
		float frameBottom = (source.flipY ? (0f - frameBounds.max.y) : frameBounds.min.y) * scale;
		return sourceBottom - frameBottom;
	}

	private static SpriteRenderer FindSourceRenderer(GameObject root, string hint)
	{
		SpriteRenderer[] renderers = root.GetComponentsInChildren<SpriteRenderer>(true);
		if (!string.IsNullOrWhiteSpace(hint))
			foreach (SpriteRenderer renderer in renderers)
				if (renderer.name.Equals(hint, StringComparison.OrdinalIgnoreCase) || GetPath(renderer.transform, root.transform).Equals(hint, StringComparison.OrdinalIgnoreCase)) return renderer;
		foreach (SpriteRenderer renderer in renderers) if (renderer.sprite != null) return renderer;
		return renderers.Length > 0 ? renderers[0] : null;
	}

	private static string GetPath(Transform transform, Transform root)
	{
		string path = transform.name;
		while (transform.parent != null && transform.parent != root) { transform = transform.parent; path = transform.name + "/" + path; }
		return path;
	}

	private static int FindAnimation(RuntimeSpriteAnimationData[] animations, string name)
	{
		for (int i = 0; i < animations.Length; i++) if (animations[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return i;
		return -1;
	}

	private static string Normalize(string value)
	{
		return (value ?? "").Replace(" ", "").Replace("_", "").Replace("-", "").ToLowerInvariant();
	}

	private static Vector2 ParseVector2(string raw, Vector2 fallback)
	{
		string[] parts = (raw ?? "").Split(',');
		return parts.Length >= 2 && float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) ? new Vector2(x, y) : fallback;
	}

	internal static Vector3 ParseVector3(string raw, Vector3 fallback)
	{
		string[] parts = (raw ?? "").Split(',');
		if (parts.Length < 2 || !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y)) return fallback;
		float z = parts.Length > 2 && float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float parsedZ) ? parsedZ : fallback.z;
		return new Vector3(x, y, z);
	}
}
