using UnityEngine;

namespace PncEdi;

/// <summary>Marks a gargoyle clone that should play Baphomet grab scenes.</summary>
internal sealed class BaphometGargoyleMarker : MonoBehaviour
{
    internal bool SharedPresentation;

	/// <summary>"start" = Start/Loop/Cum (EDI Baphomet_Start…); "start2" = Start2/Loop2/Cum2.</summary>
	internal string VariantLabel;

	internal bool IsSet2;

	internal string GrabStartState;

	internal string GrabLoopState;

	internal string GrabCumState;

	internal string EdiStart;

	internal string EdiLoop;

	internal string EdiCum;

	internal string[] GrabStateNames;

	private float _nextStripAt;

	private void OnEnable()
	{
		StripBaseNow();
		_nextStripAt = Time.unscaledTime + 0.15f;
	}

	private void LateUpdate()
	{
        if(SharedPresentation)return;
		if (Time.unscaledTime < _nextStripAt)
		{
			return;
		}
		_nextStripAt = Time.unscaledTime + 0.15f;
		StripBaseNow();
	}

	internal void StripBaseNow()
	{
		Renderer[] renderers = GetComponentsInChildren<Renderer>(includeInactive: true);
		for (int i = 0; i < renderers.Length; i++)
		{
			Renderer renderer = renderers[i];
			if (renderer == null)
			{
				continue;
			}
			if (renderer.GetComponentInParent<BaphometStatueVisual>() != null)
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
