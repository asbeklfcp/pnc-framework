using System.Collections.Generic;
using PncCustomEnemies.Api;

namespace PncCustomEnemies;

/// <summary>
/// The live package components that claim the Edi channel between scenes.
///
/// This is a registry rather than a scan because PncEdi asks the question on the filler's schedule,
/// several times a second: `FindObjectsByType` over every component in the level to find an
/// interface would be a per-frame cost for an answer that is almost always "no". A component
/// registers when it starts holding the channel and unregisters when it stops, and a destroyed one
/// is dropped on the next read - a Unity object that has been destroyed is "fake null" but still a
/// live reference, so the null test has to be the Unity one (see learnings/unity-runtime.md).
/// </summary>
internal static class PackageSceneOwners
{
	private static readonly List<IPackageEdiChannelOwner> Owners = new List<IPackageEdiChannelOwner>();

	internal static void Register(IPackageEdiChannelOwner owner)
	{
		if (owner != null && !Owners.Contains(owner))
		{
			Owners.Add(owner);
		}
	}

	internal static void Unregister(IPackageEdiChannelOwner owner)
	{
		if (owner != null)
		{
			Owners.Remove(owner);
		}
	}

	internal static bool AnyHoldsEdiChannel()
	{
		for (int i = Owners.Count - 1; i >= 0; i--)
		{
			IPackageEdiChannelOwner owner = Owners[i];
			// `asObject == null` is Unity's overloaded comparison, which is true for a destroyed
			// object; `ReferenceEquals` is the plain one, which separates "destroyed MonoBehaviour"
			// from "not a Unity object at all". Testing `owner == null` alone would keep a
			// destroyed component in the list forever, because the interface reference is still live.
			UnityEngine.Object asObject = owner as UnityEngine.Object;
			bool destroyed = !ReferenceEquals(asObject, null) && asObject == null;
			if (ReferenceEquals(owner, null) || destroyed)
			{
				Owners.RemoveAt(i);
				continue;
			}
			if (owner.HoldsEdiChannel)
			{
				return true;
			}
		}
		return false;
	}
}
