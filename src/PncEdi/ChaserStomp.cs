using System;
using System.Reflection;
using UnityEngine;

namespace PncEdi;

// The chaser bosses' approach: a real row, synced to the footfall, in place of the filler.
//
// This used to be two mechanisms. §151's ChaserAura scaled the filler's own amplitude down as a
// dragon or wendigo closed in - `POST /Edi/Intensity/{max}`, no re-dispatch, no seam - because
// that endpoint can only ever cap a row's travel, never raise it past what the row was authored
// for. So the closest a chaser could make the device feel was "the filler, unsqueezed" - never
// more than baseline, whatever the distance. Asked for directly: something that reads as *more*
// than baseline right next to the thing chasing you, which needs a real row. That became a second
// mechanism dispatching Dragon_Stomp / Wendigo_Stomp - one row per creature, phase-locked to its
// own footfall - and once it existed, ChaserAura's squeeze had nothing left to do: the moment a
// chaser is close enough to be part of either mechanism, this is what plays, never the filler, so
// there was no longer a filler squeeze for that mechanism to be squeezing. The two are one file
// now: one gate (is a chaser audible and in range), one row (this creature's stomp, held across a
// brief loss of that gate), one intensity (how hard the row hits, by distance, held the same way).
//
// THE GATE is the sound itself, because that is what both requests were about.
// `DragonEnemyAI.UpdateIdleMovingSound` plays `idleMovingSound` on `loopingAudioSource` while
// `!isDead && !frozenByArena && (state is Idle or Chasing) && !isAttemptingGrab`, and stops it
// otherwise. Reading `loopingAudioSource.isPlaying` is that whole predicate, one frame fresh - the
// same posture as `SerpentHypnosis.InPlayerView` reading vanilla's `hypnosisInView` rather than
// recomputing visibility. It also covers the case §47 found - a wendigo parked in the level with
// its AI ticking, waiting for its trigger time - correctly rather than by accident, because a
// parked chaser *is* audible. `ChaserStompRange` is the band's far edge in metres (0 = trust the
// looping source's own `maxDistance`, honest but only if the prefab set it - see PluginConfig for
// why that is not the default); the near edge is the chaser's own `grabRange`, where its grab
// lands and takes the device over. Both AI classes keep a public static `ActiveDragons` list, so
// finding the nearest one is two list walks of length 0-2 in practice, never a scene search.
//
// THE ROW is chosen by name, not by C# type: `DragonEnemyAI` and `ProximityDragonEnemyAI` are
// both used for both creatures in this build (a wendigo is a reskinned prefab sharing the dragon's
// AI, confirmed off the live assembly - there is no WendigoEnemyAI class at all), so which one is
// closer is read off the GameObject's own name the same way ChaserBossHelper already does.
//
// THE PHASE LOCK is Plugin.SendPlay's existing animator-phase machinery
// (animNormalizedTime/animClipSeconds), fed an audio clock instead of an Animator's - neither AI
// class fires a footstep event, so the only signal that exists at all is the same looping walk
// clip the gate above already reads. `GalleryRegistry.LoopMs(row)` doubles as the beat period (one
// cycle, by construction - see code/ladders.py's CHASER_STOMP_ROWS), so the funscript's own
// declared length and the phase math can never drift out of step with each other. `DragonFirst-
// OnsetSec` / `WendigoFirstOnsetSec` correct for the one thing that math cannot know on its own:
// the walk clip's *audio file* loops at its own t=0, not at a footfall - the two are 90 ms / 60 ms
// apart, measured against DragonWalk.wav and "Wendigo walk".wav. See code/ladders.py for the full
// onset measurement.
//
// THE HOLD is what keeps the gate above from flapping *anything it drives* - the row and the
// intensity target both, the same guard for both, because both turned out to need it for the
// same reason. This file's first cut gave the row a hold (`ChaserStompGrace`) and the intensity
// a slew (`ChaserStompRamp`) instead, on the theory that a number can self-damp a flickering
// target by capping how fast it moves - `SerpentHypnosis` had just proven a distance-driven
// number does not need rate-limiting at all, only the step/interval POST dedupe every device
// write already needs, because there is no crossing for a continuous value to flap across.
// ChaserStomp's intensity looked the same shape, so it got the same treatment ChaserAura already
// used - but ChaserStomp's `found` is not distance alone the way Serpent's view check is: it is
// distance AND `loop.isPlaying`, and that second half is a state boolean, not a continuous
// reading. A real grab attempt (`DragonEnemyAI.GrabSequence`, measured off the decompiled
// assembly) sets `isAttemptingGrab` for exactly `grabAttemptDuration` (1.5s) before clearing it,
// silencing `idleMovingSound` for that whole window with nothing about the player's distance
// changing at all - a real, sustained gate loss with no ramp-worthy motion behind it, no more a
// "close and getting closer" event than a single dropped frame is. A slew answers a *fast-moving*
// number; this is a temporarily false one. Capping how fast the ramp may fall left the ramp
// racing to reach 100% every time a boss wound up a grab and then reversing when the sound came
// back - and, going the other direction, meant a dash-speed real approach could not be trusted
// either, because the same cap that (usefully) throttles the grab-windup case also (uselessly)
// throttles a genuine ten-metre close in under a second. Two different failures share one knob
// only because they happen to move the same field.
//
// So: no ramp. `IntensityTargetFor()` is assigned directly off `_whoDistance` the moment `found`
// is true - proven safe by the same measurement `SerpentHypnosis`'s header cites, a Handy 2 Pro
// tracking a direct `Intensity` write within 22 ms of prediction with no smoothing at all. A lost
// gate holds the last real target - not 100%, and not recomputed from a stale distance reading -
// for `ChaserStompGrace` seconds, then releases to 100% in one step exactly the way the row hands
// itself back to the filler in one step once its own grace expires. `ChaserStompGrace` is sized
// to clear `grabAttemptDuration` with margin (2.0s against a measured 1.5s) rather than merely
// matching it, because a grace equal to the exact windup length is a coin flip on every attempt,
// not a guard.
//
// PRIORITY. SerpentHypnosis outranks this exactly the way it outranked ChaserAura: both write into
// the filler slot and the Intensity channel, and only one chaser mechanic should be doing either
// at a time. `Plugin.GetFillerGallery` asks the serpent first; this stands down on `Tick` whenever
// `SerpentHypnosis.OwnsIntensity` is true, the same way ChaserAura always did.
//
// AND A REAL SCENE TAKES THE RANGE BACK AT ONCE. Leaving the band slews and the row holds through
// its grace, because both are things happening in the world; a grab starting does not, because
// §125's second run is what that costs - a scene played at the amplitude the last approach left
// behind, with nothing on screen to say why. `Tick` runs from `Plugin.Update` unconditionally for
// exactly that case: it is the one time the filler refresh the row-selection half of this would
// otherwise live in is not being asked anything.
internal static class ChaserStomp
{
	private static FieldInfo _dragonLoopSource;
	private static FieldInfo _proxDragonLoopSource;

