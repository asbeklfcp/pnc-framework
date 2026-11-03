using System;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;

namespace PncEdi;

[HarmonyPatch]
public static class PathfindingSpamFix
{
	public static MethodBase TargetMethod()
	{
		Type type = AccessTools.TypeByName("Pathfinding.ProceduralGraphMover");
		if (type == null)
		{
			return null;
		}
		MethodInfo methodInfo = AccessTools.Method(type, "UpdateGraph", new Type[1] { typeof(bool) }, (Type[])null) ?? AccessTools.Method(type, "Update", (Type[])null, (Type[])null);
		if (methodInfo == null)
		{
			ManualLogSource log = Plugin.Log;
			if (log == null)
			{
				return methodInfo;
			}
			log.LogWarning((object)"[PathfindingSpamFix] target method not found");
		}
		return methodInfo;
	}

	[HarmonyFinalizer]
	public static Exception Finalizer(Exception __exception)
	{
		return null;
	}
}
