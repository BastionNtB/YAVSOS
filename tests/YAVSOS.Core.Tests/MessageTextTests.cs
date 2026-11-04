using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;
using YAVSOS.Core.Messages;

namespace YAVSOS.Core.Tests;

/// <summary>P10-04 (X30 step 2): the message formatter, the message-file reader, and the checks over the shipped files.</summary>
public sealed class MessageTextTests
{
    private static Func<string, string?> Table(params (string Key, string Text)[] rows)
    {
        var table = rows.ToDictionary(x => x.Key, x => x.Text, StringComparer.Ordinal);
        return key => table.TryGetValue(key, out var text) ? text : null;
    }

    [Fact]
    public void Format_FillsNamedPlaceholders_InTheTranslationsOrder()
    {
        var lookup = Table(("yavsos_t_moved", "{{item}} went to {{chest}}"), ("yavsos_t_moved_de", "In {{chest}}: {{item}}"));
        var message = Message.Of("yavsos_t_moved").With("chest", "Chest").With("item", "Copper");
        Assert.Equal("Copper went to Chest", MessageFormat.Format(message, lookup));
        Assert.Equal("In Chest: Copper", MessageFormat.Format(Message.Of("yavsos_t_moved_de").With("chest", "Chest").With("item", "Copper"), lookup));
    }

    [Fact]
    public void Format_ShowsAMissingKeyAndAnUnknownPlaceholder_SoBothAreVisible()
    {
        var lookup = Table(("yavsos_t_a", "Has {{name}} and {{other}}"));
        Assert.Equal("[yavsos_t_missing]", MessageFormat.Format(Message.Of("yavsos_t_missing"), lookup));
        Assert.Equal("Has x and {{other}}", MessageFormat.Format(Message.Of("yavsos_t_a").With("name", "x"), lookup));
    }

    [Fact]
    public void Format_LeavesBracesThatAreNotAPlaceholder()
    {
        var lookup = Table(("yavsos_t_a", "{{ name }} {x} {{}} {{n}"));
        Assert.Equal("{{ name }} {x} {{}} {{n}", MessageFormat.Format(Message.Of("yavsos_t_a").With("name", "v").With("n", "w"), lookup));
    }

    [Fact]
    public void Format_PicksThePluralFormByCount_AndShowsTheCount()
    {
        var lookup = Table(("yavsos_t_stacks_one", "{{count}} stack"), ("yavsos_t_stacks_other", "{{count}} stacks"));
        Assert.Equal("1 stack", MessageFormat.Format(Message.Of("yavsos_t_stacks").Count(1), lookup));
        Assert.Equal("0 stacks", MessageFormat.Format(Message.Of("yavsos_t_stacks").Count(0), lookup));
        Assert.Equal("12345 stacks", MessageFormat.Format(Message.Of("yavsos_t_stacks").Count(12345), lookup));
    }

    [Fact]
    public void Format_FallsBackToOther_WhenALanguageRuleNamesAFormTheFileLacks()
    {
        var lookup = Table(("yavsos_t_stacks_one", "{{count}} stack"), ("yavsos_t_stacks_other", "{{count}} stacks"));
        Assert.Equal("3 stacks", MessageFormat.Format(Message.Of("yavsos_t_stacks").Count(3), lookup, _ => "few"));
    }

    [Fact]
    public void Format_FormatsANestedMessage_SoOneSentenceCanHoldTwoCounts()
    {
        var lookup = Table(("yavsos_t_moved", "moved {{stacks}} ({{items}})"),
            ("yavsos_t_stacks_one", "{{count}} stack"), ("yavsos_t_stacks_other", "{{count}} stacks"),
            ("yavsos_t_items_one", "{{count}} item"), ("yavsos_t_items_other", "{{count}} items"));
        var message = Message.Of("yavsos_t_moved").With("stacks", Message.Of("yavsos_t_stacks").Count(1)).With("items", Message.Of("yavsos_t_items").Count(40));
        Assert.Equal("moved 1 stack (40 items)", MessageFormat.Format(message, lookup));
    }

    [Fact]
    public void Format_DoesNotReadPlaceholdersInsideAValue()
    {
        var lookup = Table(("yavsos_t_a", "Named {{name}}"));
        Assert.Equal("Named {{name}}", MessageFormat.Format(Message.Of("yavsos_t_a").With("name", "{{name}}"), lookup));
    }

