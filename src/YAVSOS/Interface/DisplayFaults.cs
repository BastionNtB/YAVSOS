using System.Collections.Generic;

namespace YAVSOS.Interface;

/// <summary>
/// SF4 (D087): the display-only parts that turned themselves off this session, in the order they failed, for the
/// panel's Display mark (<see cref="YAVSOS.Core.Interface.PanelMarks.Display"/>). Each part still logs its own warning
/// with the cause. Unity game thread only.
/// </summary>
internal static class DisplayFaults
{
    internal const string Where = "\"where it would go\" in item tooltips", Search = "the panel's search", Categories = "categories in item tooltips",
        Labels = "chest hover labels", Results = "the result panel (results still show as a message)";
    private static readonly List<string> off = new();
    internal static IReadOnlyList<string> Off => off;
    internal static void Add(string part) { if (!off.Contains(part)) off.Add(part); }
}
