using System;
using YAVSOS.Core.Classification;
using YAVSOS.Core.Messages;
using YAVSOS.Core.Transfers;

namespace YAVSOS.Core.Interface;

/// <summary>
/// The chest an answer names (inventory case 3): the open chest, or another one by its name and distance, since many
/// chests share a name. Typed, so the answer never compares English words. Immutable.
/// </summary>
public sealed class ChestName : IEquatable<ChestName>
{
    private ChestName(bool open, string name, long meters) { IsOpen = open; Name = name; Meters = meters; }
    /// <summary>The chest that is open now.</summary>
    public static ChestName Open { get; } = new(true, "", 0);
    /// <summary>Another chest: its name, already in the player's language, and how far away it is, in whole meters.</summary>
    public static ChestName Away(string name, long meters) => new(false, name ?? "", meters);
    public bool IsOpen { get; }
    public string Name { get; }
    public long Meters { get; }
    /// <summary>"this chest", "Reinforced chest 5 m away", or "a chest nearby" for a chest without a name.</summary>
    public Message Said => IsOpen ? Message.Of("yavsos_where_chest_this")
        : string.IsNullOrWhiteSpace(Name) ? Message.Of("yavsos_where_chest_nearby")
        : Message.Of("yavsos_where_chest_away").With("chest", Name).With("meters", Meters);
    public bool Equals(ChestName? other) => other is not null && other.IsOpen == IsOpen && other.Name == Name && other.Meters == Meters;
    public override bool Equals(object? obj) => obj is ChestName other && Equals(other);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Name) ^ Meters.GetHashCode() ^ (IsOpen ? 1 : 0);
    public override string ToString() => IsOpen ? "(open)" : Name + " " + Meters + " m";
}

/// <summary>
/// B2-07: the item tooltip's where-line in its two parts, the lead ("Stash stack [.]:") and the answer, so the game
/// side colors the lead from the parts instead of searching the finished text for ": ". Immutable.
/// </summary>
public sealed class WhereLine : IEquatable<WhereLine>
{
    public WhereLine(Message lead, Message answer)
    {
        Lead = lead ?? throw new ArgumentNullException(nameof(lead)); Answer = answer ?? throw new ArgumentNullException(nameof(answer));
    }
    public Message Lead { get; }
    public Message Answer { get; }

    /// <summary>The whole line, the lead tinted <see cref="Tone.Lead"/>: for tests and the Lab.</summary>
    public Message Whole => Message.Of("yavsos_where_line").With("lead", Lead, Tone.Lead).With("answer", Answer);

    /// <summary>
    /// The line to show: each part formatted with <paramref name="plain"/> and made safe with <paramref name="escape"/>
    /// (names come from configuration and other mods), and the answer cut with "…" so the whole line stays within
    /// <paramref name="maximum"/> characters (case 9: never inside a surrogate pair). The lead stays whole and tinted.
    /// </summary>
    public Message Fit(Func<Message, string> plain, Func<string, string> escape, int maximum = ExplanationView.MaximumText)
    {
        if (plain == null || escape == null) throw new ArgumentNullException(nameof(plain));
        var lead = escape(plain(Lead)); var answer = escape(plain(Answer));
        Message Line(string text) => Message.Of("yavsos_where_line").With("lead", lead, Tone.Lead).With("answer", text);
        var over = plain(Line(answer)).Length - maximum;
        return over <= 0 ? Line(answer) : Line(RuleText.Cut(answer, Math.Max(1, answer.Length - over)));
    }

    public bool Equals(WhereLine? other) => other is not null && other.Lead.Equals(Lead) && other.Answer.Equals(Answer);
    public override bool Equals(object? obj) => obj is WhereLine other && Equals(other);
    public override int GetHashCode() => Lead.GetHashCode() ^ Answer.GetHashCode();
    public override string ToString() => Lead + " " + Answer;
}

/// <summary>
/// S2 (audit D02): what an explanation found, kept typed until it is shown: the answer with its chosen chest, how to
/// name chests, and a note that says why there is no answer or qualifies one. <see cref="Line"/> is the tooltip's line.
/// </summary>
public sealed class ExplanationAnswer
{
    public PlacementExplanation? Explanation { get; }
    public bool Cleanup { get; }
    /// <summary>Why there is no answer, or a qualifier ("This server has turned off stashing"); null when there is nothing to add.</summary>
    public Message? Note { get; }
    private readonly Func<string, ChestName?>? chestName;
    private readonly bool azuStores;
    private readonly MessageList? stackKey;

    public ExplanationAnswer(PlacementExplanation explanation, bool cleanup, Message? note, Func<string, ChestName?>? chestName = null, bool azuStores = false,
        MessageList? stackKey = null)
    {
        Explanation = explanation ?? throw new ArgumentNullException(nameof(explanation));
        Cleanup = cleanup; Note = note; this.chestName = chestName; this.azuStores = azuStores; this.stackKey = stackKey;
    }

    private ExplanationAnswer(Message? note) => Note = note;

