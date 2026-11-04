using YAVSOS.Core.Messages;

namespace YAVSOS.Core.Interface;

/// <summary>What the strip under the panel's header shows (P10-25, D132), most important first in <see cref="PanelStrip.Of"/>.</summary>
public enum StripKind { Refusal, Summary, Banner, Label, NoRule }

/// <summary>The panel's state that puts a banner in the strip: another player is using the chest, or its rule is from another version.</summary>
public enum StripBanner { None, InUse, Unreadable }

/// <summary>
/// One state of the strip: its kind, its text (a message, or the chest label as written), how many lines it is high
/// and its tooltip (the whole text, or the chest label's tooltip). Immutable.
/// </summary>
public sealed class StripLine
{
    public StripKind Kind { get; }
    /// <summary>The message shown; null when the strip shows <see cref="Label"/>.</summary>
    public Message? Text { get; }
    /// <summary>The chest label as written (escaped for rich text by the caller), for <see cref="StripKind.Label"/>.</summary>
    public string? Label { get; }
    /// <summary>2, or 3 for a rule from another version, whose banner needs it and whose panel shows no chips.</summary>
    public int Lines { get; }
    public Tooltip Tip { get; }

    internal StripLine(StripKind kind, Message? text, string? label, int lines, Tooltip tip)
    { Kind = kind; Text = text; Label = label; Lines = lines; Tip = tip; }
}

/// <summary>
/// P10-25 (D132, variant B): one strip under the panel's header for every message. A refusal or summary from the last
/// action comes first, then a banner (another player is using the chest, or a rule from another version), and when idle
/// the chest label, faint, or "No rule". Its height is fixed per panel mode, so a message never moves the controls.
/// Pure and Unity-free.
/// </summary>
public static class PanelStrip
{
    public const int Lines = 2, UnreadableLines = 3;

    /// <param name="label">The chest label, escaped for rich text by the caller; null or "" without a rule.</param>
    /// <param name="folded">The panel is folded (X2): the label's tooltip then says how to see the rule.</param>
    public static StripLine Of(PanelMessages messages, StripBanner banner, Stasher stasher, string? label, bool folded)
    {
        if (messages == null) throw new System.ArgumentNullException(nameof(messages));
        var lines = banner == StripBanner.Unreadable ? UnreadableLines : Lines;
        if (messages.Current is { } said)
            return new StripLine(messages.Kind == MessageKind.Refusal ? StripKind.Refusal : StripKind.Summary, said, null, lines, Tooltip.Text(said));
        if (Banner(banner, stasher) is { } shown) return new StripLine(StripKind.Banner, shown, null, lines, Tooltip.Text(shown));
        return label is { Length: > 0 }
            ? new StripLine(StripKind.Label, null, label, lines, PanelFold.LineTooltip(label, folded))
            : new StripLine(StripKind.NoRule, Message.Of("yavsos_panel_norule"), null, lines, PanelFold.LineTooltip(null, folded));
    }

    /// <summary>The banner's message: one sentence per stasher for a rule from another version (P10-04).</summary>
    public static Message? Banner(StripBanner banner, Stasher stasher) => banner switch
    {
        StripBanner.InUse => Message.Of("yavsos_panel_banner_in_use"),
        StripBanner.Unreadable => Message.Of(stasher == Stasher.AzuAutoStore ? "yavsos_panel_banner_unreadable_azu" : "yavsos_panel_banner_unreadable"),
        _ => null
    };
}
