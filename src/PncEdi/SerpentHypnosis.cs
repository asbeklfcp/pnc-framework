using System;
using System.Reflection;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PncEdi;

// The Black Serpent's camera grab, scripted by how close it is.
//
// `canHypnotise` on BrawlerEnemyAI, in the game's own tooltip: "it drags the player's camera onto
// itself while it's on-screen and advances until it lands a grab". There is no grab screen behind
// it and no cum clip - the three Hypnosis clips sit on the enemy's own controller beside Idle and
// Walk - which is why §71 left `serpent_hypnosis_*` mapped to skip and said so. That decision was
// right for a dispatch keyed on an animator state: the state alone says "hypnotising" and nothing
// about how close the thing is, so the only script it could have chosen would have been the same
// one at 8 m and at 2 m. What makes this scriptable is the *distance*, which is not in the state.
//
// TWO THINGS THIS DOES THAT NOTHING ELSE IN THE MOD DOES
//
//   * **Distance drives intensity.** It has only ever driven audibility - `AmbientProximity`
//     picks a diorama by whether the player can hear its loop. Nothing else in the mod varies
//     anything continuously; every other choice is a dispatch.
//   * **It changes the device without changing what is playing.** Every other switch in the mod
//     POSTs a different row name. This one POSTs `Edi/Intensity/{max}`, which scales the device's
//     stroke range in place while the same row keeps looping - so closing in changes how far the
//     device travels without changing where it is, with no seam to carry a phase across.
//
// The band is the prefab's own: `hypnosisStartRange` (8 m on the shipped serpent) down to
// `grabRange` (2 m), read off the live component rather than repeated here, so a retuned serpent
// retunes the approach with it. At `grabRange` vanilla lands the grab and `Serpent_Loop` takes
// over from position 0 - which is where this row sits at its seams, so that handoff is stepless.
//
// **THERE WERE TIERS HERE, AND THERE ARE NOT ANY MORE (§125).** Three rows chosen by distance
// became two in §120, because the serpent crosses six metres too fast for a middle rung to be
// held long enough to feel. The 2026-08-25 runs then showed that two rows is not a fix for the
// same problem: the ladder was never slow - it switched 1.50 s and 1.86 s apart, which is the
// dwell it was given and nothing more - it was *coarse*, one step in six metres with three
// unresponsive metres either side. Adding rungs back was the move already rejected. Scaling one
// row is what replaced them, and with it went the tier table, the boundary fractions, the
// hysteresis and the minimum dwell: there are no boundaries to flap across and no crossing that
// can arrive faster than it can be felt, because there is no crossing.
//
// AND IT IS HELD THROUGH A BLINK. The gaze gate below answers per frame, and the 2026-08-24 run
// showed the answer flickering while the player is plainly still being hypnotised.
// `SerpentHypnosisViewGrace` is how long a lost pull keeps what is playing - the row and the
// intensity both - before the filler gets the device back.
//
// AND ONLY WHILE THE CAMERA IS ACTUALLY BEING PULLED. `InPlayerView` below is the gate that was
// missing: the serpent hypnotising and the serpent *doing something to the player* are not the
// same condition, and a player who turns their back is in the second gap.
internal static class SerpentHypnosis
{
	private const int HypnotisingState = 4;
	private static FieldInfo _currentHypnotistField;
	private static FieldInfo _hypnosisInViewField;
	private static MethodInfo _inHypnosisLoopMethod;
	private static float _viewLostAt;
	private static string _lastLogged;
	// The row currently being played. The view grace holds *this* rather than re-deriving it
	// from a distance nobody is being pulled by.
	private static string _currentRow;
	private static int _lastIntensity = -1;
	private static float _lastIntensityAt;
	// Set once this approach has lowered the device's range, and cleared when it is handed back.
	private static bool _intensityLowered;
	private static float _releaseArmedAt;

	/// <summary>
	/// True while the approach is holding the channel's range down - including through the armed
	/// hand-back, which is a range this still owns and has only decided not to give back yet.
	/// `ChaserStomp` stands down on it: both write one channel-wide number, and this one is part
	/// of a scene it is also choosing the row for.
	/// </summary>
	internal static bool OwnsIntensity => _intensityLowered;

