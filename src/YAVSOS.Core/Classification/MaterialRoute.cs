using System;
using System.Collections.Generic;
using System.Linq;

namespace YAVSOS.Core.Classification;

/// <summary>One required input of a loaded production route (conversion, cultivation or exchange).</summary>
public sealed class MaterialLink
{
    public string Input { get; }
    public string Output { get; }
    public string Kind { get; }
    public string Producer { get; }
    public bool Enabled { get; }
    public int Amount { get; }
    public bool UpgradeOnly { get; }
    public MaterialLink(string input, string output, string kind, string producer,
        bool enabled = true, int amount = 1, bool upgradeOnly = false)
    {
        Input = MaterialRoute.Identity(input); Output = MaterialRoute.Identity(output);
        Kind = MaterialRoute.Identity(kind); Producer = MaterialRoute.Identity(producer);
        Enabled = enabled; Amount = amount; UpgradeOnly = upgradeOnly;
    }
    internal bool CarriesMaterial => Enabled && Amount > 0 && !UpgradeOnly &&
        (Kind == "craft" || Kind == "smelt" || Kind == "cook" || Kind == "ferment" || Kind == "cultivate" ||
         Kind == "produce" || Kind == "hatch" || Kind == "grow" || Kind == "exchange" || Kind == "disenchant");
}

/// <summary>A route preserves the producer's AND requirements; alternatives are separate routes.</summary>
public sealed class MaterialRoute
{
    public string Id { get; }
    public string Output { get; }
    public bool AnyIngredient { get; }
    public IReadOnlyList<MaterialLink> Inputs { get; }
    public MaterialRoute(string id, IEnumerable<MaterialLink> inputs, bool anyIngredient = false)
    {
        Id = Identity(id);
        var rows = inputs.Take(257).ToArray();
        if (rows.Length == 0 || rows.Length > 256 || rows.Any(x => x == null || !x.CarriesMaterial))
            throw new ArgumentException("A production route needs 1–256 positive enabled creation inputs.");
        Output = rows[0].Output; AnyIngredient = anyIngredient;
        if (rows.Any(x => x.Output != Output || x.Kind != rows[0].Kind || x.Producer != rows[0].Producer))
            throw new ArgumentException("A route must describe one producer, kind and output.");
        Inputs = Array.AsReadOnly(rows.GroupBy(x => x.Input, StringComparer.Ordinal)
            .Select(g => g.OrderBy(x => x.Amount).First()).OrderBy(x => x.Input, StringComparer.Ordinal).ToArray());
    }
    internal static string Identity(string value) => string.IsNullOrWhiteSpace(value) || value.Length > 2048
        ? throw new ArgumentException("Expected a nonempty bounded identity/evidence value.") : value;
}
