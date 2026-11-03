using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PncEdi;

// The clinging family's grab screens are silent, and it is vanilla's own asset gap rather than
// anything the mod does.
//
// `GrabScreen` plays grab audio from one `AudioSource` on its own GameObject, through
// `CheckAnimationStateSound`, which walks the enemy's `grabScreenAnimations` - a
// `GrabAnimationData[]` of `animationName` -> clip - and starts the clip whose name matches the
// state the grab-screen animator just entered. When that array is empty it falls back to
// `PlayGrabAudio`, which uses the GrabScreen's own `grabStartSound` / `grabLoopSound`.
//
// Read out of the shipped assets, both halves are empty for exactly two families:
//
//   * `grabScreenAnimations` is populated on the zombie, nun, Blinded Beast, serpent and both
//     mimics, and **empty on GoonShroom_Enemy and all five Imp_Enemy variants**;
//   * `grabStartSound`, `grabLoopSound` and `grabEndSound` are **null on all three** GrabScreen
//     instances in the game (level1, level2, level3), so the fallback plays nothing either.
//
// Vanilla's own tooltip on the field says what it was for - "Used when a trio-grab overflow
// escalates from GrappleScreen to GrabScreen" - which is precisely the goonshroom pile the
// 2026-08-25 run went silent on. The clips exist and are simply unreferenced: `Goonshroom
// gangbang sex`, `Goonshroom Gangbang cum`, `Imp Gangbang grab sound`, `Imp Gangbang grab CUM
// sound`. The goonshroom is silent through its cling too - its `grappleLoopOne/Two/Three` are
// null where the imp's carry `imp grab 1/2/3`.
//
// So this fills the array by name when the enemy hands back an empty one, and never touches a
// family that authored its own. It is the same shape as GrabScreenHeatParam: deliver the message
// the game is already trying to send, do not reimplement the mechanic.
//
// FINDING THE CLIP IS THE PART THAT CAN FAIL. `Resources.FindObjectsOfTypeAll` only sees assets
// that are *loaded*, which is what cost §110 a session on the Blinded Beast's EnemyData, and
// nothing in the build references these clips - so whether they are resident is a question only a
// run can answer. Two sources are tried, in order, and the log says which one paid:
//
//   1. any clip the grappling enemy already holds a reference to - its own
//      `grappleLoopOne/Two/Three` - which are live objects and therefore always resident, and
//   2. the loaded-asset sweep by name.
//
// **With the shipped defaults the first one never fires, and that is worth being clear about
// rather than hoping.** The imp's grapple loops are `imp grab 1/2/3` and the config asks for
// `Imp Gangbang grab sound`; the goonshroom's are null outright. So both families rest entirely
// on the sweep, and there is no control case among them - either the sweep resolves unreferenced
// clips in this build or it resolves none of them. The harvest stays because it costs nothing and
// makes the fallback available to anyone who points the config at a clip the enemy does hold.
//
// A name that resolves to neither is logged once per grab and left silent, which is exactly what
// vanilla does today.
[HarmonyPatch]
internal static class GrabScreenAudioFill
{
	// Resolved clips by config name. A miss is not cached: an asset can load later in a session.
	private static readonly Dictionary<string, AudioClip> ClipByName = new Dictionary<string, AudioClip>(StringComparer.OrdinalIgnoreCase);

	// One log line per state name that could not be resolved, rather than one per grab.
	private static readonly HashSet<string> ReportedMisses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	internal static void ClearCache()
	{
		ClipByName.Clear();
		ReportedMisses.Clear();
		_lastAudibleClip = null;
	}

	// Remember every clip the enemy already holds a reference to. Cheap, and it is what makes the
	// imps resolve without a sweep.
	private static void HarvestFromEnemy(GameObject enemy)
	{
		if (enemy == null)
		{
			return;
		}
		ChargingEnemyAI charging = enemy.GetComponent<ChargingEnemyAI>();
		if (charging == null)
		{
			return;
		}
		Remember(charging.grappleLoopOne);
		Remember(charging.grappleLoopTwo);
		Remember(charging.grappleLoopThree);
	}

	private static void Remember(AudioClip clip)
	{
		if (clip != null && !string.IsNullOrEmpty(clip.name))
		{
			ClipByName[clip.name] = clip;
		}
	}

	private static AudioClip Resolve(string clipName)
	{
		if (string.IsNullOrEmpty(clipName))
		{
			return null;
		}
		if (ClipByName.TryGetValue(clipName, out var cached) && cached != null)
		{
			return cached;
		}
		try
		{
			AudioClip[] loaded = Resources.FindObjectsOfTypeAll<AudioClip>();
			for (int i = 0; i < loaded.Length; i++)
			{
				if (loaded[i] != null && string.Equals(loaded[i].name, clipName, StringComparison.OrdinalIgnoreCase))
				{
					ClipByName[clipName] = loaded[i];
					return loaded[i];
				}
			}
		}
		catch (Exception ex)
		{
			Plugin.DBG("GRAB-AUDIO", "clip sweep failed: " + ex.Message);
		}
		return null;
	}

