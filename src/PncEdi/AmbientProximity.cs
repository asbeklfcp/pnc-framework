using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace PncEdi;

public static class AmbientProximity
{
	private class Pattern
	{
		public string CanonicalName;
		public string[] Aliases;
		public int LongestAliasLen;
	}

	private struct Source
	{
		public AudioSource Audio;
		public string ClipName;
		public Pattern PatternMatched;

		// Resolved once per scan: FindObjectsByType per frame would be far too costly.
		public bool IsPeekHole;
	}

	private struct PatternMatch
	{
		public Pattern Pattern;
		public int Score;
	}

	private static readonly Dictionary<string, string> ExactAmbientGalleryMap = new Dictionary<string, string>
	{
		{ "gargoyle_ledge_fuck", "ambient_gargoyle_ledge_fuck" },
		{ "gooper_bed_blowjob", "ambient_gooper_bed_blowjob" },
		{ "imp_gangbang", "ambient_imp_gangbang" },
		{ "mimic_wall_fuck", "ambient_mimic_wall_fuck" },
		{ "nun_chair", "ambient_nun_chair_fuck" },
		{ "nun_chair_fuck", "ambient_nun_chair_fuck" },
		{ "nun_wall_chain_head", "ambient_nun_wall_chain_head" },
		{ "plantasha_blowjob", "ambient_plantasha_blowjob" },
		{ "zombie_bench_fuck", "ambient_zombie_bench_fuck" },
		{ "wendigo_hole", "ambient_wendigo_hole" }
	};

	private static readonly List<Source> Sources = new List<Source>();

	// Every OTHER looping source in the scene - the ones no pattern claimed. They take no part
	// in identifying anything; they exist so a box-identified diorama can still be given an
	// anchor to measure a look against. See AnchorInBox.
	private static readonly List<AudioSource> Unmatched = new List<AudioSource>();
	private static Transform _player;
	private static string _activeKey;
	internal static bool IsFunscriptActive => _activeKey != null;
	private static float _lastAtDioramaAt = -99f;
	private static bool _lastStopWasSightLoss;
	private static readonly HashSet<int> LoggedPeekHoles = new HashSet<int>();
	private static readonly HashSet<int> AnchorLogged = new HashSet<int>();

	public static string TryResolveAmbientGalleryForHierarchy(GameObject root)
	{
		if (root == null)
		{
			return null;
		}
		List<Pattern> patterns = GetPatterns();
		AudioSource[] componentsInChildren = root.GetComponentsInChildren<AudioSource>(true);
		Pattern pattern = null;
		int bestScore = -1;
		foreach (AudioSource audioSource in componentsInChildren)
		{
			if (!(audioSource == null) && !(audioSource.clip == null))
			{
				string clip = audioSource.clip.name?.ToLowerInvariant() ?? "";
				string objectName = audioSource.gameObject.name?.ToLowerInvariant() ?? "";
				PatternMatch patternMatch = MatchPattern(patterns, clip, objectName);
				if (patternMatch.Pattern != null && patternMatch.Score > bestScore)
				{
					pattern = patternMatch.Pattern;
					bestScore = patternMatch.Score;
				}
			}
		}
		return ResolveGallery(pattern);
	}

	private static string ResolveGallery(Pattern pattern)
	{
		if (pattern == null)
		{
			return null;
		}
		string slug = NameRemap.Slug(pattern.CanonicalName);
		if (ExactAmbientGalleryMap.TryGetValue(slug, out var value))
		{
			return value;
		}
		string[] aliases = pattern.Aliases;
		for (int i = 0; i < aliases.Length; i++)
		{
			string key = NameRemap.Slug(aliases[i]);
			if (ExactAmbientGalleryMap.TryGetValue(key, out value))
			{
				return value;
			}
		}
		return Plugin.CfgAmbientPrefix.Value + slug;
	}

	private static PatternMatch MatchPattern(List<Pattern> patterns, string clip, string objectName)
	{
		Pattern pattern = null;
		int bestScore = -1;
		for (int i = 0; i < patterns.Count; i++)
		{
			Pattern pattern2 = patterns[i];
			string[] aliases = pattern2.Aliases;
			foreach (string alias in aliases)
			{
				if (!string.IsNullOrEmpty(alias) && (clip.Contains(alias) || objectName.Contains(alias)))
				{
					int score = alias.Length;
					if (clip.Equals(alias) || objectName.Equals(alias))
					{
						score += 1000;
					}
					if (score > bestScore)
					{
						pattern = pattern2;
						bestScore = score;
					}
				}
			}
		}
		return new PatternMatch
		{
			Pattern = pattern,
			Score = bestScore
		};
	}

