using System.Reflection;
using UnityEngine;

namespace PncEdi;

/// <summary>
/// Gallery_Gravy_Grabbed has no Start→Loop exit-time transitions.
/// Drive start (once) → loop (until max heat) → cum (once) → loop, and push EDI rows explicitly.
/// </summary>
internal sealed class GravyGrabDriver : MonoBehaviour
{
	internal Animator Drive;

	internal Animator Host;

	internal string StartState;

	internal string LoopState;

	internal string CumState;

	internal string EdiStart;

	internal string EdiLoop;

	internal string EdiCum;

	private enum Phase
	{
		Start,
		Loop,
		Cum
	}

	private Phase _phase = Phase.Start;

	private float _phaseStartedAt;

	private float _startClipLength = 1.5f;

	private float _cumClipLength = 2f;

	private bool _cumPlayedOnce;

	private FieldInfo _maxHeatPlayingField;

	private FieldInfo _grabScreenAnimatorField;

	internal void Begin(Animator drive, Animator host, string start, string loop, string cum, string ediStart, string ediLoop, string ediCum)
	{
		Drive = drive;
		Host = host;
		bool set2 = ediStart != null && ediStart.EndsWith("2");
		// Always prefer short gallery states — long clip asset names stall Start forever.
		StartState = set2 ? "Start2" : "Start";
		LoopState = set2 ? "Loop2" : "Loop";
		CumState = set2 ? "Cum2" : "Cum";
		if (!string.IsNullOrEmpty(start) && IsUsableStateName(start))
		{
			StartState = start;
		}
		if (!string.IsNullOrEmpty(loop) && IsUsableStateName(loop))
		{
			LoopState = loop;
		}
		if (!string.IsNullOrEmpty(cum) && IsUsableStateName(cum))
		{
			CumState = cum;
		}
		EdiStart = ediStart;
		EdiLoop = ediLoop;
		EdiCum = ediCum;
		_phase = Phase.Start;
		_phaseStartedAt = Time.time;
		_cumPlayedOnce = false;
		ProbePlayableStates(set2);
		_startClipLength = Mathf.Clamp(EstimateClipLength(Drive, StartState, 1.5f), 0.6f, 3.5f);
		PlayBoth(StartState);
		NotifyEdi(EdiStart);
		Plugin.DBG("GRAB-START", "gravy_serpent driver begin start='" + StartState + "' loop='" + LoopState + "' cum='" + CumState + "' edi=" + EdiStart + "/" + EdiLoop + "/" + EdiCum + " len=" + _startClipLength.ToString("0.00"));
	}

	private static bool IsUsableStateName(string name)
	{
		if (string.IsNullOrEmpty(name))
		{
			return false;
		}
		if (name.IndexOf("Gallery_", System.StringComparison.OrdinalIgnoreCase) >= 0)
		{
			return false;
		}
		return name.Length < 24;
	}

	private void ProbePlayableStates(bool set2)
	{
		Animator probe = Drive != null ? Drive : Host;
		if (probe == null)
		{
			return;
		}
		if (set2)
		{
			StartState = FirstPlayableStrict(probe, true, StartState, "Start2");
			LoopState = FirstPlayableStrict(probe, true, LoopState, "Loop2");
			CumState = FirstPlayableStrict(probe, true, CumState, "Cum2");
		}
		else
		{
			StartState = FirstPlayableStrict(probe, false, StartState, "Start");
			LoopState = FirstPlayableStrict(probe, false, LoopState, "Loop");
			CumState = FirstPlayableStrict(probe, false, CumState, "Cum");
		}
	}

