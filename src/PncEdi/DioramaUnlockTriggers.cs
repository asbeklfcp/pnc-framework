using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PncEdi;

// The game already answers "has the player seen this diorama", and it does not use sight at
// all: every diorama room carries a hand-placed GalleryUnlockTrigger - a trigger box with a
// galleryID of D1..D9 whose OnTriggerEnter calls GalleryProgressManager.UnlockEnemy. Walking
// into that box is the level designer's own definition of having arrived at the diorama.
//
// That is a far better signal than the raycast this used to depend on. Three separate rounds
// of testing died on the ray: the camera resolving to the wrong object, the ray hitting the
// player's own collider, and the audio source origin sitting inside the floor. The trigger
// box needs no camera, no ray and no guess about where the art actually is.
internal static class DioramaUnlockTriggers
{
	private struct Entry
	{
		public Collider Volume;
		public string GalleryId;
		public string Ambient;
	}

	private static readonly List<Entry> Entries = new List<Entry>();
	private static FieldInfo _galleryIdField;
	private static float _nextScanAt = -99f;
	private static string _lastRoster;
	private static Transform _player;
	private static Collider _playerCollider;
	internal static bool Enabled => Plugin.GameplayTweaksEnabled && ((OverkeksMode.UsesRecipe ? OverkeksConfig.CfgAmbientReleaseUseUnlockTriggers : Plugin.CfgAmbientReleaseUseUnlockTriggers)?.Value ?? true);

	// Tight for the release - the player should be at the thing to spend it. Looser for
	// playback, so the script does not stutter off as they shift their feet in a 1m box.
	internal static float ReleasePadding => Mathf.Max(0f, (OverkeksMode.UsesRecipe ? OverkeksConfig.CfgAmbientReleaseTriggerPadding : Plugin.CfgAmbientReleaseTriggerPadding)?.Value ?? 1.5f);
	internal static float PlaybackPadding => Mathf.Max(0f, (OverkeksMode.UsesRecipe ? OverkeksConfig.CfgAmbientPlaybackTriggerPadding : Plugin.CfgAmbientPlaybackTriggerPadding)?.Value ?? 4f);

	internal static void ResetForScene()
	{
		Entries.Clear();
		_nextScanAt = -99f;
		_lastRoster = null;
		_player = null;
		_playerCollider = null;
	}

	// Rooms are streamed in by the dungeon generator, so the roster is rebuilt periodically
	// rather than once. The scan is cheap next to the per-frame raycasting it replaces.
	private static void EnsureScanned()
	{
		if (Time.time < _nextScanAt)
		{
			return;
		}
		_nextScanAt = Time.time + 2f;
		if (_galleryIdField == null)
		{
			_galleryIdField = AccessTools.Field(typeof(GalleryUnlockTrigger), "galleryID");
		}
		Entries.Clear();
		GalleryUnlockTrigger[] galleryUnlockTriggers = Object.FindObjectsByType<GalleryUnlockTrigger>((FindObjectsSortMode)0);
		StringBuilder stringBuilder = new StringBuilder();
		for (int i = 0; i < galleryUnlockTriggers.Length; i++)
		{
			GalleryUnlockTrigger galleryUnlockTrigger = galleryUnlockTriggers[i];
			if (galleryUnlockTrigger == null)
			{
				continue;
			}
			Collider collider = galleryUnlockTrigger.GetComponent<Collider>();
			if (collider == null)
			{
				continue;
			}
			string galleryId = (_galleryIdField?.GetValue(galleryUnlockTrigger) as string) ?? "";
			Entry entry = default(Entry);
			entry.Volume = collider;
			entry.GalleryId = galleryId;
			entry.Ambient = DioramaGalleryMap.Resolve(galleryId);
			Entries.Add(entry);
			if (stringBuilder.Length > 0)
			{
				stringBuilder.Append(", ");
			}
			Vector3 size = collider.bounds.size;
			stringBuilder.Append(galleryId + "->" + (entry.Ambient ?? "<unmapped>") + $" {size.x:F1}x{size.y:F1}x{size.z:F1}m");
		}
		string roster = ((stringBuilder.Length > 0) ? stringBuilder.ToString() : "none");
		if (roster != _lastRoster)
		{
			// Once per change, not per scan: the previous version of this diagnostic logged
			// 138 lines for one source in a single run.
			_lastRoster = roster;
			Plugin.DBG("HEAT-LOCK", $"unlock triggers: {roster}");
		}
	}

	private static Bounds PlayerBounds(out bool haveBounds)
	{
		haveBounds = false;
		if (_player == null)
		{
			GameObject gameObject = GameObject.FindGameObjectWithTag("Player");
			if (gameObject == null)
			{
				return default(Bounds);
			}
			_player = gameObject.transform;
			_playerCollider = gameObject.GetComponentInChildren<Collider>();
		}
		haveBounds = true;
		if (_playerCollider != null)
		{
			// Match what OnTriggerEnter actually tests: the player's body overlapping the box,
			// not the player's origin being inside it. Several of these boxes are only 1m tall
			// and sit at floor level, so a point test on the player transform would miss.
			return _playerCollider.bounds;
		}
		return new Bounds(_player.position, new Vector3(0.8f, 1.8f, 0.8f));
	}

