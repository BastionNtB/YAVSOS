namespace YAVSOS.Configuration;

/// <summary>
/// Display hints for the BepInEx Configuration Manager, which finds this type by name on a setting's tags and copies its
/// non-null fields onto the setting it shows each time its window opens. Only the hints YAVSOS uses are declared; a
/// missing Configuration Manager ignores the tag. ServerSync adds its own tag of the same name ahead of this one and keeps
/// this object, so a field changed later still shows (D079).
/// </summary>
internal sealed class ConfigurationManagerAttributes
{
    /// <summary>Shown but not editable (D079: settings for behaviour another installed mod takes over).</summary>
    public bool? ReadOnly;

    /// <summary>
    /// P10-04 (EXTRACTION-TOOL-CHECK §3, route A): the description in the player's language. The bound description stays
    /// English, so the <c>.cfg</c> file and <c>docs/SETTINGS.md</c> stay English. A Configuration Manager that reads only
    /// a tag's properties ignores this field and shows the English description.
    /// </summary>
    public string? Description;
}
