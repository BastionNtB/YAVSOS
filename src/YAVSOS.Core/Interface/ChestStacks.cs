using System;
using System.Collections.Generic;
using YAVSOS.Core.Messages;

namespace YAVSOS.Core.Interface;

/// <summary>A chest's position in the world (Unity's axes: Y is up), detached from the game.</summary>
public readonly struct ChestSpot : IEquatable<ChestSpot>
{
    public ChestSpot(float x, float y, float z) { X = x; Y = y; Z = z; }
    public float X { get; }
    public float Y { get; }
    public float Z { get; }
    internal bool Finite => !float.IsNaN(X) && !float.IsInfinity(X) && !float.IsNaN(Y) && !float.IsInfinity(Y) && !float.IsNaN(Z) && !float.IsInfinity(Z);
    public bool Equals(ChestSpot other) => X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z);
    public override bool Equals(object? obj) => obj is ChestSpot other && Equals(other);
    public override int GetHashCode() => X.GetHashCode() ^ (Y.GetHashCode() * 397) ^ (Z.GetHashCode() * 7919);
    public override string ToString() => $"({X:0.00}, {Y:0.00}, {Z:0.00})";
}

/// <summary>
/// A chest's place in its stack: <see cref="Place"/> 1 is the bottom chest, <see cref="Size"/> the chests in the stack
/// (1: it stands alone). <see cref="Stack"/> numbers the stacks of one <see cref="ChestStacks.Group"/> call.
/// </summary>
public readonly struct StackPlace : IEquatable<StackPlace>
{
    public StackPlace(int stack, int place, int size) { Stack = stack; Place = place; Size = size; }
    public int Stack { get; }
    public int Place { get; }
    public int Size { get; }
    public bool Stacked => Size > 1;
    public bool Equals(StackPlace other) => Stack == other.Stack && Place == other.Place && Size == other.Size;
    public override bool Equals(object? obj) => obj is StackPlace other && Equals(other);
    public override int GetHashCode() => Stack ^ (Place << 12) ^ (Size << 22);
    public override string ToString() => $"stack {Stack}: {Place} of {Size}";
}

/// <summary>
/// X65 (D156, #78): finds the chests that stand in one column, so a chest name can say where in its stack a chest is.
/// A column is chained from the bottom up: each chest joins the column whose top chest so far is within
/// <c>tolerance</c> of it horizontally, so a stack whose chests are each placed a few centimeters off the one below
/// stays one stack even when the top chest ends up farther from the bottom one (#120). Neighbours in a wall or back to
/// back are farther apart than any chest is narrow, so they stay apart.
/// A column splits into stacks where the vertical gap between two neighbours is more than <c>maximumGap</c>, such as
/// chests on two floors of a building. Deterministic: the same positions give the same stacks in the same order. Cost:
/// one sort and a grid lookup per chest, O(n log n). Detached; no game types.
/// </summary>
public static class ChestStacks
{
    /// <summary>
    /// Horizontal reach from one chest to the next above it, in meters: well under the narrowest chest's width (about
    /// 0.6 m) and back-to-back spacing (about 0.5 m). #120: a hand-placed black metal stack in the lab stepped 0.27 m.
    /// </summary>
    public const float DefaultTolerance = 0.35f;
    /// <summary>The largest vertical gap, in meters, between the bases of two neighbours in one stack (a chest's height plus a shelf).</summary>
    public const float DefaultGap = 2.5f;

    private sealed class Column
    {
        // The top chest so far: the next chest above is measured from it (#120), and the column is filed under its cell.
        internal float X, Z;
        internal (long, long) Cell;
        internal readonly List<int> Members = new List<int>();
    }