	// First footfall within each creature's own walk clip, in seconds. See the file header.
	private const float DragonFirstOnsetSec = 0.090f;
	private const float WendigoFirstOnsetSec = 0.060f;

	// The nearest in-range chaser, as of the last FindNearest. Kept as fields so both the
	// row-selection and the intensity target can read one scan instead of two.
	private static string _who;
	private static AudioSource _whoLoop;
	private static string _whoRow;
	private static float _whoFirstOnsetSec;
	private static float _whoDistance;
	private static float _whoBand;
	private static float _whoGrabRange;
	private static float _whoSourceMax;

	// Row-dispatch state: the row currently held, and since when the gate was last lost.
	private static string _currentRow;
	private static float _lostAt;
	private static string _lastLoggedRow;

	// Intensity state: the last real (found=true) target, held across a lost gate for
	// ChaserStompGrace before releasing to 100% - independent of the row's own _lostAt/_currentRow
	// above, because Tick runs even when CurrentGallery is not being asked anything (see the file
	// header's "a real scene takes the range back at once").
	private static float _heldIntensity = 100f;
	private static float _intensityLostAt;
	private static int _lastSent = -1;
	private static float _lastSentAt;
	private static bool _lowered;
	private static string _lastLoggedIntensity;

	/// <summary>
	/// The row that should be playing right now, phase-locked to the real footfall on first
	/// dispatch, or null when no chaser is close enough (including through the hold). Called from
	/// <see cref="Plugin.GetFillerGallery"/>, itself called every frame the filler is allowed to
	/// run.
	/// </summary>
	internal static string CurrentGallery(out float animNormalizedTime, out float animClipSeconds)
	{
		animNormalizedTime = -1f;
		animClipSeconds = 0f;
		if (!Plugin.CfgEnableChaserStomp.Value)
		{
			Reset();
			return null;
		}
		if (FindNearest())
		{
			_lostAt = 0f;
			// A different row than what was held needs a fresh seek; the same creature
			// continuing does not - Edi is already looping it from wherever it has got to, and
			// RefreshFillerForCurrentHeat only calls SendPlay again when the name changes.
			if (!string.Equals(_currentRow, _whoRow, StringComparison.OrdinalIgnoreCase))
			{
				int loopMs = GalleryRegistry.LoopMs(_whoRow);
				if (loopMs > 0 && _whoLoop != null)
				{
					float periodSec = loopMs / 1000f;
					// How far the source clip is into its current repetition, minus the offset to
					// its first real footfall, wrapped into one beat. Mathf.Repeat handles the
					// negative case - the first `_whoFirstOnsetSec` of every repetition, before
					// that repetition's first footfall has actually landed.
					float sincePreviousBeat = Mathf.Repeat(_whoLoop.time - _whoFirstOnsetSec, periodSec);
					animNormalizedTime = sincePreviousBeat / periodSec;
					animClipSeconds = periodSec;
				}
				_currentRow = _whoRow;
				if (_lastLoggedRow != _whoRow)
				{
					_lastLoggedRow = _whoRow;
					Plugin.DBG("CHASER-STOMP", $"{NameRemap.StripCloneSuffix(_who)} at {_whoDistance:0.0}m of {_whoBand:0.0}-{_whoGrabRange:0.0}m -> {_whoRow}");
				}
			}
			return _currentRow;
		}
		// Out of range or inaudible this frame. Hold what was playing for a grace rather than
		// dropping it on the first false frame - see the file header on why a row needs this and
		// a slewed number does not.
		if (string.IsNullOrEmpty(_currentRow))
		{
			return null;
		}
		if (_lostAt == 0f)
		{
			_lostAt = Time.unscaledTime;
			Plugin.DBG("CHASER-STOMP", $"out of range - holding {_currentRow} for up to {Mathf.Max(0f, Plugin.CfgChaserStompGrace.Value):0.0}s");
		}
		if (Time.unscaledTime - _lostAt >= Mathf.Max(0f, Plugin.CfgChaserStompGrace.Value))
		{
			Plugin.DBG("CHASER-STOMP", "stayed out of range - the stomp gives the device back");
			Reset();
			return null;
		}
		return _currentRow;
	}

