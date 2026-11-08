using System;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;
using YAVSOS.Core.Templates;
using YAVSOS.Interface;

namespace YAVSOS.Core.Tests;

// D166: personal templates as one file in BepInEx/config that every character shares. Each test uses its own temporary
// config folder; a second PersonalTemplateFile on one path stands in for another character or a relog.
public sealed class PersonalTemplateFileTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "yavsos-personal-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { if (File.Exists(folder)) File.Delete(folder); else if (Directory.Exists(folder)) Directory.Delete(folder, true); } catch (IOException) { } }

    private string FilePath => PersonalTemplateFile.In(folder);
    private PersonalTemplateFile Open() => new PersonalTemplateFile(FilePath);

    private static RuleTemplate T(string id, string name) =>
        new RuleTemplate(id, name, 1, "", new[] { "food" }, null, null, Array.Empty<System.Collections.Generic.KeyValuePair<string, System.Collections.Generic.IEnumerable<string>>>(), 0);

    private static TemplateList With(TemplateListRead read, RuleTemplate template)
    {
        var edit = read.List!.Add(template);
        Assert.Equal(TemplateOutcome.Done, edit.Outcome);
        return edit.List;
    }

    [Fact]
    public void A_missing_file_is_an_empty_list_and_the_first_save_makes_the_folder()
    {
        var store = Open();
        var read = store.Read();
        Assert.Equal(TemplateReadState.Empty, read.State);
        Assert.True(read.Writable);
        Assert.False(File.Exists(FilePath));

        Assert.Equal(TemplateOutcome.Done, store.Write(With(read, T("aaaaaaaaaaaa", "Food"))));
        Assert.True(File.Exists(Path.Combine(folder, "com.bastionntb.yavsos.personal-templates.xml")));
        Assert.Equal("Food", store.Read().List!.Templates.Single().Name);
        Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
    }

    [Fact]
    public void Another_character_or_a_relog_reads_the_same_list()
    {
        var first = Open();
        var read = first.Read();
        Assert.Equal(TemplateOutcome.Done, first.Write(With(read, T("aaaaaaaaaaaa", "Food"))));

        var other = Open();
        Assert.Equal(new[] { "Food" }, other.Read().List!.Templates.Select(x => x.Name));
    }

    [Fact]
    public void The_same_read_comes_back_until_a_save_and_the_file_is_read_once()
    {
        var store = Open();
        var read = store.Read();
        Assert.Same(read, store.Read());

        // A hand edit while the game runs isn't read; the next game start (here Forget) reads it.
        Directory.CreateDirectory(folder);
        var other = Open();
        Assert.Equal(TemplateOutcome.Done, other.Write(With(other.Read(), T("bbbbbbbbbbbb", "Ores"))));
        Assert.Same(read, store.Read());
        store.Forget();
        Assert.Equal(new[] { "Ores" }, store.Read().List!.Templates.Select(x => x.Name));

        var saved = store.Read();
        Assert.Equal(TemplateOutcome.Done, store.Write(With(saved, T("aaaaaaaaaaaa", "Food"))));
        Assert.NotSame(saved, store.Read());
        Assert.Equal(new[] { "Food", "Ores" }, store.Read().List!.Templates.Select(x => x.Name)); // Add puts a personal template first.
    }

    [Theory]
    [InlineData("<yavsos-templates schema=\"2\" kind=\"personal\" revision=\"3\"><template future=\"yes\" /></yavsos-templates>", TemplateReadState.Newer)]
    [InlineData("not a template list", TemplateReadState.Malformed)]
    public void A_file_this_version_cant_read_is_never_written_over(string text, TemplateReadState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, text, new UTF8Encoding(false));
        var store = Open();
        var read = store.Read();
        Assert.Equal(state, read.State);
        Assert.False(read.Writable);
        Assert.Equal(TemplateOutcome.Unreadable, store.Write(With(TemplateListRead(), T("aaaaaaaaaaaa", "Food"))));
        Assert.Equal(text, File.ReadAllText(FilePath));
    }

    [Fact]
    public void Bytes_that_arent_utf8_are_kept_as_they_are()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var bytes = new byte[] { 0x3C, 0xFF, 0xFE, 0x3E };
        File.WriteAllBytes(FilePath, bytes);
        var store = Open();
        var read = store.Read();
        Assert.False(read.Writable);
        Assert.Equal(TemplateOutcome.Unreadable, store.Write(With(TemplateListRead(), T("aaaaaaaaaaaa", "Food"))));
        Assert.Equal(bytes, File.ReadAllBytes(FilePath));
    }

    [Fact]
    public void A_file_far_over_the_limit_isnt_read_or_written_over()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllBytes(FilePath, new byte[TemplateLimits.PersonalListBytes * 4 + 1]);
        var store = Open();
        var read = store.Read();
        Assert.Equal(TemplateReadState.Malformed, read.State);
        Assert.Equal(TemplateOutcome.Unreadable, store.Write(With(TemplateListRead(), T("aaaaaaaaaaaa", "Food"))));
        Assert.Equal(TemplateLimits.PersonalListBytes * 4 + 1, new FileInfo(FilePath).Length);
    }

    [Fact]
    public void A_file_that_cant_be_written_refuses_the_change_and_says_why()
    {
        // The save folder's place is taken by a file, so the folder can't be made.
        File.WriteAllText(folder, "in the way");
        var store = Open();
        var read = store.Read();
        Assert.Equal(TemplateOutcome.Unavailable, store.Write(With(read, T("aaaaaaaaaaaa", "Food"))));
        Assert.NotEqual("", store.LastError);
        Assert.Equal("in the way", File.ReadAllText(folder));
    }

    [Fact]
    public void Deleting_the_last_template_saves_an_empty_list()
    {
        var store = Open();
        var read = store.Read();
        Assert.Equal(TemplateOutcome.Done, store.Write(With(read, T("aaaaaaaaaaaa", "Food"))));
        var saved = store.Read();
        var deleted = saved.List!.Delete("aaaaaaaaaaaa");
        Assert.Equal(TemplateOutcome.Done, store.Write(deleted.List));
        var again = Open().Read();
        Assert.True(again.Writable);
        Assert.Equal(0, again.List!.Count);
    }

    [Fact]
    public void On_a_hosted_world_the_servers_templates_file_beside_it_is_left_alone()
    {
        // The host's game keeps both files in BepInEx/config (TemplateHost's is RuleTemplateCodec.FileName).
        Assert.NotEqual(RuleTemplateCodec.FileName, PersonalTemplateFile.FileName);
        Directory.CreateDirectory(folder);
        var server = Path.Combine(folder, RuleTemplateCodec.FileName);
        File.WriteAllText(server, "server list", new UTF8Encoding(false));
        var store = Open();
        Assert.Equal(TemplateOutcome.Done, store.Write(With(store.Read(), T("aaaaaaaaaaaa", "Food"))));
        Assert.Equal("server list", File.ReadAllText(server));
        Assert.Equal(new[] { PersonalTemplateFile.FileName, RuleTemplateCodec.FileName }.OrderBy(x => x, StringComparer.Ordinal),
            Directory.GetFiles(folder).Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void A_server_list_is_refused()
    {
        var store = Open();
        Assert.Throws<ArgumentException>(() => store.Write(TemplateList.Empty(TemplateListKind.Server)));
    }

    private static TemplateListRead TemplateListRead() => RuleTemplateCodec.Decode("", TemplateListKind.Personal);
}
