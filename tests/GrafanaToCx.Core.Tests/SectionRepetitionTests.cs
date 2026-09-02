using GrafanaToCx.Core.Converter;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;

namespace GrafanaToCx.Core.Tests;

/// <summary>
/// Grafana repeats a panel; Coralogix repeats a section. A repeating panel therefore has to be
/// lifted into a section of its own, carrying options.custom.repetitiveVar.
/// </summary>
public class SectionRepetitionTests
{
    private static JObject Panel(int id, string title, string? repeat = null)
    {
        var panel = new JObject
        {
            ["id"] = id,
            ["type"] = "timeseries",
            ["title"] = title,
            ["targets"] = new JArray(new JObject { ["refId"] = "A", ["expr"] = "up" })
        };
        if (repeat is not null) panel["repeat"] = repeat;
        return panel;
    }

    private static JObject Variable(string name, bool multi) => new()
    {
        ["name"] = name,
        ["type"] = "query",
        ["query"] = "label_values(up, instance)",
        ["multi"] = multi,
        ["includeAll"] = multi,
        ["current"] = new JObject { ["value"] = "a", ["text"] = "a" }
    };

    private static JObject Row(int id, string title, string? repeat = null, params JObject[] collapsedChildren)
    {
        var row = new JObject
        {
            ["id"] = id,
            ["type"] = "row",
            ["title"] = title
        };
        if (repeat is not null) row["repeat"] = repeat;
        if (collapsedChildren.Length > 0)
        {
            row["collapsed"] = true;
            row["panels"] = new JArray(collapsedChildren.Cast<object>().ToArray());
        }
        return row;
    }

    private static (JArray sections, IReadOnlyList<DashboardConversionDiagnostic> diagnostics) Convert(
        JArray panels, params JObject[] variables)
    {
        var dashboard = new JObject
        {
            ["title"] = "Board",
            ["panels"] = panels,
            ["templating"] = new JObject { ["list"] = new JArray(variables.Cast<object>().ToArray()) }
        };

        var converter = new GrafanaToCxConverter(NullLogger<GrafanaToCxConverter>.Instance);
        var result = converter.ConvertToJObject(dashboard.ToString());
        return ((JArray)result["layout"]!["sections"]!, converter.DashboardDiagnostics);
    }

    private static JObject? RepetitiveVar(JToken section) =>
        section["options"]?["custom"]?["repetitiveVar"] as JObject;

    [Fact]
    public void RepeatingPanel_GetsItsOwnRepeatingSection()
    {
        var (sections, _) = Convert(
            new JArray(Panel(1, "Per host", repeat: "host")),
            Variable("host", multi: true));

        var section = Assert.Single(sections);
        Assert.Equal("host", RepetitiveVar(section)?.Value<string>("name"));
    }

    [Fact]
    public void RepeatingPanel_IsSeparatedFromItsNeighbours()
    {
        // Coralogix repeats the whole section, so neighbours must not be swept along.
        var (sections, _) = Convert(
            new JArray(Panel(1, "Plain"), Panel(2, "Per host", repeat: "host"), Panel(3, "Another")),
            Variable("host", multi: true));

        Assert.Equal(3, sections.Count);
        Assert.Null(RepetitiveVar(sections[0]));
        Assert.Equal("host", RepetitiveVar(sections[1])?.Value<string>("name"));
        Assert.Null(RepetitiveVar(sections[2]));
    }

    [Fact]
    public void RepeatingSection_UsesCustomOptions_NotInternal()
    {
        // SectionOptions is a oneof: setting custom alongside internal is rejected by the API.
        var (sections, _) = Convert(
            new JArray(Panel(1, "Per host", repeat: "host")),
            Variable("host", multi: true));

        Assert.NotNull(sections[0]["options"]?["custom"]);
        Assert.Null(sections[0]["options"]?["internal"]);
    }