	/// <summary>
	/// Re-derive the intensity target and send it if it has moved enough to be worth a POST, and
	/// hand the channel back at once if a real scene has taken it. Called every frame from
	/// <see cref="Plugin.Update"/>, independent of whether <see cref="CurrentGallery"/> ran this
	/// frame - a grab scene is exactly the case where the filler refresh CurrentGallery lives in
	/// stops being called at all, and the release still has to happen.
	/// </summary>
	internal static void Tick()
	{
		if (!Plugin.CfgEnableChaserStomp.Value)
		{
			ReleaseIntensityNow();
			return;
		}
		if (!Plugin.FillerPlaybackActive || Plugin.PlayerDead)
		{
			ReleaseIntensityNow();
			return;
		}
		bool found = FindNearest();
		float target;
		if (found)
		{
			_intensityLostAt = 0f;
			target = IntensityTargetFor();
			_heldIntensity = target;
		}
		else if (_heldIntensity >= 100f)
		{
			// Nothing engaged since the last release - no grace to run out.
			target = 100f;
		}
		else
		{
			if (_intensityLostAt == 0f)
			{
				_intensityLostAt = Time.unscaledTime;
			}
			float grace = Mathf.Max(0f, Plugin.CfgChaserStompGrace.Value);
			if (Time.unscaledTime - _intensityLostAt >= grace)
			{
				target = 100f;
				_heldIntensity = 100f;
			}
			else
			{
				// Held, not recomputed: _whoDistance is stale the instant the gate is lost (a
				// missing chaser reads as float.MaxValue), so this is the last reading a real
				// FindNearest actually trusted, not a fresh guess against bad data.
				target = _heldIntensity;
			}
		}
		if (SerpentHypnosis.OwnsIntensity)
		{
			// Stood down, but still tracking, so a serpent that breaks off hands back to a value
			// that belongs to where the chaser actually is rather than to where it was.
			_lastSent = -1;
			_lowered = false;
			return;
		}
		int rounded = Mathf.Clamp(Mathf.RoundToInt(target), 0, 100);
		if (rounded >= 100)
		{
			// Arriving at the top is the release, and it goes whatever the step and the interval
			// say - a range left at 97 is a range nothing will ever restore.
			ReleaseIntensityNow();
			return;
		}
		int step = Mathf.Max(1, Plugin.CfgChaserStompIntensityStep.Value);
		float interval = Mathf.Max(0f, Plugin.CfgChaserStompIntensityInterval.Value);
		// `Plugin.RequestedIntensity` rather than our own last figure: anything else that moved
		// the channel - the serpent releasing, a scene restoring it - has to be answered, and
		// comparing against what we *sent* would leave this believing a number the device no
		// longer holds.
		bool first = _lastSent < 0;
		if (!first && Mathf.Abs(rounded - Plugin.RequestedIntensity) < step)
		{
			return;
		}
		if (!first && Time.unscaledTime - _lastSentAt < interval)
		{
			return;
		}
		_lastSent = rounded;
		_lastSentAt = Time.unscaledTime;
		_lowered = true;
		string why = DescribeIntensity();
		Plugin.SendIntensity(rounded, why);
		if (!string.Equals(_lastLoggedIntensity, _who, StringComparison.Ordinal))
		{
			_lastLoggedIntensity = _who;
			Plugin.DBG("CHASER-STOMP", why + " -> " + rounded + "%");
		}
	}

