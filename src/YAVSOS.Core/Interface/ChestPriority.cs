using System;
using YAVSOS.Core.Messages;

namespace YAVSOS.Core.Interface;

/// <summary>
/// D069: players see a chest's priority as a level from 1 (fills first) to 10 (fills last), each with a name. The
/// stored rule keeps its D061 rank (larger fills first, 0 when never set), mapped as level = 5 − rank, so existing
/// chests read as 5 (Normal) and routing, the stored format and the AzuAutoStore adapter are unchanged. A rank outside
/// that range (set before D069) keeps its order and shows as the nearest level until the player changes it.
/// </summary>
public static class ChestPriority
{
    public const int Highest = 1, Lowest = 10, Default = 5;

    public static int Level(int rank) => rank >= Default - Highest ? Highest : rank <= Default - Lowest ? Lowest : Default - rank;

    public static int Rank(int level) => Default - Math.Max(Highest, Math.Min(Lowest, level));

    /// <summary>The level's name (the guide quotes Critical, Urgent, Normal and Fuhgeddaboudit).</summary>
    public static Message Name(int level) => Message.Of(level switch
    {
        <= 1 => "yavsos_priority_critical", 2 or 3 => "yavsos_priority_urgent", 4 or 5 => "yavsos_priority_normal",
        6 or 7 => "yavsos_priority_moderate", 8 or 9 => "yavsos_priority_minor", _ => "yavsos_priority_fuhgeddaboudit"
    });

    /// <summary>The "This chest" heading (D101): the chosen level's name once the chest has a rule.</summary>
    public static Message Heading(int rank, bool ruled) =>
        ruled ? Message.Of("yavsos_chest_heading_priority").With("priority", Name(Level(rank))) : Message.Of("yavsos_chest_heading");

    /// <summary>One number's tooltip on the 1–10 bar (D101), in place of the panel's old priority sentence and legend.</summary>
    public static Tooltip Tip(int level, int chosen, bool ruled) =>
        Tooltip.Of(Message.Of(ruled && level == chosen ? "yavsos_priority_heading_chosen" : "yavsos_priority_heading").With("level", level).With("name", Name(level)),
            Message.Of(ruled ? "yavsos_priority_tip" : "yavsos_priority_tip_norule"));

    /// <summary>The colour band of a level on the panel's 1–10 bar (D071): one band per name, 0 (Critical) to 5.</summary>
    public static int Band(int level) => level switch { <= 1 => 0, 2 or 3 => 1, 4 or 5 => 2, 6 or 7 => 3, 8 or 9 => 4, _ => 5 };

    /// <summary>"priority 2 (Urgent)", for labels and explanations; null for the default level.</summary>
    public static Message? Phrase(int rank)
    {
        if (rank == 0) return null;
        var level = Level(rank);
        return Message.Of("yavsos_priority_short").With("level", level).With("name", Name(level));
    }

    /// <summary>The rank one level up (step −1) or down (step +1), or null at either end.</summary>
    public static int? Step(int rank, int step)
    {
        var next = Level(rank) + step;
        return next < Highest || next > Lowest ? null : Rank(next);
    }
}
