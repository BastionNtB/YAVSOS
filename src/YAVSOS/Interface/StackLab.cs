using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using YAVSOS.Cleanup;
using YAVSOS.Core.Interface;
using YAVSOS.Core.Transfers;
using YAVSOS.Transfers;

namespace YAVSOS.Interface;

/// <summary>
/// X65 (#78, D161, D163): Lab dump of the chests near the player with the name YAVSOS gives each one (the picked
/// place-in-stack form), the search tooltip's and the marker's text, for the client check. Run at the lab's capture wall. One line per chest,
/// tab-separated <c>key=value</c> fields, also written to the log with the prefix "YAVSOS stack place:". Read only.
/// Lab build only (through the test bridge's debug/call_method). Unity game thread only.
/// </summary>
internal static class StackLab
{
    internal const string Prefix = "YAVSOS stack place:";

    /// <summary>Every container within <paramref name="radius"/> m of the player, named from where the player stands.</summary>
    public static string Dump(float radius)
    {
        var player = Player.m_localPlayer;
        if (!player) return "no player";
        var p = player.transform.position;
        return DumpFrom(p.x, p.y, p.z, radius);
    }

    /// <summary>As <see cref="Dump"/>, but named as a player standing at (x, y, z) would read them; chests within <paramref name="radius"/> of that point.</summary>
    public static string DumpFrom(float x, float y, float z, float radius)
    {
        var plugin = Plugin.Instance;
        if (plugin == null || !Player.m_localPlayer) return "no player";
        var here = new Vector3(x, y, z);
        var chests = CleanupRunner.Scene().Where(c => c && Vector3.Distance(c.transform.position, here) <= radius)
            .OrderBy(c => Vector3.Distance(c.transform.position, here)).ToList();
        var started = System.Diagnostics.Stopwatch.StartNew();
        var places = ChestStackPlaces.Of(chests); // X65 (D163): the grouping the chest names use.
        var grouped = started.Elapsed.TotalMilliseconds;
        var lines = new List<string>(); var names = new List<string>();
        for (var i = 0; i < chests.Count; i++)
        {
            var chest = chests[i]; var at = chest.transform.position;
            var name = GameText.Localize(chest.m_name);
            places.TryGetValue(chest, out var place);
            // The where-line's and result panel's name (D163): flat distance, then the place in the stack.
            var text = Texts.English(ChestName.Away(name, ChestStackPlaces.Across(here, at), place).Said);
            var tip = Texts.English(HighlightText.TipChest(name, ChestStackPlaces.Across(here, at), place));
            var marker = ChestPlaceText.Marker(place) is { } tag ? Texts.English(tag) : "-";
            names.Add(text);
            lines.Add("chest\t" + string.Join("\t", new[]
            {
                "n=" + (i + 1), "prefab=" + Prefab(chest), "name=" + name, "pos=" + F(at.x) + "," + F(at.y) + "," + F(at.z),
                "eligible=" + ChestRuleStore.Eligibility(chest), "category=" + FirstCategory(plugin, chest),
                "distance=" + F(Vector3.Distance(here, at)), "across=" + F(Across(here, at)), "height=" + F(at.y - here.y),
                "stack=" + place.Stack, "place=" + place.Place + "/" + place.Size, "text=" + text, "tip=" + tip, "marker=" + marker
            }));
        }
        // How many chests share their name with another chest: the fewer, the better the names tell them apart.
        var shared = names.GroupBy(t => t).Where(g => g.Count() > 1).Sum(g => g.Count());
        var stacks = places.Values.Where(p => p.Stacked).Select(p => p.Stack).Distinct().Count();
        lines.Insert(0, $"stacks\tv=2\tfrom={F(x)},{F(y)},{F(z)}\tradius={F(radius)}\tchests={chests.Count}\tstacks={stacks}\ttolerance={F(ChestStacks.DefaultTolerance)}\t" +
            $"gap={F(ChestStacks.DefaultGap)}\tgroup_ms={grouped.ToString("0.000", CultureInfo.InvariantCulture)}\tshared_text={shared}");
        foreach (var line in lines) plugin.LogForLab.LogInfo(Prefix + " " + line);
        return string.Join("\n", lines);
    }

    private static ChestSpot Spot(Vector3 at) => new ChestSpot(at.x, at.y, at.z);
    private static float Across(Vector3 a, Vector3 b) => new Vector2(a.x - b.x, a.z - b.z).magnitude;
    private static string F(float value) => value.ToString("0.00", CultureInfo.InvariantCulture);
    private static string Prefab(Container chest)
    {
        var root = chest.GetComponentInParent<ZNetView>();
        var name = (root ? root!.gameObject : chest.gameObject).name;
        return name.EndsWith("(Clone)", StringComparison.Ordinal) ? name.Substring(0, name.Length - 7) : name;
    }

    // The rule's first ticked category ID, "items" for a rule with only Always items, "-" without a rule or unreadable.
    private static string FirstCategory(Plugin plugin, Container chest)
    {
        try
        {
            var rules = plugin.ChestRules.Read(chest).Policy?.Rules;
            if (rules == null) return "-";
            var view = ChestRuleEdit.From(rules);
            return view.Categories.FirstOrDefault() ?? (view.Includes.Any() ? "items" : "-");
        }
        catch (Exception error) when (Integrations.OptionalFailure.Recoverable(error)) { return "unreadable"; }
    }
}