	// Whether the clip the fill supplied is *audible* is a separate question from whether the fill
	// landed, and the 2026-08-25 run could not answer it: `[GRAB-AUDIO] filled 4` says the array
	// was written, and `DIAG-AUDIO` lists nothing playing - but that sweep only reports sources
	// within 15 m of the player, and `GrabScreen`'s AudioSource sits on the GrabScreen object,
	// wherever the scene put it. So its absence there is not evidence of silence.
	//
	// This says what the source is actually doing, once per clip change rather than per frame:
	// the clip vanilla selected, whether it is playing, and the four settings that can make a
	// playing source inaudible. `CheckAnimationStateSound` runs every frame of a grab and starts
	// a clip only when the animator changes state, which is exactly the moment worth a line.
	private static string _lastAudibleClip;

	[HarmonyPatch(typeof(GrabScreen), "CheckAnimationStateSound")]
	[HarmonyPostfix]
	public static void CheckAnimationStateSound_Postfix(GrabScreen __instance)
	{
		try
		{
			AudioSource source = Traverse.Create((object)__instance).Field("audioSource").GetValue<AudioSource>();
			if (source == null)
			{
				if (_lastAudibleClip != "<no source>")
				{
					_lastAudibleClip = "<no source>";
					Plugin.DBG("GRAB-AUDIO", "the grab screen has no AudioSource at all");
				}
				return;
			}
			string clipName = ((source.clip != null) ? source.clip.name : "<null>");
			if (_lastAudibleClip == clipName)
			{
				return;
			}
			_lastAudibleClip = clipName;
			Plugin.DBG("GRAB-AUDIO", "the grab screen source is on '" + source.gameObject.name
				+ "' clip='" + clipName + "' playing=" + source.isPlaying
				+ " volume=" + source.volume.ToString("0.00") + " mute=" + source.mute
				+ " spatialBlend=" + source.spatialBlend.ToString("0.00")
				+ " maxDistance=" + source.maxDistance.ToString("0")
				+ " mixer=" + ((source.outputAudioMixerGroup != null) ? source.outputAudioMixerGroup.name : "<none>")
				+ " listener=" + AudioListener.volume.ToString("0.00"));
		}
		catch (Exception ex)
		{
			Plugin.DBG("GRAB-AUDIO", "could not read the grab screen source: " + ex.Message);
		}
	}

	// `currentGrabAnimations` is private and is what CheckAnimationStateSound reads, so the fill
	// goes there rather than onto the enemy component - writing the enemy's own serialized array
	// would persist for the rest of the session on a prefab instance the game reuses.
	[HarmonyPatch(typeof(GrabScreen), "ResolveGrabAnimations")]
	[HarmonyPostfix]
	public static void ResolveGrabAnimations_Postfix(GrabScreen __instance, GameObject enemy)
	{
		if (__instance == null || !Plugin.GameplayTweaksEnabled)
		{
			return;
		}
		string map = (OverkeksMode.UsesRecipe ? OverkeksConfig.CfgGrabScreenAudioFill : Plugin.CfgGrabScreenAudioFill).Value;
		if (string.IsNullOrEmpty(map))
		{
			return;
		}
		// A package's capture is silent here for a reason - it plays its own captureSound off its
		// own AudioSource - so an empty array is not the vanilla asset gap this fill exists for.
		if (CustomEnemyBridge.OwnsGrabScene(enemy))
		{
			return;
		}
		try
		{
			Traverse field = Traverse.Create((object)__instance).Field("currentGrabAnimations");
			GrabAnimationData[] existing = field.GetValue<GrabAnimationData[]>();
			if (existing != null && existing.Length != 0)
			{
				return;
			}
			HarvestFromEnemy(enemy);
			List<GrabAnimationData> filled = new List<GrabAnimationData>();
			List<string> missing = new List<string>();
			foreach (KeyValuePair<string, string> pair in ConfigMap.Pairs(map))
			{
				AudioClip clip = Resolve(pair.Value);
				if (clip == null)
				{
					if (ReportedMisses.Add(pair.Key))
					{
						missing.Add(pair.Key + " wanted '" + pair.Value + "'");
					}
					continue;
				}
				filled.Add(new GrabAnimationData
				{
					animationName = pair.Key,
					sound = clip
				});
			}
			if (missing.Count != 0)
			{
				Plugin.DBG("GRAB-AUDIO", "no loaded clip for " + string.Join(", ", missing.ToArray())
					+ " - that state stays as silent as vanilla leaves it");
			}
			if (filled.Count == 0)
			{
				return;
			}
			field.SetValue(filled.ToArray());
			string[] names = new string[filled.Count];
			for (int i = 0; i < filled.Count; i++)
			{
				names[i] = filled[i].animationName + "->" + filled[i].sound.name;
			}
			Plugin.DBG("GRAB-AUDIO", "'" + NameRemap.StripCloneSuffix(enemy != null ? enemy.name : "?")
				+ "' authored no grab-screen sounds - filled " + filled.Count + ": " + string.Join(", ", names));
		}
		catch (Exception ex)
		{
			Plugin.DBG("GRAB-AUDIO", "fill failed: " + ex.Message);
		}
	}
}
