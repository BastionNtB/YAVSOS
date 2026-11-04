using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using YAVSOS.Core.Messages;

namespace YAVSOS.Core.Tests;

/// <summary>
/// P10-04 (S14, EXTRACTION-TOOL-CHECK §3 route A): each setting's description is the message
/// <c>yavsos_setting_&lt;section&gt;_&lt;key&gt;</c>, which <c>scripts/settings_doc.py</c> derives from the setting's section and
/// name. These checks keep the code and the English file in step with that rule, so the detached tests catch a drift
/// before <c>package.py</c> runs the script.
/// </summary>
public sealed class SettingTextTests
{
    private static string Source => File.ReadAllText(Path.Combine(EnglishText.Root(), "src", "YAVSOS", "Configuration", "PluginSettings.cs"));

    // Bind(file, sync, "Section", "Key", value, [Stash(]Message.Of("yavsos_setting_…") …) and the locking
    // file.Bind("Section", "Key", value, Describe(Message.Of(…))).
    private static readonly Regex Setting = new(
        "(?:Bind\\(file, sync, |file\\.Bind\\()\"(\\w+)\", \"(\\w+)\",[^\\n]*\\n?[^\\n]*?(?:Stash\\(|Describe\\()?Message\\.Of\\(\"(yavsos_setting_\\w+)\"\\)");

    [Fact]
    public void EverySettingDescription_UsesTheKeyDerivedFromItsSectionAndName()
    {
        var found = Setting.Matches(Source).Cast<Match>().ToList();
        Assert.Equal(27, found.Count);
        Assert.All(found, match => Assert.Equal(("yavsos_setting_" + match.Groups[1].Value + "_" + match.Groups[2].Value).ToLowerInvariant(), match.Groups[3].Value));
    }

    [Fact]
    public void EverySettingKeyInTheEnglishFile_BelongsToASetting_OrIsTheAzuAutoStorePrefix()
    {
        var bound = Setting.Matches(Source).Cast<Match>().Select(match => match.Groups[3].Value).Append("yavsos_setting_stash_azuautostore");
        Assert.Equal(bound.OrderBy(x => x, System.StringComparer.Ordinal),
            EnglishText.File.Keys.Where(key => key.StartsWith("yavsos_setting_", System.StringComparison.Ordinal)).OrderBy(x => x, System.StringComparer.Ordinal));
    }

    [Fact]
    public void AzuAutoStorePrefix_HoldsTheWholeDescription()
    {
        var text = EnglishText.Of(Message.Of("yavsos_setting_stash_azuautostore").With("description", Message.Of("yavsos_setting_stash_shortcut")));
        Assert.StartsWith("AzuAutoStore is installed and does the stashing, so this setting is read-only", text);
        Assert.EndsWith(EnglishText.Of(Message.Of("yavsos_setting_stash_shortcut")), text);
    }

    [Fact]
    public void RangeDescriptions_SayMeters_SoTheSettingsDocumentAddsTheUnit()
    {
        foreach (var key in new[] { "yavsos_setting_cleanup_range", "yavsos_setting_stash_range", "yavsos_setting_server_maximumrange" })
            Assert.Matches("\\bmeters\\b", EnglishText.File[key]);
        Assert.DoesNotContain(EnglishText.File, row => row.Key.StartsWith("yavsos_setting_", System.StringComparison.Ordinal) && Regex.IsMatch(row.Value, "metre|colour"));
    }
}
