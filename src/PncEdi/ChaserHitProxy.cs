using PixelCrushers.GridController;
using UnityEngine;

namespace PncEdi;

/// <summary>
/// Forwards melee/projectile hits from child colliders that lack IDamageable to the chaser AI.
/// DualWieldingSystem only calls GetComponent on the hit collider (not InParent).
/// </summary>
internal sealed class ChaserHitProxy : MonoBehaviour, IDamageable
{
	internal MonoBehaviour OwnerAi;

	public void TakeDamage(int damage)
	{
		if (OwnerAi == null || damage <= 0)
		{
			return;
		}
		if (OwnerAi is IDamageable damageable)
		{
			damageable.TakeDamage(damage);
		}
	}
}