	private static string DescribeIntensity()
	{
		if (_who == null)
		{
			return "no chaser in range, releasing to 100%";
		}
		return $"{NameRemap.StripCloneSuffix(_who)} at {_whoDistance:0.0}m of {_whoBand:0.0}-{_whoGrabRange:0.0}m (its loop reaches {_whoSourceMax:0.0}m)";
	}

	// The device intensity for the nearest in-range chaser, assigned directly - see the file
	// header on why this no longer ramps. Same orientation ChaserAura and SerpentHypnosis both
	// use: 1 is the far edge of the band, 0 is grabRange, so the two knobs read the same way in
	// the config file.
	//
	// The 0 end used to be grabRange itself, so Near (100%) was only ever reached the instant the
	// grab was about to land - basically touching. ChaserStompIntensityNearDistance moves that
	// saturation point out to a few metres instead: anything closer reads as full impact, not just
	// the final foot of approach. Clamped to at least grabRange so it can never sit inside the
	// zone the grab scene itself owns.
	private static float IntensityTargetFor()
	{
		int far = Mathf.Clamp(Plugin.CfgChaserStompIntensityFar.Value, 0, 100);
		int near = Mathf.Clamp(Plugin.CfgChaserStompIntensityNear.Value, 0, 100);
		float nearDistance = Mathf.Max(_whoGrabRange, Plugin.CfgChaserStompIntensityNearDistance.Value);
		float bandFraction = Mathf.Clamp01((_whoDistance - nearDistance) / Mathf.Max(0.01f, _whoBand - nearDistance));
		return Mathf.Lerp(near, far, bandFraction);
	}