	private static List<Pattern> GetPatterns()
	{
		string raw = Plugin.CfgAmbientPatterns?.Value ?? "";
		List<Pattern> patterns = new List<Pattern>();
		string[] fields = raw.Split(',');
		for (int i = 0; i < fields.Length; i++)
		{
			string field = fields[i].Trim();
			if (field.Length == 0)
			{
				continue;
			}
			string[] parts = field.Split('|');
			for (int j = 0; j < parts.Length; j++)
			{
				parts[j] = parts[j].Trim().ToLowerInvariant();
			}
			List<string> aliases = new List<string>();
			foreach (string alias in parts)
			{
				if (!string.IsNullOrEmpty(alias))
				{
					aliases.Add(alias);
				}
			}
			if (aliases.Count == 0)
			{
				continue;
			}
			int longest = 0;
			foreach (string item in aliases)
			{
				if (item.Length > longest)
				{
					longest = item.Length;
				}
			}
			patterns.Add(new Pattern
			{
				CanonicalName = aliases[0],
				Aliases = aliases.ToArray(),
				LongestAliasLen = longest
			});
		}
		patterns.Sort((Pattern a, Pattern b) => b.LongestAliasLen.CompareTo(a.LongestAliasLen));
		return patterns;
	}

	public static void Rescan()
	{
		Sources.Clear();
		Unmatched.Clear();
		List<Pattern> patterns = GetPatterns();
		AudioSource[] audioSources = Object.FindObjectsByType<AudioSource>((FindObjectsSortMode)0);
		foreach (AudioSource audioSource in audioSources)
		{
			if (!(audioSource == null) && !(audioSource.clip == null) && audioSource.loop)
			{
				string lower = audioSource.clip.name?.ToLowerInvariant() ?? "";
				string objectName = audioSource.gameObject.name?.ToLowerInvariant() ?? "";
				PatternMatch patternMatch = MatchPattern(patterns, lower, objectName);
				if (patternMatch.Pattern == null)
				{
					if (!HeatLockSystem.IsNearPeekHole(audioSource.transform, Mathf.Max(0.5f, Plugin.CfgAmbientPeekHoleRadius?.Value ?? 1.5f)))
					{
						Unmatched.Add(audioSource);
					}
				}
				else
				{
					bool enabled = HeatLockSystem.IsNearPeekHole(audioSource.transform, Mathf.Max(0.5f, Plugin.CfgAmbientPeekHoleRadius?.Value ?? 1.5f));
					// Rescan runs every few seconds, so log each source once rather than
					// forever.
					if (enabled && LoggedPeekHoles.Add(audioSource.GetInstanceID()))
					{
						Plugin.DBG("AMBIENT", "'" + objectName + "' is a peek hole - left to the interact path");
					}
					Sources.Add(new Source
					{
						Audio = audioSource,
						ClipName = lower,
						PatternMatched = patternMatch.Pattern,
						IsPeekHole = enabled
					});
				}
			}
		}
		if (Sources.Count <= 0)
		{
			return;
		}
		Dictionary<string, int> dictionary = new Dictionary<string, int>();
		foreach (Source source in Sources)
		{
			dictionary.TryGetValue(source.PatternMatched.CanonicalName, out var value);
			dictionary[source.PatternMatched.CanonicalName] = value + 1;
		}
		StringBuilder stringBuilder = new StringBuilder();
		foreach (KeyValuePair<string, int> item in dictionary)
		{
			if (stringBuilder.Length > 0)
			{
				stringBuilder.Append(", ");
			}
			stringBuilder.Append($"{item.Value}x {item.Key}");
		}
		Plugin.DBG("AMBIENT-SCAN", $"{Sources.Count} audio source(s): {stringBuilder}");
	}

