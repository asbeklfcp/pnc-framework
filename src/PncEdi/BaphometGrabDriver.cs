using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

namespace PncEdi;

/// <summary>
/// Gallery_Baphomet_Grabbed has no Start→Loop exit-time transitions.
/// Drive start (once) → loop (until max heat) → cum (once) → loop, and push EDI rows explicitly.
/// </summary>
internal sealed class BaphometGrabDriver : MonoBehaviour
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
		StartState = start;
		LoopState = loop;
		CumState = cum;
		EdiStart = ediStart;
		EdiLoop = ediLoop;
		EdiCum = ediCum;
		_phase = Phase.Start;
		_phaseStartedAt = Time.time;
		_cumPlayedOnce = false;
		ProbePlayableStates(ediStart != null && ediStart.EndsWith("2"));
		_startClipLength = EstimateClipLength(Drive, StartState, 1.5f);
		PlayBoth(StartState);
		NotifyEdi(EdiStart);
		Plugin.DBG("GRAB-START", "baphomet_gargoyle driver begin start='" + StartState + "' loop='" + LoopState + "' cum='" + CumState + "' edi=" + EdiStart + "/" + EdiLoop + "/" + EdiCum + " len=" + _startClipLength.ToString("0.00"));
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
			// Only accept Start2-family — never silently fall back to Start.
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
			if (string.IsNullOrEmpty(name))
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
			string clip = CurrentClipName(animator);
			if (!string.IsNullOrEmpty(clip))
			{
				bool clipIsSet2 = clip.IndexOf("riding", System.StringComparison.OrdinalIgnoreCase) >= 0
					|| clip.IndexOf("Start2", System.StringComparison.OrdinalIgnoreCase) >= 0
					|| clip.IndexOf("_2", System.StringComparison.OrdinalIgnoreCase) >= 0;
				if (set2 == clipIsSet2)
				{
					return name;
				}
			}
		}
		return candidates[0];
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

	private static string FirstPlayable(Animator animator, params string[] candidates)
	{
		return FirstPlayableStrict(animator, set2: false, candidates);
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
		switch (_phase)
		{
		case Phase.Start:
			if (StartFinished(info))
			{
				EnterPhase(Phase.Loop, LoopState, EdiLoop, "Start → Loop");
			}
			break;
		case Phase.Loop:
			if (maxHeat && !string.IsNullOrEmpty(CumState) && !_cumPlayedOnce)
			{
				_cumPlayedOnce = true;
				_cumClipLength = EstimateClipLength(Drive, CumState, 2f);
				EnterPhase(Phase.Cum, CumState, EdiCum, "Loop → Cum");
			}
			break;
		case Phase.Cum:
			if (CumFinished(info))
			{
				// One cum, then back to loop (clear MaxHeat so vanilla can fire again later).
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
		Plugin.DBG("GRAB-START", "baphomet_gargoyle advanced " + log + " state='" + state + "' edi='" + ediRow + "'");
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
			Plugin.DBG("GRAB-START", "baphomet_gargoyle EDI → " + ediRow);
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
		if (string.IsNullOrEmpty(want))
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

	private bool StartFinished(AnimatorStateInfo info)
	{
		float elapsed = Time.time - _phaseStartedAt;
		if (!string.IsNullOrEmpty(StartState) && info.IsName(StartState))
		{
			return info.normalizedTime >= 0.98f || elapsed >= _startClipLength * 0.95f;
		}
		string clip = CurrentClipName(Drive);
		if (!string.IsNullOrEmpty(clip) && clip.IndexOf("start", System.StringComparison.OrdinalIgnoreCase) >= 0)
		{
			return info.normalizedTime >= 0.98f || elapsed >= _startClipLength * 0.95f;
		}
		return elapsed >= Mathf.Max(_startClipLength, 1.2f);
	}

	private bool CumFinished(AnimatorStateInfo info)
	{
		float elapsed = Time.time - _phaseStartedAt;
		if (!string.IsNullOrEmpty(CumState) && info.IsName(CumState))
		{
			return info.normalizedTime >= 0.98f || elapsed >= _cumClipLength * 0.95f;
		}
		return elapsed >= Mathf.Max(_cumClipLength, 1.5f);
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
			if (len > 0.05f)
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
			if (Host != null)
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
