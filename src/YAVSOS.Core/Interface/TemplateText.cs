using System;
using System.Collections.Generic;
using System.Linq;
using YAVSOS.Core.Messages;
using YAVSOS.Core.Templates;

namespace YAVSOS.Core.Interface;

/// <summary>
/// X23 (D157): what the Templates tab says about a template: a row's one-line summary and the details area's lines,
/// with its missing parts (<see cref="TemplateMissing"/>), and the messages for each outcome. Names come in the
/// player's language from the game side. Pure and Unity-free.
/// </summary>
public static class TemplateText
{
    /// <summary>Names a details line lists before "+N more".</summary>
    public const int ShownNames = 12;

    /// <summary>
    /// A row's second line: "Ores, Metals · 1 Always · priority 3 (Urgent) · by Bastion". The row shows "1 part missing
    /// here" (<see cref="Missing"/>) in amber before it.
    /// </summary>
    public static MessageList Summary(RuleTemplate template, TemplateListKind kind, Func<string, string> categoryName)
    {
        if (template == null) throw new ArgumentNullException(nameof(template));
        var parts = new List<object>();
        if (template.Categories.Count != 0) parts.Add(Names(template.Categories.Select(categoryName), 3));
        if (template.Includes.Count != 0) parts.Add(Message.Of("yavsos_template_summary_always").Count(template.Includes.Count));
        if (template.Excludes.Count != 0) parts.Add(Message.Of("yavsos_template_summary_never").Count(template.Excludes.Count));
        if (template.Filters.Count != 0) parts.Add(Message.Of("yavsos_template_summary_filters").Count(template.Filters.Count));
        if (ChestPriority.Phrase(template.Priority) is { } priority) parts.Add(priority);
        if (kind == TemplateListKind.Server && template.By.Length != 0) parts.Add(Message.Of("yavsos_template_summary_by").With("name", template.By));
        return MessageList.Of(ListKind.Parts, parts);
    }

    /// <summary>"1 part missing here".</summary>
    public static Message Missing(int count) => Message.Of("yavsos_template_missing").Count(count);

    /// <summary>
    /// The details area's lines: categories, Always, Never, filters, priority and who shared it. What is missing here is
    /// its own amber line under them (<see cref="MissingLine"/>); the row's tooltip has both (<see cref="Tip"/>).
    /// </summary>
    /// <param name="filters">The filters in a few words (<see cref="RuleText.Short"/>), or null without filters.</param>
    public static MessageList Details(RuleTemplate template, TemplateListKind kind, Func<string, string> categoryName,
        Func<string, string> itemName, MessageList? filters)
    {
        if (template == null) throw new ArgumentNullException(nameof(template));
        var lines = new List<object>();
        if (template.Categories.Count != 0)
            lines.Add(Message.Of("yavsos_template_details_categories").With("names", Names(template.Categories.Select(categoryName), ShownNames)));
        if (template.Includes.Count != 0)
            lines.Add(Message.Of("yavsos_template_details_always").With("names", Names(template.Includes.Select(itemName), ShownNames)));
        if (template.Excludes.Count != 0)
            lines.Add(Message.Of("yavsos_template_details_never").With("names", Names(template.Excludes.Select(itemName), ShownNames)));
        if (filters != null) lines.Add(Message.Of("yavsos_template_details_filters").With("filters", filters));
        var level = ChestPriority.Level(template.Priority);
        lines.Add(Message.Of("yavsos_template_details_priority").With("level", level).With("name", ChestPriority.Name(level)));
        if (kind == TemplateListKind.Server && template.By.Length != 0) lines.Add(Message.Of("yavsos_template_details_by").With("name", template.By));
        return MessageList.Of(ListKind.Lines, lines);
    }

    /// <summary>"Missing here: magic.epicloot.dust. Apply uses the rest; …", by ID (what doesn't exist here has no name here); null when nothing is missing.</summary>
    /// <param name="escape">Escapes an ID for the text it goes into (rich text on the game side).</param>
    public static Message? MissingLine(TemplateMissing missing, Func<string, string>? escape = null)
    {
        if (missing == null) throw new ArgumentNullException(nameof(missing));
        if (missing.Count == 0) return null;
        var ids = missing.Categories.Concat(missing.Items).Concat(missing.Filters.Select(x => x.Value == null ? x.Dimension : x.Dimension + ": " + x.Value));
        return Message.Of("yavsos_template_details_missing").With("parts", Names(ids.Select(escape ?? (x => x)), ShownNames)).Count(missing.Count);
    }

    /// <summary>A row's tooltip: the template's name, its details and, in amber, what is missing here.</summary>
    public static Tooltip Tip(string name, MessageList details, Message? missing)
    {
        var lines = details.Items.ToList();
        if (missing != null) lines.Add(Tinted.Of(missing, Tone.Warning));
        return Tooltip.Named(name ?? "", MessageList.Of(ListKind.Lines, lines));
    }