    /// <summary>The place of each chest in <paramref name="chests"/>, by index. A chest with a non-finite position stands alone.</summary>
    public static StackPlace[] Group(IReadOnlyList<ChestSpot> chests, float tolerance = DefaultTolerance, float maximumGap = DefaultGap)
    {
        if (chests == null) throw new ArgumentNullException(nameof(chests));
        if (!(tolerance > 0) || float.IsInfinity(tolerance)) throw new ArgumentOutOfRangeException(nameof(tolerance));
        if (!(maximumGap >= 0) || float.IsInfinity(maximumGap)) throw new ArgumentOutOfRangeException(nameof(maximumGap));
        var count = chests.Count;
        var result = new StackPlace[count];
        var order = new int[count];
        for (var i = 0; i < count; i++) order[i] = i;
        // Bottom up, so each chest finds the column below it; ties by X, Z, then index.
        Array.Sort(order, (a, b) =>
        {
            var p = chests[a]; var q = chests[b];
            var c = p.Y.CompareTo(q.Y); if (c != 0) return c;
            c = p.X.CompareTo(q.X); if (c != 0) return c;
            c = p.Z.CompareTo(q.Z); return c != 0 ? c : a.CompareTo(b);
        });
        var columns = new List<Column>();
        // Columns by the grid cell of their top chest; a cell is as wide as the tolerance, so a match is in the 3 × 3 cells around.
        var cells = new Dictionary<(long, long), List<int>>();
        var reach = (double)tolerance * tolerance;
        foreach (var index in order)
        {
            var spot = chests[index];
            if (!spot.Finite) { var alone = new Column(); alone.Members.Add(index); columns.Add(alone); continue; }
            var cx = Cell(spot.X, tolerance); var cz = Cell(spot.Z, tolerance);
            var best = -1; var bestDistance = double.MaxValue;
            for (var dx = -1L; dx <= 1; dx++)
                for (var dz = -1L; dz <= 1; dz++)
                {
                    if (!cells.TryGetValue((cx + dx, cz + dz), out var near)) continue;
                    foreach (var c in near)
                    {
                        double ox = spot.X - columns[c].X, oz = spot.Z - columns[c].Z;
                        var distance = ox * ox + oz * oz;
                        if (distance <= reach && (distance < bestDistance || distance == bestDistance && c < best)) { best = c; bestDistance = distance; }
                    }
                }
            if (best < 0) { best = columns.Count; columns.Add(new Column()); }
            else cells[columns[best].Cell].Remove(best);
            // The chest is the column's new top: the column moves to its spot and its cell.
            var column = columns[best];
            column.X = spot.X; column.Z = spot.Z; column.Cell = (cx, cz);
            if (!cells.TryGetValue((cx, cz), out var list)) cells[(cx, cz)] = list = new List<int>();
            list.Add(best);
            column.Members.Add(index);
        }
        var stack = 0;
        foreach (var each in columns)
        {
            var members = each.Members;
            var start = 0;
            for (var k = 1; k <= members.Count; k++)
            {
                if (k < members.Count && chests[members[k]].Y - chests[members[k - 1]].Y <= maximumGap) continue;
                var size = k - start;
                for (var m = start; m < k; m++) result[members[m]] = new StackPlace(stack, m - start + 1, size);
                stack++; start = k;
            }
        }
        return result;
    }

    private static long Cell(float value, float size) => (long)Math.Floor(value / size);
}

/// <summary>
/// X65 (D163): how YAVSOS says where a chest is: its horizontal ("flat") distance from the player, so every chest of a
/// stack reads the same, and its place in its stack: "2 of 3 from the bottom", "bottom of a stack of 3", "top of a
/// stack of 3". A chest standing alone has no place to add. Detached.
/// </summary>
public static class ChestPlaceText
{
    /// <summary>The horizontal distance from <paramref name="player"/> to <paramref name="chest"/>, whole meters (half away from zero); 0 when either is not a finite position.</summary>
    public static long Across(ChestSpot player, ChestSpot chest)
    {
        if (!player.Finite || !chest.Finite) return 0;
        double dx = chest.X - player.X, dz = chest.Z - player.Z;
        return (long)Math.Round(Math.Sqrt(dx * dx + dz * dz), MidpointRounding.AwayFromZero);
    }

    /// <summary>"bottom of a stack of 3", "2 of 3 from the bottom", "top of a stack of 3", or null for a chest standing alone.</summary>
    public static Message? Stack(StackPlace place) =>
        !place.Stacked ? null
        : place.Place <= 1 ? Message.Of("yavsos_where_place_stack_bottom").With("size", (long)place.Size)
        : place.Place >= place.Size ? Message.Of("yavsos_where_place_stack_top").With("size", (long)place.Size)
        : Message.Of("yavsos_where_place_stack").With("place", (long)place.Place).With("size", (long)place.Size);

    /// <summary>X66: the screen marker's short text under a lit chest, "2 of 3" (counted from the bottom); null for a chest standing alone.</summary>
    public static Message? Marker(StackPlace place) =>
        !place.Stacked ? null : Message.Of("yavsos_marker_place").With("place", (long)Math.Max(1, Math.Min(place.Place, place.Size))).With("size", (long)place.Size);
}
