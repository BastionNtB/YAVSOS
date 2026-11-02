namespace YAVSOS.Core.Interface;

/// <summary>The rule panel's parts, top to bottom, plus the label line only the folded panel shows (X2).</summary>
public enum PanelPart { Header, Banners, Rule, Edit, Filters, Chest, Cleanup, Label }

/// <summary>
/// X2 (D116): the rule panel folds to its header (title and status marks), any banner and one label line. One local
/// setting for every chest, kept across sessions. Folding keeps the header where it is and shortens the panel downward.
/// </summary>
public static class PanelFold
{
    public const string NoRule = "No rule";

    public static bool Shows(PanelPart part, bool folded) => part switch
    {
        PanelPart.Header or PanelPart.Banners => true,
        PanelPart.Label => folded,
        _ => !folded
    };

    /// <summary>
    /// The folded panel's line: the chest's label in the label colour, or a faint "No rule". Nothing for a rule this
    /// version can't read; its banner says so.
    /// </summary>
    public static (string Text, bool IsLabel)? Line(string? label, bool unreadable) =>
        unreadable ? null : label is { Length: > 0 } ? (label, true) : (NoRule, false);

    public static string ButtonTopic(bool folded) => folded ? "Unfold" : "Fold";

    // D101: tooltips explain.
    public static string ButtonTooltip(bool folded) => folded ? "Shows the whole panel again."
        : "Shows only the title, the status marks and the chest's label. Every chest opens like this until you unfold the panel.";

    public const string LineTooltip = "What players see when they point at this chest. Unfold the panel to see or change its rule.";

    /// <summary>
    /// The height to place the panel by. Folded, it is the last unfolded height, so a panel pushed up from the screen's
    /// bottom keeps its header where it was (D101: nothing moves under the pointer); the panel just ends higher.
    /// </summary>
    public static float PlacedHeight(bool folded, float height, float unfoldedHeight) =>
        folded && unfoldedHeight > height ? unfoldedHeight : height;
}

/// <summary>X2 (D116): the chest's hover label (S3) in the tooltip of the Rule heading and the chips.</summary>
public static class ChestLabelPreview
{
    /// <summary>The colour the hover text gives the label.</summary>
    public const string Color = "#E8B04A";
    public const string Topic = "Label";

    /// <param name="label">The label, plain text; null or "" without a rule.</param>
    /// <param name="labelsShown">The player's own ShowChestLabels setting.</param>
    public static string Tooltip(string? label, bool labelsShown) =>
        (label is { Length: > 0 } ? "What players see when they point at this chest:\n<color=" + Color + ">" + label + "</color>"
            : "This chest has no rule, so it shows no label.") +
        (labelsShown ? "" : "\nYour ShowChestLabels setting is off, so you don't see labels on chests.");
}
