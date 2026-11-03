using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PncEdi;

// A diorama's one-time horny release used to fire the instant it came into *earshot*, so it
// could be spent by walking past a wall with a diorama behind it, without ever seeing the
// thing. Now it has to be looked at: the meter fills while the player is at the diorama and
// facing it, and drains - rather than resetting - while they are not, so glancing away
// briefly costs progress instead of the whole attempt.
//
// "At the diorama" is the game's own GalleryUnlockTrigger box (see DioramaUnlockTriggers),
// and "looking at it" is an angle. There is deliberately no raycast: four rounds of testing
// died on one, and the two things it was there for are both covered without it - peek holes
// never reach this path at all (AmbientProximity skips them; peeking is an interaction), and
// every diorama the ambient patterns can match has an unlock box.
internal static class AmbientReleaseGaze
{
	private static string _gallery;
	private static string _canonicalName;
	private static AudioSource _source;
	private static int _notedFrame = -1;
	private static float _progress;
	private static string _lastSkipReason;
	private static int _lastVisible = -1;
	private static float _nextBlockLogAt;
	private static float _nextSkipStatusAt;
	private static Camera _viewCamera;
	private static FirstPersonController _fpc;
	private static Traverse _fpcCamera;
	internal static float Progress => _progress;
	internal static bool Enabled => Plugin.GameplayTweaksEnabled && HeatLockSystem.Enabled && LookSeconds > 0f;
	private static float LookSeconds => Mathf.Max(0f, (OverkeksMode.UsesRecipe ? OverkeksConfig.CfgAmbientReleaseLookSeconds : Plugin.CfgAmbientReleaseLookSeconds)?.Value ?? 3f);
	private static float LookAngle => Mathf.Clamp((OverkeksMode.UsesRecipe ? OverkeksConfig.CfgAmbientReleaseLookAngle : Plugin.CfgAmbientReleaseLookAngle)?.Value ?? 75f, 5f, 180f);

	internal static void ResetForScene()
	{
		_gallery = null;
		_canonicalName = null;
		_source = null;
		_viewCamera = null;
		_fpc = null;
		_fpcCamera = null;
		_notedFrame = -1;
		_progress = 0f;
		_lastSkipReason = null;
		_nextSkipStatusAt = 0f;
		_lastVisible = -1;
		DioramaUnlockTriggers.ResetForScene();
	}

	// Called from AmbientProximity each frame that a diorama is the nearest audible one.
	internal static void NoteCandidate(string gallery, string canonicalName, AudioSource source)
	{
		if (!Enabled || source == null)
		{
			return;
		}
		string label = canonicalName ?? gallery;
		if (HeatLockSystem.IsReleaseSourceUsed("ambient", label, source.transform))
		{
			// Same wording the peephole path uses. It used to read "ambient <pattern name>
			// already used", which put the mod's internal source-kind prefix on screen.
			LogSkip(label + ": release already used", "Already used");
			return;
		}
		// Do not let the meter run down a release the player cannot spend - TryRelease would
		// consume the one-time source for nothing. No on-screen label: a player with no locks
		// does not need telling, and the log line is enough to diagnose it.
		if (HeatLockSystem.CurrentLocks <= 0)
		{
			LogSkip(label + ": no horny locks held, nothing to release", null);
			return;
		}
		_lastSkipReason = null;
		if (!string.Equals(_gallery, gallery) || (Object)(object)_source != (Object)(object)source)
		{
			_gallery = gallery;
			_canonicalName = canonicalName;
			_source = source;
			_progress = 0f;
			// Per-candidate, or walking up to a second diorama while the first is still
			// remembered as "not visible" produces no message at all.
			_lastVisible = -1;
			_lastSkipReason = null;
		}
		_notedFrame = Time.frameCount;
	}

