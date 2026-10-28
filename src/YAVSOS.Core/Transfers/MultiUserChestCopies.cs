using System;
using System.Linq;

namespace YAVSOS.Core.Transfers;

public enum MultiUserChestCopy { None, Standalone, AzuAutoStore, Yavsos }

/// <summary>
/// Which MultiUserChest copy handles other players' chests (D057 O11, D061): standalone MultiUserChest, else
/// AzuAutoStore's bundled copy unless a mod Azu steps aside for is loaded, else YAVSOS's own copy, which steps aside
/// for all of those and for AzuAutoStore itself. Mods that conflict with MultiUserChest (QuickStack, SimpleSort,
/// QuickStore) leave no copy at all.
/// </summary>
public static class MultiUserChestCopies
{
    public const string StandaloneGuid = "com.maxsch.valheim.MultiUserChest";
    public const string AzuGuid = "Azumatt.AzuAutoStore";
    public static readonly string[] Conflicting = { "org.bepinex.plugins.valheim.quick_stack", "aedenthorn.SimpleSort", "aedenthorn.QuickStore" };

    public static MultiUserChestCopy Choose(Func<string, bool> loaded)
    {
        if (loaded == null) throw new ArgumentNullException(nameof(loaded));
        if (loaded(StandaloneGuid)) return MultiUserChestCopy.Standalone;
        if (Conflicting.Any(loaded)) return MultiUserChestCopy.None;
        return loaded(AzuGuid) ? MultiUserChestCopy.AzuAutoStore : MultiUserChestCopy.Yavsos;
    }
}
