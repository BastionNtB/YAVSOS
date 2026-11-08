using System;
using System.Collections.Generic;
using UnityEngine;
using YAVSOS.Cleanup;
using YAVSOS.Core.Interface;
using YAVSOS.Integrations;

namespace YAVSOS.Interface;

/// <summary>
/// X65 (D163): each chest's place in its stack, for the names YAVSOS gives chests (where-lines, result places, the
/// search tooltip) and the through-walls marker (X66). One <see cref="ChestStacks.Group"/> call per run, tooltip or
/// marker change, over the scene's containers that stand within <see cref="Margin"/> m (across the ground) of the box
/// around the chests asked about: a column's chests all stand within twice the tolerance of each other, so nothing
/// farther away changes their stacks. Every container counts, with or without a rule, since the player sees them all.
/// A failure gives no places, so names keep today's form. Read only. Unity game thread only.
/// </summary>
internal static class ChestStackPlaces
{
    internal const float Margin = 1f;

    /// <summary>The place in its stack of each live chest of <paramref name="chests"/>.</summary>
    internal static Dictionary<Container, StackPlace> Of(IEnumerable<Container> chests)
    {
        var result = new Dictionary<Container, StackPlace>();
        try
        {
            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
            var wanted = new HashSet<Container>();
            foreach (var chest in chests)
            {
                if (!chest || !wanted.Add(chest)) continue;
                var p = chest.transform.position;
                minX = Math.Min(minX, p.x); maxX = Math.Max(maxX, p.x); minZ = Math.Min(minZ, p.z); maxZ = Math.Max(maxZ, p.z);
            }
            if (wanted.Count == 0) return result;
            minX -= Margin; maxX += Margin; minZ -= Margin; maxZ += Margin;
            var near = new List<Container>(); var spots = new List<ChestSpot>(); var found = 0;
            foreach (var chest in CleanupRunner.Scene())
            {
                if (!chest) continue;
                var p = chest.transform.position;
                if (p.x < minX || p.x > maxX || p.z < minZ || p.z > maxZ) continue;
                near.Add(chest); spots.Add(Spot(p));
                if (wanted.Contains(chest)) found++;
            }
            if (found < wanted.Count) // Normally all listed; a chest the registry missed still gets its own place.
            {
                var seen = new HashSet<Container>(near);
                foreach (var chest in wanted) if (!seen.Contains(chest)) { near.Add(chest); spots.Add(Spot(chest.transform.position)); }
            }
            var places = ChestStacks.Group(spots);
            for (var i = 0; i < near.Count; i++) if (wanted.Contains(near[i])) result[near[i]] = places[i];
        }
        catch (Exception error) when (OptionalFailure.Recoverable(error)) { result.Clear(); } // Names without places.
        return result;
    }

    /// <summary>The flat distance between two points, whole meters (D163: a whole stack reads the same).</summary>
    internal static long Across(Vector3 from, Vector3 to) => ChestPlaceText.Across(Spot(from), Spot(to));

    internal static ChestSpot Spot(Vector3 p) => new ChestSpot(p.x, p.y, p.z);
}
