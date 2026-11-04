using YAVSOS.Core.Messages;

namespace YAVSOS.Core.Interface;

/// <summary>The rule panel's parts, top to bottom; the strip (P10-25, D132) holds the messages, banners and chest label.</summary>
public enum PanelPart { Header, Strip, Rule, Edit, Filters, Chest, Cleanup }

/// <summary>
/// X2 (D116): the rule panel folds to its header (title and status marks) and the strip under it, which then shows the
/// chest label, a banner or a message (D132). One local setting for every chest, kept across sessions. Folding keeps the
/// header where it is and shortens the panel downward.
/// </summary>
public static class PanelFold
{
    public static bool Shows(PanelPart part, bool folded) => part is PanelPart.Header or PanelPart.Strip || !folded;

    /// <summary>The fold button's tooltip (D101: tooltips explain); its heading names what a click does.</summary>
    public static Tooltip ButtonTooltip(bool folded) => folded
        ? Tooltip.Of(Message.Of("yavsos_panel_unfold"), Message.Of("yavsos_panel_unfold_tip"))
        : Tooltip.Of(Message.Of("yavsos_panel_fold"), Message.Of("yavsos_panel_fold_tip"));

    /// <summary>
    /// The strip's tooltip while it shows the chest label (D132): the label (escaped for rich text by the caller) above
    /// what the line is; folded, how to see the rule, unfolded, that messages take the line. Null label: no rule.
    /// </summary>
    public static Tooltip LineTooltip(string? label, bool folded)
    {
        var heading = Message.Of("yavsos_panel_label_heading");
        var what = Message.Of(folded ? "yavsos_panel_label_line_tip" : "yavsos_panel_label_strip_tip");
        return label is { Length: > 0 }
            ? Tooltip.Of(heading, MessageList.Of(ListKind.Lines, new object[] { Tinted.Of(label, Tone.Label), what }))
            : Tooltip.Of(heading, what);
    }

    /// <summary>
    /// The height to place the panel by. Folded, it is the last unfolded height, so a panel pushed up from the screen's
    /// bottom keeps its header where it was (D101: nothing moves under the pointer); the panel just ends higher.
    /// </summary>
    public static float PlacedHeight(bool folded, float height, float unfoldedHeight) =>
        folded && unfoldedHeight > height ? unfoldedHeight : height;
}

/// <summary>X2 (D116): the chest label (S3) in the tooltip of the Rule heading and the chips.</summary>
public static class ChestLabelPreview
{
    /// <summary>The color the hover text gives the label (<see cref="Tone.Label"/> on the game side).</summary>
    public const string Color = "#E8B04A";
    /// <summary>YAVSOS's own setting, by its key name (inventory case 11: setting names aren't translated).</summary>
    public const string ShowChestLabels = "ShowChestLabels";

    /// <param name="label">The label, plain text and escaped for rich text by the caller; null or "" without a rule.</param>
    /// <param name="labelsShown">The player's own ShowChestLabels setting.</param>
    public static Tooltip Of(string? label, bool labelsShown)
    {
        var heading = Message.Of("yavsos_panel_label_heading");
        var what = label is { Length: > 0 }
            ? Message.Of("yavsos_panel_label_tip").With("label", label, Tone.Label)
            : Message.Of("yavsos_panel_label_tip_norule");
        return labelsShown ? Tooltip.Of(heading, what)
            : Tooltip.Of(heading, MessageList.Of(ListKind.Lines, new[] { what, Message.Of("yavsos_panel_label_tip_hidden").With("setting", ShowChestLabels) }));
    }
}
