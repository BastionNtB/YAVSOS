using System;
using System.Collections.Generic;
using System.Linq;

namespace YAVSOS.Core.Messages;

/// <summary>How a <see cref="MessageList"/> joins its items.</summary>
public enum ListKind
{
    /// <summary>"Copper, Tin and Iron".</summary>
    And,
    /// <summary>"Copper, Tin or Iron".</summary>
    Or,
    /// <summary>"Copper, Tin, Iron": names side by side, with no conjunction.</summary>
    Comma,
    /// <summary>One item per line; no words, so no keys.</summary>
    Lines,
    /// <summary>Keys pressed together: "Ctrl + Shift + ." (<see cref="Interface.KeyNames"/>).</summary>
    Keys,
    /// <summary>The parts of one line, each a phrase of its own: "Moved 4 stacks (63 items) · 2 stay in place (no room)".</summary>
    Parts
}

/// <summary>
/// P10-04 (inventory case 5): items joined in the player's language instead of with ", " and " or " in code. Each
/// conjunction has four keys, as CLDR list patterns do: <c>_pair</c> for two items, then <c>_start</c>,
/// <c>_middle</c> and <c>_end</c> for three or more, each with <c>{{first}}</c> and <c>{{rest}}</c>. A list may end
/// in "+N more" (<see cref="More"/>). An item is a string (a name already in the player's language), a
/// <see cref="Message"/>, a <see cref="Tinted"/> value or another list (a phrase of alternatives inside a list of
/// phrases, such as "magic or rare, not teleportable"). Immutable.
/// </summary>
public sealed class MessageList : IEquatable<MessageList>
{
    private MessageList(ListKind kind, object[] items, long more) { Kind = kind; this.items = items; More = more; }
    private readonly object[] items;
    public ListKind Kind { get; }
    public IReadOnlyList<object> Items => items;
    /// <summary>Items left out of the list, shown as "+N more" (or a line "… and N more"); 0 for none.</summary>
    public long More { get; }

    public static MessageList Of(ListKind kind, IEnumerable<string> items) => new(kind, Checked(items).Cast<object>().ToArray(), 0);
    public static MessageList Of(ListKind kind, IEnumerable<Message> items) => new(kind, Checked(items).Cast<object>().ToArray(), 0);
    /// <summary>Items of mixed kinds: each a string, a <see cref="Message"/>, a <see cref="Tinted"/> value or a <see cref="MessageList"/>.</summary>
    public static MessageList Of(ListKind kind, IEnumerable<object> items)
    {
        var all = Checked(items).ToArray();
        if (all.Any(x => x is not (string or Message or Tinted or MessageList))) throw new ArgumentException("A list item is a string, a Message, a Tinted value or a list.", nameof(items));
        return new MessageList(kind, all, 0);
    }
    public static MessageList And(params string[] items) => Of(ListKind.And, items);
    public static MessageList Or(params string[] items) => Of(ListKind.Or, items);
    public static MessageList Comma(params string[] items) => Of(ListKind.Comma, items);

    /// <summary>The same list with <paramref name="more"/> items left out, said after it.</summary>
    public MessageList WithMore(long more)
    {
        if (more < 0) throw new ArgumentOutOfRangeException(nameof(more));
        return new MessageList(Kind, items, more);
    }

    private static IEnumerable<T> Checked<T>(IEnumerable<T> items) where T : class
    {
        if (items == null) throw new ArgumentNullException(nameof(items));
        return items.Select(x => x ?? throw new ArgumentException("A list item is null.", nameof(items)));
    }

    /// <summary>The four pattern keys of <paramref name="kind"/>, or null for <see cref="ListKind.Lines"/>.</summary>
    internal static (string Pair, string Start, string Middle, string End)? Patterns(ListKind kind) => kind switch
    {
        ListKind.And => ("yavsos_list_and_pair", "yavsos_list_and_start", "yavsos_list_and_middle", "yavsos_list_and_end"),
        ListKind.Or => ("yavsos_list_or_pair", "yavsos_list_or_start", "yavsos_list_or_middle", "yavsos_list_or_end"),
        ListKind.Comma => ("yavsos_list_comma_pair", "yavsos_list_comma_start", "yavsos_list_comma_middle", "yavsos_list_comma_end"),
        ListKind.Keys => ("yavsos_key_join_pair", "yavsos_key_join_start", "yavsos_key_join_middle", "yavsos_key_join_end"),
        ListKind.Parts => ("yavsos_list_parts_pair", "yavsos_list_parts_start", "yavsos_list_parts_middle", "yavsos_list_parts_end"),
        _ => null
    };

    /// <summary>The plural key of the "+N more" tail: inline, or a line of its own for <see cref="ListKind.Lines"/>.</summary>
    internal static string MoreKey(ListKind kind) => kind == ListKind.Lines ? "yavsos_list_lines_more" : "yavsos_list_more";

    public bool Equals(MessageList? other) =>
        other is not null && other.Kind == Kind && other.More == More && other.items.SequenceEqual(items);
    public override bool Equals(object? obj) => obj is MessageList other && Equals(other);
    public override int GetHashCode() => (int)Kind ^ items.Length ^ More.GetHashCode();
    public override string ToString() => Kind + "[" + string.Join("; ", items) + (More == 0 ? "" : "; +" + More) + "]";
}
