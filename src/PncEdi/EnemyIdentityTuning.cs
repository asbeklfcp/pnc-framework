using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace PncEdi;

/// <summary>
/// Per-enemy combat identity on top of global doom-pace HP/move.
/// Zombie: slow tanky hitter. Nun: fast glass. Gravy/Baphomet: miniboss-tier (Blinded-Beast strength).
/// </summary>
internal static class EnemyIdentityTuning
{
	private struct Profile
	{
		public float Move;

		public float Health;

		public float Damage;

		public Profile(float move, float health, float damage)
		{
			Move = move;
			Health = health;
			Damage = damage;
		}
	}

	private static readonly HashSet<int> AppliedIds = new HashSet<int>();

	private static readonly Dictionary<string, Profile> Defaults = new Dictionary<string, Profile>(StringComparer.OrdinalIgnoreCase)
	{
		["zombie"] = new Profile(0.78f, 1.75f, 1.85f),
		["nun"] = new Profile(1.35f, 0.95f, 1.4f),
		["gargoyle"] = new Profile(1.08f, 1.5f, 1.45f),
		["gooper"] = new Profile(1.12f, 1.4f, 1.4f),
		["plantasha"] = new Profile(1.1f, 1.45f, 1.4f),
		["gravy"] = new Profile(0.7f, 1.9f, 1.45f),
		["baphomet"] = new Profile(0.75f, 3f, 1.4f)
	};

	internal static void Forget(int id)=>AppliedIds.Remove(id);

	internal static void ResetForScene()
	{
		AppliedIds.Clear();
	}

	internal static void Apply(GameObject enemy, string forcedKey = null)
	{
		if (!OverkeksEnemyRuntime.Active || enemy == null)
		{
			return;
		}
		MonoBehaviour ai = EnemyAiTypes.First(enemy);
		if (ai == null)
		{
			return;
		}
		string key = forcedKey;
		if (string.IsNullOrEmpty(key))
		{
			key = ResolveIdentityKey(enemy, ai);
		}
		if (string.IsNullOrEmpty(key) || !TryResolveProfile(key, out Profile profile))
		{
			return;
		}
		int id = ai.GetInstanceID();
		// Never re-apply: forcedKey used to Remove+re-scale and stacked gravy/baphomet HP
		// (Start via marker, then shuffle Convert Apply(forced) → ~2.8² on serpent base).
		if (!AppliedIds.Add(id))
		{
			return;
		}
		if (!Mathf.Approximately(profile.Health, 1f))
		{
			ScaleHealth(ai, profile.Health);
		}
		if (!Mathf.Approximately(profile.Move, 1f))
		{
			ScaleMove(ai, profile.Move);
		}
		if (!Mathf.Approximately(profile.Damage, 1f))
		{
			ScaleDamage(ai, profile.Damage);
		}
		int maxHp = 0;
		Traverse healthT = Traverse.Create((object)ai);
		if (healthT.Field("maxHealth").FieldExists())
		{
			maxHp = healthT.Field("maxHealth").GetValue<int>();
		}
		Plugin.DBG("SPAWN", "identity '" + key + "' on " + NameRemap.StripCloneSuffix(enemy.name)
			+ " move x" + profile.Move.ToString("0.##")
			+ " hp x" + profile.Health.ToString("0.##")
			+ " dmg x" + profile.Damage.ToString("0.##")
			+ " maxHP=" + maxHp);
	}

	internal static string ResolveIdentityKey(GameObject enemy, MonoBehaviour ai)
	{
		if (enemy == null)
		{
			return "";
		}
		if (OverkeksEnemyRuntime.HasMarker(enemy,"PncEdi.BaphometGargoyleMarker"))
		{
			return "baphomet";
		}
		if (OverkeksEnemyRuntime.HasMarker(enemy,"PncEdi.GravySerpentMarker"))
		{
			return "gravy";
		}
		if (NunGrabTuning.IsNunEnemy(enemy))
		{
			return "nun";
		}
		string galleryId = null;
		Traverse t = Traverse.Create((object)ai);
		if (ai != null && t.Field("galleryEnemyID").FieldExists())
		{
			galleryId = t.Field("galleryEnemyID").GetValue<string>();
		}
		return IdentityPolicy.Resolve(NameRemap.ResolveEnemyKey(enemy.name), NameRemap.ResolveEnemyKey(galleryId ?? ""));
	}