	public static void Tick()
	{
		// Not `Sources.Count == 0` any more: a diorama is now identified by the box the player
		// is standing in, which works whether or not its ambient audio matched a pattern. Only
		// when that path is switched off does an empty source list mean there is nothing to do.
		if (Sources.Count == 0 && !DioramaUnlockTriggers.Enabled)
		{
			return;
		}
		if (_player == null)
		{
			GameObject gameObject = GameObject.FindGameObjectWithTag("Player");
			if (gameObject != null)
			{
				_player = gameObject.transform;
			}
			if (_player == null)
			{
				return;
			}
		}
		GrabScreen grabScreen = GrabScreen.Instance;
		GrappleScreenobject grappleScreenobject = GrappleScreenobject.Instance;
		// Standing in a charm circle is itself a scene the device is playing, so ambient proximity
		// has to stand down for the same reason it does during a grab: two rows fighting over one
		// channel is not two scenes, it is neither.
		if (Plugin.PlayerDead || (grabScreen != null && grabScreen.IsGrabbed) || (grappleScreenobject != null && grappleScreenobject.IsGrappling) || CameraSwapHooks.Active || GalleryHooks.BlocksInGameEdiTracking() || CustomEnemyBridge.EdiChannelHeld)
		{
			if (_activeKey != null)
			{
				_activeKey = null;
			}
			return;
		}
		float defaultRange = Plugin.CfgAmbientRange.Value;
		float hearingMultiplier = Plugin.CfgAmbientHearingMultiplier.Value;
		Pattern pattern = null;
		string clipName = null;
		AudioSource source = null;
		float nearest = float.MaxValue;
		for (int i = 0; i < Sources.Count; i++)
		{
			Source candidate = Sources[i];
			if (candidate.IsPeekHole)
			{
				continue;
			}
			if (!(candidate.Audio == null) && candidate.Audio.isPlaying)
			{
				float distance = Vector3.Distance(candidate.Audio.transform.position, _player.position);
				float audibleRange = ((candidate.Audio.spatialBlend > 0f && candidate.Audio.maxDistance > 0.1f) ? (candidate.Audio.maxDistance * hearingMultiplier) : defaultRange);
				if (!(distance > audibleRange) && distance < nearest)
				{
					nearest = distance;
					pattern = candidate.PatternMatched;
					clipName = candidate.ClipName;
					source = candidate.Audio;
				}
			}
		}
		string gallery = ResolveGallery(pattern);

		// The box the player is standing in outranks whatever is audible. Both are asking
		// "which diorama is this", and only one of them is the game's own answer: the box
		// carries a galleryID and DioramaAmbientMap turns it into the script, where the audio
		// route matches a clip-name pattern maintained by hand here. Where they disagree the
		// pattern is the thing that can be wrong (§65).
		string boxGalleryId;
		string boxGallery = DioramaUnlockTriggers.AmbientAtPlayer(DioramaUnlockTriggers.PlaybackPadding,
			out boxGalleryId);
		if (boxGallery != null)
		{
			if (gallery != null && gallery != boxGallery)
			{
				Plugin.DBG("AMBIENT", $"box {boxGalleryId} says {boxGallery}, audio said {gallery} - trusting the box");
			}
			else if (gallery == null)
			{
				Plugin.DBG("AMBIENT", $"box {boxGalleryId} -> {boxGallery} (no ambient pattern matched here)");
			}
			gallery = boxGallery;
			// The box answered "which diorama", but the release still needs a *point in the
			// world* to measure a look against, and that has only ever come from a
			// pattern-matched AudioSource. Give it the nearest unclaimed loop inside the box
			// instead, so a missing Patterns entry costs a log line and nothing else.
			if (source == null)
			{
				source = AnchorInBox(boxGallery, boxGalleryId);
			}
		}

		if (gallery != null)
		{
			// Earshot is only the precondition now - AmbientReleaseGaze requires the player
			// to actually look at it before the one-time release is spent. NoteCandidate still
			// declines a null source, but AnchorInBox above means a box-identified diorama
			// arrives here with one whether or not a Patterns entry claimed its audio.
			AmbientReleaseGaze.NoteCandidate(gallery, pattern?.CanonicalName ?? gallery, source);
			if (!IsPlayerAtDiorama(gallery, source))
			{
				gallery = null;
				_lastStopWasSightLoss = true;
			}
			else
			{
				_lastStopWasSightLoss = false;
			}
		}
		if (gallery != null && gallery != _activeKey)
		{
			_activeKey = gallery;
			Plugin.SendPlay(gallery);
			string where = ((pattern != null) ? $"hear '{pattern.CanonicalName}' clip='{clipName}' d={nearest:F1}m"
				: $"at box {boxGalleryId}");
			Plugin.DBG("AMBIENT", where + " -> " + gallery);
		}
		else if (gallery == null && _activeKey != null)
		{
			_activeKey = null;
			Plugin.DBG("AMBIENT", (_lastStopWasSightLoss ? "left the diorama -> filler" : "left earshot -> filler"));
			Plugin.GoFiller();
		}
	}

