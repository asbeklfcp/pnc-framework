using UnityEngine;

namespace PncEdi;

// One-line descriptions of where the game thinks the screen is and where a UI element actually
// landed on it, printed once per run for the escape hint and once for the game's game-over
// prompt.
//
// Added after "both of those sit too low and are cut off on Linux" turned out to be a window
// manager placing the window partly off the display - the game and the mod were both correct
// (§76). That is the value of the lines: an element genuinely off-canvas and an element the
// compositor has pushed off-screen look identical to the eye, and these numbers separate them
// in one glance instead of a session. Both live UI elements sit in the bottom ~70 px, so this
// class of report will happen again.
internal static class ScreenDiag
{
	internal static string Describe()
	{
		Resolution res = Screen.currentResolution;
		Rect safe = Screen.safeArea;
		return $"screen={Screen.width}x{Screen.height} desktop={res.width}x{res.height} " +
			$"mode={Screen.fullScreenMode} dpi={Screen.dpi} " +
			$"safeArea=({safe.x},{safe.y},{safe.width},{safe.height})";
	}

	// For a Screen Space - Overlay canvas the world corners ARE screen pixels, so a bottom edge
	// below 0 or a top edge above Screen.height is the element hanging off the display.
	internal static string DescribeRect(RectTransform rect)
	{
		if (rect == null)
		{
			return "rect=<null>";
		}
		Vector3[] corners = new Vector3[4];
		rect.GetWorldCorners(corners);
		Canvas canvas = rect.GetComponentInParent<Canvas>();
		string scale = (canvas != null)
			? $" canvasScale={canvas.scaleFactor:0.###} render={canvas.renderMode}"
			: " canvas=<none>";
		return $"rect x={corners[0].x:0}..{corners[2].x:0} y={corners[0].y:0}..{corners[1].y:0}" + scale;
	}
}