    [Fact]
    public void Message_WithTheSameNameReplacesTheValue_AndEqualsComparesKeyAndValues()
    {
        var a = Message.Of("yavsos_t_a").With("x", "1").With("x", "2");
        Assert.Single(a.Args);
        Assert.Equal(a, Message.Of("yavsos_t_a").With("x", "2"));
        Assert.NotEqual(a, Message.Of("yavsos_t_a").With("x", 2));
        Assert.NotEqual(a, Message.Of("yavsos_t_b").With("x", "2"));
        Assert.Throws<ArgumentException>(() => Message.Of(""));
    }

    [Fact]
    public void Format_TintsAValue_OnlyWhenTheCallerWritesMarkup()
    {
        var lookup = Table(("yavsos_t_a", "Label: {{label}}."), ("yavsos_t_b", "inner {{x}}"));
        var message = Message.Of("yavsos_t_a").With("label", "Copper here", Tone.Label);
        Assert.Equal("Label: Copper here.", MessageFormat.Format(message, lookup));
        Assert.Equal("Label: <Label>Copper here</Label>.", MessageFormat.Format(message, lookup, tint: (tone, text) => "<" + tone + ">" + text + "</" + tone + ">"));
        var nested = Message.Of("yavsos_t_a").With("label", Message.Of("yavsos_t_b").With("x", "y"), Tone.Faint);
        Assert.Equal("Label: [Faint:inner y].", MessageFormat.Format(nested, lookup, tint: (tone, text) => "[" + tone + ":" + text + "]"));
        Assert.Equal("Label: .", MessageFormat.Format(Message.Of("yavsos_t_a").With("label", "", Tone.Label), lookup, tint: (_, text) => "<" + text + ">"));
    }

    [Theory]
    [InlineData(0, "Pick .")]
    [InlineData(1, "Pick Copper.")]
    [InlineData(2, "Pick Copper or Tin.")]
    [InlineData(3, "Pick Copper, Tin or Iron.")]
    [InlineData(5, "Pick Copper, Tin, Iron, Silver or Wood.")]
    public void List_JoinsWithTheEnglishFilesPatterns(int count, string expected)
    {
        var names = new[] { "Copper", "Tin", "Iron", "Silver", "Wood" }.Take(count);
        var lookup = Table(("yavsos_t_pick", "Pick {{names}}."));
        Func<string, string?> both = key => lookup(key) ?? (EnglishText.File.TryGetValue(key, out var text) ? text : null);
        Assert.Equal(expected, MessageFormat.Format(Message.Of("yavsos_t_pick").With("names", MessageList.Of(ListKind.Or, names)), both));
    }

    [Fact]
    public void List_UsesEachLanguagesPatterns_AndSaysHowManyMore()
    {
        // A language whose last item takes another word order, as CLDR's patterns allow.
        var lookup = Table(("yavsos_list_and_pair", "{{first}} & {{rest}}"), ("yavsos_list_and_start", "{{first}}; {{rest}}"),
            ("yavsos_list_and_middle", "{{first}} / {{rest}}"), ("yavsos_list_and_end", "{{first}} + {{rest}}"),
            ("yavsos_list_more_other", "{{list}} (+{{count}})"), ("yavsos_t_x", "{{x}}"));
        Message Of(MessageList list) => Message.Of("yavsos_t_x").With("x", list);
        Assert.Equal("a & b", MessageFormat.Format(Of(MessageList.And("a", "b")), lookup));
        Assert.Equal("a; b / c + d", MessageFormat.Format(Of(MessageList.And("a", "b", "c", "d")), lookup));
        Assert.Equal("a & b (+3)", MessageFormat.Format(Of(MessageList.And("a", "b").WithMore(3)), lookup));
        Assert.Equal("(+3)", MessageFormat.Format(Of(MessageList.And().WithMore(3)), lookup));
        Assert.Equal("Copper, Tin +1 more", EnglishText.Of(MessageList.Comma("Copper", "Tin").WithMore(1)));
        Assert.Equal("a\nb\n… and 2 more", EnglishText.Of(MessageList.Of(ListKind.Lines, new[] { "a", "b" }).WithMore(2)));
    }