	// True while the player is standing in (or within `padding` of) the unlock box belonging
	// to this ambient gallery. `gap` reports how far outside it they are, for the log.
	internal static bool IsPlayerAt(string ambientGallery, float padding, out float gap, out string galleryId)
	{
		gap = float.MaxValue;
		galleryId = null;
		if (!Enabled || string.IsNullOrEmpty(ambientGallery))
		{
			return false;
		}
		EnsureScanned();
		bool haveBounds;
		Bounds bounds = PlayerBounds(out haveBounds);
		if (!haveBounds)
		{
			return false;
		}
		padding = Mathf.Max(0f, padding);
		for (int i = 0; i < Entries.Count; i++)
		{
			Entry entry = Entries[i];
			if (!string.Equals(entry.Ambient, ambientGallery) || entry.Volume == null)
			{
				continue;
			}
			galleryId = entry.GalleryId;
			Bounds bounds2 = entry.Volume.bounds;
			// Bounds.Expand adds to the *size*, i.e. half the amount on each side, so the
			// padding has to be doubled to mean "padding metres of slack all round".
			bounds2.Expand(2f * padding);
			if (bounds2.Intersects(bounds))
			{
				gap = 0f;
				return true;
			}
			float distance = Vector3.Distance(bounds2.ClosestPoint(bounds.center), bounds.center);
			if (distance < gap)
			{
				gap = distance;
			}
		}
		return false;
	}

	// Which diorama the player is standing in, if any - identification rather than the
	// yes/no IsPlayerAt asks.
	//
	// This is the game's own answer and it is exact: every diorama carries a box with a
	// galleryID, and DioramaAmbientMap turns that into the script. The ambient *audio* path
	// answers the same question by matching a hand-maintained clip-name pattern, which has to
	// be extended by hand for every new scene and can be wrong. Same lesson as CHANGELOG §26,
	// applied to identification instead of to the release: where the game already labels a
	// thing, read the label (§65).
	//
	// Nearest box wins if two overlap. Note a galleryID is NOT unique - game 0.3.1 ships two
	// D2 boxes, one for the secret-room version of that room - which is exactly why Entries is
	// a flat list rather than a dictionary keyed on the id.
	internal static string AmbientAtPlayer(float padding, out string galleryId)
	{
		galleryId = null;
		if (!Enabled)
		{
			return null;
		}
		EnsureScanned();
		bool haveBounds;
		Bounds bounds = PlayerBounds(out haveBounds);
		if (!haveBounds)
		{
			return null;
		}
		padding = Mathf.Max(0f, padding);
		string best = null;
		float bestSize = float.MaxValue;
		for (int i = 0; i < Entries.Count; i++)
		{
			Entry entry = Entries[i];
			if (string.IsNullOrEmpty(entry.Ambient) || entry.Volume == null)
			{
				continue;
			}
			Bounds box = entry.Volume.bounds;
			// Expand adds to the *size*, i.e. half on each side - see IsPlayerAt.
			box.Expand(2f * padding);
			if (!box.Intersects(bounds))
			{
				continue;
			}
			// Two boxes can overlap where rooms adjoin; the tighter one is the more specific
			// claim on where the player is standing.
			float size = box.size.x * box.size.y * box.size.z;
			if (size < bestSize)
			{
				bestSize = size;
				best = entry.Ambient;
				galleryId = entry.GalleryId;
			}
		}
		return best;
	}

	// The box for one gallery, padded, for anything that needs the volume rather than a yes/no.
	// Same tightest-box tie-break as AmbientAtPlayer: two boxes overlap where rooms adjoin, and
	// the smaller one is the more specific claim.
	internal static bool TryGetBounds(string ambientGallery, float padding, out Bounds bounds)
	{
		bounds = default(Bounds);
		if (!Enabled || string.IsNullOrEmpty(ambientGallery))
		{
			return false;
		}
		EnsureScanned();
		bool found = false;
		float bestSize = float.MaxValue;
		for (int i = 0; i < Entries.Count; i++)
		{
			Entry entry = Entries[i];
			if (entry.Volume == null || !string.Equals(entry.Ambient, ambientGallery))
			{
				continue;
			}
			Bounds box = entry.Volume.bounds;
			box.Expand(2f * Mathf.Max(0f, padding));
			float size = box.size.x * box.size.y * box.size.z;
			if (size < bestSize)
			{
				bestSize = size;
				bounds = box;
				found = true;
			}
		}
		return found;
	}

	// Whether this gallery has an unlock box at all. Peek holes and any future diorama the
	// game does not register keep the old line-of-sight path.
	internal static bool HasTriggerFor(string ambientGallery)
	{
		if (!Enabled || string.IsNullOrEmpty(ambientGallery))
		{
			return false;
		}
		EnsureScanned();
		for (int i = 0; i < Entries.Count; i++)
		{
			if (string.Equals(Entries[i].Ambient, ambientGallery) && Entries[i].Volume != null)
			{
				return true;
			}
		}
		return false;
	}
}