	// The row that should be playing right now, or null when no serpent is hypnotising. Cheap
	// enough for the per-frame filler refresh to call: a static field read, a state compare and
	// one distance.
	internal static string CurrentGallery()
	{
		if (!Plugin.CfgEnableSerpentHypnosis.Value)
		{
			Reset();
			return null;
		}
		BrawlerEnemyAI hypnotist = GetHypnotist();
		if (hypnotist == null || (int)hypnotist.CurrentState != HypnotisingState || !InHypnosisLoop(hypnotist))
		{
			Reset();
			return null;
		}
		// The pull can stop and resume inside a second - a step to the side, a doorframe, the
		// serpent's own walk cycle - and dropping the approach on the first false frame is what
		// the 2026-08-24 run felt as a tier "barely having time to switch": it held 1.16 s and
		// went back to filler, not because the serpent had moved but because the gaze test
		// blinked. That is not a distance problem, so nothing in the distance axis could have
		// covered it. A lost pull holds what is playing for a grace, and only a pull that stays
		// lost hands the device back. Nothing is held that was never entered.
		if (!InPlayerView(hypnotist))
		{
			if (string.IsNullOrEmpty(_currentRow))
			{
				Reset();
				return null;
			}
			if (_viewLostAt == 0f)
			{
				_viewLostAt = Time.unscaledTime;
				Plugin.DBG("HYPNOSIS", $"the pull stopped - holding {_currentRow} for up to {Mathf.Max(0f, Plugin.CfgSerpentHypnosisViewGrace.Value):0.0}s");
			}
			if (Time.unscaledTime - _viewLostAt >= Mathf.Max(0f, Plugin.CfgSerpentHypnosisViewGrace.Value))
			{
				Plugin.DBG("HYPNOSIS", "the pull stayed stopped - the approach gives the device back");
				Reset();
				return null;
			}
			// Held, not re-derived: following the distance here would keep tracking a serpent
			// that is no longer doing anything to the player.
			return _currentRow;
		}
		if (_viewLostAt != 0f)
		{
			_viewLostAt = 0f;
			Plugin.DBG("HYPNOSIS", "the pull resumed inside the grace - the approach kept the row");
		}
		return Approach(hypnotist);
	}

