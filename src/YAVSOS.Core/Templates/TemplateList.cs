using System;
using System.Collections.Generic;
using System.Linq;

namespace YAVSOS.Core.Templates;

/// <summary>What a template change came to. Each refusal leaves the list as it was.</summary>
public enum TemplateOutcome
{
    Done,
    /// <summary>The list holds <see cref="TemplateLimits.PerList"/> templates.</summary>
    Full,
    /// <summary>The template is over <see cref="TemplateLimits.TemplateBytes"/>.</summary>
    TooLarge,
    /// <summary>The whole list would be over its size limit.</summary>
    ListTooLarge,
    /// <summary>The list has another template of that name.</summary>
    NameTaken,
    /// <summary>The name is empty, too long or has characters a name can't have.</summary>
    NameInvalid,
    /// <summary>The template changed or went since the sender looked (a server change names the revision it saw).</summary>
    Changed,
    /// <summary>The sender isn't on the server's admin list (D157: only admins change server templates in 1.3).</summary>
    NotAdmin,
    /// <summary>Too many changes from this sender just now, or another change is still waiting.</summary>
    Busy,
    /// <summary>The stored list can't be read by this YAVSOS version (newer or damaged), so it isn't changed (D153).</summary>
    Unreadable,
    /// <summary>The open chest has no rule to save.</summary>
    NoRule,
    /// <summary>The server couldn't save its templates file, or can't check admins in this game version.</summary>
    Unavailable,
    /// <summary>No answer from the server in time; the change may or may not have been made.</summary>
    NoAnswer,
    /// <summary>The request couldn't be read.</summary>
    Malformed
}

/// <summary>A change's result: the outcome, the new list when it was <see cref="TemplateOutcome.Done"/>, and the template it named.</summary>
public sealed class TemplateEdit
{
    public TemplateOutcome Outcome { get; }
    /// <summary>The list after the change; the old list when refused.</summary>
    public TemplateList List { get; }
    /// <summary>The template added or changed (null for a delete or a refusal).</summary>
    public RuleTemplate? Template { get; }
    public bool Done => Outcome == TemplateOutcome.Done;

    internal TemplateEdit(TemplateOutcome outcome, TemplateList list, RuleTemplate? template) { Outcome = outcome; List = list; Template = template; }
}

/// <summary>
/// X23 (D157): one list of templates, personal or the server's, in its shown order (a new personal template first, a
/// new server template last, as drawn). Every change checks the limits and returns a new list; refusals change nothing.
/// The list's revision rises with each change. Immutable.
/// </summary>
public sealed class TemplateList
{
    public TemplateListKind Kind { get; }
    public ulong Revision { get; }
    public IReadOnlyList<RuleTemplate> Templates { get; }

    public TemplateList(TemplateListKind kind, ulong revision, IEnumerable<RuleTemplate> templates)
    {
        if (!Enum.IsDefined(typeof(TemplateListKind), kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        Kind = kind; Revision = revision;
        var owned = (templates ?? throw new ArgumentNullException(nameof(templates))).Take(TemplateLimits.PerList + 1).ToArray();
        if (owned.Length > TemplateLimits.PerList) throw new ArgumentException("Too many templates.");
        if (owned.Any(x => x == null)) throw new ArgumentNullException(nameof(templates));
        if (owned.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != owned.Length) throw new ArgumentException("Duplicate template ID.");
        if (owned.Select(x => x.Name).Distinct(TemplateNames.Comparer).Count() != owned.Length) throw new ArgumentException("Duplicate template name.");
        Templates = Array.AsReadOnly(owned);
    }

    public static TemplateList Empty(TemplateListKind kind) => new TemplateList(kind, 0, Array.Empty<RuleTemplate>());

    public int Count => Templates.Count;
    public RuleTemplate? Find(string id) => Templates.FirstOrDefault(x => StringComparer.Ordinal.Equals(x.Id, id));
    public bool HasName(string name, string? except = null) =>
        Templates.Any(x => TemplateNames.Comparer.Equals(x.Name, name) && !StringComparer.Ordinal.Equals(x.Id, except));
    public bool HasId(string id) => Find(id) != null;

    /// <summary>
    /// Adds a template (Save, Share, Keep a copy): refused when the list is full, the name is taken, the template or the
    /// list would be too large. A personal template goes first, a server template last.
    /// </summary>
    public TemplateEdit Add(RuleTemplate template)
    {
        if (template == null) throw new ArgumentNullException(nameof(template));
        if (Count >= TemplateLimits.PerList) return Refuse(TemplateOutcome.Full);
        if (HasName(template.Name)) return Refuse(TemplateOutcome.NameTaken);
        if (HasId(template.Id)) throw new ArgumentException("Duplicate template ID.", nameof(template));
        var rows = Kind == TemplateListKind.Personal ? new[] { template }.Concat(Templates) : Templates.Concat(new[] { template });
        return Checked(rows, template);
    }

    /// <summary>"Update from this chest": the template keeps its ID, name and author and takes <paramref name="content"/>'s rule.</summary>
    public TemplateEdit Replace(string id, RuleTemplate content, ulong? seen = null)
    {
        if (content == null) throw new ArgumentNullException(nameof(content));
        if (Current(id, seen) is not { } old) return Refuse(TemplateOutcome.Changed);
        var next = old.WithRuleOf(content, old.Revision + 1);
        return Checked(Templates.Select(x => ReferenceEquals(x, old) ? next : x), next);
    }

    public TemplateEdit Rename(string id, string name, ulong? seen = null)
    {
        if (TemplateNames.Clean(name) is not { } clean) return Refuse(TemplateOutcome.NameInvalid);
        if (Current(id, seen) is not { } old) return Refuse(TemplateOutcome.Changed);
        if (HasName(clean, except: id)) return Refuse(TemplateOutcome.NameTaken);
        var next = old.With(name: clean, revision: old.Revision + 1);
        return Checked(Templates.Select(x => ReferenceEquals(x, old) ? next : x), next);
    }

    public TemplateEdit Delete(string id, ulong? seen = null)
    {
        if (Current(id, seen) is not { } old) return Refuse(TemplateOutcome.Changed);
        return new TemplateEdit(TemplateOutcome.Done, new TemplateList(Kind, Revision + 1, Templates.Where(x => !ReferenceEquals(x, old))), null);
    }

    /// <summary>A new ID this list doesn't hold.</summary>
    public string NewId(Func<int, int> random) => TemplateIds.New(random, HasId);

    // A change names the revision it saw (D157); a personal change names none (one game writes that list).
    private RuleTemplate? Current(string id, ulong? seen) => Find(id) is { } found && (seen == null || seen == found.Revision) ? found : null;

    private TemplateEdit Checked(IEnumerable<RuleTemplate> rows, RuleTemplate changed)
    {
        if (RuleTemplateCodec.TemplateBytes(changed) > TemplateLimits.TemplateBytes) return Refuse(TemplateOutcome.TooLarge);
        var next = new TemplateList(Kind, Revision + 1, rows);
        if (RuleTemplateCodec.Bytes(next) > TemplateLimits.ListBytes(Kind)) return Refuse(TemplateOutcome.ListTooLarge);
        return new TemplateEdit(TemplateOutcome.Done, next, changed);
    }

    private TemplateEdit Refuse(TemplateOutcome outcome) => new TemplateEdit(outcome, this, null);
}
