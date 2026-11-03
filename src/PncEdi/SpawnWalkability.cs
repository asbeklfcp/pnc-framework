using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace PncEdi;

/// <summary>
/// A* walkability via reflection so we do not pull PackageTools/Drawing into the plugin refs.
/// </summary>
internal static class SpawnWalkability
{
	private const float MaxAstarSnapDistance = 1.75f;

	private static bool _resolved;

	private static PropertyInfo _activeProp;

	private static MethodInfo _getNearest;

	private static MethodInfo _linecast;

	private static object _walkableConstraint;

	private static FieldInfo _nnNodeField;

	private static FieldInfo _nnPositionField;

	private static PropertyInfo _nodeWalkableProp;

	private static void EnsureResolved()
	{
		if (_resolved)
		{
			return;
		}
		_resolved = true;
		Type astarType = AccessTools.TypeByName("Pathfinding.AstarPath");
		if (astarType == null)
		{
			Plugin.DBG("SPAWN", "AstarPath type missing — walkability falls back to physics only");
			return;
		}
		_activeProp = AccessTools.Property(astarType, "active");
		Type nnConstraintType = AccessTools.TypeByName("Pathfinding.NNConstraint")
			?? AccessTools.TypeByName("Pathfinding.NearestNodeConstraint");
		if (nnConstraintType != null)
		{
			FieldInfo walkableField = AccessTools.Field(nnConstraintType, "Walkable");
			PropertyInfo walkableProp = AccessTools.Property(nnConstraintType, "Walkable");
			if (walkableField != null)
			{
				_walkableConstraint = walkableField.GetValue(null);
			}
			else if (walkableProp != null)
			{
				_walkableConstraint = walkableProp.GetValue(null, null);
			}
			else if (nnConstraintType.IsValueType)
			{
				_walkableConstraint = Activator.CreateInstance(nnConstraintType);
				FieldInfo constrainWalkability = AccessTools.Field(nnConstraintType, "constrainWalkability");
				FieldInfo walkable = AccessTools.Field(nnConstraintType, "walkable");
				constrainWalkability?.SetValue(_walkableConstraint, true);
				walkable?.SetValue(_walkableConstraint, true);
			}
		}
		if (_walkableConstraint != null)
		{
			_getNearest = AccessTools.Method(astarType, "GetNearest", new Type[2]
			{
				typeof(Vector3),
				_walkableConstraint.GetType()
			});
		}
		if (_getNearest == null)
		{
			_getNearest = AccessTools.Method(astarType, "GetNearest", new Type[1] { typeof(Vector3) });
		}
		_linecast = AccessTools.Method(astarType, "Linecast", new Type[2]
		{
			typeof(Vector3),
			typeof(Vector3)
		});
		Type nnInfoType = AccessTools.TypeByName("Pathfinding.NNInfo");
		if (nnInfoType != null)
		{
			_nnNodeField = AccessTools.Field(nnInfoType, "node");
			_nnPositionField = AccessTools.Field(nnInfoType, "position");
		}
		Type graphNodeType = AccessTools.TypeByName("Pathfinding.GraphNode");
		if (graphNodeType != null)
		{
			_nodeWalkableProp = AccessTools.Property(graphNodeType, "Walkable");
		}
	}

	private static object ActiveAstar()
	{
		EnsureResolved();
		return _activeProp?.GetValue(null, null);
	}

	internal static bool TrySnapWalkable(Vector3 candidate, float referenceY, float maxFloorYDelta, out Vector3 snapped)
	{
		snapped = candidate;
		object active = ActiveAstar();
		if (active == null || _getNearest == null)
		{
			return true;
		}
		object nnInfo;
		try
		{
			nnInfo = _walkableConstraint != null
				? _getNearest.Invoke(active, new object[2] { candidate, _walkableConstraint })
				: _getNearest.Invoke(active, new object[1] { candidate });
		}
		catch (Exception ex)
		{
			Plugin.DBG("SPAWN", "A* GetNearest failed: " + ex.GetBaseException().Message);
			return true;
		}
		if (nnInfo == null)
		{
			return false;
		}
		object node = _nnNodeField?.GetValue(nnInfo);
		if (node == null)
		{
			return false;
		}
		if (_nodeWalkableProp != null && !(bool)_nodeWalkableProp.GetValue(node, null))
		{
			return false;
		}
		Vector3 graphPos = _nnPositionField != null ? (Vector3)_nnPositionField.GetValue(nnInfo) : candidate;
		Vector3 delta = graphPos - candidate;
		delta.y = 0f;
		if (delta.sqrMagnitude > MaxAstarSnapDistance * MaxAstarSnapDistance)
		{
			return false;
		}
		if (graphPos.y < referenceY - maxFloorYDelta || graphPos.y > referenceY + maxFloorYDelta)
		{
			return false;
		}
		snapped = graphPos;
		return true;
	}

	internal static bool IsPathBlocked(Vector3 from, Vector3 to)
	{
		object active = ActiveAstar();
		if (active == null || _linecast == null)
		{
			return Physics.Linecast(from + Vector3.up, to + Vector3.up, -5, QueryTriggerInteraction.Ignore);
		}
		try
		{
			object result = _linecast.Invoke(active, new object[2] { from, to });
			return result is bool blocked && blocked;
		}
		catch
		{
			return Physics.Linecast(from + Vector3.up, to + Vector3.up, -5, QueryTriggerInteraction.Ignore);
		}
	}
}