    [Fact]
    public void List_TakesMessagesAndTintedItems_AndNestsWithoutADepthLimit()
    {
        var lookup = Table(("yavsos_t_x", "{{x}}"), ("yavsos_t_always", "Always: {{name}}"), ("yavsos_list_comma_pair", "{{first}}, {{rest}}"),
            ("yavsos_list_comma_start", "{{first}}, {{rest}}"), ("yavsos_list_comma_middle", "{{first}}, {{rest}}"), ("yavsos_list_comma_end", "{{first}}, {{rest}}"));
        var list = MessageList.Of(ListKind.Comma, new object[] { "Wood", Message.Of("yavsos_t_always").With("name", "Copper"), Tinted.Of("Tin", Tone.Caution) });
        Assert.Equal("Wood, Always: Copper, [Caution:Tin]", MessageFormat.Format(Message.Of("yavsos_t_x").With("x", list), lookup, tint: (tone, text) => "[" + tone + ":" + text + "]"));
        var many = MessageList.Of(ListKind.Comma, Enumerable.Range(1, 40).Select(x => x.ToString()));
        Assert.Equal(string.Join(", ", Enumerable.Range(1, 40)), MessageFormat.Format(Message.Of("yavsos_t_x").With("x", many), lookup));
        Assert.Throws<ArgumentException>(() => MessageList.Of(ListKind.Comma, new object[] { 3 }));
        Assert.Equal(list, MessageList.Of(ListKind.Comma, new object[] { "Wood", Message.Of("yavsos_t_always").With("name", "Copper"), Tinted.Of("Tin", Tone.Caution) }));
    }

    [Fact]
    public void Tooltip_FormatsHeadingAndBody_Separately()
    {
        var lookup = Table(("yavsos_t_head", "Always: {{name}}"), ("yavsos_t_body", "Line one.\nLine two."));
        Assert.Equal(("Always: Copper", "Line one.\nLine two."),
            Tooltip.Of(Message.Of("yavsos_t_head").With("name", "Copper"), Message.Of("yavsos_t_body")).Format(lookup));
        Assert.Equal(("Metals", "Line one.\nLine two."), Tooltip.Named("Metals", Message.Of("yavsos_t_body")).Format(lookup));
        Assert.Equal(("", "Line one.\nLine two."), Tooltip.Text(Message.Of("yavsos_t_body")).Format(lookup));
        Assert.Equal(("Metals", ""), Tooltip.Named("Metals").Format(lookup));
        Assert.Equal(Tooltip.Named("Metals", Message.Of("yavsos_t_body")), Tooltip.Named("Metals", Message.Of("yavsos_t_body")));
        Assert.NotEqual(Tooltip.Named("Metals"), Tooltip.Text(Message.Of("yavsos_t_body")));
    }

    [Theory]
    [InlineData("yavsos_hud_message", true)]
    [InlineData("yavsos_result_moved_stacks_one", true)]
    [InlineData("yavsos_stash.moved_one", false)]
    [InlineData("yavsos_Stash", false)]
    [InlineData("yavsos_", false)]
    [InlineData("yavsos_a__b", false)]
    [InlineData("yavsos_a_", false)]
    [InlineData("stash_moved", false)]
    public void IsValidKey_AllowsOnlyJotunnSafeLowerCaseKeys(string key, bool valid) => Assert.Equal(valid, MessageFormat.IsValidKey(key));

    [Fact]
    public void PluralForm_AndBaseKey_SplitTheCldrSuffix()
    {
        Assert.Equal("one", MessageFormat.PluralForm("yavsos_x_one"));
        Assert.Null(MessageFormat.PluralForm("yavsos_x_ones"));
        Assert.Equal("yavsos_x", MessageFormat.BaseKey("yavsos_x_other"));
        Assert.Equal("yavsos_x_y", MessageFormat.BaseKey("yavsos_x_y"));
    }

    [Fact]
    public void FlatJson_ReadsStringsAndEscapes()
    {
        var parsed = FlatJson.Parse("﻿{ \"a\": \"x\\\"y\\n\\u00e9\\\\\", \"b\":\"\" }");
        Assert.Equal("x\"y\né\\", parsed["a"]);
        Assert.Equal("", parsed["b"]);
        Assert.Empty(FlatJson.Parse(" {} "));
    }

    [Theory]
    [InlineData("{\"a\": {\"b\": \"c\"}}")]
    [InlineData("{\"a\": 1}")]
    [InlineData("{\"a\": \"x\", \"a\": \"y\"}")]
    [InlineData("{\"a\": \"x\"} extra")]
    [InlineData("{\"a\": \"x\",}")]
    [InlineData("{\"a\": \"x")]
    [InlineData("[\"a\"]")]
    [InlineData("{\"a\": \"line\nbreak\"}")]
    public void FlatJson_RefusesWhatJotunnWouldRefuse(string text) => Assert.Throws<FormatException>(() => FlatJson.Parse(text));

