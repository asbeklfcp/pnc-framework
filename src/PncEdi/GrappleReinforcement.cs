using System.Collections;
using HarmonyLib;
using UnityEngine;

namespace PncEdi;

// Calls in more of whatever is already clinging, while a grapple runs.
//
// This was ImpGrappleReinforcement and imps were hardcoded twice over: the session gate
// demanded that every clinging enemy be an imp, and the spawn read Tools/SpawnImpNameHint.
// Game 0.3.1 made the goonshroom a grappler too (§66), so both had to become the *family*
// that is actually clinging - resolved the same way the cling dispatch resolves it, off the
// enemies in GrappleScreenobject.grapplingEnemies.
//
// A family with no plan spawns nothing and says so once. Same rule as GrapplePrefixMap:
// a gap is inert and announces itself, where borrowing another creature's pacing would be
// silent and plausible.
internal static class GrappleReinforcement
{
	// Per-family pacing. Imps are a flat drumbeat (Ramp = 1) that tops out; the goonshroom
	// ramp is what makes it feel like a different creature rather than a reskinned imp.
	private struct Plan
	{
		internal bool Enabled;
		internal float Interval;
		internal float Ramp;
		internal int Ceiling;
		internal string Hint;
	}

	private const int VanillaMaxGrappleCount = 3;
	private static float _nextSpawnTime = -1f;
	private static bool _spawnInProgress;
	private static string _unplannedFamilyLogged;

	internal static void Tick()
	{
		// The death sequence needs three imps to reach vanilla's trio overflow, so it
		// overrides the player's on/off setting - otherwise a death with reinforcement
		// disabled would sit at one or two imps with nothing to escalate it.
		bool deathSequence = GrappleDeathSequence.Active;
		if (!Plugin.GameplayTweaksEnabled)
		{
			ResetTimer();
			return;
		}
		GrappleScreenobject grappleScreenobject = GrappleScreenobject.Instance;
		string family = ResolveFamily(grappleScreenobject);
		if (!TryResolvePlan(family, out var plan))
		{
			ResetTimer();
			return;
		}
		if (!plan.Enabled && !deathSequence)
		{
			ResetTimer();
			return;
		}
		if (!ShouldReinforce(grappleScreenobject, plan))
		{
			ResetTimer();
			return;
		}
		float interval = NextInterval(grappleScreenobject, plan, deathSequence);
		if (_nextSpawnTime < 0f)
		{
			_nextSpawnTime = Time.time + interval;
		}
		else if (!(Time.time < _nextSpawnTime) && !_spawnInProgress && Plugin.Instance != null)
		{
			Plugin.Instance.StartCoroutine(SpawnAndAttachRoutine(grappleScreenobject, plan, family));
		}
	}

	// Null when there is no grapple to read a family off, which every caller treats as
	// "nothing to do" rather than as the imp default.
	private static string ResolveFamily(GrappleScreenobject grapple)
	{
		if (grapple == null || !grapple.IsGrappling)
		{
			return null;
		}
		// A grapple cannot be mixed: vanilla StartGrapple refuses an enemy whose
		// grappleAnimatorController differs from the one already running, so the first
		// readable clinger names the whole session.
		return GrappleEnemies.ResolveGrappleFamily(grapple);
	}

	private static bool TryResolvePlan(string family, out Plan plan)
	{
		plan = default(Plan);
		if (string.IsNullOrEmpty(family))
		{
			return false;
		}
		if (family == "imp")
		{
			plan = new Plan
			{
				Enabled = (OverkeksMode.UsesRecipe ? OverkeksConfig.CfgImpGrappleReinforcement : Plugin.CfgImpGrappleReinforcement).Value,
				Interval = (OverkeksMode.UsesRecipe ? OverkeksConfig.CfgImpGrappleReinforcementInterval : Plugin.CfgImpGrappleReinforcementInterval).Value,
				Ramp = (OverkeksMode.UsesRecipe ? OverkeksConfig.CfgImpGrappleReinforcementRamp : Plugin.CfgImpGrappleReinforcementRamp).Value,
				Ceiling = (OverkeksMode.UsesRecipe ? OverkeksConfig.CfgImpGrappleReinforcementCeiling : Plugin.CfgImpGrappleReinforcementCeiling).Value,
				Hint = Plugin.CfgSpawnImpNameHint?.Value ?? "imp"
			};
			return true;
		}
		if (family == "goonshroom")
		{
			plan = new Plan
			{
				Enabled = (OverkeksMode.UsesRecipe ? OverkeksConfig.CfgGoonShroomGrappleReinforcement : Plugin.CfgGoonShroomGrappleReinforcement).Value,
				Interval = (OverkeksMode.UsesRecipe ? OverkeksConfig.CfgGoonShroomGrappleReinforcementInterval : Plugin.CfgGoonShroomGrappleReinforcementInterval).Value,
				Ramp = (OverkeksMode.UsesRecipe ? OverkeksConfig.CfgGoonShroomGrappleReinforcementRamp : Plugin.CfgGoonShroomGrappleReinforcementRamp).Value,
				Ceiling = (OverkeksMode.UsesRecipe ? OverkeksConfig.CfgGoonShroomGrappleReinforcementCeiling : Plugin.CfgGoonShroomGrappleReinforcementCeiling).Value,
				Hint = Plugin.CfgSpawnGoonShroomNameHint?.Value ?? "goonshroom"
			};
			return true;
		}
		// An unplanned family is a new grappler that nobody has written pacing for. The death
		// sequence can begin on any family now, so this is also the one way it can stall - and
		// it is the right way: Tick's bail-outs force the overflow and then end the grapple,
		// which still presents the game over.
		if (_unplannedFamilyLogged != family)
		{
			_unplannedFamilyLogged = family;
			Plugin.DBG("GRAPPLE-REINFORCE", "no reinforcement plan for family '" + family + "' - calling nobody in");
		}
		return false;
	}