    private static MessageList Names(IEnumerable<string> names, int shown)
    {
        var all = names.ToList();
        return MessageList.Of(ListKind.Comma, all.Take(shown)).WithMore(Math.Max(0, all.Count - shown));
    }

    /// <summary>A section heading's count: "3 of 64".</summary>
    public static Message Count(int templates) => Message.Of("yavsos_template_count").With("templates", templates).With("limit", TemplateLimits.PerList);

    /// <summary>A refusal of a change to a personal template, or of Save; null for <see cref="TemplateOutcome.Done"/>.</summary>
    public static Message? PersonalRefusal(TemplateOutcome outcome, string name) => outcome switch
    {
        TemplateOutcome.Done => null,
        TemplateOutcome.NoRule => Message.Of("yavsos_template_no_rule"),
        TemplateOutcome.Full => Message.Of("yavsos_template_full").With("limit", TemplateLimits.PerList),
        TemplateOutcome.TooLarge => Message.Of("yavsos_template_too_large").With("kib", TemplateLimits.TemplateBytes / 1024),
        TemplateOutcome.ListTooLarge => Message.Of("yavsos_template_list_too_large").With("kib", TemplateLimits.PersonalListBytes / 1024),
        TemplateOutcome.NameTaken => Message.Of("yavsos_template_name_taken").With("name", name),
        TemplateOutcome.NameInvalid => Message.Of("yavsos_template_name_invalid").With("limit", TemplateLimits.NameLength),
        TemplateOutcome.Unreadable => Message.Of("yavsos_template_personal_unreadable"),
        TemplateOutcome.Changed => Message.Of("yavsos_template_gone"),
        _ => Message.Of("yavsos_template_personal_failed")
    };

    /// <summary>What the strip says when the server answers a change: a summary when done, otherwise the refusal.</summary>
    public static (Message Text, MessageKind Kind) ServerAnswer(TemplateOperation operation, TemplateOutcome outcome, string name, bool gone) => outcome switch
    {
        TemplateOutcome.Done => (ServerDone(operation).With("name", name), MessageKind.Summary),
        TemplateOutcome.Full => (Message.Of("yavsos_template_server_full").With("limit", TemplateLimits.PerList), MessageKind.Refusal),
        TemplateOutcome.TooLarge => (Message.Of("yavsos_template_too_large").With("kib", TemplateLimits.TemplateBytes / 1024), MessageKind.Refusal),
        TemplateOutcome.ListTooLarge => (Message.Of("yavsos_template_server_list_too_large").With("kib", TemplateLimits.ServerListBytes / 1024), MessageKind.Refusal),
        TemplateOutcome.NameTaken => (Message.Of("yavsos_template_server_name_taken").With("name", name), MessageKind.Refusal),
        TemplateOutcome.NameInvalid => (Message.Of("yavsos_template_name_invalid").With("limit", TemplateLimits.NameLength), MessageKind.Refusal),
        TemplateOutcome.Changed => (gone ? Message.Of("yavsos_template_gone") : Message.Of("yavsos_template_changed").With("name", name), MessageKind.Refusal),
        TemplateOutcome.NotAdmin => (Message.Of("yavsos_template_not_admin"), MessageKind.Refusal),
        TemplateOutcome.Busy => (Message.Of("yavsos_template_busy"), MessageKind.Refusal),
        TemplateOutcome.Unreadable => (Message.Of("yavsos_template_server_file_unreadable"), MessageKind.Refusal),
        TemplateOutcome.NoAnswer => (Message.Of("yavsos_template_no_answer"), MessageKind.Refusal),
        _ => (Message.Of("yavsos_template_unavailable"), MessageKind.Refusal)
    };

    private static Message ServerDone(TemplateOperation operation) => operation switch
    {
        TemplateOperation.Share => Message.Of("yavsos_template_server_shared"),
        TemplateOperation.Update => Message.Of("yavsos_template_server_updated"),
        TemplateOperation.Rename => Message.Of("yavsos_template_server_renamed"),
        _ => Message.Of("yavsos_template_server_deleted")
    };

    /// <summary>The question that takes the buttons' place before a change with no undo (D157).</summary>
    public static Message Confirm(TemplateAsk ask, string name, string? newName, int missing) => ask switch
    {
        TemplateAsk.UpdateServer => Message.Of("yavsos_template_confirm_update").With("name", name),
        TemplateAsk.RenameServer => Message.Of("yavsos_template_confirm_rename").With("name", name).With("new", newName ?? ""),
        TemplateAsk.DeleteServer => Message.Of("yavsos_template_confirm_delete_server").With("name", name),
        TemplateAsk.DeletePersonal => Message.Of("yavsos_template_confirm_delete").With("name", name),
        _ => Message.Of("yavsos_template_confirm_share").Count(missing)
    };
}

/// <summary>What a confirmation asks (D157): anything that changes a server template, deleting a personal one, sharing one with missing parts.</summary>
public enum TemplateAsk { UpdateServer, RenameServer, DeleteServer, DeletePersonal, ShareMissing }
