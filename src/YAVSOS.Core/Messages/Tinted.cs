using System;

namespace YAVSOS.Core.Messages;

/// <summary>
/// The color role of a value in a message. Core names the role; the game side picks the color and writes the markup
/// (inventory case 7), so translated text never holds <c>&lt;color&gt;</c> tags.
/// </summary>
public enum Tone
{
    /// <summary>The chest label's gold (#E8B04A): the label itself, and the item tooltip's category line.</summary>
    Label,
    /// <summary>The where-line's lead-in (#F0A64A), such as "Stash stack [.]:".</summary>
    Lead,
    /// <summary>Quieter context (#A0957F).</summary>
    Faint,
    /// <summary>Something to know about, not a fault (#F0C050), such as AzuAutoStore keeping an item out.</summary>
    Caution,
    /// <summary>A warning the player acts on (the game's orange).</summary>
    Warning,
    /// <summary>A status mark's "working" word (#7FD47F).</summary>
    Working,
    /// <summary>A status mark's "broken" word (#EF6B5B).</summary>
    Broken
}

/// <summary>A message value drawn in a <see cref="Tone"/>'s color: a string or a <see cref="Message"/>. Immutable.</summary>
public sealed class Tinted : IEquatable<Tinted>
{
    internal Tinted(object value, Tone tone) { Value = value; Tone = tone; }
    /// <summary>For a list item; in a message use <see cref="Message.With(string, string, Tone)"/>.</summary>
    public static Tinted Of(string value, Tone tone) => new(value ?? "", tone);
    public static Tinted Of(Message value, Tone tone) => new(value ?? throw new ArgumentNullException(nameof(value)), tone);
    /// <summary>A <see cref="string"/> or a <see cref="Message"/>.</summary>
    public object Value { get; }
    public Tone Tone { get; }
    public bool Equals(Tinted? other) => other is not null && other.Tone == Tone && Equals(other.Value, Value);
    public override bool Equals(object? obj) => obj is Tinted other && Equals(other);
    public override int GetHashCode() => Value.GetHashCode() ^ (int)Tone;
    public override string ToString() => Tone + ":" + Value;
}
