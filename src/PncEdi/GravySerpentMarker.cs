using UnityEngine;

namespace PncEdi;

/// <summary>Marks a serpent clone that should play Gravy/Minotaur grab scenes.</summary>
internal sealed class GravySerpentMarker : MonoBehaviour
{
    internal bool SharedPresentation;

	/// <summary>"start" = Start/Loop/Cum (EDI Gravy_Start…); "start2" = Start2/Loop2/Cum2.</summary>
	internal string VariantLabel;

	internal bool IsSet2;

	internal string GrabStartState;

	internal string GrabLoopState;

	internal string GrabCumState;

	internal string EdiStart;

	internal string EdiLoop;

	internal string EdiCum;

	internal string[] GrabStateNames;

	/// <summary>Only strip serpent renderers after a real Gravy visual is attached.</summary>
	internal bool StripBaseRenderers;

	private float _nextStripAt;

	private void OnEnable()
	{
		if (StripBaseRenderers)
		{
			StripBaseNow();
		}
		_nextStripAt = Time.unscaledTime + 0.15f;
	}

	private void LateUpdate()
	{
        if(SharedPresentation)return;
		if (!StripBaseRenderers)
		{
			return;
		}
		if (Time.unscaledTime < _nextStripAt)
		{
			return;
		}
		_nextStripAt = Time.unscaledTime + 0.15f;
		StripBaseNow();
	}

	internal void StripBaseNow()
	{
		if (!StripBaseRenderers)
		{
			return;
		}
		Renderer[] renderers = GetComponentsInChildren<Renderer>(includeInactive: true);
		for (int i = 0; i < renderers.Length; i++)
		{
			Renderer renderer = renderers[i];
			if (renderer == null)
			{
				continue;
			}
			if (renderer.GetComponentInParent<GravyVisual>() != null)
			{
				continue;
			}
			if (renderer.enabled)
			{
				renderer.enabled = false;
			}
		}
	}
}
