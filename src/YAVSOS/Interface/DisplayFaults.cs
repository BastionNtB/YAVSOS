using System.Collections.Generic;
using YAVSOS.Core.Interface;

namespace YAVSOS.Interface;

/// <summary>
/// SF4 (D087): the display-only parts that turned themselves off this session, in the order they failed, for the
/// panel's Display mark (<see cref="PanelMarks.Display"/>, which names each part). Each part still logs its own warning
/// with the cause. Unity game thread only.
/// </summary>
internal static class DisplayFaults
{
    internal const DisplayPart Where = DisplayPart.Where, Search = DisplayPart.Search, Categories = DisplayPart.Categories,
        Labels = DisplayPart.Labels, Glow = DisplayPart.Glow, Results = DisplayPart.Results;
    private static readonly List<DisplayPart> off = new();
    internal static IReadOnlyList<DisplayPart> Off => off;
    internal static void Add(DisplayPart part) { if (!off.Contains(part)) off.Add(part); }
}