    [Fact]
    public void HonouredRepeat_IsNotReportedAsLost()
    {
        var (_, diagnostics) = Convert(
            new JArray(Panel(1, "Per host", repeat: "host")),
            Variable("host", multi: true));

        Assert.DoesNotContain(diagnostics, d => d.ElementKind == "panelRepeat");
    }

    [Fact]
    public void RepeatOverAMissingVariable_IsReportedAndNotEmitted()
    {
        // Every repeat in the sample corpus is orphaned like this — it does not repeat in
        // Grafana either, so inventing a reference would be worse than reporting it.
        var (sections, diagnostics) = Convert(
            new JArray(Panel(1, "Per host", repeat: "host")),
            Variable("hostname", multi: true));

        Assert.Null(RepetitiveVar(Assert.Single(sections)));
        var reported = Assert.Single(diagnostics, d => d.ElementKind == "panelRepeat");
        Assert.Contains("no multi-value variable", reported.Reason);
    }

    [Fact]
    public void RepeatOverASingleValueVariable_IsReportedAndNotEmitted()
    {
        var (sections, diagnostics) = Convert(
            new JArray(Panel(1, "Per host", repeat: "host")),
            Variable("host", multi: false));

        Assert.Null(RepetitiveVar(Assert.Single(sections)));
        Assert.Single(diagnostics, d => d.ElementKind == "panelRepeat");
    }

    [Fact]
    public void SeveralRepeatingPanels_EachGetTheirOwnSection()
    {
        var (sections, _) = Convert(
            new JArray(
                Panel(1, "Per host", repeat: "host"),
                Panel(2, "Per cluster", repeat: "cluster")),
            Variable("host", multi: true), Variable("cluster", multi: true));

        Assert.Equal(2, sections.Count);
        Assert.Equal("host", RepetitiveVar(sections[0])?.Value<string>("name"));
        Assert.Equal("cluster", RepetitiveVar(sections[1])?.Value<string>("name"));
    }