	private static string FirstPlayableStrict(Animator animator, bool set2, params string[] candidates)
	{
		if (animator == null || candidates == null || candidates.Length == 0)
		{
			return candidates != null && candidates.Length > 0 ? candidates[0] : null;
		}
		for (int i = 0; i < candidates.Length; i++)
		{
			string name = candidates[i];
			if (string.IsNullOrEmpty(name) || !IsUsableStateName(name))
			{
				continue;
			}
			if (set2 && IsSet1OnlyName(name))
			{
				continue;
			}
			if (!set2 && IsSet2OnlyName(name))
			{
				continue;
			}
			try
			{
				animator.Play(name, 0, 0f);
				animator.Update(0f);
			}
			catch
			{
				continue;
			}
			AnimatorStateInfo info = animator.GetCurrentAnimatorStateInfo(0);
			if (info.IsName(name))
			{
				return name;
			}
		}
		return set2 ? "Start2" : "Start";
	}

	private static bool IsSet2OnlyName(string name)
	{
		string t = name.ToLowerInvariant();
		return t.Contains("start2") || t.Contains("loop2") || t.Contains("cum2") || t.Contains("riding") || t.EndsWith("2");
	}

	private static bool IsSet1OnlyName(string name)
	{
		string t = name.ToLowerInvariant();
		if (IsSet2OnlyName(name))
		{
			return false;
		}
		return t == "start" || t == "loop" || t == "cum" || t.Contains("bj");
	}

	private void LateUpdate()
	{
        if(Time.timeScale<=.0001f)return;
		if (Drive == null || !Drive.isActiveAndEnabled)
		{
			return;
		}
		GrabScreen grabScreen = GrabScreen.Instance;
		if (grabScreen == null || !grabScreen.IsGrabbed)
		{
			return;
		}
		AnimatorStateInfo info = Drive.GetCurrentAnimatorStateInfo(0);
		bool maxHeat = IsMaxHeat(grabScreen);
		float elapsed = Time.time - _phaseStartedAt;
		switch (_phase)
		{
		case Phase.Start:
			if (StartFinished(info, elapsed))
			{
				EnterPhase(Phase.Loop, LoopState, EdiLoop, "Start → Loop");
			}
			break;
		case Phase.Loop:
			if (maxHeat && !string.IsNullOrEmpty(CumState) && !_cumPlayedOnce)
			{
				_cumPlayedOnce = true;
				_cumClipLength = Mathf.Clamp(EstimateClipLength(Drive, CumState, 2f), 0.8f, 5f);
				EnterPhase(Phase.Cum, CumState, EdiCum, "Loop → Cum");
			}
			break;
		case Phase.Cum:
			if (CumFinished(info, elapsed))
			{
				try
				{
					if (Host != null)
					{
						Host.SetBool("MaxHeat", value: false);
					}
				}
				catch
				{
				}
				try
				{
					grabScreen.OnMaxHeatAnimationComplete();
				}
				catch
				{
				}
				_cumPlayedOnce = false;
				EnterPhase(Phase.Loop, LoopState, EdiLoop, "Cum → Loop");
			}
			break;
		}
		SyncHostIfNeeded();
	}

	private void EnterPhase(Phase phase, string state, string ediRow, string log)
	{
		_phase = phase;
		_phaseStartedAt = Time.time;
		PlayBoth(state);
		NotifyEdi(ediRow);
		Plugin.DBG("GRAB-START", "gravy_serpent advanced " + log + " state='" + state + "' edi='" + ediRow + "'");
	}

	private void NotifyEdi(string ediRow)
	{
		if (string.IsNullOrEmpty(ediRow))
		{
			return;
		}
		try
		{
			Plugin.Instance?.ResetGrabStepHash();
			Plugin.SendPlay(ediRow, loop: true, inGame: true, filler: false, preservePhase: false);
			Plugin.DBG("GRAB-START", "gravy_serpent EDI → " + ediRow);
		}
		catch
		{
		}
	}

