using System.Text.RegularExpressions;

namespace GrafanaToCx.Core.Tests;

/// <summary>
/// Two whole classes of dashboard rejection came from the converter emitting a proto enum value
/// that Coralogix does not define — <c>LEGEND_COLUMN_FIRST</c> took down six dashboards, and
/// <c>UNIT_BITS</c> was sitting in the unit table waiting for the first dashboard to use Grafana's
/// <c>bits</c> unit. Both were plausible-looking names that no test asserted against the schema.
/// This checks every such constant the converter can emit against the published spec, so the next
/// invented value fails here rather than mid-migration.
/// </summary>
public sealed class SpecEnumConformanceTests
{
    /// <summary>
    /// Screaming-snake literals that are deliberately not proto enum members: environment variable
    /// names and a Grafana-side variable name.
    /// </summary>
    private static readonly HashSet<string> NotEnumValues = new(StringComparer.Ordinal)
    {
        "CX_API_KEY",
        "CX_REGION",
        "DS_PROMETHEUS"
    };

    [Fact]
    public void EveryEnumConstantTheConverterEmits_ExistsInTheCoralogixSpec()
    {
        var root = TestFixtureLoader.RepositoryRoot();
        var specPath = Path.Combine(root, "spec", "openapi (1).yaml");
        Assert.True(File.Exists(specPath), $"Coralogix spec not found at '{specPath}'.");

        // Enum members appear as bare YAML list items: "        - UNIT_BYTES".
        var declared = Regex.Matches(File.ReadAllText(specPath), @"^\s+- ([A-Z][A-Z0-9_]{2,})$", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(declared);

        var converterRoot = Path.Combine(root, "src", "GrafanaToCx.Core");
        var offenders = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(converterRoot, "*.cs", SearchOption.AllDirectories))
        {
            foreach (Match match in Regex.Matches(File.ReadAllText(file), @"""([A-Z][A-Z0-9]*_[A-Z0-9_]+)"""))
            {
                var value = match.Groups[1].Value;
                if (declared.Contains(value) || NotEnumValues.Contains(value))
                    continue;

                if (!offenders.TryGetValue(value, out var files))
                {
                    offenders[value] = files = [];
                }

                var relative = Path.GetRelativePath(root, file);
                if (!files.Contains(relative))
                {
                    files.Add(relative);
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "These constants are not members of any enum in the Coralogix spec, so the API will "
            + "reject any dashboard carrying them:\n"
            + string.Join("\n", offenders.Select(o => $"  {o.Key} — {string.Join(", ", o.Value)}")));
    }
}
