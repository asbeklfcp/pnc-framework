using UnityEngine;

namespace PncEdi;

/// <summary>
/// Keeps base serpent/gargoyle renderers hidden when a reskin visual (Gravy / Baphomet statue) is attached.
/// Vanilla EndGrabHidden / EnemyReactivationHelper re-enable every Renderer — this puts the strip back.
/// </summary>
internal static class ReskinBaseStrip
{
	internal static void Enforce(GameObject enemy)
	{
		if (enemy == null)
		{
			return;
		}
		GravySerpentMarker gravy = enemy.GetComponent<GravySerpentMarker>();
		if (gravy != null)
		{
			if (enemy.GetComponentInChildren<GravyVisual>(includeInactive: true) != null)
			{
				gravy.StripBaseRenderers = true;
				gravy.StripBaseNow();
			}
			return;
		}
		BaphometGargoyleMarker baphomet = enemy.GetComponent<BaphometGargoyleMarker>();
		if (baphomet != null && enemy.GetComponentInChildren<BaphometStatueVisual>(includeInactive: true) != null)
		{
			baphomet.StripBaseNow();
		}
	}

	internal static bool IsReskinEnemy(GameObject enemy)
	{
		if (enemy == null)
		{
			return false;
		}
		return enemy.GetComponent<GravySerpentMarker>() != null
			|| enemy.GetComponent<BaphometGargoyleMarker>() != null;
	}
}