    [Fact]
    public void DashboardWithNoRepeats_IsUnchanged()
    {
        var (sections, diagnostics) = Convert(new JArray(Panel(1, "Plain"), Panel(2, "Also plain")));

        Assert.Single(sections);
        Assert.Null(RepetitiveVar(sections[0]));
        Assert.DoesNotContain(diagnostics, d => d.ElementKind == "panelRepeat");
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void MultiValueDetection_RequiresAnExplicitMultiOrIncludeAll(bool multi, bool includeAll)
    {
        var variable = new JObject
        {
            ["name"] = "host",
            ["type"] = "query",
            ["multi"] = multi,
            ["includeAll"] = includeAll
        };

        Assert.Equal(multi || includeAll, VariableConverter.WillBeMultiValue(variable));
    }

    [Theory]
    [InlineData("adhoc")]
    [InlineData("datasource")]
    public void VariableTypesThatAreNeverEmitted_AreNotRepeatable(string type)
    {
        var variable = new JObject { ["name"] = "Filters", ["type"] = type, ["multi"] = true };

        Assert.False(VariableConverter.WillBeMultiValue(variable));
    }

    // ── Row repeat ────────────────────────────────────────────────────────────
    //
    // Grafana repeats a row as well as a panel, and a row is the closer analogue of a Coralogix
    // section. A row panel is consumed as a separator, so its repeat has to be captured during
    // grouping — nothing downstream ever sees the row object again.

    [Fact]
    public void RepeatingRow_BecomesARepeatingSection()
    {
        var (sections, _) = Convert(
            new JArray(Row(10, "Per environment", repeat: "environment"), Panel(1, "Latency")),
            Variable("environment", multi: true));

        var section = Assert.Single(sections);
        Assert.Equal("environment", RepetitiveVar(section)?.Value<string>("name"));
        Assert.Equal("Per environment", section["options"]?["custom"]?.Value<string>("name"));
    }

    [Fact]
    public void RepeatingRow_CarriesEveryPanelInTheRow()
    {
        var (sections, _) = Convert(
            new JArray(Row(10, "Per environment", repeat: "environment"),
                Panel(1, "Latency"), Panel(2, "Errors")),
            Variable("environment", multi: true));

        var section = Assert.Single(sections);
        var widgets = (section["rows"] as JArray ?? [])
            .Children<JObject>()
            .SelectMany(r => (r["widgets"] as JArray ?? []).Children<JObject>())
            .ToList();
        Assert.Equal(2, widgets.Count);
        Assert.Equal("environment", RepetitiveVar(section)?.Value<string>("name"));
    }

    /// <remarks>
    /// The shape in test_data/import_validation/dataacquisitions_grafana_from_prompt.json: a real
    /// row repeat over a single-value custom variable. Grafana draws that row exactly once, so
    /// emitting no repetition is correct — but it has to be reported, which is what was missing.
    /// </remarks>
    [Fact]
    public void RowRepeatOverSingleValueVariable_IsNotEmittedAndIsReported()
    {
        var (sections, diagnostics) = Convert(
            new JArray(Row(10, "Per environment", repeat: "environment"), Panel(1, "Latency")),
            Variable("environment", multi: false));

        var section = Assert.Single(sections);
        Assert.Null(RepetitiveVar(section));

        var loss = Assert.Single(diagnostics.Where(d => d.ElementKind == "rowRepeat"));
        Assert.Equal("${environment}", loss.ElementName);
        Assert.Equal("UNS-RPT-001", loss.Code);
    }

    [Fact]
    public void RowRepeatOverUnknownVariable_IsReported()
    {
        var (sections, diagnostics) = Convert(
            new JArray(Row(10, "Per environment", repeat: "nosuchvar"), Panel(1, "Latency")));

        Assert.Null(RepetitiveVar(Assert.Single(sections)));
        Assert.Single(diagnostics.Where(d => d.ElementKind == "rowRepeat"));
    }

    [Fact]
    public void CollapsedRepeatingRow_StillBecomesARepeatingSection()
    {
        // A collapsed row nests its children, and the row itself is still discarded after they are
        // absorbed — so the repeat must be read on that same pass or it is lost with the row.
        var (sections, _) = Convert(
            new JArray(Row(10, "Per environment", "environment", Panel(1, "Latency"), Panel(2, "Errors"))),
            Variable("environment", multi: true));

        var section = Assert.Single(sections);
        Assert.Equal("environment", RepetitiveVar(section)?.Value<string>("name"));
    }

    [Fact]
    public void NonRepeatingRow_GetsNoRepetition()
    {
        var (sections, diagnostics) = Convert(
            new JArray(Row(10, "Plain row"), Panel(1, "Latency")),
            Variable("environment", multi: true));

        Assert.Null(RepetitiveVar(Assert.Single(sections)));
        Assert.Empty(diagnostics.Where(d => d.ElementKind == "rowRepeat"));
    }

    [Fact]
    public void PanelRepeatInsideARepeatingRow_KeepsThePanelsOwnVariable()
    {
        // Two nested repeats, one repetitiveVar per section: the panel's is the more specific.
        var (sections, diagnostics) = Convert(
            new JArray(Row(10, "Per environment", repeat: "environment"),
                Panel(1, "Plain"), Panel(2, "Per host", repeat: "host")),
            Variable("environment", multi: true),
            Variable("host", multi: true));

        Assert.Equal(2, sections.Count);
        Assert.Equal("environment", RepetitiveVar(sections[0])?.Value<string>("name"));
        Assert.Equal("host", RepetitiveVar(sections[1])?.Value<string>("name"));

        var collision = Assert.Single(diagnostics.Where(d => d.ElementKind == "rowRepeat"));
        Assert.Contains("Per host", collision.Reason);
        Assert.Contains("${host}", collision.Reason);
    }
}