	// The nearest playing loop inside this diorama's own unlock box.
	//
	// WHY THIS EXISTS, AND WHAT IT MAKES REDUNDANT. Two things used to come out of the pattern
	// match together, and only one of them still had to: *which diorama this is*, and *what to
	// measure a look against*. The first was settled in §65 - the box carries the game's own
	// galleryID, DioramaAmbientMap turns it into a script, and where box and pattern disagree the
	// pattern is the one that can be wrong. The second was left riding on it, because `Rescan`
	// keeps only pattern-matched sources and `AmbientReleaseGaze` needs a Transform: an angle to
	// face (`IsFacing`), an `isPlaying` liveness test, and the position that keys which *instance*
	// of a repeated diorama has already been spent (`IsReleaseSourceUsed`). A box with no matching
	// pattern therefore played fine and could never arm its release - the "no ambient pattern
	// matched here" defect, open since §65 and carried in TODO §4 as a walk past six dioramas.
	//
	// Anchoring on the box's own contents closes that class of bug rather than the instance:
	// `Patterns` is now only what identifies a diorama the GAME does not register with a box, and
	// a missing entry costs a log line instead of a silent loss of the release.
	//
	// Deliberately reads the pre-scanned Unmatched list rather than searching the scene: this runs
	// per frame and `FindObjectsByType` here is what `Rescan` exists to avoid.
	private static AudioSource AnchorInBox(string ambientGallery, string galleryId)
	{
		Bounds box;
		if (!DioramaUnlockTriggers.TryGetBounds(ambientGallery, DioramaUnlockTriggers.PlaybackPadding, out box))
		{
			return null;
		}
		AudioSource best = null;
		float bestDistance = float.MaxValue;
		for (int i = 0; i < Unmatched.Count; i++)
		{
			AudioSource candidate = Unmatched[i];
			if (candidate == null || !candidate.isPlaying)
			{
				continue;
			}
			Vector3 at = candidate.transform.position;
			if (!box.Contains(at))
			{
				continue;
			}
			float distance = Vector3.Distance(at, _player.position);
			if (distance < bestDistance)
			{
				bestDistance = distance;
				best = candidate;
			}
		}
		if (best != null && AnchorLogged.Add(best.GetInstanceID()))
		{
			Plugin.DBG("AMBIENT", $"box {galleryId} -> {ambientGallery}: anchoring the release on "
				+ $"'{best.gameObject.name}' clip='{((best.clip != null) ? best.clip.name : "<null>")}' "
				+ $"inside the box, since no Patterns entry claimed it");
		}
		return best;
	}

	// Playback follows where the player is standing as well as what they can hear: a diorama
	// heard through a wall no longer drives the device. "At the diorama" is the game's own
	// GalleryUnlockTrigger box, widened by AmbientPlaybackTriggerPadding so the script does
	// not cut out the moment you shift your feet - several boxes are only a metre across.
	// AmbientPlaybackLingerSeconds debounces the boundary itself, where the overlap test can
	// flip frame to frame; losing the gallery bounces the device to filler and back.
	private static bool IsPlayerAtDiorama(string gallery, AudioSource source)
	{
		if (!Plugin.GameplayTweaksEnabled || !(Plugin.CfgAmbientPlaybackRequiresPresence?.Value ?? false))
		{
			return true;
		}
		// A gallery with no unlock box has nothing to gate on, so it falls back to earshot
		// alone rather than going silent.
		if (!DioramaUnlockTriggers.HasTriggerFor(gallery))
		{
			return true;
		}
		float gap;
		string galleryId;
		if (DioramaUnlockTriggers.IsPlayerAt(gallery, DioramaUnlockTriggers.PlaybackPadding, out gap, out galleryId))
		{
			_lastAtDioramaAt = Time.time;
			return true;
		}
		return Time.time - _lastAtDioramaAt < Mathf.Max(0f, Plugin.CfgAmbientPlaybackLingerSeconds?.Value ?? 0.25f);
	}

