namespace YAVSOS.Configuration;

/// <summary>
/// Display hints for the BepInEx Configuration Manager, which finds this type by name on a setting's tags and reads its
/// fields. Only the hints YAVSOS uses are declared; a missing Configuration Manager ignores the tag.
/// </summary>
internal sealed class ConfigurationManagerAttributes
{
    /// <summary>Shown but not editable (D079: settings for behaviour another installed mod takes over).</summary>
    public bool? ReadOnly;
}