	private void SyncHostIfNeeded()
	{
		if (Host == null || !Host.isActiveAndEnabled)
		{
			return;
		}
		string want = _phase switch
		{
			Phase.Start => StartState,
			Phase.Loop => LoopState,
			Phase.Cum => CumState,
			_ => LoopState
		};
		if (string.IsNullOrEmpty(want) || !IsUsableStateName(want))
		{
			return;
		}
		AnimatorStateInfo hostInfo = Host.GetCurrentAnimatorStateInfo(0);
		if (hostInfo.IsName(want))
		{
			return;
		}
		try
		{
			float t = Drive != null ? Mathf.Min(Drive.GetCurrentAnimatorStateInfo(0).normalizedTime, 0.99f) : 0f;
			Host.Play(want, 0, t);
			Host.Update(0f);
		}
		catch
		{
		}
	}

	private bool StartFinished(AnimatorStateInfo info, float elapsed)
	{
		// Hard cap — never stall on Start even if state/clip names mismatch.
		if (elapsed >= _startClipLength * 0.92f)
		{
			return true;
		}
		if (!string.IsNullOrEmpty(StartState) && info.IsName(StartState) && info.normalizedTime >= 0.95f)
		{
			return true;
		}
		string clip = CurrentClipName(Drive);
		if (!string.IsNullOrEmpty(clip) && clip.IndexOf("start", System.StringComparison.OrdinalIgnoreCase) >= 0 && info.normalizedTime >= 0.95f)
		{
			return true;
		}
		return false;
	}

	private bool CumFinished(AnimatorStateInfo info, float elapsed)
	{
		if (elapsed >= _cumClipLength * 0.92f)
		{
			return true;
		}
		if (!string.IsNullOrEmpty(CumState) && info.IsName(CumState) && info.normalizedTime >= 0.95f)
		{
			return true;
		}
		return false;
	}

	private static float EstimateClipLength(Animator animator, string state, float fallback)
	{
		if (animator == null || string.IsNullOrEmpty(state))
		{
			return fallback;
		}
		try
		{
			animator.Play(state, 0, 0f);
			animator.Update(0f);
			AnimatorClipInfo[] clips = animator.GetCurrentAnimatorClipInfo(0);
			if (clips != null && clips.Length != 0 && clips[0].clip != null && clips[0].clip.length > 0.05f)
			{
				return clips[0].clip.length;
			}
			float len = animator.GetCurrentAnimatorStateInfo(0).length;
			if (len > 0.05f && len < 30f)
			{
				return len;
			}
		}
		catch
		{
		}
		return fallback;
	}

	private static string CurrentClipName(Animator animator)
	{
		if (animator == null)
		{
			return null;
		}
		AnimatorClipInfo[] clips = animator.GetCurrentAnimatorClipInfo(0);
		if (clips != null && clips.Length != 0 && clips[0].clip != null)
		{
			return clips[0].clip.name;
		}
		return null;
	}

	private void PlayBoth(string state)
	{
		if (string.IsNullOrEmpty(state))
		{
			return;
		}
		try
		{
			if (Drive != null)
			{
				Drive.Play(state, 0, 0f);
				Drive.Update(0f);
			}
		}
		catch
		{
		}
		try
		{
			if (Host != null && IsUsableStateName(state))
			{
				Host.Play(state, 0, 0f);
				Host.Update(0f);
			}
		}
		catch
		{
		}
	}

	private bool IsMaxHeat(GrabScreen grabScreen)
	{
		if (_maxHeatPlayingField == null)
		{
			_maxHeatPlayingField = typeof(GrabScreen).GetField("maxHeatAnimationPlaying", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		}
		if (_maxHeatPlayingField != null && _maxHeatPlayingField.GetValue(grabScreen) is bool playing && playing)
		{
			return true;
		}
		Animator host = Host;
		if (host == null)
		{
			if (_grabScreenAnimatorField == null)
			{
				_grabScreenAnimatorField = typeof(GrabScreen).GetField("grabScreenAnimator", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			}
			host = _grabScreenAnimatorField?.GetValue(grabScreen) as Animator;
		}
		if (host != null)
		{
			try
			{
				return host.GetBool("MaxHeat");
			}
			catch
			{
			}
		}
		return false;
	}
}