	private static bool TryResolveProfile(string key, out Profile profile)
	{
		profile = default;
		Dictionary<string, Profile> map = BuildProfileMap();
		return map.TryGetValue(key, out profile);
	}

	private static Dictionary<string, Profile> BuildProfileMap()
	{
		Dictionary<string, Profile> map = new Dictionary<string, Profile>(Defaults, StringComparer.OrdinalIgnoreCase);
		string raw = OverkeksEnemyRuntime.IdentityProfiles;
		if (string.IsNullOrWhiteSpace(raw))
		{
			return map;
		}
		string[] entries = raw.Split(new char[1] { ';' }, StringSplitOptions.RemoveEmptyEntries);
		for (int i = 0; i < entries.Length; i++)
		{
			string entry = entries[i].Trim();
			int eq = entry.IndexOf('=');
			if (eq <= 0)
			{
				continue;
			}
			string key = entry.Substring(0, eq).Trim();
			if (string.IsNullOrEmpty(key))
			{
				continue;
			}
			Profile profile = map.TryGetValue(key, out Profile existing) ? existing : new Profile(1f, 1f, 1f);
			string[] parts = entry.Substring(eq + 1).Split(new char[1] { ',' }, StringSplitOptions.RemoveEmptyEntries);
			for (int j = 0; j < parts.Length; j++)
			{
				string part = parts[j].Trim();
				int colon = part.IndexOf(':');
				if (colon <= 0)
				{
					continue;
				}
				string field = part.Substring(0, colon).Trim().ToLowerInvariant();
				if (!float.TryParse(part.Substring(colon + 1).Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float value) || value <= 0f)
				{
					continue;
				}
				if (field == "move" || field == "speed")
				{
					profile.Move = value;
				}
				else if (field == "hp" || field == "health")
				{
					profile.Health = value;
				}
				else if (field == "dmg" || field == "damage")
				{
					profile.Damage = value;
				}
			}
			map[key] = profile;
		}
		return map;
	}

	private static void ScaleHealth(MonoBehaviour ai, float mult)
	{
		Traverse t = Traverse.Create((object)ai);
		if (!t.Field("maxHealth").FieldExists())
		{
			return;
		}
		int maxHealth = Mathf.Max(1, Mathf.CeilToInt(t.Field("maxHealth").GetValue<int>() * mult));
		t.Field("maxHealth").SetValue(maxHealth);
		if (t.Field("currentHealth").FieldExists())
		{
			t.Field("currentHealth").SetValue(maxHealth);
		}
		if (t.Field("secondStageMaxHealth").FieldExists())
		{
			int stage2 = t.Field("secondStageMaxHealth").GetValue<int>();
			if (stage2 > 0)
			{
				t.Field("secondStageMaxHealth").SetValue(Mathf.Max(1, Mathf.CeilToInt(stage2 * mult)));
			}
		}
	}

	private static void ScaleMove(MonoBehaviour ai, float mult)
    {
        var t=Traverse.Create(ai);
        if(t.Field("followerEntity").FieldExists()) {
            object follower=t.Field("followerEntity").GetValue();
            if(follower!=null)OverkeksEnemyRuntime.ScaleFollower(follower,mult);
        }
        ScaleFloatField(t,"secondStageMoveSpeed",mult);
        ScaleFloatField(t,"chargeSpeed",mult);
        ScaleFloatField(t,"spinSpeed",mult);
    }

	private static void ScaleDamage(MonoBehaviour ai, float mult)
	{
		Traverse t = Traverse.Create((object)ai);
		ScaleIntField(t, "attackDamage", mult);
		ScaleIntField(t, "projectileDamage", mult);
	}

	private static void ScaleFloatField(Traverse t, string fieldName, float mult)
	{
		if (!t.Field(fieldName).FieldExists())
		{
			return;
		}
		float value = t.Field(fieldName).GetValue<float>();
		if (value > 0f)
		{
			t.Field(fieldName).SetValue(Mathf.Max(0.1f, value * mult));
		}
	}

	private static void ScaleIntField(Traverse t, string fieldName, float mult)
	{
		if (!t.Field(fieldName).FieldExists())
		{
			return;
		}
		int value = t.Field(fieldName).GetValue<int>();
		if (value > 0)
		{
			t.Field(fieldName).SetValue(Mathf.Max(1, Mathf.CeilToInt(value * mult)));
		}
	}
}
