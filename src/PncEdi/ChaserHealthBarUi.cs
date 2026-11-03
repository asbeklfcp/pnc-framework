using System;
using HarmonyLib;
using UnityEngine;

namespace PncEdi;

/// <summary>
/// Drives the screen-space MiniBossHealthBarUI for dragon/wendigo (vanilla bar only tracks EnemyAI).
/// </summary>
internal static class ChaserHealthBarUi
{
	private static int _trackedId;

	internal static void Show(MonoBehaviour ai, string displayName, int current, int max)
	{
		if (ai == null)
		{
			return;
		}
		MiniBossHealthBarUI ui = MiniBossHealthBarUI.Instance;
		if (ui == null)
		{
			return;
		}
		_trackedId = ai.GetInstanceID();
        OverkeksMode.CaptureBossBar(ui);
		max = Mathf.Max(1, max);
		current = Mathf.Clamp(current, 0, max);
		Traverse t = Traverse.Create((object)ui);
		t.Field("currentBoss").SetValue(null);
		t.Field("scriptedFill").SetValue(true);
		t.Field("displayedMaxHealth").SetValue(max);
		t.Field("displayedFill").SetValue((float)current / (float)max);
		InvokeNoArg(t, "ApplyFill");
		InvokeSetNumber(t, current, max);
		object nameText = t.Field("enemyNameText").FieldExists() ? t.Field("enemyNameText").GetValue() : null;
		if (nameText != null)
		{
			Traverse nameTraverse = Traverse.Create(nameText);
			if (nameTraverse.Property("text").PropertyExists())
			{
				nameTraverse.Property("text").SetValue(displayName ?? NameRemap.StripCloneSuffix(ai.gameObject.name));
			}
		}
		ui.Show();
	}

	internal static void UpdateHealth(MonoBehaviour ai, int current, int max)
	{
		if (ai == null || _trackedId != ai.GetInstanceID())
		{
			return;
		}
		MiniBossHealthBarUI ui = MiniBossHealthBarUI.Instance;
		if (ui == null)
		{
			return;
		}
		max = Mathf.Max(1, max);
		current = Mathf.Clamp(current, 0, max);
		Traverse t = Traverse.Create((object)ui);
		t.Field("scriptedFill").SetValue(true);
		t.Field("displayedMaxHealth").SetValue(max);
		t.Field("displayedFill").SetValue((float)current / (float)max);
		InvokeNoArg(t, "ApplyFill");
		InvokeSetNumber(t, current, max);
	}

	internal static void Hide(MonoBehaviour ai)
	{
		if (ai != null && _trackedId != 0 && _trackedId != ai.GetInstanceID())
		{
			return;
		}
		Clear();
	}

	internal static void Clear()
	{
        if(_trackedId==0)return;
		_trackedId = 0;
		MiniBossHealthBarUI ui = MiniBossHealthBarUI.Instance;
		if (ui != null)
		{
			ui.ForceHide();
		}
	}

	private static void InvokeNoArg(Traverse t, string method)
	{
		if (t.Method(method, Array.Empty<object>()).MethodExists())
		{
			t.Method(method, Array.Empty<object>()).GetValue();
		}
	}

	private static void InvokeSetNumber(Traverse t, int current, int max)
	{
		object[] args = new object[] { current, max };
		if (t.Method("SetNumberText", args).MethodExists())
		{
			t.Method("SetNumberText", args).GetValue();
		}
	}
}