	internal static void Tick()
	{
		// A peephole scene owns the bar while its release is pending - it is the same gauge,
		// showing the watch timer instead of the look timer. The two cannot overlap anyway
		// (AmbientProximity stops ticking while a camera-swap scene is up), but the diorama
		// path would otherwise push a zero progress every frame and hide it. Checked ahead of
		// Enabled, which is about the *diorama* look timer: turning that off must not take the
		// peephole bar with it.
		if (HeatLockSystem.HasKeyholeWatchBar)
		{
			_progress = 0f;
			HeatLockSystem.SetAmbientGauge(HeatLockSystem.KeyholeWatchProgress);
			return;
		}
		if (!Enabled)
		{
			_progress = 0f;
			HeatLockSystem.SetAmbientGauge(0f);
			return;
		}
		float lookSeconds = LookSeconds;
		bool noted = _notedFrame >= Time.frameCount - 1;
		bool inSight = noted && IsWatchingSource();
		if (_source != null)
		{
			int visible = (inSight ? 1 : 0);
			if (visible != _lastVisible)
			{
				_lastVisible = visible;
				Plugin.DBG("HEAT-LOCK", "diorama " + (_canonicalName ?? _gallery) + (inSight ? " in sight - filling" : " out of sight - draining"));
				if (!inSight)
				{
					// A marker rather than a sentence: an unspent diorama within reach is worth
					// signalling, but it does not need explaining every time.
					HeatLockSystem.SetAmbientReleaseStatus((OverkeksMode.UsesRecipe ? OverkeksConfig.CfgAmbientNearbyIcon : Plugin.CfgAmbientNearbyIcon)?.Value ?? "♦");
				}
			}
			if (!inSight && Time.time >= _nextBlockLogAt)
			{
				_nextBlockLogAt = Time.time + 2f;
				Plugin.DBG("HEAT-LOCK", "look check: " + DescribeVisibility(_source));
			}
		}
		if (inSight)
		{
			_progress += Time.deltaTime / lookSeconds;
		}
		else
		{
			// Drains rather than resetting: a glance away is a setback, not a lost attempt.
			_progress -= Time.deltaTime / lookSeconds * Mathf.Max(0f, (OverkeksMode.UsesRecipe ? OverkeksConfig.CfgAmbientReleaseDecayMultiplier : Plugin.CfgAmbientReleaseDecayMultiplier)?.Value ?? 1f);
		}
		_progress = Mathf.Clamp01(_progress);
		if (_progress >= 1f)
		{
			AudioSource source = _source;
			string gallery = _gallery;
			string canonicalName = _canonicalName;
			_progress = 0f;
			_source = null;
			_gallery = null;
			_notedFrame = -1;
			HeatLockSystem.SetAmbientGauge(0f);
			Plugin.DBG("HEAT-LOCK", "watched " + (canonicalName ?? gallery) + " for " + lookSeconds.ToString("F1") + "s -> release");
			HeatLockSystem.TryReleaseFromAmbient(gallery, canonicalName, source);
			return;
		}
		if (!noted && _progress <= 0f)
		{
			// Only drop the candidate once it is *both* gone and spent. Clearing it whenever
			// the meter merely sat at zero made NoteCandidate re-arm on the next frame, which
			// reset _lastVisible and logged "out of sight" every single frame - 12k lines in
			// one run, and the transition log became useless.
			_source = null;
			_gallery = null;
			_canonicalName = null;
			_lastVisible = -1;
		}
		HeatLockSystem.SetAmbientGauge(_progress);
	}

	// The log line is once per reason, so a diorama that can never charge says so without
	// spamming. The on-screen status is not: SetReleaseStatus only bumps a 6s timer, so
	// tying it to the log meant a spent diorama said "already used" once, for six seconds,
	// and then stayed silent every time the player came back to it.
	private static void LogSkip(string reason, string onScreen)
	{
		if (!string.Equals(_lastSkipReason, reason))
		{
			_lastSkipReason = reason;
			_nextSkipStatusAt = 0f;
			Plugin.DBG("HEAT-LOCK", "no look meter for " + reason);
		}
		if (onScreen != null && Time.time >= _nextSkipStatusAt)
		{
			_nextSkipStatusAt = Time.time + 2f;
			HeatLockSystem.SetAmbientReleaseStatus(onScreen);
		}
	}

