using System;
using System.Collections.Generic;
using System.Reflection;
using PncCustomEnemies.Api;
using UnityEngine;

namespace PncEdi;

// A clone package only replaces the sprite. Everything else the cloned enemy owns - its meshes,
// particle systems, muzzle flashes, death burst and its whole voice - still ships with the copy,
// so the custom enemy reads as the vanilla one wearing new art. Stripping removes all of it and
// leaves the package's own visual and behaviour as the only things the player can see or hear.
internal sealed class BaseEnemyStripper : MonoBehaviour
{
	private const float GuardInterval = 0.5f;

	private readonly List<Renderer> _renderers = new List<Renderer>();
	private readonly List<Light> _lights = new List<Light>();
	private float _nextGuardAt;

	internal static void Apply(GameObject root, bool strip, string enemyId)
	{
		if (root == null || !strip)
		{
			return;
		}
		int renderers = HideRenderers(root);
		int particles = StopParticles(root);
		int lights = DisableLights(root);
		int sources = SilenceAudioSources(root);
		int fields = ClearAssetFields(root);
		if (root.GetComponent<BaseEnemyStripper>() == null)
		{
			root.AddComponent<BaseEnemyStripper>();
		}
		Plugin.Log?.LogInfo("[CustomEnemies] stripped base enemy from '" + enemyId + "': " + renderers + " renderer(s), " + particles + " particle system(s), " + lights + " light(s), " + sources + " audio source(s), " + fields + " asset reference(s)");
	}

	// The package's own artwork and anything a package's behaviour built - a charm circle, a trap
	// overlay - are children of the enemy, so protection is by ownership: anything under an object
	// the mod or a package owns survives the strip. A package marks its own with
	// `IPackageOwnedVisual`, which is the seam's answer to what used to be a hardcoded test for the
	// one behaviour the framework happened to contain (§165).
	private static bool IsOwnedByMod(Transform transform)
	{
		while (transform != null)
		{
			if (transform.GetComponent<RuntimeSpriteVisual>() != null || transform.GetComponent<IPackageOwnedVisual>() != null)
			{
				return true;
			}
			transform = transform.parent;
		}
		return false;
	}

	private static int HideRenderers(GameObject root)
	{
		int count = 0;
		foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
		{
			if (renderer == null || IsOwnedByMod(renderer.transform) || !renderer.enabled) continue;
			renderer.enabled = false;
			count++;
		}
		return count;
	}

	private static int StopParticles(GameObject root)
	{
		int count = 0;
		foreach (ParticleSystem particles in root.GetComponentsInChildren<ParticleSystem>(true))
		{
			if (particles == null || IsOwnedByMod(particles.transform)) continue;
			ParticleSystem.EmissionModule emission = particles.emission;
			emission.enabled = false;
			ParticleSystem.MainModule main = particles.main;
			main.playOnAwake = false;
			particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
			count++;
		}
		return count;
	}

	private static int DisableLights(GameObject root)
	{
		int count = 0;
		foreach (Light light in root.GetComponentsInChildren<Light>(true))
		{
			if (light == null || IsOwnedByMod(light.transform) || !light.enabled) continue;
			light.enabled = false;
			count++;
		}
		return count;
	}

	private static int SilenceAudioSources(GameObject root)
	{
		int count = 0;
		foreach (AudioSource source in root.GetComponentsInChildren<AudioSource>(true))
		{
			if (source == null || IsOwnedByMod(source.transform)) continue;
			source.Stop();
			source.playOnAwake = false;
			source.clip = null;
			count++;
		}
		return count;
	}

	// Every sound and effect the base AI plays is reached through a field on the AI component, and
	// the game null-checks all of them before use, so clearing the references is enough: no patch
	// per AI class, and a package built on a different base enemy is stripped the same way.
	private static int ClearAssetFields(GameObject root)
	{
		int count = 0;
		foreach (MonoBehaviour behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
		{
			if (behaviour == null || behaviour.GetType().Namespace == typeof(BaseEnemyStripper).Namespace) continue;
			for (Type type = behaviour.GetType(); type != null && type != typeof(MonoBehaviour); type = type.BaseType)
			{
				foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
				{
					if (!ShouldClear(field) || field.GetValue(behaviour) == null) continue;
					field.SetValue(behaviour, null);
					count++;
				}
			}
		}
		return count;
	}

	private static bool ShouldClear(FieldInfo field)
	{
		if (field.FieldType == typeof(AudioClip))
		{
			return true;
		}
		if (field.FieldType != typeof(GameObject))
		{
			return false;
		}
		string name = field.Name;
		return name.IndexOf("effect", StringComparison.OrdinalIgnoreCase) >= 0
			|| name.IndexOf("projectile", StringComparison.OrdinalIgnoreCase) >= 0
			|| name.IndexOf("muzzle", StringComparison.OrdinalIgnoreCase) >= 0;
	}

	// Spawned copies come from a stripped template, but reactivation helpers and the base AI's own
	// Start can switch renderers back on, so the strip is re-asserted on a slow tick.
	private void OnEnable()
	{
		Collect();
		Enforce();
		_nextGuardAt = Time.time + GuardInterval;
	}

	private void Collect()
	{
		_renderers.Clear();
		_lights.Clear();
		foreach (Renderer renderer in GetComponentsInChildren<Renderer>(true))
			if (renderer != null && !IsOwnedByMod(renderer.transform)) _renderers.Add(renderer);
		foreach (Light light in GetComponentsInChildren<Light>(true))
			if (light != null && !IsOwnedByMod(light.transform)) _lights.Add(light);
	}

	private void Update()
	{
		if (Time.time < _nextGuardAt)
		{
			return;
		}
		_nextGuardAt = Time.time + GuardInterval;
		Enforce();
	}

	private void Enforce()
	{
		for (int i = 0; i < _renderers.Count; i++)
			if (_renderers[i] != null && _renderers[i].enabled) _renderers[i].enabled = false;
		for (int i = 0; i < _lights.Count; i++)
			if (_lights[i] != null && _lights[i].enabled) _lights[i].enabled = false;
	}
}