    // ---- the shipped files ----

    private static string Root([CallerFilePath] string path = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", ".."));
    private static string Translations => Path.Combine(Root(), "src", "YAVSOS", "Translations");
    private static Dictionary<string, string> English => FlatJson.Parse(File.ReadAllText(Path.Combine(Translations, "English", "yavsos.json")));

    /// <summary>Every "yavsos_…" string literal in the shipped and Lab code.</summary>
    private static HashSet<string> UsedKeys()
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var literal = new Regex("\"(yavsos_[A-Za-z0-9_]+)\"");
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Root(), "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)) continue;
            foreach (Match match in literal.Matches(File.ReadAllText(file))) used.Add(match.Groups[1].Value);
        }
        return used;
    }

    [Fact]
    public void EnglishFile_KeysAreValidAndSorted()
    {
        var text = File.ReadAllText(Path.Combine(Translations, "English", "yavsos.json"));
        var keys = Regex.Matches(text, "^  \"([^\"]+)\":", RegexOptions.Multiline).Select(x => x.Groups[1].Value).ToList();
        Assert.Equal(English.Count, keys.Count);
        Assert.All(keys, key => Assert.True(MessageFormat.IsValidKey(key), key + " is not a valid key"));
        Assert.Equal(keys.OrderBy(x => x, StringComparer.Ordinal), keys);
    }

    [Fact]
    public void EnglishFile_PluralKeysComeInPairs_AndOtherKeysDontEndInAPluralWord()
    {
        var english = English;
        foreach (var key in english.Keys)
        {
            if (MessageFormat.PluralForm(key) is not { } form) continue;
            Assert.True(form is "one" or "other", key + ": English has only the one and other forms");
            var root = MessageFormat.BaseKey(key);
            Assert.True(english.ContainsKey(root + "_one") && english.ContainsKey(root + "_other"), key + " needs both _one and _other");
            Assert.False(english.ContainsKey(root), root + " is also a plain key");
        }
    }

    [Fact]
    public void EveryKeyTheCodeUses_IsInTheEnglishFile()
    {
        var english = English;
        var missing = UsedKeys().Where(key => !english.ContainsKey(key) && !english.ContainsKey(key + "_other")).OrderBy(x => x).ToList();
        Assert.True(missing.Count == 0, "Not in the English file: " + string.Join(", ", missing));
    }

    [Fact]
    public void EveryEnglishKey_IsUsedByTheCode()
    {
        var used = UsedKeys();
        var unused = English.Keys.Where(key => !used.Contains(key) && !used.Contains(MessageFormat.BaseKey(key))).OrderBy(x => x).ToList();
        Assert.True(unused.Count == 0, "No code uses: " + string.Join(", ", unused));
    }

    [Fact]
    public void NotesFile_HasANoteForEveryEnglishKey_AndNoOther()
    {
        var notes = FlatJson.Parse(File.ReadAllText(Path.Combine(Translations, "notes.json")));
        var english = English.Keys.Select(MessageFormat.BaseKey).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.Equal(english, notes.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList());
        Assert.All(notes, note => Assert.False(string.IsNullOrWhiteSpace(note.Value), note.Key + " has an empty note"));
    }

    [Fact]
    public void EveryLanguage_HasTheEnglishPlaceholders_AndOnlyEnglishKeys()
    {
        var english = English;
        foreach (var folder in Directory.EnumerateDirectories(Translations))
        {
            var language = Path.GetFileName(folder);
            Assert.True(char.IsUpper(language[0]), language + ": Jotunn needs Valheim's language name, which starts with a capital");
            var file = Path.Combine(folder, "yavsos.json");
            Assert.True(File.Exists(file), language + " has no yavsos.json");
            foreach (var row in FlatJson.Parse(File.ReadAllText(file)))
            {
                var source = english.TryGetValue(row.Key, out var exact) ? exact
                    : english.TryGetValue(MessageFormat.BaseKey(row.Key) + "_other", out var other) ? other : null;
                Assert.True(source != null, language + ": " + row.Key + " is not an English key");
                Assert.Equal(MessageFormat.Placeholders(source!).OrderBy(x => x), MessageFormat.Placeholders(row.Value).OrderBy(x => x));
            }
        }
    }
}