	// Refreshes _who and friends from live game state; true when something is in range. Two short
	// list walks, no scene search.
	private static bool FindNearest()
	{
		float nearest = float.MaxValue;
		float band = 0f, grabRange = 0f, sourceMax = 0f;
		string who = null;
		AudioSource whoLoop = null;
		for (int i = 0; i < DragonEnemyAI.ActiveDragons.Count; i++)
		{
			DragonEnemyAI ai = DragonEnemyAI.ActiveDragons[i];
			if (ai == null)
			{
				continue;
			}
			Consider(ai.DistanceToPlayer, ai.grabRange, LoopSource(ai, ref _dragonLoopSource), ai.name,
				ref nearest, ref band, ref grabRange, ref sourceMax, ref who, ref whoLoop);
		}
		for (int i = 0; i < ProximityDragonEnemyAI.ActiveDragons.Count; i++)
		{
			ProximityDragonEnemyAI ai = ProximityDragonEnemyAI.ActiveDragons[i];
			if (ai == null)
			{
				continue;
			}
			Consider(ai.DistanceToPlayer, ai.grabRange, LoopSource(ai, ref _proxDragonLoopSource), ai.name,
				ref nearest, ref band, ref grabRange, ref sourceMax, ref who, ref whoLoop);
		}
		_who = who;
		_whoLoop = whoLoop;
		_whoDistance = nearest;
		_whoBand = band;
		_whoGrabRange = grabRange;
		_whoSourceMax = sourceMax;
		if (who == null)
		{
			_whoRow = null;
			return false;
		}
		bool isWendigo = who.IndexOf("wendigo", StringComparison.OrdinalIgnoreCase) >= 0;
		_whoFirstOnsetSec = isWendigo ? WendigoFirstOnsetSec : DragonFirstOnsetSec;
		_whoRow = (isWendigo ? Plugin.CfgChaserStompWendigoGallery.Value : Plugin.CfgChaserStompDragonGallery.Value)?.Trim();
		return !string.IsNullOrEmpty(_whoRow);
	}

	private static void Consider(float distance, float grabRange, AudioSource loop, string name,
		ref float nearest, ref float band, ref float nearestGrabRange, ref float nearestSourceMax, ref string who, ref AudioSource whoLoop)
	{
		// Vanilla's own answer to "can the player hear this thing coming" - a chaser with no
		// looping source, or one whose loop is stopped, is not part of this at all.
		if (loop == null || !loop.isPlaying)
		{
			return;
		}
		float sourceMax = loop.maxDistance;
		float far = Plugin.CfgChaserStompRange.Value > 0f ? Plugin.CfgChaserStompRange.Value : sourceMax;
		// Outside the band it is audible but not close enough to mean anything; inside it, the
		// nearest one wins, because that is the one this is about.
		if (far <= 0f || distance > far || distance >= nearest)
		{
			return;
		}
		nearest = distance;
		band = far;
		nearestGrabRange = Mathf.Max(0f, grabRange);
		nearestSourceMax = sourceMax;
		who = name;
		whoLoop = loop;
	}

	// `loopingAudioSource` is private and the two AI classes do not share a base, so this is one
	// cached FieldInfo each. A rename upstream turns this off rather than throwing per frame,
	// which is the safe way to be wrong: nothing else depends on it.
	private static AudioSource LoopSource(MonoBehaviour ai, ref FieldInfo cached)
	{
		if (cached == null)
		{
			cached = ai.GetType().GetField("loopingAudioSource", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if (cached == null)
			{
				return null;
			}
		}
		try
		{
			return cached.GetValue(ai) as AudioSource;
		}
		catch (Exception)
		{
			return null;
		}
	}

	private static void Reset()
	{
		_currentRow = null;
		_lostAt = 0f;
		_lastLoggedRow = null;
	}

	/// <summary>Hand the row and the range back at once and forget the approach. A scene change
	/// has nothing to hold or release-after-grace back to.</summary>
	internal static void ResetNow()
	{
		Reset();
		_heldIntensity = 100f;
		_intensityLostAt = 0f;
		_lastLoggedIntensity = null;
		ReleaseIntensityNow();
	}

	private static void ReleaseIntensityNow()
	{
		_heldIntensity = 100f;
		_intensityLostAt = 0f;
		_lastSent = -1;
		// Never restore over the serpent: it lowered the range for a scene of its own and owns
		// putting it back. The stand-down in Tick already clears `_lowered` for that case, so
		// this is the belt to its braces - `ResetIntensity` is a write to one shared number and a
		// spurious one costs a full-amplitude stroke in the middle of an approach.
		if (_lowered && !SerpentHypnosis.OwnsIntensity)
		{
			_lowered = false;
			_lastLoggedIntensity = null;
			Plugin.ResetIntensity("the chaser stomp gave the device back");
		}
	}
}