	private static bool IsWatchingSource()
	{
		if (_source == null || !_source.isPlaying)
		{
			return false;
		}
		if (!IsFacing(_source))
		{
			return false;
		}
		// The game's own unlock box for this diorama: standing in it is what the game itself
		// counts as having seen the thing. A gallery with no box - future content the game
		// does not register - falls back to facing and earshot alone.
		if (!DioramaUnlockTriggers.HasTriggerFor(_gallery))
		{
			return true;
		}
		float gap;
		string galleryId;
		return DioramaUnlockTriggers.IsPlayerAt(_gallery, DioramaUnlockTriggers.ReleasePadding, out gap, out galleryId);
	}

	// Direction only - no ray, no projection. Being off by a metre on where the art sits does
	// not matter to an angle, which is the whole point after three rounds lost to geometry.
	private static bool IsFacing(AudioSource source)
	{
		Camera camera = ResolveViewCamera();
		if (camera == null)
		{
			return true;
		}
		Vector3 vector3 = source.transform.position - camera.transform.position;
		if (vector3.magnitude < 1.5f)
		{
			// Standing on top of it: the direction is meaningless, so do not gate on it.
			return true;
		}
		return Vector3.Angle(camera.transform.forward, vector3) <= LookAngle;
	}

	// FirstPersonController.cameraComponent is the actual view camera, and it is the only
	// thing asked for now. Camera.main only finds a camera tagged MainCamera, and the old
	// depth-ordered fallback latched onto the Effects Camera and then answered every query
	// for the rest of the run.
	internal static Camera ResolveViewCamera()
	{
		if (_fpc == null)
		{
			GameObject gameObject = GameObject.FindGameObjectWithTag("Player");
			if (gameObject != null)
			{
				_fpc = gameObject.GetComponent<FirstPersonController>();
			}
			if (_fpc == null)
			{
				FirstPersonController[] firstPersonControllers = Object.FindObjectsByType<FirstPersonController>((FindObjectsSortMode)0);
				if (firstPersonControllers.Length != 0)
				{
					_fpc = firstPersonControllers[0];
				}
			}
			_fpcCamera = ((_fpc != null) ? Traverse.Create((object)_fpc).Field("cameraComponent") : null);
		}
		if (_fpcCamera != null)
		{
			Camera fpcCamera = _fpcCamera.GetValue<Camera>();
			if (fpcCamera != null && fpcCamera.isActiveAndEnabled)
			{
				return fpcCamera;
			}
		}
		if (_viewCamera != null && _viewCamera.isActiveAndEnabled)
		{
			return _viewCamera;
		}
		_viewCamera = Camera.main;
		if (_viewCamera != null && _viewCamera.isActiveAndEnabled)
		{
			return _viewCamera;
		}
		GameObject player = GameObject.FindGameObjectWithTag("Player");
		if (player != null)
		{
			_viewCamera = player.GetComponentInChildren<Camera>();
		}
		return _viewCamera;
	}

	// Says why the meter is not filling, so a diorama that never charges can be diagnosed
	// from one run instead of guessed at.
	private static string DescribeVisibility(AudioSource source)
	{
		if (source == null)
		{
			return "no source";
		}
		Camera camera = ResolveViewCamera();
		if (camera == null)
		{
			return "no camera found";
		}
		Vector3 toSource = source.transform.position - camera.transform.position;
		float magnitude = toSource.magnitude;
		float angle = Vector3.Angle(camera.transform.forward, toSource);
		string detail = $"facing {angle:F0}deg (max {LookAngle:F0}), d={magnitude:F1}m cam='{camera.name}'";
		if (!DioramaUnlockTriggers.HasTriggerFor(_gallery))
		{
			return (_canonicalName ?? _gallery) + " no unlock box, " + detail;
		}
		float gap;
		string galleryId;
		bool inBox = DioramaUnlockTriggers.IsPlayerAt(_gallery, DioramaUnlockTriggers.ReleasePadding, out gap, out galleryId);
		return (_canonicalName ?? _gallery) + " [" + (galleryId ?? "?") + "] " + (inBox ? "in box" : $"{gap:F1}m outside box") + ", " + detail;
	}
}
