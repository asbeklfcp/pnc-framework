using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace PncEdi;

// Death while something is grappling you plays out as that creature's trio scene instead of
// being deferred behind a fake 1 HP. The player's health stays at 0, struggling stops working,
// and more of whatever is clinging is called in until vanilla's trio overflow escalates the
// grapple into the GrabScreen scene - which then counts as the game over via HeatLockSystem's
// pending flag.
//
// Reinforcement spawns are normally optional (GrappleReinforcement), but the sequence needs a
// full pile to reach the overflow, so while it runs the spawner is forced on and runs on its own
// faster interval.
//
// Every stage has a bail-out. This is a death the player cannot escape, so a stall here
// would strand the run exactly like the softlock this replaced.
//
// **It was imp-only until §114, and that was a hole rather than a policy.** The clinging enemies
// are what make the sequence necessary in the first place: a grapple owns the screen, so no grab
// can start while one runs, and the death needs a grab. With goonshrooms attached the sequence
// declined and the old revive-to-1-HP path took over - which is being invulnerable while
// grappled, the exact behaviour this replaced. Vanilla is family-neutral here and always was:
// `TriggerTrioGrabOverflow` reads `trioGrabAnimatorController` and the camera offsets off
// whichever clinger it picks, and the goonshroom prefab carries `GoonShroom_GrabScreen` the same
// way the imp carries `Imp_Grab_Screen`. Read out of the prefabs before this was widened, not
// assumed.
//
// One thing this now leans on: forcing max heat drives the goonshroom's grab screen into its cum
// clip, whose animator parameter is `Max Heat` rather than `MaxHeat` on that one controller.
// §109's `GrabScreenHeatParam` is what makes that fire; without it this death would reach the
// scene and the scene would sit on its loop.
[HarmonyPatch]
public static class GrappleDeathSequence
{
	// Vanilla's own ceiling, not ours: maxGrappleCount is serialized per prefab (3 on the shipped
	// GrappleScreenobject) and StartGrapple refuses past it, so a hardcoded 3 would stall the
	// sequence forever on a prefab tuned to 2.
	private const int FallbackMaxGrapplers = 3;
	private static bool _active;
	private static string _family;
	private static float _startedAt;
	private static float _overflowForcedAt;
	private static Text _grappleText;
	internal static bool Active => _active;
	internal static bool Enabled => Plugin.GameplayTweaksEnabled && (OverkeksMode.UsesRecipe ? OverkeksConfig.CfgGrappleDeathSequence : Plugin.CfgGrappleDeathSequence).Value;

	// True when this sequence owns the death, so the caller must not fall back to the
	// revive-to-1-HP path. Damage keeps arriving after health hits 0 (Die is blocked, so
	// isDead never latches and every further tick re-enters Die), and each of those must
	// answer "already handled" rather than starting the old behaviour.
	internal static bool HandleDeath(GrappleScreenobject grapple)
	{
		return _active || TryBegin(grapple);
	}

	internal static bool TryBegin(GrappleScreenobject grapple)
	{
		if (!Enabled || _active || grapple == null || !grapple.IsGrappling)
		{
			return false;
		}
		GrabScreen grabScreen = GrabScreen.Instance;
		if (grabScreen != null && grabScreen.IsGrabbed)
		{
			return false;
		}
		// Any clinging family, but a known one: an unresolvable family means the enemy list is
		// unreadable, and this is not a sequence to begin on a guess.
		string family = ImpGrappleGate.ActiveGrappleFamily();
		if (string.IsNullOrEmpty(family))
		{
			return false;
		}
		_family = family;
		_active = true;
		_startedAt = Time.time;
		_overflowForcedAt = 0f;
		ResetShakeProgress(grapple);
		CacheGrappleText(grapple);
		// Arms the game over without reviving the player - EnsureSceneEntrySurvival would
		// put them back to 1 HP, which is exactly the "invulnerable during grapple" feel
		// this replaces.
		HeatLockSystem.ArmPendingGameOver(_family + " grapple death");
		Plugin.DBG("GRAPPLE-DEATH", "begin at " + grapple.GrappleCount + " " + _family + "(s) - struggle locked, calling in the rest");
		return true;
	}

	internal static void Reset(string reason)
	{
		if (_active)
		{
			_active = false;
			_family = null;
			_grappleText = null;
			Plugin.DBG("GRAPPLE-DEATH", "end (" + reason + ")");
		}
	}

