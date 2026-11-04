using YAVSOS.Core.Classification;

namespace YAVSOS.Interface;

/// <summary>
/// P10-04 (S13): category names in the player's language. A built-in category shows its message, an admin-set name
/// shows as written (<see cref="CategoryNames"/>). One set of names for the current taxonomy, built again after a
/// language change or when the taxonomy changes. Main thread only, like the panel, labels and tooltips that use it.
/// </summary>
internal static class CategoryText
{
    private static CategoryNames? cached;
    private static int language;

    internal static CategoryNames For(ClassificationTaxonomy taxonomy)
    {
        var current = cached;
        if (current != null && ReferenceEquals(current.Taxonomy, taxonomy) && language == Texts.Generation) return current;
        language = Texts.Generation;
        return cached = new CategoryNames(taxonomy, Texts.Get);
    }

    /// <summary>A category's name as the player sees it; an unknown ID as is.</summary>
    internal static string Name(ClassificationTaxonomy taxonomy, string id) => For(taxonomy).Shown(id);
}
