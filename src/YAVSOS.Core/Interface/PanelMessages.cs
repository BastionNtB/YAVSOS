using YAVSOS.Core.Messages;

namespace YAVSOS.Core.Interface;

/// <summary>
/// P10-25 (D132): the control a message came from. Every message shows in the one strip under the panel's header; the
/// control keeps an amber outline until the next action, so the message points back to it. <see cref="Edit"/> is the
/// Edit section's tab body (ticks, Items-grid clicks, filters and the Remove buttons above it).
/// </summary>
public enum PanelControl { Edit, Undo, Paste, Clear, Priority, OptOut, Cleanup, Suggestion }

/// <summary>A refusal (amber: nothing was done) or a summary (cleanup found nothing to move, so no result panel follows).</summary>
public enum MessageKind { Refusal, Summary }

/// <summary>
/// D101, D132: the YAVSOS panel's one message. Successes say nothing (the panel already shows the change); a refusal or
/// summary stays in the strip until the player's next action, so it never fades out under the pointer. It holds the
/// message, not its text (P10-04): the panel formats it when it draws, so it follows a language change. Pure and Unity-free.
/// </summary>
public sealed class PanelMessages
{
    /// <summary>The control the current message came from.</summary>
    public PanelControl Control { get; private set; }
    public MessageKind Kind { get; private set; }
    /// <summary>The current message, or null.</summary>
    public Message? Current { get; private set; }
    /// <summary>Bumped on every change, so the panel redraws the strip only then.</summary>
    public int Revision { get; private set; }

    /// <summary>Whether <paramref name="control"/> was refused and keeps its amber outline (D132); a summary flags nothing.</summary>
    public bool Flagged(PanelControl control) => Current != null && Kind == MessageKind.Refusal && Control == control;

    /// <param name="message">Null says nothing (clears the message).</param>
    public void Say(PanelControl control, Message? message, MessageKind kind = MessageKind.Refusal)
    {
        if (Equals(message, Current) && control == Control && kind == Kind) return;
        Control = control; Kind = kind; Current = message; Revision++;
    }

    /// <summary>A click on any control: what was said before it goes.</summary>
    public void Action()
    {
        if (Current == null) return;
        Current = null; Revision++;
    }
}