    /// <summary>No answer, only <paramref name="note"/> (possibly none).</summary>
    public static ExplanationAnswer Without(Message? note) => new ExplanationAnswer(note);

    /// <summary>The tooltip line (<see cref="ExplanationView.Tooltip"/>), or null when there is no answer.</summary>
    public WhereLine? Line => Explanation == null ? null : ExplanationView.Tooltip(Explanation, Cleanup, chestName, azuStores, stackKey);
}

/// <summary>
/// The answer as one short line for the game's item tooltip (P6-05, simplified in the R4 review, then P6-23): what the
/// stash key or "Clean up nearby" would do with the stack, and why. The competing chests are not shown (maintainer,
/// 2026-10-23: the list was too technical for players). Read only.
/// </summary>
public static class ExplanationView
{
    public const int MaximumText = 160;

    /// <summary>
    /// P6-23 (D071): the answer as a short line for the game's item tooltip, such as "Stash stack: Reinforced chest
    /// 3 m away (counts as Metals)" or "Clean up: stays here". The lead ("Stash stack:", "Clean up:") comes first, then
    /// where, then why in brackets (X1: for a chosen chest, what made it accept the stack).
    /// </summary>
    /// <param name="stackKey">
    /// P8-17 (Q12, revised 2026-10-28): the stack key that applies (Azu's when Azu stashes), as <see cref="KeyNames"/>
    /// names it, since the tooltip is about this one stack; the lead reads "Stash stack [Middle mouse]:"; null or empty
    /// keeps "Stash stack:".
    /// </param>
    public static WhereLine Tooltip(PlacementExplanation explanation, bool cleanup, Func<string, ChestName?>? chestName = null, bool azuStores = false,
        MessageList? stackKey = null)
    {
        if (explanation == null) throw new ArgumentNullException(nameof(explanation));
        var lead = cleanup ? Message.Of("yavsos_where_lead_cleanup")
            : stackKey == null || stackKey.Items.Count == 0 ? Message.Of("yavsos_where_lead_stash")
            : Message.Of("yavsos_where_lead_stash_key").With("key", stackKey);
        if (explanation.Outcome == PlacementOutcome.Goes && explanation.ChosenId != null)
        {
            var chest = chestName?.Invoke(explanation.ChosenId) ?? ChestName.Away("", 0);
            if (cleanup && chest.IsOpen) return new WhereLine(lead, Message.Of("yavsos_where_cleanup_stays"));
            var because = Because(explanation);
            var answer = cleanup
                ? because == null ? Message.Of("yavsos_where_cleanup_moves").With("chest", chest.Said)
                    : Message.Of("yavsos_where_cleanup_moves_because").With("chest", chest.Said).With("because", because)
                : because == null ? chest.Said : Message.Of("yavsos_where_stash_because").With("chest", chest.Said).With("because", because);
            return new WhereLine(lead, answer);
        }
        var reason = explanation.Outcome switch
        {
            PlacementOutcome.NoRoom => Message.Of("yavsos_where_reason_full"),
            PlacementOutcome.NoChest => Message.Of("yavsos_where_reason_no_chest"),
            PlacementOutcome.Blocked when explanation.Blocked != null => RunReport.Reason(explanation.Blocked.Value),
            PlacementOutcome.Kept => Message.Of("yavsos_where_reason_kept"),
            PlacementOutcome.NotReady => Message.Of("yavsos_where_reason_loading"),
            PlacementOutcome.OptedOut => Message.Of("yavsos_where_reason_opted_out").With("option", Message.Of("yavsos_cleanup_optout")),
            PlacementOutcome.Skipped => RunReport.Reason(RunReason.ChestLimit),
            _ => null
        };
        if (!cleanup && azuStores && explanation.Outcome is PlacementOutcome.NoChest or PlacementOutcome.NoRoom or PlacementOutcome.Blocked)
            return new WhereLine(lead, Message.Of("yavsos_where_azu_decides")
                .With("reason", explanation.Outcome == PlacementOutcome.NoChest || reason == null ? Message.Of("yavsos_where_reason_azu_no_rule") : reason));
        if (cleanup)
            return new WhereLine(lead, reason == null ? Message.Of("yavsos_where_cleanup_stays") : Message.Of("yavsos_where_cleanup_stays_reason").With("reason", reason));
        return new WhereLine(lead, reason == null ? Message.Of("yavsos_where_stash_stays") : Message.Of("yavsos_where_stash_stays_reason").With("reason", reason));
    }

    /// <summary>X1: why the chosen chest accepts it, "counts as Metals"; null when there is nothing useful to say.</summary>
    public static Message? Because(PlacementExplanation explanation) => explanation.Basis switch
    {
        PlacementBasis.Category when explanation.CategoryName != null => CategoryNames.With(Message.Of("yavsos_where_because_category"), "category", explanation.CategoryName),
        PlacementBasis.Always => Message.Of("yavsos_where_because_always"),
        PlacementBasis.Holds => Message.Of("yavsos_where_because_holds"),
        PlacementBasis.Filters => Message.Of("yavsos_where_because_filters"),
        _ => null
    };
}