	internal static void AppendReleaseMarkerSources(List<HeatLockSystem.ReleaseMarkerSource> markers)
	{
		if (markers == null)
		{
			return;
		}
		if (_player == null)
		{
			GameObject gameObject = GameObject.FindGameObjectWithTag("Player");
			if (gameObject != null)
			{
				_player = gameObject.transform;
			}
		}
		if (_player == null)
		{
			return;
		}
		float defaultRange = Plugin.CfgAmbientRange.Value;
		float hearingMultiplier = Plugin.CfgAmbientHearingMultiplier.Value;
		for (int i = 0; i < Sources.Count; i++)
		{
			Source source = Sources[i];
			if (!(source.Audio == null) && source.PatternMatched != null && source.Audio.isPlaying)
			{
				float distance = Vector3.Distance(source.Audio.transform.position, _player.position);
				float audibleRange = ((source.Audio.spatialBlend > 0f && source.Audio.maxDistance > 0.1f) ? (source.Audio.maxDistance * hearingMultiplier) : defaultRange);
				if (!(distance > audibleRange))
				{
					markers.Add(new HeatLockSystem.ReleaseMarkerSource
					{
						Kind = "ambient",
						Label = source.PatternMatched.CanonicalName,
						Transform = source.Audio.transform
					});
				}
			}
		}
	}

	public static void DiagnosticDump(float range)
	{
		if (_player == null)
		{
			GameObject playerObject = GameObject.FindGameObjectWithTag("Player");
			if (playerObject != null)
			{
				_player = playerObject.transform;
			}
			if (_player == null)
			{
				return;
			}
		}
		float rangeSquared = range * range;
		int audioCount = 0;
		StringBuilder audio = new StringBuilder();
		AudioSource[] audioSources = Object.FindObjectsByType<AudioSource>((FindObjectsSortMode)0);
		Vector3 offset;
		foreach (AudioSource audioSource in audioSources)
		{
			if (audioSource == null || !audioSource.isPlaying)
			{
				continue;
			}
			offset = audioSource.transform.position - _player.position;
			float sqrMagnitude = offset.sqrMagnitude;
			if (!(sqrMagnitude > rangeSquared))
			{
				if (audio.Length > 0)
				{
					audio.Append(" | ");
				}
				audio.Append("obj='" + audioSource.gameObject.name + "' clip='" + ((bool)((Object)(object)audioSource.clip) ? audioSource.clip.name : "<null>") + "' " + $"loop={audioSource.loop} maxD={audioSource.maxDistance:F0} d={Mathf.Sqrt(sqrMagnitude):F1}m");
				audioCount++;
				if (audioCount >= 10)
				{
					audio.Append(" [+more]");
					break;
				}
			}
		}
		Plugin.DBG("DIAG-AUDIO", $"{audioCount} playing audio in {range}m: {audio}");
		int spriteCount = 0;
		StringBuilder sprites = new StringBuilder();
		SpriteRenderer[] spriteRenderers = Object.FindObjectsByType<SpriteRenderer>((FindObjectsSortMode)0);
		foreach (SpriteRenderer spriteRenderer in spriteRenderers)
		{
			if (spriteRenderer == null)
			{
				continue;
			}
			offset = spriteRenderer.transform.position - _player.position;
			float sqrMagnitude2 = offset.sqrMagnitude;
			if (!(sqrMagnitude2 > rangeSquared))
			{
				string spriteName = ((bool)((Object)(object)spriteRenderer.sprite) ? spriteRenderer.sprite.name : "<null>");
				string parentName = ((spriteRenderer.transform.parent != null) ? ((Component)spriteRenderer).transform.parent.gameObject.name : "<root>");
				if (sprites.Length > 0)
				{
					sprites.Append(" | ");
				}
				sprites.Append($"obj='{spriteRenderer.gameObject.name}' parent='{parentName}' sprite='{spriteName}' d={Mathf.Sqrt(sqrMagnitude2):F1}m");
				spriteCount++;
				if (spriteCount >= 12)
				{
					sprites.Append(" [+more truncated]");
					break;
				}
			}
		}
		Plugin.DBG("DIAG-SPRITE", $"{spriteCount} sprites in {range}m: {sprites}");
	}
}