	// Vanilla keeps a single hypnotist in a private static field - set in StartHypnosis when it
	// is null, cleared in StopHypnosis by whoever owns it - so there is no scene search to do
	// and no chance of picking the wrong serpent when two are alive.
	private static BrawlerEnemyAI GetHypnotist()
	{
		if (_currentHypnotistField == null)
		{
			_currentHypnotistField = typeof(BrawlerEnemyAI).GetField("currentHypnotist", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
			if (_currentHypnotistField == null)
			{
				return null;
			}
		}
		return _currentHypnotistField.GetValue(null) as BrawlerEnemyAI;
	}

	// The gaze-pull, the advance and the grab all happen only while the looping clip is playing:
	// Hypnosis Start is a wind-up telegraph and Hypnosis End a wind-down, and vanilla gates all
	// three behaviours on this. Scripting the wind-up would put the device ahead of the mechanic.
	private static bool InHypnosisLoop(BrawlerEnemyAI ai)
	{
		if (_inHypnosisLoopMethod == null)
		{
			_inHypnosisLoopMethod = typeof(BrawlerEnemyAI).GetMethod("InHypnosisLoop", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if (_inHypnosisLoopMethod == null)
			{
				// A rename upstream should not silently stop the ladder; the state gate alone
				// is a slightly wider window and is the safe way to be wrong here.
				return true;
			}
		}
		try
		{
			return (bool)_inHypnosisLoopMethod.Invoke(ai, null);
		}
		catch (Exception)
		{
			return true;
		}
	}

	// The gaze-pull is vanilla's alone to decide, and it decides with one private bool.
	// `HandleHypnotisingState` sets `hypnosisInView = IsPlayerDead || IsInPlayerView()` every
	// tick, and `LateUpdate` pulls the camera only when it is true - so a player who turns their
	// back is still being *approached* but is no longer being *pulled*, and vanilla even stops
	// the advance with it (`followerEntity.simulateMovement = flag && hypnosisInView`). Reading
	// the field rather than recomputing visibility means the device is gated on exactly the same
	// answer the camera is, one frame fresh, with no second view test to disagree with the first.
	private static bool InPlayerView(BrawlerEnemyAI ai)
	{
		if (_hypnosisInViewField == null)
		{
			_hypnosisInViewField = typeof(BrawlerEnemyAI).GetField("hypnosisInView", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if (_hypnosisInViewField == null)
			{
				// Same posture as InHypnosisLoop: a rename upstream widens the window rather
				// than silently killing the ladder.
				return true;
			}
		}
		try
		{
			return (bool)_hypnosisInViewField.GetValue(ai);
		}
		catch (Exception)
		{
			return true;
		}
	}

	// One row, and the distance drives how far the device travels rather than which script it is
	// playing.
	//
	// The ladder was never slow - the 2026-08-25 run switched tiers 1.50 s and 1.86 s apart,
	// which is `SerpentHypnosisMinDwell` and nothing else - it was *coarse*. Two tiers over the
	// 8 m-to-2 m band is one step, at 5 m, so most of the approach produced no change at all,
	// and adding tiers back is the move the 2026-08-23 run already rejected: a step too short to
	// register is not a step.
	//
	// What makes the third option possible is that Edi can change amplitude **without changing
	// what is playing**. `POST /Edi/Intensity/{max}` scales the device's stroke range between the
	// bounds configured for it; DeviceBase debounces the write 100 ms and applies it in place, and
	// nothing re-dispatches the gallery. Measured rather than believed (§106 is the standing
	// warning about this exact shortcut): against a Handy 2 Pro with the patched Edi this project
	// ships, `v2/slide` max followed 100/70/40/20 step for step and the playback phase stayed
	// within 22 ms of prediction across six changes, one of them across a loop seam.
	//
	// So the tiers, the hysteresis and the dwell all go: there are no boundaries to flap across
	// and no crossing that can arrive too fast to feel, because there is no crossing. The view
	// grace stays and means what it always did - a lost pull holds what is playing, which on this
	// path is the last intensity as well as the row.
	//
	// The two rate limits are not tuning knobs so much as the shape of the transport. Distance
	// changes every frame and the device cannot feel a 1% step, so `SerpentHypnosisIntensityStep`
	// is what keeps a walk down the band from becoming sixty requests a second, and
	// `SerpentHypnosisIntensityInterval` sits at Edi's own 100 ms debounce, below which a write
	// is discarded before it reaches the hardware anyway.
	private static string Approach(BrawlerEnemyAI ai)
	{
		string row = Plugin.CfgSerpentHypnosisGallery.Value;
		if (string.IsNullOrWhiteSpace(row))
		{
			Reset();
			return null;
		}
		row = row.Trim();
		float band = Mathf.Max(0.01f, ai.hypnosisStartRange - ai.grabRange);
		float bandFraction = Mathf.Clamp01((ai.DistanceToPlayer - ai.grabRange) / band);
		int far = Mathf.Clamp(Plugin.CfgSerpentHypnosisIntensityFar.Value, 0, 100);
		int near = Mathf.Clamp(Plugin.CfgSerpentHypnosisIntensityNear.Value, 0, 100);
		// 1 is the far edge of the band, 0 is grabRange.
		int intensity = Mathf.Clamp(Mathf.RoundToInt(Mathf.Lerp(near, far, bandFraction)), 0, 100);
		int step = Mathf.Max(1, Plugin.CfgSerpentHypnosisIntensityStep.Value);
		float interval = Mathf.Max(0f, Plugin.CfgSerpentHypnosisIntensityInterval.Value);
		// The first reading of an approach always goes, whatever the step says: it is what sets
		// the device to the far end instead of leaving it wherever the last scene left it.
		bool first = _lastIntensity < 0;
		if (first || (Mathf.Abs(intensity - _lastIntensity) >= step
			&& Time.unscaledTime - _lastIntensityAt >= interval))
		{
			_lastIntensity = intensity;
			_lastIntensityAt = Time.unscaledTime;
			_intensityLowered = true;
			// A pending hand-back is cancelled by the approach resuming, which is the whole point
			// of delaying it - see Reset.
			_releaseArmedAt = 0f;
			Plugin.SendIntensity(intensity, $"serpent at {ai.DistanceToPlayer:0.0}m of {ai.hypnosisStartRange:0.0}-{ai.grabRange:0.0}");
		}
		if (_lastLogged != row)
		{
			_lastLogged = row;
			Plugin.DBG("HYPNOSIS", $"{ai.DistanceToPlayer:0.0}m of {ai.hypnosisStartRange:0.0}-{ai.grabRange:0.0} -> {row}, intensity by distance {far}-{near}%");
		}
		_currentRow = row;
		return row;
	}

	/// <summary>
	/// Give the device back, and with it its full range.
	///
	/// Intensity is global to the channel rather than a property of a row, so the ladder owns
	/// restoring it: a hypnosis that ended at 44% and did not put it back would leave every
	/// later scene quieter, with nothing on screen to say why. Called every frame that no
	/// serpent is hypnotising, and from the scene change, so it has to be free when there is
	/// nothing to undo - `SendIntensity` drops a repeat of the value it last sent.
	/// </summary>
	internal static void Reset()
	{
		_viewLostAt = 0f;
		_lastLogged = null;
		_currentRow = null;
		_lastIntensity = -1;
		ArmRelease();
	}

	// **Not immediately**, and the first 2026-08-25 run is why. The view grace expired at 26.793
	// and the approach resumed 91 ms later, so the device was handed the filler at full range and
	// taken back down to 84% within a tenth of a second - one full-amplitude stroke belonging to
	// neither scene. The row hand-back has to be instant, because something has to be playing;
	// the *range* does not, because for a tenth of a second whatever plays sounds right at either
	// range and wrong only if the wait drags on.
	private static void ArmRelease()
	{
		if (_intensityLowered && _releaseArmedAt == 0f)
		{
			_releaseArmedAt = Time.unscaledTime;
		}
	}

	/// <summary>
	/// Any row that is not the approach's own takes the range back with it.
	///
	/// The second 2026-08-25 run is what this is for. A serpent grab landed - the one seam the
	/// ladder had never reached - and `Serpent_Loop` then played the **whole 15 s grab** at the
	/// 97% the approach had left behind, because the ladder is ticked from the filler refresh and
	/// the filler refresh does not run during a grab scene. Nothing was audibly wrong at 97%, and
	/// that is exactly why it is worth fixing now: the grab lands at `grabRange`, where the ramp
	/// is near its top by construction, so the leak is invisible until someone widens the band
	/// (`SerpentHypnosisIntensityFar` is the tuning knob this file recommends) or gets grabbed by
	/// something else mid-approach - a nun grab at 44% of its authored travel, with nothing to say
	/// why.
	///
	/// Arming rather than releasing keeps the flap fix above: a `filler` dispatched between two
	/// halves of one approach arms a release the resuming approach then cancels.
	/// </summary>
	internal static void NoteRowPlayed(string row)
	{
		if (!_intensityLowered || string.IsNullOrEmpty(row))
		{
			return;
		}
		string ours = Plugin.CfgSerpentHypnosisGallery.Value;
		if (string.IsNullOrWhiteSpace(ours) || !row.Trim().Equals(ours.Trim(), StringComparison.OrdinalIgnoreCase))
		{
			ArmRelease();
		}
	}

	/// <summary>
	/// Run the armed hand-back. Called every frame from `Plugin.Update` rather than from the
	/// ladder itself, because the case that needs it most - a grab scene - is exactly when the
	/// ladder is not being asked anything.
	/// </summary>
	internal static void TickIntensityRelease()
	{
		if (_releaseArmedAt != 0f
			&& Time.unscaledTime - _releaseArmedAt >= Mathf.Max(0f, Plugin.CfgSerpentHypnosisIntensityRelease.Value))
		{
			ReleaseIntensityNow();
		}
	}

	/// <summary>Hand the range back with no delay: a scene change has no next approach to wait for.</summary>
	internal static void ResetNow()
	{
		Reset();
		ReleaseIntensityNow();
	}

	private static void ReleaseIntensityNow()
	{
		if (_intensityLowered)
		{
			_intensityLowered = false;
			_releaseArmedAt = 0f;
			Plugin.ResetIntensity("the serpent gave the device back");
		}
	}
}
