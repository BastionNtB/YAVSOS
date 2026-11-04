using System;
using System.Collections.Generic;
using System.Globalization;

namespace YAVSOS.Core.Messages;

/// <summary>
/// X30 step 2 (P10-04): a piece of player text as a message key plus named values, so Core decides what to say and the
/// game side looks up the words in the player's language (<see cref="MessageFormat"/>). A value is a string (an item
/// name already in the player's language, a key name), a whole number, another message, a <see cref="Tinted"/> value
/// (drawn in a color the game side picks) or a <see cref="MessageList"/>. A message with a
/// <see cref="MessageFormat.CountName"/> value picks its plural form from it. Immutable.
/// </summary>
public sealed class Message : IEquatable<Message>
{
    private static readonly KeyValuePair<string, object>[] None = Array.Empty<KeyValuePair<string, object>>();
    private readonly KeyValuePair<string, object>[] args;

    private Message(string key, KeyValuePair<string, object>[] args) { Key = key; this.args = args; }

    /// <summary>The message key, without Jotunn's <c>$</c>, and without a plural suffix.</summary>
    public string Key { get; }
    public IReadOnlyList<KeyValuePair<string, object>> Args => args;

    public static Message Of(string key)
    {
        if (string.IsNullOrEmpty(key)) throw new ArgumentException("A message needs a key.", nameof(key));
        return new Message(key, None);
    }

    public Message With(string name, string value) => Add(name, value ?? "");
    public Message With(string name, long value) => Add(name, value);
    public Message With(string name, Message value) => Add(name, value ?? throw new ArgumentNullException(nameof(value)));
    /// <summary>A name or text drawn in <paramref name="tone"/>'s color (inventory case 7: markup stays out of the text).</summary>
    public Message With(string name, string value, Tone tone) => Add(name, new Tinted(value ?? "", tone));
    /// <summary>A whole message drawn in <paramref name="tone"/>'s color.</summary>
    public Message With(string name, Message value, Tone tone) => Add(name, new Tinted(value ?? throw new ArgumentNullException(nameof(value)), tone));
    /// <summary>Names joined in the player's language: "Copper, Tin or Iron" (inventory case 5).</summary>
    public Message With(string name, MessageList value) => Add(name, value ?? throw new ArgumentNullException(nameof(value)));
    /// <summary>A whole list drawn in <paramref name="tone"/>'s color (the names in a tree row's note).</summary>
    public Message With(string name, MessageList value, Tone tone) => Add(name, new Tinted(value ?? throw new ArgumentNullException(nameof(value)), tone));
    /// <summary>The count that picks the plural form; shown where the text has <c>{{count}}</c>.</summary>
    public Message Count(long value) => Add(MessageFormat.CountName, value);

    public bool TryGet(string name, out object value)
    {
        foreach (var arg in args)
            if (arg.Key == name) { value = arg.Value; return true; }
        value = null!; return false;
    }

    private Message Add(string name, object value)
    {
        if (string.IsNullOrEmpty(name)) throw new ArgumentException("A value needs a name.", nameof(name));
        var copy = new KeyValuePair<string, object>[args.Length + 1];
        var at = 0;
        foreach (var arg in args)
            if (arg.Key != name) copy[at++] = arg;
        copy[at++] = new KeyValuePair<string, object>(name, value);
        if (at != copy.Length) Array.Resize(ref copy, at);
        return new Message(Key, copy);
    }

    public bool Equals(Message? other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other is null || other.Key != Key || other.args.Length != args.Length) return false;
        foreach (var arg in args)
            if (!other.TryGet(arg.Key, out var value) || !Equals(value, arg.Value)) return false;
        return true;
    }

    public override bool Equals(object? obj) => obj is Message other && Equals(other);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Key) ^ args.Length;

    /// <summary>For logs and test output only; players get <see cref="MessageFormat.Format"/>.</summary>
    public override string ToString()
    {
        if (args.Length == 0) return Key;
        var parts = new List<string>(args.Length);
        foreach (var arg in args)
            parts.Add(arg.Key + "=" + (arg.Value is long n ? n.ToString(CultureInfo.InvariantCulture) : arg.Value.ToString()));
        return Key + "(" + string.Join(", ", parts) + ")";
    }
}