	internal static void Tick()
	{
		if (!_active)
		{
			return;
		}
		GrappleScreenobject grappleScreenobject = GrappleScreenobject.Instance;
		GrabScreen grabScreen = GrabScreen.Instance;
		bool grabbed = grabScreen != null && grabScreen.IsGrabbed;
		if (grappleScreenobject == null || !grappleScreenobject.IsGrappling)
		{
			// The grapple handed off to the trio scene: nothing left to drive. Its end
			// fires the pending game over through CompleteSceneEntrySurvival.
			if (!grabbed)
			{
				Reset("grapple ended");
			}
			return;
		}
		if (grabbed)
		{
			return;
		}
		ApplyDeathGrappleText();
		if (grappleScreenobject.GrappleCount < MaxGrapplers(grappleScreenobject))
		{
			// GrappleReinforcement does the spawning; it checks Active to override the
			// user's on/off setting and to use the faster interval.
			return;
		}
		// A full pile. Vanilla's CheckTrioGrabOverflow wants max heat as well, so give
		// it that rather than calling the private escalation directly.
		PlayerStats playerStats = Object.FindAnyObjectByType<PlayerStats>();
		if (playerStats != null && playerStats.CurrentHeat < playerStats.MaxHeat)
		{
			playerStats.SetHeat(playerStats.MaxHeat);
		}
		if (_overflowForcedAt <= 0f)
		{
			_overflowForcedAt = Time.time;
			return;
		}
		if (Time.time - _overflowForcedAt < 2f)
		{
			return;
		}
		// Max heat did not escalate it (enableTrioGrabOverflow off on the prefab, or the
		// grab was refused). Escalate directly, and if even that fails, end the grapple so
		// CompleteSceneEntrySurvival can present the game over rather than leaving the
		// player pinned forever.
		Plugin.DBG("GRAPPLE-DEATH", "trio overflow did not fire on max heat - forcing");
		Traverse.Create((object)grappleScreenobject).Method("TriggerTrioGrabOverflow", System.Array.Empty<object>()).GetValue();
		GrabScreen afterOverflow = GrabScreen.Instance;
		if (afterOverflow == null || !afterOverflow.IsGrabbed)
		{
			Plugin.DBG("GRAPPLE-DEATH", "forced overflow failed - ending grapple for the game over");
			grappleScreenobject.ForceEndGrapple();
			Reset("overflow unavailable");
		}
		else
		{
			_overflowForcedAt = Time.time;
		}
	}

	private static int MaxGrapplers(GrappleScreenobject grapple)
	{
		Traverse traverse = Traverse.Create((object)grapple);
		if (traverse.Field("maxGrappleCount").FieldExists())
		{
			int maxGrapple = traverse.Field("maxGrappleCount").GetValue<int>();
			if (maxGrapple > 0)
			{
				return maxGrapple;
			}
		}
		return FallbackMaxGrapplers;
	}

	// The spawner path runs while the player is flagged dead, which leaves
	// EnemyReactivationHelper.ReactivateComponent holding the AI disabled and can leave a
	// fresh grappler without health. StartGrapple rejects an enemy at 0 HP and refuses outright
	// when CanBeGrabbed is false, so make sure of both before it is offered.
	internal static void PrepareSpawnedGrappler(GameObject grappler)
	{
		if (!_active || grappler == null)
		{
			return;
		}
		EnsureHealth((MonoBehaviour)(object)grappler.GetComponent<ChargingEnemyAI>());
		EnsureHealth((MonoBehaviour)(object)grappler.GetComponent<EnemyAI>());
		PlayerStats playerStats = Object.FindAnyObjectByType<PlayerStats>();
		if (playerStats != null)
		{
			playerStats.CanBeGrabbed = true;
		}
	}

	private static void EnsureHealth(MonoBehaviour ai)
	{
		if (!(ai == null))
		{
			Traverse traverse = Traverse.Create((object)ai);
			if (traverse.Field("currentHealth").FieldExists() && traverse.Field("currentHealth").GetValue<int>() <= 0)
			{
				int maxHealth = (traverse.Field("maxHealth").FieldExists() ? traverse.Field("maxHealth").GetValue<int>() : 0);
				traverse.Field("currentHealth").SetValue((object)Mathf.Max(1, maxHealth));
			}
			if (traverse.Field("isDead").FieldExists())
			{
				traverse.Field("isDead").SetValue((object)false);
			}
		}
	}

	private static void ResetShakeProgress(GrappleScreenobject grapple)
	{
		Traverse traverse = Traverse.Create((object)grapple);
		traverse.Field("shakeProgress").SetValue((object)0f);
		traverse.Field("accumulatedMouseX").SetValue((object)0f);
		traverse.Field("lastShakeDirection").SetValue((object)0);
		Image shakeProgressFill = traverse.Field("shakeProgressFill").GetValue<Image>();
		if (shakeProgressFill != null)
		{
			shakeProgressFill.fillAmount = 0f;
		}
	}

	private static void CacheGrappleText(GrappleScreenobject grapple)
	{
		_grappleText = Traverse.Create((object)grapple).Field("grappleText").GetValue<Text>();
	}

	// UpdateGrappleUI rewrites this every time an imp attaches, so it has to be reapplied.
	// Leaving "Shake mouse to escape!" up while the shake is disabled would just read as a
	// broken control.
	private static void ApplyDeathGrappleText()
	{
		if (!(_grappleText == null))
		{
			string configured = (OverkeksMode.UsesRecipe ? OverkeksConfig.CfgGrappleDeathText : Plugin.CfgGrappleDeathText)?.Value ?? "";
			if (configured.Length != 0 && !string.Equals(_grappleText.text, configured))
			{
				_grappleText.text = configured;
			}
		}
	}

	[HarmonyPatch(typeof(GrappleScreenobject), "TrackMouseShake")]
	[HarmonyPrefix]
	public static bool TrackMouseShake_Prefix()
	{
		return !_active;
	}

	[HarmonyPatch(typeof(GrappleScreenobject), "RemoveOneGrappler")]
	[HarmonyPrefix]
	[HarmonyPriority(800)]
	public static bool RemoveOneGrappler_Prefix()
	{
		return !_active;
	}
}
