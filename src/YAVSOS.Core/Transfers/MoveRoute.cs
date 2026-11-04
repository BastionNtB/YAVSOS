namespace YAVSOS.Core.Transfers;

/// <summary>How a stash or cleanup move reaches its chests: written directly, or through MultiUserChest.</summary>
public enum MoveRoute
{
    /// <summary>The local peer owns both chests and writes itself.</summary>
    Direct,
    /// <summary>Local source, destination owned by another player: MultiUserChest adds it there.</summary>
    MucAdd,
    /// <summary>Source owned by another player, local destination: MultiUserChest removes it there.</summary>
    MucRemove
}
