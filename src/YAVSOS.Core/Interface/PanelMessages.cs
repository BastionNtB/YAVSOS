using System;

namespace YAVSOS.Core.Interface;

/// <summary>
/// D101: where a message shows, next to the control that was clicked: the line under the tab body (edits), beside
/// Clear rule, in the "This chest" row (priority), or on the line under the row of the clean-up opt-out and Clean up nearby.
/// </summary>
public enum MessageSpot { Tab, Clear, Chest, Cleanup }

/// <summary>A refusal (amber: nothing was done) or a summary (cleanup found nothing to move, so no result panel follows).</summary>
public enum MessageKind { Refusal, Summary }

/// <summary>
/// D101: the rule panel's one message. Successes say nothing (the panel already shows the change); a refusal or summary
/// stays at its spot until the player's next action, so it never fades out under the pointer. Pure and Unity-free.
/// </summary>
public sealed class PanelMessages
{
    public MessageSpot Spot { get; private set; }
    public MessageKind Kind { get; private set; }
    public string Text { get; private set; } = "";
    /// <summary>Bumped on every change, so the panel redraws the spots only then.</summary>
    public int Revision { get; private set; }

    /// <summary>The message at <paramref name="spot"/>, or "".</summary>
    public string At(MessageSpot spot) => Text.Length != 0 && Spot == spot ? Text : "";

    public void Say(MessageSpot spot, string? text, MessageKind kind = MessageKind.Refusal)
    {
        text ??= "";
        if (text == Text && spot == Spot && kind == Kind) return;
        Spot = spot; Kind = kind; Text = text; Revision++;
    }

    /// <summary>A click on any control: what was said before it goes.</summary>
    public void Action()
    {
        if (Text.Length == 0) return;
        Text = ""; Revision++;
    }
}
