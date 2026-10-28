using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace YAVSOS.Core.Catalog;

/// <summary>Detached per-capture identities for recipe definitions; Unity names need not be unique.</summary>
public sealed class CatalogRecipeIdentity
{
    private readonly Dictionary<string,int> occurrences = new Dictionary<string,int>(StringComparer.Ordinal);
    public IReadOnlyList<CatalogFact> Add(CatalogFact recipe, IReadOnlyList<CatalogFact> requirements)
    {
        if (recipe == null || requirements == null) throw new ArgumentNullException(nameof(recipe));
        if (recipe.Kind != CatalogFactKind.Recipe || recipe.Values.ContainsKey("unityName") || requirements.Count > 256)
            throw new ArgumentException("Expected a raw bounded recipe definition.");
        var slots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var slot in requirements)
            if (slot == null || slot.Kind != CatalogFactKind.Requirement || slot.Value("recipe") != recipe.Identity || !slots.Add(slot.Identity))
                throw new ArgumentException("Expected distinct requirements of this recipe.");
        using var sha = SHA256.Create();
        using var stream = new CryptoStream(Stream.Null, sha, CryptoStreamMode.Write);
        using var writer = new BinaryWriter(stream, new UTF8Encoding(false,true), true);
        void Write(CatalogFact fact)
        {
            writer.Write(fact.Identity); writer.Write(fact.Values.Count);
            foreach (var pair in fact.Values) { writer.Write(pair.Key); writer.Write(pair.Value); }
        }
        writer.Write("yavsos-loaded-recipe-1"); Write(recipe); writer.Write(requirements.Count);
        foreach (var slot in requirements) Write(slot); // Resource slot order is captured evidence.
        writer.Flush(); stream.FlushFinalBlock();
        var hash = BitConverter.ToString(sha.Hash!).Replace("-", "").ToLowerInvariant();
        occurrences.TryGetValue(hash, out var occurrence);
        var identity = "recipe:" + hash + ":" + occurrence.ToString(CultureInfo.InvariantCulture);
        var rows = new List<CatalogFact>(requirements.Count+1) {
            new CatalogFact(CatalogFactKind.Recipe, identity, recipe.Values.Concat(new[] { new KeyValuePair<string,string>("unityName",recipe.Identity) })) };
        for (var i=0;i<requirements.Count;i++)
            rows.Add(new CatalogFact(CatalogFactKind.Requirement, identity+":"+i.ToString(CultureInfo.InvariantCulture),
                requirements[i].Values.Select(p => p.Key == "recipe" ? new KeyValuePair<string,string>(p.Key,identity) : p)));
        occurrences[hash] = occurrence+1;
        return rows.AsReadOnly();
    }
}
