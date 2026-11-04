using System;

namespace YAVSOS.Core.Messages;

/// <summary>
/// P10-04 (inventory case 7): a tooltip's heading and body as separate parts, instead of one string split at its first
/// newline. The heading is a <see cref="Message"/> or a name shown as it is (an item, category or chest name already in
/// the player's language); the body is a <see cref="Message"/> or a <see cref="MessageList"/>; either part may be
/// missing. The game side formats both when the tooltip opens, so it follows a language change. Immutable.
/// </summary>
public sealed class Tooltip : IEquatable<Tooltip>
{
    private Tooltip(Message? heading, string name, Message? body, MessageList? list)
    { Heading = heading; Name = name; Body = body; List = list; }

    /// <summary>The heading as a message; null when <see cref="Name"/> is the heading or there is none.</summary>
    public Message? Heading { get; }
    /// <summary>A heading shown as it is; "" when <see cref="Heading"/> is set or there is no heading.</summary>
    public string Name { get; }
    /// <summary>The body as a message; null when <see cref="List"/> is the body or there is none.</summary>
    public Message? Body { get; }
    /// <summary>A body that is only a list (one name per line, say); null otherwise.</summary>
    public MessageList? List { get; }

    /// <summary>A heading message and a body.</summary>
    public static Tooltip Of(Message heading, Message? body = null) => new(heading ?? throw new ArgumentNullException(nameof(heading)), "", body, null);
    /// <summary>A heading message over a list.</summary>
    public static Tooltip Of(Message heading, MessageList list) =>
        new(heading ?? throw new ArgumentNullException(nameof(heading)), "", null, list ?? throw new ArgumentNullException(nameof(list)));
    /// <summary>A name as the heading ("Copper", "Metals") and a body.</summary>
    public static Tooltip Named(string name, Message? body = null) => new(null, name ?? "", body, null);
    /// <summary>A body without a heading, such as a message spot's whole text.</summary>
    public static Tooltip Text(Message body) => new(null, "", body ?? throw new ArgumentNullException(nameof(body)), null);

    /// <summary>
    /// The heading and body as text, with the same arguments as <see cref="MessageFormat.Format(Message, Func{string, string?}, Func{long, string}?, Func{Tone, string, string}?)"/>
    /// (the game side's <c>Texts.Get(Tooltip)</c> passes its lookup and markup).
    /// </summary>
    public (string Topic, string Text) Format(Func<string, string?> lookup, Func<long, string>? plural = null, Func<Tone, string, string>? tint = null)
    {
        if (lookup == null) throw new ArgumentNullException(nameof(lookup));
        var topic = Heading != null ? MessageFormat.Format(Heading, lookup, plural, tint) : Name;
        var text = Body != null ? MessageFormat.Format(Body, lookup, plural, tint) : List != null ? MessageFormat.Format(List, lookup, plural, tint) : "";
        return (topic, text);
    }

    public bool Equals(Tooltip? other) =>
        other is not null && Equals(other.Heading, Heading) && other.Name == Name && Equals(other.Body, Body) && Equals(other.List, List);
    public override bool Equals(object? obj) => obj is Tooltip other && Equals(other);
    public override int GetHashCode() => (Heading?.GetHashCode() ?? 0) ^ Name.GetHashCode() ^ (Body?.GetHashCode() ?? 0) ^ (List?.GetHashCode() ?? 0);
    public override string ToString() => (Heading?.ToString() ?? Name) + " | " + (Body?.ToString() ?? List?.ToString() ?? "");
}
