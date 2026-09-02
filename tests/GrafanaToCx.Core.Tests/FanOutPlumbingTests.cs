using GrafanaToCx.Cli.Cli;
using GrafanaToCx.Core.ApiClient;
using GrafanaToCx.Core.Converter;
using GrafanaToCx.Core.Migration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;

namespace GrafanaToCx.Core.Tests;

/// <summary>
/// A Coralogix gauge holds one query, so a multi-query stat panel either fans out into one widget
/// per query or loses every query after the first. The setting for it existed but only the
/// <c>migrate</c> path passed it to the converter: <c>import</c> had no field for it on
/// DashboardTransformContext, <c>push</c> built options without it, and <c>convert</c> passed no
/// options at all. These assert through each path's real entry point — constructing
/// ConversionOptions directly would prove nothing about the wiring.
/// </summary>
public class FanOutPlumbingTests
{
    private static JObject FiveQueryStatDashboard() => new()
    {
        ["title"] = "Board",
        ["panels"] = new JArray(new JObject
        {
            ["id"] = 1,
            ["type"] = "stat",
            ["title"] = "Cluster A",
            ["targets"] = new JArray(
                Target("A", "Total request"),
                Target("B", "Accepted"),
                Target("C", "Rejected"),
                Target("D", "Warning"),
                Target("E", "Other"))
        })
    };

    private static JObject Target(string refId, string alias) => new()
    {
        ["refId"] = refId,
        ["alias"] = alias,
        ["expr"] = $"sum(requests_total{{kind=\"{alias}\"}})"
    };

    private static int WidgetCount(JObject dashboard) =>
        (dashboard["layout"]?["sections"] as JArray ?? [])
        .Children<JObject>()
        .SelectMany(s => (s["rows"] as JArray ?? []).Children<JObject>())
        .SelectMany(r => (r["widgets"] as JArray ?? []).Children<JObject>())
        .Count();

    // ── convert ───────────────────────────────────────────────────────────────
    // Ran the converter with no options at all, so nothing the operator configured could apply.

    [Fact]
    public async Task Convert_FansOutByDefault()
    {
        var (input, output) = await WriteTempDashboardAsync();
        try
        {
            var handlers = new CommandHandlers(NullLoggerFactory.Instance);
            var exit = await handlers.RunConvertAsync(input, output);

            Assert.Equal(0, exit);
            Assert.Equal(5, WidgetCount(JObject.Parse(await File.ReadAllTextAsync(output))));
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    [Fact]
    public async Task Convert_WithFanOutOff_KeepsOneWidget()
    {
        var (input, output) = await WriteTempDashboardAsync();
        try
        {
            var handlers = new CommandHandlers(NullLoggerFactory.Instance);
            await handlers.RunConvertAsync(input, output, fanOutMultiQueryPanels: false);

            Assert.Equal(1, WidgetCount(JObject.Parse(await File.ReadAllTextAsync(output))));
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    [Theory]
    [InlineData(new[] { "convert", "in.json" }, false)]
    [InlineData(new[] { "convert", "in.json", "--no-fan-out" }, true)]
    public void ConvertParser_ReadsTheNoFanOutFlag(string[] args, bool expected)
    {
        var parsed = ArgumentParser.Parse(args);
        Assert.Equal(expected, parsed.GetBool("no-fan-out"));
    }

    // ── import ────────────────────────────────────────────────────────────────
    // CoralogixTransformer rebuilt ConversionOptions from the context, which had no field for the
    // flag — so migration.fanOutMultiQueryPanels read as configured and did nothing.

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ImportTransformer_ForwardsTheContextFlagToTheConverter(bool fanOut)
    {
        var converter = new OptionsRecordingConverter();
        var transformer = new CoralogixTransformer(converter, new DashboardValidator());

        transformer.Transform(
            FiveQueryStatDashboard().ToString(),
            new DashboardTransformContext("a.json", "folder-1", null, null, fanOut));

        Assert.Equal(fanOut, converter.LastOptions?.FanOutMultiQueryPanels);
    }

    [Fact]
    public void ImportTransformContext_DefaultsToFanningOut()
    {
        var context = new DashboardTransformContext("a.json", null, null);
        Assert.True(context.FanOutMultiQueryPanels);
    }

    // ── migrate ───────────────────────────────────────────────────────────────

    [Fact]
    public void MigrationSettings_DefaultToFanningOut_WhenTheKeyIsAbsent()
    {
        // The settings files in this repo omit the key, and a bare `init` bool would bind to false
        // and quietly restore the lossy behaviour — so the initialiser is load-bearing.
        var json = """
            { "migration": { "checkpointFile": "c.json", "reportFile": "r.txt" } }
            """;

        var settings = BindSettings(json);

        Assert.True(settings.Migration.FanOutMultiQueryPanels);
    }

    [Fact]
    public void MigrationSettings_HonourAnExplicitFalse()
    {
        var json = """
            { "migration": { "fanOutMultiQueryPanels": false } }
            """;

        Assert.False(BindSettings(json).Migration.FanOutMultiQueryPanels);
    }

    [Fact]
    public void ImportSettings_DefaultToFanningOut()
    {
        Assert.True(new ImportSettings().FanOutMultiQueryPanels);
    }

    private static MigrationSettings BindSettings(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"fanout-settings-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        try
        {
            return new ConfigurationBuilder()
                .AddJsonFile(path, optional: false)
                .Build()
                .Get<MigrationSettings>() ?? new MigrationSettings();
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task<(string input, string output)> WriteTempDashboardAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"fanout-convert-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var input = Path.Combine(dir, "board.json");
        await File.WriteAllTextAsync(input, FiveQueryStatDashboard().ToString());
        return (input, Path.Combine(dir, "board_cx.json"));
    }

    private static void Cleanup(string input, string output)
    {
        var dir = Path.GetDirectoryName(input);
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
        _ = output;
    }

    /// <summary>Records the options it was handed, so a test can assert the wiring rather than the result.</summary>
    private sealed class OptionsRecordingConverter : IGrafanaToCxConverter
    {
        public ConversionOptions? LastOptions { get; private set; }

        public string Convert(string grafanaJson, ConversionOptions? options = null) =>
            ConvertToJObject(grafanaJson, options).ToString();

        public JObject ConvertToJObject(string grafanaJson, ConversionOptions? options = null)
        {
            LastOptions = options;
            return new JObject
            {
                ["name"] = "Board",
                ["layout"] = new JObject { ["sections"] = new JArray() }
            };
        }

        public IReadOnlyList<PanelConversionDiagnostic> ConversionDiagnostics => [];
        public IReadOnlyList<DashboardConversionDiagnostic> DashboardDiagnostics => [];
        public IReadOnlyList<JObject> ConversionDecisionEvents => [];
    }
}