	// Vanilla's own ceiling wins over ours. maxGrappleCount is serialized per prefab, so it
	// is not a constant we get to assume; StartGrapple returns false at it, and both the
	// grapple UI animator and the cling scenes (prefix + count) stop at three, so spawning
	// past it would burn a prefab for nothing.
	private static int ResolveCeiling(GrappleScreenobject grapple, Plan plan)
	{
		int ceiling = VanillaMaxGrappleCount;
		if (grapple != null)
		{
			Traverse traverse = Traverse.Create((object)grapple).Field("maxGrappleCount");
			if (traverse.FieldExists())
			{
				int declared = traverse.GetValue<int>();
				if (declared > 0)
				{
					ceiling = declared;
				}
			}
		}
		return Mathf.Clamp(Mathf.Min(plan.Ceiling, ceiling), 1, ceiling);
	}

	// Seconds until the next arrival, given how many are already clinging. Ramp compounds
	// per clinger past the first, so a ramp below 1 closes the gap as the pile grows and a
	// ramp of 1 is the flat interval imps have always had.
	private static float NextInterval(GrappleScreenobject grapple, Plan plan, bool deathSequence)
	{
		if (deathSequence)
		{
			return Mathf.Max(0.1f, (OverkeksMode.UsesRecipe ? OverkeksConfig.CfgGrappleDeathImpInterval : Plugin.CfgGrappleDeathImpInterval).Value);
		}
		float interval = Mathf.Max(1f, plan.Interval);
		float ramp = Mathf.Clamp(plan.Ramp, 0.1f, 4f);
		int clinging = ((grapple != null) ? grapple.GrappleCount : 1);
		for (int i = 1; i < clinging; i++)
		{
			interval *= ramp;
		}
		return Mathf.Max(0.5f, interval);
	}

	private static bool ShouldReinforce(GrappleScreenobject grapple, Plan plan)
	{
		if (grapple == null || !grapple.IsGrappling)
		{
			return false;
		}
		int grappleCount = grapple.GrappleCount;
		if (grappleCount < 1 || grappleCount >= ResolveCeiling(grapple, plan))
		{
			return false;
		}
		GrabScreen grabScreen = GrabScreen.Instance;
		if (grabScreen != null && grabScreen.IsGrabbed)
		{
			return false;
		}
		if (GrabEndHelper.IsGrabSessionLive(grabScreen))
		{
			return false;
		}
		return true;
	}

	private static IEnumerator SpawnAndAttachRoutine(GrappleScreenobject grapple, Plan plan, string family)
	{
		_spawnInProgress = true;
		try
		{
			GameObject spawned = DebugEnemySpawn.SpawnEnemyInstance(plan.Hint, family);
			if (spawned == null)
			{
				_nextSpawnTime = Time.time + 1f;
				yield break;
			}
			yield return null;
			if (spawned == null || grapple == null || !ShouldReinforce(grapple, plan))
			{
				if (spawned != null)
				{
					Object.Destroy((Object)(object)spawned);
				}
				_nextSpawnTime = Time.time + 1f;
				yield break;
			}
			DebugEnemySpawn.EnsureSpawnActive(spawned);
			GrappleDeathSequence.PrepareSpawnedGrappler(spawned);
			int countBefore = grapple.GrappleCount;
			grapple.StartGrapple(spawned);
			if (grapple.GrappleCount <= countBefore)
			{
				Object.Destroy((Object)(object)spawned);
				Plugin.DBG("GRAPPLE-REINFORCE", "StartGrapple rejected spawned " + family);
				_nextSpawnTime = Time.time + 1f;
				yield break;
			}
			// Vanilla's PerformGrabAttack deactivates an enemy that charged into a grapple;
			// one spawned straight into it never charged, so it has to be stowed here or it
			// stands around in the level while its overlay clings to the player.
			GrappleEnemies.StowSpawnedGrappler(spawned);
			_nextSpawnTime = Time.time + NextInterval(grapple, plan, GrappleDeathSequence.Active);
			Plugin.DBG("GRAPPLE-REINFORCE", "attached " + NameRemap.StripCloneSuffix(spawned.name) + " (" + countBefore + " -> " + grapple.GrappleCount + ")");
		}
		finally
		{
			_spawnInProgress = false;
		}
	}

	private static void ResetTimer()
	{
		_nextSpawnTime = -1f;
	}
}
