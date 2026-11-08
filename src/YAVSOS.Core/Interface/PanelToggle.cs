using YAVSOS.Core.Messages;

namespace YAVSOS.Core.Interface;

/// <summary>X58 (D148): where the YAVSOS button sits below the chest window: its left end, its middle or its right end.</summary>
public enum ToggleButtonPlace { Left, Middle, Right }

/// <summary>
/// X58 (D141, D148): the "YAVSOS" button below the chest window that shows and hides the panel on every chest. The button
/// is the <c>ShowRuleControls</c> setting: lit while the panel shows, dimmed while it is hidden. Pure; the game adapter
/// draws it.
/// </summary>
public static class PanelToggle
{
    /// <summary>The button's text, the mod's name in every language (D148).</summary>
    public const string Label = "YAVSOS";

    /// <summary>The offsets a preset sets: flush with its end of the window, a small gap below it (UI units).</summary>
    public const float DefaultOffsetX = 0, DefaultOffsetY = 6;

    /// <summary>
    /// The button's top-left corner in the window's coordinates (y up): below <paramref name="window"/>'s bottom edge at
    /// the preset's end, moved right by <paramref name="offsetX"/> and down by <paramref name="offsetY"/>. Each preset is
    /// measured from its own end, so it stays at that end of a wider or narrower chest window (other mods resize it).
    /// </summary>
    public static (float Left, float Top) Place(PanelRect window, float width, ToggleButtonPlace place, float offsetX, float offsetY)
    {
        var left = place switch
        {
            ToggleButtonPlace.Middle => window.Left + (window.Width - width) / 2,
            ToggleButtonPlace.Right => window.Right - width,
            _ => window.Left
        };
        return (left + offsetX, window.Bottom - offsetY);
    }

    /// <summary>The button's tooltip: what a click does now.</summary>
    public static Tooltip Tip(bool panelShown) =>
        Tooltip.Named(Label, Message.Of(panelShown ? "yavsos_toggle_hide_tip" : "yavsos_toggle_show_tip"));
}
